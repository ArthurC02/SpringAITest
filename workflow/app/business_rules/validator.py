"""Typed canonical AST validator for Business Rules."""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from decimal import Decimal
from typing import Any

from app.business_rules.catalog import (
    ACTION_BY_NAME,
    FACT_BY_NAME,
    GATES,
    LIMITS,
    OPERATOR_BY_NAME,
    ROLE_VALUES,
    RULE_SET_VERSION,
)
from app.business_rules.decimal_value import (
    DecimalWireError,
    is_decimal_wire,
    is_number,
    parse_decimal_wire,
)
from app.business_rules.models import (
    CanonicalRule,
    CanonicalRuleSet,
    GroupCondition,
    LeafCondition,
    RuleError,
    ValidationOutcome,
)

_IDENTIFIER = re.compile(r"^[a-z][a-z0-9]*(?:[-_.][a-z0-9]+)*$")
_ROLE_VALUES = frozenset(ROLE_VALUES)
_MISSING = object()


@dataclass
class _State:
    gate: str
    references: dict[str, frozenset[str]] = field(default_factory=dict)
    errors: list[RuleError] = field(default_factory=list)
    nodes: int = 0
    errors_truncated: bool = False
    error_root: str = "$.ruleSet"

    def error(self, path: str, code: str, message: str) -> None:
        if len(self.errors) >= LIMITS["maxErrors"] - 1:
            if not self.errors_truncated:
                self.errors.append(
                    RuleError(
                        path=self.error_root,
                        code="too_many_errors",
                        message=(
                            "Error reporting stopped after "
                            f"{LIMITS['maxErrors'] - 1} errors."
                        ),
                    )
                )
                self.errors_truncated = True
            return
        self.errors.append(RuleError(path=path, code=code, message=message))

    def consume_node(self, path: str) -> bool:
        self.nodes += 1
        if self.nodes > LIMITS["maxNodes"]:
            if not any(error.code == "too_many_nodes" for error in self.errors):
                self.error(
                    path,
                    "too_many_nodes",
                    f"Rule AST exceeds the {LIMITS['maxNodes']} node limit.",
                )
            return False
        return True


def _valid_scalar_for_fact(value: Any, fact_type: str) -> bool:
    if fact_type in ("string", "enum"):
        return isinstance(value, str)
    if fact_type == "number":
        return is_number(value)
    if fact_type == "decimal":
        return is_decimal_wire(value)
    if fact_type == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    return False


def _validate_string(
    value: Any,
    path: str,
    state: _State,
    *,
    required: bool = True,
    identifier: bool = False,
) -> str | None:
    if not isinstance(value, str) or (required and not value.strip()):
        state.error(path, "invalid_string", "A non-empty string is required.")
        return None
    if len(value) > LIMITS["maxStringLength"]:
        state.error(
            path,
            "string_too_long",
            f"String exceeds the {LIMITS['maxStringLength']} character limit.",
        )
        return None
    if identifier and ("__" in value or not _IDENTIFIER.fullmatch(value)):
        state.error(
            path,
            "invalid_identifier",
            "Identifier contains unsupported characters or traversal syntax.",
        )
        return None
    return value


def _validate_collection_size(value: list[Any], path: str, state: _State) -> bool:
    if len(value) > LIMITS["maxCollectionItems"]:
        state.error(
            path,
            "collection_too_large",
            f"Collection exceeds the {LIMITS['maxCollectionItems']} item limit.",
        )
        return False
    return True


def _validate_object_fields(
    raw: dict[Any, Any],
    allowed: set[str],
    path: str,
    state: _State,
) -> None:
    """Bound object fan-out and report only a bounded number of safe paths."""
    if len(raw) > LIMITS["maxObjectFields"]:
        state.error(
            path,
            "too_many_fields",
            f"Object exceeds the {LIMITS['maxObjectFields']} field limit.",
        )
    for index, key in enumerate(raw):
        if index >= LIMITS["maxObjectFields"]:
            break
        if not isinstance(key, str):
            state.error(path, "invalid_field_name", "Object field names must be strings.")
        elif len(key) > LIMITS["maxStringLength"]:
            state.error(
                path,
                "field_name_too_long",
                f"Field name exceeds the {LIMITS['maxStringLength']} character limit.",
            )
        elif key not in allowed:
            state.error(f"{path}.{key}", "unknown_field", "Field is not supported.")


def _reference_is_allowed(
    kind: str, value: str, path: str, state: _State
) -> bool:
    allowed = state.references.get(kind)
    if allowed is None or value in allowed:
        return True
    state.error(
        path,
        f"unknown_{kind[:-1]}_reference",
        f"Referenced {kind[:-1]} '{value}' is not available.",
    )
    return False


def _validate_leaf(raw: dict[str, Any], path: str, state: _State) -> LeafCondition | None:
    allowed = {"fact", "op", "value"}
    _validate_object_fields(raw, allowed, path, state)

    fact_name = _validate_string(
        raw.get("fact"), f"{path}.fact", state, identifier=True
    )
    op = _validate_string(raw.get("op"), f"{path}.op", state, identifier=True)
    fact = FACT_BY_NAME.get(fact_name or "")
    if fact_name is not None and fact is None:
        state.error(f"{path}.fact", "unknown_fact", f"Unknown fact '{fact_name}'.")
    if fact is not None and state.gate not in fact.gates:
        state.error(
            f"{path}.fact",
            "fact_unavailable_at_gate",
            f"Fact '{fact.name}' is not available at gate '{state.gate}'.",
        )

    operator = OPERATOR_BY_NAME.get(op or "")
    if op is not None and operator is None:
        state.error(f"{path}.op", "unknown_operator", f"Unknown operator '{op}'.")
    if fact is not None and operator is not None and op not in fact.operators:
        state.error(
            f"{path}.op",
            "operator_type_mismatch",
            f"Operator '{op}' is not compatible with fact type '{fact.type}'.",
        )

    value = raw.get("value", _MISSING)
    expects_value = (
        operator is not None and operator["value"]["kind"] != "none"
    )
    if expects_value and value is _MISSING:
        state.error(f"{path}.value", "missing_value", "Operator requires a value.")
    elif not expects_value and operator is not None and value is not _MISSING:
        state.error(
            f"{path}.value",
            "unexpected_value",
            "Operator does not accept a value.",
        )
    elif expects_value and fact is not None:
        value = _validate_operator_value(value, fact, op or "", path, state)

    if fact is None or operator is None:
        return None
    return LeafCondition(
        fact=fact.name,
        op=op,
        value=None if value is _MISSING else value,
        has_value=value is not _MISSING,
    )


def _validate_operator_value(
    value: Any, fact: Any, op: str, path: str, state: _State
) -> Any:
    fact_type = fact.type
    value_path = f"{path}.value"
    if op in ("in", "contains_any"):
        if not isinstance(value, list):
            state.error(value_path, "invalid_value_type", "A JSON array is required.")
            return value
        if not _validate_collection_size(value, value_path, state):
            return value
        if not value:
            state.error(value_path, "empty_collection", "Collection must not be empty.")
            return value
        items_valid = True
        for index, item in enumerate(value):
            item_path = f"{value_path}[{index}]"
            if not isinstance(item, str):
                state.error(
                    item_path,
                    "invalid_value_type",
                    "Every collection item must be a string.",
                )
                items_valid = False
            elif len(item) > LIMITS["maxStringLength"]:
                state.error(
                    item_path,
                    "string_too_long",
                    (
                        "Collection item exceeds the "
                        f"{LIMITS['maxStringLength']} character limit."
                    ),
                )
                items_valid = False
        if (
            items_valid
            and fact.enum_values
            and any(item not in fact.enum_values for item in value)
        ):
            state.error(
                value_path,
                "invalid_enum_value",
                f"Value must be one of: {', '.join(fact.enum_values)}.",
            )
        return value
    if op == "between":
        if not isinstance(value, list) or len(value) != 2:
            state.error(
                value_path,
                "invalid_value_type",
                f"Between requires a two-value array of '{fact_type}' values.",
            )
            return value
        if fact_type == "decimal":
            canonical_bounds: list[str] = []
            decimal_bounds: list[Decimal] = []
            for index, item in enumerate(value):
                try:
                    canonical, parsed = parse_decimal_wire(item)
                except DecimalWireError as error:
                    state.error(f"{value_path}[{index}]", error.code, str(error))
                else:
                    canonical_bounds.append(canonical)
                    decimal_bounds.append(parsed)
            if len(canonical_bounds) != 2:
                return value
            if decimal_bounds[0] > decimal_bounds[1]:
                state.error(
                    value_path,
                    "invalid_range",
                    "Between lower bound must not exceed upper bound.",
                )
            return canonical_bounds
        if not all(_valid_scalar_for_fact(item, fact_type) for item in value):
            state.error(
                value_path,
                "invalid_value_type",
                f"Between requires a two-value array of '{fact_type}' values.",
            )
        elif value[0] > value[1]:
            state.error(
                value_path,
                "invalid_range",
                "Between lower bound must not exceed upper bound.",
            )
        return value
    if op == "contains" and fact_type == "collection":
        if not isinstance(value, str):
            state.error(value_path, "invalid_value_type", "A string value is required.")
        return value
    if fact_type == "decimal":
        try:
            canonical, _ = parse_decimal_wire(value)
        except DecimalWireError as error:
            state.error(value_path, error.code, str(error))
            return value
        return canonical
    if not _valid_scalar_for_fact(value, fact_type):
        state.error(
            value_path,
            "invalid_value_type",
            f"Value must match fact type '{fact_type}'.",
        )
    elif fact.enum_values and value not in fact.enum_values:
        state.error(
            value_path,
            "invalid_enum_value",
            f"Value must be one of: {', '.join(fact.enum_values)}.",
        )
    elif isinstance(value, str) and len(value) > LIMITS["maxStringLength"]:
        state.error(
            value_path,
            "string_too_long",
            f"String exceeds the {LIMITS['maxStringLength']} character limit.",
        )
    return value


def _validate_condition(
    raw: Any, path: str, state: _State, depth: int
) -> LeafCondition | GroupCondition | None:
    if not state.consume_node(path):
        return None
    if not isinstance(raw, dict):
        state.error(path, "invalid_condition", "Condition must be a JSON object.")
        return None
    if len(raw) > LIMITS["maxObjectFields"]:
        state.error(
            path,
            "too_many_fields",
            f"Object exceeds the {LIMITS['maxObjectFields']} field limit.",
        )

    group_keys = [key for key in ("all", "any", "not") if key in raw]
    is_leaf = "fact" in raw or "op" in raw
    if len(group_keys) + int(is_leaf) != 1:
        state.error(
            path,
            "invalid_condition",
            "Condition must contain exactly one leaf, all, any, or not expression.",
        )
        return None
    if is_leaf:
        return _validate_leaf(raw, path, state)

    kind = group_keys[0]
    if depth > LIMITS["maxDepth"]:
        state.error(
            path,
            "max_depth_exceeded",
            f"Condition nesting exceeds the {LIMITS['maxDepth']} level limit.",
        )
        return None
    _validate_object_fields(raw, {kind}, path, state)
    child_raw = raw[kind]
    if kind == "not":
        child = _validate_condition(child_raw, f"{path}.not", state, depth + 1)
        return (
            GroupCondition(kind="not", conditions=(child,))
            if child is not None
            else None
        )
    if not isinstance(child_raw, list) or not child_raw:
        state.error(
            f"{path}.{kind}",
            "invalid_group",
            f"'{kind}' must be a non-empty JSON array.",
        )
        return None
    if not _validate_collection_size(child_raw, f"{path}.{kind}", state):
        child_raw = child_raw[: LIMITS["maxCollectionItems"]]
    children = []
    for index, item in enumerate(child_raw):
        child = _validate_condition(
            item, f"{path}.{kind}[{index}]", state, depth + 1
        )
        if child is not None:
            children.append(child)
    return (
        GroupCondition(kind=kind, conditions=tuple(children))
        if len(children) == len(child_raw)
        else None
    )


def _validate_action(
    raw: Any, path: str, state: _State
) -> dict[str, Any] | None:
    if not state.consume_node(path):
        return None
    if not isinstance(raw, dict):
        state.error(path, "invalid_action", "Action must be a JSON object.")
        return None
    action_name = _validate_string(
        raw.get("action"), f"{path}.action", state, identifier=True
    )
    spec = ACTION_BY_NAME.get(action_name or "")
    if action_name is not None and spec is None:
        state.error(
            f"{path}.action", "unknown_action", f"Unknown action '{action_name}'."
        )
        return None
    if spec is None:
        return None

    parameters = {parameter["name"]: parameter for parameter in spec["parameters"]}
    _validate_object_fields(raw, {"action", *parameters}, path, state)

    canonical: dict[str, Any] = {"action": action_name}
    for name, parameter in parameters.items():
        value = raw.get(name, _MISSING)
        parameter_path = f"{path}.{name}"
        if value is _MISSING:
            if parameter["required"]:
                state.error(
                    parameter_path,
                    "missing_action_parameter",
                    f"Action '{action_name}' requires '{name}'.",
                )
            continue
        if parameter["type"] in ("string", "enum"):
            valid = _validate_string(
                value,
                parameter_path,
                state,
                identifier=name in ("skill",),
            )
            if valid is not None and name == "role" and valid not in _ROLE_VALUES:
                state.error(
                    parameter_path,
                    "invalid_role",
                    f"Unsupported role '{valid}'.",
                )
            elif (
                valid is not None
                and name == "role"
                and not _reference_is_allowed("roles", valid, parameter_path, state)
            ):
                pass
            elif (
                valid is not None
                and name == "skill"
                and not _reference_is_allowed("skills", valid, parameter_path, state)
            ):
                pass
            elif valid is not None:
                canonical[name] = valid
        elif parameter["type"] == "collection":
            if not isinstance(value, list):
                state.error(
                    parameter_path,
                    "invalid_action_parameter",
                    "A JSON array is required.",
                )
            elif _validate_collection_size(value, parameter_path, state):
                items: list[str] = []
                for index, item in enumerate(value):
                    valid = _validate_string(
                        item,
                        f"{parameter_path}[{index}]",
                        state,
                        identifier=name in ("tools", "facts"),
                    )
                    reference_ok = True
                    if valid is not None and name == "facts":
                        if valid not in FACT_BY_NAME:
                            state.error(
                                f"{parameter_path}[{index}]",
                                "unknown_fact_reference",
                                f"Referenced fact '{valid}' is not available.",
                            )
                            reference_ok = False
                        elif not _reference_is_allowed(
                            "facts", valid, f"{parameter_path}[{index}]", state
                        ):
                            reference_ok = False
                    elif valid is not None and name == "tools":
                        reference_ok = _reference_is_allowed(
                            "tools", valid, f"{parameter_path}[{index}]", state
                        )
                    if valid is not None and reference_ok:
                        items.append(valid)
                if not items and parameter["required"]:
                    state.error(
                        parameter_path,
                        "empty_collection",
                        "Collection must not be empty.",
                    )
                if len(items) == len(value):
                    canonical[name] = items
    return canonical


def _validate_actions(
    raw: Any, path: str, state: _State, *, default_unknown: bool = False
) -> tuple[dict[str, Any], ...] | None:
    if raw is _MISSING and default_unknown:
        if not state.consume_node(path):
            return None
        return (
            {
                "action": "deny",
                "reason": "required fact is missing, invalid, or unavailable",
            },
        )
    if not isinstance(raw, list) or not raw:
        state.error(path, "invalid_actions", "A non-empty action array is required.")
        return None
    if len(raw) > LIMITS["maxActionsPerRule"]:
        state.error(
            path,
            "too_many_actions",
            f"Rule exceeds the {LIMITS['maxActionsPerRule']} action limit.",
        )
        raw = raw[: LIMITS["maxActionsPerRule"]]
    actions = []
    for index, item in enumerate(raw):
        action = _validate_action(item, f"{path}[{index}]", state)
        if action is not None:
            actions.append(action)
    return tuple(actions) if len(actions) == len(raw) else None


def _validate_rule(raw: Any, path: str, state: _State) -> CanonicalRule | None:
    if not state.consume_node(path):
        return None
    if not isinstance(raw, dict):
        state.error(path, "invalid_rule", "Rule must be a JSON object.")
        return None
    allowed = {"id", "name", "enabled", "priority", "when", "then", "onUnknown"}
    _validate_object_fields(raw, allowed, path, state)

    rule_id = _validate_string(
        raw.get("id"), f"{path}.id", state, identifier=True
    )
    name = _validate_string(raw.get("name"), f"{path}.name", state)
    enabled = raw.get("enabled", True)
    if not isinstance(enabled, bool):
        state.error(f"{path}.enabled", "invalid_boolean", "A boolean is required.")
    priority = raw.get("priority", 0)
    if (
        not isinstance(priority, int)
        or isinstance(priority, bool)
        or not -10000 <= priority <= 10000
    ):
        state.error(
            f"{path}.priority",
            "invalid_priority",
            "Priority must be an integer from -10000 through 10000.",
        )
    condition = _validate_condition(raw.get("when"), f"{path}.when", state, 1)
    actions = _validate_actions(raw.get("then"), f"{path}.then", state)
    unknown_actions = _validate_actions(
        raw.get("onUnknown", _MISSING),
        f"{path}.onUnknown",
        state,
        default_unknown=True,
    )
    if unknown_actions is not None and not any(
        action["action"]
        in {
            "deny",
            "require_approval",
            "escalate",
            "ask_user",
            "require_context",
        }
        for action in unknown_actions
    ):
        state.error(
            f"{path}.onUnknown",
            "unsafe_unknown_policy",
            "Unknown handling must contain an explicit fail-closed decision.",
        )
    if (
        rule_id is None
        or name is None
        or not isinstance(enabled, bool)
        or not isinstance(priority, int)
        or isinstance(priority, bool)
        or condition is None
        or actions is None
        or unknown_actions is None
    ):
        return None
    return CanonicalRule(
        id=rule_id,
        name=name,
        enabled=enabled,
        priority=priority,
        when=condition,
        then=actions,
        on_unknown=unknown_actions,
    )


def validate_rule_set(
    gate: str,
    raw: Any,
    reference_catalog: dict[str, list[str] | None] | None = None,
) -> ValidationOutcome:
    """Validate and canonicalize an untrusted JSON RuleSet without side effects."""
    references = {
        kind: frozenset(values)
        for kind, values in (reference_catalog or {}).items()
        if values is not None
    }
    state = _State(gate=gate, references=references)
    if gate not in GATES:
        state.error(
            "$.gate",
            "unknown_gate",
            f"Gate must be one of: {', '.join(GATES)}.",
        )
    if not isinstance(raw, dict):
        state.error("$.ruleSet", "invalid_rule_set", "RuleSet must be a JSON object.")
        return ValidationOutcome(valid=False, errors=tuple(state.errors))
    _validate_object_fields(raw, {"version", "rules"}, "$.ruleSet", state)
    version = raw.get("version")
    if (
        not isinstance(version, int)
        or isinstance(version, bool)
        or version != RULE_SET_VERSION
    ):
        state.error(
            "$.ruleSet.version",
            "unsupported_version",
            f"Only RuleSet version {RULE_SET_VERSION} is supported.",
        )
    rules_raw = raw.get("rules")
    if not isinstance(rules_raw, list):
        state.error(
            "$.ruleSet.rules", "invalid_rules", "Rules must be a JSON array."
        )
        return ValidationOutcome(valid=False, errors=tuple(state.errors))
    if len(rules_raw) > LIMITS["maxRules"]:
        state.error(
            "$.ruleSet.rules",
            "too_many_rules",
            f"RuleSet exceeds the {LIMITS['maxRules']} rule limit.",
        )
        rules_raw = rules_raw[: LIMITS["maxRules"]]

    rules = []
    ids: dict[str, int] = {}
    for index, item in enumerate(rules_raw):
        rule = _validate_rule(item, f"$.ruleSet.rules[{index}]", state)
        if rule is not None:
            if rule.id in ids:
                state.error(
                    f"$.ruleSet.rules[{index}].id",
                    "duplicate_rule_id",
                    f"Rule id duplicates rules[{ids[rule.id]}].",
                )
            else:
                ids[rule.id] = index
                rules.append(rule)

    if state.errors:
        return ValidationOutcome(valid=False, errors=tuple(state.errors))
    rules.sort(key=lambda rule: (-rule.priority, rule.id))
    return ValidationOutcome(
        valid=True,
        canonical_rule_set=CanonicalRuleSet(version=1, rules=tuple(rules)),
    )


def condition_to_json(condition: LeafCondition | GroupCondition) -> dict[str, Any]:
    if isinstance(condition, LeafCondition):
        result: dict[str, Any] = {"fact": condition.fact, "op": condition.op}
        if condition.has_value:
            result["value"] = condition.value
        return result
    if condition.kind == "not":
        return {"not": condition_to_json(condition.conditions[0])}
    return {
        condition.kind: [condition_to_json(child) for child in condition.conditions]
    }


def canonical_to_json(rule_set: CanonicalRuleSet) -> dict[str, Any]:
    return {
        "version": rule_set.version,
        "rules": [
            {
                "id": rule.id,
                "name": rule.name,
                "enabled": rule.enabled,
                "priority": rule.priority,
                "when": condition_to_json(rule.when),
                "then": [dict(action) for action in rule.then],
                "onUnknown": [dict(action) for action in rule.on_unknown],
            }
            for rule in rule_set.rules
        ],
    }


def validate_simulation_facts(facts: dict[str, Any]) -> tuple[RuleError, ...]:
    """Apply catalog/resource bounds to simulator facts without type coercion.

    A wrong runtime type is intentionally left to the evaluator, where it
    becomes ``unknown`` and follows the rule's explicit fail-closed policy.
    This precheck only rejects unknown references and resource-amplifying values.
    """
    state = _State(gate="", error_root="$.facts")
    if len(facts) > LIMITS["maxFacts"]:
        return (
            RuleError(
                path="$.facts",
                code="too_many_facts",
                message=f"Facts exceed the {LIMITS['maxFacts']} item limit.",
            ),
        )
    for index, (name, value) in enumerate(facts.items()):
        path = f"$.facts[{index}]"
        if len(name) > LIMITS["maxStringLength"]:
            state.error(
                path,
                "fact_name_too_long",
                f"Fact name exceeds the {LIMITS['maxStringLength']} character limit.",
            )
            continue
        if name not in FACT_BY_NAME:
            state.error(path, "unknown_fact", "Fact is not present in the catalog.")
            continue
        if isinstance(value, str) and len(value) > LIMITS["maxStringLength"]:
            state.error(
                path,
                "string_too_long",
                (
                    f"String exceeds the {LIMITS['maxStringLength']} "
                    "character limit."
                ),
            )
        elif isinstance(value, list):
            if len(value) > LIMITS["maxCollectionItems"]:
                state.error(
                    path,
                    "collection_too_large",
                    (
                        f"Collection exceeds the "
                        f"{LIMITS['maxCollectionItems']} item limit."
                    ),
                )
            elif any(
                isinstance(item, str)
                and len(item) > LIMITS["maxStringLength"]
                for item in value
            ):
                state.error(
                    path,
                    "string_too_long",
                    (
                        "Collection item exceeds the "
                        f"{LIMITS['maxStringLength']} character limit."
                    ),
                )
    return tuple(state.errors)
