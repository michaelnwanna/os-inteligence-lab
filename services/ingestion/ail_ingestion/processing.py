"""Validation, normalization, enrichment and aggregation of telemetry events."""

from __future__ import annotations

from dataclasses import dataclass

from ail_schemas import TelemetryEvent, bucket_of

ARCH_ALIASES = {
    "x86_64": "x64", "amd64": "x64", "x64": "x64",
    "aarch64": "arm64", "arm64": "arm64",
    "i386": "x86", "i686": "x86", "x86": "x86",
}


@dataclass(frozen=True)
class Normalized:
    event_id: str
    ts: int  # unix seconds
    bucket: int
    dims: tuple[str, ...]  # ordered as ail_schemas.DIMENSIONS
    kind: str  # "session" | "metric"
    failed: bool
    signature: str | None
    metric_name: str | None
    metric_value: float | None
    payload: str


def normalize(ev: TelemetryEvent) -> Normalized:
    arch = ARCH_ALIASES.get(ev.architecture.strip().lower(), ev.architecture.strip().lower())
    dims = (
        ev.os_version.strip(),
        arch,
        ev.device_model.strip(),
        ev.application.strip(),
        ev.application_version.strip(),
        (ev.driver_version or "none").strip(),
    )
    kind = "metric" if ev.event_type == "metric" else "session"
    signature = ev.failure_signature
    if ev.event_type == "crash" and not signature:
        signature = "UNKNOWN_CRASH"
    # Enrichment: a stable cohort tag makes raw events searchable by cohort.
    payload = ev.model_copy(
        update={"architecture": arch, "dimensions": {**ev.dimensions, "cohort": "|".join(dims)}}
    ).model_dump_json()
    return Normalized(
        event_id=ev.resolved_event_id(),
        ts=int(ev.timestamp.timestamp()),
        bucket=bucket_of(ev.timestamp),
        dims=dims,
        kind=kind,
        failed=signature is not None,
        signature=signature,
        metric_name=ev.metric_name,
        metric_value=ev.metric_value,
        payload=payload,
    )


def aggregate_rows(batch: list[Normalized]) -> list[tuple]:
    """Roll a batch up into minute/cohort aggregate rows: (bucket, metric, *dims, n, k, s, ss)."""
    acc: dict[tuple, list[float]] = {}

    def add(bucket: int, metric: str, dims: tuple[str, ...], n: float, k: float, s: float, ss: float):
        cell = acc.setdefault((bucket, metric, *dims), [0.0, 0.0, 0.0, 0.0])
        cell[0] += n
        cell[1] += k
        cell[2] += s
        cell[3] += ss

    for e in batch:
        if e.kind == "session":
            add(e.bucket, "failure_rate", e.dims, 1, 1.0 if e.failed else 0.0, 0, 0)
            if e.signature:
                add(e.bucket, f"sig:{e.signature}", e.dims, 1, 0, 0, 0)
        else:
            v = float(e.metric_value or 0.0)
            add(e.bucket, str(e.metric_name), e.dims, 1, 0, v, v * v)
    return [(*key, *vals) for key, vals in acc.items()]
