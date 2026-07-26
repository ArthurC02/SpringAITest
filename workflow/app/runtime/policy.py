from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Iterable, Literal
from app.settings import settings

from app.business_rules.evaluator import evaluate
from app.business_rules.validator import validate_rule_set
from app.runtime.facts import (
    ProposedAction,
    RuntimeFactEnvelope,
    action_envelopes,
    materialize_trusted_facts,
)


class PolicyError(RuntimeError):
    """Persisted policy could not be evaluated safely."""


PolicyOutcome = Literal["continue", "blocked", "waiting_input", "waiting_approval"]


@dataclass(frozen=True)
class PolicyDecision:
    outcome: PolicyOutcome
    code: str
    question: str = ""
    required_role: str = ""
    routed_skill: str = ""
    allowed_read_tools: frozenset[str] | None = None
    audit_tags: tuple[str, ...] = ()
    response_policies: tuple[str, ...] = ()
    summary: dict[str, Any] | None = None


class PreActionPolicy:
    """D2's validator/evaluator bound to D3's trusted fact envelopes."""

    def __init__(
        self,
        raw_rule_set: dict[str, Any],
        *,
        pinned_skills: Iterable[str],
        registered_tools: Iterable[str],
        roles: Iterable[str],
    ):
        self._pinned_skills = frozenset(pinned_skills)
        self._registered_tools = frozenset(registered_tools)
        validated = validate_rule_set(
            "pre-action",
            raw_rule_set,
            {
                "skills": sorted(self._pinned_skills),
                "tools": sorted(self._registered_tools),
                "roles": sorted(set(roles)),
                "facts": None,
            },
        )
        if not validated.valid or validated.canonical_rule_set is None:
            raise PolicyError("persisted business rules failed runtime validation")
        self._rules = validated.canonical_rule_set

    def decide(
        self,
        action: ProposedAction,
        base_facts: Iterable[RuntimeFactEnvelope],
    ) -> PolicyDecision:
        facts = materialize_trusted_facts(
            "pre-action", [*base_facts, *action_envelopes(action)]
        )
        try:
            result = evaluate("pre-action", self._rules, facts)
        except Exception as exc:
            raise PolicyError("business rule evaluation failed") from exc

        raw_decision = result["decision"]
        selected = raw_decision.get("outcome", "continue")
        selected_action = raw_decision.get("action") or {}
        actions = result.get("actions") or []
        read_sets = [
            frozenset(str(tool) for tool in item.get("tools", []))
            for item in actions
            if item.get("action") == "allow_read_tool"
        ]
        allowed_read_tools = (
            frozenset.intersection(*read_sets) if read_sets else None
        )
        audit_tags = tuple(
            str(item["tag"])
            for item in actions
            if item.get("action") == "add_audit_tag" and "tag" in item
        )
        response_policies = tuple(
            str(item["policy"])
            for item in actions
            if item.get("action") == "set_response_policy" and "policy" in item
        )
        summary = {
            "outcome": selected,
            "source_rule_id": raw_decision.get("sourceRuleId"),
            "from_unknown": bool(raw_decision.get("fromUnknown", False)),
            "matched_rule_ids": list(result.get("matchedRules") or []),
        }

        if selected == "continue":
            return PolicyDecision(
                outcome="continue",
                code="allowed",
                allowed_read_tools=allowed_read_tools,
                audit_tags=audit_tags,
                response_policies=response_policies,
                summary=summary,
            )
        if selected in {"ask_user", "require_context"}:
            question = str(
                selected_action.get("question")
                or "請補充完成此操作所需的必要資訊。"
            )
            return PolicyDecision(
                outcome="waiting_input",
                code=selected,
                question=question,
                allowed_read_tools=allowed_read_tools,
                audit_tags=audit_tags,
                response_policies=response_policies,
                summary=summary,
            )
        if selected == "route_to_skill":
            skill = str(selected_action.get("skill") or "")
            # Currently unreachable, kept as defence in depth. `__init__` (`:50`)
            # hands the very same `pinned_skills` to the validator as the
            # `skills` referenceCatalog, and `validator._reference_is_allowed`
            # (`app/business_rules/validator.py:170`) turns any unpinned
            # `route_to_skill` into an `unknown_skill_reference` error, so
            # construction already raises PolicyError and `decide()` never sees
            # one. It becomes reachable as soon as the validator's catalogue and
            # the runtime pin list come from different sources (e.g. validating
            # against a tenant catalogue while pinning from the snapshot).
            if skill not in self._pinned_skills:
                return PolicyDecision(
                    outcome="blocked",
                    code="unpinned_rule_skill",
                    summary=summary,
                )
            return PolicyDecision(
                outcome="continue",
                code="route_to_skill",
                routed_skill=skill,
                allowed_read_tools=allowed_read_tools,
                audit_tags=audit_tags,
                response_policies=response_policies,
                summary=summary,
            )
        if selected == "require_approval":
            if settings.agent_write_tools_enabled:
                role = str(selected_action.get("role") or "")
                if role:
                    return PolicyDecision(
                        outcome="waiting_approval",
                        code="require_approval",
                        required_role=role,
                        summary=summary,
                    )
            return PolicyDecision(
                outcome="blocked",
                code="approval_not_enabled",
                summary=summary,
            )
        if selected in {"deny", "escalate"}:
            return PolicyDecision(
                outcome="blocked", code=f"rule_{selected}", summary=summary
            )
        return PolicyDecision(
            outcome="blocked", code="unknown_rule_decision", summary=summary
        )
