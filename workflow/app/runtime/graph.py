from __future__ import annotations

import hashlib
import json
import math
from dataclasses import dataclass, field
from typing import Any

from langgraph.checkpoint.base import BaseCheckpointSaver
from langgraph.graph import END, START, StateGraph
from langgraph.graph.state import CompiledStateGraph
from langgraph.runtime import Runtime
from langgraph.types import interrupt

# Populate the sole Tool Registry even when the runtime is imported outside
# FastAPI's main module (for example a recovery worker or integration test).
from app import tools as _registered_tools  # noqa: F401
from app.engine import tool_registry
from app.runtime.artifacts import (
    ArtifactError,
    LoadedSkillArtifact,
    RevisionArtifactReader,
    read_artifact_resource,
)
from app.runtime.checkpoints import ensure_checkpointer
from app.runtime.events import runtime_event
from app.runtime.facts import ProposedAction, caller_envelopes, verified_tool_fact
from app.runtime.model import ModelContextTooLarge, ModelProtocolError, RuntimeModel
from app.runtime.legacy_flow import LegacyFlowDenied, invoke_pinned_legacy_flow
from app.runtime.models import (
    MAX_BACKEND_RESULT_JSON_BYTES,
    ActiveSkillScope,
    DirectAgentExecutionSnapshot,
    RuntimeCommand,
    RuntimeState,
    backend_result_wire_size,
    canonical_json_sha256,
)
from app.runtime.policy import PolicyDecision, PreActionPolicy
from app.runtime.output_contract import output_matches
from app.runtime.tool_boundary import (
    DirectToolDenied,
    ToolArgumentsInvalid,
    effective_specs,
    effective_tool_names,
    invoke_direct_tool,
    tool_fingerprint,
)
from app.security import RequestContext
from app.settings import settings


class RuntimePreflightError(RuntimeError):
    pass


@dataclass(frozen=True)
class EffectiveLimits:
    max_tool_rounds: int
    max_context_rounds: int
    timeout_seconds: int
    token_budget: int
    step_budget: int


@dataclass
class RuntimeGraphContext:
    snapshot: DirectAgentExecutionSnapshot
    request_context: RequestContext
    model: RuntimeModel
    artifact_reader: RevisionArtifactReader
    policy: PreActionPolicy
    limits: EffectiveLimits
    deps: Any = None
    tool_timeout_seconds: float = 15.0
    artifact_cache: dict[tuple[str, int, str], LoadedSkillArtifact] = field(
        default_factory=dict
    )


def build_context(
    *,
    snapshot: DirectAgentExecutionSnapshot,
    request_context: RequestContext,
    model: RuntimeModel,
    artifact_reader: RevisionArtifactReader,
    deps: Any = None,
) -> RuntimeGraphContext:
    _validate_preflight_snapshot(snapshot, request_context)
    policy = PreActionPolicy(
        snapshot.agent.business_rules,
        pinned_skills=(item.name for item in snapshot.skills),
        registered_tools=(item.name for item in tool_registry.all_specs()),
        roles=[snapshot.caller.role],
    )
    return RuntimeGraphContext(
        snapshot=snapshot,
        request_context=request_context,
        model=model,
        artifact_reader=artifact_reader,
        policy=policy,
        limits=_effective_limits(snapshot),
        deps=deps,
    )


def initial_state(
    snapshot: DirectAgentExecutionSnapshot, message: str
) -> RuntimeState:
    return RuntimeState(
        run_id=snapshot.run_id,
        snapshot_hash=snapshot.snapshot_hash,
        messages=[{"role": "user", "content": message}],
        pending_command=None,
        pending_input=None,
        active_skill_scope=None,
        step_count=0,
        tool_rounds=0,
        context_rounds=0,
        context_acquisition_attempts=0,
        verified_context=None,
        estimated_tokens=0,
        status="running",
        final_output=None,
        error_code=None,
        audit_tags=[],
        response_policies=[],
        last_rule_decision=None,
        rule_allowed_tools=None,
        checkpoint_version=None,
        tool_fingerprints=[],
        events=[],
    )


def compile_runtime_graph(
    checkpointer: BaseCheckpointSaver[str],
) -> CompiledStateGraph:
    graph = StateGraph(RuntimeState, context_schema=RuntimeGraphContext)
    graph.add_node("preflight", _preflight)
    graph.add_node("model_step", _model_step)
    graph.add_node("policy_gate", _policy_gate)
    graph.add_node("load_skill", _load_skill)
    graph.add_node("exit_skill", _exit_skill)
    graph.add_node("invoke_tool", _invoke_tool)
    graph.add_node("read_resource", _read_resource)
    graph.add_node("prepare_input", _prepare_input)
    graph.add_node("pause_for_input", _pause_for_input)
    graph.add_node("accept_final", _accept_final)
    graph.add_node("validate_output", _validate_output)
    graph.add_node("budget_gate", _budget_gate)
    graph.add_node("finalize", _finalize)

    graph.add_edge(START, "preflight")
    graph.add_edge("preflight", "model_step")
    graph.add_conditional_edges(
        "model_step",
        _after_model,
        {"policy": "policy_gate", "finalize": "finalize"},
    )
    graph.add_conditional_edges(
        "policy_gate",
        _after_policy,
        {
            "load_skill": "load_skill",
            "exit_skill": "exit_skill",
            "tool_call": "invoke_tool",
            "read_resource": "read_resource",
            "request_input": "prepare_input",
            "final": "accept_final",
            "waiting_input": "prepare_input",
            "finalize": "finalize",
        },
    )
    for node in ("load_skill", "exit_skill", "invoke_tool", "read_resource"):
        graph.add_edge(node, "budget_gate")
    graph.add_edge("prepare_input", "pause_for_input")
    graph.add_edge("pause_for_input", "budget_gate")
    graph.add_edge("accept_final", "validate_output")
    graph.add_edge("validate_output", "finalize")
    graph.add_conditional_edges(
        "budget_gate",
        lambda state: "continue" if state.get("status") == "running" else "finalize",
        {"continue": "model_step", "finalize": "finalize"},
    )
    graph.add_edge("finalize", END)
    return graph.compile(checkpointer=ensure_checkpointer(checkpointer))


async def _preflight(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    context = runtime.context
    if (
        state.get("run_id") != context.snapshot.run_id
        or state.get("snapshot_hash") != context.snapshot.snapshot_hash
    ):
        raise RuntimePreflightError("checkpoint does not match the execution snapshot")
    return _with_event(
        state,
        context,
        "run_preflight",
        "preflight",
        {"status": "ok", "agent_revision": context.snapshot.agent.revision},
    )


async def _model_step(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    context = runtime.context
    budget_error = _budget_error(state, context.limits)
    if budget_error:
        return {
            **_failure(state, context, "model_step", budget_error),
            "pending_command": None,
        }
    artifact = await _active_artifact(state, context)
    scope = state.get("active_skill_scope") or {}
    rule_tools = (
        frozenset(scope.get("effective_tools") or []) if artifact is not None else None
    )
    specs = effective_specs(
        context.snapshot,
        artifact=artifact,
        rule_tools=rule_tools,
        deps=context.deps,
    )
    try:
        turn = await context.model.next_command(
            snapshot=context.snapshot,
            messages=list(state.get("messages") or []),
            active_instruction=artifact.instruction if artifact else "",
            active_skill_name=artifact.name if artifact else "",
            tools=specs,
            resource_paths=_readable_resource_paths(artifact) if artifact else [],
            remaining_token_budget=max(
                context.limits.token_budget
                - int(state.get("estimated_tokens") or 0),
                0,
            ),
        )
    except ModelContextTooLarge:
        return _failure(state, context, "model_step", "runtime_context_too_large")
    except ModelProtocolError:
        return _failure(state, context, "model_step", "model_protocol_error")
    raw_usage = turn.token_usage
    remaining = (
        context.limits.token_budget - int(state.get("estimated_tokens") or 0)
    )
    if (
        isinstance(raw_usage, bool)
        or not isinstance(raw_usage, int | float)
        or not math.isfinite(raw_usage)
        or raw_usage < 0
        or int(raw_usage) != raw_usage
        or raw_usage > remaining
    ):
        return _failure(state, context, "model_step", "model_usage_invalid")
    token_usage = int(raw_usage)
    step = int(state.get("step_count") or 0) + 1
    events = _event_list(
        state,
        context,
        "model_step",
        "model_step",
        {
            "status": "ok",
                "step": step,
                "action_kind": turn.command.kind,
                "active_skill": bool(artifact),
                **(turn.audit_metadata or {}),
            },
    )
    return {
        "pending_command": turn.command.model_dump(mode="json"),
        "step_count": step,
        "estimated_tokens": int(state.get("estimated_tokens") or 0)
        + token_usage,
        "events": events,
    }


def _after_model(state: RuntimeState) -> str:
    return "policy" if state.get("status") == "running" else "finalize"


async def _policy_gate(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    context = runtime.context
    command = RuntimeCommand.model_validate(state.get("pending_command"))
    context_planned = False
    if (
        command.kind == "request_input"
        and int(state.get("context_acquisition_attempts") or 0) < 1
    ):
        artifact = await _active_artifact(state, context)
        scope = state.get("active_skill_scope") or {}
        rule_tools = (
            frozenset(scope.get("effective_tools") or [])
            if artifact is not None
            else None
        )
        available = {
            spec.name
            for spec in effective_specs(
                context.snapshot,
                artifact=artifact,
                rule_tools=rule_tools,
                deps=context.deps,
            )
        }
        latest_user = next(
            (
                str(item.get("content") or "")
                for item in reversed(state.get("messages") or [])
                if item.get("role") == "user"
            ),
            "",
        )
        if "backend.retrieval_search" in available and latest_user.strip():
            command = RuntimeCommand(
                kind="tool_call",
                name="backend.retrieval_search",
                arguments={
                    "query": latest_user.strip()[:4_000],
                    "top_k": settings.retrieval_top_k,
                },
            )
            context_planned = True
        else:
            command = command.model_copy(
                update={"content": _minimal_question(command.content)}
            )
    if (
        command.kind == "tool_call"
        and int(state.get("tool_rounds") or 0) >= context.limits.max_tool_rounds
    ):
        return _failure(state, context, "policy_gate", "tool_budget_exceeded")
    action = _proposed_action(command, state, context.snapshot)
    try:
        base_facts = caller_envelopes(
            tenant_id=context.snapshot.caller.tenant_id,
            role=context.snapshot.caller.role,
            groups=context.snapshot.caller.groups,
        )
        verified_context = state.get("verified_context") or {}
        if verified_context.get("producer") == "backend_retrieval":
            source_ref = str(verified_context.get("tool_event") or "")
            base_facts.extend(
                [
                    verified_tool_fact(
                        "context.source_count",
                        int(verified_context.get("source_count") or 0),
                        producer="backend_retrieval",
                        source_ref=source_ref,
                    ),
                    verified_tool_fact(
                        "context.source_types",
                        list(verified_context.get("source_types") or []),
                        producer="backend_retrieval",
                        source_ref=source_ref,
                    ),
                ]
            )
        decision = context.policy.decide(
            action,
            base_facts,
        )
    except Exception:
        return _failure(state, context, "policy_gate", "policy_evaluation_failed")
    audit_tags = _bounded_unique(
        [*(state.get("audit_tags") or []), *decision.audit_tags]
    )
    response_policies = _bounded_unique(
        [*(state.get("response_policies") or []), *decision.response_policies]
    )
    decision_payload = {
        **(decision.summary or {"outcome": decision.outcome}),
        "audit_tags": audit_tags,
        "response_policy_count": len(response_policies),
        "action_provenance": {
            "action_type": action.action_type,
            "tool_name": action.tool_name,
            "skill_name": action.skill_name,
            "skill_revision": action.skill_revision,
            "skill_kind": action.skill_kind,
        },
    }
    output: dict[str, Any] = {
        "pending_command": command.model_dump(mode="json"),
        "context_acquisition_attempts": (
            int(state.get("context_acquisition_attempts") or 0)
            + (1 if context_planned else 0)
        ),
        "last_rule_decision": decision.summary,
        "rule_allowed_tools": (
            sorted(decision.allowed_read_tools)
            if decision.allowed_read_tools is not None
            else None
        ),
        "audit_tags": audit_tags,
        "response_policies": response_policies,
        "events": _event_list(
            {
                **state,
                "events": (
                    _event_list(
                        state,
                        context,
                        "context_acquisition_planned",
                        "policy_gate",
                        {
                            "status": "planned",
                            "tool_name": "backend.retrieval_search",
                            "attempt": int(
                                state.get("context_acquisition_attempts") or 0
                            )
                            + 1,
                        },
                    )
                    if context_planned
                    else list(state.get("events") or [])
                ),
            },
            context,
            "rule_decision",
            "policy_gate",
            decision_payload,
        ),
    }
    if response_policies:
        output.update(
            status="failed",
            error_code="unsupported_response_policy",
            pending_command=None,
        )
    elif decision.outcome == "blocked":
        output.update(
            status="failed",
            error_code=decision.code,
            pending_command=None,
        )
    elif decision.outcome == "waiting_input" and decision.code == "require_context":
        attempts = int(state.get("context_acquisition_attempts") or 0)
        artifact = await _active_artifact(state, context)
        available = {
            spec.name
            for spec in effective_specs(
                context.snapshot,
                artifact=artifact,
                rule_tools=None,
                deps=context.deps,
            )
        }
        latest_user = next(
            (
                str(item.get("content") or "")
                for item in reversed(state.get("messages") or [])
                if item.get("role") == "user"
            ),
            "",
        )
        if attempts >= 1:
            output.update(
                status="failed",
                error_code="required_context_unresolved",
                pending_command=None,
            )
        elif "backend.retrieval_search" not in available or not latest_user.strip():
            output.update(
                status="failed",
                error_code="required_context_unavailable",
                pending_command=None,
            )
        else:
            output.update(
                pending_command=RuntimeCommand(
                    kind="tool_call",
                    name="backend.retrieval_search",
                    arguments={
                        "query": latest_user.strip()[:4_000],
                        "top_k": settings.retrieval_top_k,
                    },
                ).model_dump(mode="json"),
                pending_input=None,
                rule_allowed_tools=None,
                context_acquisition_attempts=attempts + 1,
            )
    elif decision.outcome == "waiting_input":
        if int(state.get("context_rounds") or 0) >= context.limits.max_context_rounds:
            output.update(
                status="failed",
                error_code="context_budget_exceeded",
                pending_command=None,
            )
        else:
            output.update(
                pending_input={
                    "question": _minimal_question(decision.question),
                    "source": "rule",
                }
            )
    elif decision.routed_skill:
        active_scope = state.get("active_skill_scope") or {}
        pin = context.snapshot.skill_pin(decision.routed_skill)
        route_satisfied = (
            pin is not None
            and active_scope.get("name") == pin.name
            and active_scope.get("revision") == pin.revision
            and active_scope.get("kind") == pin.kind
        )
        if not route_satisfied:
            output["pending_command"] = RuntimeCommand(
                kind="load_skill", name=decision.routed_skill
            ).model_dump(mode="json")
    return output


def _after_policy(state: RuntimeState) -> str:
    if state.get("status") != "running":
        return "finalize"
    if state.get("pending_input"):
        return "waiting_input"
    kind = str((state.get("pending_command") or {}).get("kind") or "finalize")
    # A final answer produced inside a Skill scope completes that task frame,
    # not the entire Agent run. Close the scope first; the next model turn is
    # rebuilt without the Skill instruction and with the broader direct-tool
    # authority, then its final response can terminate the run.
    if kind == "final" and state.get("active_skill_scope"):
        return "exit_skill"
    return kind


async def _load_skill(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    context = runtime.context
    command = RuntimeCommand.model_validate(state.get("pending_command"))
    pin = context.snapshot.skill_pin(command.name or "")
    if pin is None:
        return _failure(state, context, "load_skill", "skill_not_pinned")
    try:
        artifact = await _artifact(pin.name, context)
    except ArtifactError:
        return _failure(state, context, "load_skill", "skill_artifact_unavailable")
    if artifact.kind == "flow":
        try:
            result = await invoke_pinned_legacy_flow(
                artifact=artifact,
                raw_input=command.arguments,
                snapshot=context.snapshot,
                rule_tools=_rule_tools(state),
                deps=context.deps,
                timeout_seconds=min(
                    float(context.limits.timeout_seconds),
                    max(context.tool_timeout_seconds, 1.0),
                ),
                recursion_cap=max(
                    2,
                    context.limits.step_budget
                    - int(state.get("step_count") or 0)
                    + 1,
                ),
                remaining_tool_rounds=max(
                    0,
                    context.limits.max_tool_rounds
                    - int(state.get("tool_rounds") or 0),
                ),
            )
        except LegacyFlowDenied:
            return _failure(state, context, "load_skill", "legacy_flow_not_safe")
        return {
            "pending_command": None,
            "rule_allowed_tools": None,
            "step_count": int(state.get("step_count") or 0)
            + max(result.steps_bound - 1, 0),
            "tool_rounds": int(state.get("tool_rounds") or 0)
            + result.tool_calls_bound,
            "messages": [
                *(state.get("messages") or []),
                {
                    "role": "tool",
                    "name": "load_skill",
                    "content": result.content,
                },
            ],
            "events": _event_list(
                state,
                context,
                "legacy_flow_completed",
                "load_skill",
                {
                    "skill_name": artifact.name,
                    "skill_revision": artifact.revision,
                    "definition_sha256": artifact.definition_sha256,
                    "status": result.status,
                    "tool_calls_bound": result.tool_calls_bound,
                    "steps_bound": result.steps_bound,
                },
            ),
        }
    rule_tools = _rule_tools(state)
    effective = effective_tool_names(
        context.snapshot,
        artifact=artifact,
        rule_tools=rule_tools,
        deps=context.deps,
    )
    scope = ActiveSkillScope(
        name=artifact.name,
        revision=artifact.revision,
        kind="agentic",
        definition_sha256=artifact.definition_sha256,
        package_sha256=artifact.package_sha256,
        instruction_sha256=artifact.instruction_sha256,
        effective_tools=sorted(effective),
        resource_paths=_readable_resource_paths(artifact),
    )
    observation = (
        f"Pinned Skill {artifact.name}@{artifact.revision} is active in this run. "
        f"{len(effective)} read-only tools and {len(artifact.resources)} resources are available."
    )
    events = list(state.get("events") or [])
    previous_scope = state.get("active_skill_scope") or {}
    if previous_scope:
        events = _event_list(
            {**state, "events": events},
            context,
            "skill_scope_exited",
            "load_skill",
            {
                "skill_name": previous_scope.get("name"),
                "skill_revision": previous_scope.get("revision"),
                "reason": "scope_switch",
            },
        )
    events = _event_list(
        {**state, "events": events},
        context,
        "skill_scope_entered",
        "load_skill",
        {
            "skill_name": artifact.name,
            "skill_revision": artifact.revision,
            "definition_sha256": artifact.definition_sha256,
            "package_sha256": artifact.package_sha256,
            "effective_tool_count": len(effective),
            "file_count": len(artifact.resources),
            "scripts_present": artifact.scripts_present,
        },
    )
    return {
        "active_skill_scope": scope.model_dump(mode="json"),
        "pending_command": None,
        "rule_allowed_tools": None,
        "messages": [
            *(state.get("messages") or []),
            {"role": "tool", "name": "load_skill", "content": observation},
        ],
        "events": events,
    }


async def _exit_skill(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    scope = state.get("active_skill_scope") or {}
    command = RuntimeCommand.model_validate(state.get("pending_command"))
    task_completed = command.kind == "final"
    observation = (
        f"Skill task result: {(command.content or '')[:16_384]}"
        if task_completed
        else "The pinned Skill scope is closed."
    )
    return {
        "active_skill_scope": None,
        "pending_command": None,
        "rule_allowed_tools": None,
        "messages": [
            *(state.get("messages") or []),
            {
                "role": "tool",
                "name": "exit_skill",
                "content": observation,
            },
        ],
        "events": _event_list(
            state,
            runtime.context,
            "skill_scope_exited",
            "exit_skill",
            {
                "skill_name": scope.get("name"),
                "skill_revision": scope.get("revision"),
                "reason": "task_completed" if task_completed else "explicit_exit",
            },
        ),
    }


async def _invoke_tool(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    context = runtime.context
    command = RuntimeCommand.model_validate(state.get("pending_command"))
    name = command.name or ""
    fingerprint = tool_fingerprint(name, command.arguments)
    if fingerprint in set(state.get("tool_fingerprints") or []):
        return {
            "pending_command": None,
            "rule_allowed_tools": None,
            "messages": [
                *(state.get("messages") or []),
                {
                    "role": "tool",
                    "name": name,
                    "content": "Duplicate tool request denied by the run budget.",
                },
            ],
            "events": _event_list(
                state,
                context,
                "tool_deduplicated",
                "invoke_tool",
                {"tool_name": name, "status": "denied"},
            ),
        }
    artifact = await _active_artifact(state, context)
    try:
        observation = await invoke_direct_tool(
            name=name,
            arguments=command.arguments,
            snapshot=context.snapshot,
            artifact=artifact,
            rule_tools=_rule_tools(state),
            deps=context.deps,
            timeout_seconds=context.tool_timeout_seconds,
        )
    except ToolArgumentsInvalid:
        return _failure(state, context, "invoke_tool", "tool_arguments_invalid")
    except DirectToolDenied:
        return _failure(state, context, "invoke_tool", "tool_denied")
    return {
        "pending_command": None,
        "rule_allowed_tools": None,
        "tool_rounds": int(state.get("tool_rounds") or 0) + 1,
        "tool_fingerprints": [
            *(state.get("tool_fingerprints") or []),
            observation.fingerprint,
        ][-context.limits.max_tool_rounds :],
        "messages": [
            *(state.get("messages") or []),
            {"role": "tool", "name": name, "content": observation.content},
        ],
        "verified_context": (
            observation.verified_context
            if observation.verified_context is not None
            else state.get("verified_context")
        ),
        "events": _event_list(
            state,
            context,
            "tool_completed",
            "invoke_tool",
            {
                "tool_name": name,
                "status": "ok",
                "verified_context": (
                    {
                        "source_count": observation.verified_context["source_count"],
                        "source_types": observation.verified_context["source_types"],
                        "knowledge_source_count": len(
                            observation.verified_context["knowledge_sources"]
                        ),
                        "tool_event": observation.verified_context["tool_event"],
                    }
                    if observation.verified_context is not None
                    else None
                ),
            },
        ),
    }


async def _read_resource(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    context = runtime.context
    command = RuntimeCommand.model_validate(state.get("pending_command"))
    try:
        artifact = await _active_artifact(state, context)
        if artifact is None:
            raise ArtifactError("no active Skill")
        path = command.name or ""
        if path not in _readable_resource_paths(artifact):
            raise ArtifactError("resource is not exposed to the runtime")
        content = read_artifact_resource(artifact, path)
    except ArtifactError:
        return _failure(state, context, "read_resource", "resource_denied")
    return {
        "pending_command": None,
        "rule_allowed_tools": None,
        "messages": [
            *(state.get("messages") or []),
            {
                "role": "tool",
                "name": "read_resource",
                "content": content,
            },
        ],
        "events": _event_list(
            state,
            context,
            "skill_file_read",
            "read_resource",
            {
                "path_sha256": hashlib.sha256(
                    (command.name or "").encode("utf-8")
                ).hexdigest(),
                "character_count": len(content),
            },
        ),
    }


async def _prepare_input(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    if state.get("pending_input"):
        pending = state["pending_input"]
    else:
        command = RuntimeCommand.model_validate(state.get("pending_command"))
        pending = {
            "question": _minimal_question(command.content),
            "source": "model",
        }
    return {
        "pending_input": pending,
        "pending_command": None,
        "rule_allowed_tools": None,
        "events": _event_list(
            state,
            runtime.context,
            "input_requested",
            "prepare_input",
            {"source": pending.get("source"), "status": "waiting_input"},
        ),
    }


def _pause_for_input(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    pending = state.get("pending_input") or {}
    value = interrupt(
        {
            "kind": "input",
            "question": str(pending.get("question") or "")[:2_000],
            "run_id": runtime.context.snapshot.run_id,
            "snapshot_hash": runtime.context.snapshot.snapshot_hash,
        }
    )
    if not isinstance(value, str) or not value.strip():
        return _failure(state, runtime.context, "pause_for_input", "invalid_resume_input")
    if len(value) > settings.runtime_max_message_chars:
        return _failure(state, runtime.context, "pause_for_input", "resume_input_too_large")
    return {
        "pending_input": None,
        "context_rounds": int(state.get("context_rounds") or 0) + 1,
        "messages": [
            *(state.get("messages") or []),
            {"role": "user", "content": value.strip()},
        ],
        "events": _event_list(
            state,
            runtime.context,
            "run_resumed",
            "pause_for_input",
            {"status": "running"},
        ),
    }


async def _accept_final(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    command = RuntimeCommand.model_validate(state.get("pending_command"))
    return {
        "pending_command": None,
        "final_output": command.content or "",
        "events": _event_list(
            state,
            runtime.context,
            "response_proposed",
            "accept_final",
            {"character_count": len(command.content or "")},
        ),
    }


async def _validate_output(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    output = state.get("final_output") or ""
    if not output_matches(runtime.context.snapshot.agent.output_contract, output):
        return _failure(state, runtime.context, "validate_output", "output_contract_invalid")
    if (
        backend_result_wire_size({"output": output})
        > MAX_BACKEND_RESULT_JSON_BYTES
    ):
        return _failure(
            state,
            runtime.context,
            "validate_output",
            "runtime_result_too_large",
        )
    return {
        "status": "completed",
        "events": _event_list(
            state,
            runtime.context,
            "output_validated",
            "validate_output",
            {"status": "ok", "character_count": len(output)},
        ),
    }


async def _budget_gate(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    error = _budget_error(state, runtime.context.limits)
    if error:
        return _failure(state, runtime.context, "budget_gate", error)
    return {
        "events": _event_list(
            state,
            runtime.context,
            "checkpoint_budget",
            "budget_gate",
            {
                "step_count": int(state.get("step_count") or 0),
                "tool_rounds": int(state.get("tool_rounds") or 0),
                "context_rounds": int(state.get("context_rounds") or 0),
                "estimated_tokens": int(state.get("estimated_tokens") or 0),
            },
        )
    }


async def _finalize(
    state: RuntimeState, runtime: Runtime[RuntimeGraphContext]
) -> dict[str, Any]:
    status = state.get("status") or "failed"
    events = list(state.get("events") or [])
    scope = state.get("active_skill_scope") or {}
    if scope:
        events = _event_list(
            {**state, "events": events},
            runtime.context,
            "skill_scope_exited",
            "finalize",
            {
                "skill_name": scope.get("name"),
                "skill_revision": scope.get("revision"),
                "reason": "terminal",
            },
        )
    events = _event_list(
        {**state, "events": events},
        runtime.context,
        "run_terminal",
        "finalize",
        {"status": status, "error_code": state.get("error_code")},
    )
    return {
        "active_skill_scope": None,
        "pending_command": None,
        "rule_allowed_tools": None,
        "events": events,
    }


async def _artifact(
    name: str, context: RuntimeGraphContext
) -> LoadedSkillArtifact:
    pin = context.snapshot.skill_pin(name)
    if pin is None:
        raise ArtifactError("Skill is not pinned")
    key = (pin.name, pin.revision, pin.definition_sha256)
    cached = context.artifact_cache.get(key)
    if cached is not None:
        return cached
    artifact = await context.artifact_reader.read(pin, context.request_context)
    if (
        artifact.name != pin.name
        or artifact.revision != pin.revision
        or artifact.kind != pin.kind
        or artifact.definition_sha256 != pin.definition_sha256
        or artifact.package_sha256 != pin.package_sha256
    ):
        raise ArtifactError("loaded Skill artifact does not match its snapshot pin")
    context.artifact_cache[key] = artifact
    return artifact


async def _active_artifact(
    state: RuntimeState, context: RuntimeGraphContext
) -> LoadedSkillArtifact | None:
    scope = state.get("active_skill_scope")
    if not scope:
        return None
    artifact = await _artifact(str(scope.get("name") or ""), context)
    expected = ActiveSkillScope.model_validate(scope)
    if (
        artifact.revision != expected.revision
        or artifact.definition_sha256 != expected.definition_sha256
        or artifact.package_sha256 != expected.package_sha256
        or artifact.instruction_sha256 != expected.instruction_sha256
    ):
        raise ArtifactError("active Skill scope does not match its checkpoint pin")
    return artifact


def _proposed_action(
    command: RuntimeCommand,
    state: RuntimeState,
    snapshot: DirectAgentExecutionSnapshot,
) -> ProposedAction:
    scope = state.get("active_skill_scope") or {}
    skill_name = str(scope.get("name") or "")
    skill_revision = int(scope.get("revision") or 0)
    skill_kind = str(scope.get("kind") or "")
    if command.kind == "load_skill":
        pin = snapshot.skill_pin(command.name or "")
        if pin is not None:
            skill_name = pin.name
            skill_revision = pin.revision
            skill_kind = pin.kind
    if command.kind == "tool_call":
        return ProposedAction(
            action_type="tool_call",
            tool_name=command.name or "",
            requested_tools=(command.name or "",),
            skill_name=skill_name,
            skill_revision=skill_revision,
            skill_kind=skill_kind,
        )
    if command.kind == "read_resource":
        return ProposedAction(
            action_type="skill_call",
            tool_name="runtime_read_resource",
            requested_tools=("runtime_read_resource",),
            skill_name=skill_name,
            skill_revision=skill_revision,
            skill_kind=skill_kind,
        )
    if command.kind in {"load_skill", "exit_skill"}:
        return ProposedAction(
            action_type="skill_call",
            skill_name=skill_name,
            skill_revision=skill_revision,
            skill_kind=skill_kind,
        )
    return ProposedAction(action_type="response")


def _rule_tools(state: RuntimeState) -> frozenset[str] | None:
    value = state.get("rule_allowed_tools")
    return frozenset(value) if value is not None else None


def _readable_resource_paths(artifact: LoadedSkillArtifact) -> list[str]:
    return sorted(
        path
        for path in artifact.resources
        if len(path) <= 128
        and (path.startswith("references/") or path.startswith("assets/"))
    )


def _budget_error(state: RuntimeState, limits: EffectiveLimits) -> str | None:
    if int(state.get("step_count") or 0) >= limits.step_budget:
        return "step_budget_exceeded"
    if int(state.get("context_rounds") or 0) > limits.max_context_rounds:
        return "context_budget_exceeded"
    if int(state.get("estimated_tokens") or 0) >= limits.token_budget:
        return "token_budget_exceeded"
    return None


def _failure(
    state: RuntimeState,
    context: RuntimeGraphContext,
    node: str,
    code: str,
) -> dict[str, Any]:
    return {
        "status": "failed",
        "error_code": code,
        "events": _event_list(
            state,
            context,
            "runtime_denied",
            node,
            {"status": "failed", "error_code": code},
        ),
    }


def _event_list(
    state: RuntimeState,
    context: RuntimeGraphContext,
    event_type: str,
    node_id: str,
    payload: dict[str, Any],
) -> list[dict[str, Any]]:
    current = list(state.get("events") or [])
    event = runtime_event(
        run_id=context.snapshot.run_id,
        snapshot_hash=context.snapshot.snapshot_hash,
        event_type=event_type,
        node_id=node_id,
        event_key=f"{state.get('step_count', 0)}:{len(current)}",
        payload=payload,
    )
    current.append(event.as_backend_dict())
    return current


def _with_event(
    state: RuntimeState,
    context: RuntimeGraphContext,
    event_type: str,
    node_id: str,
    payload: dict[str, Any],
) -> dict[str, Any]:
    return {"events": _event_list(state, context, event_type, node_id, payload)}


def _bounded_unique(values: list[str]) -> list[str]:
    return list(dict.fromkeys(item[:200] for item in values))[:100]


def _minimal_question(value: str | None) -> str:
    text = " ".join((value or "").strip().split())
    if not text:
        return "請補充完成此任務所需的唯一必要資訊。"
    # Keep exactly the first question/sentence and cap persisted public input.
    for delimiter in ("？", "?", "。", "."):
        if delimiter in text:
            text = text.split(delimiter, 1)[0].strip() + delimiter
            break
    return text[:500]


def _effective_limits(snapshot: DirectAgentExecutionSnapshot) -> EffectiveLimits:
    raw = snapshot.agent.runtime_limits
    definition = snapshot.workflow.definition
    if canonical_json_sha256(definition) != snapshot.workflow.definition_sha256:
        raise RuntimePreflightError(
            "runtime workflow definition hash does not match its snapshot pin"
        )
    governance = definition.get("governance") or {}
    workflow_steps = governance.get("maxSteps")
    loop_iterations = 0
    for node in definition.get("nodes") or []:
        if node.get("type") == "bounded_agent_loop":
            loop_iterations = int((node.get("config") or {}).get("maxIterations") or 0)
            break
    step_budget = raw.step_budget or settings.runtime_default_step_budget
    if isinstance(workflow_steps, int) and workflow_steps > 0:
        step_budget = min(step_budget, workflow_steps)
    tool_rounds = raw.max_tool_rounds or settings.runtime_default_tool_rounds
    if loop_iterations > 0:
        tool_rounds = min(tool_rounds, loop_iterations)
    return EffectiveLimits(
        max_tool_rounds=tool_rounds,
        max_context_rounds=(
            raw.max_context_rounds or settings.runtime_default_context_rounds
        ),
        timeout_seconds=min(
            raw.effective_timeout_seconds
            or raw.timeout_seconds
            or settings.runtime_default_timeout_seconds,
            600,
        ),
        token_budget=raw.token_budget or settings.runtime_default_token_budget,
        step_budget=step_budget,
    )


def _validate_preflight_snapshot(
    snapshot: DirectAgentExecutionSnapshot, ctx: RequestContext
) -> None:
    snapshot.assert_hash()
    if snapshot.run_id == "":
        raise RuntimePreflightError("run id is missing")
    if (
        snapshot.caller.tenant_id != ctx.tenant_id
        or snapshot.caller.user_id != ctx.user_id
        or snapshot.caller.role != ctx.role
    ):
        raise RuntimePreflightError("request identity does not match the snapshot")
    if snapshot.workflow.compiler_contract_version != "1":
        raise RuntimePreflightError("unsupported runtime compiler contract")
    definition = snapshot.workflow.definition
    if definition.get("schemaVersion") != 1 or definition.get("kind") != "agent-runtime":
        raise RuntimePreflightError("workflow is not an Agent-Runtime v1 graph")
    nodes = definition.get("nodes")
    if not isinstance(nodes, list):
        raise RuntimePreflightError("runtime workflow has no node catalog")
    node_types = {node.get("type") for node in nodes if isinstance(node, dict)}
    required = {
        "start",
        "dependency_and_capability_preflight",
        "inject_authorized_context",
        "checkpoint",
        "bounded_agent_loop",
        "validate_structured_output",
        "bounded_repair_or_controlled_failure",
        "end",
    }
    if not required <= node_types:
        raise RuntimePreflightError("runtime workflow is missing a required stage")
    loop = next(
        (
            node
            for node in nodes
            if isinstance(node, dict) and node.get("type") == "bounded_agent_loop"
        ),
        None,
    )
    if not isinstance(loop, dict) or not isinstance(
        (loop.get("config") or {}).get("maxIterations"), int
    ):
        raise RuntimePreflightError("runtime workflow loop is not bounded")
    child_types = {
        child.get("type")
        for child in loop.get("children") or []
        if isinstance(child, dict)
    }
    if not {
        "model_step",
        "tool_policy_and_approval_gate",
        "tool_call_and_observation",
        "checkpoint_and_budget_gate",
    } <= child_types:
        raise RuntimePreflightError("runtime workflow loop is missing governance stages")
