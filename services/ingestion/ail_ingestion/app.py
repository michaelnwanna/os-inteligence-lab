"""FastAPI ingestion endpoints: POST /api/v1/events, /metrics, /deployments."""

from __future__ import annotations

import hmac
import json
import os
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from typing import Any

from ail_schemas import ChangeEvent, TelemetryEvent
from ail_schemas.observability import REGISTRY
from fastapi import APIRouter, FastAPI, Header, HTTPException, Request
from fastapi.responses import JSONResponse, PlainTextResponse
from pydantic import ValidationError

from ail_ingestion.ratelimit import RateLimiter
from ail_ingestion.store import TelemetryStore
from ail_ingestion.stream import Backpressure, InMemoryStream, Pipeline, StreamProcessor

MAX_BODY_BYTES = 10 * 1024 * 1024


@dataclass
class IngestionContext:
    store: TelemetryStore
    stream: InMemoryStream = field(default_factory=InMemoryStream)
    token: str | None = field(default_factory=lambda: os.environ.get("AIL_API_TOKEN") or None)
    inline: bool = False  # process synchronously (tests); otherwise a worker thread drains the stream
    limiter: RateLimiter | None = field(
        default_factory=lambda: RateLimiter(float(os.environ["AIL_RATE_LIMIT"])) if os.environ.get("AIL_RATE_LIMIT") else None)

    def __post_init__(self) -> None:
        self.processor = StreamProcessor(self.stream, Pipeline(self.store))
        REGISTRY.gauge("ail_queue_depth", self.stream.depth)


def _items(payload: Any, key: str) -> list[Any]:
    if isinstance(payload, list):
        return payload
    if isinstance(payload, dict):
        if isinstance(payload.get(key), list):
            return payload[key]
        return [payload]
    raise HTTPException(400, "body must be a JSON object or array")


async def _read_json(request: Request) -> Any:
    declared = request.headers.get("content-length")
    if declared and declared.isdigit() and int(declared) > MAX_BODY_BYTES:
        raise HTTPException(413, "request body too large")
    body = await request.body()
    if len(body) > MAX_BODY_BYTES:
        raise HTTPException(413, "request body too large")
    try:
        return json.loads(body)
    except ValueError as exc:
        raise HTTPException(400, "invalid JSON") from exc


def build_ingestion_router(ctx: IngestionContext) -> APIRouter:
    router = APIRouter(prefix="/api/v1", tags=["ingestion"])

    def throttle(request: Request) -> JSONResponse | None:
        if ctx.limiter is None:
            return None
        ok, wait = ctx.limiter.allow(request.client.host if request.client else "unknown")
        if ok:
            return None
        REGISTRY.inc("ail_ingest_throttled_total")
        return JSONResponse({"error": "rate limit exceeded"}, status_code=429,
                            headers={"Retry-After": str(max(1, int(wait + 0.999)))})

    def authorize(authorization: str | None) -> None:
        if ctx.token is None:
            return
        supplied = (authorization or "").removeprefix("Bearer ").strip()
        if not hmac.compare_digest(supplied.encode(), ctx.token.encode()):
            raise HTTPException(401, "missing or invalid API token")

    def accept(items: list[Any], force_type: str | None) -> JSONResponse:
        valid: list[TelemetryEvent] = []
        rejected: list[dict[str, Any]] = []
        for i, raw in enumerate(items):
            try:
                ev = TelemetryEvent.model_validate(raw)
                if force_type and ev.event_type != force_type:
                    raise ValueError(f"event_type must be '{force_type}' on this endpoint")
                valid.append(ev)
            except (ValidationError, ValueError) as exc:
                msg = exc.errors()[0]["msg"] if isinstance(exc, ValidationError) else str(exc)
                rejected.append({"index": i, "error": msg})
        try:
            ctx.stream.publish(valid)
        except Backpressure:
            return JSONResponse(
                {"error": "stream full, retry later"}, status_code=503, headers={"Retry-After": "1"}
            )
        if ctx.inline:
            ctx.processor.drain()
        REGISTRY.inc("ail_ingest_events_total", len(valid))
        REGISTRY.inc("ail_ingest_rejected_total", len(rejected))
        return JSONResponse({"accepted": len(valid), "rejected": rejected}, status_code=202)

    @router.post("/events")
    async def post_events(request: Request, authorization: str | None = Header(default=None)):
        authorize(authorization)
        if (limited := throttle(request)) is not None:
            return limited
        return accept(_items(await _read_json(request), "events"), None)

    @router.post("/metrics")
    async def post_metrics(request: Request, authorization: str | None = Header(default=None)):
        authorize(authorization)
        if (limited := throttle(request)) is not None:
            return limited
        return accept(_items(await _read_json(request), "metrics"), "metric")

    @router.post("/deployments")
    async def post_deployments(request: Request, authorization: str | None = Header(default=None)):
        authorize(authorization)
        if (limited := throttle(request)) is not None:
            return limited
        accepted, rejected = 0, []
        for i, raw in enumerate(_items(await _read_json(request), "deployments")):
            try:
                ctx.store.add_change(ChangeEvent.model_validate(raw))
                accepted += 1
            except ValidationError as exc:
                rejected.append({"index": i, "error": exc.errors()[0]["msg"]})
        return JSONResponse({"accepted": accepted, "rejected": rejected}, status_code=202)

    return router


def add_health_routes(app: FastAPI, ctx: IngestionContext) -> None:
    @app.get("/health")
    def health() -> dict[str, Any]:
        return {"status": "ok", "queue_depth": ctx.stream.depth()}

    @app.get("/metrics", response_class=PlainTextResponse)
    def metrics() -> str:
        return REGISTRY.render()


def create_ingestion_app(store: TelemetryStore | None = None, inline: bool = False) -> FastAPI:
    ctx = IngestionContext(store=store or TelemetryStore(), inline=inline)

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        if not ctx.inline:
            ctx.processor.start()
        yield
        ctx.processor.stop()

    app = FastAPI(title="OS Intelligence Lab - Ingestion", lifespan=lifespan)
    app.include_router(build_ingestion_router(ctx))
    add_health_routes(app, ctx)
    app.state.ctx = ctx
    return app
