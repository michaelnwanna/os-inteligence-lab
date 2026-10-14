# ADR 0005: Dependency-free portal

- Status: accepted (deviates from the spec's React/Next.js/Tailwind/ECharts suggestion)

## Context
The authoring environment had no Node toolchain, and a `npm install` step would lengthen the one-command demo and add a supply-chain surface.

## Decision
Ship the portal as static HTML, CSS and ES modules-free JavaScript with a small SVG chart helper, served by the API. All dynamic text is inserted via `textContent`/text nodes, never as HTML.

## Consequences
- `make demo` needs only Python.
- The five required views exist, including the Failure Time Machine (play, pause, speed, rewind, step, scrub, per-stage evidence).
- No type checking or component library. If the portal outgrows this, the API is already the contract for a Next.js front end; the JSON the portal reads would not change.
