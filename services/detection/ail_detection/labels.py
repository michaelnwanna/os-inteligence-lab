"""Human-readable labels for metrics and cohorts."""

from __future__ import annotations

METRIC_LABELS = {
    "failure_rate": "Failure rate",
    "latency_ms": "Latency",
    "memory_mb": "Memory usage",
}

METRIC_UNITS = {"failure_rate": "%", "latency_ms": " ms", "memory_mb": " MB"}


def metric_label(metric: str) -> str:
    return METRIC_LABELS.get(metric, metric)


def format_value(metric: str, value: float) -> str:
    if metric == "failure_rate":
        return f"{value * 100:.2f}%"
    return f"{value:.0f}{METRIC_UNITS.get(metric, '')}"


def cohort_label(dims: dict[str, str]) -> str:
    """e.g. {'architecture': 'arm64', 'application_version': '2.1'} -> 'ARM64 + App 2.1'."""
    if not dims:
        return "all devices"
    order = ["os_version", "architecture", "device_model", "application", "application_version", "driver_version"]
    parts = []
    for key in order:
        if key not in dims:
            continue
        v = dims[key]
        parts.append(
            {"architecture": v.upper(), "application_version": f"App {v}", "driver_version": f"Driver {v}",
             "os_version": f"OS {v}"}.get(key, v)
        )
    return " + ".join(parts)
