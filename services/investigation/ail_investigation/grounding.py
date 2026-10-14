"""Guardrails: model output may explain the evidence but must not invent telemetry facts."""

from __future__ import annotations

import re

from ail_schemas import EvidenceItem
from pydantic import BaseModel, Field, ValidationError

_NUM = re.compile(r"(?<![\w.])\d+(?:\.\d+)?(?![\w])")
_HEX = re.compile(r"\b(?=[0-9a-f]*\d)(?=[0-9a-f]*[a-f])[0-9a-f]{7,40}\b")
_CERTAIN = re.compile(r"\b(proves?|proven|definitely|certainly|undoubtedly|guaranteed)\b", re.I)
_ID = re.compile(r"^[ECH]\d+$")


class _Hyp(BaseModel):
    title: str = Field(min_length=3, max_length=300)
    rationale: str = Field(min_length=3, max_length=2000)
    confidence: str
    supporting_evidence: list[str]


class _Step(BaseModel):
    step: str = Field(min_length=3, max_length=600)
    evidence: list[str] = []


class AgentOutput(BaseModel):
    summary: str = Field(min_length=3, max_length=3000)
    hypotheses: list[_Hyp] = Field(min_length=1, max_length=5)
    recommended_steps: list[_Step] = Field(max_length=8)


def numbers_in(text: str) -> set[float]:
    return {round(float(m), 6) for m in _NUM.findall(text)}


def validate_output(raw: dict, evidence: list[EvidenceItem], known_shas: list[str]) -> list[str]:
    """Return a list of violations; an empty list means the output is acceptable."""
    try:
        out = AgentOutput.model_validate(raw)
    except ValidationError as exc:
        return [f"schema: {e['loc']}: {e['msg']}" for e in exc.errors()[:5]]

    ids = {e.id for e in evidence}
    allowed = set().union(*(numbers_in(e.statement) for e in evidence)) if evidence else set()
    problems: list[str] = []

    def check_text(where: str, text: str) -> None:
        extra = numbers_in(text) - allowed
        if extra:
            problems.append(f"{where}: numbers not present in evidence: {sorted(extra)}")
        for token in _HEX.findall(text):
            if not any(sha.startswith(token) for sha in known_shas):
                problems.append(f"{where}: unknown commit hash '{token}'")
        if _CERTAIN.search(text):
            problems.append(f"{where}: asserts certainty about a hypothesis")

    def check_refs(where: str, refs: list[str]) -> None:
        for ref in refs:
            if not _ID.match(ref) or ref not in ids:
                problems.append(f"{where}: cites unknown evidence '{ref}'")

    check_text("summary", out.summary)
    for i, h in enumerate(out.hypotheses):
        check_text(f"hypothesis[{i}]", f"{h.title} {h.rationale}")
        if h.confidence not in ("low", "medium", "high"):
            problems.append(f"hypothesis[{i}]: invalid confidence '{h.confidence}'")
        if not h.supporting_evidence:
            problems.append(f"hypothesis[{i}]: no supporting evidence cited")
        check_refs(f"hypothesis[{i}]", h.supporting_evidence)
    for i, s in enumerate(out.recommended_steps):
        check_text(f"step[{i}]", s.step)
        check_refs(f"step[{i}]", s.evidence)
    return problems
