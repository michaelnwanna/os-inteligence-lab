# Detection methods

## Baseline and dispersion

For the first 60 minutes the engine fits a baseline: `p0` (failure rate) or `mu0, sigma0` (gauges). It also estimates an over-dispersion factor `phi` as the variance of per-minute standardized residuals in the baseline (clamped to 1..25). Every z-score below is divided by `sqrt(phi)`, so noisier-than-binomial fleets do not generate false alarms.

Rate residual: `z = (k - n p0) / sqrt(n p0 (1 - p0))`. Gauge residual: `z = (mean - mu0) sqrt(n) / sigma0`.

## Detectors

| Detector | Statistic | Alarm | Onset estimate |
|---|---|---|---|
| `ZScoreDetector` | trailing 5-minute window z | z > 5 for 3 consecutive minutes | change-point refinement |
| `EwmaCusumDetector` | one-sided CUSUM (`k=0.5`, `h=8`) and EWMA (`lambda=0.2`, `L=4.5`) on per-minute z | either chart crosses | last CUSUM zero |
| `ChangePointDetector` | max over split points of the LLR of "segment differs from baseline" | LLR > 15 and relative change >= 10% | arg-max split |
| `DistributionShiftDetector` | baseline vs trailing 10 minutes: two-proportion z / Welch t | z > 5 for 3 minutes | change-point refinement |
| `MLDetector` | Mahalanobis distance of the (rate, latency, memory) z-vector, mean/covariance fitted on the baseline | d² > 40 for 3 minutes, at least one component positive | change-point refinement |
| `CohortScanDetector` | scan statistic, below | same cohort tops the scan on 2 consecutive minutes | change point of the cohort's excess |

Only deterioration (increases) alerts. Every alarm must still hold `confirm_minutes` (2) later, which discards blips; alarms too close to the end of data to confirm are kept and flagged `confirmed: null`.

`AnomalyResult.confidence` is `s² / (1 + s²)` where `s` is the statistic over its threshold at confirmation (1x gives 0.50, 2x gives 0.80). It is a saturating strength score, not a calibrated probability.

Severity is a deterministic function of observed/baseline: rates critical >= 5x, high >= 2.5x, moderate >= 1.25x; gauges critical >= 2x, high >= 1.5x, moderate >= 1.2x. An incident takes the maximum of its cohort and fleet-wide severities.

## Cohort scan

Candidates are all value combinations over up to three dimensions that actually vary in the data (449 on the demo fleet, which has 144 cohort cells).

**Expected value per cell.** Starting from the fleet-wide baseline, estimates are refined down a hierarchy (architecture, then architecture+model+app, then +OS, then +driver, then all dimensions) with shrinkage `(K + m * parent) / (N + m)`, `m = 20,000` sessions for rates. A cell that did not exist in the baseline (a new app version) inherits the estimate of the hardware/OS group it belongs to, so a brand-new version is compared with the right peers.

**Statistic.** For a cohort with observed failures `O` and expected `E`, the Poisson log-likelihood ratio `O ln(O/E) - O + E` (gauges: `(O - E)^2 / (2 sigma^2 n)`). `E` is first scaled by the excess elevation of the *rest* of the population, so a global regression does not make a random slice look special.

**Selection.** Score `LLR - 2 (depth - 1)`: an extra dimension must pay for itself. Guards: at least 15 failures and a ratio of at least 1.5 (gauges 1.1). Rate expectations are estimates, so they are inflated by `expectation_margin` (25%) before the test; a cohort that is only marginally above a noisy expectation is not reported. The best candidate must have a Bonferroni-adjusted p < 0.001 (`p = N_candidates * 0.5 * erfc(sqrt(LLR))`). The cohort is then removed ("explained away") and the scan repeats for up to three cohorts.

**Backfitting.** When two regressions overlap in cell space (say a driver and an app version that share some devices), greedy selection first picks the blend ("driver 7.14"). After the greedy pass each cohort is re-picked with the full membership of the others removed, which turns the blend into `x64 + Driver 7.14` and `ARM64 + App 2.1`. A second cohort that keeps at least 20% of the total excess and is not nested with the first becomes its own incident, and each incident's correlations ignore cells explained by the other.

## Correlation

For every recorded change: `temporal = exp(-max(onset - change_time, 0) / 60 min)`, and zero if the change began more than 3 minutes after the onset. `overlap` is the share of the excess (observed minus expected) in the union of the anomalous cohort and the population touched by the change that lies in their intersection. `score = 0.4 temporal + 0.6 overlap`. This ranks candidates; it does not establish causation, and every surface says so.

## Why not plain thresholds?

A threshold on the fleet-wide failure rate misses the flagship regression (0.38% to 0.67%) until it is far too late, and fires on traffic surges that change volume but not the rate. Working on rates with per-cell expectations, dispersion adjustment, persistence and cohort scans addresses both.

## Recovery and later episodes

An incident is **resolved** when its alarm cohort (or the whole fleet for a global incident) shows no significant excess (LLR < 2, i.e. z < 2) over a clean `recovery_minutes` (15) window. Its statistics are then measured from onset to the start of that window, so a hotfixed regression is not diluted by the healthy tail. If every incident in a pass is resolved, the engine starts a new pass on the data after `rebaseline_gap_minutes` (5) beyond the recovery, fits a fresh baseline there, and repeats (up to six passes). Incident replays (`timemachine`) are built on the window each incident was analysed in. Limits: a regression already present when a baseline starts is invisible to that baseline, and a pass only continues when every incident in it has recovered.

## Incident memory and recurrence

`ail_investigation.history.similarity` compares two incidents: `0.15 metric_equal + 0.30 jaccard(cohort dimension=value pairs) + 0.35 jaccard(failure-signature tokens with at least 20% of the excess) + 0.20 same (trigger kind, component)`. Signature tokens split on non-alphanumerics (`OOM_KILL|MemoryCache.Allocate` gives `oom, kill, memorycache, allocate`), so related signatures overlap partially. Matches at 0.5 or above are listed; 0.8 or above are labelled a possible recurrence and cited as evidence (`H1`) that the agent can use. An incident never matches an earlier analysis of the same data (compared by data fingerprint). Weights are judgement calls, not fitted.

## Natural-language queries

`ail_detection.nlq` parses a question into a metric (failure rate by default; latency and memory by keyword), a window (`last N minutes/hours/days/weeks`, relative to the newest data and clipped to what exists), an include cohort and optional excluded cohorts after `but not`, `excluding`, `except` or `without`. It then evaluates each cohort with the cohort statistics (significant if LLR > 12 and the ratio clears the cohort threshold) and answers "regression", "also affects the excluded cohort" or "no regression", listing matching incidents. The parsed query is always returned so a misunderstanding is visible.

## Evaluation

`ail evaluate` runs every scenario with several seeds and reports precision, recall, F1, detection latency, false-positive rate, cohort accuracy, correlation top-1/top-3 and agent grounding. Results are in [../benchmarks/RESULTS.md](../benchmarks/RESULTS.md).
