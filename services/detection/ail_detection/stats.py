"""Statistical building blocks shared by the detectors."""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np
from ail_schemas import SEVERITY_ORDER, AnomalyResult, Severity, dt_of

from ail_detection.cube import Series

# (critical, high, moderate) observed/baseline ratios
_RATIO_THRESHOLDS = {"rate": (5.0, 2.5, 1.25), "gauge": (2.0, 1.5, 1.2)}


def severity_for(ratio: float, kind: str) -> tuple[Severity, float]:
    crit, high, mod = _RATIO_THRESHOLDS["rate" if kind in ("rate", "count") else "gauge"]
    score = 0.0 if ratio <= 1 else min(1.0, math.log(ratio) / math.log(crit))
    if ratio >= crit:
        return "critical", score
    if ratio >= high:
        return "high", score
    if ratio >= mod:
        return "moderate", score
    return "low", score


def max_severity(*levels: str) -> Severity:
    return max(levels, key=lambda s: SEVERITY_ORDER[s])  # type: ignore[return-value]


def confidence_from_strength(strength: float) -> float:
    """Saturating map of (statistic / threshold): 1.0 -> 0.50, 2.0 -> 0.80, 3.0 -> 0.90."""
    s = max(strength, 0.0)
    return round(min(0.99, s * s / (1.0 + s * s)), 4)


@dataclass
class Baseline:
    kind: str
    level: float  # p0 for rates, mean for gauges
    sigma: float  # session-level standard deviation (gauges)
    phi: float  # over-dispersion factor estimated on the baseline window
    n: float
    end: int  # first index after the baseline window


def fit_baseline(series: Series, end: int) -> Baseline:
    n, k, s, ss = series.wsum(0, end)
    n, k, s, ss = float(n), float(k), float(s), float(ss)
    nt, kt, st = series.n[:end], series.k[:end], series.s[:end]
    ok = nt > 0
    if series.kind == "rate":
        p0 = max(k / n, 1e-6)
        z = (kt[ok] - nt[ok] * p0) / np.sqrt(nt[ok] * p0 * (1 - p0))
        level, sigma = p0, 0.0
    else:
        level = s / n
        sigma = math.sqrt(max((ss - s * s / n) / max(n - 1, 1), 1e-12))
        z = (st[ok] / nt[ok] - level) * np.sqrt(nt[ok]) / sigma
    phi = float(min(max(np.var(z, ddof=1) if len(z) > 2 else 1.0, 1.0), 25.0))
    return Baseline(series.kind, level, sigma, phi, n, end)


def window_z(series: Series, base: Baseline, i0, i1):
    """Dispersion-adjusted z-score of the mean/rate over [i0, i1) against the baseline."""
    n, k, s, _ = series.wsum(i0, i1)
    n = np.asarray(n, dtype=float)
    safe = np.maximum(n, 1.0)
    if base.kind == "rate":
        p0 = base.level
        z = (k - n * p0) / np.sqrt(safe * p0 * (1 - p0))
    else:
        z = (s / safe - base.level) * np.sqrt(safe) / base.sigma
    return np.where(n > 0, z, 0.0) / math.sqrt(base.phi)


def bucket_z(series: Series, base: Baseline) -> np.ndarray:
    idx = np.arange(series.T)
    return window_z(series, base, idx, idx + 1)


def trailing_z(series: Series, base: Baseline, width: int) -> np.ndarray:
    idx = np.arange(series.T)
    return window_z(series, base, np.maximum(idx - width + 1, 0), idx + 1)


def segment_llr(base: Baseline, n, k, s):
    """Log-likelihood ratio of 'segment differs (upward)' vs 'segment equals baseline'."""
    n = np.asarray(n, dtype=float)
    safe = np.maximum(n, 1.0)
    if base.kind == "rate":
        p0 = base.level
        r = np.clip(k / safe, 1e-12, 1 - 1e-12)
        llr = k * np.log(r / p0) + (n - k) * np.log((1 - r) / (1 - p0))
        up = r > p0
    else:
        m = s / safe
        llr = safe * (m - base.level) ** 2 / (2 * base.sigma**2)
        up = m > base.level
    return np.where(up & (n > 0), llr, 0.0) / base.phi


def estimate_onset(series: Series, base: Baseline, start: int, end: int, min_len: int = 2) -> int:
    """Index in [start, end] maximising the change-point LLR of the segment [tau, end]."""
    last = max(start, end - min_len + 1)
    tau = np.arange(start, last + 1)
    n, k, s, _ = series.wsum(tau, end + 1)
    llr = segment_llr(base, n, k, s)
    return int(tau[len(tau) - 1 - int(np.argmax(llr[::-1]))])


def first_persistent(above: np.ndarray, need: int, start: int) -> int | None:
    """Index at which `need` consecutive True values (at or after `start`) are first completed."""
    run = 0
    for i in range(start, len(above)):
        run = run + 1 if above[i] else 0
        if run >= need:
            return i
    return None


def confirmed(above: np.ndarray, alarm: int, confirm: int) -> bool | None:
    """Whether the alarm statistic stays above threshold `confirm` minutes later; None if not yet known."""
    if alarm + confirm >= len(above):
        return None
    return bool(above[alarm + confirm])


def build_result(
    series: Series,
    base: Baseline,
    metric: str,
    detector: str,
    alarm: int,
    onset: int,
    strength: float,
    evidence: dict,
    confirm: int,
    dims: dict[str, str] | None = None,
    affected: float = 1.0,
) -> AnomalyResult:
    end = min(alarm + confirm, series.T - 1)
    observed = series.value(onset, end + 1)
    ratio = observed / base.level if base.level else 1.0
    severity, score = severity_for(ratio, series.kind)
    return AnomalyResult(
        metric=metric,
        timestamp=dt_of(series.t0 + alarm),
        onset=dt_of(series.t0 + onset),
        baseline=float(base.level),
        observed_value=float(observed),
        delta=float(observed - base.level),
        severity=severity,
        severity_score=round(score, 4),
        confidence=confidence_from_strength(strength),
        affected_population=float(affected),
        detector=detector,
        dimensions=dims or {},
        evidence={
            "alarm_minute_index": int(alarm),
            "onset_minute_index": int(onset),
            "strength": round(float(strength), 3),
            "ratio": round(float(ratio), 4),
            "baseline_sessions": float(base.n),
            "dispersion_phi": round(base.phi, 3),
            **evidence,
        },
    )
