from __future__ import annotations

from ail_detection import DetectionConfig, Workspace
from ail_detection.nlq import answer, parse, vocab_of
from ail_investigation import run_analysis
from ail_investigation.history import IncidentArchive, signature_tokens, similarity


def test_signature_tokens_split_type_and_component():
    assert signature_tokens("OOM_KILL|MemoryCache.Allocate") == {"oom", "kill", "memorycache", "allocate"}


def test_similar_scenarios_score_high_and_unrelated_ones_low(scenario_run):
    leak = scenario_run("scenario-003").incidents[0]
    slow_leak = scenario_run("scenario-008").incidents[0]
    latency = scenario_run("scenario-005").incidents[0]
    assert similarity(leak, slow_leak)[0] >= 0.8
    assert similarity(leak, latency)[0] < 0.4


def test_recurrence_is_recalled_cited_and_grounded(scenario_run):
    archive = IncidentArchive(":memory:")
    first = scenario_run("scenario-003")
    archive.add(first.incidents)
    second = scenario_run("scenario-008")
    (inc,) = run_analysis(second.store, DetectionConfig(), save=False, archive=archive)

    (match,) = inc.similar_incidents[:1]
    assert match["recurring"] and match["data_fingerprint"] == first.incidents[0].reproducibility["data_fingerprint"]
    inv = inc.investigation
    assert inv.guardrail["provider_output_valid"]
    history = [e for e in inv.observed_facts if e.kind == "history"]
    assert history and "may be a recurrence" in history[0].statement
    assert any("recurrence" in h.title for h in inv.hypotheses)


def test_an_incident_never_matches_itself(scenario_run):
    archive = IncidentArchive(":memory:")
    run = scenario_run("scenario-003")
    archive.add(run.incidents)
    (inc,) = run_analysis(run.store, DetectionConfig(), save=False, archive=archive)
    assert inc.similar_incidents == []


# --- natural-language queries -------------------------------------------------------------------
def _ws(flagship):
    return Workspace(flagship.store, DetectionConfig())


def test_parser_extracts_metric_window_and_cohorts(flagship):
    vocab = vocab_of(_ws(flagship))
    q = parse("Show regressions introduced during the last seven days affecting ARM64 but not x64", vocab)
    assert q.metric == "failure_rate" and q.window_minutes == 7 * 1440
    assert q.include == {"architecture": "arm64"} and q.exclude == [{"architecture": "x64"}]
    q = parse("memory leak on app 2.1 and driver 7.12 in the last 30 minutes", vocab)
    assert q.metric == "memory_mb" and q.window_minutes == 30
    assert q.include == {"application_version": "2.1", "driver_version": "7.12"}


def test_parser_reports_what_it_could_not_understand(flagship):
    q = parse("anything weird lately?", vocab_of(_ws(flagship)))
    assert q.include == {} and any("whole fleet" in n for n in q.notes)


def test_answers_match_ground_truth(flagship):
    ws = _ws(flagship)
    vocab = vocab_of(ws)
    incidents = flagship.incidents

    hit = answer(ws, parse("regressions affecting ARM64 but not x64", vocab), incidents)
    assert hit["verdict"] == "regression" and hit["exclude"][0]["regressed"] is False
    assert [i["id"] for i in hit["incidents"]] == ["AIL-1001"]

    miss = answer(ws, parse("failure regression on x64", vocab), incidents)
    assert miss["verdict"] == "no_regression" and miss["incidents"] == []

    mem = answer(ws, parse("memory leak on arm64 app 2.1", vocab), incidents)
    assert mem["verdict"] == "regression" and mem["include"]["ratio"] > 2

    broad = answer(ws, parse("failures on app 2.1 but not x64", vocab), incidents)
    assert broad["verdict"] == "regression"  # app 2.1 is elevated on arm64, not on x64


def test_window_longer_than_the_data_is_flagged(flagship):
    ws = _ws(flagship)
    res = answer(ws, parse("failures on arm64 in the last 7 days", vocab_of(ws)))
    assert res["window_clipped_to_available_data"] is True
