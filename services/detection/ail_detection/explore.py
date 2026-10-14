"""Read-only analytics used by the portal: series, cohort breakdowns and population health."""

from __future__ import annotations

import numpy as np
from ail_schemas import DIMENSIONS, hhmm, iso

from ail_detection.workspace import Workspace


def series_payload(ws: Workspace, metric: str, filters: dict[str, str] | None = None) -> dict:
    cube = ws.cube(metric)
    scanner = ws.scanner(metric)
    mask = cube.mask(filters)
    n = cube.N[:, mask].sum(1)
    obs = (cube.K if cube.kind == "rate" else cube.S)[:, mask].sum(1)
    exp = (cube.N[:, mask] * scanner.level[mask]).sum(1)
    safe = np.maximum(n, 1.0)
    return {
        "metric": metric,
        "t0": iso(cube.t0),
        "minutes": [hhmm(cube.t0 + i) for i in range(cube.T)],
        "sessions": n.tolist(),
        "observed": np.where(n > 0, obs / safe, 0.0).tolist(),
        "expected": np.where(n > 0, exp / safe, 0.0).tolist(),
        "baseline_end": ws.baseline_end,
    }


def breakdown(ws: Workspace, metric: str, filters: dict[str, str] | None = None,
              window_minutes: int | None = None) -> dict:
    """For each dimension not yet fixed by `filters`, the cohort statistics of every value."""
    cube, scanner = ws.cube(metric), ws.scanner(metric)
    i1 = cube.T
    i0 = max(ws.baseline_end, i1 - (window_minutes or ws.cfg.cohort.window_minutes * 3))
    base_mask = cube.mask(filters)
    nw, kw, sw, _ = cube.window(i0, i1)
    total_n = float(nw[base_mask].sum()) or 1.0
    out = []
    for dim in DIMENSIONS:
        if filters and dim in filters or len(cube.vocab[dim]) < 2:
            continue
        j = DIMENSIONS.index(dim)
        rows = []
        for code, value in enumerate(cube.vocab[dim]):
            m = base_mask & (cube.codes[:, j] == code)
            n = float(nw[m].sum())
            if n <= 0:
                continue
            ev = scanner.evaluate(m, i0, i1)
            rows.append({"value": value, "sessions": n, "share": n / total_n, "observed": ev["observed"],
                         "baseline": ev["baseline"], "ratio": ev["ratio"], "llr": ev["llr"]})
        rows.sort(key=lambda r: r["llr"], reverse=True)
        out.append({"dimension": dim, "values": rows})
    sel = scanner.evaluate(base_mask, i0, i1)
    return {"metric": metric, "filters": filters or {}, "window": [iso(cube.t0 + i0), iso(cube.t0 + i1 - 1)],
            "selection": sel, "dimensions": out}


def health(ws: Workspace, window_minutes: int = 10) -> dict:
    """Monitored series (metric x dimension value) and how many currently deviate from baseline."""
    monitored = flagged = 0
    for metric in ws.metrics:
        if metric not in ws.cfg.metrics:
            continue
        cube, scanner = ws.cube(metric), ws.scanner(metric)
        i1 = cube.T
        i0 = max(ws.baseline_end, i1 - window_minutes)
        min_ratio = ws.cfg.cohort.min_ratio_rate if cube.kind == "rate" else ws.cfg.cohort.min_ratio_gauge
        monitored += 1
        ev = scanner.evaluate(np.ones(cube.C, bool), i0, i1)
        flagged += int(ev["llr"] > 12 and ev["ratio"] >= min_ratio)
        for j, dim in enumerate(DIMENSIONS):
            if len(cube.vocab[dim]) < 2:
                continue
            for code in range(len(cube.vocab[dim])):
                monitored += 1
                ev = scanner.evaluate(cube.codes[:, j] == code, i0, i1)
                flagged += int(ev["llr"] > 12 and ev["ratio"] >= min_ratio)
    return {"monitored_series": monitored, "deviating_series": flagged,
            "healthy_series": monitored - flagged}
