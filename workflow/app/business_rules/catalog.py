"""Versioned, server-owned Business Rule catalogs and resource limits."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Literal

from app.business_rules.decimal_value import (
    DECIMAL_WIRE_FORMAT,
    MAX_DECIMAL_INTEGER_DIGITS,
    MAX_DECIMAL_PRECISION,
    MAX_DECIMAL_SCALE,
)

CATALOG_VERSION = 1
RULE_SET_VERSION = 1

GATES = (
    "preflight",
    "post-context",
    "pre-action",
    "post-action",
    "pre-response",
)

LIMITS = {
    "maxDepth": 3,
    "maxJsonDepth": 16,
    "maxNodes": 256,
    "maxRules": 100,
    "maxActionsPerRule": 16,
    "maxStringLength": 1024,
    "maxCollectionItems": 100,
    "maxFacts": 128,
    "maxObjectFields": 32,
    "maxErrors": 100,
    "maxRequestBytes": 1_048_576,
    "maxDecimalPrecision": MAX_DECIMAL_PRECISION,
    "maxDecimalScale": MAX_DECIMAL_SCALE,
    "maxDecimalIntegerDigits": MAX_DECIMAL_INTEGER_DIGITS,
}

FactType = Literal[
    "string", "enum", "number", "decimal", "integer", "boolean", "collection"
]
ROLE_VALUES = ("USER", "ADMIN")


@dataclass(frozen=True)
class FactSpec:
    name: str
    type: FactType
    provenance: Literal["system", "tool", "llm-inferred", "user"]
    trust_tier: Literal["trusted", "verified", "inferred", "untrusted"]
    gates: tuple[str, ...]
    operators: tuple[str, ...]
    visible_value: bool = True
    enum_values: tuple[str, ...] = ()
    item_type: str | None = None

    def public(self) -> dict[str, Any]:
        item: dict[str, Any] = {
            "name": self.name,
            "type": self.type,
            "provenance": self.provenance,
            "trustTier": self.trust_tier,
            "gates": list(self.gates),
            "operators": list(self.operators),
            "visibleValue": self.visible_value,
        }
        if self.enum_values:
            item["enumValues"] = list(self.enum_values)
        if self.item_type is not None:
            item["itemType"] = self.item_type
        if self.type == "decimal":
            item["wireFormat"] = DECIMAL_WIRE_FORMAT
        return item


ALL_GATES = GATES
AFTER_CONTEXT = ("post-context", "pre-action", "post-action", "pre-response")
ACTION_GATES = ("pre-action", "post-action")
AFTER_ACTION = ("post-action", "pre-response")

FACTS: tuple[FactSpec, ...] = (
    FactSpec(
        "caller.role",
        "enum",
        "system",
        "trusted",
        ALL_GATES,
        ("eq", "neq", "in", "exists", "not_exists"),
        enum_values=ROLE_VALUES,
    ),
    FactSpec(
        "caller.tenant_id",
        "string",
        "system",
        "trusted",
        ALL_GATES,
        ("eq", "neq", "in", "contains", "exists", "not_exists"),
        visible_value=False,
    ),
    FactSpec(
        "caller.groups",
        "collection",
        "system",
        "trusted",
        ALL_GATES,
        ("contains", "contains_any", "is_empty", "exists", "not_exists"),
        item_type="string",
    ),
    FactSpec(
        "request.channel",
        "enum",
        "system",
        "trusted",
        ALL_GATES,
        ("eq", "neq", "in", "exists", "not_exists"),
        enum_values=("chat", "copilot", "api", "scheduled"),
    ),
    FactSpec(
        "request.intent",
        "string",
        "llm-inferred",
        "inferred",
        ALL_GATES,
        ("eq", "neq", "in", "contains", "exists", "not_exists"),
    ),
    FactSpec(
        "context.confidence",
        "number",
        "llm-inferred",
        "inferred",
        AFTER_CONTEXT,
        ("eq", "gt", "gte", "lt", "lte", "between", "exists", "not_exists"),
    ),
    FactSpec(
        "context.source_count",
        "integer",
        "tool",
        "verified",
        AFTER_CONTEXT,
        ("eq", "gt", "gte", "lt", "lte", "between", "exists", "not_exists"),
    ),
    FactSpec(
        "context.source_types",
        "collection",
        "tool",
        "verified",
        AFTER_CONTEXT,
        ("contains", "contains_any", "is_empty", "exists", "not_exists"),
        item_type="string",
    ),
    FactSpec(
        "action.type",
        "enum",
        "system",
        "trusted",
        ACTION_GATES,
        ("eq", "neq", "in", "exists", "not_exists"),
        enum_values=("tool_call", "skill_call", "response", "refund", "write"),
    ),
    FactSpec(
        "action.tool_name",
        "string",
        "system",
        "trusted",
        ACTION_GATES,
        ("eq", "neq", "in", "contains", "exists", "not_exists"),
    ),
    FactSpec(
        "action.amount",
        "decimal",
        "system",
        "trusted",
        ACTION_GATES,
        ("eq", "gt", "gte", "lt", "lte", "between", "exists", "not_exists"),
    ),
    FactSpec(
        "action.requested_tools",
        "collection",
        "system",
        "trusted",
        ACTION_GATES,
        ("contains", "contains_any", "is_empty", "exists", "not_exists"),
        item_type="string",
    ),
    FactSpec(
        "skill.name",
        "string",
        "system",
        "trusted",
        ACTION_GATES,
        ("eq", "neq", "in", "contains", "exists", "not_exists"),
    ),
    FactSpec(
        "result.has_citations",
        "boolean",
        "system",
        "trusted",
        AFTER_ACTION,
        ("is_true", "is_false", "exists", "not_exists"),
    ),
)

FACT_BY_NAME = {fact.name: fact for fact in FACTS}

OPERATORS: tuple[dict[str, Any], ...] = (
    {"name": "eq", "compatibleFactTypes": ["string", "enum", "number", "decimal", "integer"], "value": {"kind": "scalar", "types": ["string", "number", "decimal", "integer"]}},
    {"name": "neq", "compatibleFactTypes": ["string", "enum"], "value": {"kind": "scalar", "types": ["string"]}},
    {"name": "in", "compatibleFactTypes": ["string", "enum"], "value": {"kind": "list", "types": ["string"]}},
    {"name": "contains", "compatibleFactTypes": ["string", "collection"], "value": {"kind": "scalar", "types": ["string"]}},
    {"name": "gt", "compatibleFactTypes": ["number", "decimal", "integer"], "value": {"kind": "scalar", "types": ["number", "decimal", "integer"]}},
    {"name": "gte", "compatibleFactTypes": ["number", "decimal", "integer"], "value": {"kind": "scalar", "types": ["number", "decimal", "integer"]}},
    {"name": "lt", "compatibleFactTypes": ["number", "decimal", "integer"], "value": {"kind": "scalar", "types": ["number", "decimal", "integer"]}},
    {"name": "lte", "compatibleFactTypes": ["number", "decimal", "integer"], "value": {"kind": "scalar", "types": ["number", "decimal", "integer"]}},
    {"name": "between", "compatibleFactTypes": ["number", "decimal", "integer"], "value": {"kind": "range", "types": ["number", "decimal", "integer"]}},
    {"name": "is_true", "compatibleFactTypes": ["boolean"], "value": {"kind": "none", "types": []}},
    {"name": "is_false", "compatibleFactTypes": ["boolean"], "value": {"kind": "none", "types": []}},
    {"name": "contains_any", "compatibleFactTypes": ["collection"], "value": {"kind": "list", "types": ["string"]}},
    {"name": "is_empty", "compatibleFactTypes": ["collection"], "value": {"kind": "none", "types": []}},
    {"name": "exists", "compatibleFactTypes": ["string", "enum", "number", "decimal", "integer", "boolean", "collection"], "value": {"kind": "none", "types": []}},
    {"name": "not_exists", "compatibleFactTypes": ["string", "enum", "number", "decimal", "integer", "boolean", "collection"], "value": {"kind": "none", "types": []}},
)

OPERATOR_BY_NAME = {operator["name"]: operator for operator in OPERATORS}


def _parameter(
    name: str,
    type_: str,
    *,
    required: bool,
    max_items: int | None = None,
    enum_values: tuple[str, ...] = (),
) -> dict[str, Any]:
    result: dict[str, Any] = {"name": name, "type": type_, "required": required}
    if max_items is not None:
        result["maxItems"] = max_items
    if enum_values:
        result["enumValues"] = list(enum_values)
    return result


# Larger precedence wins.  Modifier actions never become the primary decision,
# but are still returned in deterministic action order.
ACTIONS: tuple[dict[str, Any], ...] = (
    {"name": "deny", "decision": True, "precedence": 500, "parameters": [_parameter("reason", "string", required=False)]},
    {"name": "require_approval", "decision": True, "precedence": 400, "parameters": [_parameter("role", "enum", required=True, enum_values=ROLE_VALUES)]},
    {"name": "escalate", "decision": True, "precedence": 300, "parameters": [_parameter("role", "enum", required=False, enum_values=ROLE_VALUES), _parameter("reason", "string", required=False)]},
    {"name": "ask_user", "decision": True, "precedence": 200, "parameters": [_parameter("question", "string", required=False)]},
    {"name": "require_context", "decision": True, "precedence": 200, "parameters": [_parameter("facts", "collection", required=False, max_items=LIMITS["maxCollectionItems"])]},
    {"name": "route_to_skill", "decision": True, "precedence": 100, "parameters": [_parameter("skill", "string", required=True)]},
    {"name": "allow_read_tool", "decision": False, "precedence": 0, "parameters": [_parameter("tools", "collection", required=True, max_items=LIMITS["maxCollectionItems"])]},
    {"name": "set_response_policy", "decision": False, "precedence": 0, "parameters": [_parameter("policy", "string", required=True)]},
    {"name": "add_audit_tag", "decision": False, "precedence": 0, "parameters": [_parameter("tag", "string", required=True)]},
)

ACTION_BY_NAME = {action["name"]: action for action in ACTIONS}


def catalog_response() -> dict[str, Any]:
    """Return a fresh JSON-safe catalog DTO so callers cannot mutate registries."""
    return {
        "version": CATALOG_VERSION,
        "versions": {
            "facts": CATALOG_VERSION,
            "operators": CATALOG_VERSION,
            "actions": CATALOG_VERSION,
        },
        "ruleSetVersion": RULE_SET_VERSION,
        "decimalWireFormat": {
            "type": "string",
            "format": DECIMAL_WIRE_FORMAT,
            "allowExponent": False,
            "maxPrecision": MAX_DECIMAL_PRECISION,
            "maxScale": MAX_DECIMAL_SCALE,
            "maxIntegerDigits": MAX_DECIMAL_INTEGER_DIGITS,
        },
        "gates": list(GATES),
        "limits": dict(LIMITS),
        "facts": [fact.public() for fact in FACTS],
        "operators": [
            {
                **operator,
                "compatibleFactTypes": list(operator["compatibleFactTypes"]),
                "value": {
                    **operator["value"],
                    "types": list(operator["value"]["types"]),
                },
            }
            for operator in OPERATORS
        ],
        "actions": [
            {
                **action,
                "parameters": [
                    {
                        **parameter,
                        **(
                            {"enumValues": list(parameter["enumValues"])}
                            if "enumValues" in parameter
                            else {}
                        ),
                    }
                    for parameter in action["parameters"]
                ],
            }
            for action in ACTIONS
        ],
    }
