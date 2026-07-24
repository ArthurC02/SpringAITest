"""初始 Tool 清單（規格 §6.2）：以 @tool 包裝**既有**的 adapter／元件。

規格 §6.1 明說「ports.py 的 Protocol 介面不變」——所以這裡沒有任何新的檢索／rerank／
詞彙邏輯，只是把既有實作接上 Tool Registry。新增 Tool = 加一個 @tool 函式。

依賴一律注入：tool 從 `ToolContext.deps`（skill 的依賴容器）取 port 實例，測試給假的
（tests/kbquery_fakes.py 的 FakeSearch 等）就能不打網路。deps 沒提供時才退回正式
adapter —— 自訂 skill（P4）沒有專屬依賴容器時走這條路。
"""

from typing import Any

from app.engine.tool_registry import ToolContext, tool
from app.nodes.kbquery import calculator
from app.nodes.kbquery.adapters import BackendVectorSearch, ScoreReranker, StaticGlossary
from app.nodes.kbquery.models import SourceResult


def _dep(ctx: ToolContext, name: str) -> Any:
    return getattr(ctx.deps, name, None) if ctx.deps is not None else None


@tool(
    name="backend.retrieval_search",
    kind="http",
    description="在目前租戶已授權的知識庫中進行向量檢索",
    args_schema={"query": str, "top_k": int},
    returns="list[chunk]",
    risk="read",
)
async def retrieval_search(ctx: ToolContext, query: str, top_k: int = 4) -> list[dict]:
    """租戶邊界由 ctx.tenant_id 決定，不由 args 決定：script 偽造不了租戶。"""
    searcher = (_dep(ctx, "searchers") or {}).get("vector") or BackendVectorSearch()
    results = await searcher.search(
        query, filters={}, top_k=top_k, tenant_id=ctx.tenant_id
    )
    return [r.model_dump() for r in results]


@tool(
    name="local.calculator",
    kind="local",
    description="確定性計算器（AST 白名單求值，取代 LLM 心算）",
    args_schema={"expression": str, "inputs": dict},
    returns="float",
    risk="low",
)
async def calculate(
    ctx: ToolContext, expression: str, inputs: dict | None = None
) -> float:
    return calculator.evaluate(expression, inputs or {})


@tool(
    name="local.glossary",
    kind="local",
    description="業務詞彙字典：canonical 指標、同義詞、易混淆指標",
    args_schema={"text": str},
    returns="dict",
    risk="read",
)
async def glossary_lookup(ctx: ToolContext, text: str) -> dict:
    glossary = _dep(ctx, "glossary") or StaticGlossary()
    canonical = glossary.canonical(text)
    return {
        "canonical": canonical,
        "synonyms": glossary.synonyms(canonical) if canonical else [],
        "confusables": glossary.confusables(canonical) if canonical else [],
    }


@tool(
    name="local.rerank",
    kind="local",
    description="確定性重排（留下 score breakdown 供稽核）",
    args_schema={"query": str, "sources": list, "context": dict},
    returns="list[source]",
    risk="low",
)
async def rerank(
    ctx: ToolContext,
    query: str,
    sources: list[dict],
    policy: str = "score_with_context_boost",
    context: dict | None = None,
) -> list[dict]:
    reranker = _dep(ctx, "reranker") or ScoreReranker()
    parsed = [SourceResult.model_validate(s) for s in sources]
    ranked = await reranker.rerank(
        query, parsed, policy=policy, context=context or {}
    )
    return [s.model_dump() for s in ranked]
