from __future__ import annotations

import httpx
import pytest
from ail_api.app import create_app
from ail_ingestion import create_ingestion_app
from ail_ingestion.ratelimit import RateLimiter
from ail_ingestion.store import TelemetryStore
from ail_sdk import IngestionClient
from fastapi.testclient import TestClient

EVENT = dict(
    timestamp="2026-03-10T12:00:30Z", device_id="d1", session_id="s1", os="AtlasOS", os_version="12.4",
    architecture="arm64", device_model="Nova-A1", application="Atlas", application_version="2.1",
    event_type="session",
)


def test_token_bucket_allows_bursts_then_throttles_then_recovers():
    rl = RateLimiter(rate=2, burst=3)
    assert [rl.allow("a", now=0.0)[0] for _ in range(4)] == [True, True, True, False]
    allowed, wait = rl.allow("a", now=0.0)
    assert not allowed and wait == pytest.approx(0.5)
    assert rl.allow("a", now=0.5)[0] is True  # one token refilled
    assert rl.allow("b", now=0.0)[0] is True  # clients are independent


def test_ingestion_returns_429_with_retry_after_when_a_client_floods():
    app = create_ingestion_app(TelemetryStore(":memory:"), inline=True)
    app.state.ctx.limiter = RateLimiter(rate=0.5, burst=2)
    client = TestClient(app)
    codes = [client.post("/api/v1/events", json=[{**EVENT, "session_id": str(i)}]).status_code for i in range(4)]
    assert codes[:2] == [202, 202] and codes[2:] == [429, 429]
    assert int(client.post("/api/v1/events", json=[EVENT]).headers["Retry-After"]) >= 1


def test_sdk_backs_off_on_429_and_succeeds():
    calls = []

    def handler(request: httpx.Request) -> httpx.Response:
        calls.append(1)
        if len(calls) < 3:
            return httpx.Response(429, headers={"Retry-After": "0"}, json={})
        return httpx.Response(202, json={"accepted": 1, "rejected": []})

    http = httpx.Client(base_url="http://x", transport=httpx.MockTransport(handler))
    assert IngestionClient(client=http).send_events([EVENT]) == {"accepted": 1, "rejected": 0}
    assert len(calls) == 3


def test_read_endpoints_need_the_read_token_when_configured(tmp_path):
    app = create_app(tmp_path / "a.db", inline=True, archive_path=":memory:", read_token="r3ad")
    with TestClient(app) as client:
        assert client.get("/health").status_code == 200  # liveness stays open
        for path in ("/api/v1/meta", "/api/v1/incidents", "/metrics"):
            assert client.get(path).status_code == 401, path
        assert client.get("/api/v1/meta", headers={"Authorization": "Bearer wrong"}).status_code == 401
        assert client.get("/api/v1/meta", headers={"Authorization": "Bearer r3ad"}).status_code == 200
        assert client.get("/").status_code == 200  # the portal shell itself carries no data
