"""真實／確定性 adapter 實作（對應 ports.py 的 Protocol）。

不放假業務資料；測試用假資料在 tests/kbquery_fakes.py。
DEFAULT_GLOSSARY 是業務詞彙字典「設定」，非假資料。
"""

import logging
from typing import Any

from app.backend_http import search_chunks
from app.nodes.kbquery.models import AuditTrail, SourceResult
from app.llm import build_llm, get_llm
from app.settings import settings
from pydantic import BaseModel

logger = logging.getLogger(__name__)

# 業務詞彙字典設定：canonical 指標 → 同義詞與易混淆指標
DEFAULT_GLOSSARY: dict[str, dict] = {
    "稅後淨利": {"synonyms": ["稅後盈餘", "稅後獲利"], "confusables": ["稅前淨利"]},
    "稅前淨利": {"synonyms": ["稅前盈餘"], "confusables": ["稅後淨利"]},
    "授信利差": {"synonyms": ["放款利差"], "confusables": ["淨利差"]},
    "淨利差": {"synonyms": ["NIM"], "confusables": ["授信利差"]},
    "逾放比": {"synonyms": ["逾期放款比率"], "confusables": ["備抵呆帳覆蓋率"]},
}


class StaticGlossary:
    """靜態詞彙字典（GlossaryPort）：term → canonical 反查，最長匹配優先。"""

    def __init__(self, entries: dict[str, dict] = DEFAULT_GLOSSARY):
        self._entries = entries
        self._term_to_canonical: dict[str, str] = {}
        for canon, info in entries.items():
            self._term_to_canonical[canon] = canon
            for syn in info.get("synonyms", []):
                self._term_to_canonical[syn] = canon

    def canonical(self, text: str) -> str | None:
        # 取最長匹配：問句含「稅後淨利」時不得誤中較短的「淨利差」等 term
        best_term = ""
        best_canon = None
        for term, canon in self._term_to_canonical.items():
            if term in text and len(term) > len(best_term):
                best_term, best_canon = term, canon
        return best_canon

    def synonyms(self, canonical: str) -> list[str]:
        return list(self._entries.get(canonical, {}).get("synonyms", []))

    def confusables(self, canonical: str) -> list[str]:
        return list(self._entries.get(canonical, {}).get("confusables", []))


class LangChainStructuredLLM:
    """StructuredLLMPort：LangChain with_structured_output，失敗回 None。

    無參數 → 全域單例（get_llm：model=settings.llm_model、temperature 0.7），行為與過去一致。
    帶 model / temperature → per-config（P4c）：繞開 get_llm 的 lru_cache，各建一顆客戶端。
    version 記錄實際 model 供 trace 稽核；temperature 暴露供測試觀察覆寫值（全域路徑為 None）。
    """

    def __init__(self, model: str | None = None, temperature: float | None = None):
        self.version = model or settings.llm_model
        self.temperature = temperature  # 公開供測試觀察覆寫值（全域路徑為 None）
        self._model = model
        self._client: Any = None

    def _get_client(self) -> Any:
        if self._client is None:
            if self._model is None and self.temperature is None:
                self._client = get_llm()  # 全域路徑：單例、0.7，不變
            else:
                self._client = build_llm(
                    self._model or settings.llm_model,
                    self.temperature if self.temperature is not None else 0.7,
                )
        return self._client

    async def structured(
        self, system: str, user: str, schema: type[BaseModel]
    ) -> BaseModel | None:
        try:
            llm = self._get_client().with_structured_output(schema)
            out = await llm.ainvoke([("system", system), ("user", user)])
            # with_structured_output 依設定可能回 dict；一律轉成已驗證的 schema 實例
            return out if isinstance(out, schema) else schema.model_validate(out)
        except Exception:
            # 節點層收到 None 會走確定性 fallback，不讓 LLM 失敗中斷流程
            logger.warning("structured LLM 呼叫失敗，回傳 None", exc_info=True)
            return None


class BackendVectorSearch:
    """SearchPort（vector）：呼叫 backend /api/retrieval/search（仿 nodes/retrieve.py）。"""

    async def search(
        self, query: str, *, filters: dict[str, Any], top_k: int, tenant_id: str
    ) -> list[SourceResult]:
        chunks = await search_chunks(query, top_k, tenant_id)

        results = [
            SourceResult(
                source_id=f"{chunk['document_id']}#chunk{i}",
                document_id=chunk["document_id"],
                document_title=chunk["title"],
                page=None,
                source_type="text",
                retrieval_method="vector",
                original_score=chunk["score"],
                metadata={"content": chunk["content"]},
            )
            for i, chunk in enumerate(chunks)
        ]

        # ponytail: backend API 尚不支援 metadata 過濾，先做客戶端後過濾；
        # backend 支援 filter 參數後改為伺服端過濾
        period = filters.get("period")
        if filters.get("strict_period") and period:
            targets = [period] if isinstance(period, str) else list(period)
            results = [
                r
                for r in results
                if any(t in r.metadata.get("content", "") for t in targets)
            ]
        return results


class RevisionedScopedBackendVectorSearch:
    """Production D3 adapter for Backend's revision-1 scoped search contract."""

    scope_contract_version = 1

    async def search(
        self, query: str, *, filters: dict[str, Any], top_k: int, tenant_id: str
    ) -> list[SourceResult]:
        from app.backend_http import search_chunks_scoped

        sources = filters.get("knowledge_sources")
        if not isinstance(sources, list) or not sources:
            return []
        chunks = await search_chunks_scoped(query, top_k, tenant_id, sources)
        return [
            SourceResult(
                source_id=f"{chunk['document_id']}#chunk{i}",
                document_id=chunk["document_id"],
                document_title=chunk["title"],
                page=None,
                source_type="text",
                retrieval_method="vector",
                original_score=chunk["score"],
                metadata={"content": chunk["content"]},
            )
            for i, chunk in enumerate(chunks)
        ]


class ScoreReranker:
    """RerankerPort：確定性重排（無外部 reranker 服務時的預設），留下 score breakdown。"""

    async def rerank(
        self,
        query: str,
        sources: list[SourceResult],
        *,
        policy: str,
        context: dict[str, Any],
    ) -> list[SourceResult]:
        tp = context.get("target_period")
        period_targets = [tp] if isinstance(tp, str) else list(tp or [])
        metric_terms = context.get("metric_terms") or []
        excluded_terms = context.get("excluded_terms") or []

        for src in sources:
            score = src.original_score
            reason = [f"base={src.original_score}"]
            haystack = src.document_title + str(src.metadata)
            if any(t in haystack for t in period_targets):
                score += 0.2
                reason.append("period=+0.2")
            if any(t in haystack for t in metric_terms):
                score += 0.2
                reason.append("metric=+0.2")
            if any(t in haystack for t in excluded_terms):
                score -= 0.5
                reason.append("excluded=-0.5")
            src.rerank_score = score
            src.rerank_reason = ";".join(reason)

        return sorted(sources, key=lambda s: s.rerank_score or 0.0, reverse=True)


class LoggingAuditRepository:
    """AuditRepositoryPort：以結構化 log 落地稽核紀錄。"""

    # ponytail: 正式稽核 DB 未串接前先寫 log；之後換成 DB adapter
    async def save(self, trail: AuditTrail) -> None:
        logging.getLogger(__name__).info(
            "kb_query audit trail %s", trail.model_dump_json()
        )
