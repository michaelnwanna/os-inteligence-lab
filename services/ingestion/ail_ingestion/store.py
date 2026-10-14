"""SQLite telemetry store (raw events, minute aggregates, change events, incidents).

The store is the only place that knows about SQL. A PostgreSQL or ClickHouse implementation
only needs to provide the same public methods.
"""

from __future__ import annotations

import json
import sqlite3
import threading
from collections.abc import Iterable, Sequence
from pathlib import Path
from typing import Any

from ail_schemas import DIMENSIONS, ChangeEvent, Incident

_DIM_COLS = ", ".join(DIMENSIONS)
_AGG_COLS = len(DIMENSIONS) + 6  # bucket, metric, dims..., n, k, s, ss

_SCHEMA = f"""
CREATE TABLE IF NOT EXISTS events (
    event_id TEXT PRIMARY KEY,
    ts INTEGER NOT NULL,
    payload TEXT NOT NULL
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS events_ts ON events (ts);
CREATE TABLE IF NOT EXISTS agg (
    bucket INTEGER NOT NULL,
    metric TEXT NOT NULL,
    {" TEXT NOT NULL, ".join(DIMENSIONS)} TEXT NOT NULL,
    n REAL NOT NULL, k REAL NOT NULL, s REAL NOT NULL, ss REAL NOT NULL,
    PRIMARY KEY (bucket, metric, {_DIM_COLS})
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS agg_metric ON agg (metric, bucket);
CREATE TABLE IF NOT EXISTS changes (deployment_id TEXT PRIMARY KEY, ts INTEGER NOT NULL, payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS incidents (id TEXT PRIMARY KEY, seq INTEGER NOT NULL, payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
"""

_UPSERT = (
    f"INSERT INTO agg (bucket, metric, {_DIM_COLS}, n, k, s, ss) "
    f"VALUES ({','.join('?' * _AGG_COLS)}) "
    f"ON CONFLICT(bucket, metric, {_DIM_COLS}) DO UPDATE SET "
    "n = n + excluded.n, k = k + excluded.k, s = s + excluded.s, ss = ss + excluded.ss"
)


class TelemetryStore:
    def __init__(self, path: str | Path = ":memory:") -> None:
        self.path = str(path)
        if self.path != ":memory:":
            Path(self.path).parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(self.path, check_same_thread=False)
        self._lock = threading.RLock()
        with self._lock:
            if self.path != ":memory:":
                self._conn.execute("PRAGMA journal_mode=WAL")
                self._conn.execute("PRAGMA synchronous=NORMAL")
            self._conn.executescript(_SCHEMA)

    def close(self) -> None:
        with self._lock:
            self._conn.close()

    # --- raw events -------------------------------------------------------------------------
    def insert_raw(self, event_id: str, ts: int, payload: str) -> bool:
        """Insert a raw event; returns False when this event_id was already stored."""
        with self._lock:
            cur = self._conn.execute(
                "INSERT OR IGNORE INTO events (event_id, ts, payload) VALUES (?, ?, ?)",
                (event_id, ts, payload),
            )
            return cur.rowcount == 1

    # --- aggregates -------------------------------------------------------------------------
    def add_aggregates(self, rows: Iterable[Sequence[Any]]) -> None:
        with self._lock:
            self._conn.executemany(_UPSERT, rows)

    def commit(self) -> None:
        with self._lock:
            self._conn.commit()

    def agg_rows(self, metric: str) -> list[tuple]:
        """Rows of (bucket, *dimensions, n, k, s, ss) for one metric, ordered by bucket."""
        with self._lock:
            cur = self._conn.execute(
                f"SELECT bucket, {_DIM_COLS}, n, k, s, ss FROM agg WHERE metric = ? ORDER BY bucket",
                (metric,),
            )
            return cur.fetchall()

    def metrics(self) -> list[str]:
        with self._lock:
            return [r[0] for r in self._conn.execute("SELECT DISTINCT metric FROM agg ORDER BY 1")]

    def time_range(self) -> tuple[int, int] | None:
        with self._lock:
            lo, hi = self._conn.execute("SELECT MIN(bucket), MAX(bucket) FROM agg").fetchone()
        return None if lo is None else (int(lo), int(hi))

    # --- change events ----------------------------------------------------------------------
    def add_change(self, change: ChangeEvent) -> None:
        with self._lock:
            self._conn.execute(
                "INSERT OR REPLACE INTO changes (deployment_id, ts, payload) VALUES (?, ?, ?)",
                (change.deployment_id, int(change.timestamp.timestamp()), change.model_dump_json()),
            )
            self._conn.commit()

    def list_changes(self) -> list[ChangeEvent]:
        with self._lock:
            rows = self._conn.execute("SELECT payload FROM changes ORDER BY ts").fetchall()
        return [ChangeEvent.model_validate_json(r[0]) for r in rows]

    # --- incidents --------------------------------------------------------------------------
    def replace_incidents(self, incidents: Iterable[Incident]) -> None:
        with self._lock:
            self._conn.execute("DELETE FROM incidents")
            self._conn.executemany(
                "INSERT INTO incidents (id, seq, payload) VALUES (?, ?, ?)",
                [(i.id, n, i.model_dump_json()) for n, i in enumerate(incidents)],
            )
            self._conn.commit()

    def get_incident(self, incident_id: str) -> Incident | None:
        with self._lock:
            row = self._conn.execute(
                "SELECT payload FROM incidents WHERE id = ?", (incident_id,)
            ).fetchone()
        return Incident.model_validate_json(row[0]) if row else None

    def list_incidents(self) -> list[Incident]:
        with self._lock:
            rows = self._conn.execute("SELECT payload FROM incidents ORDER BY seq").fetchall()
        return [Incident.model_validate_json(r[0]) for r in rows]

    # --- metadata and stats -----------------------------------------------------------------
    def set_meta(self, key: str, value: Any) -> None:
        with self._lock:
            self._conn.execute(
                "INSERT OR REPLACE INTO meta (key, value) VALUES (?, ?)", (key, json.dumps(value))
            )
            self._conn.commit()

    def get_meta(self, key: str, default: Any = None) -> Any:
        with self._lock:
            row = self._conn.execute("SELECT value FROM meta WHERE key = ?", (key,)).fetchone()
        return json.loads(row[0]) if row else default

    def counts(self) -> dict[str, int]:
        with self._lock:
            q = self._conn.execute
            return {
                "raw_events": q("SELECT COUNT(*) FROM events").fetchone()[0],
                "aggregate_rows": q("SELECT COUNT(*) FROM agg").fetchone()[0],
                "changes": q("SELECT COUNT(*) FROM changes").fetchone()[0],
                "incidents": q("SELECT COUNT(*) FROM incidents").fetchone()[0],
            }

    def data_fingerprint(self) -> str:
        """Order-independent hash of all aggregates, used to prove analyses are reproducible."""
        import hashlib

        h = hashlib.sha256()
        with self._lock:
            cur = self._conn.execute(
                f"SELECT bucket, metric, {_DIM_COLS}, n, k, s, ss FROM agg ORDER BY bucket, metric, {_DIM_COLS}"
            )
            for row in cur:
                h.update(repr((*row[:-2], round(row[-2], 6), round(row[-1], 6))).encode())
        return h.hexdigest()[:16]
