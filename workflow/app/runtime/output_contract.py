from __future__ import annotations

import json
import math
from decimal import Decimal
from typing import Any

MAX_CONTRACT_UTF8_BYTES = 262_144
MAX_CONTRACT_DEPTH = 8
MAX_CONTRACT_NODES = 256
MAX_PROPERTIES = 64
MAX_REQUIRED = 64
MAX_ENUM_VALUES = 64
MAX_PROPERTY_NAME_CHARS = 128
MAX_ENUM_STRING_CHARS = 4_096

_TYPES = {"object", "array", "string", "number", "integer", "boolean", "null"}
_KEYS = {"type", "properties", "required", "additionalProperties", "items", "enum"}


def validate_output_contract(contract: dict[str, Any]) -> None:
    if not contract:
        return  # Published D1/D2 compatibility: empty means string.
    size = len(
        json.dumps(
            contract,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
            default=str,
        ).encode("utf-8")
    )
    if size > MAX_CONTRACT_UTF8_BYTES:
        raise ValueError("output contract exceeds its canonical byte limit")
    counter = [0]
    _validate_schema(contract, depth=1, counter=counter)


def output_matches(contract: dict[str, Any], output: str) -> bool:
    if not contract or contract.get("type") == "string":
        value: Any = output
    else:
        try:
            value = json.loads(output)
        except (TypeError, json.JSONDecodeError):
            return False
    return _matches(contract or {"type": "string"}, value)


def _validate_schema(schema: Any, *, depth: int, counter: list[int]) -> None:
    if not isinstance(schema, dict):
        raise ValueError("output contract schema nodes must be objects")
    counter[0] += 1
    if depth > MAX_CONTRACT_DEPTH or counter[0] > MAX_CONTRACT_NODES:
        raise ValueError("output contract exceeds its structural limits")
    unknown = set(schema) - _KEYS
    if unknown:
        raise ValueError(f"output contract contains unsupported keywords: {sorted(unknown)}")
    expected = schema.get("type")
    if not isinstance(expected, str) or expected not in _TYPES:
        raise ValueError("output contract type is invalid")

    type_keys = {
        "object": {"type", "properties", "required", "additionalProperties", "enum"},
        "array": {"type", "items", "enum"},
        "string": {"type", "enum"},
        "number": {"type", "enum"},
        "integer": {"type", "enum"},
        "boolean": {"type", "enum"},
        "null": {"type", "enum"},
    }[expected]
    if set(schema) - type_keys:
        raise ValueError("output contract keyword is not valid for its type")

    if expected == "object":
        properties = schema.get("properties", {})
        required = schema.get("required", [])
        additional = schema.get("additionalProperties", True)
        if not isinstance(properties, dict) or len(properties) > MAX_PROPERTIES:
            raise ValueError("output contract properties are invalid")
        if (
            not isinstance(required, list)
            or len(required) > MAX_REQUIRED
            or any(not isinstance(item, str) for item in required)
            or len(set(required)) != len(required)
            or any(item not in properties for item in required)
        ):
            raise ValueError("output contract required fields are invalid")
        if not isinstance(additional, bool):
            raise ValueError("output contract additionalProperties must be boolean")
        for name, child in properties.items():
            if (
                not isinstance(name, str)
                or not name
                or len(name) > MAX_PROPERTY_NAME_CHARS
            ):
                raise ValueError("output contract property name is invalid")
            _validate_schema(child, depth=depth + 1, counter=counter)
    elif expected == "array":
        if "items" not in schema:
            raise ValueError("output contract array requires items")
        _validate_schema(schema["items"], depth=depth + 1, counter=counter)

    if "enum" in schema:
        values = schema["enum"]
        if not isinstance(values, list) or not 1 <= len(values) <= MAX_ENUM_VALUES:
            raise ValueError("output contract enum is invalid")
        if any(
            isinstance(item, (dict, list))
            or isinstance(item, str) and len(item) > MAX_ENUM_STRING_CHARS
            or not _type_matches(expected, item)
            for item in values
        ):
            raise ValueError("output contract enum value is invalid")
        fingerprints = [_scalar_fingerprint(item) for item in values]
        if len(set(fingerprints)) != len(fingerprints):
            raise ValueError("output contract enum values must be unique")


def _matches(schema: dict[str, Any], value: Any) -> bool:
    expected = schema["type"]
    if not _type_matches(expected, value):
        return False
    if "enum" in schema and _scalar_fingerprint(value) not in {
        _scalar_fingerprint(item) for item in schema["enum"]
    }:
        return False
    if expected == "object":
        properties = schema.get("properties", {})
        if any(name not in value for name in schema.get("required", [])):
            return False
        if schema.get("additionalProperties", True) is False and any(
            name not in properties for name in value
        ):
            return False
        return all(
            name not in value or _matches(child, value[name])
            for name, child in properties.items()
        )
    if expected == "array":
        return all(_matches(schema["items"], item) for item in value)
    return True


def _type_matches(expected: str, value: Any) -> bool:
    if expected == "object":
        return isinstance(value, dict)
    if expected == "array":
        return isinstance(value, list)
    if expected == "string":
        return isinstance(value, str)
    if expected == "boolean":
        return isinstance(value, bool)
    if expected == "null":
        return value is None
    if expected == "integer":
        return (
            not isinstance(value, bool)
            and isinstance(value, int | float | Decimal)
            and _finite(value)
            and int(value) == value
        )
    if expected == "number":
        return (
            not isinstance(value, bool)
            and isinstance(value, int | float | Decimal)
            and _finite(value)
        )
    return False


def _scalar_fingerprint(value: Any) -> tuple[str, Any]:
    if value is None:
        return ("null", None)
    if isinstance(value, bool):
        return ("boolean", value)
    if isinstance(value, str):
        return ("string", value)
    if isinstance(value, int):
        return ("number", Decimal(value))
    if isinstance(value, Decimal):
        return ("number", value.normalize())
    if isinstance(value, float):
        return ("number", Decimal(str(value)).normalize())
    raise ValueError("enum values must be scalar")


def _finite(value: int | float | Decimal) -> bool:
    return value.is_finite() if isinstance(value, Decimal) else math.isfinite(value)
