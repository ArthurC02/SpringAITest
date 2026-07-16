"""kb_query 圖的組裝：依賴注入 + Node Registry 的十個節點（經 Harness 包裝）+ 驗證閘門迴圈。

流程（Mermaid）::

    flowchart TD
        START --> query_intake
        query_intake --> query_rewrite
        query_rewrite --> intent_classification
        intent_classification --> context_resolver
        context_resolver --> retrieval_planner
        retrieval_planner --> source_retrieval_rerank
        source_retrieval_rerank --> data_locator
        data_locator --> evidence_verification
        evidence_verification -- retry --> retrieval_planner
        evidence_verification -- compose --> answer_composer
        answer_composer --> audit_feedback
        audit_feedback --> END
"""

from dataclasses import dataclass

from langgraph.graph import END, START, StateGraph
from langgraph.graph.state import CompiledStateGraph

from app.engine import node_registry
from app.engine.harness import harnessed

# import 觸發十個節點的 @node 註冊；圖只認名字，節點本體由 registry 提供
from app.kbquery import nodes as _nodes  # noqa: F401
from app.kbquery.ports import (
    AuditRepositoryPort,
    EvidenceLocatorPort,
    GlossaryPort,
    RerankerPort,
    SearchPort,
    StructuredLLMPort,
)
from app.kbquery.routing import route_after_verification
from app.kbquery.state import KbQueryState

# 圖上的節點 (name, version)（拓樸見 module docstring）；實際邊在下方明確連接。
# 版本一律鎖死：日後有人註冊 data_locator@2.0 時，本圖不會靜默改用新版，
# 切版必須是這裡的一次明確 code change（P2 的 skills/kb_query.yaml 同樣寫 node@version）。
_NODES = (
    ("query_intake", "1.0"),
    ("query_rewrite", "1.0"),
    ("intent_classification", "1.0"),
    ("context_resolver", "1.0"),
    ("retrieval_planner", "1.0"),
    ("source_retrieval_rerank", "1.0"),
    ("data_locator", "1.0"),
    ("evidence_verification", "1.0"),
    ("answer_composer", "1.0"),
    ("audit_feedback", "1.0"),
)

# 只有呼叫 LLM 的節點需要在 trace 記下模型版本
_LLM_NODES = frozenset({"query_rewrite", "intent_classification"})


@dataclass
class KbQueryDeps:
    """kb_query 的所有外部依賴；節點不碰全域 settings 或單例，全部由這裡注入。"""

    llm: StructuredLLMPort | None
    glossary: GlossaryPort
    searchers: dict[str, SearchPort]
    reranker: RerankerPort
    locators: dict[str, EvidenceLocatorPort]
    audit_repo: AuditRepositoryPort
    default_top_k: int = 8
    max_retrieval_attempts: int = 2
    # intent_classification 的 LLM 補位信心門檻（P4c 促升，per-config 可覆寫；預設對齊節點內建 0.6）
    intent_confidence_threshold: float = 0.6


def build_kb_query_graph(deps: KbQueryDeps) -> CompiledStateGraph:
    """組圖：節點一律從 Node Registry 取得並經 Harness 包裝（run_on_fatal 由節點契約宣告）。"""
    llm_version = getattr(deps.llm, "version", "")
    g = StateGraph(KbQueryState)
    for name, version in _NODES:
        spec = node_registry.get(name, version=version)
        if spec is None:  # 節點模組未被 import，或名字／版本打錯 → 建圖當下就炸，不拖到執行期
            raise ValueError(f"unknown node: {name}@{version}")
        g.add_node(
            name,
            harnessed(
                spec.name,
                spec.build(deps),
                run_on_fatal=spec.run_on_fatal,
                component_version=llm_version if name in _LLM_NODES else "",
                writes=spec.writes,
            ),
        )

    g.add_edge(START, "query_intake")
    g.add_edge("query_intake", "query_rewrite")
    g.add_edge("query_rewrite", "intent_classification")
    g.add_edge("intent_classification", "context_resolver")
    g.add_edge("context_resolver", "retrieval_planner")
    g.add_edge("retrieval_planner", "source_retrieval_rerank")
    g.add_edge("source_retrieval_rerank", "data_locator")
    g.add_edge("data_locator", "evidence_verification")
    g.add_conditional_edges(
        "evidence_verification",
        route_after_verification,
        {"retry": "retrieval_planner", "compose": "answer_composer"},
    )
    g.add_edge("answer_composer", "audit_feedback")
    g.add_edge("audit_feedback", END)
    return g.compile()
