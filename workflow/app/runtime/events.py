from __future__ import annotations

import json
import uuid
from dataclasses import dataclass, field
from typing import Any

_EVENT_NAMESPACE = uuid.UUID("9d7ee3cb-85fb-49e9-aa8d-5fcc858bf340")
MAX_EVENT_PAYLOAD_JSON_BYTES = 32_768
_FORBIDDEN_KEYS = frozenset(
    {
        "prompt",
        "message",
        "content",
        "args",
        "arguments",
        "value",
        "token",
        "secret",
        "resource",
    }
)


def _sanitize_payload(value: Any, depth: int = 0) -> Any:
    if depth > 8:
        raise ValueError("event payload is too deep")
    if value is None or isinstance(value, (bool, int, float)):
        return value
    if isinstance(value, str):
        return value[:500]
    if isinstance(value, list | tuple):
        return [_sanitize_payload(item, depth + 1) for item in value[:100]]
    if isinstance(value, dict):
        result: dict[str, Any] = {}
        for raw_key, item in list(value.items())[:100]:
            key = str(raw_key)
            if key.lower() in _FORBIDDEN_KEYS:
                raise ValueError(f"event payload key is sensitive: {key}")
            result[key] = _sanitize_payload(item, depth + 1)
        return result
    raise ValueError("event payload contains a non-JSON value")


def _payload_size(value: dict[str, Any]) -> int:
    return len(
        json.dumps(
            value,
            ensure_ascii=False,
            separators=(",", ":"),
            allow_nan=False,
        ).encode("utf-8")
    )


def _safe_payload(value: Any) -> dict[str, Any]:
    sanitized = _sanitize_payload(value)
    if not isinstance(sanitized, dict):
        raise ValueError("event payload must be an object")
    original_size = _payload_size(sanitized)
    if original_size <= MAX_EVENT_PAYLOAD_JSON_BYTES:
        return sanitized

    result = dict(sanitized)
    result["truncated"] = True
    result["original_size_bytes"] = original_size
    for key, item in list(result.items()):
        if isinstance(item, list):
            result[f"{key}_total"] = len(item)

    # Preserve stable prefixes and aggregate counts. Removing from list tails
    # is deterministic and keeps rule/evaluation order meaningful.
    while _payload_size(result) > MAX_EVENT_PAYLOAD_JSON_BYTES:
        candidates = [
            (len(item), key, item)
            for key, item in result.items()
            if isinstance(item, list) and item
        ]
        if not candidates:
            break
        _, _, longest = max(candidates, key=lambda item: (item[0], item[1]))
        longest.pop()

    # Generic safety for payloads dominated by strings rather than lists.
    while _payload_size(result) > MAX_EVENT_PAYLOAD_JSON_BYTES:
        strings = [
            (len(item), key)
            for key, item in result.items()
            if isinstance(item, str) and item
        ]
        if not strings:
            break
        length, key = max(strings, key=lambda item: (item[0], item[1]))
        result[key] = result[key][: max(0, length // 2)]

    if _payload_size(result) <= MAX_EVENT_PAYLOAD_JSON_BYTES:
        return result

    summary: dict[str, Any] = {
        "truncated": True,
        "original_size_bytes": original_size,
    }
    for key, item in sanitized.items():
        if isinstance(item, list):
            summary[f"{key}_total"] = len(item)
        elif isinstance(item, (bool, int, float)) or item is None:
            summary[key] = item
        elif key in {"outcome", "source_rule_id", "error_code", "status"}:
            summary[key] = item
    return summary


@dataclass(frozen=True)
class RuntimeEvent:
    event_id: str
    event_type: str
    node_id: str
    snapshot_hash: str
    payload: dict[str, Any] = field(default_factory=dict)

    def as_backend_dict(self) -> dict[str, Any]:
        return {
            "event_id": self.event_id,
            "event_type": self.event_type,
            "node_id": self.node_id or None,
            "snapshot_hash": self.snapshot_hash,
            "payload": _safe_payload(self.payload),
        }


def runtime_event(
    *,
    run_id: str,
    snapshot_hash: str,
    event_type: str,
    node_id: str,
    event_key: str,
    payload: dict[str, Any] | None = None,
) -> RuntimeEvent:
    # A stable ID makes supervisor retries safe against duplicate audit rows.
    identity = f"{run_id}:{snapshot_hash}:{event_type}:{node_id}:{event_key}"
    return RuntimeEvent(
        event_id=str(uuid.uuid5(_EVENT_NAMESPACE, identity)),
        event_type=event_type[:100],
        node_id=node_id[:200],
        snapshot_hash=snapshot_hash,
        payload=_safe_payload(payload or {}),
    )
