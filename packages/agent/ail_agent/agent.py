"""Payload construction and delivery. `build_payload` is what `preview` prints and `run` sends."""

from __future__ import annotations

import time
from collections.abc import Callable
from datetime import datetime, timezone

from ail_sdk import IngestionClient

from ail_agent.collectors import collect, system_info
from ail_agent.consent import Consent

AGENT_NAME = "ail-agent"
AGENT_VERSION = "0.1"


def build_payload(consent: Consent, now: datetime | None = None,
                  collector: Callable[[str], float | None] = collect,
                  info: dict | None = None) -> list[dict]:
    """TelemetryEvent-shaped dicts containing only the approved signals."""
    now = now or datetime.now(timezone.utc)
    info = info or system_info()
    base = {
        "timestamp": now.isoformat(), "device_id": consent.device_id,
        "session_id": f"{consent.device_id[:8]}-{int(now.timestamp())}", **info,
        "application": AGENT_NAME, "application_version": AGENT_VERSION,
    }
    events: list[dict] = []
    if "session_heartbeat" in consent.signals:
        events.append({**base, "event_type": "session"})
    for signal in consent.signals:
        if signal == "session_heartbeat":
            continue
        value = collector(signal)
        if value is not None:
            events.append({**base, "event_type": "metric", "metric_name": signal, "metric_value": float(value)})
    return events


def run_once(consent: Consent, token: str | None = None, client: IngestionClient | None = None) -> dict:
    client = client or IngestionClient(consent.endpoint, token=token)
    return client.send_events(build_payload(consent))


def run_loop(consent: Consent, interval: float, count: int | None = None, token: str | None = None) -> None:
    client = IngestionClient(consent.endpoint, token=token)
    sent = 0
    while count is None or sent < count:
        print(run_once(consent, client=client), flush=True)
        sent += 1
        if count is None or sent < count:
            time.sleep(interval)
