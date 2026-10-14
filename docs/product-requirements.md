# Product requirements and engineering roadmap

This document restates the product requirements for OS Intelligence Lab and records what is implemented. The authoritative status table is in the [README](../README.md#roadmap-status).

## 1. Vision

An open-source, AI-powered observability and failure-intelligence platform for operating systems and the software on them. It collects telemetry, finds abnormal behaviour, isolates affected cohorts, correlates failures with changes, and uses an investigation agent to produce evidence-backed root-cause hypotheses. It answers: *what changed, who is affected, why is it happening, and what should an engineer investigate next?*

## 2. Principles

| Principle | Requirement | Where it shows up |
|---|---|---|
| Privacy first | No personal or sensitive data required. Demos use synthetic or explicitly approved local telemetry. | Schema forbids unknown fields; PII-like dimension keys rejected; [threat model](threat-model.md). |
| Evidence before AI | Deterministic analysis produces evidence; AI explains, correlates, recommends. | `ail_investigation.evidence`, `grounding`. |
| Cohort-aware | A 2% regression can be 80% of one cohort. | Cohort scan, Cohort Explorer. |
| Reproducible | Every anomaly can be regenerated from telemetry plus detection config. | Incident `reproducibility` block; `test_analysis_is_reproducible`. |
| Explainable | What, when, baseline, observed, cohorts, method, confidence/severity, evidence. | `AnomalyResult`, evidence items. |
| Modular | Collectors, detectors, storage, AI providers, visualisation replaceable. | [architecture](architecture/overview.md#replaceable-parts). |

## 3. Personas

Reliability engineer, developer checking a deployment, data/ML engineer experimenting with detectors, SRE automating health detection, researcher or student.

## 4. Functional requirements

| ID | Requirement | Status |
|---|---|---|
| FR1 | Reproducible synthetic population simulator with configurable devices, OS versions, architectures, apps, drivers, hardware cohorts, baseline rates, distributions, deployments and regressions. | Done: [scenarios](scenarios/). |
| FR2 | Ingestion APIs for events, metrics and deployments with batch support. | Done. Driver changes and application versions arrive as deployments/changes. |
| FR3 | Near-real-time processing: validation, normalization, enrichment, aggregation, cohort tagging, metric calculation; abstract enough for Kafka. | Done with an in-memory stream behind an interface. |
| Detection | Common detector interface; distribution shift, z-score, change point, EWMA/CUSUM, ML. | Done. |
| Cohorts | Automatic evaluation across OS version/build, architecture, CPU, model, application, version, driver. | Done for the six dimensions in the aggregate cube; OS build and CPU are carried on raw events but not aggregated. |
| Correlation | Search nearby deployments, driver/OS updates, config, dependency, infrastructure changes and commits; report correlation, not causation. | Done. |
| Agent | Answer what/when/who/what changed/evidence/hypotheses/next steps from structured evidence. | Done. |
| GitHub | Anomaly to deployment to release to commits to changed files to code analysis to hypothesis; separate facts from hypotheses. | Partial: see README. |
| Reports | Per-anomaly investigation page. | Done. |
| Time Machine | Replay with play, pause, accelerate, rewind, inspect. | Done. |
| Portal | Command Center, Anomaly Explorer, Cohort Explorer, Investigation Workspace. | Done. |

## 5. Non-functional requirements

| Area | Target | Status |
|---|---|---|
| Performance (initial) | 1,000 events/s locally, 100,000 simulated devices, 10 million stored events | 100,000 devices: demonstrated (3-hour scenario simulates and analyses in about 4 s). Ingestion pipeline: measured in [benchmarks](benchmarks/RESULTS.md). 10 million stored events: **not demonstrated**. |
| Performance (later) | 100K+ events/s, 1M+ devices | Not attempted. |
| Reliability | Tolerate malformed telemetry, retryable ingestion, no duplicate corruption, health, structured logs, graceful restart | Done. |
| Security | No committed secrets; token auth; least-privilege GitHub | Bearer token for writes; GitHub access is read-only unless issue creation is requested. OAuth not implemented. |
| Observability | Ingestion rate, processing latency, queue depth, detector latency/errors, AI latency/tokens, investigation duration | Done at `/metrics`. |

## 6. Phased roadmap and exit criteria

| Phase | Exit criterion | Evidence |
|---|---|---|
| 0 | Builds locally with one documented command | `make demo` |
| 1 | README users can launch a complete synthetic environment | `make demo`, `ail simulate` |
| 2 | Known injected regressions detected automatically with reproducible evidence | `tests/integration/test_scenarios.py` |
| 3 | Injected cohort identified without the user supplying dimensions | same; `ail evaluate` cohort accuracy |
| 4 | Injected deployment relationships surfaced and shown as correlation | same; correlation top-1/top-3 |
| 5 | Evidence-grounded report without invented telemetry facts | `tests/integration/test_investigation.py` |
| 6 | Deliberately introduced commit identified among several | `ail evaluate`: injected commit ranked first |
| 7 | Complete incident replayed from deployment through investigation | portal `#/timemachine`, `test_incident_endpoints` |
| 8 | Two operating systems send privacy-safe telemetry into the same schema | Agent built and run on macOS; Linux and Windows collectors are tested on fixture data only. Not yet demonstrated on two real systems. See [local-agent.md](architecture/local-agent.md). |
| 9 | Recurring-regression detection, clustering, signature similarity, history retrieval, ensembles, adaptive baselines, forecasting, dependency intelligence, natural-language query, experiment generation | Done: recurrence detection, signature similarity, history retrieval, natural-language query. Not started: clustering, ensembles, adaptive baselines, forecasting, dependency intelligence, experiment generation. |

## 7. Test strategy

The simulator is the test foundation. Scenarios 001 to 008 (no regression, global crash, ARM64-only, driver-specific, performance degradation, deployment-correlated, false-positive trap, gradual degradation) each declare an expected outcome; see [scenarios](scenarios/README.md). Tracked metrics: precision, recall, F1, detection latency, false-positive rate, cohort accuracy, correlation ranking accuracy, agent grounding and ingestion throughput.
