from __future__ import annotations

import asyncio
import hashlib
import time
from contextvars import copy_context
from dataclasses import dataclass
from typing import Any

from langgraph.graph.state import CompiledStateGraph

from pydantic import ValidationError

# The compiler always appends this system-owned audit node. Importing it here
# keeps recovery workers self-contained instead of relying on FastAPI import
# order to populate the Node Registry.
from app.nodes.kbquery.nodes import audit_feedback as _audit_feedback  # noqa: F401
from app.engine import compiler
from app.engine.node_shell import (
    BudgetExhausted,
    RUNTIME_AUTHORITY_KEYS,
    set_budget_callback,
    set_step_guard,
)
from app.engine.skill import (
    ENGINE_KEYS,
    RESERVED_KEYS,
    Skill,
    build_input_model,
    clean_invoke_input,
    parse_step,
    resolve_node,
)
from app.runtime.artifacts import LoadedSkillArtifact
from app.runtime.bounded_json import bounded_canonical_json
from app.runtime.models import DirectAgentExecutionSnapshot
from app.runtime.tool_boundary import (
    effective_knowledge_sources,
    effective_tool_names,
)

MAX_FLOW_RESULT_CHARS = 16_384

PUBLIC_DENY_KEYS = frozenset(
    RESERVED_KEYS
    | ENGINE_KEYS
    | RUNTIME_AUTHORITY_KEYS
    | {
        "audit_trail",
        "issue_label",
        "regression_test_item",
        "improvement_backlog",
    }
)

class FlowDenied(RuntimeError):
    pass


@dataclass(frozen=True)
class FlowResult:
    status: str
    content: str
    tool_calls_bound: int
    steps_bound: int
    steps_consumed: int | None = None
    tool_rounds_consumed: int | None = None


@dataclass(frozen=True)
class FlowGovernanceResult:
    status: str
    output: dict
    governance: dict


def _public_flow_output(state: dict[str, Any]) -> dict[str, Any]:
    return {
        key: value
        for key, value in state.items()
        if key not in PUBLIC_DENY_KEYS and not str(key).startswith("__")
    }


def _prepare_flow_state(
    skill: Skill, raw_input: dict[str, Any], *, recursion_cap: int | None = None
) -> tuple[dict[str, Any], dict[str, Any]]:
    recursion_limit = compiler.step_analysis(skill).recursion_limit
    return dict(raw_input), {
        "recursion_limit": min(
            recursion_limit,
            recursion_cap or recursion_limit,
        )
    }


async def _execute_compiled_flow(
    graph: CompiledStateGraph,
    state: dict[str, Any],
    config: dict[str, Any],
    step_guard: Any,
    tool_callback: Any,
    timeout_seconds: float,
) -> dict[str, Any]:
    context = copy_context()
    context.run(set_step_guard, step_guard)
    context.run(set_budget_callback, tool_callback)
    task = asyncio.create_task(graph.ainvoke(state, config=config), context=context)
    async with asyncio.timeout(timeout_seconds):
        return await task


async def invoke_flow_with_governance(
    *,
    skill: Skill,
    raw_input: dict[str, Any],
    deps: Any,
    timeout_seconds: float,
    step_budget: int,
    tool_round_budget: int,
    graph: CompiledStateGraph | None = None,
    recursion_limit: int | None = None,
    definition: str = "",
    definition_sha256: str | None = None,
) -> FlowGovernanceResult:
    started = time.perf_counter()
    steps = tool_rounds = 0
    status = "completed"
    output: dict[str, Any] = {}
    governance: dict[str, Any] = {"events": []}
    preflight_complete = False
    actual_hash = hashlib.sha256(definition.encode("utf-8")).hexdigest() if definition else None
    governance["preflight"] = {"status": "ok", "definition_sha256": actual_hash}

    async def charge_tool_call(node_name: str, elapsed_ms: float) -> None:
        del elapsed_ms
        nonlocal tool_rounds
        if node_name.startswith("__tool__:"):
            if tool_rounds + 1 > tool_round_budget:
                raise BudgetExhausted(f"tool budget exhausted before {node_name[9:]}")
            tool_rounds += 1
            return
    async def reserve_step(node_name: str) -> None:
        nonlocal steps
        if steps + 1 > step_budget:
            raise BudgetExhausted(f"step budget exhausted before {node_name}")
        steps += 1

    try:
        if definition_sha256 is not None and actual_hash != definition_sha256:
            raise FlowDenied("definition hash mismatch")
        analysis = compiler.step_analysis(skill)
        # Include the compiler-owned audit node. Reject before any node/tool side effect.
        execution_steps = analysis.step_bound + int(compiler.appends_audit(skill))
        if execution_steps > step_budget or analysis.tool_call_bound > tool_round_budget:
            status = "budget_exhausted"
        else:
            state, config = _prepare_flow_state(skill, raw_input, recursion_cap=recursion_limit)
            compiled = graph or compiler.compile(skill, deps)
            preflight_complete = True
            output = await _execute_compiled_flow(
                compiled, state, config,
                reserve_step, charge_tool_call, timeout_seconds
            )
            if output.get("fatal_error"):
                status = (
                    "budget_exhausted"
                    if str(output["fatal_error"]).startswith("budget_exhausted:")
                    else "error"
                )
                governance["error"] = str(output["fatal_error"])
    except TimeoutError:
        status = "timeout"
    except Exception as exc:
        status = "error"
        if not preflight_complete:
            governance["preflight"]["status"] = "error"
        governance["error"] = str(exc)
    finally:
        governance["events"].append("workflow_completed")
        governance["finalize"] = {
            "event_type": "workflow_completed",
            "skill_name": skill.name,
            "status": status,
            "steps_consumed": steps,
            "tool_rounds_consumed": tool_rounds,
            "elapsed_ms": round((time.perf_counter() - started) * 1000, 3),
        }
    return FlowGovernanceResult(status, _public_flow_output(output), governance)


class _NoopAuditRepository:
    async def save(self, trail: Any) -> None:
        return None


class _RuntimeDeps:
    """Expose only governed runtime dependencies and replace audit output."""

    _ALLOWED = frozenset(
        {
            "script_runner",
            "llm",
            "glossary",
            "searchers",
            "reranker",
            "locators",
            "default_top_k",
            "max_retrieval_attempts",
            "intent_confidence_threshold",
        }
    )

    def __init__(self, base: Any):
        self._base = base
        self._audit = _NoopAuditRepository()

    @property
    def audit_repo(self) -> _NoopAuditRepository:
        return self._audit

    def __getattr__(self, name: str) -> Any:
        if name not in self._ALLOWED or self._base is None:
            raise AttributeError(f"_RuntimeDeps does not expose '{name}'")
        return getattr(self._base, name)


async def invoke_pinned_flow(
    *,
    artifact: LoadedSkillArtifact,
    raw_input: dict[str, Any],
    snapshot: DirectAgentExecutionSnapshot,
    rule_tools: frozenset[str] | None,
    deps: Any,
    timeout_seconds: float,
    recursion_cap: int,
    remaining_tool_rounds: int,
    remaining_steps: int = 100,
) -> FlowResult:
    if artifact.kind != "flow":
        raise FlowDenied("not a flow skill")
    runtime_deps = _RuntimeDeps(deps)
    effective = effective_tool_names(
        snapshot, artifact=artifact, rule_tools=rule_tools, deps=deps
    )
    _validate_steps(artifact.skill.flow, effective)
    analysis = compiler.step_analysis(artifact.skill)
    tool_calls_bound = analysis.tool_call_bound
    steps_bound = min(
        analysis.step_bound + int(compiler.appends_audit(artifact.skill)), recursion_cap
    )
    if steps_bound > remaining_steps or tool_calls_bound > remaining_tool_rounds:
        return FlowResult(
            status="budget_exhausted",
            content="{}",
            tool_calls_bound=tool_calls_bound,
            steps_bound=steps_bound,
            steps_consumed=0,
            tool_rounds_consumed=0,
        )
    cleaned = clean_invoke_input(raw_input)
    input_model = build_input_model(artifact.skill)
    if input_model is not None:
        try:
            input_model.model_validate(cleaned)
        except ValidationError as exc:
            raise FlowDenied("flow input failed its pinned schema") from exc
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
    steps_consumed = 0
    tool_rounds_consumed = 0

    async def _on_tool(node_name: str, elapsed_ms: float) -> None:
        del elapsed_ms
        nonlocal tool_rounds_consumed
        if node_name.startswith("__tool__:"):
            if tool_rounds_consumed + 1 > remaining_tool_rounds:
                raise BudgetExhausted(
                    f"tool budget exhausted before {node_name[9:]}"
                )
            tool_rounds_consumed += 1
            return
    async def _reserve_step(node_name: str) -> None:
        nonlocal steps_consumed
        if steps_consumed + 1 > remaining_steps:
            raise BudgetExhausted(f"step budget exhausted before {node_name}")
        steps_consumed += 1

    try:
        graph = compiler.compile(artifact.skill, runtime_deps)
        state, config = _prepare_flow_state(
            artifact.skill, state, recursion_cap=max(2, recursion_cap)
        )
        output = await _execute_compiled_flow(
            graph, state, config, _reserve_step, _on_tool, timeout_seconds
        )
    except asyncio.CancelledError:
        raise
    except TimeoutError:
        return FlowResult(
            status="timeout",
            content="{}",
            tool_calls_bound=tool_calls_bound,
            steps_bound=steps_bound,
            steps_consumed=steps_consumed,
            tool_rounds_consumed=tool_rounds_consumed,
        )
    except FlowDenied:
        raise
    except Exception:
        return FlowResult(
            status="error",
            content="{}",
            tool_calls_bound=tool_calls_bound,
            steps_bound=steps_bound,
            steps_consumed=steps_consumed,
            tool_rounds_consumed=tool_rounds_consumed,
        )
    if str(output.get("fatal_error") or "").startswith("budget_exhausted:"):
        status = "budget_exhausted"
    else:
        status = "error" if output.get("fatal_error") else "completed"
    public = _public_flow_output(output)
    return FlowResult(
        status=status,
        content=_bounded_json(public),
        tool_calls_bound=tool_calls_bound,
        steps_bound=steps_bound,
        steps_consumed=steps_consumed,
        tool_rounds_consumed=tool_rounds_consumed,
    )


def _validate_steps(
    steps: list[dict[str, Any]],
    effective_tools: frozenset[str],
) -> None:
    for step in steps:
        parsed = parse_step(step)
        if parsed is None:
            raise FlowDenied("flow contains an invalid step")
        kind, body = parsed
        if kind == "tool":
            if body not in effective_tools:
                raise FlowDenied(f"tool {body} not in effective set")
        elif kind == "node":
            spec = resolve_node(body)
            if spec is None:
                raise FlowDenied(f"unknown node: {body}")
            denied = set(spec.requires_tools) - effective_tools
            if denied:
                raise FlowDenied(f"tool {sorted(denied)[0]} not in effective set")
        elif kind == "script":
            denied = set(compiler._script_contract(body).tools) - effective_tools
            if denied:
                raise FlowDenied(f"tool {sorted(denied)[0]} not in effective set")
        elif kind == "sequence":
            _validate_steps(body, effective_tools)
        elif kind == "branch":
            _validate_steps(body.get("then") or [], effective_tools)
            _validate_steps(body.get("else") or [], effective_tools)
        elif kind == "loop":
            maximum = body.get("max_iterations")
            if not isinstance(maximum, int) or maximum < 1:
                raise FlowDenied("flow loop is not bounded")
            _validate_steps(body.get("body") or [], effective_tools)
        else:
            raise FlowDenied("flow step kind is unsupported")


def _bounded_json(value: dict[str, Any]) -> str:
    def _deny(exc: TypeError | ValueError) -> str:
        raise FlowDenied("flow result is not serializable") from exc

    return bounded_canonical_json(
        value, max_chars=MAX_FLOW_RESULT_CHARS, on_unserializable=_deny
    )
