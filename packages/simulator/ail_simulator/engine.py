"""Reproducible synthetic population simulator.

Devices are assigned attributes once (seeded). Each simulated minute, rollouts move devices to new
versions, devices complete sessions, and injected effects change the failure rate, latency and
memory of matching cohorts. The simulator can emit minute/cohort aggregates directly (fast path for
100K+ devices) or individual `TelemetryEvent` records (for exercising the ingestion path).
"""

from __future__ import annotations

from collections.abc import Callable, Iterator
from dataclasses import dataclass, field
from datetime import timedelta

import numpy as np
from ail_schemas import DIMENSIONS, ChangeEvent, bucket_of

from ail_simulator.config import Effect, Rollout, Scenario


@dataclass
class Tick:
    minute: int
    bucket: int
    cell_of_device: np.ndarray
    sessions: np.ndarray
    failures: np.ndarray
    latency: tuple[np.ndarray, np.ndarray]  # per-cell (mean, sd) used for the minute
    memory: tuple[np.ndarray, np.ndarray]
    lat_stats: tuple[np.ndarray, np.ndarray]  # per-cell (sum, sumsq)
    mem_stats: tuple[np.ndarray, np.ndarray]
    sig_counts: dict[str, np.ndarray] = field(default_factory=dict)
    exposure: dict[str, float] = field(default_factory=dict)


def _norm(weights: dict[str, float]) -> tuple[list[str], np.ndarray]:
    names = list(weights)
    w = np.array([weights[k] for k in names], dtype=float)
    return names, w / w.sum()


def _lognormal_factor(rng: np.random.Generator, size: int, spread: float) -> np.ndarray:
    if spread <= 0:
        return np.ones(size)
    return np.exp(rng.normal(0.0, spread, size) - spread**2 / 2)


class Simulator:
    def __init__(self, scenario: Scenario, devices: int | None = None, seed: int | None = None):
        self.sc = scenario
        pop = scenario.population
        self.n = devices or pop.devices
        self.rng = np.random.default_rng(scenario.seed if seed is None else seed)
        rng = self.rng

        arch_names, arch_p = _norm(pop.architectures)
        self.model_names: list[str] = []
        self.model_arch: list[str] = []
        arch_offset, arch_count = [], []
        for a in arch_names:
            arch_offset.append(len(self.model_names))
            arch_count.append(len(pop.models[a]))
            for m in pop.models[a]:
                self.model_names.append(m)
                self.model_arch.append(a)

        def vocab(base: dict[str, float], dim: str) -> list[str]:
            names = list(base)
            for r in scenario.rollouts:
                if r.dimension == dim and r.to_version not in names:
                    names.append(r.to_version)
            return names

        self.os_names = vocab(pop.os_versions, "os_version")
        self.app_names = vocab(pop.app_versions, "application_version")
        self.drv_names = vocab(pop.drivers, "driver_version")

        def draw(base: dict[str, float], names: list[str]) -> np.ndarray:
            _, p = _norm({k: base.get(k, 0.0) for k in names})
            return rng.choice(len(names), self.n, p=p)

        arch_idx = rng.choice(len(arch_names), self.n, p=arch_p)
        within = (rng.random(self.n) * np.array(arch_count)[arch_idx]).astype(int)
        self.model_idx = np.array(arch_offset)[arch_idx] + within
        self.os0 = draw(pop.os_versions, self.os_names)
        self.app0 = draw(pop.app_versions, self.app_names)
        self.drv0 = draw(pop.drivers, self.drv_names)
        self.rollouts = sorted(scenario.rollouts, key=lambda r: r.at_minute)
        self.rollout_u = [rng.random(self.n) for _ in self.rollouts]

        m = len(self.model_names)
        self.shape = (len(self.os_names), m, len(self.app_names), len(self.drv_names))
        self.ncell = int(np.prod(self.shape))
        o, mi, a, d = np.unravel_index(np.arange(self.ncell), self.shape)
        self.cell_os, self.cell_model, self.cell_app, self.cell_drv = o, mi, a, d
        spread = scenario.baseline.model_spread
        self.model_fail = _lognormal_factor(rng, m, spread)
        self.model_lat = _lognormal_factor(rng, m, spread / 2)
        self.model_mem = _lognormal_factor(rng, m, spread / 2)

        arch_of_model = np.array(self.model_arch)
        self.cell_values: dict[str, np.ndarray] = {
            "os_version": np.array(self.os_names)[o],
            "architecture": arch_of_model[mi],
            "device_model": np.array(self.model_names)[mi],
            "application": np.full(self.ncell, pop.application),
            "application_version": np.array(self.app_names)[a],
            "driver_version": np.array(self.drv_names)[d],
        }
        self.effect_masks = {fx.id: self._mask(fx) for fx in scenario.effects}
        self._dims_cache: dict[int, tuple[str, ...]] = {}

    def _mask(self, fx: Effect) -> np.ndarray:
        mask = np.ones(self.ncell, dtype=bool)
        for dim, want in fx.where.items():
            if dim not in self.cell_values:
                raise ValueError(f"effect {fx.id}: unknown dimension '{dim}'")
            values = [want] if isinstance(want, str) else list(want)
            mask &= np.isin(self.cell_values[dim], values)
        return mask

    @staticmethod
    def exposure(r: Rollout, minute: int) -> float:
        if minute < r.at_minute:
            return 0.0
        if r.ramp_minutes <= 0:
            return r.max_exposure
        return r.max_exposure * min(1.0, (minute - r.at_minute) / r.ramp_minutes)

    @staticmethod
    def _alpha(fx: Effect, minute: int) -> float:
        if minute < fx.start_minute or (fx.end_minute is not None and minute >= fx.end_minute):
            return 0.0
        if fx.ramp_minutes <= 0:
            return 1.0
        return min(1.0, (minute - fx.start_minute + 1) / fx.ramp_minutes)

    def cell_dims(self, c: int) -> tuple[str, ...]:
        dims = self._dims_cache.get(c)
        if dims is None:
            dims = tuple(str(self.cell_values[d][c]) for d in DIMENSIONS)
            self._dims_cache[c] = dims
        return dims

    def _device_cells(self, minute: int) -> tuple[np.ndarray, dict[str, float]]:
        attrs = {"os_version": self.os0.copy(), "application_version": self.app0.copy(),
                 "driver_version": self.drv0.copy()}
        names = {"os_version": self.os_names, "application_version": self.app_names,
                 "driver_version": self.drv_names}
        exposures: dict[str, float] = {}
        for r, u in zip(self.rollouts, self.rollout_u, strict=True):
            e = self.exposure(r, minute)
            exposures[r.id] = e
            if e <= 0:
                continue
            arr = attrs[r.dimension]
            arr[(arr == names[r.dimension].index(r.from_version)) & (u < e)] = names[
                r.dimension
            ].index(r.to_version)
        shape = self.shape
        cell = ((attrs["os_version"] * shape[1] + self.model_idx) * shape[2]
                + attrs["application_version"]) * shape[3] + attrs["driver_version"]
        return cell, exposures

    def _gauge(self, n: np.ndarray, mean: np.ndarray, sd: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        """Sample the sum and sum-of-squares of `n` iid N(mean, sd) values without drawing each one."""
        rng = self.rng
        n1 = np.maximum(n, 1)
        mean_obs = mean + sd / np.sqrt(n1) * rng.standard_normal(self.ncell)
        dof = np.maximum(n - 1, 1)
        var_obs = sd**2 * rng.chisquare(dof) / dof
        total = n * mean_obs
        sumsq = np.maximum(n - 1, 0) * var_obs + n * mean_obs**2
        return total, sumsq

    def tick(self, minute: int) -> Tick:
        sc, rng, base = self.sc, self.rng, self.sc.baseline
        cell_of_device, exposures = self._device_cells(minute)
        devices = np.bincount(cell_of_device, minlength=self.ncell)

        volume = 1.0
        for tr in sc.traffic:
            if tr.start_minute <= minute < tr.end_minute:
                volume *= tr.multiplier
        sessions = rng.binomial(devices, min(1.0, base.active_session_rate * volume))

        rate = base.failure_rate * self.model_fail[self.cell_model]
        lat_mean = base.latency_ms.mean * self.model_lat[self.cell_model]
        mem_mean = base.memory_mb.mean * self.model_mem[self.cell_model]
        lat_scale = np.ones(self.ncell)
        mem_scale = np.ones(self.ncell)
        excess: list[tuple[Effect, np.ndarray]] = []
        for fx in sc.effects:
            a = self._alpha(fx, minute)
            if a <= 0:
                continue
            mask = self.effect_masks[fx.id]
            if fx.failure_rate is not None:
                # fx.failure_rate is the target for an average device; per-model differences are preserved
                factor = 1 + (fx.failure_rate / base.failure_rate - 1) * a
                new = np.where(mask, rate * factor, rate)
                excess.append((fx, np.where(mask, np.maximum(new - rate, 0.0), 0.0)))
                rate = new
            lat_scale = np.where(mask, lat_scale * (1 + (fx.latency_multiplier - 1) * a), lat_scale)
            mem_scale = np.where(mask, mem_scale * (1 + (fx.memory_multiplier - 1) * a), mem_scale)
        rate = np.clip(rate, 0.0, 1.0)
        failures = rng.binomial(sessions, rate)

        sig_counts: dict[str, np.ndarray] = {}
        remaining = failures.copy()
        rem_prob = np.ones(self.ncell)
        safe_rate = np.maximum(rate, 1e-12)
        for fx, ex in excess:
            if not fx.signature:
                continue
            share = ex / safe_rate
            take = rng.binomial(remaining, np.clip(share / np.maximum(rem_prob, 1e-12), 0, 1))
            remaining -= take
            rem_prob -= share
            sig_counts[fx.signature] = sig_counts.get(fx.signature, 0) + take
        names, probs = _norm(base.signatures)
        left = 1.0
        for i, (name, p) in enumerate(zip(names, probs, strict=True)):
            if i == len(names) - 1:
                take = remaining.copy()
            else:
                take = rng.binomial(remaining, min(1.0, p / left))
            remaining -= take
            left -= p
            sig_counts[name] = sig_counts.get(name, 0) + take

        lat = (lat_mean * lat_scale, base.latency_ms.std * lat_scale)
        mem = (mem_mean * mem_scale, base.memory_mb.std * mem_scale)
        return Tick(
            minute=minute,
            bucket=bucket_of(sc.start) + minute,
            cell_of_device=cell_of_device,
            sessions=sessions,
            failures=failures,
            latency=lat,
            memory=mem,
            lat_stats=self._gauge(sessions, *lat),
            mem_stats=self._gauge(sessions, *mem),
            sig_counts=sig_counts,
            exposure=exposures,
        )

    def ticks(self) -> Iterator[Tick]:
        for minute in range(self.sc.duration_minutes):
            yield self.tick(minute)

    def rows(self, t: Tick) -> list[tuple]:
        """Aggregate rows for one tick: (bucket, metric, *dims, n, k, s, ss)."""
        out: list[tuple] = []
        sessions = t.sessions.tolist()
        failures = t.failures.tolist()
        ls, lss = t.lat_stats[0].tolist(), t.lat_stats[1].tolist()
        ms, mss = t.mem_stats[0].tolist(), t.mem_stats[1].tolist()
        sigs = {k: v.tolist() for k, v in t.sig_counts.items()}
        for c in np.nonzero(t.sessions)[0].tolist():
            d, n = self.cell_dims(c), float(sessions[c])
            out.append((t.bucket, "failure_rate", *d, n, float(failures[c]), 0.0, 0.0))
            out.append((t.bucket, "latency_ms", *d, n, 0.0, ls[c], lss[c]))
            out.append((t.bucket, "memory_mb", *d, n, 0.0, ms[c], mss[c]))
            for name, arr in sigs.items():
                if arr[c] > 0:
                    out.append((t.bucket, f"sig:{name}", *d, float(arr[c]), 0.0, 0.0, 0.0))
        return out

    def change_events(self) -> list[ChangeEvent]:
        extra = [
            ChangeEvent(
                deployment_id=c.id,
                timestamp=self.sc.start + timedelta(minutes=c.at_minute),
                kind=c.kind,
                component=c.component,
                description=c.description or c.component,
            )
            for c in self.sc.changes
        ]
        return extra + [
            ChangeEvent(
                deployment_id=r.id,
                timestamp=self.sc.start + timedelta(minutes=r.at_minute),
                kind=r.kind,
                component=r.component,
                dimension=r.dimension,
                from_version=r.from_version,
                to_version=r.to_version,
                description=r.description or f"{r.component} {r.from_version} -> {r.to_version}",
                repository=r.repository,
                base_ref=r.base_ref,
                head_ref=r.head_ref,
                metadata={"ramp_minutes": r.ramp_minutes, "max_exposure": r.max_exposure},
            )
            for r in self.rollouts
        ]


def simulate_to_store(
    scenario: Scenario,
    store,
    devices: int | None = None,
    seed: int | None = None,
    progress: Callable[[int, int], None] | None = None,
) -> Simulator:
    """Fast path: write minute/cohort aggregates straight into the store."""
    sim = Simulator(scenario, devices, seed)
    for change in sim.change_events():
        store.add_change(change)
    for t in sim.ticks():
        store.add_aggregates(sim.rows(t))
        if progress:
            progress(t.minute + 1, scenario.duration_minutes)
    store.commit()
    store.set_meta(
        "simulation",
        {"scenario": scenario.id, "name": scenario.name, "devices": sim.n,
         "seed": scenario.seed if seed is None else seed, "config_hash": scenario.config_hash(),
         "start": scenario.start.isoformat(), "duration_minutes": scenario.duration_minutes,
         "application": scenario.population.application},
    )
    return sim
