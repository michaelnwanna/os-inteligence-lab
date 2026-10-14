"""End-to-end detection: detectors -> cohort scan -> incident grouping -> correlation."""

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
ENGINE_VERSION = "0.1.0"
_METRIC_PRIORITY = ["failure_rate", "memory_mb", "latency_ms"]


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
    if not ws.signature_metrics:
        return []
    f_cube, f_scan = ws.cube("failure_rate"), ws.scanner("failure_rate")
    f_mask = f_cube.mask(dims)
    nw, kw, _, _ = f_cube.window(onset, last + 1)
    expected_failures = float((nw * f_scan.level)[f_mask].sum())
    total_failures = float(kw[f_mask].sum())
    base_counts, win_counts = {}, {}
    for m in ws.signature_metrics:
        cube = ws.cube(m)
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


def _build_incident(ws: Workspace, number: int, group: list[_Cluster], changes: list[ChangeEvent]) -> Incident:
    cfg = ws.cfg
    present = {c.metric: c for c in group}
    primary = next((present[m] for m in _METRIC_PRIORITY if m in present), group[0])
    metric = primary.metric
    cube, scanner = ws.cube(metric), ws.scanner(metric)
    last = ws.T - 1
    detected = min(c.detected for c in group)
    onset = min(detected, min(c.onset for c in group))
    results = sorted((r for c in group for r in c.results), key=lambda r: r.timestamp)
    confirm_candidates = [r.evidence["alarm_minute_index"] + cfg.confirm_minutes
                          for r in results if r.evidence.get("confirmed") is True]
    confirmed = min(confirm_candidates) if confirm_candidates else None

    findings = scanner.scan(onset, last + 1)
    cohorts = [f for f, _ in findings]
    mask = findings[0][1] if findings else None
    dims = cohorts[0].dimensions if cohorts else {}
    scope_mask = mask if mask is not None else np.ones(cube.C, dtype=bool)

    series = cube.series()
    base = fit_baseline(series, ws.baseline_end)
    g_obs = series.value(onset, last + 1)
    ev = scanner.evaluate(scope_mask, onset, last + 1)
    baseline, observed = (cohorts[0].baseline, cohorts[0].observed) if cohorts else (base.level, g_obs)

    supporting = []
    for m2 in ws.metrics:
        if m2 == metric or m2 not in cfg.metrics:
            continue
        mask2 = ws.cube(m2).mask(dims) if dims else None
        ev2 = ws.scanner(m2).evaluate(mask2 if mask2 is not None else np.ones(ws.cube(m2).C, bool), onset, last + 1)
        supporting.append({"metric": m2, "scope": cohort_label(dims), "baseline": ev2["baseline"],
                           "observed": ev2["observed"], "ratio": ev2["ratio"], "significant": ev2["llr"] > 12.0})

    sev_global, _ = severity_for(g_obs / base.level if base.level else 1.0, cube.kind)
    severity = max_severity(sev_global, *(r.severity for r in primary.results),
                            *( [cohorts[0].severity] if cohorts else []))
    recent = scanner.evaluate(scope_mask, last - 9, last + 1)
    status = "resolved" if recent["llr"] < cfg.recovery_z**2 / 2 else "open"
    scope_txt = cohort_label(dims) if dims else "all devices"
    title = (f"{metric_label(metric)} regression in {scope_txt}" if dims
             else f"Global {metric_label(metric).lower()} regression")
    correlations = correlate(cube, scanner, mask, onset, last, changes, cfg.correlation)

    return Incident(
        id=f"AIL-{1000 + number}", title=title, status=status, severity=severity, metric=metric,
        onset=dt_of(ws.t0 + onset), first_detection=dt_of(ws.t0 + detected),
        confirmed=dt_of(ws.t0 + confirmed) if confirmed is not None else None,
        baseline=baseline, observed=observed, global_baseline=base.level, global_observed=g_obs,
        supporting_metrics=supporting, results=results, cohorts=cohorts,
        signatures=_signature_summary(ws, dims, onset, last), correlations=correlations,
        reproducibility={
            "engine_version": ENGINE_VERSION, "config_hash": cfg.fingerprint(),
            "config": cfg.model_dump(), "data_fingerprint": ws.store.data_fingerprint(),
            "scan_window": [dt_of(ws.t0 + onset).isoformat(), dt_of(ws.t0 + last).isoformat()],
            "recent_llr": round(recent["llr"], 3), "scope_llr": round(ev["llr"], 3),
        },
    )


def _is_material(inc: Incident, cfg: DetectionConfig) -> bool:
    """Statistical significance is not enough: require a practically meaningful effect."""
    kind = "rate" if inc.metric == "failure_rate" else "gauge"
    min_cohort = cfg.cohort.min_ratio_rate if kind == "rate" else cfg.cohort.min_ratio_gauge
    cohort_ok = bool(inc.cohorts) and inc.cohorts[0].ratio >= min_cohort
    global_ok = inc.global_baseline > 0 and inc.global_observed / inc.global_baseline >= 1 + cfg.min_relative_change
    return cohort_ok or global_ok


def analyze(store, cfg: DetectionConfig | None = None, detectors: list[Detector] | None = None,
            ws: Workspace | None = None) -> list[Incident]:
    """Run detection over everything in the store and return incidents (not yet investigated)."""
    ws = ws or Workspace(store, cfg)
    cfg = ws.cfg
    detectors = detectors or DEFAULT_DETECTORS
    metrics = [m for m in cfg.metrics if m in ws.metrics]
    if "failure_rate" not in metrics or ws.T <= ws.baseline_end + cfg.window_minutes:
        return []
    with REGISTRY.timer("ail_detection_run_seconds"):
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
        groups = _group(results, cfg)
        built = [_build_incident(ws, 0, g, changes) for g in groups]
        incidents = [i for i in built if _is_material(i, cfg)]
        for n, inc in enumerate(incidents, start=1):
            inc.id = f"AIL-{1000 + n}"
    return incidents
