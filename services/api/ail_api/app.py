"""HTTP API: ingestion, incidents, cohort analytics and the portal."""

from __future__ import annotations

import hmac
import os
import threading
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

from ail_detection import DetectionConfig, Workspace
from ail_detection.explore import breakdown, health, series_payload
from ail_detection.labels import cohort_label
from ail_detection.nlq import answer, parse, vocab_of
from ail_ingestion.app import IngestionContext, add_health_routes, build_ingestion_router
from ail_ingestion.store import TelemetryStore
from ail_investigation import create_provider, run_analysis
from ail_investigation.history import IncidentArchive
from ail_investigation.report import render_markdown
from ail_schemas import DIMENSIONS, Incident, iso
from ail_simulator import repo_root
from fastapi import FastAPI, Header, HTTPException, Query, Request
from fastapi.responses import JSONResponse, PlainTextResponse
from fastapi.staticfiles import StaticFiles


def _parse_filters(raw: str | None) -> dict[str, str]:
    out: dict[str, str] = {}
    for part in filter(None, (raw or "").split(",")):
        key, _, value = part.partition("=")
        if key not in DIMENSIONS or not value:
            raise HTTPException(400, f"invalid filter '{part}'; keys must be one of {list(DIMENSIONS)}")
        out[key] = value
    return out


def _summary(inc: Incident) -> dict[str, Any]:
    dims = inc.cohorts[0].dimensions if inc.cohorts else {}
    trigger = inc.correlations[0] if inc.correlations and not inc.correlations[0].after_onset else None
    return {
        "id": inc.id, "title": inc.title, "status": inc.status, "severity": inc.severity, "metric": inc.metric,
        "onset": inc.onset, "first_detection": inc.first_detection, "baseline": inc.baseline,
        "observed": inc.observed, "cohort": cohort_label(dims), "cohort_dimensions": dims,
        "trigger": (f"{trigger.change.component} {trigger.change.to_version or ''}".strip() if trigger else None),
        "correlation": trigger.score if trigger else None,
    }


class AppState:
    def __init__(self, store: TelemetryStore) -> None:
        self.store = store
        self._ws: Workspace | None = None
        self._key: tuple | None = None
        self._lock = threading.Lock()

    def ws(self) -> Workspace:
        key = (self.store.counts()["aggregate_rows"], self.store.time_range())
        with self._lock:
            if self._ws is None or key != self._key:
                try:
                    self._ws = Workspace(self.store, DetectionConfig())
                except ValueError as exc:
                    raise HTTPException(409, "no telemetry has been ingested yet") from exc
                self._key = key
            return self._ws


def create_app(db_path: str | Path = "data/ail.db", inline: bool = False,
               archive_path: str | Path | None = None, read_token: str | None = None) -> FastAPI:
    read_token = read_token or os.environ.get("AIL_READ_TOKEN") or None
    store = TelemetryStore(db_path)
    archive = IncidentArchive(archive_path or os.environ.get("AIL_ARCHIVE", "data/archive.db"))
    ctx = IngestionContext(store=store, inline=inline)
    state = AppState(store)

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        if not ctx.inline:
            ctx.processor.start()
        yield
        ctx.processor.stop()

    app = FastAPI(title="OS Intelligence Lab", version="0.1.0", lifespan=lifespan)
    app.state.ctx, app.state.store = ctx, store
    app.include_router(build_ingestion_router(ctx))
    add_health_routes(app, ctx)

    @app.middleware("http")
    async def read_guard(request: Request, call_next):
        guarded = request.url.path.startswith("/api/v1/") or request.url.path == "/metrics"
        if read_token and request.method == "GET" and guarded:
            supplied = request.headers.get("authorization", "").removeprefix("Bearer ").strip()
            if not hmac.compare_digest(supplied.encode(), read_token.encode()):
                return JSONResponse({"detail": "missing or invalid read token"}, status_code=401)
        return await call_next(request)

    @app.get("/api/v1/meta")
    def meta() -> dict[str, Any]:
        rng = store.time_range()
        return {"simulation": store.get_meta("simulation"), "counts": store.counts(),
                "time_range": [iso(rng[0]), iso(rng[1])] if rng else None, "llm_provider": create_provider().name}

    @app.get("/api/v1/overview")
    def overview() -> dict[str, Any]:
        incidents = store.list_incidents()
        active = [i for i in incidents if i.status != "resolved"]
        out: dict[str, Any] = {
            "meta": meta(),
            "incidents": {"total": len(incidents), "active": len(active),
                          "critical": sum(1 for i in active if i.severity == "critical"),
                          "investigating": sum(1 for i in incidents if i.status == "investigating"),
                          "resolved": sum(1 for i in incidents if i.status == "resolved")},
            "recent": [_summary(i) for i in incidents[:5]],
        }
        if store.time_range():
            ws = state.ws()
            out["health"] = health(ws)
            ev = ws.scanner("failure_rate").evaluate(ws.cube("failure_rate").mask(None), ws.T - 10, ws.T)
            out["failure_rate"] = {"baseline": ev["baseline"], "current": ev["observed"], "ratio": ev["ratio"]}
        return out

    @app.get("/api/v1/incidents")
    def incidents() -> list[dict[str, Any]]:
        return [_summary(i) for i in store.list_incidents()]

    def _get(incident_id: str) -> Incident:
        inc = store.get_incident(incident_id)
        if inc is None:
            raise HTTPException(404, "incident not found")
        return inc

    @app.get("/api/v1/incidents/{incident_id}")
    def incident(incident_id: str) -> dict[str, Any]:
        data = _get(incident_id).model_dump(mode="json")
        data.pop("timemachine", None)
        data["cohort_label"] = _summary(_get(incident_id))["cohort"]
        return data

    @app.get("/api/v1/incidents/{incident_id}/timemachine")
    def timemachine(incident_id: str) -> dict[str, Any]:
        tm = _get(incident_id).timemachine
        if tm is None:
            raise HTTPException(404, "no time machine data for this incident")
        return tm

    @app.get("/api/v1/incidents/{incident_id}/report", response_class=PlainTextResponse)
    def report(incident_id: str) -> str:
        return render_markdown(_get(incident_id))

    @app.get("/api/v1/anomalies")
    def anomalies(
        metric: str | None = None, severity: str | None = None, detector: str | None = None,
        os_version: str | None = None, architecture: str | None = None, device_model: str | None = None,
        application_version: str | None = None, driver_version: str | None = None,
    ) -> list[dict[str, Any]]:
        wanted = {k: v for k, v in dict(os_version=os_version, architecture=architecture, device_model=device_model,
                                        application_version=application_version, driver_version=driver_version).items() if v}
        rows = []
        for inc in store.list_incidents():
            cohort = inc.cohorts[0].dimensions if inc.cohorts else {}
            for r in inc.results:
                dims = {**cohort, **r.dimensions}
                if metric and r.metric != metric or severity and r.severity != severity or detector and r.detector != detector:
                    continue
                if any(dims.get(k) != v for k, v in wanted.items()):
                    continue
                rows.append({"incident_id": inc.id, "scope": cohort_label(dims), "dimensions": dims,
                             **r.model_dump(mode="json", exclude={"evidence"}), "evidence": r.evidence})
        return sorted(rows, key=lambda r: r["timestamp"])

    @app.get("/api/v1/series")
    def series(metric: str = "failure_rate", filters: str | None = None) -> dict[str, Any]:
        ws = state.ws()
        if metric not in ws.metrics:
            raise HTTPException(404, f"unknown metric '{metric}'")
        return series_payload(ws, metric, _parse_filters(filters))

    @app.get("/api/v1/cohorts")
    def cohorts(metric: str = "failure_rate", filters: str | None = None,
                window: int | None = Query(default=None, ge=1, le=1440)) -> dict[str, Any]:
        ws = state.ws()
        if metric not in ws.metrics:
            raise HTTPException(404, f"unknown metric '{metric}'")
        return breakdown(ws, metric, _parse_filters(filters), window)

    @app.get("/api/v1/query")
    def query(q: str = Query(min_length=3, max_length=300)) -> dict[str, Any]:
        ws = state.ws()
        return answer(ws, parse(q, vocab_of(ws)), store.list_incidents())

    @app.get("/api/v1/changes")
    def changes() -> list[dict[str, Any]]:
        return [c.model_dump(mode="json") for c in store.list_changes()]

    @app.post("/api/v1/analyze")
    def analyze(authorization: str | None = Header(default=None)) -> dict[str, Any]:
        if ctx.token is not None:
            supplied = (authorization or "").removeprefix("Bearer ").strip()
            if not hmac.compare_digest(supplied.encode(), ctx.token.encode()):
                raise HTTPException(401, "missing or invalid API token")
        ctx.processor.drain()
        found = run_analysis(store, DetectionConfig(), create_provider(), archive=archive)
        return {"incidents": [i.id for i in found]}

    portal = repo_root() / "apps" / "portal"
    if portal.exists():
        app.mount("/", StaticFiles(directory=portal, html=True), name="portal")
    return app
