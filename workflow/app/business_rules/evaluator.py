"""Pure, deterministic three-valued Business Rule evaluator."""

from __future__ import annotations

from decimal import Decimal
from typing import Any

from app.business_rules.catalog import ACTION_BY_NAME, FACT_BY_NAME, GATES, LIMITS
from app.business_rules.decimal_value import (
    is_decimal_wire,
    is_number,
    parse_decimal_wire,
)
from app.business_rules.models import (
    CanonicalRuleSet,
    Condition,
    GroupCondition,
    LeafCondition,
    TruthValue,
)

_MISSING = object()


def _fact_value_is_valid(
    value: Any,
    fact_type: str,
    item_type: str | None,
    enum_values: tuple[str, ...],
) -> bool:
    if fact_type in ("string", "enum"):
        return (
            isinstance(value, str)
            and len(value) <= LIMITS["maxStringLength"]
            and (not enum_values or value in enum_values)
        )
    if fact_type == "number":
        return is_number(value)
    if fact_type == "decimal":
        return is_decimal_wire(value)
    if fact_type == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if fact_type == "boolean":
        return isinstance(value, bool)
    if fact_type == "collection":
        return (
            isinstance(value, list)
            and len(value) <= LIMITS["maxCollectionItems"]
            and (
                item_type != "string"
                or all(
                    isinstance(item, str)
                    and len(item) <= LIMITS["maxStringLength"]
                    for item in value
                )
            )
        )
    return False


def _leaf(
    condition: LeafCondition, facts: dict[str, Any], path: str, gate: str
) -> tuple[TruthValue, dict[str, Any]]:
    value = facts.get(condition.fact, _MISSING)
    trace: dict[str, Any] = {
        "path": path,
        "kind": "leaf",
        "fact": condition.fact,
        "operator": condition.op,
    }
    spec = FACT_BY_NAME[condition.fact]
    if gate not in GATES or gate not in spec.gates:
        trace.update(
            result=TruthValue.UNKNOWN.value,
            reason="fact_unavailable_at_gate",
        )
        return TruthValue.UNKNOWN, trace
    if value is not _MISSING and not _fact_value_is_valid(
        value, spec.type, spec.item_type, spec.enum_values
    ):
        trace.update(result=TruthValue.UNKNOWN.value, reason="invalid_fact_type")
        return TruthValue.UNKNOWN, trace
    if condition.op == "exists":
        result = TruthValue.FALSE if value is _MISSING else TruthValue.TRUE
        trace["result"] = result.value
        if value is _MISSING:
            trace["reason"] = "missing_fact"
        return result, trace
    if condition.op == "not_exists":
        result = TruthValue.TRUE if value is _MISSING else TruthValue.FALSE
        trace["result"] = result.value
        return result, trace
    if value is _MISSING:
        trace.update(result=TruthValue.UNKNOWN.value, reason="missing_fact")
        return TruthValue.UNKNOWN, trace

    expected = condition.value
    compared_value = value
    compared_expected = expected
    if spec.type == "decimal":
        _, compared_value = parse_decimal_wire(value)
        if condition.op == "between":
            compared_expected = tuple(
                parse_decimal_wire(item)[1] for item in expected
            )
        elif condition.op not in ("exists", "not_exists"):
            compared_expected = parse_decimal_wire(expected)[1]
    elif spec.type in ("number", "integer"):
        compared_value = Decimal(str(value))
        if condition.op == "between":
            compared_expected = tuple(Decimal(str(item)) for item in expected)
        elif condition.op not in ("exists", "not_exists"):
            compared_expected = Decimal(str(expected))
    op = condition.op
    if op == "eq":
        outcome = compared_value == compared_expected
    elif op == "neq":
        outcome = compared_value != compared_expected
    elif op == "in":
        outcome = value in expected
    elif op == "contains":
        outcome = expected in value
    elif op == "gt":
        outcome = compared_value > compared_expected
    elif op == "gte":
        outcome = compared_value >= compared_expected
    elif op == "lt":
        outcome = compared_value < compared_expected
    elif op == "lte":
        outcome = compared_value <= compared_expected
    elif op == "between":
        outcome = compared_expected[0] <= compared_value <= compared_expected[1]
    elif op == "is_true":
        outcome = value is True
    elif op == "is_false":
        outcome = value is False
    elif op == "contains_any":
        outcome = any(item in value for item in expected)
    elif op == "is_empty":
        outcome = len(value) == 0
    else:  # Canonical models can only be produced by the validator.
        trace.update(result=TruthValue.UNKNOWN.value, reason="invalid_operator")
        return TruthValue.UNKNOWN, trace
    result = TruthValue.TRUE if outcome else TruthValue.FALSE
    trace["result"] = result.value
    return result, trace


def _condition(
    condition: Condition, facts: dict[str, Any], path: str, gate: str
) -> tuple[TruthValue, dict[str, Any]]:
    if isinstance(condition, LeafCondition):
        return _leaf(condition, facts, path, gate)

    children: list[dict[str, Any]] = []
    results: list[TruthValue] = []
    for index, child in enumerate(condition.conditions):
        child_path = (
            f"{path}.not"
            if condition.kind == "not"
            else f"{path}.{condition.kind}[{index}]"
        )
        result, trace = _condition(child, facts, child_path, gate)
        results.append(result)
        children.append(trace)

    if condition.kind == "not":
        result = {
            TruthValue.TRUE: TruthValue.FALSE,
            TruthValue.FALSE: TruthValue.TRUE,
            TruthValue.UNKNOWN: TruthValue.UNKNOWN,
        }[results[0]]
    elif condition.kind == "all":
        if TruthValue.FALSE in results:
            result = TruthValue.FALSE
        elif TruthValue.UNKNOWN in results:
            result = TruthValue.UNKNOWN
        else:
            result = TruthValue.TRUE
    elif TruthValue.TRUE in results:
        result = TruthValue.TRUE
    elif TruthValue.UNKNOWN in results:
        result = TruthValue.UNKNOWN
    else:
        result = TruthValue.FALSE
    return result, {
        "path": path,
        "kind": condition.kind,
        "result": result.value,
        "children": children,
    }


def evaluate(
    gate: str, rule_set: CanonicalRuleSet, facts: dict[str, Any]
) -> dict[str, Any]:
    """Evaluate canonical rules.

    No imports in this module can perform I/O.  The only inputs are the gate,
    immutable canonical model, and caller-supplied fact values.
    """
    action_candidates: list[dict[str, Any]] = []
    matched_rules: list[str] = []
    rule_traces: list[dict[str, Any]] = []
    for rule in rule_set.rules:
        if not rule.enabled:
            rule_traces.append(
                {
                    "ruleId": rule.id,
                    "priority": rule.priority,
                    "status": "disabled",
                    "emittedActions": [],
                }
            )
            continue
        truth, condition_trace = _condition(
            rule.when, facts, f"$.ruleSet.rules[id={rule.id}].when", gate
        )
        if truth is TruthValue.TRUE:
            status = "matched"
            emitted = rule.then
            matched_rules.append(rule.id)
        elif truth is TruthValue.UNKNOWN:
            status = "unknown"
            emitted = rule.on_unknown
        else:
            status = "not_matched"
            emitted = ()

        emitted_actions: list[dict[str, Any]] = []
        for index, action in enumerate(emitted):
            candidate = {
                "ruleId": rule.id,
                "priority": rule.priority,
                "actionIndex": index,
                "fromUnknown": truth is TruthValue.UNKNOWN,
                "action": dict(action),
            }
            action_candidates.append(candidate)
            emitted_actions.append(dict(action))
        rule_traces.append(
            {
                "ruleId": rule.id,
                "priority": rule.priority,
                "status": status,
                "condition": condition_trace,
                "emittedActions": emitted_actions,
            }
        )

    def action_sort_key(candidate: dict[str, Any]) -> tuple[int, int, str, int]:
        action_name = candidate["action"]["action"]
        return (
            -ACTION_BY_NAME[action_name]["precedence"],
            -candidate["priority"],
            candidate["ruleId"],
            candidate["actionIndex"],
        )

    action_candidates.sort(key=action_sort_key)
    primary = next(
        (
            candidate
            for candidate in action_candidates
            if ACTION_BY_NAME[candidate["action"]["action"]]["decision"]
        ),
        None,
    )
    resolution = []
    for candidate in action_candidates:
        selected = candidate is primary
        resolution.append(
            {
                **candidate,
                "selected": selected,
                "reason": (
                    "highest_precedence"
                    if selected
                    else (
                        "modifier"
                        if not ACTION_BY_NAME[candidate["action"]["action"]]["decision"]
                        else "lower_precedence"
                    )
                ),
            }
        )

    if primary is None:
        decision = {"outcome": "continue", "action": None}
    else:
        decision = {
            "outcome": primary["action"]["action"],
            "action": primary["action"],
            "sourceRuleId": primary["ruleId"],
            "priority": primary["priority"],
            "fromUnknown": primary["fromUnknown"],
        }
    return {
        "decision": decision,
        "matchedRules": matched_rules,
        "actions": [candidate["action"] for candidate in action_candidates],
        "trace": {
            "gate": gate,
            "rules": rule_traces,
            "actionResolution": resolution,
        },
    }
