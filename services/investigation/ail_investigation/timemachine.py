"""Failure Time Machine: the replayable story of an incident, from deployment to investigation."""

from __future__ import annotations

from datetime import timedelta

import numpy as np
from ail_detection.labels import cohort_label, metric_label
from ail_detection.workspace import Workspace
from ail_schemas import ChangeEvent, Incident, TimelineEvent, bucket_of, dt_of, hhmm


def _lead_change(inc: Incident) -> tuple[ChangeEvent, float] | None:
    for c in inc.correlations:
        if not c.after_onset and c.score >= 0.3:
            return c.change, c.score
    return None


def build_timemachine(ws: Workspace, inc: Incident) -> tuple[list[TimelineEvent], dict]:
    cfg = ws.cfg
    cube = ws.cube(inc.metric)
    scanner = ws.scanner(inc.metric)
    dims = inc.cohorts[0].dimensions if inc.cohorts else {}
    mask = cube.mask(dims)
    start = max(0, ws.baseline_end - 10)
    sl = slice(start, cube.T)

    def per_minute(num: np.ndarray, den: np.ndarray) -> list[float]:
        return np.where(den > 0, num / np.maximum(den, 1e-12), 0.0).tolist()

    n_all = cube.N.sum(1)
    obs_col = cube.K if cube.kind == "rate" else cube.S
    frames: dict = {
        "global": per_minute(obs_col.sum(1)[sl], n_all[sl]),
        "cohort": per_minute(obs_col[:, mask].sum(1)[sl], cube.N[:, mask].sum(1)[sl]),
        "expected": per_minute((cube.N[:, mask] * scanner.level[mask]).sum(1)[sl], cube.N[:, mask].sum(1)[sl]),
        "cohort_sessions": cube.N[:, mask].sum(1)[sl].tolist(),
    }
    lead = _lead_change(inc)
    exposure = None
    if lead and lead[0].dimension and lead[0].to_version:
        touched = cube.mask({lead[0].dimension: lead[0].to_version})
        exposure = per_minute(cube.N[:, touched].sum(1), n_all)
        frames["exposure"] = exposure[start:]
    secondary = next((s for s in inc.supporting_metrics if s["significant"]), None)
    if secondary:
        c2 = ws.cube(secondary["metric"])
        m2 = c2.mask(dims)
        frames["secondary"] = {"metric": secondary["metric"], "label": metric_label(secondary["metric"]),
                               "values": per_minute(c2.S[:, m2].sum(1)[sl], c2.N[:, m2].sum(1)[sl])}

    stage = timedelta(minutes=cfg.pipeline_stage_minutes)
    first = min(inc.results, key=lambda r: r.timestamp) if inc.results else None
    events: list[TimelineEvent] = []

    def add(ts, kind, title, detail="", simulated=False, **evidence):
        events.append(TimelineEvent(ts=ts, kind=kind, title=title, detail=detail,
                                    simulated_latency=simulated, evidence=evidence))

    if lead:
        ch, score = lead
        add(ch.timestamp, "deployment_started", f"{ch.component} {ch.to_version or ''} {ch.kind.replace('_', ' ')} started".replace("  ", " "),
            ch.description, deployment_id=ch.deployment_id, correlation_score=score)
        if exposure is not None:
            hit = next((i for i, v in enumerate(exposure) if i >= bucket_of(ch.timestamp) - ws.t0 and v >= 0.05), None)
            if hit is not None:
                add(dt_of(ws.t0 + hit), "exposure", "5% of sessions exposed to the change",
                    f"{exposure[hit] * 100:.1f}% of sessions now run {ch.to_version}", exposure=exposure[hit])
    add(inc.onset, "first_abnormal", f"First abnormal {metric_label(inc.metric).lower()} (estimated onset)",
        f"Change-point estimate for {cohort_label(dims)}", scope=cohort_label(dims))
    add(inc.first_detection, "threshold_exceeded", "Statistical threshold exceeded",
        f"{first.detector} on {metric_label(first.metric).lower()}" if first else "", detectors=[r.detector for r in inc.results])
    if inc.confirmed:
        add(inc.confirmed, "anomaly_confirmed", "Anomaly confirmed", "Alarm persisted for the confirmation interval")
    cohort_hits = [r for r in inc.results if r.detector == "CohortScanDetector"]
    isolated = min((r.timestamp for r in cohort_hits), default=None)
    if inc.cohorts and isolated:
        c = inc.cohorts[0]
        add(isolated, "cohort_isolated", f"{cohort_label(c.dimensions)} cohort isolated",
            f"{metric_label(inc.metric)} {c.ratio:.1f}x baseline in {c.population_share * 100:.1f}% of sessions",
            dimensions=c.dimensions, ratio=c.ratio)

    anchor = max([t for t in (inc.confirmed, isolated, inc.first_detection) if t])
    steps = 1
    if lead:
        add(anchor + stage * steps, "correlation_discovered", f"Correlation discovered: {lead[0].component} {lead[0].to_version or ''}".strip(),
            "Ranked by temporal proximity and population overlap (correlation, not causation)", True,
            score=lead[1], deployment_id=lead[0].deployment_id)
        steps += 1
    inv = inc.investigation
    if inv and inv.candidate_commits:
        top = inv.candidate_commits[0]
        add(anchor + stage * steps, "commits_analyzed", "GitHub commits analyzed",
            f"{len(inv.candidate_commits)} candidates ranked; top: {top.sha[:7]} ({top.component})", True,
            sha=top.sha, score=top.score)
        steps += 1
    if inv:
        add(anchor + stage * steps, "ai_completed", "AI investigation completed",
            inv.hypotheses[0].title if inv.hypotheses else "", True, provider=inv.provider)
    if inc.resolved_at:
        add(inc.resolved_at, "recovered", f"{metric_label(inc.metric)} recovered",
            f"No significant excess for {cfg.recovery_minutes} minutes in {cohort_label(dims)}")
    events.sort(key=lambda e: e.ts)

    last_ts = dt_of(ws.t0 + cube.T - 1)
    tm = {
        "metric": inc.metric, "label": metric_label(inc.metric), "scope": cohort_label(dims),
        "start": dt_of(ws.t0 + start).isoformat(),
        "minutes": [hhmm(ws.t0 + start + i) for i in range(cube.T - start)],
        "frames": frames,
        "events": [{"index": max(0, min(int((e.ts - dt_of(ws.t0 + start)).total_seconds() // 60), cube.T - start - 1)),
                    "after_data": e.ts > last_ts, **e.model_dump(mode="json")} for e in events],
    }
    return events, tm
