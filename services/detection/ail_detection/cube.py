"""Dense (time x cohort-cell) views of aggregated telemetry."""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np
from ail_schemas import DIMENSIONS


@dataclass
class Series:
    """Per-minute sums for a selection of cells. rate: (n, k); gauge: (n, s, ss)."""

    kind: str
    t0: int
    n: np.ndarray
    k: np.ndarray
    s: np.ndarray
    ss: np.ndarray
    _prefix: tuple[np.ndarray, ...] | None = field(default=None, repr=False)

    @property
    def T(self) -> int:
        return len(self.n)

    def prefix(self) -> tuple[np.ndarray, ...]:
        if self._prefix is None:
            z = np.zeros(1)
            self._prefix = tuple(np.concatenate([z, np.cumsum(a)]) for a in (self.n, self.k, self.s, self.ss))
        return self._prefix

    def wsum(self, i0, i1):
        """Sums over [i0, i1) for scalar or array indices."""
        pn, pk, ps, pss = self.prefix()
        i0 = np.maximum(i0, 0)
        return pn[i1] - pn[i0], pk[i1] - pk[i0], ps[i1] - ps[i0], pss[i1] - pss[i0]

    def value(self, i0: int, i1: int) -> float:
        n, k, s, _ = self.wsum(i0, i1)
        if n <= 0:
            return 0.0
        return float(k / n) if self.kind == "rate" else float(s / n)


@dataclass
class Cube:
    metric: str
    kind: str
    t0: int
    codes: np.ndarray  # (C, D) int codes into vocab
    vocab: dict[str, list[str]]
    N: np.ndarray  # (T, C)
    K: np.ndarray
    S: np.ndarray
    SS: np.ndarray
    _prefix: tuple[np.ndarray, ...] | None = field(default=None, repr=False)

    @property
    def T(self) -> int:
        return self.N.shape[0]

    @property
    def C(self) -> int:
        return self.N.shape[1]

    @staticmethod
    def kind_of(metric: str) -> str:
        if metric == "failure_rate":
            return "rate"
        return "count" if metric.startswith("sig:") else "gauge"

    @classmethod
    def from_rows(cls, metric: str, rows: list[tuple], t0: int | None = None, t1: int | None = None) -> Cube:
        """rows: (bucket, *dims, n, k, s, ss) as returned by TelemetryStore.agg_rows."""
        d = len(DIMENSIONS)
        if not rows:
            raise ValueError(f"no data for metric '{metric}'")
        lo = rows[0][0] if t0 is None else t0
        hi = max(r[0] for r in rows) if t1 is None else t1
        T = hi - lo + 1
        cell_ids: dict[tuple, int] = {}
        bi, ci, vals = [], [], []
        for r in rows:
            b = r[0] - lo
            if b < 0 or b >= T:
                continue
            key = r[1 : 1 + d]
            c = cell_ids.setdefault(key, len(cell_ids))
            bi.append(b)
            ci.append(c)
            vals.append(r[1 + d :])
        if not vals:
            raise ValueError(f"no data for metric '{metric}' in the requested window")
        C = len(cell_ids)
        arr = np.array(vals, dtype=float)
        shape = (T, C)
        out = [np.zeros(shape) for _ in range(4)]
        bi_a, ci_a = np.array(bi), np.array(ci)
        for j in range(4):
            np.add.at(out[j], (bi_a, ci_a), arr[:, j])
        vocab: dict[str, list[str]] = {dim: [] for dim in DIMENSIONS}
        codes = np.zeros((C, d), dtype=np.int32)
        for key, c in cell_ids.items():
            for j, dim in enumerate(DIMENSIONS):
                v = vocab[dim]
                if key[j] not in v:
                    v.append(key[j])
                codes[c, j] = v.index(key[j])
        return cls(metric, cls.kind_of(metric), lo, codes, vocab, *out)

    def mask(self, filters: dict[str, str | list[str]] | None) -> np.ndarray:
        m = np.ones(self.C, dtype=bool)
        for dim, want in (filters or {}).items():
            j = DIMENSIONS.index(dim)
            wanted = [want] if isinstance(want, str) else list(want)
            allowed = [i for i, v in enumerate(self.vocab[dim]) if v in wanted]
            m &= np.isin(self.codes[:, j], allowed)
        return m

    def series(self, mask: np.ndarray | None = None) -> Series:
        sel = slice(None) if mask is None else mask
        return Series(self.kind, self.t0, self.N[:, sel].sum(1), self.K[:, sel].sum(1),
                      self.S[:, sel].sum(1), self.SS[:, sel].sum(1))

    def _pre(self) -> tuple[np.ndarray, ...]:
        if self._prefix is None:
            z = np.zeros((1, self.C))
            self._prefix = tuple(np.vstack([z, np.cumsum(a, axis=0)]) for a in (self.N, self.K, self.S, self.SS))
        return self._prefix

    def window(self, i0: int, i1: int) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
        """Per-cell sums over [i0, i1)."""
        i0, i1 = max(i0, 0), min(i1, self.T)
        pn, pk, ps, pss = self._pre()
        return pn[i1] - pn[i0], pk[i1] - pk[i0], ps[i1] - ps[i0], pss[i1] - pss[i0]

    def cell_dims(self, c: int) -> dict[str, str]:
        return {dim: self.vocab[dim][int(self.codes[c, j])] for j, dim in enumerate(DIMENSIONS)}
