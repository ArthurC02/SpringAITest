from __future__ import annotations

import asyncio
import hashlib
import json
import math
from types import UnionType
from typing import get_args, get_origin
from dataclasses import dataclass
from typing import Any

from app.engine import tool_registry
from app.engine.tool_registry import ToolContext, ToolSpec
from app.runtime.artifacts import LoadedSkillArtifact
from app.runtime.models import DirectAgentExecutionSnapshot, canonical_json_bytes
from app.settings import settings

MAX_TOOL_ARGUMENT_BYTES = 32_768
MAX_TOOL_OBSERVATION_CHARS = 16_384


class DirectToolDenied(RuntimeError):
    pass


class ToolArgumentsInvalid(DirectToolDenied):
    pass


@dataclass(frozen=True)
class ToolObservation:
    content: str
    fingerprint: str
    verified_context: dict[str, Any] | None = None


def effective_knowledge_sources(
    snapshot: DirectAgentExecutionSnapshot,
) -> frozenset[str]:
    return frozenset(snapshot.agent.knowledge_sources) & frozenset(
        snapshot.caller.knowledge_source_grants
    )


def effective_tool_names(
    snapshot: DirectAgentExecutionSnapshot,
    *,
    artifact: LoadedSkillArtifact | None,
    rule_tools: frozenset[str] | None,
    deps: Any,
) -> frozenset[str]:
    names = (
        frozenset(snapshot.agent.allowed_tools)
        & frozenset(snapshot.caller.tool_grants)
    )
    if artifact is not None:
        names &= artifact.uses_tools
    if rule_tools is not None:
        names &= rule_tools
    safe = {
        spec.name
        for spec in tool_registry.all_specs()
        if spec.risk in {"low", "read"} and _runtime_available(spec, snapshot, deps)
    }
    if settings.agent_write_tools_enabled:
        configured_tools = {x.strip() for x in settings.agent_write_tools_allowlist.split(",") if x.strip()}
        configured_tenants = {x.strip() for x in settings.agent_write_tools_tenant_allowlist.split(",") if x.strip()}
        safe |= {spec.name for spec in tool_registry.all_specs() if spec.risk == "write" and spec.name in configured_tools and snapshot.caller.tenant_id in configured_tenants}
    return names & safe


def effective_specs(
    snapshot: DirectAgentExecutionSnapshot,
    *,
    artifact: LoadedSkillArtifact | None,
    rule_tools: frozenset[str] | None,
    deps: Any,
) -> list[ToolSpec]:
    allowed = effective_tool_names(
        snapshot, artifact=artifact, rule_tools=rule_tools, deps=deps
    )
    return [spec for spec in tool_registry.all_specs() if spec.name in allowed]


def _runtime_available(
    spec: ToolSpec, snapshot: DirectAgentExecutionSnapshot, deps: Any
) -> bool:
    if spec.name != "backend.retrieval_search":
        return True
    if not effective_knowledge_sources(snapshot):
        return False
    searcher = (getattr(deps, "searchers", None) or {}).get("vector")
    return searcher is not None


async def invoke_direct_tool(
    *,
    name: str,
    arguments: dict[str, Any],
    snapshot: DirectAgentExecutionSnapshot,
    artifact: LoadedSkillArtifact | None,
    rule_tools: frozenset[str] | None,
    deps: Any,
    timeout_seconds: float,
) -> ToolObservation:
    allowed = effective_tool_names(
        snapshot, artifact=artifact, rule_tools=rule_tools, deps=deps
    )
    if name not in allowed:
        raise DirectToolDenied("tool is outside the effective read-only authority")
    spec = tool_registry.get(name)
    if spec is None or spec.risk not in {"low", "read"}:
        raise DirectToolDenied("tool is not a registered read-only capability")
    _validate_arguments(spec, arguments)
    fingerprint = tool_fingerprint(name, arguments)
    ctx = ToolContext(
        tenant_id=snapshot.caller.tenant_id,
        user_id=snapshot.caller.user_id,
        role=snapshot.caller.role,
        deps=deps,
        run_id=snapshot.run_id,
        agent_id=snapshot.agent.id,
        agent_revision=snapshot.agent.revision,
        knowledge_sources=effective_knowledge_sources(snapshot),
        enforce_data_scope=True,
    )
    try:
        async with asyncio.timeout(timeout_seconds):
            result = await tool_registry.invoke(name, ctx, allowed, arguments)
    except TimeoutError as exc:
        raise DirectToolDenied("tool exceeded its bounded timeout") from exc
    verified_context = None
    if name == "backend.retrieval_search" and isinstance(result, list):
        verified_context = {
            "source_count": min(len(result), 100),
            "source_types": sorted(
                {
                    str(item.get("source_type") or item.get("type") or "document")[
                        :128
                    ]
                    for item in result[:100]
                    if isinstance(item, dict)
                }
            )[:100],
            "producer": "backend_retrieval",
            "tool_event": fingerprint,
            "run_id": snapshot.run_id,
            "snapshot_hash": snapshot.snapshot_hash,
            "knowledge_sources": sorted(effective_knowledge_sources(snapshot))[:100],
        }
    return ToolObservation(
        content=_bounded_observation(result),
        fingerprint=fingerprint,
        verified_context=verified_context,
    )


async def invoke_approved_write_tool(
    *, name: str, arguments: dict[str, Any], snapshot: DirectAgentExecutionSnapshot,
    artifact: LoadedSkillArtifact | None, rule_tools: frozenset[str] | None,
    deps: Any, timeout_seconds: float, effect_id: str,
) -> ToolObservation:
    allowed = effective_tool_names(snapshot, artifact=artifact, rule_tools=rule_tools, deps=deps)
    spec = tool_registry.get(name)
    if name not in allowed or spec is None or spec.risk != "write":
        raise DirectToolDenied("tool is not an approved write capability")
    _validate_arguments(spec, arguments)
    ctx = ToolContext(tenant_id=snapshot.caller.tenant_id,user_id=snapshot.caller.user_id,role=snapshot.caller.role,deps=deps,run_id=snapshot.run_id,agent_id=snapshot.agent.id,agent_revision=snapshot.agent.revision,knowledge_sources=effective_knowledge_sources(snapshot),enforce_data_scope=True,effect_id=effect_id)
    try:
        async with asyncio.timeout(timeout_seconds): result = await tool_registry.invoke(name, ctx, allowed, arguments)
    except TimeoutError as exc: raise DirectToolDenied("tool exceeded its bounded timeout") from exc
    return ToolObservation(content=_bounded_observation(result), fingerprint=tool_fingerprint(name, arguments))


def tool_fingerprint(name: str, arguments: dict[str, Any]) -> str:
    return hashlib.sha256(
        name.encode("utf-8") + b"\0" + canonical_json_bytes(arguments)
    ).hexdigest()


def _validate_arguments(spec: ToolSpec, arguments: dict[str, Any]) -> None:
    if not isinstance(arguments, dict):
        raise ToolArgumentsInvalid("tool arguments must be an object")
    try:
        size = len(canonical_json_bytes(arguments))
    except (TypeError, ValueError) as exc:
        raise ToolArgumentsInvalid("tool arguments must be finite JSON values") from exc
    if size > MAX_TOOL_ARGUMENT_BYTES:
        raise ToolArgumentsInvalid("tool arguments exceed the runtime limit")
    if set(arguments) - set(spec.args_schema):
        raise ToolArgumentsInvalid("tool arguments contain undeclared fields")
    if spec.required_args - set(arguments):
        raise ToolArgumentsInvalid("tool arguments are missing required fields")
    for key, value in arguments.items():
        expected = spec.args_schema[key]
        if value is None:
            if key not in spec.nullable_args:
                raise ToolArgumentsInvalid(f"tool argument {key} cannot be null")
            continue
        expected_types = tuple(
            item for item in get_args(expected) if item is not type(None)
        ) or (get_origin(expected) or expected,)
        if any(item in {int, float} for item in expected_types) and isinstance(
            value, bool
        ):
            raise ToolArgumentsInvalid(f"tool argument {key} has an invalid type")
        if float in expected_types and isinstance(value, (int, float)):
            if not math.isfinite(value):
                raise ToolArgumentsInvalid(f"tool argument {key} has an invalid type")
            continue
        runtime_types = tuple(
            item
            for item in expected_types
            if item in {str, int, float, bool, list, dict}
        )
        if runtime_types and not isinstance(value, runtime_types):
            raise ToolArgumentsInvalid(f"tool argument {key} has an invalid type")


def _bounded_observation(value: Any) -> str:
    try:
        text = json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
            sort_keys=True,
            default=str,
        )
    except (TypeError, ValueError):
        text = json.dumps({"status": "unserializable_result"})
    if len(text) > MAX_TOOL_OBSERVATION_CHARS:
        return text[:MAX_TOOL_OBSERVATION_CHARS] + "…[truncated]"
    return text
