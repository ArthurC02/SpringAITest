"""Agent Skill 圖工廠：用統一 invoke 面執行能力包的相容橋接層。

Agent Skill 不是 Business Workflow；這張單節點圖只是為了維持
``/skills/{name}/invoke`` 的統一 ``{skill, output}`` 契約。執行仍經 Node Shell
與強制 audit 終端，套件作者無法注入圖節點契約。
"""

from typing import Any

from langgraph.graph import END, START, StateGraph
from langgraph.graph.state import CompiledStateGraph

from app.engine import skill as skill_mod
from app.engine.graph_primitives import (
    AUDIT_NODE,
    SkillCompileError,
    add_contract_node,
    build_state_schema,
)
from app.engine.node_shell import IDENTITY_KEYS, harnessed
from app.engine.skill import Skill

AGENT_RUNNER_NODE = "agent_skill_runner"


def build(skill: Skill, deps: Any) -> CompiledStateGraph:
    """建立 agentic runner + audit 圖，共用 kind-neutral 治理原語。"""
    audit_spec = skill_mod.resolve_node(AUDIT_NODE)
    if audit_spec is None:
        raise SkillCompileError(f"稽核節點 {AUDIT_NODE} 未註冊")
    runner_spec = skill_mod.resolve_node(AGENT_RUNNER_NODE)
    if runner_spec is None:
        raise SkillCompileError(f"agentic runner 節點 {AGENT_RUNNER_NODE} 未註冊")

    reader = getattr(deps, "agent_package_reader", None)
    if reader is None:
        raise SkillCompileError(
            "agentic skill 需要 deps.agent_package_reader（package reader port 未注入）"
        )
    chat_model_factory = getattr(deps, "agent_chat_model", None)
    if chat_model_factory is None:
        raise SkillCompileError(
            "agentic skill 需要 deps.agent_chat_model（LLM factory 未注入）"
        )

    graph = StateGraph(build_state_schema(skill, [runner_spec], audit_spec))

    runner_fn = runner_spec.build(
        deps,
        reader=reader,
        chat_model_factory=chat_model_factory,
        container_deps=deps,
        skill_name=skill.name,
        uses_tools=list(skill.uses_tools),
        input_keys=tuple(skill.input_schema),
        timeout_s=skill.timeout_seconds,
    )
    llm_version = str(getattr(getattr(deps, "llm", None), "version", "") or "")
    runner_reads = (
        set(IDENTITY_KEYS) | set(runner_spec.reads) | set(skill.input_schema)
    )
    graph.add_node(
        AGENT_RUNNER_NODE,
        harnessed(
            runner_spec.name,
            runner_fn,
            run_on_fatal=runner_spec.run_on_fatal,
            component_version=llm_version,
            writes=runner_spec.writes,
            reads=runner_reads,
        ),
    )
    graph.add_edge(START, AGENT_RUNNER_NODE)
    audit_id = f"n1_{audit_spec.name}"
    add_contract_node(
        graph,
        node_id=audit_id,
        spec=audit_spec,
        deps=deps,
        params={},
        llm_version=llm_version,
    )
    graph.add_edge(AGENT_RUNNER_NODE, audit_id)
    graph.add_edge(audit_id, END)
    return graph.compile()
