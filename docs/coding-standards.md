# Coding standards

## Python
- Python 3.10+, type hints on public functions, `from __future__ import annotations`.
- `ruff check .` must pass (rules `E F I B UP`, line length 100, `E501` ignored). Run `ruff check . --fix` for import ordering.
- Pydantic models for anything that crosses a boundary (HTTP, storage, reports). Plain dataclasses for internal numeric structures.
- NumPy over Python loops for anything that scales with devices or minutes; keep loops to per-minute orchestration.
- Statistics: write the formula in the docstring or `docs/architecture/detection-methods.md`, name thresholds in a config model, and never hard-code a magic number inside a detector.
- Detectors must be **causal** (no look-ahead) and return at most one `AnomalyResult` per metric. Add a test using `tests/unit/test_detectors.py` fixtures.
- SQL lives only in `TelemetryStore` and is always parameterised.
- No network calls at import time. Providers receive their clients by injection so tests can use `httpx.MockTransport`.
- Comments say *why*; do not restate the code.

## Determinism
Everything that uses randomness takes a seed. Tests and benchmarks must be reproducible from a scenario file plus a seed.

## Tests
- `tests/unit`: pure functions, detectors on synthetic series, ingestion behaviour.
- `tests/integration`: scenario ground truth, agent guardrails, HTTP API.
- A new detector, scenario or provider is not done until it has a test that would fail if it were wrong.
- Never weaken a ground-truth assertion to make a test pass; fix the engine or explain the change in the PR.

## Portal
- Never use `innerHTML` with data. Build DOM with the `el()` helper.
- No new runtime dependency without an ADR.

## Commits and PRs
Small, focused, with the reason in the description. Update docs and the benchmark table when behaviour changes.
