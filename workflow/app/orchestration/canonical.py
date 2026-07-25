"""Canonical JSON and hashes for semantic Graph IR and UI metadata."""

from __future__ import annotations

import hashlib
import json
from typing import Any


def _json_bytes(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")


def canonical_json(value: Any) -> Any:
    """Return JSON-normalised values with deterministic object member order."""
    return json.loads(_json_bytes(value))


def canonical_definition(definition: dict[str, Any]) -> dict[str, Any]:
    result = canonical_json(definition)
    result["nodes"] = sorted(result["nodes"], key=lambda item: item["id"])
    result["edges"] = sorted(result["edges"], key=lambda item: item["id"])
    for node in result["nodes"]:
        if isinstance(node.get("children"), list):
            node["children"] = sorted(node["children"], key=lambda item: item["id"])
    return canonical_json(result)


def sha256(value: Any) -> str:
    return hashlib.sha256(_json_bytes(value)).hexdigest()
