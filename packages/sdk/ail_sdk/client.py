"""Batching, retrying ingestion client."""

from __future__ import annotations

import time
from collections.abc import Iterable
from typing import Any

import httpx


class IngestionClient:
    def __init__(
        self,
        base_url: str = "http://127.0.0.1:8000",
        token: str | None = None,
        batch_size: int = 500,
        max_retries: int = 5,
        client: httpx.Client | None = None,
    ) -> None:
        headers = {"Authorization": f"Bearer {token}"} if token else {}
        self._http = client or httpx.Client(base_url=base_url, headers=headers, timeout=30)
        self.batch_size = batch_size
        self.max_retries = max_retries

    def _post(self, path: str, body: list[dict[str, Any]]) -> dict[str, Any]:
        """POST with exponential backoff on 503 / transport errors. Safe to retry: the server de-duplicates."""
        delay = 0.2
        for attempt in range(self.max_retries + 1):
            try:
                resp = self._http.post(path, json=body)
                if resp.status_code in (429, 503) and attempt < self.max_retries:
                    retry_after = resp.headers.get("Retry-After", "")
                    time.sleep(min(float(retry_after), 30.0) if retry_after.replace(".", "", 1).isdigit() else delay)
                    delay *= 2
                    continue
                resp.raise_for_status()
                return resp.json()
            except httpx.TransportError:
                if attempt == self.max_retries:
                    raise
                time.sleep(delay)
                delay *= 2
        raise RuntimeError("unreachable")

    def send(self, path: str, records: Iterable[dict[str, Any]]) -> dict[str, int]:
        accepted = rejected = 0
        batch: list[dict[str, Any]] = []

        def flush() -> None:
            nonlocal accepted, rejected, batch
            if batch:
                result = self._post(path, batch)
                accepted += result["accepted"]
                rejected += len(result["rejected"])
                batch = []

        for rec in records:
            batch.append(rec)
            if len(batch) >= self.batch_size:
                flush()
        flush()
        return {"accepted": accepted, "rejected": rejected}

    def send_events(self, events: Iterable[dict[str, Any]]) -> dict[str, int]:
        return self.send("/api/v1/events", events)

    def send_metrics(self, metrics: Iterable[dict[str, Any]]) -> dict[str, int]:
        return self.send("/api/v1/metrics", metrics)

    def send_deployments(self, deployments: Iterable[dict[str, Any]]) -> dict[str, int]:
        return self.send("/api/v1/deployments", deployments)
