"""The investigation agent: evidence in, grounded hypotheses out."""

from __future__ import annotations

import json
import re
import time

from ail_detection.labels import cohort_label
from ail_schemas import (
    CandidateCommit,
    ChangeEvent,
    Hypothesis,
    Incident,
    Investigation,
    RecommendedStep,
)
from ail_schemas.observability import REGISTRY, get_logger

from ail_investigation.codeintel import rank_commits
from ail_investigation.evidence import build_evidence
from ail_investigation.grounding import validate_output
from ail_investigation.llm import SYSTEM_PROMPT, LLMProvider, RulesProvider
from ail_investigation.repos import CommitInfo, RepoProvider, get_provider

log = get_logger("ail.investigation")


def _extract_json(text: str) -> dict:
    text = text.strip()
    fence = re.match(r"^```(?:json)?\s*(.*?)\s*```$", text, re.S)
    return json.loads(fence.group(1) if fence else text)


class InvestigationAgent:
    def __init__(self, llm: LLMProvider | None = None, repo_mode: str = "auto",
                 repo: RepoProvider | None = None) -> None:
        self.llm = llm or RulesProvider()
        self.repo_mode = repo_mode
        self._repo = repo

    def _code_candidates(self, inc: Incident) -> tuple[list[CandidateCommit], int, str | None, tuple[str, str] | None]:
        lead = next((c for c in inc.correlations if not c.after_onset and c.score >= 0.3
                     and c.change.repository and c.change.base_ref and c.change.head_ref), None)
        if lead is None:
            return [], 0, None, None
        ch: ChangeEvent = lead.change
        repo = self._repo or get_provider(ch.repository, self.repo_mode)
        if repo is None:
            return [], 0, None, None
        try:
            commits: list[CommitInfo] = repo.compare(ch.base_ref, ch.head_ref)
        except Exception:
            log.exception("repository comparison failed", extra={"fields": {"repo": ch.repository}})
            return [], 0, None, None
        signatures = [s["signature"] for s in inc.signatures if s["excess_share"] >= 0.2]
        significant = [s["metric"] for s in inc.supporting_metrics if s["significant"]]
        arch = inc.cohorts[0].dimensions.get("architecture") if inc.cohorts else None
        ranked = rank_commits(commits, inc.metric, signatures, significant, arch)
        return ranked, len(commits), repo.name, (ch.base_ref, ch.head_ref)

    def investigate(self, inc: Incident) -> Investigation:
        start = time.perf_counter()
        candidates, total, repo_name, refs = self._code_candidates(inc)
        evidence = build_evidence(inc, candidates, total, repo_name, refs)
        known_shas = [c.sha for c in candidates]
        payload = json.dumps({
            "incident": {"id": inc.id, "title": inc.title},
            "evidence": [{"id": e.id, "kind": e.kind, "statement": e.statement,
                          "data": {k: v for k, v in e.data.items() if k != "message"}} for e in evidence],
        })
        guardrail: dict = {"provider": self.llm.name, "evidence_items": len(evidence),
                           "facts_source": "deterministic statistics (never model-generated)"}
        output, used = None, self.llm.name
        t0 = time.perf_counter()
        try:
            result = self.llm.complete(SYSTEM_PROMPT, payload)
            REGISTRY.observe("ail_ai_request_latency_seconds", time.perf_counter() - t0)
            REGISTRY.inc("ail_ai_tokens_total", result.prompt_tokens + result.completion_tokens)
            raw = _extract_json(result.text)
            violations = validate_output(raw, evidence, known_shas)
            guardrail.update(provider_output_valid=not violations, violations=violations[:10])
            output = raw if not violations else None
        except Exception as exc:
            REGISTRY.inc("ail_ai_errors_total")
            guardrail.update(provider_output_valid=False, violations=[f"provider error: {type(exc).__name__}"])
            log.warning("LLM provider failed", extra={"fields": {"provider": self.llm.name}})
        if output is None:
            fallback = RulesProvider()
            output = _extract_json(fallback.complete(SYSTEM_PROMPT, payload).text)
            violations = validate_output(output, evidence, known_shas)
            if violations:  # a bug in the rules provider must never reach users
                raise RuntimeError(f"fallback output failed grounding: {violations}")
            used = fallback.name
            guardrail["fallback_used"] = self.llm.name != fallback.name
        guardrail["generated_by"] = used
        REGISTRY.observe("ail_investigation_duration_seconds", time.perf_counter() - start)
        return Investigation(
            provider=used,
            summary=output["summary"],
            observed_facts=evidence,
            hypotheses=[Hypothesis(**h) for h in output["hypotheses"]],
            recommended_steps=[RecommendedStep(**s) for s in output["recommended_steps"]],
            candidate_commits=candidates,
            guardrail=guardrail,
        )


def scope_of(inc: Incident) -> str:
    return cohort_label(inc.cohorts[0].dimensions) if inc.cohorts else "all devices"
