"""Fail-closed validator for the D4 constrained Harness Graph IR."""

from __future__ import annotations

import re
from collections import defaultdict, deque
from dataclasses import dataclass, field
from typing import Any

from app.orchestration.canonical import canonical_definition, canonical_json, sha256
from app.orchestration.catalog import get
from app.orchestration.models import GraphDiagnostic
from app.workflow_contracts import (
    AGENT_LOOP_REQUIRED_STAGES,
    AGENT_REQUIRED_STAGES,
    AGENT_STAGE_ORDER,
)

_ID = re.compile(r"^[A-Za-z][A-Za-z0-9_-]{0,127}$")
_FORBIDDEN_KEYS = frozenset(
    {
        "systemprompt",
        "prompt",
        "businessrule",
        "businessrules",
        "business_rule",
        "business_rules",
        "skillinstruction",
        "skill_instruction",
        "agentid",
        "agent_id",
        "agentrevision",
        "agent_revision",
    }
)
_VERIFIER_LOOP_REQUIRED = frozenset(
    {
        "model_step",
        "checkpoint_and_budget_gate",
    }
)
_WORKER_ONLY_TYPES = frozenset(
    {
        "load_skill",
        "tool_policy_and_approval_gate",
        "tool_call_and_observation",
    }
)
_ORCHESTRATOR_STAGE_ORDER = (
    "start",
    "acquire_context_and_analyze_problem",
    "sufficiency_gate",
    "decompose_work",
    "dispatch_agents",
    "join_worker_results",
    "invoke_verifier",
    "bounded_repair",
    "aggregate_results",
    "respond",
    "audit",
    "end",
)
_ORCHESTRATOR_REQUIRED = frozenset(_ORCHESTRATOR_STAGE_ORDER)
_EXPLICIT_LOOP_TYPES = frozenset({"bounded_agent_loop", "bounded_repair"})


@dataclass
class ValidationResult:
    valid: bool
    canonical_definition: dict[str, Any] | None = None
    canonical_ui_metadata: Any | None = None
    definition_sha256: str | None = None
    ui_metadata_sha256: str | None = None
    errors: list[GraphDiagnostic] = field(default_factory=list)


class _State:
    def __init__(self) -> None:
        self.errors: list[GraphDiagnostic] = []

    def error(
        self,
        path: str,
        code: str,
        message: str,
        *,
        node_id: str | None = None,
        edge_id: str | None = None,
    ) -> None:
        if len(self.errors) < 100:
            self.errors.append(
                GraphDiagnostic(
                    path=path,
                    code=code,
                    message=message,
                    node_id=node_id,
                    edge_id=edge_id,
                )
            )


def _find_forbidden(value: Any, path: str, state: _State) -> None:
    if isinstance(value, dict):
        for key, nested in value.items():
            key_text = str(key)
            compact = key_text.replace("-", "").replace("_", "").replace(" ", "").lower()
            if compact in _FORBIDDEN_KEYS:
                state.error(f"{path}.{key_text}", "forbidden_embedded_content", "Graph IR must use typed pinned references, not embedded prompt/rule/skill/Agent content.")
            _find_forbidden(nested, f"{path}.{key_text}", state)
    elif isinstance(value, list):
        for index, nested in enumerate(value):
            _find_forbidden(nested, f"{path}[{index}]", state)
    elif isinstance(value, str) and value.strip().lower() == "latest":
        state.error(path, "forbidden_latest_selector", "Graph IR must pin revisions and cannot use 'latest'.")


def _valid_positive(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def _validate_node(
    raw: Any,
    path: str,
    kind: str,
    state: _State,
    *,
    child: bool = False,
    runtime_variant: str | None = None,
) -> tuple[str, dict[str, Any]] | None:
    if not isinstance(raw, dict):
        state.error(path, "invalid_node", "Node must be a JSON object.")
        return None
    allowed_fields = {"id", "type", "typeVersion", "config", "children", "optional"}
    for key in raw:
        if key not in allowed_fields:
            state.error(f"{path}.{key}", "unknown_node_field", "Field is not part of the versioned node schema.")
    node_id = raw.get("id")
    if not isinstance(node_id, str) or not _ID.fullmatch(node_id):
        state.error(f"{path}.id", "invalid_node_id", "Node id must be a stable identifier.")
        return None
    type_name, version = raw.get("type"), raw.get("typeVersion")
    if not isinstance(type_name, str) or not isinstance(version, str):
        state.error(path, "invalid_node_type", "Node type and typeVersion are required.", node_id=node_id)
        return node_id, raw
    spec = get(type_name, version)
    if spec is None:
        state.error(f"{path}.type", "unknown_node_version", "Node type/version is not present in the server catalogue.", node_id=node_id)
    elif kind not in spec.kinds:
        state.error(f"{path}.type", "node_not_allowed_for_kind", f"Node '{type_name}' is not allowed for '{kind}'.", node_id=node_id)
    elif (
        kind == "agent-runtime"
        and spec.runtime_variants
        and runtime_variant not in spec.runtime_variants
    ):
        state.error(
            f"{path}.type",
            "node_not_allowed_for_runtime_variant",
            f"Node '{type_name}' is not allowed for runtimeVariant '{runtime_variant}'.",
            node_id=node_id,
        )
    config = raw.get("config", {})
    if not isinstance(config, dict):
        state.error(f"{path}.config", "invalid_node_config", "Node config must be an object.", node_id=node_id)
    elif spec is not None and spec.bounded:
        field_name = "maxIterations" if type_name in {"bounded_agent_loop", "bounded_repair"} else "maxRepairRounds"
        if not _valid_positive(config.get(field_name)):
            state.error(f"{path}.config.{field_name}", "unbounded_loop", "A bounded control node needs a positive explicit limit.", node_id=node_id)
    if isinstance(config, dict) and spec is not None:
        allowed_config = set((spec.config_schema or {}).get("properties", {}))
        for key in config:
            if key not in allowed_config:
                state.error(
                    f"{path}.config.{key}",
                    "unknown_config_field",
                    "Config field is not present in the versioned node schema.",
                    node_id=node_id,
                )
    children = raw.get("children", [])
    if children is not None and not isinstance(children, list):
        state.error(f"{path}.children", "invalid_children", "Children must be an array.", node_id=node_id)
    elif isinstance(children, list):
        seen: set[str] = set()
        for index, nested in enumerate(children):
            child_result = _validate_node(
                nested,
                f"{path}.children[{index}]",
                kind,
                state,
                child=True,
                runtime_variant=runtime_variant,
            )
            if child_result and child_result[0] in seen:
                state.error(f"{path}.children[{index}].id", "duplicate_node_id", "Node id is duplicated.", node_id=child_result[0])
            elif child_result:
                seen.add(child_result[0])
    return node_id, raw


def _reachable(
    start: str, graph: dict[str, list[str]], blocked: frozenset[str] = frozenset()
) -> set[str]:
    if start in blocked:
        return set()
    seen: set[str] = set()
    pending = [start]
    while pending:
        current = pending.pop()
        if current in seen:
            continue
        seen.add(current)
        pending.extend(target for target in graph.get(current, ()) if target not in blocked)
    return seen


def _strongly_connected_components(
    node_ids: set[str], forward: dict[str, list[str]], reverse: dict[str, list[str]]
) -> list[set[str]]:
    order: list[str] = []
    visited: set[str] = set()

    def visit(node_id: str) -> None:
        visited.add(node_id)
        for target in forward.get(node_id, ()):
            if target not in visited:
                visit(target)
        order.append(node_id)

    for node_id in sorted(node_ids):
        if node_id not in visited:
            visit(node_id)
    components: list[set[str]] = []
    visited.clear()
    for root in reversed(order):
        if root in visited:
            continue
        component: set[str] = set()
        pending = [root]
        while pending:
            current = pending.pop()
            if current in visited:
                continue
            visited.add(current)
            component.add(current)
            pending.extend(reverse.get(current, ()))
        components.append(component)
    return components


def _validate_cycles(
    by_id: dict[str, dict[str, Any]],
    control_edges: list[dict[str, Any]],
    forward: dict[str, list[str]],
    reverse: dict[str, list[str]],
    state: _State,
) -> None:
    types = {node_id: str(node.get("type", "")) for node_id, node in by_id.items()}
    for component in _strongly_connected_components(set(by_id), forward, reverse):
        has_cycle = len(component) > 1 or any(
            target == node_id for node_id in component for target in forward.get(node_id, ())
        )
        if not has_cycle:
            continue
        loop_ids = sorted(node_id for node_id in component if types[node_id] in _EXPLICIT_LOOP_TYPES)
        if len(loop_ids) != 1:
            state.error("$.definition.edges", "unbounded_cycle", "A cycle requires exactly one explicit bounded loop node.")
            continue
        loop_id = loop_ids[0]
        loop_edges = [edge for edge in control_edges if edge["source"]["nodeId"] == loop_id]
        return_edges = [
            edge for edge in control_edges
            if edge["target"]["nodeId"] == loop_id and edge["source"]["nodeId"] in component
        ]
        body = [edge for edge in loop_edges if edge["source"]["port"] == "body"]
        exits = [edge for edge in loop_edges if edge["source"]["port"] == "exit"]
        body_is_internal = len(body) == 1 and body[0]["target"]["nodeId"] in component
        exit_escapes = len(exits) == 1 and exits[0]["target"]["nodeId"] not in component
        only_continue_returns = len(return_edges) == 1 and return_edges[0]["target"]["port"] == "continue"
        only_body_or_exit_outputs = all(edge["source"]["port"] in {"body", "exit"} for edge in loop_edges)
        if not (body_is_internal and exit_escapes and only_continue_returns and only_body_or_exit_outputs):
            state.error(
                "$.definition.edges",
                "invalid_bounded_loop_contract",
                "A bounded cycle must use one body edge, one continue return edge, and one exit edge that leaves the loop body.",
                node_id=loop_id,
            )
            # Preserve the public generic rejection reason for clients that only
            # distinguish legal bounded loops from all other cycles.
            state.error("$.definition.edges", "unbounded_cycle", "Cycle does not satisfy the explicit bounded-loop body/exit contract.", node_id=loop_id)


def _validate_fanout(
    by_id: dict[str, dict[str, Any]],
    control_edges: list[dict[str, Any]],
    forward: dict[str, list[str]],
    state: _State,
) -> None:
    targets_by_port: dict[tuple[str, str], list[str]] = defaultdict(list)
    for edge in control_edges:
        targets_by_port[(edge["source"]["nodeId"], edge["source"]["port"])].append(edge["target"]["nodeId"])
    joins = [node_id for node_id, node in by_id.items() if node.get("type") == "join_worker_results"]
    for (source_id, _port), targets in targets_by_port.items():
        if len(targets) < 2:
            continue
        matching = [
            join_id for join_id in joins
            if all(join_id in _reachable(target, forward) for target in targets)
        ]
        if not matching:
            state.error("$.definition.edges", "fanout_missing_matching_join", "Every control fan-out must converge at a matching Join node.", node_id=source_id)
            state.error("$.definition.edges", "fanout_missing_join", "Fan-out requires a matching Join node.", node_id=source_id)
            continue
        join_id = matching[0]
        for target in targets:
            if any(node.get("type") == "end" for node_id, node in by_id.items() if node_id in _reachable(target, forward, frozenset({join_id}))):
                state.error("$.definition.edges", "fanout_branch_bypasses_join", "A fan-out branch must not terminate while bypassing its matching Join node.", node_id=source_id)


def _validate_stage_order(
    kind: str,
    by_id: dict[str, dict[str, Any]],
    forward: dict[str, list[str]],
    state: _State,
) -> None:
    order = _ORCHESTRATOR_STAGE_ORDER if kind == "orchestrator" else AGENT_STAGE_ORDER if kind == "agent-runtime" else ()
    if not order:
        return
    ids_by_type: dict[str, list[str]] = defaultdict(list)
    for node_id, node in by_id.items():
        ids_by_type[str(node.get("type"))].append(node_id)
    for stage in order:
        if len(ids_by_type[stage]) > 1:
            state.error("$.definition.nodes", "duplicate_required_stage", f"Governance stage '{stage}' may appear only once.")
    if any(len(ids_by_type[stage]) != 1 for stage in order):
        return
    stage_ids = [ids_by_type[stage][0] for stage in order]
    start_id, end_id = stage_ids[0], stage_ids[-1]
    for stage, node_id in zip(order[1:-1], stage_ids[1:-1], strict=True):
        if end_id in _reachable(start_id, forward, frozenset({node_id})):
            state.error("$.definition.edges", "governance_stage_bypassed", f"Every control path must include required stage '{stage}'.", node_id=node_id)
    for previous, current, stage in zip(stage_ids[:-1], stage_ids[1:], order[1:], strict=True):
        if current not in _reachable(previous, forward):
            state.error("$.definition.edges", "governance_stage_order", f"Required stage '{stage}' must follow its governance predecessor.", node_id=current)


def _validate_topology(
    nodes: list[dict[str, Any]], control_edges: list[dict[str, Any]], kind: str, state: _State
) -> None:
    by_id = {node["id"]: node for node in nodes if isinstance(node.get("id"), str)}
    types = {node_id: str(node.get("type", "")) for node_id, node in by_id.items()}
    starts = [node_id for node_id, node_type in types.items() if node_type == "start"]
    ends = [node_id for node_id, node_type in types.items() if node_type == "end"]
    if len(starts) != 1:
        state.error("$.definition.nodes", "start_count", "Graph must contain exactly one Start node.")
    if len(ends) != 1:
        state.error("$.definition.nodes", "end_count", "Graph must contain exactly one End node.")
    forward: dict[str, list[str]] = defaultdict(list)
    reverse: dict[str, list[str]] = defaultdict(list)
    for edge in control_edges:
        source = edge["source"]["nodeId"]
        target = edge["target"]["nodeId"]
        forward[source].append(target)
        reverse[target].append(source)
    if len(starts) == 1:
        reached = _reachable(starts[0], forward)
        for node_id in sorted(set(by_id) - reached):
            state.error("$.definition.nodes", "unreachable_node", "Node is unreachable from Start.", node_id=node_id)
    if len(ends) == 1:
        reaches_end = _reachable(ends[0], reverse)
        for node_id in sorted(set(by_id) - reaches_end):
            state.error("$.definition.nodes", "dead_end", "Node cannot reach End.", node_id=node_id)

    _validate_cycles(by_id, control_edges, forward, reverse, state)
    _validate_fanout(by_id, control_edges, forward, state)
    _validate_stage_order(kind, by_id, forward, state)


def _validate_edges(
    edges_raw: list[Any],
    nodes: list[dict[str, Any]],
    ids: set[str],
    state: _State,
) -> tuple[list[dict[str, Any]], dict[tuple[str, str], int], dict[str, Any]]:
    """逐條驗證 edge，回 (typed control edges, 每個 input port 的連線數, node → 型別規格)。

    第一個錯誤即 `continue`：同一條 edge 不重複報錯，錯誤碼與產出順序與展開時相同。
    """
    control_edges: list[dict[str, Any]] = []
    edge_ids: set[str] = set()
    semantic_edges: set[tuple[str, str, str, str]] = set()
    input_connections: dict[tuple[str, str], int] = defaultdict(int)
    node_specs = {node["id"]: get(str(node.get("type")), str(node.get("typeVersion"))) for node in nodes}
    for index, raw in enumerate(edges_raw):
        path = f"$.definition.edges[{index}]"
        if not isinstance(raw, dict) or not isinstance(raw.get("id"), str) or not _ID.fullmatch(raw.get("id", "")):
            state.error(path, "invalid_edge", "Edge needs a stable id and endpoint objects.")
            continue
        for key in raw:
            if key not in {"id", "source", "target"}:
                state.error(f"{path}.{key}", "unknown_edge_field", "Field is not part of the versioned edge schema.", edge_id=raw["id"])
        edge_id = raw["id"]
        if edge_id in edge_ids:
            state.error(f"{path}.id", "duplicate_edge_id", "Edge id is duplicated.", edge_id=edge_id)
            continue
        edge_ids.add(edge_id)
        source, target = raw.get("source"), raw.get("target")
        if not isinstance(source, dict) or not isinstance(target, dict):
            state.error(path, "invalid_edge_endpoint", "Edge source and target must be objects.", edge_id=edge_id)
            continue
        if set(source) != {"nodeId", "port"} or set(target) != {"nodeId", "port"}:
            state.error(path, "invalid_edge_endpoint", "Edge endpoints must contain only nodeId and port.", edge_id=edge_id)
            continue
        source_id, source_port = source.get("nodeId"), source.get("port")
        target_id, target_port = target.get("nodeId"), target.get("port")
        if not all(isinstance(value, str) for value in (source_id, source_port, target_id, target_port)):
            state.error(path, "invalid_edge_endpoint", "Edge nodeId and port values must be strings.", edge_id=edge_id)
            continue
        if source_id not in ids or target_id not in ids:
            state.error(path, "dangling_edge", "Edge references an unknown node.", edge_id=edge_id)
            continue
        source_spec, target_spec = node_specs[source_id], node_specs[target_id]
        output = next((port for port in (source_spec.outputs if source_spec else ()) if port.id == source_port), None)
        input_port = next((port for port in (target_spec.inputs if target_spec else ()) if port.id == target_port), None)
        if output is None or input_port is None:
            state.error(path, "unknown_port", "Edge must connect a declared output port to a declared input port.", edge_id=edge_id)
            continue
        if output.data_type != input_port.data_type:
            state.error(path, "incompatible_port_type", "Source and target port types are incompatible.", edge_id=edge_id)
            continue
        semantic = (source_id, source_port, target_id, target_port)
        if semantic in semantic_edges:
            state.error(path, "duplicate_semantic_edge", "A semantic edge may appear only once even when edge ids differ.", edge_id=edge_id)
            continue
        semantic_edges.add(semantic)
        # Topology is checked over every typed control edge, including one
        # that separately violates input cardinality.  This keeps malformed
        # graphs from hiding a cycle or fan-out behind a second error.
        if output.data_type == "Control":
            control_edges.append(raw)
        input_key = (target_id, target_port)
        input_connections[input_key] += 1
        if input_port.max_connections is not None and input_connections[input_key] > input_port.max_connections:
            state.error(path, "input_port_cardinality_exceeded", f"Input port '{target_port}' accepts at most {input_port.max_connections} connection(s).", edge_id=edge_id)
    return control_edges, input_connections, node_specs


def validate(definition: Any, ui_metadata: Any = None) -> ValidationResult:
    state = _State()
    if not isinstance(definition, dict):
        state.error("$.definition", "invalid_definition", "Definition must be a JSON object.")
        return ValidationResult(valid=False, errors=state.errors)
    _find_forbidden(definition, "$.definition", state)
    for key in definition:
        if key not in {"schemaVersion", "kind", "runtimeVariant", "nodes", "edges", "governance"}:
            state.error(f"$.definition.{key}", "unknown_definition_field", "Field is not part of Graph IR schemaVersion 1.")
    if definition.get("schemaVersion") != 1:
        state.error("$.definition.schemaVersion", "unsupported_schema_version", "Only Graph IR schemaVersion 1 is supported.")
    kind = definition.get("kind")
    if kind == "subflow":
        state.error("$.definition.kind", "subflow_not_supported", "Executable subflow is reserved for a future pinned Call Workflow contract and is not supported in D4.")
        kind = ""
    runtime_variant = definition.get("runtimeVariant")
    if kind == "agent-runtime" and runtime_variant not in {"worker", "verifier"}:
        state.error(
            "$.definition.runtimeVariant",
            "invalid_runtime_variant",
            "agent-runtime requires runtimeVariant worker or verifier.",
        )
    if kind != "agent-runtime" and "runtimeVariant" in definition:
        state.error(
            "$.definition.runtimeVariant",
            "runtime_variant_not_allowed",
            "runtimeVariant is only valid for agent-runtime workflows.",
        )
    elif kind not in {"orchestrator", "agent-runtime"}:
        state.error("$.definition.kind", "unsupported_workflow_kind", "kind must be orchestrator or agent-runtime.")
        kind = ""
    nodes_raw = definition.get("nodes")
    edges_raw = definition.get("edges")
    governance = definition.get("governance")
    if not isinstance(nodes_raw, list) or not nodes_raw:
        state.error("$.definition.nodes", "invalid_nodes", "nodes must be a non-empty array.")
        nodes_raw = []
    if not isinstance(edges_raw, list):
        state.error("$.definition.edges", "invalid_edges", "edges must be an array.")
        edges_raw = []
    if not isinstance(governance, dict):
        state.error("$.definition.governance", "invalid_governance", "governance must be an object.")
        governance = {}
    elif set(governance) - {"maxSteps", "maxConcurrency"}:
        for key in sorted(set(governance) - {"maxSteps", "maxConcurrency"}):
            state.error(f"$.definition.governance.{key}", "unknown_governance_field", "Field is not part of governance schemaVersion 1.")
    if ui_metadata is not None and not isinstance(ui_metadata, dict):
        state.error("$.ui_metadata", "invalid_ui_metadata", "ui_metadata must be an object independent from semantic Graph IR.")
    for field_name in ("maxSteps", "maxConcurrency"):
        if not _valid_positive(governance.get(field_name)):
            state.error(f"$.definition.governance.{field_name}", "invalid_governance_limit", "Governance limit must be a positive integer.")

    nodes: list[dict[str, Any]] = []
    ids: set[str] = set()
    for index, raw in enumerate(nodes_raw):
        result = _validate_node(
            raw,
            f"$.definition.nodes[{index}]",
            kind,
            state,
            runtime_variant=runtime_variant if isinstance(runtime_variant, str) else None,
        )
        if result is not None:
            node_id, node = result
            if node_id in ids:
                state.error(f"$.definition.nodes[{index}].id", "duplicate_node_id", "Node id is duplicated.", node_id=node_id)
            else:
                ids.add(node_id)
                nodes.append(node)

    control_edges, input_connections, node_specs = _validate_edges(
        edges_raw, nodes, ids, state
    )

    if nodes:
        _validate_topology(nodes, control_edges, kind, state)
    for node_id, spec in node_specs.items():
        if spec is None:
            continue
        for port in spec.inputs:
            if port.required and input_connections[(node_id, port.id)] == 0:
                state.error(
                    "$.definition.edges",
                    "missing_required_input",
                    f"Required input port '{port.id}' is not connected.",
                    node_id=node_id,
                )
    types = {str(node.get("type")) for node in nodes}
    required = AGENT_REQUIRED_STAGES if kind == "agent-runtime" else _ORCHESTRATOR_REQUIRED if kind == "orchestrator" else frozenset()
    for missing in sorted(required - types):
        state.error("$.definition.nodes", "missing_required_stage", f"Graph kind '{kind}' requires '{missing}'.")
    if kind == "agent-runtime":
        loops = [node for node in nodes if node.get("type") == "bounded_agent_loop"]
        if loops:
            child_types = {str(child.get("type")) for child in loops[0].get("children", []) if isinstance(child, dict)}
            loop_required = (
                _VERIFIER_LOOP_REQUIRED
                if runtime_variant == "verifier"
                else AGENT_LOOP_REQUIRED_STAGES
            )
            for missing in sorted(loop_required - child_types):
                state.error("$.definition.nodes", "missing_required_loop_stage", f"bounded_agent_loop requires '{missing}'.", node_id=str(loops[0].get("id")))
            if runtime_variant == "verifier":
                for forbidden in sorted(child_types & _WORKER_ONLY_TYPES):
                    state.error(
                        "$.definition.nodes",
                        "verifier_forbidden_stage",
                        f"verifier runtime cannot contain '{forbidden}'.",
                        node_id=str(loops[0].get("id")),
                    )
    if kind == "orchestrator" and any(node.get("type") == "dispatch_agents" for node in nodes):
        if not _valid_positive(governance.get("maxConcurrency")):
            state.error("$.definition.governance.maxConcurrency", "fanout_missing_concurrency_budget", "dispatch_agents needs maxConcurrency.")

    if state.errors:
        return ValidationResult(valid=False, errors=state.errors)
    try:
        canonical_def = canonical_definition(definition)
        canonical_ui = canonical_json({} if ui_metadata is None else ui_metadata)
        return ValidationResult(
            valid=True,
            canonical_definition=canonical_def,
            canonical_ui_metadata=canonical_ui,
            definition_sha256=sha256(canonical_def),
            ui_metadata_sha256=sha256(canonical_ui),
        )
    except (TypeError, ValueError):
        return ValidationResult(
            valid=False,
            errors=[GraphDiagnostic(path="$.definition", code="invalid_json_value", message="Definition and UI metadata must contain finite JSON values.")],
        )
