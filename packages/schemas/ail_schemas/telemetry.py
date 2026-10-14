"""Core telemetry domain model. No personal-data fields are defined or accepted."""

from __future__ import annotations

import hashlib
import math
import re
from datetime import datetime, timezone
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

# Dimensions along which populations are segmented into cohorts.
DIMENSIONS: tuple[str, ...] = (
    "os_version",
    "architecture",
    "device_model",
    "application",
    "application_version",
    "driver_version",
)

_PII_TOKENS = frozenset(
    {"email", "username", "user", "ip", "phone", "address", "ssn", "name", "firstname",
     "lastname", "hostname", "mac", "imei", "serial"}
)


def _utc(value: datetime) -> datetime:
    return value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value.astimezone(timezone.utc)


class TelemetryEvent(BaseModel):
    """One telemetry record.

    `session` events describe a completed session (a set `failure_signature` marks a failed
    session). `crash` is accepted as an alias for a failed session. `metric` events carry a gauge
    sample in `metric_name` / `metric_value`.
    """

    model_config = ConfigDict(extra="forbid")

    event_id: str | None = Field(default=None, max_length=128)
    timestamp: datetime
    device_id: str = Field(min_length=1, max_length=128, description="Pseudonymous device id")
    session_id: str = Field(min_length=1, max_length=128)
    os: str = Field(min_length=1, max_length=64)
    os_version: str = Field(min_length=1, max_length=64)
    os_build: str | None = Field(default=None, max_length=64)
    architecture: str = Field(min_length=1, max_length=32)
    device_model: str = Field(min_length=1, max_length=64)
    cpu: str | None = Field(default=None, max_length=64)
    memory_gb: float | None = Field(default=None, ge=0, le=100_000)
    application: str = Field(min_length=1, max_length=64)
    application_version: str = Field(min_length=1, max_length=64)
    driver: str | None = Field(default=None, max_length=64)
    driver_version: str | None = Field(default=None, max_length=64)
    event_type: Literal["session", "crash", "metric"]
    metric_name: str | None = Field(default=None, max_length=64)
    metric_value: float | None = None
    failure_signature: str | None = Field(default=None, max_length=256)
    deployment_id: str | None = Field(default=None, max_length=128)
    dimensions: dict[str, str] = Field(default_factory=dict)

    @field_validator("timestamp")
    @classmethod
    def _tz(cls, v: datetime) -> datetime:
        return _utc(v)

    @field_validator("dimensions")
    @classmethod
    def _no_pii_keys(cls, v: dict[str, str]) -> dict[str, str]:
        if len(v) > 16:
            raise ValueError("at most 16 custom dimensions are allowed")
        for key in v:
            if set(re.split(r"[^a-z0-9]+", key.lower())) & _PII_TOKENS:
                raise ValueError(f"dimension key '{key}' looks like personal data and is not accepted")
        return v

    @model_validator(mode="after")
    def _shape(self) -> TelemetryEvent:
        if self.event_type == "metric":
            if not self.metric_name or self.metric_value is None:
                raise ValueError("metric events require metric_name and metric_value")
            if not math.isfinite(self.metric_value):
                raise ValueError("metric_value must be finite")
        return self

    def resolved_event_id(self) -> str:
        """Stable id used to drop duplicate deliveries of the same event."""
        if self.event_id:
            return self.event_id
        raw = "|".join(
            [self.timestamp.isoformat(), self.device_id, self.session_id, self.event_type,
             self.metric_name or ""]
        )
        return hashlib.sha256(raw.encode()).hexdigest()[:32]


ChangeKind = Literal[
    "app_deployment",
    "driver_update",
    "os_update",
    "config_change",
    "dependency_update",
    "infrastructure_change",
    "git_commit",
]


class ChangeEvent(BaseModel):
    """A change to the environment that may explain a regression."""

    model_config = ConfigDict(extra="forbid")

    deployment_id: str = Field(min_length=1, max_length=128)
    timestamp: datetime
    kind: ChangeKind
    component: str = Field(min_length=1, max_length=128)
    dimension: str | None = Field(default=None, description="Dimension that gains `to_version`")
    from_version: str | None = None
    to_version: str | None = None
    description: str = ""
    repository: str | None = None
    base_ref: str | None = None
    head_ref: str | None = None
    metadata: dict[str, Any] = Field(default_factory=dict)

    @field_validator("timestamp")
    @classmethod
    def _tz(cls, v: datetime) -> datetime:
        return _utc(v)

    @field_validator("dimension")
    @classmethod
    def _known_dimension(cls, v: str | None) -> str | None:
        if v is not None and v not in DIMENSIONS:
            raise ValueError(f"dimension must be one of {DIMENSIONS}")
        return v
