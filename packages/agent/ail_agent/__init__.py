"""Opt-in local telemetry agent. It only sends signals the user explicitly approved."""

from ail_agent.agent import build_payload, run_once
from ail_agent.collectors import SIGNALS
from ail_agent.consent import Consent, grant, load_consent, revoke

__all__ = ["SIGNALS", "Consent", "build_payload", "grant", "load_consent", "revoke", "run_once"]
