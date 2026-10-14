# Contributing

Thanks for helping. The fastest way in is a new scenario, detector or provider, because each has an automatic scoreboard.

## Setup
```bash
git clone <your fork> && cd os-intelligence-lab
make setup        # venv + editable install with dev tools
make test         # ~30 s
make lint
```

## Workflow
1. Open an issue describing the problem or idea (or pick one).
2. Branch, make a focused change, add or update tests.
3. `make test lint`. If detection behaviour changed, run `make evaluate` and include the diff of `docs/benchmarks/RESULTS.md`.
4. Open a pull request explaining *why*. Link the ADR if you made an architectural decision (copy `docs/adr/0001-*.md` as a template).

## Good first contributions
- **A scenario.** Copy a file in `docs/scenarios/`, give it an `expected:` block, add it to `docs/scenarios/README.md`. The acceptance test picks it up automatically.
- **A detector.** Subclass `Detector`, add it to `DEFAULT_DETECTORS`, add a unit test (fires on a step, silent on stationary data, causal).
- **An LLM provider.** Implement `LLMProvider.complete`; the guardrails already apply. Test with `httpx.MockTransport`.
- **A repository provider.** Implement `RepoProvider.compare`.
- **An agent collector or OS.** Add a signal only after documenting it in `SIGNALS`, `docs/architecture/local-agent.md` and the threat model; test the parser with fixture text. Running the agent on Linux or Windows and reporting results is especially valuable.
- **A storage backend.** Implement the `TelemetryStore` methods.

## Rules of the road
- No personal data in code, fixtures, issues or logs.
- Never commit secrets; use environment variables.
- Do not make the agent state a number it was not given. If you extend the evidence, extend the validator tests.
- Be kind. We follow the [Contributor Covenant](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).

See [docs/coding-standards.md](docs/coding-standards.md).
