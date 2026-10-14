"""Full analysis workflow: detect -> investigate -> replay -> persist."""

from __future__ import annotations

from datetime import datetime

from ail_detection import DetectionConfig, Workspace, analyze
from ail_schemas import Incident

from ail_investigation.agent import InvestigationAgent
from ail_investigation.history import IncidentArchive
from ail_investigation.llm import LLMProvider
from ail_investigation.timemachine import build_timemachine


def run_analysis(
    store,
    cfg: DetectionConfig | None = None,
    llm: LLMProvider | None = None,
    repo_mode: str = "auto",
    investigate: bool = True,
    save: bool = True,
    archive: IncidentArchive | None = None,
) -> list[Incident]:
    ws = Workspace(store, cfg)
    incidents = analyze(store, ws=ws)
    agent = InvestigationAgent(llm, repo_mode)
    windows: dict[str, Workspace] = {}
    for inc in incidents:
        start = inc.reproducibility["analysis_start"]
        if start not in windows:  # later episodes were analysed on a re-baselined window
            windows[start] = Workspace(store, cfg, start=int(datetime.fromisoformat(start).timestamp() // 60))
        if archive is not None:
            inc.similar_incidents = archive.find_similar(inc)
        if investigate:
            inc.investigation = agent.investigate(inc)
            if inc.status == "open":
                inc.status = "investigating"
        inc.timeline, inc.timemachine = build_timemachine(windows[start], inc)
    if save:
        store.replace_incidents(incidents)
    if archive is not None:
        archive.add(incidents)
    return incidents
