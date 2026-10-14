"""Lazy, cached analytic views over a telemetry store."""

from __future__ import annotations

import threading

from ail_detection.cohort import CohortScanner
from ail_detection.config import DetectionConfig
from ail_detection.cube import Cube


class Workspace:
    """Builds cubes and cohort scanners on demand. Create a new Workspace when the data changes."""

    def __init__(self, store, cfg: DetectionConfig | None = None, start: int | None = None):
        self.store = store
        self.cfg = cfg or DetectionConfig()
        self._cubes: dict[str, Cube] = {}
        self._scanners: dict[str, CohortScanner] = {}
        self._lock = threading.RLock()
        rng = store.time_range()
        if rng is None:
            raise ValueError("the store contains no telemetry")
        self.t0, self.t1 = rng
        if start is not None:
            self.t0 = max(self.t0, start)
        self.metrics = [m for m in store.metrics() if not m.startswith("sig:")]
        self.signature_metrics = [m for m in store.metrics() if m.startswith("sig:")]

    @property
    def T(self) -> int:
        return self.t1 - self.t0 + 1

    @property
    def baseline_end(self) -> int:
        return self.cfg.baseline_minutes

    def cube(self, metric: str) -> Cube:
        with self._lock:
            if metric not in self._cubes:
                self._cubes[metric] = Cube.from_rows(metric, self.store.agg_rows(metric), self.t0, self.t1)
            return self._cubes[metric]

    def scanner(self, metric: str) -> CohortScanner:
        with self._lock:
            if metric not in self._scanners:
                self._scanners[metric] = CohortScanner(self.cube(metric), self.baseline_end, self.cfg.cohort)
            return self._scanners[metric]
