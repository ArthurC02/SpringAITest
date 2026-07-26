"""Version identifiers and stage contracts shared by Workflow-owned authoring and runtime paths.

The Agent-Runtime stage names are one contract with two enforcement points: the
D4 validator rejects a graph that omits them at authoring time, and the D3
runtime preflight rejects a snapshot that omits them at execution time. Keeping
the names in one place is what makes those two checks provably the same set.
"""

GRAPH_IR_COMPILER_CONTRACT_VERSION = "d4-graph-ir-1"

AGENT_STAGE_ORDER = (
    "start",
    "dependency_and_capability_preflight",
    "inject_authorized_context",
    "checkpoint",
    "bounded_agent_loop",
    "validate_structured_output",
    "bounded_repair_or_controlled_failure",
    "end",
)
AGENT_REQUIRED_STAGES = frozenset(AGENT_STAGE_ORDER)
AGENT_LOOP_REQUIRED_STAGES = frozenset(
    {
        "model_step",
        "tool_policy_and_approval_gate",
        "tool_call_and_observation",
        "checkpoint_and_budget_gate",
    }
)
