# ADR 0004: Expectation-based scan statistic for cohort discovery

- Status: accepted

## Context
The differentiating feature is finding the affected cohort without being told which dimensions to look at, including when the fleet-wide metric looks nearly healthy. Naive group-by breakdowns either miss small cohorts or flag many noisy ones, and a new version has no history of its own.

## Decision
Use an expectation-based Poisson scan statistic over all combinations of up to three dimensions, with:
- an empirical-Bayes baseline hierarchy so cells absent from the baseline inherit their hardware/OS peers' rate;
- a log-likelihood-ratio score with a per-dimension penalty, so `ARM64 + App 2.1` beats `ARM64 + App 2.1 + OS 12.4` when both fit;
- Bonferroni-adjusted significance over all candidates;
- scaling of expectations by the rest-of-population elevation, so a global regression reports no cohort;
- greedy "explain away" to list further cohorts.

## Consequences
- Cheap enough to run every minute (449 candidates over 144 cells on the demo fleet).
- Cohorts are limited to conjunctions of dimension values; interactions such as "x64 OR driver 7.14" are not found.
- The `application` dimension is constant in the demo and therefore skipped.
- Thresholds are in `CohortConfig` and are part of every incident's reproducibility record.
