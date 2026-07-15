"""Harness：節點的標準執行殼（app/kbquery/runtime.py::traced 的泛化版）。

治理硬規則寫死在這裡，Skill/節點/Script 都關不掉：計時與 trace、fatal 短路（run_on_fatal
的節點例外）、例外轉 fatal_error 走安全 ABSTAIN + 稽核路徑、不可變鍵防護。

泛化只多一件事：依 NodeSpec.writes 剝除未宣告的輸出鍵，節點無法偷寫 state。
writes=None 代表「未宣告契約」（例如既有測試直接包裝一個匿名節點函式）→ 不剝除，
維持 traced() 的原行為。

trace 的兩個回填管道（P3）：步驟本體在執行當下才知道某些 trace 欄位（script 的 sha256
與讀寫鍵名、tool 的 args 鍵名），而 Harness 的 TraceEntry 是在步驟跑完才組出來的。
用 ContextVar 而不是把值塞進 state：state 是 Skill 的資料流（會進 output、進 audit），
trace 中繼資料不該在那裡繞一圈；ContextVar 又天然是 per-task 的，同一張圖並行執行
多個請求也不會互相污染。
- describe(...)：步驟回填**自己那一筆** entry 的欄位（可換 entry 型別）。
- record(entry)：步驟內的**子事件** entry（script 裡的每一次 tools.call），
  一次步驟可以有 N 筆。
"""

import time
from contextvars import ContextVar
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Awaitable, Callable, Iterable

# ponytail: TraceEntry 借用 kbquery.models —— 目前唯一的 trace 型別，等第二個
# 領域出現再抽到 engine/models.py，先抽只是搬檔案不產生價值
from app.kbquery.models import TraceEntry

# Query Intake 建立後不可被任何後續節點覆寫的鍵
IMMUTABLE_KEYS = {"query_id", "original_query", "query_timestamp"}

# 由伺服器依 RequestContext 注入的身分鍵：節點/Script 一律不得寫入（寫得了就等於
# 可以在流程中途換租戶 —— 多租戶隔離邊界會直接破功）。
IDENTITY_KEYS = {"tenant_id", "user_id", "role"}


@dataclass
class _Step:
    """單一步驟的 trace 中繼資料（per-task，由 Harness 建立與清掉）。"""

    detail: dict[str, Any] = field(default_factory=dict)
    children: list[TraceEntry] = field(default_factory=list)


_CURRENT: ContextVar[_Step | None] = ContextVar("harness_step", default=None)


def describe(model: type[TraceEntry] | None = None, **fields: Any) -> None:
    """步驟內回填自己這筆 trace entry 的欄位（不在 Harness 內執行時為 no-op）。"""
    step = _CURRENT.get()
    if step is None:
        return
    if model is not None:
        step.detail["model"] = model
    step.detail.update(fields)


def record(entry: TraceEntry) -> None:
    """步驟內的子事件 entry（每一次 tool 呼叫）。不在 Harness 內執行時丟棄。"""
    step = _CURRENT.get()
    if step is not None:
        step.children.append(entry)


def _build_entry(step: _Step, **base: Any) -> TraceEntry:
    """Harness 的基本欄位 + 步驟回填的欄位；型別可由步驟指定（Script/ToolTraceEntry）。"""
    fields = {**base, **step.detail}
    model: type[TraceEntry] = fields.pop("model", TraceEntry)
    return model(**fields)


def _keys_summary(d: dict) -> str:
    """排序後的欄位鍵名逗號串、截 200 字；絕不放內容原文。

    __ 前綴的引擎內部鍵（compiler 植入的迴圈計數與條件式求值結果）不入摘要：
    它們不是 Skill 的資料流，落進 trace 只會讓稽核紀錄多出實作細節。
    """
    return ",".join(sorted(k for k in d if not k.startswith("__")))[:200]


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def harnessed(
    node_name: str,
    fn: Callable[[dict], Awaitable[dict | None]],
    *,
    run_on_fatal: bool = False,
    component_version: str = "",
    writes: Iterable[str] | None = None,
) -> Callable[[dict], Awaitable[dict]]:
    """包裝節點函式：fatal 後短路、例外轉 fatal_error、寫入 TraceEntry、剝除未宣告的鍵。"""
    allowed: set[str] | None = None if writes is None else set(writes)

    async def wrapper(state: dict[str, Any]) -> dict:
        # fatal 後短路：只有 answer_composer / audit_feedback（run_on_fatal=True）例外
        if state.get("fatal_error") and not run_on_fatal:
            ts = now()
            return {
                "trace": [
                    TraceEntry(
                        node_name=node_name,
                        start_time=ts,
                        end_time=ts,
                        latency_ms=0,
                        status="skipped",
                    )
                ]
            }

        start_time = now()
        t0 = time.perf_counter()
        step = _Step()
        token = _CURRENT.set(step)
        try:
            out = await fn(state) or {}
        except Exception as e:
            # 不可恢復錯誤 → 設 fatal_error，走安全 ABSTAIN + 稽核路徑
            entry = _build_entry(
                step,
                node_name=node_name,
                start_time=start_time,
                end_time=now(),
                latency_ms=(time.perf_counter() - t0) * 1000,
                status="error",
                error_code=type(e).__name__,
                input_summary=_keys_summary(state),
                component_version=component_version,
            )
            return {
                "fatal_error": f"{node_name}: {e}",
                "errors": [
                    {"node": node_name, "error": str(e), "error_type": type(e).__name__}
                ],
                # 子事件（失敗前已完成的 tool 呼叫）照樣入 trace：稽核要看得到「錯之前做了什麼」
                "trace": [*step.children, entry],
            }
        finally:
            _CURRENT.reset(token)

        # I/O 契約：宣告了 writes 就只放行宣告過的鍵（trace/errors/fatal_error 是引擎鍵，
        # 由 Harness 自己加，節點不得宣告也不得寫入）
        if allowed is not None:
            out = {k: v for k, v in out.items() if k in allowed}

        # 強制防護：original_query 等鍵建立後不可被任何節點／Script 覆寫（AT1-05／AT-GOV-03）
        if node_name != "query_intake":
            for key in IMMUTABLE_KEYS:
                out.pop(key, None)

        entry = _build_entry(
            step,
            node_name=node_name,
            start_time=start_time,
            end_time=now(),
            latency_ms=(time.perf_counter() - t0) * 1000,
            status="ok",
            input_summary=_keys_summary(state),
            output_summary=_keys_summary(out),
            component_version=component_version,
        )
        return {**out, "trace": [*step.children, entry]}

    return wrapper
