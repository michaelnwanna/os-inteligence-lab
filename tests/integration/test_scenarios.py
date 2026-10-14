"""Acceptance tests: each scenario has a known ground truth that the platform must recover."""

from __future__ import annotations

import pytest
from ail_schemas import bucket_of
from conftest import scenario_ids


@pytest.mark.parametrize("scenario_id", scenario_ids())
def test_scenario_outcome_matches_ground_truth(scenario_run, scenario_id):
    run = scenario_run(scenario_id)
    episodes = run.scenario.expected.all_episodes()
    assert len(run.incidents) == len(episodes), [i.title for i in run.incidents]

    remaining = list(run.incidents)
    for ep in episodes:
        start = bucket_of(run.scenario.start) + ep.effect_start_minute
        inc = min(remaining, key=lambda i: abs(bucket_of(i.onset) - start))
        remaining.remove(inc)

        # detection: onset estimate near the true start, detected soon after
        assert abs(bucket_of(inc.onset) - start) <= 10, inc.title
        assert 0 <= bucket_of(inc.first_detection) - start <= 15, inc.title
        assert inc.metric == ep.metric

        # cohort intelligence: the cohort is found without being told its dimensions
        found = inc.cohorts[0].dimensions if inc.cohorts else None
        assert found == ep.cohort

        # correlation: the true change ranks first
        assert inc.correlations[0].change.deployment_id == ep.trigger
        assert not inc.correlations[0].after_onset


def test_flagship_matches_the_spec_narrative(flagship):
    (inc,) = flagship.incidents
    assert inc.severity == "critical"
    top = inc.cohorts[0]
    assert top.dimensions == {"architecture": "arm64", "application_version": "2.1"}
    assert top.ratio > 15 and 0.02 < top.population_share < 0.06  # a small slice of the fleet
    assert 0.002 < top.baseline < 0.008 and 0.04 < top.observed < 0.12
    assert inc.global_observed / inc.global_baseline < 3  # the global metric looks only mildly off
    assert inc.signatures[0]["signature"] == "OOM_KILL|MemoryCache.Allocate"
    assert inc.signatures[0]["excess_share"] > 0.9
    memory = next(s for s in inc.supporting_metrics if s["metric"] == "memory_mb")
    assert memory["significant"] and memory["ratio"] > 2


def test_global_regression_reports_no_spurious_cohort(scenario_run):
    (inc,) = scenario_run("scenario-002").incidents
    assert inc.cohorts == []
    assert inc.title.startswith("Global")


def test_later_unrelated_change_is_not_a_trigger(flagship):
    (inc,) = flagship.incidents
    late = next(c for c in inc.correlations if c.change.deployment_id == "os-13.0-update")
    assert late.after_onset and late.temporal_score == 0.0 and late.score < 0.1
    early = next(c for c in inc.correlations if c.change.deployment_id == "drv-7.14")
    assert early.score < inc.correlations[0].score - 0.4


def test_hotfix_resolves_the_first_incident_and_a_later_one_is_still_found(scenario_run):
    run = scenario_run("scenario-009")
    first, second = run.incidents
    hotfix = bucket_of(run.scenario.start) + 185
    fix = bucket_of(first.resolved_at)
    assert first.status == "resolved" and abs(fix - hotfix) <= 15
    assert first.observed > 0.04  # measured over the incident, not diluted by the recovered tail
    assert second.status != "resolved"
    assert second.cohorts[0].dimensions == {"architecture": "x64", "driver_version": "7.14"}
    assert bucket_of(second.onset) > fix + 60  # found on a re-baselined window after the recovery
    assert any(e.kind == "recovered" for e in first.timeline) if first.timeline else True


def test_simultaneous_regressions_become_separate_incidents_with_separate_triggers(scenario_run):
    incidents = scenario_run("scenario-010").incidents
    by_trigger = {i.correlations[0].change.deployment_id: i for i in incidents}
    assert set(by_trigger) == {"dep-app-2.1", "drv-7.14"}
    assert by_trigger["dep-app-2.1"].cohorts[0].dimensions == {"architecture": "arm64", "application_version": "2.1"}
    assert by_trigger["drv-7.14"].cohorts[0].dimensions == {"architecture": "x64", "driver_version": "7.14"}
    assert all(i.correlations[0].score > 0.9 for i in incidents)


def test_analysis_is_reproducible(flagship):
    from ail_detection import DetectionConfig, analyze

    again = analyze(flagship.store, DetectionConfig())
    first = flagship.incidents[0]
    assert again[0].reproducibility["data_fingerprint"] == first.reproducibility["data_fingerprint"]
    assert again[0].reproducibility["config_hash"] == first.reproducibility["config_hash"]
    assert [r.model_dump() for r in again[0].results] == [r.model_dump() for r in first.results]
    assert again[0].cohorts[0] == first.cohorts[0]
