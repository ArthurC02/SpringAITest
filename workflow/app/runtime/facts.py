from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal
from typing import Any, Iterable

from app.business_rules.catalog import FACT_BY_NAME


@dataclass(frozen=True)
class RuntimeFactEnvelope:
    """A value plus the server-owned provenance required by the D2 catalog."""

    name: str
    value: Any
    producer: str
    provenance: str
    trust_tier: str
    source_ref: str = ""


@dataclass(frozen=True)
class ProposedAction:
    action_type: str
    tool_name: str = ""
    requested_tools: tuple[str, ...] = ()
    skill_name: str = ""
    skill_revision: int = 0
    skill_kind: str = ""
    # Amount is never inferred from arbitrary model arguments. A registered
    # adapter may provide a verified decimal envelope in a later phase.
    verified_amount: RuntimeFactEnvelope | None = None


def system_fact(name: str, value: Any, *, producer: str) -> RuntimeFactEnvelope:
    spec = FACT_BY_NAME[name]
    if spec.provenance != "system" or spec.trust_tier != "trusted":
        raise ValueError(f"{name} is not a trusted system fact")
    return RuntimeFactEnvelope(
        name=name,
        value=value,
        producer=producer,
        provenance="system",
        trust_tier="trusted",
    )


def verified_tool_fact(
    name: str, value: Any, *, producer: str, source_ref: str
) -> RuntimeFactEnvelope:
    spec = FACT_BY_NAME[name]
    if spec.provenance != "tool" or spec.trust_tier != "verified":
        raise ValueError(f"{name} is not a verified tool fact")
    return RuntimeFactEnvelope(
        name=name,
        value=value,
        producer=producer,
        provenance="tool",
        trust_tier="verified",
        source_ref=source_ref,
    )


def caller_envelopes(
    *, tenant_id: str, role: str, groups: Iterable[str], channel: str = "api"
) -> list[RuntimeFactEnvelope]:
    return [
        system_fact("caller.tenant_id", tenant_id, producer="request_context"),
        system_fact("caller.role", role, producer="request_context"),
        system_fact("caller.groups", list(groups), producer="execution_snapshot"),
        system_fact("request.channel", channel, producer="runtime_endpoint"),
    ]


def action_envelopes(action: ProposedAction) -> list[RuntimeFactEnvelope]:
    values = [
        system_fact("action.type", action.action_type, producer="runtime_router"),
        system_fact(
            "action.requested_tools",
            list(action.requested_tools),
            producer="runtime_router",
        ),
    ]
    if action.tool_name:
        values.append(
            system_fact(
                "action.tool_name", action.tool_name, producer="runtime_router"
            )
        )
    if action.skill_name:
        values.append(
            system_fact("skill.name", action.skill_name, producer="runtime_router")
        )
    amount = action.verified_amount
    if amount is not None:
        # action.amount's catalog provenance is currently system/trusted. Only a
        # registered adapter is allowed to construct this envelope; arbitrary
        # model args never reach this branch.
        if (
            amount.name == "action.amount"
            and amount.producer.startswith("adapter:")
            and isinstance(amount.value, (str, Decimal))
        ):
            values.append(amount)
    return values


def materialize_trusted_facts(
    gate: str, envelopes: Iterable[RuntimeFactEnvelope]
) -> dict[str, Any]:
    """Return only catalog-matching trusted/verified facts.

    Missing, mistyped provenance, untrusted, unavailable-at-gate, and
    conflicting values are omitted. The evaluator then produces ``unknown``
    and follows the rule's explicit ``onUnknown`` branch.
    """

    accepted: dict[str, RuntimeFactEnvelope] = {}
    conflicts: set[str] = set()
    for envelope in envelopes:
        spec = FACT_BY_NAME.get(envelope.name)
        if spec is None:
            continue
        if gate not in spec.gates:
            continue
        if envelope.trust_tier not in {"trusted", "verified"}:
            continue
        if (
            envelope.provenance != spec.provenance
            or envelope.trust_tier != spec.trust_tier
        ):
            continue
        existing = accepted.get(envelope.name)
        if existing is not None and existing.value != envelope.value:
            conflicts.add(envelope.name)
            continue
        accepted[envelope.name] = envelope
    return {
        name: envelope.value
        for name, envelope in accepted.items()
        if name not in conflicts
    }
