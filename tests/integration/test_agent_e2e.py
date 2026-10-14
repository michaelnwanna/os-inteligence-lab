"""Runs the real collectors of whatever OS the tests execute on and ingests the result end to end."""

from __future__ import annotations

import sys

import pytest
from ail_agent import grant, run_once
from ail_ingestion import create_ingestion_app
from ail_ingestion.store import TelemetryStore
from ail_sdk import IngestionClient
from fastapi.testclient import TestClient


@pytest.fixture(autouse=True)
def agent_home(tmp_path, monkeypatch):
    monkeypatch.setenv("AIL_AGENT_HOME", str(tmp_path / "agent"))


@pytest.mark.skipif(sys.platform not in ("linux", "darwin", "win32"), reason="unsupported platform")
def test_real_signals_from_this_machine_land_in_the_backend_schema():
    store = TelemetryStore(":memory:")
    http = TestClient(create_ingestion_app(store, inline=True))
    consent = grant(["session_heartbeat", "cpu_load_pct", "memory_mb"], "http://testserver")
    result = run_once(consent, client=IngestionClient(client=http))

    assert result == {"accepted": 3, "rejected": 0}
    (heartbeat,) = store.agg_rows("failure_rate")
    assert heartbeat[-4] == 1.0 and heartbeat[-3] == 0.0  # one healthy session
    (memory,) = store.agg_rows("memory_mb")
    assert memory[-2] > 0  # sum of one sample is that sample, in MB
    assert "cpu_load_pct" in store.metrics()
    os_names = {row[1] for row in store.agg_rows("memory_mb")}
    assert os_names  # the OS version string arrives as a cohort dimension
