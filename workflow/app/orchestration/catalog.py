"""Versioned, server-owned Harness node catalogue.

The catalogue intentionally exposes authoring metadata, not executable
callables.  Runtime adapters and authority are server-owned in later phases.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from app.workflow_contracts import GRAPH_IR_COMPILER_CONTRACT_VERSION


CONTROL = "Control"


@dataclass(frozen=True)
class Port:
    id: str
    data_type: str = CONTROL
    required: bool = True
    max_connections: int | None = 1

    def wire(self) -> dict[str, Any]:
        wire = {"id": self.id, "dataType": self.data_type, "required": self.required}
        if self.max_connections is not None:
            wire["maxConnections"] = self.max_connections
        return wire


@dataclass(frozen=True)
class NodeType:
    type: str
    version: str
    title: str
    kinds: frozenset[str]
    inputs: tuple[Port, ...] = ()
    outputs: tuple[Port, ...] = ()
    config_schema: dict[str, Any] | None = None
    risk: str = "orchestration"
    runtime_policy: str = "run.control"
    required_for: frozenset[str] = frozenset()
    runtime_variants: frozenset[str] = frozenset()
    bounded: bool = False

    def wire(self) -> dict[str, Any]:
        wire = {
            "type": self.type,
            "version": self.version,
            "title": self.title,
            "kind": "control",
            "inputs": [port.wire() for port in self.inputs],
            "outputs": [port.wire() for port in self.outputs],
            "configSchema": self.config_schema or {},
            "risk": self.risk,
            "authoringCapability": "workflow.manage",
            "catalogVisibility": "system-admin",
            "runtimePolicy": self.runtime_policy,
            "runtimeAdapter": "server-owned",
            "workflowKinds": sorted(self.kinds),
            "requiredStage": bool(self.required_for),
        }
        if self.runtime_variants:
            wire["runtimeVariants"] = sorted(self.runtime_variants)
        return wire


IN = (Port("in"),)
OUT = (Port("out", required=False, max_connections=None),)

# A loop cannot be inferred from an arbitrary back edge.  These ports make the
# body, return, and escape edge independently checkable by the compiler.
LOOP_INPUTS = (Port("in"), Port("continue", required=False))
LOOP_OUTPUTS = (
    Port("out", required=False, max_connections=None),  # compatibility / acyclic use
    Port("body", required=False, max_connections=None),
    Port("exit", required=False, max_connections=None),
)


def _node(
    name: str,
    *,
    kinds: tuple[str, ...],
    required_for: tuple[str, ...] = (),
    bounded: bool = False,
    inputs: tuple[Port, ...] = IN,
    outputs: tuple[Port, ...] = OUT,
    runtime_policy: str = "run.control",
    runtime_variants: tuple[str, ...] = (),
) -> NodeType:
    config_schema: dict[str, Any] = {"type": "object", "additionalProperties": False}
    if name in {"bounded_agent_loop", "bounded_repair"}:
        config_schema = {
            "type": "object",
            "additionalProperties": False,
            "required": ["maxIterations"],
            "properties": {"maxIterations": {"type": "integer", "minimum": 1}},
        }
    elif name == "bounded_repair_or_controlled_failure":
        config_schema = {
            "type": "object",
            "additionalProperties": False,
            "required": ["maxRepairRounds"],
            "properties": {"maxRepairRounds": {"type": "integer", "minimum": 1}},
        }
    return NodeType(
        type=name,
        version="1.0",
        title=name.replace("_", " ").title(),
        kinds=frozenset(kinds),
        inputs=inputs,
        outputs=outputs,
        config_schema=config_schema,
        required_for=frozenset(required_for),
        runtime_variants=frozenset(runtime_variants),
        bounded=bounded,
        runtime_policy=runtime_policy,
    )


_ALL: tuple[NodeType, ...] = (
    _node("start", kinds=("orchestrator", "agent-runtime"), inputs=(), required_for=("orchestrator", "agent-runtime")),
    _node("end", kinds=("orchestrator", "agent-runtime"), outputs=(), required_for=("orchestrator", "agent-runtime")),
    _node("dependency_and_capability_preflight", kinds=("agent-runtime",), required_for=("agent-runtime",), runtime_variants=("worker", "verifier")),
    _node("inject_authorized_context", kinds=("agent-runtime",), required_for=("agent-runtime",), runtime_variants=("worker", "verifier")),
    _node("checkpoint", kinds=("agent-runtime",), required_for=("agent-runtime",), runtime_variants=("worker", "verifier")),
    _node(
        "bounded_agent_loop", kinds=("agent-runtime",), required_for=("agent-runtime",),
        bounded=True, inputs=LOOP_INPUTS, outputs=LOOP_OUTPUTS, runtime_policy="agent.step",
        runtime_variants=("worker", "verifier"),
    ),
    _node("model_step", kinds=("agent-runtime",), runtime_policy="agent.step", runtime_variants=("worker", "verifier")),
    _node("load_skill", kinds=("agent-runtime",), runtime_policy="skill.load", runtime_variants=("worker",)),
    _node("tool_policy_and_approval_gate", kinds=("agent-runtime",), runtime_policy="tool.gate", runtime_variants=("worker",)),
    _node("tool_call_and_observation", kinds=("agent-runtime",), runtime_policy="tool.invoke", runtime_variants=("worker",)),
    _node("checkpoint_and_budget_gate", kinds=("agent-runtime",), runtime_policy="run.control", runtime_variants=("worker", "verifier")),
    _node("validate_structured_output", kinds=("agent-runtime",), required_for=("agent-runtime",), runtime_variants=("worker", "verifier")),
    _node("bounded_repair_or_controlled_failure", kinds=("agent-runtime",), required_for=("agent-runtime",), bounded=True, runtime_variants=("worker", "verifier")),
    _node("acquire_context_and_analyze_problem", kinds=("orchestrator",), required_for=("orchestrator",), runtime_policy="context.read"),
    _node("sufficiency_gate", kinds=("orchestrator",), required_for=("orchestrator",)),
    NodeType(
        type="decompose_work", version="1.0", title="Decompose Work",
        kinds=frozenset({"orchestrator"}), inputs=IN,
        outputs=OUT + (Port("tasks", "TaskAssignment[]"),),
        config_schema={"type": "object", "additionalProperties": False},
        required_for=frozenset({"orchestrator"}),
    ),
    NodeType(
        type="dispatch_agents", version="1.0", title="Dispatch Agents",
        kinds=frozenset({"orchestrator"}),
        inputs=IN + (Port("tasks", "TaskAssignment[]"),),
        outputs=OUT + (Port("results", "WorkerResult[]", required=False),),
        config_schema={"type": "object", "additionalProperties": False},
        required_for=frozenset({"orchestrator"}),
        runtime_policy="orchestration.dispatch",
    ),
    NodeType(
        type="join_worker_results", version="1.0", title="Join Worker Results",
        kinds=frozenset({"orchestrator"}),
        inputs=(Port("in", max_connections=None), Port("results", "WorkerResult[]")),
        outputs=OUT + (Port("acceptedResults", "WorkerResult[]", required=False),),
        config_schema={"type": "object", "additionalProperties": False},
        required_for=frozenset({"orchestrator"}),
    ),
    _node("invoke_verifier", kinds=("orchestrator",), required_for=("orchestrator",), runtime_policy="verification.execute"),
    _node(
        "bounded_repair", kinds=("orchestrator",), required_for=("orchestrator",),
        bounded=True, inputs=LOOP_INPUTS, outputs=LOOP_OUTPUTS,
    ),
    _node("aggregate_results", kinds=("orchestrator",), required_for=("orchestrator",)),
    _node("respond", kinds=("orchestrator",), required_for=("orchestrator",)),
    _node("audit", kinds=("orchestrator",), required_for=("orchestrator",), runtime_policy="run.control"),
)

CATALOG: dict[tuple[str, str], NodeType] = {(node.type, node.version): node for node in _ALL}
CATALOG_VERSION = "1"
COMPILER_CONTRACT_VERSION = GRAPH_IR_COMPILER_CONTRACT_VERSION


def get(type_name: str, version: str) -> NodeType | None:
    return CATALOG.get((type_name, version))


def all_wire() -> list[dict[str, Any]]:
    return [node.wire() for node in sorted(_ALL, key=lambda item: (item.type, item.version))]
