"""Change correlation: rank nearby changes as candidate triggers (evidence, not proof of causation)."""

from __future__ import annotations

import math

import numpy as np
from ail_schemas import ChangeEvent, CorrelationCandidate, bucket_of, hhmm

from ail_detection.cohort import CohortScanner
from ail_detection.config import CorrelationConfig
from ail_detection.cube import Cube


def correlate(
    cube: Cube,
    scanner: CohortScanner,
    cohort_mask: np.ndarray | None,
    onset_idx: int,
    end_idx: int,
    changes: list[ChangeEvent],
    cfg: CorrelationConfig,
) -> list[CorrelationCandidate]:
    """Score every change by temporal proximity to the onset and by how much of the anomaly's
    excess lies in the population the change touched."""
    nw, kw, sw, _ = cube.window(onset_idx, end_idx + 1)
    observed = kw if cube.kind == "rate" else sw
    excess = observed - nw * scanner.level
    in_cohort = cohort_mask if cohort_mask is not None else np.ones(cube.C, dtype=bool)
    onset_bucket = cube.t0 + onset_idx

    out: list[CorrelationCandidate] = []
    for ch in changes:
        delta = float(onset_bucket - bucket_of(ch.timestamp))
        if delta > cfg.lookback_minutes:
            continue
        touched = cube.mask({ch.dimension: ch.to_version}) if ch.dimension and ch.to_version else np.ones(cube.C, bool)
        inter = float(excess[in_cohort & touched].sum())
        union = float(excess[in_cohort | touched].sum())
        overlap = float(np.clip(inter / union, 0.0, 1.0)) if union > 0 and inter > 0 else 0.0
        after = delta < -cfg.onset_tolerance_minutes
        temporal = 0.0 if after else math.exp(-max(delta, 0.0) / cfg.tau_minutes)
        score = round(cfg.weight_temporal * temporal + cfg.weight_overlap * overlap, 4)
        when = hhmm(bucket_of(ch.timestamp))
        timing = (
            f"began {when}, {abs(delta):.0f} min after the anomaly onset, so it cannot be its trigger"
            if after else f"began {when}, {max(delta, 0):.0f} min before the anomaly onset"
        )
        out.append(CorrelationCandidate(
            change=ch, score=score, temporal_score=round(temporal, 4), overlap=round(overlap, 4),
            delta_minutes=delta, after_onset=after,
            rationale=f"{ch.component} {ch.from_version or ''}->{ch.to_version or ''} ({ch.kind}) {timing}; "
                      f"{overlap * 100:.0f}% overlap with the affected population.",
        ))
    out.sort(key=lambda c: c.score, reverse=True)
    return out
