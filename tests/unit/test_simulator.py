from __future__ import annotations

import numpy as np
from ail_ingestion.store import TelemetryStore
from ail_ingestion.stream import Pipeline
from ail_schemas import TelemetryEvent
from ail_simulator import Simulator, iter_events, list_scenarios, load_scenario, simulate_to_store


def test_all_scenarios_load_and_cover_the_spec():
    ids = [s.id for s in list_scenarios()]
    assert ids == [f"scenario-00{n}" for n in range(1, 9)]


def test_same_seed_reproduces_identical_data_and_other_seed_differs():
    sc = load_scenario("scenario-003")
    fingerprints = []
    for seed in (3, 3, 4):
        store = TelemetryStore(":memory:")
        simulate_to_store(sc, store, devices=2000, seed=seed)
        fingerprints.append(store.data_fingerprint())
    assert fingerprints[0] == fingerprints[1] != fingerprints[2]


def test_rollout_exposure_ramps_to_the_configured_maximum():
    sc = load_scenario("scenario-003")
    sim = Simulator(sc, devices=20_000)
    dep = next(r for r in sim.rollouts if r.id == "dep-app-2.1")
    app_idx = sim.app_names.index("2.1")
    shares = []
    for minute in (dep.at_minute - 1, dep.at_minute + 5, dep.at_minute + 40):
        cells, _ = sim._device_cells(minute)
        app = (cells // sim.shape[3]) % sim.shape[2]
        shares.append(float((app == app_idx).mean()))
    assert shares[0] == 0.0
    assert abs(shares[1] - dep.max_exposure * 5 / dep.ramp_minutes) < 0.01
    assert abs(shares[2] - dep.max_exposure) < 0.01


def test_injected_regression_hits_only_the_target_cohort():
    sc = load_scenario("scenario-003")
    store = TelemetryStore(":memory:")
    simulate_to_store(sc, store, devices=30_000)
    rows = store.agg_rows("failure_rate")
    hit = [r for r in rows if r[2] == "arm64" and r[5] == "2.1"]
    rest = [r for r in rows if not (r[2] == "arm64" and r[5] == "2.1")]
    def rate(rs):
        return sum(r[-3] for r in rs) / sum(r[-4] for r in rs)

    assert 0.05 < rate(hit) < 0.11
    assert rate(rest) < 0.006


def test_event_mode_flows_through_the_ingestion_pipeline():
    sc = load_scenario("scenario-003")
    sim = Simulator(sc, devices=500)
    events = [TelemetryEvent.model_validate(e) for e in iter_events(sim, minutes=10)]
    assert events and {e.event_type for e in events} == {"session", "metric"}
    store = TelemetryStore(":memory:")
    Pipeline(store).process(events)
    sessions = sum(1 for e in events if e.event_type == "session")
    assert sum(r[-4] for r in store.agg_rows("failure_rate")) == sessions
    assert np.isclose(sum(r[-4] for r in store.agg_rows("latency_ms")), sessions)
