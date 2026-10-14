"""Full analysis workflow: detect -> investigate -> replay -> persist."""

from __future__ import annotations

from ail_detection import DetectionConfig, Workspace, analyze
from ail_schemas import Incident

from ail_investigation.agent import InvestigationAgent
from ail_investigation.llm import LLMProvider
from ail_investigation.timemachine import build_timemachine


def run_analysis(
    store,
    cfg: DetectionConfig | None = None,
    llm: LLMProvider | None = None,
    repo_mode: str = "auto",
    investigate: bool = True,
    save: bool = True,
) -> list[Incident]:
    ws = Workspace(store, cfg)
    incidents = analyze(store, ws=ws)
    agent = InvestigationAgent(llm, repo_mode)
    for inc in incidents:
        if investigate:
            inc.investigation = agent.investigate(inc)
            if inc.status == "open":
                inc.status = "investigating"
        inc.timeline, inc.timemachine = build_timemachine(ws, inc)
    if save:
        store.replace_incidents(incidents)
    return incidents
