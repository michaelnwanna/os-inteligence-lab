"""Detection engine configuration. The full config is hashed into every incident for reproducibility."""

from __future__ import annotations

import hashlib
import json

from pydantic import BaseModel, Field


class CohortConfig(BaseModel):
    window_minutes: int = 10
    max_depth: int = 3
    top_k: int = 3
    alpha: float = 1e-3  # Bonferroni-adjusted significance across all candidate cohorts
    depth_penalty: float = 2.0  # LLR points charged per extra dimension (prefers simple cohorts)
    min_events: float = 15  # minimum observed failures (rate metrics)
    min_ratio_rate: float = 1.5  # cohort vs. the rest of the population, after baseline adjustment
    min_ratio_gauge: float = 1.1
    prior_sessions_rate: float = 20_000  # shrinkage strength of the baseline hierarchy
    prior_sessions_gauge: float = 500
    consecutive: int = 2  # consecutive scans naming the same cohort before alerting


class CorrelationConfig(BaseModel):
    tau_minutes: float = 60.0  # temporal decay
    onset_tolerance_minutes: float = 3.0  # a change this much after the onset still counts as simultaneous
    weight_temporal: float = 0.4
    weight_overlap: float = 0.6
    lookback_minutes: float = 360.0


class DetectionConfig(BaseModel):
    baseline_minutes: int = 60
    metrics: list[str] = Field(default_factory=lambda: ["failure_rate", "latency_ms", "memory_mb"])
    window_minutes: int = 5
    z_threshold: float = 5.0
    min_consecutive: int = 3
    confirm_minutes: int = 2
    cusum_k: float = 0.5
    cusum_h: float = 8.0
    ewma_lambda: float = 0.2
    ewma_limit: float = 4.5
    changepoint_llr: float = 15.0
    min_relative_change: float = 0.10
    shift_window: int = 10
    shift_z: float = 5.0
    ml_threshold: float = 40.0
    merge_minutes: int = 30  # results this close in time belong to one anomaly
    incident_onset_minutes: int = 20  # anomalies on different metrics this close share an incident
    recovery_z: float = 2.0
    pipeline_stage_minutes: int = 2  # simulated clock cost of each post-detection analysis stage
    cohort: CohortConfig = Field(default_factory=CohortConfig)
    correlation: CorrelationConfig = Field(default_factory=CorrelationConfig)

    def fingerprint(self) -> str:
        return hashlib.sha256(json.dumps(self.model_dump(), sort_keys=True).encode()).hexdigest()[:12]
