"""Scenario definitions for the synthetic telemetry simulator."""

from __future__ import annotations

import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal

import yaml
from pydantic import BaseModel, ConfigDict, Field


class GaugeSpec(BaseModel):
    mean: float
    std: float


class Population(BaseModel):
    devices: int = Field(default=100_000, ge=1)
    os: str = "AtlasOS"
    application: str = "Atlas"
    architectures: dict[str, float] = {"x64": 0.7, "arm64": 0.2, "x86": 0.1}
    models: dict[str, list[str]] = {
        "x64": ["Aero-X1", "Aero-X2", "Titan-15", "Titan-17"],
        "arm64": ["Nova-A1", "Nova-A2"],
        "x86": ["Legacy-R1", "Legacy-R2"],
    }
    os_versions: dict[str, float] = {"12.4": 0.5, "12.5": 0.35, "13.0": 0.15}
    app_versions: dict[str, float] = {"2.0": 1.0}
    drivers: dict[str, float] = {"7.12": 0.5, "7.13": 0.5}


class Baseline(BaseModel):
    failure_rate: float = 0.004
    active_session_rate: float = 0.05  # probability a device completes a session in a minute
    latency_ms: GaugeSpec = GaugeSpec(mean=240, std=80)
    memory_mb: GaugeSpec = GaugeSpec(mean=410, std=90)
    model_spread: float = 0.15  # relative spread of per-model baseline differences
    signatures: dict[str, float] = {
        "UNHANDLED_EXCEPTION|Core.Dispatch": 0.40,
        "TIMEOUT|Net.Fetch": 0.30,
        "ACCESS_VIOLATION|Render.Draw": 0.20,
        "WATCHDOG_RESET|Sys.Idle": 0.10,
    }


class Rollout(BaseModel):
    """A staged deployment that moves devices from `from_version` to `to_version`."""

    id: str
    kind: Literal["app_deployment", "driver_update", "os_update", "config_change"] = "app_deployment"
    component: str
    dimension: Literal["os_version", "driver_version", "application_version"]
    from_version: str
    to_version: str
    at_minute: int
    ramp_minutes: int = 0
    max_exposure: float = Field(default=1.0, gt=0, le=1)
    description: str = ""
    repository: str | None = None
    base_ref: str | None = None
    head_ref: str | None = None


class ChangeSpec(BaseModel):
    """A change that is recorded but does not move devices between versions (e.g. a config change)."""

    id: str
    kind: Literal["config_change", "dependency_update", "infrastructure_change"]
    component: str
    at_minute: int
    description: str = ""


class Effect(BaseModel):
    """A regression injected into every cell matching `where`."""

    id: str
    where: dict[str, str | list[str]] = Field(default_factory=dict)
    start_minute: int = 0
    end_minute: int | None = None
    ramp_minutes: int = 0
    failure_rate: float | None = None
    latency_multiplier: float = 1.0
    memory_multiplier: float = 1.0
    signature: str | None = None
    description: str = ""


class Traffic(BaseModel):
    start_minute: int
    end_minute: int
    multiplier: float


class Episode(BaseModel):
    """One injected regression the platform is expected to find."""

    effect_start_minute: int
    cohort: dict[str, str] | None = None
    trigger: str | None = None
    metric: str | None = None
    culprit_commit: str | None = None


class Expected(BaseModel):
    incidents: int = 0
    effect_start_minute: int | None = None
    cohort: dict[str, str] | None = None
    trigger: str | None = None
    metric: str | None = None
    culprit_commit: str | None = None
    episodes: list[Episode] = []  # use instead of the single-incident fields when there are several

    def all_episodes(self) -> list[Episode]:
        if self.episodes:
            return self.episodes
        if self.incidents and self.effect_start_minute is not None:
            return [Episode(effect_start_minute=self.effect_start_minute, cohort=self.cohort, trigger=self.trigger,
                            metric=self.metric, culprit_commit=self.culprit_commit)]
        return []


class Scenario(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str
    name: str
    description: str = ""
    seed: int = 1
    start: datetime = datetime(2026, 3, 10, 12, 0, tzinfo=timezone.utc)
    duration_minutes: int = 180
    population: Population = Population()
    baseline: Baseline = Baseline()
    rollouts: list[Rollout] = []
    changes: list[ChangeSpec] = []
    effects: list[Effect] = []
    traffic: list[Traffic] = []
    expected: Expected = Expected()

    def config_hash(self) -> str:
        return hashlib.sha256(json.dumps(self.model_dump(mode="json"), sort_keys=True).encode()).hexdigest()[:12]


def repo_root() -> Path:
    env = os.environ.get("AIL_HOME")
    return Path(env) if env else Path(__file__).resolve().parents[3]


def scenario_dir() -> Path:
    return repo_root() / "docs" / "scenarios"


def load_scenario(ref: str | Path) -> Scenario:
    """Load a scenario by file path, id (`scenario-003`) or id prefix."""
    path = Path(ref)
    if not path.exists():
        matches = sorted(scenario_dir().glob(f"{ref}*.yaml"))
        if not matches:
            raise FileNotFoundError(f"no scenario matching '{ref}' in {scenario_dir()}")
        path = matches[0]
    data: dict[str, Any] = yaml.safe_load(path.read_text())
    return Scenario.model_validate(data)


def list_scenarios() -> list[Scenario]:
    return [load_scenario(p) for p in sorted(scenario_dir().glob("scenario-*.yaml"))]


def list_heldout_scenarios() -> list[Scenario]:
    """Scenarios written after the detector settings were fixed; never used for tuning."""
    return [load_scenario(p) for p in sorted((scenario_dir() / "heldout").glob("heldout-*.yaml"))]
