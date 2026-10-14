from __future__ import annotations

import pytest
from ail_ingestion import create_ingestion_app
from ail_ingestion.store import TelemetryStore
from ail_ingestion.stream import InMemoryStream
from fastapi.testclient import TestClient

EVENT = dict(
    timestamp="2026-03-10T12:00:30Z", device_id="d1", session_id="s1", os="AtlasOS", os_version="12.4",
    architecture="aarch64", device_model="Nova-A1", application="Atlas", application_version="2.1",
    driver_version="7.14", event_type="session",
)


@pytest.fixture()
def client_store():
    store = TelemetryStore(":memory:")
    app = create_ingestion_app(store, inline=True)
    return TestClient(app), store, app


def _rows(store, metric):
    return store.agg_rows(metric)


def test_events_are_normalized_and_aggregated(client_store):
    client, store, _ = client_store
    body = [
        {**EVENT, "session_id": "a"},
        {**EVENT, "session_id": "b", "failure_signature": "OOM_KILL|X"},
        {**EVENT, "session_id": "c", "event_type": "crash"},
    ]
    r = client.post("/api/v1/events", json=body)
    assert r.status_code == 202 and r.json()["accepted"] == 3
    (row,) = _rows(store, "failure_rate")
    assert row[2] == "arm64"  # aarch64 alias normalised
    assert (row[-4], row[-3]) == (3.0, 2.0)  # 3 sessions, 2 failed (explicit + crash)
    assert {m for m in store.metrics()} >= {"sig:OOM_KILL|X", "sig:UNKNOWN_CRASH"}


def test_duplicate_deliveries_do_not_double_count(client_store):
    client, store, _ = client_store
    for _ in range(3):
        client.post("/api/v1/events", json=[EVENT])
    assert store.counts()["raw_events"] == 1
    assert _rows(store, "failure_rate")[0][-4] == 1.0


def test_malformed_items_are_rejected_individually(client_store):
    client, store, _ = client_store
    body = [EVENT, {"timestamp": "nope"}, {**EVENT, "session_id": "z", "dimensions": {"email": "x"}}]
    r = client.post("/api/v1/events", json={"events": body})
    data = r.json()
    assert r.status_code == 202 and data["accepted"] == 1
    assert [x["index"] for x in data["rejected"]] == [1, 2]


def test_invalid_json_and_wrong_shape_are_client_errors(client_store):
    client, _, _ = client_store
    assert client.post("/api/v1/events", content=b"{not json").status_code == 400
    assert client.post("/api/v1/events", json="string").status_code == 400


def test_metrics_endpoint_only_accepts_metric_events(client_store):
    client, store, _ = client_store
    sample = {**EVENT, "event_type": "metric", "metric_name": "memory_mb", "metric_value": 512.0}
    r = client.post("/api/v1/metrics", json=[sample, EVENT])
    assert r.json()["accepted"] == 1 and len(r.json()["rejected"]) == 1
    (row,) = _rows(store, "memory_mb")
    assert row[-2] == 512.0 and row[-1] == 512.0**2


def test_deployments_are_stored(client_store):
    client, store, _ = client_store
    dep = dict(deployment_id="dep-1", timestamp="2026-03-10T11:52:00Z", kind="app_deployment", component="Atlas",
               dimension="application_version", from_version="2.0", to_version="2.1")
    assert client.post("/api/v1/deployments", json=[dep, {"bad": 1}]).json()["accepted"] == 1
    assert [c.deployment_id for c in store.list_changes()] == ["dep-1"]


def test_api_token_is_enforced_when_configured():
    store = TelemetryStore(":memory:")
    app = create_ingestion_app(store, inline=True)
    app.state.ctx.token = "s3cret"
    client = TestClient(app)
    assert client.post("/api/v1/events", json=[EVENT]).status_code == 401
    assert client.post("/api/v1/events", json=[EVENT], headers={"Authorization": "Bearer wrong"}).status_code == 401
    assert client.post("/api/v1/events", json=[EVENT], headers={"Authorization": "Bearer s3cret"}).status_code == 202


def test_full_stream_returns_retryable_503(client_store):
    client, _, app = client_store
    app.state.ctx.stream = InMemoryStream(maxsize=1)
    r = client.post("/api/v1/events", json=[EVENT, {**EVENT, "session_id": "b"}])
    assert r.status_code == 503 and r.headers["Retry-After"] == "1"


def test_health_and_self_metrics(client_store):
    client, _, _ = client_store
    client.post("/api/v1/events", json=[EVENT])
    assert client.get("/health").json()["status"] == "ok"
    text = client.get("/metrics").text
    assert "ail_ingest_events_total" in text and "ail_queue_depth" in text
