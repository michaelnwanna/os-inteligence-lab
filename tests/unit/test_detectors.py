from __future__ import annotations

import numpy as np
import pytest
from ail_detection import DEFAULT_DETECTORS, DetectionConfig, DetectionContext
from ail_detection.cube import Series
from ail_detection.stats import fit_baseline, segment_llr
from ail_schemas import AnomalyResult

BASE_END = 60
T = 160
STEP_AT = 100
NAMES = [d.name for d in DEFAULT_DETECTORS]


def _rate_series(seed: int, step: float | None) -> Series:
    rng = np.random.default_rng(seed)
    n = np.full(T, 5000.0)
    p = np.full(T, 0.004)
    if step is not None:
        p[STEP_AT:] = 0.004 * step
    k = rng.binomial(5000, p).astype(float)
    z = np.zeros(T)
    return Series("rate", 0, n, k, z, z)


def _gauge_series(seed: int, shift: float | None) -> Series:
    rng = np.random.default_rng(seed)
    n = np.full(T, 5000.0)
    mean = np.full(T, 400.0)
    if shift is not None:
        mean[STEP_AT:] *= shift
    sd = 90.0
    total = n * (mean + sd / np.sqrt(n) * rng.standard_normal(T))
    sumsq = (n - 1) * sd**2 * rng.chisquare(n - 1) / (n - 1) + total**2 / n
    return Series("gauge", 0, n, np.zeros(T), total, sumsq)


def _run(series_map: dict[str, Series]) -> list[AnomalyResult]:
    cfg = DetectionConfig(baseline_minutes=BASE_END)
    bases = {m: fit_baseline(s, BASE_END) for m, s in series_map.items()}
    ctx = DetectionContext(series_map, bases, cfg)
    return [r for d in DEFAULT_DETECTORS for r in d.detect(ctx)]


def test_every_detector_fires_on_a_regression_and_reports_a_standard_result():
    results = _run({"failure_rate": _rate_series(1, 3.0), "latency_ms": _gauge_series(1, None),
                    "memory_mb": _gauge_series(2, 1.4)})
    fired = {r.detector for r in results}
    assert fired == set(NAMES)
    for r in results:
        assert STEP_AT - 3 <= r.timestamp.timestamp() / 60 <= STEP_AT + 20
        assert r.onset <= r.timestamp
        assert r.delta == pytest.approx(r.observed_value - r.baseline)
        assert 0 < r.confidence < 1 and r.severity in {"low", "moderate", "high", "critical"}
        assert r.evidence["baseline_sessions"] > 0


@pytest.mark.parametrize("seed", range(6))
def test_no_detector_fires_on_stationary_data(seed):
    results = _run({"failure_rate": _rate_series(seed, None), "latency_ms": _gauge_series(seed, None),
                    "memory_mb": _gauge_series(seed + 50, None)})
    assert results == []


def test_one_minute_blip_is_not_confirmed():
    s = _rate_series(3, None)
    s.k[120] += 20  # one minute at 2x the baseline rate
    s._prefix = None
    results = _run({"failure_rate": s, "latency_ms": _gauge_series(3, None), "memory_mb": _gauge_series(4, None)})
    assert results == []


def test_gradual_ramp_is_caught_by_sequential_statistics():
    rng = np.random.default_rng(5)
    p = np.full(T, 0.004)
    p[STEP_AT:] = 0.004 * (1 + np.linspace(0, 3, T - STEP_AT))
    k = rng.binomial(5000, p).astype(float)
    z = np.zeros(T)
    s = Series("rate", 0, np.full(T, 5000.0), k, z, z)
    results = _run({"failure_rate": s, "latency_ms": _gauge_series(5, None)})
    assert {"EwmaCusumDetector", "ChangePointDetector"} <= {r.detector for r in results}


def test_detectors_are_causal():
    """Truncating the data after the alarm must not move the alarm time."""
    full = _rate_series(7, 3.0)
    cut = Series("rate", 0, full.n[:130], full.k[:130], full.s[:130], full.ss[:130])
    cfg = DetectionConfig(baseline_minutes=BASE_END)
    for det in DEFAULT_DETECTORS:
        if det.name == "MLDetector":
            continue
        a = det.detect(DetectionContext({"failure_rate": full}, {"failure_rate": fit_baseline(full, BASE_END)}, cfg))
        b = det.detect(DetectionContext({"failure_rate": cut}, {"failure_rate": fit_baseline(cut, BASE_END)}, cfg))
        assert a and b and a[0].timestamp == b[0].timestamp, det.name


def test_segment_llr_is_zero_for_downward_shifts():
    s = _rate_series(1, None)
    base = fit_baseline(s, BASE_END)
    assert float(segment_llr(base, np.array([5000.0]), np.array([1.0]), np.array([0.0]))[0]) == 0.0
