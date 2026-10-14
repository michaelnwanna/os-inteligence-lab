"""Explicit, revocable consent. Without a consent file the agent refuses to run."""

from __future__ import annotations

import json
import os
import uuid
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path

from ail_agent.collectors import SIGNALS


def config_dir() -> Path:
    return Path(os.environ.get("AIL_AGENT_HOME") or Path.home() / ".ail-agent")


@dataclass
class Consent:
    signals: list[str]
    endpoint: str
    device_id: str  # random, generated locally; not derived from hardware or user identity
    granted_at: str


def load_consent() -> Consent | None:
    path = config_dir() / "consent.json"
    if not path.exists():
        return None
    data = json.loads(path.read_text())
    return Consent(**data) if set(data["signals"]) <= set(SIGNALS) else None


def grant(signals: list[str], endpoint: str) -> Consent:
    unknown = set(signals) - set(SIGNALS)
    if unknown or not signals:
        raise ValueError(f"signals must be a non-empty subset of {sorted(SIGNALS)}")
    existing = load_consent()
    consent = Consent(sorted(signals), endpoint, existing.device_id if existing else uuid.uuid4().hex,
                      datetime.now(timezone.utc).isoformat())
    config_dir().mkdir(parents=True, exist_ok=True)
    path = config_dir() / "consent.json"
    path.write_text(json.dumps(asdict(consent), indent=2))
    path.chmod(0o600)
    return consent


def revoke() -> bool:
    """Delete consent and the local device id."""
    path = config_dir() / "consent.json"
    existed = path.exists()
    path.unlink(missing_ok=True)
    return existed
