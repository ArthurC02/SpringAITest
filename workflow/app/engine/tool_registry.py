"""Tool Registry：Node 與 Script 可呼叫的能力，以 @tool 註冊（規格 §6）。

`kind` 只有兩種：`http`（呼叫 backend，帶 X-Internal-Token + 租戶標頭）與 `local`
（容器內函式庫）。兩者的差別在**實作**，不在呼叫端 —— script 一律只看得到
`tools.call(name, **args)` 一個方法。

治理硬規則（規格 §6.1／§6.3）：
- **每次 tool 呼叫入 trace**：tool 名、args 的**鍵名**摘要、耗時、狀態。args 的**值不落**
  —— `expression: "1+1"` 的 `"1+1"` 不得出現在稽核紀錄裡（AT3-15）。值可能是使用者問句、
  檢索片段、金額；trace 是要長期保存並給人看的。
- **uses_tools 是白名單**：script 呼叫不在 Skill `uses_tools` 清單裡的 tool → 直接拒絕，
  且**不得真的發出那次呼叫**（AT3-16）。檢查在排程 coroutine 之前。
- 身分（tenant_id）一律由 ToolContext 帶，不由 args 帶：script 寫不了 state 的身分鍵
  （script_runner.FORBIDDEN_WRITE_KEYS），也就偽造不了租戶。
"""

import asyncio
import time
from dataclasses import dataclass
from typing import Any, Awaitable, Callable, Literal

from app.engine import harness
from app.engine.models import TraceEntry
from pydantic import computed_field

ToolKind = Literal["http", "local"]
ToolRisk = Literal["low", "read", "write", "privileged"]
VALID_TOOL_RISKS = frozenset({"low", "read", "write", "privileged"})


@dataclass(frozen=True)
class ToolContext:
    """一次 tool 呼叫的身分與依賴（依賴注入：tool 不碰全域 settings／單例）。"""

    tenant_id: str
    user_id: str = ""
    role: str = ""
    deps: Any = None  # skill 的依賴容器（KbQueryDeps…）；tool 從這裡取 port 實作


@dataclass(frozen=True)
class ToolSpec:
    """已註冊 tool 的契約。args_schema/returns 供 Skill 作者與前端查詢，不做執行期強制轉型。"""

    name: str
    kind: ToolKind
    description: str
    args_schema: dict[str, type]
    returns: str
    fn: Callable[..., Awaitable[Any]]
    # 未明示時採最保守分類，避免新工具在 Builder 被錯標成低風險。
    risk: ToolRisk = "privileged"


class ToolError(RuntimeError):
    """tool 呼叫失敗的基底類別。"""


class UnknownTool(ToolError):
    """未註冊的 tool（存檔期對應錯誤碼 unknown_tool）。"""


class ToolNotAllowed(ToolError):
    """tool 未列在 Skill 的 uses_tools 清單中（規格 §5.1）。"""


class ToolTraceEntry(TraceEntry):
    """tool 呼叫的 trace entry：多 tool 名與 args 鍵名，**沒有**任何放 args 值的欄位。"""

    tool: str
    args_keys: str = ""  # args 的鍵名摘要（排序後逗號串）；值一律不落

    @computed_field  # type: ignore[prop-decorator]
    @property
    def duration_ms(self) -> float:
        """耗時（等同 TraceEntry.latency_ms，另取規格 §6.1 的名字）。"""
        return self.latency_ms


_REGISTRY: dict[str, ToolSpec] = {}


def tool(
    *,
    name: str,
    kind: ToolKind,
    description: str = "",
    args_schema: dict[str, type] | None = None,
    returns: str = "",
    risk: ToolRisk = "privileged",
):
    """裝飾器：把 tool 函式登記進註冊表，函式本身原樣回傳（簽名不變）。

    重複註冊同名 tool → 立即 ValueError（對齊 node_registry / workflows registry：
    在 import 期就炸掉，而不是讓後註冊者悄悄覆蓋先註冊者）。
    """

    def decorator(fn: Callable[..., Awaitable[Any]]) -> Callable[..., Awaitable[Any]]:
        if name in _REGISTRY:
            raise ValueError(f"duplicate tool: name={name}")
        if risk not in VALID_TOOL_RISKS:
            raise ValueError(
                f"tool {name} 的 risk 必須是 {sorted(VALID_TOOL_RISKS)}，收到：{risk}"
            )
        _REGISTRY[name] = ToolSpec(
            name=name,
            kind=kind,
            description=description,
            args_schema=dict(args_schema or {}),
            returns=returns,
            fn=fn,
            risk=risk,
        )
        return fn

    return decorator


def get(name: str) -> ToolSpec | None:
    """依名稱查 tool;查無回 None。"""
    return _REGISTRY.get(name) if isinstance(name, str) else None


def all_specs() -> list[ToolSpec]:
    """所有已註冊 tool，依名稱排序以維持穩定輸出順序。"""
    return sorted(_REGISTRY.values(), key=lambda s: s.name)


def _args_keys(args: dict) -> str:
    return ",".join(sorted(str(k) for k in args))[:200]


async def invoke(
    name: str,
    ctx: ToolContext,
    allowed: frozenset[str] | set[str],
    args: dict,
    *,
    as_step: bool = False,
) -> Any:
    """呼叫 tool。allowed 是 Skill 的 uses_tools 白名單（+ flow 內宣告的 tool 步驟）。

    as_step=True：這次呼叫**就是**一個 Skill 步驟 → 回填 Harness 自己那筆 entry
    （不另外產生子 entry，避免同一件事在 trace 裡出現兩次）。
    as_step=False：這次呼叫發生在 script 內 → 記一筆子 entry（一段 script 可呼叫 N 次）。
    """
    if name not in allowed:
        # 在查表與排程之前就擋：不得真的發出這次呼叫（AT3-16）
        raise ToolNotAllowed(f"tool '{name}' 不在 Skill 的 uses_tools 清單中")
    spec = get(name)
    if spec is None:
        raise UnknownTool(f"未註冊的 tool: {name}")

    if as_step:
        harness.describe(model=ToolTraceEntry, tool=name, args_keys=_args_keys(args))
        return await spec.fn(ctx, **args)

    start_time = harness.now()
    t0 = time.perf_counter()
    status, error_code = "ok", ""
    try:
        return await spec.fn(ctx, **args)
    except Exception as e:
        status, error_code = "error", type(e).__name__
        raise
    finally:
        harness.record(
            ToolTraceEntry(
                node_name=f"tool:{name}",
                tool=name,
                args_keys=_args_keys(args),
                start_time=start_time,
                end_time=harness.now(),
                latency_ms=(time.perf_counter() - t0) * 1000,
                status=status,
                error_code=error_code,
            )
        )


class ToolBag:
    """script 看到的 `tools`：只有 call 一個方法（規格 §5.1）。

    script 跑在 worker thread（見 script_runner），tool 是 async → 用
    run_coroutine_threadsafe 把 coroutine 排回事件圈，再阻塞這條 worker thread 等結果。
    事件圈本身不被阻塞（其他請求照跑）。
    """

    def __init__(
        self,
        ctx: ToolContext,
        allowed: frozenset[str] | set[str],
        loop: asyncio.AbstractEventLoop,
        timeout_s: float,
    ):
        self._ctx = ctx
        self._allowed = frozenset(allowed)
        self._loop = loop
        self._timeout_s = timeout_s

    def call(self, name: str, **args: Any) -> Any:
        if name not in self._allowed:
            # 排程之前先擋：連 coroutine 都不建立（AT3-16「不實際發出對應的 tool 呼叫」）
            raise ToolNotAllowed(f"tool '{name}' 不在 Skill 的 uses_tools 清單中")
        future = asyncio.run_coroutine_threadsafe(
            invoke(name, self._ctx, self._allowed, args), self._loop
        )
        return future.result(timeout=self._timeout_s)
