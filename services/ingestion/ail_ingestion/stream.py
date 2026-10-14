"""Streaming abstraction. The in-memory stream can be replaced by Redis Streams or Kafka."""

from __future__ import annotations

import threading
import time
from collections import deque
from typing import Protocol

from ail_schemas import TelemetryEvent
from ail_schemas.observability import REGISTRY, get_logger

from ail_ingestion.processing import aggregate_rows, normalize
from ail_ingestion.store import TelemetryStore

log = get_logger("ail.ingestion")


class Backpressure(Exception):
    """Raised when the stream is full; clients should retry with backoff."""


class EventStream(Protocol):
    def publish(self, events: list[TelemetryEvent]) -> None: ...
    def poll(self, max_items: int) -> list[TelemetryEvent]: ...
    def depth(self) -> int: ...


class InMemoryStream:
    def __init__(self, maxsize: int = 200_000) -> None:
        self._q: deque[TelemetryEvent] = deque()
        self._lock = threading.Lock()
        self.maxsize = maxsize

    def publish(self, events: list[TelemetryEvent]) -> None:
        with self._lock:
            if len(self._q) + len(events) > self.maxsize:
                raise Backpressure("event stream is full")
            self._q.extend(events)

    def poll(self, max_items: int) -> list[TelemetryEvent]:
        with self._lock:
            n = min(max_items, len(self._q))
            return [self._q.popleft() for _ in range(n)]

    def depth(self) -> int:
        with self._lock:
            return len(self._q)


class Pipeline:
    """normalize -> de-duplicate -> store raw -> enrich -> aggregate."""

    def __init__(self, store: TelemetryStore) -> None:
        self.store = store

    def process(self, batch: list[TelemetryEvent]) -> int:
        start = time.perf_counter()
        fresh = []
        for ev in batch:
            n = normalize(ev)
            if self.store.insert_raw(n.event_id, n.ts, n.payload):
                fresh.append(n)
        self.store.add_aggregates(aggregate_rows(fresh))
        self.store.commit()
        REGISTRY.inc("ail_events_stored_total", len(fresh))
        REGISTRY.inc("ail_events_duplicate_total", len(batch) - len(fresh))
        REGISTRY.observe("ail_processing_latency_seconds", time.perf_counter() - start)
        return len(fresh)


class StreamProcessor:
    def __init__(self, stream: EventStream, pipeline: Pipeline, batch_size: int = 2000) -> None:
        self.stream = stream
        self.pipeline = pipeline
        self.batch_size = batch_size
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def drain(self) -> int:
        """Process everything currently queued (synchronously)."""
        total = 0
        while True:
            batch = self.stream.poll(self.batch_size)
            if not batch:
                return total
            try:
                total += self.pipeline.process(batch)
            except Exception:  # keep the worker alive; the batch is logged and counted
                REGISTRY.inc("ail_processing_errors_total", len(batch))
                log.exception("batch processing failed")

    def start(self) -> None:
        if self._thread:
            return

        def loop() -> None:
            while not self._stop.is_set():
                if not self.drain():
                    self._stop.wait(0.05)

        self._thread = threading.Thread(target=loop, name="ail-processor", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=5)
        self.drain()
