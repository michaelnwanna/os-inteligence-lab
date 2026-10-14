"""Event-level expansion of the simulator, used to exercise the ingestion API end to end."""

from __future__ import annotations

from collections.abc import Iterator
from datetime import datetime, timezone
from typing import Any

import numpy as np

from ail_simulator.engine import Simulator

_CPU = {"x64": "x64-8c", "arm64": "arm64-8c", "x86": "x86-4c"}


def iter_events(sim: Simulator, minutes: int | None = None) -> Iterator[dict[str, Any]]:
    """Yield JSON-ready TelemetryEvent dicts: one session event plus latency/memory samples per session."""
    rng = sim.rng
    pop = sim.sc.population
    for t in sim.ticks():
        if minutes is not None and t.minute >= minutes:
            return
        order = np.argsort(t.cell_of_device, kind="stable")
        counts = np.bincount(t.cell_of_device, minlength=sim.ncell)
        starts = np.concatenate([[0], np.cumsum(counts)])
        for c in np.nonzero(t.sessions)[0].tolist():
            n_sessions, n_failed = int(t.sessions[c]), int(t.failures[c])
            members = order[starts[c]: starts[c + 1]]
            picks = members[rng.integers(0, len(members), n_sessions)]
            sigs: list[str | None] = []
            for name, arr in t.sig_counts.items():
                sigs.extend([name] * int(arr[c]))
            sigs = sigs[:n_failed] + [None] * (n_sessions - len(sigs[:n_failed]))
            os_v, arch, model, app, app_v, drv = sim.cell_dims(c)
            lat = np.maximum(rng.normal(t.latency[0][c], t.latency[1][c], n_sessions), 1.0)
            mem = np.maximum(rng.normal(t.memory[0][c], t.memory[1][c], n_sessions), 1.0)
            for i, dev in enumerate(picks.tolist()):
                ts = datetime.fromtimestamp(t.bucket * 60 + int(rng.integers(0, 60)), tz=timezone.utc)
                base = {
                    "timestamp": ts.isoformat(),
                    "device_id": f"dev-{dev:07d}",
                    "session_id": f"s-{t.bucket}-{c}-{i}",
                    "os": pop.os, "os_version": os_v, "architecture": arch, "device_model": model,
                    "cpu": _CPU.get(arch), "memory_gb": 8.0 if len(model) % 2 else 16.0,
                    "application": app, "application_version": app_v,
                    "driver": "gfx", "driver_version": drv,
                }
                yield {**base, "event_type": "session", "failure_signature": sigs[i]}
                yield {**base, "event_type": "metric", "metric_name": "latency_ms",
                       "metric_value": float(lat[i])}
                yield {**base, "event_type": "metric", "metric_name": "memory_mb",
                       "metric_value": float(mem[i])}
