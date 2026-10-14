"""Turn an incident into numbered, citable evidence. Every telemetry fact the agent may state lives here."""

from __future__ import annotations

from datetime import datetime

from ail_detection.labels import cohort_label, format_value, metric_label
from ail_schemas import CandidateCommit, EvidenceItem, Incident, bucket_of, hhmm


def _hm(dt: datetime) -> str:
    return hhmm(bucket_of(dt))


def build_evidence(
    inc: Incident,
    candidates: list[CandidateCommit],
    commit_total: int = 0,
    repo_name: str | None = None,
    ref_range: tuple[str, str] | None = None,
) -> list[EvidenceItem]:
    items: list[EvidenceItem] = []

    def add(prefix: str, kind: str, statement: str, data: dict) -> None:
        n = sum(1 for i in items if i.id.startswith(prefix)) + 1
        items.append(EvidenceItem(id=f"{prefix}{n}", kind=kind, statement=statement, data=data))

    top = inc.cohorts[0] if inc.cohorts else None
    dims = top.dimensions if top else {}
    scope = cohort_label(dims)
    label = metric_label(inc.metric)
    ratio = inc.observed / inc.baseline if inc.baseline else 0.0
    window_end = inc.reproducibility.get("scan_window", [None, None])[1]
    end_txt = _hm(datetime.fromisoformat(window_end)) if window_end else "end of data"

    add("E", "anomaly",
        f"{label} in {scope} changed from {format_value(inc.metric, inc.baseline)} to "
        f"{format_value(inc.metric, inc.observed)} ({ratio:.1f}x baseline) between {_hm(inc.onset)} and {end_txt} UTC. "
        f"Across all devices {label.lower()} moved from {format_value(inc.metric, inc.global_baseline)} to "
        f"{format_value(inc.metric, inc.global_observed)}.",
        {"metric": inc.metric, "scope": scope, "dimensions": dims, "baseline": inc.baseline,
         "observed": inc.observed, "ratio": ratio, "severity": inc.severity})

    primary = [r for r in inc.results if r.metric == inc.metric]
    detectors = sorted({r.detector for r in primary})
    first = min(inc.results, key=lambda r: r.timestamp) if inc.results else None
    conf_txt = f"; anomaly confirmed {_hm(inc.confirmed)}" if inc.confirmed else ""
    add("E", "timing",
        f"Estimated onset {_hm(inc.onset)} UTC; first alarm {_hm(inc.first_detection)} UTC"
        f"{f' ({first.detector} on {metric_label(first.metric).lower()})' if first else ''}{conf_txt}. "
        f"{len(detectors)} independent detectors fired on {label.lower()}.",
        {"onset": inc.onset.isoformat(), "detected": inc.first_detection.isoformat(), "detectors": detectors})

    alpha = inc.reproducibility.get("config", {}).get("cohort", {}).get("alpha", 0.001)
    if inc.cohorts:
        for c in inc.cohorts[:3]:
            add("E", "cohort",
                f"Cohort {cohort_label(c.dimensions)}: {label.lower()} {format_value(inc.metric, c.baseline)} to "
                f"{format_value(inc.metric, c.observed)} ({c.ratio:.1f}x), {c.population_share * 100:.1f}% of sessions, "
                f"{c.excess_share * 100:.0f}% of the excess (scan statistic, adjusted p < {alpha:g}).",
                {"dimensions": c.dimensions, "label": cohort_label(c.dimensions), "rank": c.rank,
                 "ratio": c.ratio, "population_share": c.population_share, "excess_share": c.excess_share})
    else:
        add("E", "scope", "No specific cohort stands out: the deviation is spread across the whole population.", {})

    for s in inc.supporting_metrics:
        if s["significant"]:
            add("E", "supporting_metric",
                f"{metric_label(s['metric'])} in {s['scope']}: {format_value(s['metric'], s['baseline'])} to "
                f"{format_value(s['metric'], s['observed'])} ({s['ratio']:.1f}x).",
                {"metric": s["metric"], "ratio": s["ratio"]})

    for sg in inc.signatures[:2]:
        if sg["excess_share"] >= 0.2:
            add("E", "signature",
                f"Failure signature {sg['signature']} accounts for {sg['excess_share'] * 100:.0f}% of the excess "
                f"failures in {scope} (baseline share {sg['baseline_share'] * 100:.0f}% of all failures).",
                {"signature": sg["signature"], "excess_share": sg["excess_share"]})

    for c in inc.correlations[:3]:
        ch = c.change
        base = f"{ch.component} {ch.from_version or ''}->{ch.to_version or ''} ({ch.kind}, id {ch.deployment_id})"
        data = {"deployment_id": ch.deployment_id, "component": ch.component, "kind": ch.kind,
                "from_version": ch.from_version, "to_version": ch.to_version, "score": c.score,
                "overlap": c.overlap, "after_onset": c.after_onset, "repository": ch.repository,
                "base_ref": ch.base_ref, "head_ref": ch.head_ref}
        if c.after_onset:
            add("E", "correlation",
                f"{base} began {_hm(ch.timestamp)} UTC, after the anomaly onset, so it cannot be the trigger "
                f"(correlation score {c.score:.2f}).", data)
        else:
            add("E", "correlation",
                f"{base} began {_hm(ch.timestamp)} UTC, {max(c.delta_minutes, 0):.0f} min before the onset; "
                f"{c.overlap * 100:.0f}% overlap with the affected population; correlation score {c.score:.2f}. "
                "Correlation is evidence of association, not proof of causation.", data)

    for s in inc.similar_incidents[:2]:
        add("H", "history",
            f"A similar earlier incident {s['id']} ({s['title']}, onset {s['onset'][:10]} {s['onset'][11:16]} UTC) "
            f"was recorded; similarity {s['score']:.2f} across "
            f"metric, cohort, failure signature and trigger type."
            f"{' This may be a recurrence.' if s['recurring'] else ''}",
            {"id": s["id"], "score": s["score"], "recurring": s["recurring"], "onset": s["onset"]})

    for c in candidates[:3]:
        where = f" in {repo_name} {ref_range[0]}..{ref_range[1]}" if repo_name and ref_range else ""
        add("C", "code",
            f"Commit {c.sha[:7]} ({c.component or 'unknown component'}) ranks {c.rank} of {commit_total} commits"
            f"{where} by heuristic code review (score {c.score:.2f}): {'; '.join(c.reasons) or 'no strong signals'}.",
            {"sha": c.sha, "sha7": c.sha[:7], "component": c.component, "score": c.score, "rank": c.rank,
             "files": c.files, "reasons": c.reasons, "message": c.message})
    return items
