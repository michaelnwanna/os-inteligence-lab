# ADR 0002: SQLite and an in-memory stream first

- Status: accepted (supersedes the spec's "PostgreSQL + Redis" starting point for the first cut)

## Context
Phase 0 requires that the project builds locally with one command. PostgreSQL and Redis would add two services to that command, and the demo does not need them.

## Decision
- Storage: SQLite behind `TelemetryStore`. The only SQL lives in that class.
- Streaming: an `EventStream` protocol with an in-memory implementation and a worker thread.
- Aggregation: telemetry is rolled up on ingest to minute x cohort cells, so detectors never scan raw events.

## Consequences
- `make demo` has no external services.
- Single-node, single-writer throughput; the measured pipeline rate is in the benchmarks.
- Moving to PostgreSQL/ClickHouse/Redis Streams/Kafka means implementing the same small interfaces. The aggregate-row contract was chosen to map cleanly onto ClickHouse `SummingMergeTree`.
- The 10M-event and 100K events/s targets are not claimed.
