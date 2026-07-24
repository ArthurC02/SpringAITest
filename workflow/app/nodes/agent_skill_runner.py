"""agentic skill 的受治理 runner 節點（規格 R5、設計 §4.3）。

## 為什麼 agentic 是「一顆節點」而不是頂層 orchestrator

整個功能的核心不變式：agentic 的自主性（ReAct loop）活在**一顆受 Harness 管理的節點內**，
不是另一套 orchestration runtime。因此它與其他節點共用同一套治理：fatal 短路、例外安全
輸出、immutable 身分鍵、declared-writes 剝除、trace/audit。runner 只寫一個固定 public key
`answer`；其餘全部由 Harness 移除或保護。tool 一律經 `tool_registry.invoke`（allowlist、
trace、身分由 ToolContext 帶），resource 只能唯讀讀 package 內 references/assets。

## step limit 與 timeout（設計 §4.3-5）

ReAct loop 的界限**不重用** compiler.recursion_limit（那是 YAML graph 的護欄）。這裡以
create_react_agent 支援的 `recursion_limit` config 設步數上限，並以 `asyncio.timeout` 包住
整個 run。兩個界限的實際計數語意由 test_agent_skill_runner 的 pinning test 釘住：
- 安裝版 create_react_agent 一輪 tool call 消耗 2 個 superstep（model + tools），完成再收尾
  一個 model superstep，故容納 R 輪 tool call 需要 recursion_limit >= 2R+3。
- 取**奇數** recursion_limit（2R+3）：超出預算時 create_react_agent 直接 raise
  GraphRecursionError（偶數會改回傳「need more steps」的 canned 訊息，繞過 fatal 路徑）。
兩個界限任一觸發 → 例外穿出 node fn → Harness 轉 fatal_error + 稽核仍執行，絕不無限重試，
也不留背景 tool（asyncio.timeout 取消整個 task，in-flight tool 的 await 被 cancel）。
"""

import asyncio
import re
from typing import Any, Callable, Optional

from langchain_core.tools import StructuredTool
from langgraph.prebuilt import create_react_agent
from pydantic import create_model

# ponytail: create_react_agent 在 LangGraph V1 標記 deprecated（V2 移除，改 langchain.agents.
# create_agent）。pyproject 釘 langgraph>=1.0,<2.0，故 V2 移除不會在此 pin 內發生；升級時
# 只換這一個 import + 呼叫點（recursion_limit/timeout 語意由 pinning test 守著，換 API 要重驗）。

from app.engine import tool_registry
from app.engine.node_registry import node
from app.engine.package import AgentSkillPackage
from app.engine.tool_registry import ToolContext
from app.nodes._llm_input import build_user_message

# 固定 public 輸出 key（Platform 與 Frontend 的 answer-key 優先序皆以 answer 為準）
ANSWER_KEY = "answer"

# ReAct loop 步數上限：允許最多幾輪 tool call（設計 §4.3-5；非 compiler.recursion_limit）。
# ponytail: 固定啟發值。真要 per-skill 調就從 frontmatter 讀一個 max_tool_rounds，等有需求再說。
MAX_TOOL_ROUNDS = 8
# create_react_agent 的 recursion_limit：奇數 → 超出預算 raise GraphRecursionError（見 module docstring）。
AGENT_RECURSION_LIMIT = 2 * MAX_TOOL_ROUNDS + 3

# 整體 timeout 預設（frontmatter timeout_seconds 優先）。
DEFAULT_AGENT_TIMEOUT_S = 60.0

# resource 唯讀回傳的大小上限：不把整包內容無條件塞進 prompt（規格 R5、AST-P1-006）。
RESOURCE_MAX_BYTES = 16_384

_RESOURCE_DIRS = ("references/", "assets/")
_DRIVE_RE = re.compile(r"^[a-zA-Z]:")


# ---------------------------------------------------------------------------
# resource tool（唯讀、package-scoped、path-normalized）
# ---------------------------------------------------------------------------


def read_resource(pkg: AgentSkillPackage, path: Any) -> str:
    """讀取 package 內 references/ 或 assets/ 下的資源（唯讀、有大小上限）。

    白名單、預設拒絕：絕對路徑、drive path、`..`/`.` 成分、references/assets 以外
    （含 scripts/）一律 raise。只從 in-memory 的 pkg.resources 讀，永不碰 host 檔案系統。
    """
    if not isinstance(path, str) or not path.strip():
        raise ValueError("resource path 不可為空")
    p = path.strip().replace("\\", "/")
    if p.startswith("/"):
        raise ValueError(f"不允許絕對路徑: {path!r}")
    if _DRIVE_RE.match(p):
        raise ValueError(f"不允許 drive path: {path!r}")
    if any(seg in ("..", ".") for seg in p.split("/")):
        raise ValueError(f"不允許 '..'／'.' 路徑成分: {path!r}")
    if not any(p.startswith(d) for d in _RESOURCE_DIRS):
        raise ValueError(
            f"只允許讀取 {'/'.join(d.rstrip('/') for d in _RESOURCE_DIRS)}/ 下的資源: {path!r}"
        )
    data = pkg.resources.get(p)
    if data is None:
        raise ValueError(f"找不到資源: {p!r}")
    return data[:RESOURCE_MAX_BYTES].decode("utf-8", errors="replace")


def make_resource_tool(pkg: AgentSkillPackage) -> StructuredTool:
    """把 read_resource 包成 LangChain tool（供 model 唯讀讀取 package 內附件）。"""

    async def _run(path: str) -> str:
        return read_resource(pkg, path)

    return StructuredTool.from_function(
        coroutine=_run,
        name="read_resource",
        description=(
            "讀取本 skill package 內 references/ 或 assets/ 下的附件內容（唯讀，相對路徑）。"
        ),
        args_schema=create_model("ReadResourceArgs", path=(str, ...)),
    )


# ---------------------------------------------------------------------------
# tool adapter（只轉接到 tool_registry.invoke，不繞過 registry、不持 token）
# ---------------------------------------------------------------------------


def _lc_name(name: str) -> str:
    """LangChain/OpenAI 的 function name 不接受 '.'；轉底線給 model，registry 仍用原名。"""
    return name.replace(".", "_")


def make_registry_tool(
    spec: tool_registry.ToolSpec,
    ctx: ToolContext,
    allowed: frozenset[str],
) -> StructuredTool:
    """把一個允許的 registry tool 包成 LangChain adapter。

    adapter body 只做一件事：呼叫 tool_registry.invoke(name, ctx, allowed, args)。
    - 不繞過 registry（allowlist 與 trace 的既有 enforcement 都在 invoke 內）。
    - 不持有任何 service token：身分只由閉包捕獲的 ctx（server-injected tenant/user/role）帶，
      token 由 http tool 的實作在呼叫當下自 settings 取，adapter 看不到也不需要。
    """

    async def _run(**kwargs: Any) -> Any:
        args = {k: v for k, v in kwargs.items() if v is not None}
        return await tool_registry.invoke(spec.name, ctx, allowed, args)

    # 由 spec.args_schema 動態建 args model（全設為 Optional，缺省交由 tool 本體處理）
    fields: dict[str, Any] = {
        key: (Optional[typ], None) for key, typ in spec.args_schema.items()
    }
    args_model = create_model(f"{_lc_name(spec.name)}_Args", **fields)
    return StructuredTool.from_function(
        coroutine=_run,
        name=_lc_name(spec.name),
        description=spec.description or spec.name,
        args_schema=args_model,
    )


# ---------------------------------------------------------------------------
# runner node
# ---------------------------------------------------------------------------


def _final_text(result: dict) -> str:
    """取 ReAct run 的最後一則 assistant 訊息文字。tool 結果／resource 內容／trace 不外流。"""
    messages = result.get("messages") or []
    if not messages:
        return ""
    content = getattr(messages[-1], "content", "")
    return content if isinstance(content, str) else str(content)


@node(
    name="agent_skill_runner",
    version="1.0",
    description="受 Harness 管理的 agentic runner：以 create_react_agent 執行 package instruction，只寫 answer",
    reads=["tenant_id", "user_id", "role"],  # 執行期讀身分三鍵建 ToolContext（server-injected）
    writes=[ANSWER_KEY],
    deps=[],  # reader / model / 身分容器由 compiler 以 params 注入（見 compiler agentic 分派）
    requires_tools=[],
    run_on_fatal=False,
)
def make_agent_skill_runner(
    *,
    reader: Any,
    chat_model_factory: Callable[[], Any],
    container_deps: Any,
    skill_name: str,
    uses_tools: list[str],
    input_keys: tuple[str, ...],
    timeout_s: float | None = None,
    answer_key: str = ANSWER_KEY,
):
    """建立 agent_skill_runner 節點函式。

    build 期取得 skill name／固定 answer key／deps（reader、model factory、身分容器）。
    執行期：從 state 建 ToolContext（server-injected 身分）→ reader 取 package →
    以 instruction + get_llm() 建 create_react_agent → 允許 tool 包成 adapter + resource tool
    → recursion_limit + asyncio.timeout 包住 run → 最後 assistant text 轉 {answer: text}。
    """
    allowed = frozenset(uses_tools)
    run_timeout_s = timeout_s if timeout_s is not None else DEFAULT_AGENT_TIMEOUT_S

    async def run(state: dict) -> dict:
        # 身分一律取自 state 的 server-injected 保留鍵（與 flow builder.tool_context 同源）
        ctx = ToolContext(
            tenant_id=state.get("tenant_id", ""),
            user_id=state.get("user_id", ""),
            role=state.get("role", ""),
            deps=container_deps,
        )
        pkg = await reader.read(skill_name, ctx)

        tools: list[StructuredTool] = []
        for name in sorted(allowed):
            spec = tool_registry.get(name)
            if spec is None:  # frontmatter 驗證時已擋；防呆
                raise ValueError(f"uses_tools 含未註冊的 tool: {name}")
            tools.append(make_registry_tool(spec, ctx, allowed))
        tools.append(make_resource_tool(pkg))

        agent = create_react_agent(
            chat_model_factory(), tools, prompt=pkg.instruction or None
        )
        user_message = build_user_message(state, input_keys)

        # step limit（recursion_limit）與整體 timeout：任一觸發 → 例外穿出 → Harness fatal + 稽核。
        async with asyncio.timeout(run_timeout_s):
            result = await agent.ainvoke(
                {"messages": [("user", user_message)]},
                config={"recursion_limit": AGENT_RECURSION_LIMIT},
            )

        return {answer_key: _final_text(result)}

    return run
