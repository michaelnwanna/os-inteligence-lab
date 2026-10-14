# ADR 0001: Python for the analytics core, one repository

- Status: accepted

## Context
The product is statistics-heavy (scan statistics, change-point detection, simulation) and needs a fast path from idea to experiment for data and ML engineers. The spec suggests Python/FastAPI with an optional Go or Rust collector later.

## Decision
Python 3.10+, NumPy for numerics, Pydantic for models, FastAPI for HTTP, in a single repository laid out as the spec's `apps/ services/ packages/` tree. Packages are mapped from those folders by `pyproject.toml` so `pip install -e .` gives every module.

## Consequences
- One install and one `pytest` cover everything.
- No SciPy/pandas/scikit-learn dependency: the needed statistics are small and explicit, which keeps the install light and the math reviewable.
- A high-throughput collector can be written in Go or Rust later because the wire contract is the JSON schema, not Python.
