from __future__ import annotations

import pytest
from ail_schemas import ChangeEvent, TelemetryEvent
from pydantic import ValidationError

BASE = dict(
    timestamp="2026-03-10T12:00:00Z", device_id="d1", session_id="s1", os="AtlasOS", os_version="12.4",
    architecture="arm64", device_model="Nova-A1", application="Atlas", application_version="2.1",
    event_type="session",
)


def test_valid_session_event_is_utc_and_has_stable_id():
    ev = TelemetryEvent.model_validate(BASE)
    assert ev.timestamp.utcoffset().total_seconds() == 0
    assert ev.resolved_event_id() == TelemetryEvent.model_validate(BASE).resolved_event_id()


def test_naive_timestamp_is_treated_as_utc():
    ev = TelemetryEvent.model_validate({**BASE, "timestamp": "2026-03-10T12:00:00"})
    assert ev.timestamp.tzinfo is not None


@pytest.mark.parametrize("key", ["email", "user_name", "ip_address", "Hostname", "phone-number"])
def test_pii_looking_dimension_keys_are_rejected(key):
    with pytest.raises(ValidationError):
        TelemetryEvent.model_validate({**BASE, "dimensions": {key: "x"}})


def test_unknown_fields_are_rejected():
    with pytest.raises(ValidationError):
        TelemetryEvent.model_validate({**BASE, "email": "someone@example.com"})


def test_metric_events_need_name_and_finite_value():
    with pytest.raises(ValidationError):
        TelemetryEvent.model_validate({**BASE, "event_type": "metric"})
    with pytest.raises(ValidationError):
        TelemetryEvent.model_validate({**BASE, "event_type": "metric", "metric_name": "m", "metric_value": float("inf")})
    assert TelemetryEvent.model_validate({**BASE, "event_type": "metric", "metric_name": "m", "metric_value": 1.5})


def test_change_event_dimension_must_be_known():
    ok = dict(deployment_id="d", timestamp="2026-03-10T12:00:00Z", kind="app_deployment", component="Atlas")
    assert ChangeEvent.model_validate({**ok, "dimension": "application_version"})
    with pytest.raises(ValidationError):
        ChangeEvent.model_validate({**ok, "dimension": "colour"})
