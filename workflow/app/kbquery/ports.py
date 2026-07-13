"""外部服務的 Protocol 介面：節點只依賴這裡的抽象，不耦合特定產品。

- 檢索與 rerank 是遠端服務 → async；Locator 對已取回的 payload 做本地定位 → sync。
- 具體實作見 adapters.py（真實／確定性）與 tests/kbquery_fakes.py（測試用假資料）。
"""

from typing import Any, Protocol

from pydantic import BaseModel

from app.kbquery.models import AuditTrail, Evidence, SourceResult


class StructuredLLMPort(Protocol):
    """結構化輸出 LLM：回傳經 schema 驗證的模型；服務不可用或驗證失敗回傳 None。

    節點收到 None 必須走確定性 fallback，絕不把自由文字寫入 State。
    """

    version: str  # 模型版本識別（寫入 TraceEntry.component_version）

    async def structured(
        self, system: str, user: str, schema: type[BaseModel]
    ) -> BaseModel | None: ...


class GlossaryPort(Protocol):
    """業務詞彙字典：同義詞 → canonical 指標、易混淆指標清單。"""

    def canonical(self, text: str) -> str | None:
        """在 text 中找出可對應的 canonical 指標名稱（最長匹配優先），找不到回 None。"""
        ...

    def synonyms(self, canonical: str) -> list[str]: ...

    def confusables(self, canonical: str) -> list[str]: ...


class SearchPort(Protocol):
    """單一檢索方法（vector / keyword / metadata / table / structured 各一個實作）。"""

    async def search(
        self, query: str, *, filters: dict[str, Any], top_k: int, tenant_id: str
    ) -> list[SourceResult]: ...


class RerankerPort(Protocol):
    async def rerank(
        self,
        query: str,
        sources: list[SourceResult],
        *,
        policy: str,
        context: dict[str, Any],
    ) -> list[SourceResult]:
        """重排序；實作必須在回傳結果的 rerank_score / rerank_reason 留下排序依據。"""
        ...


class EvidenceLocatorPort(Protocol):
    """在單筆檢索結果中定位證據（文字段落／表格儲存格／結構化資料列）。

    context 內容：canonical_metric、metric_terms、target_period、
    version_policy、excluded_terms、query。
    """

    def locate(
        self, source: SourceResult, *, context: dict[str, Any]
    ) -> list[Evidence]: ...


class AuditRepositoryPort(Protocol):
    async def save(self, trail: AuditTrail) -> None: ...
