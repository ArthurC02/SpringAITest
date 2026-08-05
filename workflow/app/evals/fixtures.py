"""Deterministic fixture / recorded replay 的依賴組裝。

重用既有 DI 注入點（`app.skills.deps.KbQueryDeps` + `compiler.compile(skill, deps)`），
不 monkeypatch 全域單例——每個 case 各建一份 deps，彼此互不干擾，也不影響正式流量。

只換掉會碰 I/O 或非決定性的兩個埠：llm（結構化模型呼叫）與 searchers（向量檢索）。
reranker/glossary/locators 是純本地計算（無網路、無隨機性），沿用正式 deterministic
實作——重造一份沒有意義，也違反 Re-Use 前提。audit_repo 換成 no-op：eval 不落地任何
稽核紀錄。write_evidence_sink 維持 dataclass 預設的 None：即使部署已開啟
AGENT_WRITE_TOOLS_ENABLED，`runtime.write_evidence` 工具也會因為拿不到 sink 而
fail closed，eval 不可能真的寫出一次 effect（見 app.tools.write_evidence）。

刻意不沿用正式值的另外兩項（eval PASS 不等於部署設定下的 PASS，呼叫端需自行知悉）：
- `default_top_k`/`max_retrieval_attempts` 這裡吃的是 `KbQueryDeps` 的 dataclass
  預設（8/2），不是 `_default_deps()`/`app.skills.config_apply.resolve` 會套用的
  `settings.kb_query_top_k`/`settings.kb_query_max_retrieval_attempts`。
- eval 全程不套用租戶的 active Configuration Set（`config_apply.resolve` 只在
  `/skills/{name}/invoke` 走）；candidate.pins 只是原樣 echo 進 canonical_identity
  的識別資訊，不會被拿去覆寫任何埠或參數。
"""

from typing import Any

from pydantic import BaseModel, ValidationError

from app.nodes.kbquery.adapters import ScoreReranker, StaticGlossary
from app.nodes.kbquery.locators import (
    StructuredDataLocator,
    TableCellLocator,
    TextEvidenceLocator,
)
from app.nodes.kbquery.models import AuditTrail, SourceResult
from app.skills.deps import KbQueryDeps
from app.engine.script_policy import configured_script_runner


class FixtureStructuredLLM:
    """StructuredLLMPort fixture：依呼叫順序（FIFO）吐出固定回應。

    佇列耗盡，或下一筆回應資料驗不過呼叫端要求的 schema，一律回 None——與正式 adapter
    對「LLM 不可用」的處理一致，節點會走自己的確定性 fallback，不會因為 fixture 沒配好
    而讓整個 case 500（仍可能因此 verdict=FAIL，但不是未捕捉例外）。
    """

    version = "eval-fixture-llm"

    def __init__(self, responses: list[dict[str, Any]] | None = None):
        self._queue: list[dict[str, Any]] = list(responses or [])

    async def structured(
        self, system: str, user: str, schema: type[BaseModel]
    ) -> BaseModel | None:
        if not self._queue:
            return None
        data = self._queue.pop(0)
        try:
            return schema.model_validate(data)
        except ValidationError:
            return None


class FixtureSearch:
    """SearchPort fixture：回傳固定語料，忽略 query/filters——決定性是重點，不是相關性。"""

    def __init__(self, results: list[dict[str, Any]] | None = None):
        self._results = [SourceResult.model_validate(r) for r in (results or [])]

    async def search(
        self, query: str, *, filters: dict[str, Any], top_k: int, tenant_id: str
    ) -> list[SourceResult]:
        # deep copy：與 tests/kbquery_fakes.py::FakeSearch 同理，避免 reranker 就地寫入
        # rerank_score/rerank_reason 污染下一次呼叫（同一個 case 可能有多輪檢索迴圈）。
        return [r.model_copy(deep=True) for r in self._results[:top_k]]


class NoopAuditRepository:
    """AuditRepositoryPort no-op：eval 執行不得落地任何稽核紀錄（side-effect-free 驗收）。"""

    async def save(self, trail: AuditTrail) -> None:
        return None


def build_fixture_deps(fixtures: dict[str, Any]) -> KbQueryDeps:
    """把單一 case 的 fixtures 組成 KbQueryDeps。

    fixtures 形狀：
    - llm_responses: list[dict] —— 依 graph 執行期間所有 llm.structured() 呼叫的順序
      消費，一次呼叫吃掉佇列最前面一筆。
    - search_results: list[dict]（SourceResult 欄位）—— 固定語料池，任何一次向量檢索
      都回傳同一份（依 top_k 截斷）。

    其餘 deps 欄位維持 dataclass 預設（None）：agent_package_reader/agent_chat_model
    留白代表本階段不支援 agentic candidate（compile 時會顯式報錯，不是靜默忽略）；
    write_evidence_sink/context_*/script_runner 留白＝該類節點若被觸發一律 fail closed
    或走 in-process 沙箱，無額外注入需求。
    """
    llm_responses = fixtures.get("llm_responses") or []
    search_results = fixtures.get("search_results") or []
    return KbQueryDeps(
        llm=FixtureStructuredLLM(llm_responses),
        glossary=StaticGlossary(),
        searchers={"vector": FixtureSearch(search_results)},
        reranker=ScoreReranker(),
        locators={
            "text": TextEvidenceLocator(),
            "table": TableCellLocator(),
            "structured": StructuredDataLocator(),
        },
        audit_repo=NoopAuditRepository(),
        script_runner=configured_script_runner(),
    )
