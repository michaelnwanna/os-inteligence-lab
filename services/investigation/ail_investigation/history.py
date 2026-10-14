"""Historical incident retrieval and recurring-regression detection."""

from __future__ import annotations

import re
import sqlite3
import threading
from pathlib import Path

from ail_schemas import Incident

WEIGHTS = {"metric": 0.15, "cohort": 0.30, "signature": 0.35, "trigger": 0.20}
RECURRENCE_SCORE = 0.8


def signature_tokens(signature: str) -> set[str]:
    """'OOM_KILL|MemoryCache.Allocate' -> {'oom', 'kill', 'memorycache', 'allocate'}."""
    return {t for t in re.split(r"[^a-z0-9]+", signature.lower()) if len(t) > 1}


def _jaccard(a: set, b: set) -> float:
    return len(a & b) / len(a | b) if a | b else 0.0


def _signature_set(inc: Incident) -> set[str]:
    tokens: set[str] = set()
    for s in inc.signatures:
        if s["excess_share"] >= 0.2:
            tokens |= signature_tokens(s["signature"])
    return tokens


def _trigger(inc: Incident) -> tuple[str, str] | None:
    top = next((c for c in inc.correlations if not c.after_onset and c.score >= 0.5), None)
    return (top.change.kind, top.change.component) if top else None


def similarity(a: Incident, b: Incident) -> tuple[float, dict[str, float]]:
    ca = set(a.cohorts[0].dimensions.items()) if a.cohorts else set()
    cb = set(b.cohorts[0].dimensions.items()) if b.cohorts else set()
    parts = {
        "metric": 1.0 if a.metric == b.metric else 0.0,
        "cohort": _jaccard(ca, cb) if (ca or cb) else 1.0,
        "signature": _jaccard(_signature_set(a), _signature_set(b)),
        "trigger": 1.0 if _trigger(a) and _trigger(a) == _trigger(b) else 0.0,
    }
    return round(sum(WEIGHTS[k] * v for k, v in parts.items()), 4), parts


class IncidentArchive:
    """Incidents from earlier analyses, kept across runs and databases."""

    def __init__(self, path: str | Path = ":memory:") -> None:
        if str(path) != ":memory:":
            Path(path).parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(str(path), check_same_thread=False)
        self._lock = threading.Lock()
        self._conn.execute("CREATE TABLE IF NOT EXISTS archive (key TEXT PRIMARY KEY, data_fp TEXT, payload TEXT)")

    def add(self, incidents: list[Incident]) -> None:
        rows = []
        for i in incidents:
            fp = i.reproducibility.get("data_fingerprint", "")
            slim = i.model_copy(update={"investigation": None, "timemachine": None, "timeline": []})
            rows.append((f"{fp}:{i.id}", fp, slim.model_dump_json()))
        with self._lock:
            self._conn.executemany("INSERT OR REPLACE INTO archive VALUES (?, ?, ?)", rows)
            self._conn.commit()

    def all(self, exclude_data: str | None = None) -> list[Incident]:
        with self._lock:
            rows = self._conn.execute("SELECT payload FROM archive WHERE data_fp != ? ORDER BY key",
                                      (exclude_data or "",)).fetchall()
        return [Incident.model_validate_json(r[0]) for r in rows]

    def find_similar(self, inc: Incident, k: int = 3, min_score: float = 0.5) -> list[dict]:
        found = []
        for past in self.all(exclude_data=inc.reproducibility.get("data_fingerprint")):
            score, parts = similarity(inc, past)
            if score >= min_score:
                found.append({"id": past.id, "title": past.title, "score": score, "components": parts,
                              "onset": past.onset.isoformat(), "severity": past.severity,
                              "recurring": score >= RECURRENCE_SCORE,
                              "data_fingerprint": past.reproducibility.get("data_fingerprint")})
        return sorted(found, key=lambda f: f["score"], reverse=True)[:k]
