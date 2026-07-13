"""kb_query 工作流註冊：組出正式環境依賴並註冊到 registry。圖結構見 app.kbquery.graph。"""

from typing import Any

from langgraph.graph.state import CompiledStateGraph
from pydantic import BaseModel, Field

from app.kbquery.adapters import (
    BackendVectorSearch,
    LangChainStructuredLLM,
    LoggingAuditRepository,
    ScoreReranker,
    StaticGlossary,
)
from app.kbquery.graph import KbQueryDeps, build_kb_query_graph
from app.kbquery.locators import (
    StructuredDataLocator,
    TableCellLocator,
    TextEvidenceLocator,
)
from app.settings import settings
from app.workflows.registry import register


class KbQueryInput(BaseModel):
    """kb_query 工作流的輸入 schema：query 必須是非空字串，其餘皆選填。"""

    query: str = Field(min_length=1)
    user_role: str | None = None
    session_context: dict[str, Any] | None = None
    system_entrypoint: str | None = None
    user_feedback: str | None = None


def _default_deps() -> KbQueryDeps:
    """組出正式環境的依賴組合。"""
    return KbQueryDeps(
        llm=LangChainStructuredLLM(),
        glossary=StaticGlossary(),
        # ponytail: 只串接 vector；keyword/metadata/table/structured 檢索來源
        # 後端尚未提供，等 API 就緒後在這個 dict 加上對應 adapter 即可
        searchers={"vector": BackendVectorSearch()},
        reranker=ScoreReranker(),
        locators={
            "text": TextEvidenceLocator(),
            "table": TableCellLocator(),
            "structured": StructuredDataLocator(),
        },
        audit_repo=LoggingAuditRepository(),
        default_top_k=settings.kb_query_top_k,
        max_retrieval_attempts=settings.kb_query_max_retrieval_attempts,
    )


@register(
    "kb_query",
    description="可驗證可稽核的知識查詢：固定節點＋證據驗證閘門，未通過不輸出實質答案",
    input_model=KbQueryInput,
)
def build() -> CompiledStateGraph:
    """建圖入口：所有依賴由 _default_deps() 注入，測試時可直接用假依賴呼叫 build_kb_query_graph。"""
    return build_kb_query_graph(_default_deps())
