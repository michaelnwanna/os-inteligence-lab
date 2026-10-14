"""Benchmark harness: scores detection, cohort, correlation and agent quality against scenario ground truth."""

from __future__ import annotations

import platform
import statistics
import time
from dataclasses import dataclass, field

from ail_detection import DetectionConfig
from ail_ingestion.store import TelemetryStore
from ail_ingestion.stream import Pipeline
from ail_investigation import run_analysis
from ail_schemas import TelemetryEvent, bucket_of
from ail_simulator import (
    Scenario,
    Simulator,
    iter_events,
    list_heldout_scenarios,
    list_scenarios,
    simulate_to_store,
)

START_TOLERANCE = (-5, 40)  # minutes around the true effect start in which an onset counts as a match


@dataclass
class RunResult:
    scenario: str
    seed: int
    expected: int
    incidents: int
    tp: int = 0
    fp: int = 0
    fn: int = 0
    latencies: list[float] = field(default_factory=list)
    cohort_ok: list[bool] = field(default_factory=list)
    trigger_rank: list[int | None] = field(default_factory=list)
    ai_valid: list[bool] = field(default_factory=list)
    culprit_top1: list[bool] = field(default_factory=list)


def _run(sc: Scenario, seed: int, devices: int | None) -> RunResult:
    store = TelemetryStore(":memory:")
    simulate_to_store(sc, store, devices=devices, seed=seed)
    incidents = run_analysis(store, DetectionConfig(), save=False)
    episodes = sc.expected.all_episodes()
    res = RunResult(sc.id, seed, len(episodes), len(incidents))
    unmatched = list(incidents)
    for ep in episodes:
        start = bucket_of(sc.start) + ep.effect_start_minute
        in_window = [i for i in unmatched
                     if start + START_TOLERANCE[0] <= bucket_of(i.onset) <= start + START_TOLERANCE[1]]
        exact = [i for i in in_window if (i.cohorts[0].dimensions if i.cohorts else None) == ep.cohort]
        candidates = exact or in_window  # prefer the incident whose cohort is the injected one
        if not candidates:
            res.fn += 1
            continue
        inc = min(candidates, key=lambda i: abs(bucket_of(i.onset) - start))
        unmatched.remove(inc)
        res.tp += 1
        res.latencies.append(float(bucket_of(inc.first_detection) - start))
        got = inc.cohorts[0].dimensions if inc.cohorts else None
        res.cohort_ok.append((got == ep.cohort) if ep.cohort else got is None)
        if ep.trigger:
            ids = [c.change.deployment_id for c in inc.correlations]
            res.trigger_rank.append(ids.index(ep.trigger) + 1 if ep.trigger in ids else None)
        if inc.investigation:
            res.ai_valid.append(bool(inc.investigation.guardrail.get("provider_output_valid")))
            if ep.culprit_commit:
                cands = inc.investigation.candidate_commits
                res.culprit_top1.append(bool(cands and cands[0].sha == ep.culprit_commit))
    res.fp = len(unmatched)
    return res


def _ingestion_benchmark(sc: Scenario) -> dict[str, float]:
    sim = Simulator(sc, devices=3000)
    store = TelemetryStore(":memory:")
    pipeline = Pipeline(store)
    events = [TelemetryEvent.model_validate(e) for e in iter_events(sim, minutes=20)]
    start = time.perf_counter()
    for i in range(0, len(events), 2000):
        pipeline.process(events[i: i + 2000])
    elapsed = time.perf_counter() - start
    return {"events": len(events), "seconds": elapsed, "events_per_second": len(events) / elapsed}


def _rate(num: float, den: float) -> str:
    return f"{num / den * 100:.0f}% ({int(num)}/{int(den)})" if den else "n/a"


def evaluate(devices: int | None = 50_000, seeds: int = 3, progress=print, heldout: bool = False) -> str:
    scenarios = list_heldout_scenarios() if heldout else list_scenarios()
    runs: list[RunResult] = []
    for sc in scenarios:
        for k in range(seeds):
            progress(f"  {sc.id} seed {sc.seed + k}")
            runs.append(_run(sc, sc.seed + k, devices))
    tp, fp, fn = sum(r.tp for r in runs), sum(r.fp for r in runs), sum(r.fn for r in runs)
    precision = tp / (tp + fp) if tp + fp else 1.0
    recall = tp / (tp + fn) if tp + fn else 1.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    clean = [r for r in runs if r.expected == 0]
    lat = [x for r in runs for x in r.latencies]
    cohort = [x for r in runs for x in r.cohort_ok]
    ranks = [x for r in runs for x in r.trigger_rank]
    top1 = sum(1 for x in ranks if x == 1)
    top3 = sum(1 for x in ranks if x is not None and x <= 3)
    ai = [x for r in runs for x in r.ai_valid]
    culprit = [x for r in runs for x in r.culprit_top1]
    ingest = None if heldout else _ingestion_benchmark(next(s for s in scenarios if s.id == "scenario-003"))
    episodes = sum(r.expected for r in runs)

    title = "# Held-out benchmark results" if heldout else "# Benchmark results"
    fleet = f"{devices:,} simulated devices each" if devices else "each scenario's own fleet size"
    kind = (" Held-out scenarios were written after the detector settings were fixed and were never used for tuning."
            if heldout else "")
    out = [
        title,
        "",
        f"Generated by `ail evaluate{' --heldout' if heldout else ''}` ({len(scenarios)} scenarios x {seeds} seeds, "
        f"{fleet}, {episodes} injected regressions in total). Every run is reproducible from the scenario file "
        f"and seed.{kind}",
        "",
        "## Detection",
        "| Metric | Result |", "|---|---|",
        f"| Incident precision | {precision:.2f} |",
        f"| Incident recall | {recall:.2f} |",
        f"| F1 | {f1:.2f} |",
        f"| False-positive rate (clean runs with any incident) | {_rate(sum(1 for r in clean if r.incidents), len(clean))} |",
        f"| Detection latency from effect start (median / max, minutes) | {statistics.median(lat):.0f} / {max(lat):.0f} |"
        if lat else "| Detection latency | n/a |",
        "",
        "## Cohort intelligence",
        "| Metric | Result |", "|---|---|",
        f"| Affected-cohort identification accuracy (exact dimensions) | {_rate(sum(cohort), len(cohort))} |",
        "",
        "## Correlation",
        "| Metric | Result |", "|---|---|",
        f"| True change ranked top-1 | {_rate(top1, len(ranks))} |",
        f"| True change ranked top-3 | {_rate(top3, len(ranks))} |",
        "",
        "## AI investigation (default offline `rules` provider)",
        "| Metric | Result |", "|---|---|",
        f"| Reports passing grounding validation (cites evidence, no invented numbers or hashes) | {_rate(sum(ai), len(ai))} |",
        f"| Deliberately injected commit ranked first | {_rate(sum(culprit), len(culprit))} |",
        "",
        *([
            "## Platform (single process, SQLite, this machine)",
            "| Metric | Result |", "|---|---|",
            f"| Ingestion pipeline throughput | {ingest['events_per_second']:,.0f} events/s "
            f"({ingest['events']:,} events in {ingest['seconds']:.1f}s) |",
            f"| Python / platform | {platform.python_version()} / {platform.platform()} |",
            "",
        ] if ingest else []),
        "## Per-run detail",
        "| Scenario | Seed | Expected | Incidents | Matched | Latency (min) | Cohort correct | Trigger rank |",
        "|---|---|---|---|---|---|---|---|",
    ]
    for r in runs:
        out.append(f"| {r.scenario} | {r.seed} | {r.expected} | {r.incidents} | {r.tp} | "
                   f"{', '.join(str(int(x)) for x in r.latencies)} | {', '.join(str(x) for x in r.cohort_ok)} | "
                   f"{', '.join('-' if x is None else str(x) for x in r.trigger_rank)} |")
    if not heldout:
        out += ["", "Targets from the product spec (1,000 events/s locally, 100K simulated devices, 10M stored events) "
                    "are engineering targets; only the numbers above are measured."]
    return "\n".join(out) + "\n"
