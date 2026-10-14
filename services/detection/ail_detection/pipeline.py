"""End-to-end detection: detectors -> cohort scan -> incident grouping -> correlation.

Detection runs in passes. When every incident found in a pass has recovered, the engine
re-baselines on the data after the recovery and searches again, so later episodes are found too.
"""

from __future__ import annotations

import math
import statistics
from dataclasses import dataclass

import numpy as np
from ail_schemas import AnomalyResult, ChangeEvent, CohortFinding, Incident, dt_of
from ail_schemas.observability import REGISTRY, get_logger

from ail_detection.config import DetectionConfig
from ail_detection.correlation import correlate
from ail_detection.detectors import DEFAULT_DETECTORS, DetectionContext, Detector
from ail_detection.labels import cohort_label, metric_label
from ail_detection.stats import confidence_from_strength, fit_baseline, max_severity, severity_for
from ail_detection.workspace import Workspace

log = get_logger("ail.detection")
ENGINE_VERSION = "0.2.0"
_METRIC_PRIORITY = ["failure_rate", "memory_mb", "latency_ms"]
MAX_PASSES = 6


@dataclass
class _Cluster:
    metric: str
    results: list[AnomalyResult]
    onset: int
    detected: int


def _cohort_result(ws: Workspace, metric: str, alarm: int, finding: CohortFinding, mask: np.ndarray) -> AnomalyResult:
    cfg = ws.cfg
    scanner = ws.scanner(metric)
    end = min(alarm + cfg.confirm_minutes, ws.T - 1)
    onset = scanner.onset(mask, end)
    ev = scanner.evaluate(mask, onset, end + 1)
    ratio = ev["observed"] / ev["baseline"] if ev["baseline"] > 0 else finding.ratio
    severity, score = severity_for(ratio, ws.cube(metric).kind)
    strength = min(5.0, math.log10(1 / max(finding.p_value, 1e-300)) / math.log10(1 / cfg.cohort.alpha))
    return AnomalyResult(
        metric=metric, timestamp=dt_of(ws.t0 + alarm), onset=dt_of(ws.t0 + onset),
        baseline=ev["baseline"], observed_value=ev["observed"], delta=ev["observed"] - ev["baseline"],
        severity=severity, severity_score=round(score, 4), confidence=confidence_from_strength(strength),
        affected_population=finding.population_share, detector="CohortScanDetector",
        dimensions=finding.dimensions,
        evidence={"alarm_minute_index": alarm, "onset_minute_index": onset, "llr": round(finding.llr, 2),
                  "p_value": finding.p_value, "ratio": round(ratio, 3), "strength": round(strength, 3),
                  "window_minutes": cfg.cohort.window_minutes, "confirmed": True},
    )


def _dims_compatible(a: dict | None, b: dict | None) -> bool:
    if not a or not b:
        return True
    small, big = (a, b) if len(a) <= len(b) else (b, a)
    return all(big.get(k) == v for k, v in small.items())


def _group(results: list[AnomalyResult], cfg: DetectionConfig) -> list[list[_Cluster]]:
    by_metric: dict[str, list[AnomalyResult]] = {}
    for r in results:
        by_metric.setdefault(r.metric, []).append(r)
    clusters: list[_Cluster] = []
    for metric, rs in by_metric.items():
        first = min(r.evidence["alarm_minute_index"] for r in rs)
        members = [r for r in rs if r.evidence["alarm_minute_index"] <= first + cfg.merge_minutes]
        cohort_onsets = [r.evidence["onset_minute_index"] for r in members if r.detector == "CohortScanDetector"]
        onset = (min(cohort_onsets) if cohort_onsets
                 else int(statistics.median_low(sorted(r.evidence["onset_minute_index"] for r in members))))
        clusters.append(_Cluster(metric, members, onset, first))
    clusters.sort(key=lambda c: (c.detected, c.metric))

    def cohort_dims(group: list[_Cluster]) -> dict | None:
        dims = [r.dimensions for c in group for r in c.results if r.detector == "CohortScanDetector"]
        return max(dims, key=len) if dims else None

    groups: list[list[_Cluster]] = []
    for c in clusters:
        mine = cohort_dims([c])
        for g in groups:
            if abs(g[0].onset - c.onset) <= cfg.incident_onset_minutes and _dims_compatible(cohort_dims(g), mine):
                g.append(c)
                break
        else:
            groups.append([c])
    return groups


def _signature_summary(ws: Workspace, dims: dict[str, str], onset: int, last: int) -> list[dict]:
    f_cube, f_scan = ws.cube("failure_rate"), ws.scanner("failure_rate")
    f_mask = f_cube.mask(dims)
    nw, kw, _, _ = f_cube.window(onset, last + 1)
    expected_failures = float((nw * f_scan.level)[f_mask].sum())
    total_failures = float(kw[f_mask].sum())
    base_counts, win_counts = {}, {}
    for m in ws.signature_metrics:
        try:
            cube = ws.cube(m)
        except ValueError:  # this signature never occurs inside the analysis window
            continue
        base_counts[m] = float(cube.window(0, ws.baseline_end)[0].sum())
        win_counts[m] = float(cube.window(onset, last + 1)[0][cube.mask(dims)].sum())
    base_total = max(sum(base_counts.values()), 1.0)
    rows = []
    for m, cnt in win_counts.items():
        share_b = base_counts[m] / base_total
        excess = max(cnt - expected_failures * share_b, 0.0)
        rows.append({"signature": m.removeprefix("sig:"), "count": cnt, "baseline_share": share_b,
                     "share_of_failures": cnt / total_failures if total_failures else 0.0, "excess": excess})
    total_excess = sum(r["excess"] for r in rows) or 1.0
    for r in rows:
        r["excess_share"] = r["excess"] / total_excess
    rows = [r for r in rows if r["count"] > 0]
    rows.sort(key=lambda r: r["excess"], reverse=True)
    return rows[:5]


def _recovery_index(scanner, mask: np.ndarray, start: int, last: int, cfg: DetectionConfig) -> int | None:
    """First minute of a `recovery_minutes` window with no significant excess for the scope."""
    w = cfg.recovery_minutes
    for end in range(start + w - 1, last + 1):
        if scanner.evaluate(mask, end - w + 1, end + 1)["llr"] < cfg.recovery_z**2 / 2:
            return end - w + 1
    return None


def _build_incident(ws: Workspace, group: list[_Cluster], changes: list[ChangeEvent], pick: int = 0,
                    seeded: list[tuple[CohortFinding, np.ndarray]] | None = None,
                    ) -> tuple[Incident, list[tuple[CohortFinding, np.ndarray]]] | None:
    cfg = ws.cfg
    present = {c.metric: c for c in group}
    primary = next((present[m] for m in _METRIC_PRIORITY if m in present), group[0])
    metric = primary.metric
    cube, scanner = ws.cube(metric), ws.scanner(metric)
    last = ws.T - 1
    detected = min(c.detected for c in group)
    onset = min(detected, min(c.onset for c in group))
    all_results = sorted((r for c in group for r in c.results), key=lambda r: r.timestamp)

    # The cohort that raised the alarm defines "recovered"; scanning the whole remaining history
    # would dilute it with later, unrelated episodes.
    if pick == 0:
        alarm_dims = next((r.dimensions for r in primary.results if r.detector == "CohortScanDetector"), None)
        seed_mask = cube.mask(alarm_dims) if alarm_dims else np.ones(cube.C, dtype=bool)
    else:
        _, seed_mask = seeded[pick]
        onset = min(detected, scanner.onset(seed_mask, last))

    recovered = _recovery_index(scanner, seed_mask, detected, last, cfg)
    end = recovered - 1 if recovered is not None else last
    findings = scanner.scan(onset, end + 1)
    if pick == 0:
        idx = 0
    else:
        overlap = [float((m & seed_mask).sum() / max((m | seed_mask).sum(), 1)) for _, m in findings]
        idx = int(np.argmax(overlap)) if overlap and max(overlap) > 0 else None
        if idx is None:
            return None
    mask = findings[idx][1] if findings else None
    others = np.zeros(cube.C, dtype=bool)
    for i, (_, m) in enumerate(findings):
        if i != idx:
            others |= m
    cohorts = [findings[idx][0]] + [f for i, (f, _) in enumerate(findings) if i != idx] if findings else []
    dims = cohorts[0].dimensions if cohorts else {}
    focus = cohorts[0] if cohorts else None
    if focus:  # the cohort's own change point is sharper than the fleet-wide one
        onset = min(detected, scanner.onset(mask, end))

    results = [r for r in all_results if r.detector != "CohortScanDetector" or r.dimensions == dims]
    confirm_candidates = [r.evidence["alarm_minute_index"] + cfg.confirm_minutes
                          for r in results if r.evidence.get("confirmed") is True]
    confirmed = min(confirm_candidates) if confirm_candidates else None

    series = cube.series()
    base = fit_baseline(series, ws.baseline_end)
    g_obs = series.value(onset, end + 1)
    baseline, observed = (focus.baseline, focus.observed) if focus else (base.level, g_obs)

    supporting = []
    for m2 in ws.metrics:
        if m2 == metric or m2 not in cfg.metrics:
            continue
        mask2 = ws.cube(m2).mask(dims) if dims else None
        ev2 = ws.scanner(m2).evaluate(mask2 if mask2 is not None else np.ones(ws.cube(m2).C, bool), onset, end + 1)
        supporting.append({"metric": m2, "scope": cohort_label(dims), "baseline": ev2["baseline"],
                           "observed": ev2["observed"], "ratio": ev2["ratio"], "significant": ev2["llr"] > 12.0})

    sev_global, _ = severity_for(g_obs / base.level if base.level else 1.0, cube.kind)
    primary_results = [r for r in primary.results if r.detector != "CohortScanDetector" or r.dimensions == dims]
    severity = max_severity(sev_global, *(r.severity for r in primary_results), *([focus.severity] if focus else []))
    scope_txt = cohort_label(dims) if dims else "all devices"
    title = (f"{metric_label(metric)} regression in {scope_txt}" if dims
             else f"Global {metric_label(metric).lower()} regression")
    correlations = correlate(cube, scanner, mask if focus else None, onset, end, changes, cfg.correlation,
                             ignore=others if others.any() else None)

    incident = Incident(
        id="AIL-0", title=title, status="resolved" if recovered is not None else "open", severity=severity,
        metric=metric, onset=dt_of(ws.t0 + onset), first_detection=dt_of(ws.t0 + detected),
        confirmed=dt_of(ws.t0 + confirmed) if confirmed is not None else None,
        resolved_at=dt_of(ws.t0 + recovered) if recovered is not None else None,
        baseline=baseline, observed=observed, global_baseline=base.level, global_observed=g_obs,
        supporting_metrics=supporting, results=results, cohorts=cohorts,
        signatures=_signature_summary(ws, dims, onset, end), correlations=correlations,
        reproducibility={
            "engine_version": ENGINE_VERSION, "config_hash": cfg.fingerprint(),
            "config": cfg.model_dump(), "data_fingerprint": ws.store.data_fingerprint(),
            "analysis_start": dt_of(ws.t0).isoformat(),
            "scan_window": [dt_of(ws.t0 + onset).isoformat(), dt_of(ws.t0 + end).isoformat()],
        },
    )
    return incident, findings


def _is_material(inc: Incident, cfg: DetectionConfig) -> bool:
    """Statistical significance is not enough: require a practically meaningful effect."""
    kind = "rate" if inc.metric == "failure_rate" else "gauge"
    min_cohort = cfg.cohort.min_ratio_rate if kind == "rate" else cfg.cohort.min_ratio_gauge
    cohort_ok = bool(inc.cohorts) and inc.cohorts[0].ratio >= min_cohort
    global_ok = inc.global_baseline > 0 and inc.global_observed / inc.global_baseline >= 1 + cfg.min_relative_change
    return cohort_ok or global_ok


def _analyze_window(store, ws: Workspace, detectors: list[Detector]) -> list[Incident]:
    cfg = ws.cfg
    metrics = [m for m in cfg.metrics if m in ws.metrics]
    if "failure_rate" not in metrics or ws.T <= ws.baseline_end + cfg.window_minutes:
        return []
    series = {m: ws.cube(m).series() for m in metrics}
    bases = {m: fit_baseline(series[m], ws.baseline_end) for m in metrics}
    ctx = DetectionContext(series, bases, cfg)
    results: list[AnomalyResult] = []
    for det in detectors:
        try:
            with REGISTRY.timer("ail_detector_latency_seconds"):
                results.extend(det.detect(ctx))
        except Exception:
            REGISTRY.inc("ail_detector_errors_total")
            log.exception("detector failed", extra={"fields": {"detector": det.name}})
    for m in metrics:
        hit = ws.scanner(m).first_alarm()
        if hit:
            results.append(_cohort_result(ws, m, *hit))
    changes = store.list_changes()
    built: list[Incident] = []
    for group in _group(results, cfg):
        built_first = _build_incident(ws, group, changes)
        if built_first is None:
            continue
        first, findings = built_first
        built.append(first)
        accepted = [first.cohorts[0].dimensions] if first.cohorts else []
        for k, (c, _) in enumerate(findings):
            if c.dimensions in accepted:
                continue
            # a second, unrelated cohort with a large share of the excess is its own incident
            if c.excess_share >= 0.2 and all(not _dims_compatible(a, c.dimensions) for a in accepted):
                sibling = _build_incident(ws, group, changes, pick=k, seeded=findings)
                if sibling is not None:
                    built.append(sibling[0])
                    accepted.append(c.dimensions)
    return [i for i in built if _is_material(i, cfg)]


def analyze(store, cfg: DetectionConfig | None = None, detectors: list[Detector] | None = None,
            ws: Workspace | None = None) -> list[Incident]:
    """Run detection over everything in the store and return incidents (not yet investigated)."""
    ws = ws or Workspace(store, cfg)
    cfg = ws.cfg
    detectors = detectors or DEFAULT_DETECTORS
    incidents: list[Incident] = []
    with REGISTRY.timer("ail_detection_run_seconds"):
        for _ in range(MAX_PASSES):
            found = _analyze_window(store, ws, detectors)
            incidents.extend(found)
            if not found or not all(i.resolved_at for i in found):
                break
            nxt = max(int(i.resolved_at.timestamp() // 60) for i in found) + cfg.rebaseline_gap_minutes
            if ws.t1 - nxt + 1 <= cfg.baseline_minutes + cfg.window_minutes + 10:
                break
            ws = Workspace(store, cfg, start=nxt)
    for n, inc in enumerate(incidents, start=1):
        inc.id = f"AIL-{1000 + n}"
    return incidents
