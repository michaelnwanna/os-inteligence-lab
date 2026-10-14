# OS Intelligence Lab: C# / .NET edition

A second implementation of the engine, API and CLI in C# on .NET 10. It reads the same scenario files in
[`docs/scenarios`](../docs/scenarios), serves the same portal in [`apps/portal`](../apps/portal) and exposes the
same HTTP endpoints and JSON wire format as the Python edition, so either backend can sit behind the portal.

```bash
dotnet run --project dotnet/src/Ail.Cli -c Release -- demo --serve     # simulate, analyze, open http://127.0.0.1:8000
dotnet run --project dotnet/src/Ail.Cli -c Release -- scenarios
dotnet run --project dotnet/src/Ail.Cli -c Release -- report AIL-1001 --db data/demo.db
dotnet run --project dotnet/src/Ail.Cli -c Release -- ask "regressions affecting ARM64 but not x64" --db data/demo.db
dotnet run --project dotnet/src/Ail.Cli -c Release -- evaluate --output docs/benchmarks/RESULTS-dotnet.md
dotnet run --project dotnet/src/Ail.Cli -c Release -- evaluate --heldout    # the fourteen held-out scenarios
dotnet test dotnet/OsIntelligenceLab.sln
```

Requires the .NET 10 SDK. No other tooling, packages beyond NuGet restore, or API keys are needed. Set `AIL_HOME` to the
repository root if you run the built binary from outside the repository.

## Layout

| Project | Contents |
|---|---|
| `src/Ail.Schemas` | Telemetry and result models, validation (including the personal-data key check), snake_case JSON |
| `src/Ail.Simulator` | Scenario loader (YAML), seeded RNG, fleet simulator that writes minute/cohort aggregates |
| `src/Ail.Ingestion` | SQLite store, normalisation, aggregation, in-memory stream with backpressure, Prometheus metrics |
| `src/Ail.Detection` | Five detectors, cohort scan statistic, change correlation, incident builder, series/breakdown analytics, natural-language query |
| `src/Ail.Investigation` | Evidence builder, grounding validator, offline rules provider, OpenAI-compatible provider, GitHub client, incident archive, commit ranking, time machine, report, benchmark harness |
| `src/Ail.Api` | ASP.NET Core minimal API and static portal, optional `AIL_API_TOKEN`, `AIL_READ_TOKEN`, `AIL_RATE_LIMIT` |
| `src/Ail.Cli` | The `ail` command |
| `tests/Ail.Tests` | xUnit: all ten scenarios against ground truth, grounding guardrails, schema, ingestion and API (real Kestrel server), history, natural-language query, GitHub client |

## What is ported, and what is not

Ported: the full pipeline (simulate, ingest, detect, isolate cohort, correlate, investigate, replay), multi-episode
detection with recovery and re-baselining, the grounding validator and rules fallback, the fixture repository and the
read-only GitHub client (compare, commit files, issue creation), the cross-run incident archive with recurrence matching
(`ail history`), the natural-language query (`ail ask`, `/api/v1/query`), issue drafting (`ail issue`), the portal API,
token authentication and rate limiting on the write endpoints, and the benchmark harness.

Not ported (use the Python edition): the opt-in local machine agent and `ail stream`, which sends simulated events to a
running server through the Python SDK. The GitHub client is covered by mocked-transport tests only; it has not been run
against the live GitHub API.

## Differences from the Python edition

The algorithms are the same, but the random number generator is not, so the simulated telemetry is statistically
equivalent rather than identical and numbers will not match run for run. Measured results are in
[docs/benchmarks/RESULTS-dotnet.md](../docs/benchmarks/RESULTS-dotnet.md): all ten scenarios x 3 seeds had the true cohort and
the true change ranked first, with one false incident on a clean run (precision 0.97). Over 55 clean seeds (benchmark seeds included) the .NET
edition raised 2 false incidents and Python 1, so both have a false-alarm rate of a few percent per clean run.
