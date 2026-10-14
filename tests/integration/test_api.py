from __future__ import annotations

import pytest
from ail_api.app import create_app
from ail_ingestion.store import TelemetryStore
from ail_simulator import load_scenario, simulate_to_store
from fastapi.testclient import TestClient


@pytest.fixture(scope="module")
def client(tmp_path_factory):
    db = tmp_path_factory.mktemp("api") / "ail.db"
    store = TelemetryStore(db)
    simulate_to_store(load_scenario("scenario-003"), store, devices=20_000)
    store.close()
    app = create_app(db, inline=True)
    with TestClient(app) as c:
        assert c.post("/api/v1/analyze").json()["incidents"] == ["AIL-1001"]
        yield c


def test_portal_and_health_are_served(client):
    assert client.get("/health").json()["status"] == "ok"
    assert "OS Intelligence Lab" in client.get("/").text


def test_overview_matches_command_center_contract(client):
    ov = client.get("/api/v1/overview").json()
    assert ov["incidents"]["active"] == 1 and ov["incidents"]["critical"] == 1
    assert ov["health"]["monitored_series"] > ov["health"]["deviating_series"] >= 1
    assert ov["failure_rate"]["ratio"] > 1


def test_incident_endpoints(client):
    inc = client.get("/api/v1/incidents/AIL-1001").json()
    assert inc["cohort_label"] == "ARM64 + App 2.1" and "timemachine" not in inc
    tm = client.get("/api/v1/incidents/AIL-1001/timemachine").json()
    kinds = [e["kind"] for e in tm["events"]]
    assert kinds.index("deployment_started") < kinds.index("first_abnormal") < kinds.index("ai_completed")
    assert all(e["simulated_latency"] for e in tm["events"] if e["kind"] in ("correlation_discovered", "ai_completed"))
    assert len(tm["frames"]["cohort"]) == len(tm["minutes"])
    assert "Observed facts" in client.get("/api/v1/incidents/AIL-1001/report").text
    assert client.get("/api/v1/incidents/AIL-9999").status_code == 404


def test_anomaly_filters(client):
    everything = client.get("/api/v1/anomalies").json()
    only = client.get("/api/v1/anomalies", params={"detector": "CohortScanDetector", "metric": "failure_rate"}).json()
    assert 0 < len(only) < len(everything)
    assert all(r["detector"] == "CohortScanDetector" for r in only)
    assert client.get("/api/v1/anomalies", params={"architecture": "x86"}).json() == []


def test_cohort_drilldown_and_series(client):
    top = client.get("/api/v1/cohorts", params={"metric": "failure_rate"}).json()
    arch = next(d for d in top["dimensions"] if d["dimension"] == "architecture")
    assert arch["values"][0]["value"] == "arm64"  # most deviating value sorts first
    drill = client.get("/api/v1/cohorts", params={"filters": "architecture=arm64,application_version=2.1"}).json()
    assert drill["selection"]["ratio"] > 15
    assert all(d["dimension"] not in drill["filters"] for d in drill["dimensions"])
    s = client.get("/api/v1/series", params={"filters": "architecture=arm64"}).json()
    assert len(s["minutes"]) == len(s["observed"])
    assert client.get("/api/v1/series", params={"filters": "bogus=1"}).status_code == 400
    assert client.get("/api/v1/series", params={"metric": "nope"}).status_code == 404
