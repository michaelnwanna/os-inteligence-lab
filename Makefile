PYTHON ?= python3
VENV   ?= .venv
BIN    := $(VENV)/bin

.PHONY: demo setup test lint evaluate serve clean

# One command: create the environment, simulate the flagship incident, investigate it, serve the portal.
demo: setup
	$(BIN)/ail demo --serve

setup: $(VENV)/.installed

$(VENV)/.installed: pyproject.toml
	$(PYTHON) -m venv $(VENV)
	$(BIN)/pip install --quiet --upgrade pip
	$(BIN)/pip install --quiet -e ".[dev]"
	touch $@

test: setup
	$(BIN)/pytest

lint: setup
	$(BIN)/ruff check .

evaluate: setup
	$(BIN)/ail evaluate --output docs/benchmarks/RESULTS.md

serve: setup
	$(BIN)/ail serve

clean:
	rm -rf $(VENV) data .pytest_cache .ruff_cache
