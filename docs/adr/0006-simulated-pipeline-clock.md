# ADR 0006: Simulated pipeline clock in the Failure Time Machine

- Status: accepted

## Context
The replay shows stages after detection (correlation, commit analysis, AI report). The analysis actually runs in seconds of wall time over data that has already been collected, so there are no honest wall-clock timestamps for those stages relative to the telemetry timeline.

## Decision
Stages that are properties of the data (deployment, exposure, onset, first alarm, confirmation, cohort isolation) use real timestamps from the data and the causal detectors. Post-detection analysis stages are placed at `anchor + n x pipeline_stage_minutes` (default 2) and are flagged `simulated_latency: true`; the portal labels them "simulated pipeline clock".

## Consequences
- The replay is a faithful ordering of what the system knew and when, without pretending the analysis took two minutes of telemetry time.
- Changing `pipeline_stage_minutes` changes only those labels.
