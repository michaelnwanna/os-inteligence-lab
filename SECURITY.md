# Security policy

## Reporting a vulnerability
Please do not open a public issue for security problems. Use GitHub's private vulnerability reporting ("Security" tab, "Report a vulnerability") on this repository, and include steps to reproduce and the affected version.

## Scope and guidance
- Never commit API tokens, GitHub tokens or LLM keys. Use environment variables (`AIL_API_TOKEN`, `GITHUB_TOKEN`, `AIL_LLM_API_KEY`). `.env` files are git-ignored.
- Give the GitHub token the minimum scopes: read access to contents and metadata. Only grant `issues:write` if you intend to use `ail issue --create`.
- Set `AIL_API_TOKEN` and run behind an authenticating reverse proxy before exposing the API beyond localhost; read endpoints are unauthenticated by default.
- Do not send personal data. The schema rejects unknown fields and PII-looking dimension keys, but values you send are your responsibility.

See the [threat model](docs/threat-model.md) for the full analysis.
