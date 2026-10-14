"""Write JSON Schemas for the public telemetry and result models."""

from __future__ import annotations

import json
from pathlib import Path

from ail_schemas import ChangeEvent, Incident, TelemetryEvent

OUT = Path(__file__).parent / "json"


def main() -> None:
    OUT.mkdir(exist_ok=True)
    for name, model in {"telemetry-event": TelemetryEvent, "change-event": ChangeEvent, "incident": Incident}.items():
        (OUT / f"{name}.schema.json").write_text(json.dumps(model.model_json_schema(), indent=2) + "\n")
        print(f"wrote {name}.schema.json")


if __name__ == "__main__":
    main()
