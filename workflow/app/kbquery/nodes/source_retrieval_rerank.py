"""Source Retrieval & Rerank：依計畫對各檢索來源發查、去重後 rerank 取前 top_k。"""

import logging

from app.kbquery.models import RetrievalPlan, SourceResult
from app.kbquery.ports import RerankerPort, SearchPort

logger = logging.getLogger(__name__)


def make_source_retrieval_rerank_node(
    searchers: dict[str, SearchPort], reranker: RerankerPort
):
    """建立 source_retrieval_rerank 節點：未串接的檢索方法記 debug 後略過。"""

    async def source_retrieval_rerank_node(state: dict) -> dict:
        plan: RetrievalPlan = state["retrieval_plan"]
        raw_queries: list[str] = [
            state.get("normalized_query") or state.get("original_query", ""),
            *(state.get("query_variants") or []),
        ]
        queries = list(dict.fromkeys(raw_queries))

        collected: list[SourceResult] = []
        for method in plan.methods:
            port = searchers.get(method)
            if port is None:
                logger.debug("檢索方法 %s 尚未串接 adapter，略過", method)
                continue
            for q in queries:
                collected.extend(
                    await port.search(
                        q,
                        filters=state.get("filters", {}),
                        top_k=plan.top_k,
                        tenant_id=state.get("tenant_id", ""),
                    )
                )

        # 去重：同一來源保留 original_score 較高者
        deduped: dict[tuple, SourceResult] = {}
        for s in collected:
            key = (s.document_id, s.source_type, s.page, s.source_id)
            current = deduped.get(key)
            if current is None or s.original_score > current.original_score:
                deduped[key] = s
        unique = list(deduped.values())

        ranked = await reranker.rerank(
            queries[0],
            unique,
            policy=plan.rerank_policy,
            context={
                "target_period": state.get("target_period", ""),
                "metric_terms": state.get("metric_terms", []),
                "excluded_terms": state.get("excluded_terms", []),
            },
        )

        docs: dict[tuple, dict] = {}
        pages: dict[tuple, dict] = {}
        for s in unique:
            docs.setdefault(
                (s.document_id, s.version),
                {
                    "document_id": s.document_id,
                    "document_title": s.document_title,
                    "version": s.version,
                },
            )
            if s.page is not None:
                pages.setdefault(
                    (s.document_id, s.page),
                    {"document_id": s.document_id, "page": s.page},
                )

        return {
            "ranked_sources": ranked[: plan.top_k],
            "candidate_documents": list(docs.values()),
            "candidate_pages": list(pages.values()),
        }

    return source_retrieval_rerank_node
