from __future__ import annotations

import sys
from datetime import datetime, timezone

import pytest
from ail_agent import SIGNALS, build_payload, grant, load_consent, revoke
from ail_agent.collectors import collect, memory_mb, parse_meminfo, parse_vm_stat, system_info
from ail_schemas import TelemetryEvent

MEMINFO = """MemTotal:       16384000 kB
MemFree:         1000000 kB
MemAvailable:    8192000 kB
Buffers:          100000 kB
"""
VM_STAT = """Mach Virtual Memory Statistics: (page size of 16384 bytes)
Pages free:                               20000.
Pages active:                            400000.
Pages inactive:                          300000.
Pages wired down:                        100000.
Pages occupied by compressor:             50000.
"""
FAKE_INFO = {"os": "TestOS", "os_version": "1.0", "architecture": "arm64", "device_model": "generic-arm64",
             "cpu": "arm64-8c", "memory_gb": 16.0}


@pytest.fixture(autouse=True)
def agent_home(tmp_path, monkeypatch):
    monkeypatch.setenv("AIL_AGENT_HOME", str(tmp_path / "agent"))


def test_linux_and_macos_parsers_use_documented_formulas():
    used, total = parse_meminfo(MEMINFO)
    assert (round(used), round(total)) == (8000, 16000)
    used, total = parse_vm_stat(VM_STAT, 16 * 1024**3)
    assert round(used) == round(550000 * 16384 / 1048576) and round(total) == 16384


def test_agent_refuses_without_consent_and_revoke_deletes_it():
    assert load_consent() is None
    grant(["memory_mb"], "http://127.0.0.1:8000")
    assert load_consent() is not None
    assert revoke() is True and load_consent() is None


def test_grant_rejects_unknown_signals_and_keeps_a_stable_random_device_id():
    with pytest.raises(ValueError):
        grant(["keystrokes"], "http://x")
    with pytest.raises(ValueError):
        grant([], "http://x")
    first = grant(["memory_mb"], "http://x")
    again = grant(["memory_mb", "cpu_load_pct"], "http://x")
    assert first.device_id == again.device_id and len(first.device_id) == 32


def test_payload_contains_only_approved_signals_and_validates_against_the_backend_schema():
    consent = grant(["session_heartbeat", "memory_mb"], "http://x")
    payload = build_payload(consent, datetime(2026, 3, 10, tzinfo=timezone.utc),
                            collector=lambda s: {"memory_mb": 1234.5}.get(s), info=FAKE_INFO)
    events = [TelemetryEvent.model_validate(e) for e in payload]  # extra fields would raise
    assert {(e.event_type, e.metric_name) for e in events} == {("session", None), ("metric", "memory_mb")}
    assert all(e.device_id == consent.device_id for e in events)
    assert "cpu_load_pct" not in str(payload)


def test_unapproved_signals_are_never_collected():
    consent = grant(["session_heartbeat"], "http://x")
    called = []
    build_payload(consent, collector=lambda s: called.append(s), info=FAKE_INFO)
    assert called == []


def test_system_info_has_no_identifying_fields():
    info = system_info()
    assert set(info) == {"os", "os_version", "architecture", "device_model", "cpu", "memory_gb"}
    import platform
    assert platform.node() not in str(info) or platform.node() in ("", "localhost")


@pytest.mark.skipif(sys.platform not in ("linux", "darwin", "win32"), reason="unsupported platform")
def test_live_collectors_return_plausible_values_on_this_machine():
    used, total = memory_mb()
    assert 0 < used <= total
    assert 0.0 <= collect("cpu_load_pct") <= 100.0
    assert TelemetryEvent.model_validate({**build_payload(grant(list(SIGNALS), "http://x"))[0]})
