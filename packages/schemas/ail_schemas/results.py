"""Result models shared by detection, correlation and investigation."""

from __future__ import annotations

from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, Field

from ail_schemas.telemetry import ChangeEvent

Severity = Literal["low", "moderate", "high", "critical"]
SEVERITY_ORDER: dict[str, int] = {"low": 0, "moderate": 1, "high": 2, "critical": 3}


class AnomalyResult(BaseModel):
    """Standard output of every detector."""

    metric: str
    timestamp: datetime = Field(description="When the detector fired")
    onset: datetime | None = Field(default=None, description="Estimated start of the change")
    baseline: float
    observed_value: float
    delta: float
    severity: Severity
    severity_score: float
    confidence: float
    affected_population: float = Field(description="Share of sessions covered, 0-1")
    detector: str
    dimensions: dict[str, str] = Field(default_factory=dict)
    evidence: dict[str, Any] = Field(default_factory=dict)


class CohortFinding(BaseModel):
    metric: str
    dimensions: dict[str, str]
    sessions: float
    baseline: float
    observed: float
    ratio: float
    delta: float
    llr: float
    p_value: float = Field(description="Bonferroni-adjusted over all candidate cohorts")
    severity: Severity
    population_share: float
    excess_share: float
    rank: int


class CorrelationCandidate(BaseModel):
    change: ChangeEvent
    score: float
    temporal_score: float
    overlap: float
    delta_minutes: float = Field(description="Anomaly onset minus change time")
    after_onset: bool
    rationale: str


class TimelineEvent(BaseModel):
    ts: datetime
    kind: str
    title: str
    detail: str = ""
    simulated_latency: bool = False
    evidence: dict[str, Any] = Field(default_factory=dict)


class EvidenceItem(BaseModel):
    id: str
    kind: str
    statement: str
    data: dict[str, Any] = Field(default_factory=dict)


class Hypothesis(BaseModel):
    title: str
    rationale: str
    confidence: Literal["low", "medium", "high"]
    supporting_evidence: list[str]
    label: Literal["hypothesis"] = "hypothesis"


class RecommendedStep(BaseModel):
    step: str
    evidence: list[str] = Field(default_factory=list)


class CandidateCommit(BaseModel):
    sha: str
    message: str
    files: list[str]
    component: str | None = None
    score: float
    rank: int
    reasons: list[str] = Field(default_factory=list)
    snippets: list[str] = Field(default_factory=list)


class Investigation(BaseModel):
    provider: str
    summary: str
    observed_facts: list[EvidenceItem]
    hypotheses: list[Hypothesis]
    recommended_steps: list[RecommendedStep]
    candidate_commits: list[CandidateCommit] = Field(default_factory=list)
    guardrail: dict[str, Any] = Field(default_factory=dict)


class Incident(BaseModel):
    id: str
    title: str
    status: Literal["open", "investigating", "resolved"]
    severity: Severity
    metric: str
    onset: datetime
    first_detection: datetime
    confirmed: datetime | None = None
    baseline: float
    observed: float
    global_baseline: float
    global_observed: float
    supporting_metrics: list[dict[str, Any]] = Field(default_factory=list)
    results: list[AnomalyResult]
    cohorts: list[CohortFinding] = Field(default_factory=list)
    signatures: list[dict[str, Any]] = Field(default_factory=list)
    correlations: list[CorrelationCandidate] = Field(default_factory=list)
    similar_incidents: list[dict[str, Any]] = Field(default_factory=list)
    investigation: Investigation | None = None
    timeline: list[TimelineEvent] = Field(default_factory=list)
    timemachine: dict[str, Any] | None = None
    reproducibility: dict[str, Any] = Field(default_factory=dict)
