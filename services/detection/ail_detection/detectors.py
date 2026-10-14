"""Detector framework. Every detector implements `detect(ctx) -> list[AnomalyResult]`.

All detectors are causal: the statistic at minute t only uses data up to t, so the first alarm
time is a faithful detection latency. An alarm must persist `confirm_minutes` later to be kept,
which suppresses one-minute blips.
"""

from __future__ import annotations

import math
from abc import ABC, abstractmethod
from dataclasses import dataclass, field

import numpy as np
from ail_schemas import AnomalyResult

from ail_detection.config import DetectionConfig
from ail_detection.cube import Series
from ail_detection.stats import (
    Baseline,
    bucket_z,
    build_result,
    confirmed,
    estimate_onset,
    first_persistent,
    segment_llr,
    trailing_z,
)


@dataclass
class DetectionContext:
    series: dict[str, Series]
    baselines: dict[str, Baseline]
    cfg: DetectionConfig
    dims: dict[str, str] = field(default_factory=dict)


class Detector(ABC):
    name: str

    def detect(self, ctx: DetectionContext) -> list[AnomalyResult]:
        out = []
        for metric, series in ctx.series.items():
            res = self.detect_metric(metric, series, ctx.baselines[metric], ctx)
            if res is not None:
                out.append(res)
        return out

    @abstractmethod
    def detect_metric(
        self, metric: str, series: Series, base: Baseline, ctx: DetectionContext
    ) -> AnomalyResult | None: ...


class ZScoreDetector(Detector):
    """Trailing-window z-score against the baseline, with dispersion adjustment."""

    name = "ZScoreDetector"

    def detect_metric(self, metric, series, base, ctx):
        cfg = ctx.cfg
        z = trailing_z(series, base, cfg.window_minutes)
        above = z > cfg.z_threshold
        alarm = first_persistent(above, cfg.min_consecutive, base.end)
        if alarm is None or confirmed(above, alarm, cfg.confirm_minutes) is False:
            return None
        peak = z[min(alarm + cfg.confirm_minutes, series.T - 1)]
        onset = estimate_onset(series, base, base.end, alarm)
        return build_result(
            series, base, metric, self.name, alarm, onset, peak / cfg.z_threshold,
            {"z": round(float(peak), 2), "threshold": cfg.z_threshold, "window_minutes": cfg.window_minutes,
             "confirmed": confirmed(above, alarm, cfg.confirm_minutes)},
            cfg.confirm_minutes, ctx.dims,
        )


class EwmaCusumDetector(Detector):
    """CUSUM and EWMA control charts on per-minute standardized residuals."""

    name = "EwmaCusumDetector"

    def detect_metric(self, metric, series, base, ctx):
        cfg = ctx.cfg
        zb = bucket_z(series, base)
        T, b = series.T, base.end
        cusum, ewma, ewma_sd = np.zeros(T), np.zeros(T), np.ones(T)
        lam = cfg.ewma_lambda
        for i in range(b, T):
            cusum[i] = max(0.0, (cusum[i - 1] if i > b else 0.0) + zb[i] - cfg.cusum_k)
            ewma[i] = lam * zb[i] + (1 - lam) * (ewma[i - 1] if i > b else 0.0)
            ewma_sd[i] = math.sqrt(lam / (2 - lam) * (1 - (1 - lam) ** (2 * (i - b + 1))))
        c_ratio = cusum / cfg.cusum_h
        e_ratio = ewma / (cfg.ewma_limit * ewma_sd)
        strength = np.maximum(c_ratio, e_ratio)
        above = strength > 1.0
        alarm = first_persistent(above, 1, b)
        if alarm is None or confirmed(above, alarm, cfg.confirm_minutes) is False:
            return None
        zero = np.nonzero(cusum[b : alarm + 1] == 0)[0]
        onset = b + (int(zero[-1]) + 1 if len(zero) else 0)
        onset = min(onset, alarm)
        at = min(alarm + cfg.confirm_minutes, T - 1)
        return build_result(
            series, base, metric, self.name, alarm, onset, float(strength[at]),
            {"cusum": round(float(cusum[at]), 2), "cusum_h": cfg.cusum_h,
             "ewma": round(float(ewma[at]), 3), "ewma_limit": cfg.ewma_limit,
             "confirmed": confirmed(above, alarm, cfg.confirm_minutes)},
            cfg.confirm_minutes, ctx.dims,
        )


class ChangePointDetector(Detector):
    """Maximum-likelihood single change point in the post-baseline segment (causal)."""

    name = "ChangePointDetector"

    def detect_metric(self, metric, series, base, ctx):
        cfg = ctx.cfg
        T, b = series.T, base.end
        stat = np.zeros(T)
        tau_best = np.full(T, b)
        for i in range(b + 1, T):
            tau = np.arange(b, i)
            n, k, s, _ = series.wsum(tau, i + 1)
            llr = segment_llr(base, n, k, s)
            obs = (k / np.maximum(n, 1)) if base.kind == "rate" else (s / np.maximum(n, 1))
            llr = np.where(obs >= base.level * (1 + cfg.min_relative_change), llr, 0.0)
            j = int(np.argmax(llr))
            stat[i], tau_best[i] = llr[j], tau[j]
        above = stat > cfg.changepoint_llr
        alarm = first_persistent(above, 2, b)
        if alarm is None or confirmed(above, alarm, cfg.confirm_minutes) is False:
            return None
        at = min(alarm + cfg.confirm_minutes, T - 1)
        return build_result(
            series, base, metric, self.name, alarm, int(tau_best[at]), stat[at] / cfg.changepoint_llr,
            {"llr": round(float(stat[at]), 2), "threshold": cfg.changepoint_llr,
             "confirmed": confirmed(above, alarm, cfg.confirm_minutes)},
            cfg.confirm_minutes, ctx.dims,
        )


class DistributionShiftDetector(Detector):
    """Two-window comparison (baseline vs recent): two-proportion z for rates, Welch t for gauges."""

    name = "DistributionShiftDetector"

    def detect_metric(self, metric, series, base, ctx):
        cfg = ctx.cfg
        T, b = series.T, base.end
        n1, k1, s1, ss1 = (float(x) for x in series.wsum(0, b))
        idx = np.arange(T)
        n2, k2, s2, ss2 = series.wsum(np.maximum(idx - cfg.shift_window + 1, b), idx + 1)
        n2s = np.maximum(n2, 1.0)
        if base.kind == "rate":
            p1, p2 = k1 / n1, k2 / n2s
            p = (k1 + k2) / (n1 + n2s)
            z = (p2 - p1) / np.sqrt(np.maximum(p * (1 - p), 1e-12) * (1 / n1 + 1 / n2s))
            effect = np.where(p1 > 0, p2 / p1, 1.0)
        else:
            m1, m2 = s1 / n1, s2 / n2s
            v1 = (ss1 - s1 * s1 / n1) / (n1 - 1)
            v2 = np.maximum((ss2 - s2 * s2 / n2s) / np.maximum(n2 - 1, 1), 1e-12)
            z = (m2 - m1) / np.sqrt(v1 / n1 + v2 / n2s)
            effect = (m2 - m1) / math.sqrt(v1)
        z = np.where((n2 > 0) & (idx >= b), z / math.sqrt(base.phi), 0.0)
        above = z > cfg.shift_z
        alarm = first_persistent(above, cfg.min_consecutive, b)
        if alarm is None or confirmed(above, alarm, cfg.confirm_minutes) is False:
            return None
        at = min(alarm + cfg.confirm_minutes, T - 1)
        onset = estimate_onset(series, base, b, alarm)
        return build_result(
            series, base, metric, self.name, alarm, onset, z[at] / cfg.shift_z,
            {"z": round(float(z[at]), 2), "effect": round(float(effect[at]), 3),
             "effect_kind": "rate_ratio" if base.kind == "rate" else "cohens_d",
             "recent_window_minutes": cfg.shift_window, "confirmed": confirmed(above, alarm, cfg.confirm_minutes)},
            cfg.confirm_minutes, ctx.dims,
        )


class MLDetector(Detector):
    """Unsupervised multivariate detector: Mahalanobis distance of the joint (rate, latency, memory)
    deviation vector, with mean and covariance fitted on the baseline window."""

    name = "MLDetector"

    def detect(self, ctx: DetectionContext) -> list[AnomalyResult]:
        cfg = ctx.cfg
        metrics = list(ctx.series)
        if len(metrics) < 2:
            return []
        b = ctx.baselines[metrics[0]].end
        z = np.column_stack([trailing_z(ctx.series[m], ctx.baselines[m], cfg.window_minutes) for m in metrics])
        train = z[cfg.window_minutes - 1 : b]
        mu = train.mean(0)
        cov = np.cov(train.T) + 0.05 * np.eye(len(metrics))
        inv = np.linalg.inv(cov)
        d = z - mu
        d2 = np.einsum("ij,jk,ik->i", d, inv, d)
        above = (d2 > cfg.ml_threshold) & (z.max(1) > 0)
        above[:b] = False
        alarm = first_persistent(above, cfg.min_consecutive, b)
        if alarm is None or confirmed(above, alarm, cfg.confirm_minutes) is False:
            return []
        at = min(alarm + cfg.confirm_minutes, len(d2) - 1)
        dom = int(np.argmax(z[alarm]))
        metric = metrics[dom]
        series, base = ctx.series[metric], ctx.baselines[metric]
        onset = estimate_onset(series, base, b, alarm)
        return [build_result(
            series, base, metric, self.name, alarm, onset, float(d2[at]) / cfg.ml_threshold,
            {"mahalanobis_sq": round(float(d2[at]), 1), "threshold": cfg.ml_threshold,
             "component_z": {m: round(float(z[at, j]), 2) for j, m in enumerate(metrics)},
             "confirmed": confirmed(above, alarm, cfg.confirm_minutes)},
            cfg.confirm_minutes, ctx.dims,
        )]

    def detect_metric(self, metric, series, base, ctx):  # pragma: no cover - multivariate
        return None


DEFAULT_DETECTORS: list[Detector] = [
    ZScoreDetector(),
    EwmaCusumDetector(),
    ChangePointDetector(),
    DistributionShiftDetector(),
    MLDetector(),
]
