# OS Intelligence Lab: C# / .NET edition

A second implementation of the engine, API and CLI in C# on .NET 10. It reads the same scenario files in
[`docs/scenarios`](../docs/scenarios), serves the same portal in [`apps/portal`](../apps/portal) and exposes the
same HTTP endpoints and JSON wire format as the Python edition, so either backend can sit behind the portal.

```bash
dotnet run --project dotnet/src/Ail.Cli -c Release -- demo --serve     # simulate, analyze, open http://127.0.0.1:8000
dotnet run --project dotnet/src/Ail.Cli -c Release -- scenarios
dotnet run --project dotnet/src/Ail.Cli -c Release -- report AIL-1001 --db data/demo.db
dotnet run --project dotnet/src/Ail.Cli -c Release -- evaluate --output docs/benchmarks/RESULTS-dotnet.md
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
| `src/Ail.Detection` | Five detectors, cohort scan statistic, change correlation, incident builder, series/breakdown analytics |
| `src/Ail.Investigation` | Evidence builder, grounding validator, offline rules provider, OpenAI-compatible provider, commit ranking, time machine, report, benchmark harness |
| `src/Ail.Api` | ASP.NET Core minimal API and static portal, optional `AIL_API_TOKEN`, `AIL_READ_TOKEN`, `AIL_RATE_LIMIT` |
| `src/Ail.Cli` | The `ail` command |
| `tests/Ail.Tests` | xUnit: all ten scenarios against ground truth, grounding guardrails, schema, ingestion and API (real Kestrel server) |

## What is ported, and what is not

Ported: the full pipeline (simulate, ingest, detect, isolate cohort, correlate, investigate, replay), multi-episode
detection with recovery and re-baselining, the grounding validator and rules fallback, the fixture repository, the
portal API, token authentication and rate limiting on the write endpoints, and the benchmark harness.

Not ported (use the Python edition): the opt-in local machine agent, the cross-run incident history archive and
recurrence matching, the natural-language query (`/api/v1/query` returns 501), the live GitHub and Azure DevOps
repository providers (only the fixture repository), and `ail stream` / `ail issue`.

## Differences from the Python edition

The algorithms are the same, but the random number generator is not, so the simulated telemetry is statistically
equivalent rather than identical and numbers will not match run for run. Measured results are in
[docs/benchmarks/RESULTS-dotnet.md](../docs/benchmarks/RESULTS-dotnet.md): the ten scenarios x 3 seeds all had the
true cohort and the true change ranked first, with one false incident (precision 0.97) on a clean run. The Python edition
had none on the same scenarios, and 15 further clean runs gave 1 false incident for .NET and 0 for Python, which is too few
to say whether the difference is real.

One behaviour is shared by both editions: when two regressions overlap, the second incident's first-detection time can
precede its own effect start by a few minutes, because the fleet-level alarm raised by the first is reused.
