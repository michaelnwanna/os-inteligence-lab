"""Minute-bucket helpers. A bucket is an integer count of minutes since the Unix epoch (UTC)."""

from __future__ import annotations

from datetime import datetime, timezone


def bucket_of(dt: datetime) -> int:
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return int(dt.timestamp() // 60)


def dt_of(bucket: int) -> datetime:
    return datetime.fromtimestamp(int(bucket) * 60, tz=timezone.utc)


def iso(bucket: int) -> str:
    return dt_of(bucket).strftime("%Y-%m-%dT%H:%M:%SZ")


def hhmm(bucket: int) -> str:
    return dt_of(bucket).strftime("%H:%M")
