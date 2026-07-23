"""kb_query 測試用 Fake Adapter 與固定語料（金融題材）。

- Fake 只實作 ports.py 的 Protocol 介面，不打網路、不用 LLM，可重複執行。
- 語料為 module 級常數；FakeSearch 回傳 deep copy，避免 reranker 就地
  改寫 rerank_score / rerank_reason 污染常數與其他測試。
"""

import asyncio
from typing import Any, Callable

from pydantic import BaseModel

from app import skills
from app.engine import compiler
from app.nodes.kbquery.adapters import ScoreReranker, StaticGlossary
from app.nodes.kbquery.locators import (
    StructuredDataLocator,
    TableCellLocator,
    TextEvidenceLocator,
)
from app.nodes.kbquery.models import AuditTrail, SourceResult
from app.nodes.kbquery.ports import SearchPort
from app.skills.deps import KbQueryDeps


class FakeStructuredLLM:
    """StructuredLLMPort 假實作：依 schema class 回傳預先準備好的物件。

    private_thinking 模擬模型的內部推理過程（Chain of Thought）：它只存在於
    fake 內部、絕不隨 structured() 輸出，測試以這個哨兵字串斷言任何 State
    與 Audit Trail 都不得洩漏模型私有推理。
    """

    version = "fake-llm-v1"

    def __init__(
        self,
        outputs: dict[type, BaseModel | None] | None = None,
        private_thinking: str = "",
    ):
        self._outputs = outputs or {}
        self.private_thinking = private_thinking

    async def structured(
        self, system: str, user: str, schema: type[BaseModel]
    ) -> BaseModel | None:
        return self._outputs.get(schema)


class RecordingLLM:
    """手寫 fake：記錄每次 structured 呼叫的 system/user/schema，回傳固定 output。

    原本 test_skill_{rag_qa,summarize,triage,analyze_report}.py 各有一份逐字相同的
    版本，上移去重。nl_logic / nl_extract 的變體（依 schema 動態建構回傳物件）語意不同，
    各自留在原檔。
    """

    version = "rec-llm-v1"

    def __init__(self, output=None):
        self.output = output
        self.calls: list[dict] = []

    async def structured(self, system, user, schema):
        self.calls.append({"system": system, "user": user, "schema": schema})
        return self.output


class FakeSearch:
    """SearchPort 假實作：fn(query, filters) 決定回傳哪些 SourceResult。

    一律回傳 deep copy：reranker 會就地寫入 rerank_score / rerank_reason，
    不 copy 的話 module 級語料常數會被上一個測試改髒。
    """

    def __init__(self, fn: Callable[[str, dict], list[SourceResult]]):
        self._fn = fn

    async def search(
        self, query: str, *, filters: dict[str, Any], top_k: int, tenant_id: str
    ) -> list[SourceResult]:
        return [s.model_copy(deep=True) for s in self._fn(query, filters)]


class RecordingAuditRepo:
    """AuditRepositoryPort 假實作：記錄所有落地的 AuditTrail 供測試斷言。"""

    def __init__(self):
        self.saved: list[AuditTrail] = []

    async def save(self, trail: AuditTrail) -> None:
        self.saved.append(trail)


# ---------------------------------------------------------------------------
# 固定語料：金融題材檢索結果
# ---------------------------------------------------------------------------

TEXT_2025Q3 = SourceResult(
    source_id="fin-2025q3#c1",
    document_id="doc-fin-2025q3",
    document_title="2025Q3 財務季報",
    version="v1.0",
    page=3,
    source_type="text",
    retrieval_method="vector",
    original_score=0.85,
    metadata={"content": "2025Q3 稅後淨利為 1,234 百萬元。本季表現穩健。"},
)

TEXT_2025Q2 = SourceResult(
    source_id="fin-2025q2#c1",
    document_id="doc-fin-2025q2",
    document_title="2025Q2 財務季報",
    version="v1.0",
    page=5,
    source_type="text",
    retrieval_method="vector",
    original_score=0.8,
    metadata={"content": "2025Q2 稅後淨利為 1,100 百萬元。"},
)

# 期間錯誤的干擾語料：original_score 刻意最高，考驗 rerank 與驗證閘門
TEXT_WRONG_PERIOD = SourceResult(
    source_id="fin-2024q3#c1",
    document_id="doc-fin-2024q3",
    document_title="2024Q3 財務季報",
    version="v1.0",
    page=7,
    source_type="text",
    retrieval_method="vector",
    original_score=0.9,
    metadata={"content": "2024Q3 稅後淨利為 999 百萬元。"},
)

TABLE_2025 = SourceResult(
    source_id="fin-2025q3#t1",
    document_id="doc-fin-2025q3",
    document_title="2025Q3 財務季報",
    version="v1.0",
    page=12,
    source_type="table",
    retrieval_method="table",
    original_score=0.8,
    metadata={
        "table": {
            "table_name": "損益表",
            "sheet_name": "IS",
            "unit": "百萬元",
            "rows": [
                {"row_id": "稅後淨利", "cells": {"2025Q2": 1100.0, "2025Q3": 1234.0}},
                {"row_id": "稅前淨利", "cells": {"2025Q2": 1400.0, "2025Q3": 1500.0}},
            ],
        }
    },
)


# ---------------------------------------------------------------------------
# 依賴組裝與執行 helper
# ---------------------------------------------------------------------------


def make_deps(
    searchers: dict[str, SearchPort],
    llm=None,
    audit_repo=None,
    max_attempts: int = 2,
) -> KbQueryDeps:
    """組出全假依賴的 KbQueryDeps；audit_repo 可從 deps.audit_repo 取回斷言。"""
    return KbQueryDeps(
        llm=llm,
        glossary=StaticGlossary(),
        searchers=searchers,
        reranker=ScoreReranker(),
        locators={
            "text": TextEvidenceLocator(),
            "table": TableCellLocator(),
            "structured": StructuredDataLocator(),
        },
        audit_repo=audit_repo or RecordingAuditRepo(),
        default_top_k=8,
        max_retrieval_attempts=max_attempts,
    )


def run_graph(deps: KbQueryDeps, query: str, **extra_state) -> dict:
    """編譯並同步執行一次 kb_query skill 的圖（無 pytest-asyncio，統一用 asyncio.run）。

    手寫圖（原 build_kb_query_graph）已隨 Node-First 遷移退役；kb_query 現在只有
    skills/kb_query.yaml 這一張圖，直接用 compiler 編譯後執行。
    """
    graph = compiler.compile(skills.get("kb-query").skill, deps)
    return asyncio.run(
        graph.ainvoke({"query": query, "tenant_id": "t-test", **extra_state})
    )
