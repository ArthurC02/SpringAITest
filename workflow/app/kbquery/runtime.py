"""節點統一包裝器：計時、trace 紀錄、fatal 短路與不可變鍵防護。"""

import time
from datetime import datetime, timezone
from typing import Any, Awaitable, Callable

from app.kbquery.models import TraceEntry

# Query Intake 建立後不可被任何後續節點覆寫的鍵
IMMUTABLE_KEYS = {"query_id", "original_query", "query_timestamp"}


def _keys_summary(d: dict) -> str:
    """排序後的欄位鍵名逗號串、截 200 字；絕不放內容原文。"""
    return ",".join(sorted(d.keys()))[:200]


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def traced(
    node_name: str,
    fn: Callable[[dict], Awaitable[dict | None]],
    *,
    run_on_fatal: bool = False,
    component_version: str = "",
) -> Callable[[dict], Awaitable[dict]]:
    """包裝節點函式：fatal 後短路、例外轉 fatal_error、寫入 TraceEntry。"""

    async def wrapper(state: dict[str, Any]) -> dict:
        # fatal 後短路：只有 answer_composer / audit_feedback（run_on_fatal=True）例外
        if state.get("fatal_error") and not run_on_fatal:
            now = _now()
            return {
                "trace": [
                    TraceEntry(
                        node_name=node_name,
                        start_time=now,
                        end_time=now,
                        latency_ms=0,
                        status="skipped",
                    )
                ]
            }

        start_time = _now()
        t0 = time.perf_counter()
        try:
            out = await fn(state) or {}
        except Exception as e:
            # 不可恢復錯誤 → 設 fatal_error，走安全 ABSTAIN + 稽核路徑
            return {
                "fatal_error": f"{node_name}: {e}",
                "errors": [
                    {"node": node_name, "error": str(e), "error_type": type(e).__name__}
                ],
                "trace": [
                    TraceEntry(
                        node_name=node_name,
                        start_time=start_time,
                        end_time=_now(),
                        latency_ms=(time.perf_counter() - t0) * 1000,
                        status="error",
                        error_code=type(e).__name__,
                        input_summary=_keys_summary(state),
                        component_version=component_version,
                    )
                ],
            }

        # 強制防護：original_query 等鍵建立後不可被任何節點覆寫
        if node_name != "query_intake":
            for key in IMMUTABLE_KEYS:
                out.pop(key, None)

        entry = TraceEntry(
            node_name=node_name,
            start_time=start_time,
            end_time=_now(),
            latency_ms=(time.perf_counter() - t0) * 1000,
            status="ok",
            input_summary=_keys_summary(state),
            output_summary=_keys_summary(out),
            component_version=component_version,
        )
        return {**out, "trace": [entry]}

    return wrapper
