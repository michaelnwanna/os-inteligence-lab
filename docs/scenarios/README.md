# Scenarios

Each file defines a synthetic fleet, staged rollouts, injected regressions and the **expected outcome** that the platform is scored against (`expected:`; use `episodes:` when there are several regressions). All times are minutes from the scenario start (12:00 UTC); a run lasts 180 minutes (scenario 009: 420) and the first 60 are the baseline.

| ID | Scenario | Injected | Expected |
|---|---|---|---|
| 001 | No regression | benign driver and app rollouts | no incident |
| 002 | Global crash regression | feature-flag config change at 14:00, +3% crashes everywhere | one global incident, no cohort, trigger = config change |
| 003 | ARM64 memory regression (flagship) | Atlas 2.1 rollout at 13:52, ARM64 + 2.1 fails 21x more, memory 2.4x | cohort ARM64 + App 2.1, trigger = deployment, commit `3f28a91` |
| 004 | Driver-specific regression | driver 7.14 rolled to 30% at 13:40, x64 + 7.14 fails 15x more | cohort x64 + Driver 7.14, trigger = driver update |
| 005 | Performance degradation | OS 13.0 rollout, latency 1.8x, failures unchanged | latency incident, cohort OS 13.0 |
| 006 | Deployment-correlated regression | Atlas 2.1 raises failures on all architectures; driver update before and OS update after as distractors | cohort App 2.1, deployment ranked first |
| 007 | False-positive trap | 3x traffic surge and a one-minute 2x failure blip | no incident |
| 008 | Gradual degradation | memory leak, failure rate and memory ramp over an hour | cohort App 2.1, deployment ranked first |
| 009 | Hotfix then a new regression | 7-hour run: ARM64 + App 2.1 regression fixed by a hotfix at 15:05, then driver 7.14 breaks x64 at 16:30 | two incidents; the first resolved at the hotfix, the second found after re-baselining |
| 010 | Simultaneous unrelated regressions | ARM64 + App 2.1 and x64 + Driver 7.14 start within five minutes and share some devices | two incidents, each with its own cohort and trigger |

Scenario files are plain YAML validated by `ail_simulator.config.Scenario`. To add one, copy a file, change the `id`, and `ail simulate --scenario path/to/file.yaml`. `failure_rate` in an effect is the target for an *average* device; per-model baseline differences are preserved.

`repos/atlas.json` is the controlled test repository used for code intelligence: seven commits between `v2.0` and `v2.1`, one of which (`3f28a91`) makes `MemoryCache` retain buffers without eviction.
