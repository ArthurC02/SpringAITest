from __future__ import annotations

import asyncio
import json
from dataclasses import dataclass
from typing import Any

from pydantic import ValidationError

# The compiler always appends this system-owned audit node. Importing it here
# keeps recovery workers self-contained instead of relying on FastAPI import
# order to populate the Node Registry.
from app.nodes.kbquery.nodes import audit_feedback as _audit_feedback  # noqa: F401
from app.engine import compiler, node_registry, tool_registry
from app.engine.harness import RUNTIME_AUTHORITY_KEYS
from app.engine.skill import (
    ENGINE_KEYS,
    RESERVED_KEYS,
    Skill,
    build_input_model,
    parse_step,
    resolve_node,
)
from app.runtime.artifacts import LoadedSkillArtifact
from app.runtime.models import DirectAgentExecutionSnapshot
from app.runtime.tool_boundary import (
    effective_knowledge_sources,
    effective_tool_names,
)

MAX_LEGACY_RESULT_CHARS = 16_384

# Reviewed read-only/pure node contracts. Retrieval nodes are intentionally
# absent: their legacy adapters search the tenant broadly and cannot enforce a
# D3 knowledge-source scope. `audit_feedback` is compiler-owned and replaced
# with an in-memory sink; authors cannot place it themselves.
SAFE_LEGACY_NODES = frozenset(
    {
        "answer_composer",
        "evidence_verification",
        "query_intake",
        "retrieval_planner",
    }
)


class LegacyFlowDenied(RuntimeError):
    pass


@dataclass(frozen=True)
class LegacyFlowResult:
    status: str
    content: str
    tool_calls_bound: int
    steps_bound: int


class _NoopAuditRepository:
    async def save(self, trail: Any) -> None:
        return None


class _RuntimeDeps:
    """Delegate read-only dependencies while replacing legacy audit output."""

    audit_repo = _NoopAuditRepository()

    def __init__(self, base: Any):
        self._base = base

    def __getattr__(self, name: str) -> Any:
        if self._base is None:
            raise AttributeError(name)
        return getattr(self._base, name)


async def invoke_pinned_legacy_flow(
    *,
    artifact: LoadedSkillArtifact,
    raw_input: dict[str, Any],
    snapshot: DirectAgentExecutionSnapshot,
    rule_tools: frozenset[str] | None,
    deps: Any,
    timeout_seconds: float,
    recursion_cap: int,
    remaining_tool_rounds: int,
) -> LegacyFlowResult:
    if artifact.kind != "flow":
        raise LegacyFlowDenied("artifact is not a legacy flow")
    if artifact.scripts_present:
        raise LegacyFlowDenied("legacy flow contains external code")
    runtime_deps = _RuntimeDeps(deps)
    effective = effective_tool_names(
        snapshot, artifact=artifact, rule_tools=rule_tools, deps=deps
    )
    _validate_steps(artifact.skill, artifact.skill.flow, effective, runtime_deps)
    tool_calls_bound = _tool_call_bound(artifact.skill.flow)
    # Currently unreachable, kept for the day tool steps are re-allowed.
    # `_validate_steps` above rejects every `tool` step (`:177`) and every node
    # with a non-empty `requires_tools` (`:182`), which are the only two terms
    # `_tool_call_bound` (`:220`) can add, so it is always 0 here; the caller
    # also clamps `remaining_tool_rounds` to >= 0 (`app/runtime/graph.py:607`).
    # Relaxing either rejection in `_validate_steps` re-arms this guard.
    if tool_calls_bound > remaining_tool_rounds:
        raise LegacyFlowDenied("legacy flow exceeds the remaining tool budget")
    cleaned = {
        key: value
        for key, value in raw_input.items()
        if key not in RESERVED_KEYS
        and key not in ENGINE_KEYS
        and not str(key).startswith("__")
    }
    input_model = build_input_model(artifact.skill)
    if input_model is not None:
        try:
            input_model.model_validate(cleaned)
        except ValidationError as exc:
            raise LegacyFlowDenied("legacy flow input failed its pinned schema") from exc
    state = {
        **cleaned,
        "tenant_id": snapshot.caller.tenant_id,
        "user_id": snapshot.caller.user_id,
        "role": snapshot.caller.role,
        "run_id": snapshot.run_id,
        "agent_id": snapshot.agent.id,
        "agent_revision": snapshot.agent.revision,
        "knowledge_sources": sorted(effective_knowledge_sources(snapshot)),
        "enforce_data_scope": True,
    }
    try:
        graph = compiler.compile(artifact.skill, runtime_deps)
        limit = max(2, min(compiler.recursion_limit(artifact.skill), recursion_cap))
        async with asyncio.timeout(timeout_seconds):
            output = await graph.ainvoke(state, config={"recursion_limit": limit})
    except asyncio.CancelledError:
        raise
    except TimeoutError as exc:
        raise LegacyFlowDenied("legacy flow exceeded its bounded timeout") from exc
    except LegacyFlowDenied:
        raise
    except Exception as exc:
        raise LegacyFlowDenied("legacy flow execution failed safely") from exc
    if output.get("fatal_error"):
        raise LegacyFlowDenied("legacy flow reported a controlled failure")
    public = {
        key: value
        for key, value in output.items()
        if key not in RESERVED_KEYS
        and key not in ENGINE_KEYS
        and key not in RUNTIME_AUTHORITY_KEYS
        and key
        not in {
            "audit_trail",
            "issue_label",
            "regression_test_item",
            "improvement_backlog",
        }
        and not key.startswith("__")
    }
    return LegacyFlowResult(
        status="completed",
        content=_bounded_json(public),
        tool_calls_bound=tool_calls_bound,
        steps_bound=min(_step_bound(artifact.skill.flow) + 1, recursion_cap),
    )


def _validate_steps(
    skill: Skill,
    steps: list[dict[str, Any]],
    effective_tools: frozenset[str],
    deps: _RuntimeDeps,
) -> None:
    for step in steps:
        parsed = parse_step(step)
        if parsed is None:
            raise LegacyFlowDenied("legacy flow contains an invalid step")
        kind, body = parsed
        if kind == "script":
            raise LegacyFlowDenied("legacy flow scripts are disabled")
        if kind == "tool":
            raise LegacyFlowDenied("legacy flow tool actions are not safe")
        elif kind == "node":
            spec = resolve_node(body)
            if spec is None or spec.name not in SAFE_LEGACY_NODES:
                raise LegacyFlowDenied("legacy flow node is not deterministically safe")
            if spec.requires_tools or spec.dynamic_reads or any(
                dependency in {"llm", "model"} for dependency in spec.deps
            ):
                raise LegacyFlowDenied("legacy flow node has dynamic runtime authority")
            for dependency in spec.deps:
                if not hasattr(deps, dependency):
                    raise LegacyFlowDenied(
                        "legacy flow dependency is unavailable in this runtime"
                    )
        elif kind == "sequence":
            _validate_steps(skill, body, effective_tools, deps)
        elif kind == "branch":
            _validate_steps(skill, body.get("then") or [], effective_tools, deps)
            _validate_steps(skill, body.get("else") or [], effective_tools, deps)
        elif kind == "loop":
            maximum = body.get("max_iterations")
            if not isinstance(maximum, int) or maximum < 1:
                raise LegacyFlowDenied("legacy flow loop is not bounded")
            _validate_steps(skill, body.get("body") or [], effective_tools, deps)
        else:
            raise LegacyFlowDenied("legacy flow step kind is unsupported")


def _bounded_json(value: dict[str, Any]) -> str:
    try:
        text = json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            sort_keys=True,
            separators=(",", ":"),
            default=str,
        )
    except (TypeError, ValueError) as exc:
        raise LegacyFlowDenied("legacy flow result is not serializable") from exc
    return text[:MAX_LEGACY_RESULT_CHARS]


def _tool_call_bound(steps: list[dict[str, Any]]) -> int:
    total = 0
    for step in steps:
        parsed = parse_step(step)
        if parsed is None:
            continue
        kind, body = parsed
        if kind == "tool":
            total += 1
        elif kind == "node":
            spec = resolve_node(body)
            total += len(spec.requires_tools) if spec is not None else 0
        elif kind == "sequence":
            total += _tool_call_bound(body)
        elif kind == "branch":
            total += max(
                _tool_call_bound(body.get("then") or []),
                _tool_call_bound(body.get("else") or []),
            )
        elif kind == "loop":
            total += int(body.get("max_iterations") or 0) * _tool_call_bound(
                body.get("body") or []
            )
    return total


def _step_bound(steps: list[dict[str, Any]]) -> int:
    total = 0
    for step in steps:
        parsed = parse_step(step)
        if parsed is None:
            continue
        kind, body = parsed
        if kind in {"tool", "node", "script"}:
            total += 1
        elif kind == "sequence":
            total += _step_bound(body)
        elif kind == "branch":
            total += 2 + max(
                _step_bound(body.get("then") or []),
                _step_bound(body.get("else") or []),
            )
        elif kind == "loop":
            total += 2 + int(body.get("max_iterations") or 0) * (
                _step_bound(body.get("body") or []) + 1
            )
    return total
