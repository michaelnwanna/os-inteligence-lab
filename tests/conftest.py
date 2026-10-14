"""Shared fixtures. Scenario runs are cached per session because simulation dominates test time."""

from __future__ import annotations

import pytest
from ail_detection import DetectionConfig
from ail_ingestion.store import TelemetryStore
from ail_investigation import run_analysis
from ail_simulator import list_scenarios, load_scenario, simulate_to_store

TEST_DEVICES = 30_000


class ScenarioRun:
    def __init__(self, scenario_id: str, devices: int = TEST_DEVICES, seed: int | None = None):
        self.scenario = load_scenario(scenario_id)
        self.store = TelemetryStore(":memory:")
        simulate_to_store(self.scenario, self.store, devices=devices, seed=seed)
        self.incidents = run_analysis(self.store, DetectionConfig(), save=True)


@pytest.fixture(scope="session")
def runs() -> dict[str, ScenarioRun]:
    return {}


@pytest.fixture(scope="session")
def scenario_run(runs):
    def get(scenario_id: str) -> ScenarioRun:
        if scenario_id not in runs:
            runs[scenario_id] = ScenarioRun(scenario_id)
        return runs[scenario_id]

    return get


@pytest.fixture(scope="session")
def flagship(scenario_run) -> ScenarioRun:
    return scenario_run("scenario-003")


def scenario_ids() -> list[str]:
    return [s.id for s in list_scenarios()]
