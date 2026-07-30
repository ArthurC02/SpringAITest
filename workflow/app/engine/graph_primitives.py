"""Business Workflow 與 Agent Skill 共用的 kind-neutral 建圖原語。

這裡只擁有 state channel 建立、節點 reads 契約、Node Shell 掛載與強制
audit 節點名稱；不理解 Business Workflow 控制流，也不執行 Agent Skill package。
"""

import operator
from typing import Annotated, Any, Iterable, TypedDict

from langgraph.graph import StateGraph

from app.engine import skill as skill_mod
from app.engine.node_registry import NodeSpec
from app.engine.node_shell import harnessed
from app.engine.skill import Skill

AUDIT_NODE = "audit_feedback"


class SkillCompileError(ValueError):
    """Artifact 無法編譯成受治理的圖。"""


def build_state_schema(
    skill: Skill,
    specs: Iterable[NodeSpec],
    audit_spec: NodeSpec,
    *,
    internal_keys: Iterable[str] = (),
    extra_keys: Iterable[str] = (),
) -> type:
    """依 artifact input 與節點契約建立動態 state schema。"""
    appends: set[str] = set()
    keys: set[str] = set(skill_mod.RESERVED_KEYS) | set(skill.input_schema)
    for spec in [*specs, audit_spec]:
        keys.update(spec.reads)
        keys.update(spec.writes)
        appends.update(spec.appends)
    keys.update(internal_keys)
    keys.update(extra_keys)
    keys -= skill_mod.ENGINE_KEYS

    annotations: dict[str, Any] = {key: Any for key in sorted(keys)}
    annotations["trace"] = Annotated[list, operator.add]
    annotations["errors"] = Annotated[list, operator.add]
    annotations["fatal_error"] = Any
    for key in sorted(appends):
        annotations[key] = Annotated[list, operator.add]
    return TypedDict("SkillState", annotations, total=False)  # type: ignore[operator]


def effective_reads(spec: NodeSpec, params: dict[str, Any]) -> set[str]:
    """解析節點契約的靜態、動態與 fatal-path reads。"""
    reads: set[str] = set(spec.reads)
    for param in spec.dynamic_reads:
        value = params.get(param)
        if isinstance(value, str):
            reads.add(value)
        elif isinstance(value, list):
            reads.update(item for item in value if isinstance(item, str))
    if spec.run_on_fatal:
        reads |= skill_mod.ENGINE_KEYS
    return reads


def add_contract_node(
    graph: StateGraph,
    *,
    node_id: str,
    spec: NodeSpec,
    deps: Any,
    params: dict[str, Any],
    llm_version: str,
) -> None:
    """將已註冊節點透過共用 Node Shell 契約掛入圖。"""
    graph.add_node(
        node_id,
        harnessed(
            spec.name,
            spec.build(deps, **params),
            run_on_fatal=spec.run_on_fatal,
            component_version=llm_version if "llm" in spec.deps else "",
            writes=spec.writes,
            reads=effective_reads(spec, params) or None,
        ),
    )
