"""Cohort intelligence: an expectation-based scan statistic over multi-dimensional cohorts.

For every candidate cohort (combination of up to `max_depth` dimension values) the scanner compares
what was observed in the scan window with what the baseline predicts for the same cells. The
baseline prediction uses empirical-Bayes shrinkage down a hierarchy of coarser groupings, so a cell
that did not exist during the baseline (e.g. a freshly deployed version) is compared against the
hardware/OS cohort it belongs to. Candidates are ranked by a log-likelihood ratio, penalised for
extra dimensions, and the best cohort is "explained away" before the next one is searched.
"""

from __future__ import annotations

import math
from itertools import combinations

import numpy as np
from ail_schemas import DIMENSIONS, CohortFinding

from ail_detection.config import CohortConfig
from ail_detection.cube import Cube
from ail_detection.stats import severity_for

# Coarse -> fine; each level retains a superset of the previous level's dimensions.
HIERARCHY: list[tuple[str, ...]] = [
    ("architecture",),
    ("architecture", "device_model", "application"),
    ("os_version", "architecture", "device_model", "application"),
    ("os_version", "architecture", "device_model", "application", "driver_version"),
    DIMENSIONS,
]


class CohortScanner:
    def __init__(self, cube: Cube, baseline_end: int, cfg: CohortConfig):
        if cube.kind == "count":
            raise ValueError("cohort scanning needs a rate or gauge metric")
        self.cube, self.cfg, self.baseline_end = cube, cfg, baseline_end
        nb, kb, sb, ssb = cube.window(0, baseline_end)
        values = kb if cube.kind == "rate" else sb
        self.level = self._expected_level(nb, values)
        self.sigma2 = 1.0
        if cube.kind == "gauge":
            ok = nb > 1
            dof = max(float(nb[ok].sum() - ok.sum()), 1.0)
            self.sigma2 = max(float((ssb[ok] - sb[ok] ** 2 / nb[ok]).sum() / dof), 1e-12)

        dims = [j for j, d in enumerate(DIMENSIONS) if len(cube.vocab[d]) > 1]
        self.groups: list[tuple[tuple[int, ...], np.ndarray, np.ndarray]] = []
        for depth in range(1, cfg.max_depth + 1):
            for combo in combinations(dims, depth):
                uniq, inv = np.unique(cube.codes[:, list(combo)], axis=0, return_inverse=True)
                self.groups.append((combo, uniq, inv.reshape(-1)))
        self.n_candidates = max(sum(len(u) for _, u, _ in self.groups), 1)

    def _expected_level(self, nb: np.ndarray, vb: np.ndarray) -> np.ndarray:
        prior = self.cfg.prior_sessions_rate if self.cube.kind == "rate" else self.cfg.prior_sessions_gauge
        level = np.full(self.cube.C, vb.sum() / max(nb.sum(), 1.0))
        for dims in HIERARCHY:
            cols = [DIMENSIONS.index(d) for d in dims]
            _, inv = np.unique(self.cube.codes[:, cols], axis=0, return_inverse=True)
            inv = inv.reshape(-1)
            g = int(inv.max()) + 1
            parent = np.zeros(g)
            parent[inv] = level
            est = (np.bincount(inv, vb, g) + prior * parent) / (np.bincount(inv, nb, g) + prior)
            level = est[inv]
        return level

    def scan(self, i0: int, i1: int, top_k: int | None = None, exclude: np.ndarray | None = None,
             refine: bool = True) -> list[tuple[CohortFinding, np.ndarray]]:
        cube, cfg = self.cube, self.cfg
        nw, kw, sw, _ = cube.window(i0, i1)
        N = nw.copy()
        O = (kw if cube.kind == "rate" else sw).copy()
        E = nw * self.level
        total_n = float(N.sum())
        total_excess = float((O - E).sum())
        claimed = np.zeros(cube.C, dtype=bool) if exclude is None else exclude.copy()
        N[claimed] = O[claimed] = E[claimed] = 0.0
        full_masks: list[np.ndarray] = []
        out: list[tuple[CohortFinding, np.ndarray]] = []
        for rank in range(1, (top_k or cfg.top_k) + 1):
            Ot, Et = float(O.sum()), float(E.sum())
            best = None
            for combo, _, inv in self.groups:
                g = int(inv.max()) + 1
                Ng, Og, Eg = np.bincount(inv, N, g), np.bincount(inv, O, g), np.bincount(inv, E, g)
                # Judge each cohort against the rest of the population: if everything outside is
                # elevated too, the expectation is scaled up so only *excess* elevation counts.
                out_ratio = np.where(Et - Eg > 1e-9, (Ot - Og) / np.maximum(Et - Eg, 1e-9), 1.0)
                adj_e = np.maximum(Eg * np.maximum(out_ratio, 1.0), 1e-12)
                if cube.kind == "rate":
                    adj_e = adj_e * (1 + cfg.expectation_margin)
                ratio = Og / adj_e
                if cube.kind == "rate":
                    llr = Og * np.log(np.maximum(Og, 1e-12) / adj_e) - Og + adj_e
                    ok = (Og >= cfg.min_events) & (ratio >= cfg.min_ratio_rate)
                else:
                    llr = (Og - adj_e) ** 2 / (2 * self.sigma2 * np.maximum(Ng, 1.0))
                    ok = (Ng >= 200) & (ratio >= cfg.min_ratio_gauge)
                ok &= (Eg > 0) & (Og > adj_e)
                if not ok.any():
                    continue
                score = np.where(ok, llr - cfg.depth_penalty * (len(combo) - 1), -np.inf)
                j = int(np.argmax(score))
                if best is None or score[j] > best[0]:
                    best = (float(score[j]), float(llr[j]), combo, j, inv, float(Ng[j]), float(Og[j]), float(Eg[j]))
            if best is None:
                break
            _, llr_v, combo, j, inv, ng, og, eg = best
            p = min(1.0, self.n_candidates * 0.5 * math.erfc(math.sqrt(max(llr_v, 0.0))))
            if p > cfg.alpha:
                break
            mask = (inv == j) & ~claimed
            full_masks.append(inv == j)
            first = int(np.nonzero(mask)[0][0])
            dims = {DIMENSIONS[c]: cube.vocab[DIMENSIONS[c]][int(cube.codes[first, c])] for c in combo}
            observed, baseline = og / ng, eg / ng
            ratio_v = observed / baseline if baseline > 0 else float("inf")
            severity, _ = severity_for(ratio_v, cube.kind)
            out.append((
                CohortFinding(
                    metric=cube.metric, dimensions=dims, sessions=ng, baseline=baseline,
                    observed=observed, ratio=ratio_v, delta=observed - baseline, llr=llr_v, p_value=p,
                    severity=severity, population_share=ng / max(total_n, 1.0),
                    excess_share=float(np.clip((og - eg) / total_excess, 0.0, 1.0)) if total_excess > 0 else 0.0,
                    rank=rank,
                ),
                mask,
            ))
            N[mask] = O[mask] = E[mask] = 0.0
            claimed |= mask
        if refine and len(out) >= 2:
            # backfit: re-pick each cohort with the others' cells removed, so overlapping regressions
            # (e.g. a driver and an app version sharing some devices) are not blended into one cohort
            for i in range(len(out)):
                others = np.zeros(cube.C, dtype=bool)
                for j, m in enumerate(full_masks):
                    if j != i:
                        others |= m
                again = self.scan(i0, i1, top_k=1, exclude=others, refine=False)
                if again:
                    out[i] = (again[0][0].model_copy(update={"rank": out[i][0].rank}), again[0][1])
                    full_masks[i] = again[0][1]
        return out

    def evaluate(self, mask: np.ndarray, i0: int, i1: int) -> dict[str, float]:
        """Observed vs expected for an explicit cohort over [i0, i1)."""
        nw, kw, sw, _ = self.cube.window(i0, i1)
        n = float(nw[mask].sum())
        if n <= 0:
            return {"sessions": 0.0, "observed": 0.0, "baseline": 0.0, "ratio": 1.0, "llr": 0.0}
        o = float((kw if self.cube.kind == "rate" else sw)[mask].sum())
        e = float((nw * self.level)[mask].sum())
        if o <= e or e <= 0:
            llr = 0.0
        elif self.cube.kind == "rate":
            llr = o * math.log(o / e) - o + e
        else:
            llr = (o - e) ** 2 / (2 * self.sigma2 * n)
        return {"sessions": n, "observed": o / n, "baseline": e / n,
                "ratio": (o / e) if e > 0 else 1.0, "llr": llr}

    def onset(self, mask: np.ndarray, end: int, min_len: int = 2) -> int:
        """Change-point estimate of when the cohort's excess started (index in [baseline_end, end])."""
        cube = self.cube
        n_t = cube.N[:, mask].sum(1)
        e_t = (cube.N[:, mask] * self.level[mask]).sum(1)
        o_t = (cube.K if cube.kind == "rate" else cube.S)[:, mask].sum(1)
        z = np.zeros(1)
        pn, pe, po = (np.concatenate([z, np.cumsum(a)]) for a in (n_t, e_t, o_t))
        lo, hi = self.baseline_end, max(self.baseline_end, end - min_len + 1)
        tau = np.arange(lo, hi + 1)
        n, e, o = pn[end + 1] - pn[tau], pe[end + 1] - pe[tau], po[end + 1] - po[tau]
        if cube.kind == "rate":
            llr = np.where(o > e, o * np.log(np.maximum(o, 1e-12) / np.maximum(e, 1e-12)) - o + e, 0.0)
        else:
            llr = np.where(o > e, (o - e) ** 2 / (2 * self.sigma2 * np.maximum(n, 1.0)), 0.0)
        return int(tau[len(tau) - 1 - int(np.argmax(llr[::-1]))])

    def first_alarm(self, start: int | None = None, exclude: np.ndarray | None = None,
                    ) -> tuple[int, CohortFinding, np.ndarray] | None:
        """Causal scan: first minute at which the same significant cohort tops the scan on
        `consecutive` successive minutes."""
        cfg = self.cfg
        prev, run = None, 0
        for i in range(start if start is not None else self.baseline_end, self.cube.T):
            i0 = max(self.baseline_end, i - cfg.window_minutes + 1)
            res = self.scan(i0, i + 1, top_k=1, exclude=exclude)
            if not res:
                prev, run = None, 0
                continue
            finding, mask = res[0]
            key = tuple(sorted(finding.dimensions.items()))
            run = run + 1 if key == prev else 1
            prev = key
            if run >= cfg.consecutive:
                return i, finding, mask
        return None
