# ADR 0003: Evidence before AI

- Status: accepted

## Context
The product's credibility depends on never presenting a model's invention as telemetry.

## Decision
1. All telemetry facts are computed by deterministic code and wrapped as numbered evidence items (`E1..`, `C1..`).
2. The model receives only those items and must return JSON: a summary, hypotheses with `supporting_evidence` ids, and recommended steps.
3. Output is validated before use: schema; every cited id exists; every number in the text appears in the evidence; every commit-hash-like token is a known commit; no certainty language; at most five hypotheses.
4. Failing validation falls back to the deterministic `rules` provider and records the violations in `guardrail`.
5. Reports and issues label facts and hypotheses separately. Correlation is always described as association.
6. Untrusted text (commit messages) is not sent to the model; evidence says what the diff analysis found, not what the author wrote.

## Consequences
- The model cannot add a number, time or commit the analysis did not produce.
- The check is syntactic: it cannot catch a wrong *interpretation* of a correct number. Hypotheses carry a confidence and an explicit "not verified" label for that reason.
- Providers are interchangeable; the offline provider makes the demo and tests deterministic.
