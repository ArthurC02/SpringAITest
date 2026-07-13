"""kb_query 圖的組裝：依賴注入 + 十個 traced 節點 + 驗證閘門迴圈。

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

from app.kbquery.nodes import (
    make_answer_composer_node,
    make_audit_feedback_node,
    make_context_resolver_node,
    make_data_locator_node,
    make_evidence_verification_node,
    make_intent_classification_node,
    make_query_intake_node,
    make_query_rewrite_node,
    make_retrieval_planner_node,
    make_source_retrieval_rerank_node,
)
from app.kbquery.ports import (
    AuditRepositoryPort,
    EvidenceLocatorPort,
    GlossaryPort,
    RerankerPort,
    SearchPort,
    StructuredLLMPort,
)
from app.kbquery.routing import route_after_verification
from app.kbquery.runtime import traced
from app.kbquery.state import KbQueryState


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


def build_kb_query_graph(deps: KbQueryDeps) -> CompiledStateGraph:
    """組圖：所有節點經 runtime.traced 包裝；composer/audit 在 fatal 後仍會執行。"""
    llm_version = getattr(deps.llm, "version", "")
    g = StateGraph(KbQueryState)
    g.add_node(
        "query_intake",
        traced("query_intake", make_query_intake_node(deps.max_retrieval_attempts)),
    )
    g.add_node(
        "query_rewrite",
        traced(
            "query_rewrite",
            make_query_rewrite_node(deps.llm, deps.glossary),
            component_version=llm_version,
        ),
    )
    g.add_node(
        "intent_classification",
        traced(
            "intent_classification",
            make_intent_classification_node(deps.llm),
            component_version=llm_version,
        ),
    )
    g.add_node(
        "context_resolver",
        traced("context_resolver", make_context_resolver_node(deps.glossary)),
    )
    g.add_node(
        "retrieval_planner",
        traced("retrieval_planner", make_retrieval_planner_node(deps.default_top_k)),
    )
    g.add_node(
        "source_retrieval_rerank",
        traced(
            "source_retrieval_rerank",
            make_source_retrieval_rerank_node(deps.searchers, deps.reranker),
        ),
    )
    g.add_node(
        "data_locator", traced("data_locator", make_data_locator_node(deps.locators))
    )
    g.add_node(
        "evidence_verification",
        traced("evidence_verification", make_evidence_verification_node()),
    )
    g.add_node(
        "answer_composer",
        traced("answer_composer", make_answer_composer_node(), run_on_fatal=True),
    )
    g.add_node(
        "audit_feedback",
        traced(
            "audit_feedback",
            make_audit_feedback_node(deps.audit_repo),
            run_on_fatal=True,
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
