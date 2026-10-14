# Telemetry schema

Defined in [packages/schemas/ail_schemas/telemetry.py](../../packages/schemas/ail_schemas/telemetry.py); JSON Schemas are generated into [packages/schemas/json](../../packages/schemas/json) by `python packages/schemas/export_schemas.py`.

## TelemetryEvent

| Field | Type | Notes |
|---|---|---|
| `event_id` | string, optional | Idempotency key. If omitted, a hash of timestamp, device, session, type and metric name is used. |
| `timestamp` | datetime | Naive values are treated as UTC. |
| `device_id` | string | Pseudonymous id. Do not send hardware serials, hostnames or account ids. |
| `session_id` | string | |
| `os`, `os_version`, `os_build` | string | `os_build` optional. |
| `architecture` | string | Aliases (`x86_64`, `amd64`, `aarch64`, ...) are normalised to `x64`, `arm64`, `x86`. |
| `device_model`, `cpu` | string | |
| `memory_gb` | number | Installed device memory (the spec's `memory`). Not a usage metric. |
| `application`, `application_version` | string | |
| `driver`, `driver_version` | string, optional | Missing driver version is stored as `none`. |
| `event_type` | `session` \| `crash` \| `metric` | See below. |
| `metric_name`, `metric_value` | string, number | Required for `metric`; value must be finite. |
| `failure_signature` | string | Stable signature, e.g. `OOM_KILL\|MemoryCache.Allocate`. A set signature marks a failed session. |
| `deployment_id` | string, optional | Link to a change. |
| `dimensions` | map string to string | Up to 16 custom dimensions. Keys that look like personal data (`email`, `user`, `ip`, `phone`, `address`, `name`, `hostname`, `mac`, ...) are rejected. |

Unknown fields are rejected, so personal data cannot slip in unnoticed.

Event types:

- `session`: one completed session; counts toward the failure-rate denominator. With a `failure_signature` it is a failed session.
- `crash`: alias for a failed session; stored as `session` with the signature (or `UNKNOWN_CRASH`).
- `metric`: one gauge sample such as `latency_ms` or `memory_mb`.

## ChangeEvent

`deployment_id`, `timestamp`, `kind` (`app_deployment`, `driver_update`, `os_update`, `config_change`, `dependency_update`, `infrastructure_change`, `git_commit`), `component`, optional `dimension` (the telemetry dimension that gains `to_version`), `from_version`, `to_version`, `description`, `repository`, `base_ref`, `head_ref`, `metadata`.

`dimension` + `to_version` is what lets the correlation engine work out which cohort cells a change touched. Changes without a dimension (feature flags, infrastructure) are treated as touching the whole population.

## Events from the local agent

The [local agent](local-agent.md) uses `application = "ail-agent"`, `device_model = "generic-<arch>"`, and sends `session` heartbeats plus `metric` events named `cpu_load_pct` and `memory_mb`. They go through the same endpoints and cohort cube as simulated data; `cpu_load_pct` is stored but not yet monitored by the detectors (the default monitored metrics are `failure_rate`, `latency_ms`, `memory_mb`).

## Endpoints

| Endpoint | Body | Response |
|---|---|---|
| `POST /api/v1/events` | event, array, or `{"events": [...]}` | `202 {accepted, rejected: [{index, error}]}` |
| `POST /api/v1/metrics` | same, `metric` events only | same |
| `POST /api/v1/deployments` | change, array, or `{"deployments": [...]}` | same |

Limits: 10 MB per request. `503` + `Retry-After` when the stream is full. `401` when `AIL_API_TOKEN` is set and the bearer token is missing or wrong.

## Aggregate rows

`(bucket, metric, os_version, architecture, device_model, application, application_version, driver_version, n, k, s, ss)` with the bucket in minutes since the Unix epoch. See [overview.md](overview.md).
