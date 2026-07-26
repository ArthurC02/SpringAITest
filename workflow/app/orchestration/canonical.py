"""Canonical JSON and hashes for semantic Graph IR and UI metadata.

排序語意刻意與 app/runtime/models.py（D3/D5/D7）共用 app/canonical_json.py：
兩份 canonical JSON 曾各自實作，前者用 Python code point 排序，後者對齊 .NET
StringComparer.Ordinal 的 UTF-16 code-unit 排序；非 BMP key 上兩者排序會分岔，
造成同一份定義在兩個雜湊契約下算出不同雜湊。統一改用 UTF-16 ordinal 版本。
"""

from __future__ import annotations

import json
from typing import Any

from app.canonical_json import canonical_json_bytes, canonical_json_sha256


def canonical_json(value: Any) -> Any:
    """Return JSON-normalised values with deterministic object member order."""
    return json.loads(canonical_json_bytes(value))


def canonical_definition(definition: dict[str, Any]) -> dict[str, Any]:
    result = canonical_json(definition)
    result["nodes"] = sorted(result["nodes"], key=lambda item: item["id"])
    result["edges"] = sorted(result["edges"], key=lambda item: item["id"])
    for node in result["nodes"]:
        if isinstance(node.get("children"), list):
            node["children"] = sorted(node["children"], key=lambda item: item["id"])
    return canonical_json(result)


def sha256(value: Any) -> str:
    return canonical_json_sha256(value)
