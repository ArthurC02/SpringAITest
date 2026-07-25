"""Safe deterministic simulator for Designer validation previews."""

from __future__ import annotations

from typing import Any


def simulated_trace(definition: dict[str, Any]) -> list[dict[str, str]]:
    """Return a stable, data-free topological overlay; never runs adapters/tools."""
    # A bounded loop is a valid IR shape but not a safe invitation to execute
    # it here.  A sorted, data-free overlay gives the Designer stable IDs for
    # every node without accidentally modelling runtime control flow.
    return [
        {"nodeId": node["id"], "status": "simulated"}
        for node in sorted(definition["nodes"], key=lambda item: item["id"])
    ]
