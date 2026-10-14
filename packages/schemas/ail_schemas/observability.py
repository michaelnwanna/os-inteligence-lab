"""Self-observability: a small dependency-free metrics registry with Prometheus text output."""

from __future__ import annotations

import json
import logging
import sys
import threading
import time
from collections import deque
from collections.abc import Callable, Iterator
from contextlib import contextmanager


class Registry:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._counters: dict[str, float] = {}
        self._summaries: dict[str, list[float]] = {}  # count, sum, max
        self._gauge_fns: dict[str, Callable[[], float]] = {}
        self._marks: dict[str, deque[tuple[float, float]]] = {}

    def inc(self, name: str, value: float = 1.0) -> None:
        with self._lock:
            self._counters[name] = self._counters.get(name, 0.0) + value
            marks = self._marks.setdefault(name, deque())
            now = time.monotonic()
            marks.append((now, value))
            while marks and now - marks[0][0] > 60:
                marks.popleft()

    def observe(self, name: str, value: float) -> None:
        with self._lock:
            s = self._summaries.setdefault(name, [0.0, 0.0, 0.0])
            s[0] += 1
            s[1] += value
            s[2] = max(s[2], value)

    def gauge(self, name: str, fn: Callable[[], float]) -> None:
        self._gauge_fns[name] = fn

    def rate(self, name: str, window: float = 10.0) -> float:
        """Average per-second rate of a counter over the trailing window."""
        now = time.monotonic()
        with self._lock:
            total = sum(v for t, v in self._marks.get(name, ()) if now - t <= window)
        return total / window

    @contextmanager
    def timer(self, name: str) -> Iterator[None]:
        start = time.perf_counter()
        try:
            yield
        finally:
            self.observe(name, time.perf_counter() - start)

    def snapshot(self) -> dict[str, float]:
        with self._lock:
            out = dict(self._counters)
            for name, (count, total, peak) in self._summaries.items():
                out[f"{name}_count"] = count
                out[f"{name}_sum"] = total
                out[f"{name}_max"] = peak
        for name, fn in self._gauge_fns.items():
            out[name] = float(fn())
        return out

    def render(self) -> str:
        lines = [f"{k} {v}" for k, v in sorted(self.snapshot().items())]
        lines.append(f"ail_ingest_events_per_second {self.rate('ail_ingest_events_total')}")
        return "\n".join(lines) + "\n"


REGISTRY = Registry()


class _JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload = {
            "ts": self.formatTime(record, "%Y-%m-%dT%H:%M:%S%z"),
            "level": record.levelname,
            "logger": record.name,
            "msg": record.getMessage(),
        }
        extra = getattr(record, "fields", None)
        if extra:
            payload.update(extra)
        if record.exc_info:
            payload["exc"] = self.formatException(record.exc_info)
        return json.dumps(payload, default=str)


def get_logger(name: str) -> logging.Logger:
    """Structured JSON logger writing to stderr."""
    logger = logging.getLogger(name)
    if not logger.handlers:
        handler = logging.StreamHandler(sys.stderr)
        handler.setFormatter(_JsonFormatter())
        logger.addHandler(handler)
        logger.setLevel(logging.INFO)
        logger.propagate = False
    return logger
