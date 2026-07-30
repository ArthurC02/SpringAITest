# Harness 包覆 Business Workflow — 規格書

> 依據 [01-plan.md](01-plan.md)，本文件定義每個 Phase 的精確技術規格。

---

## Phase 1: Node Shell Budget Callback（基礎設施）

### 1.1 ContextVar 宣告

```python
# node_shell.py, module-level
from contextvars import ContextVar
from typing import Callable, Awaitable

BudgetCallback = Callable[[str, float], Awaitable[None]]
#                          ^node_name ^elapsed_ms

_budget_callback_var: ContextVar[BudgetCallback | None] = ContextVar(
    "_budget_callback_var", default=None
)
```

- 型別：`Callable[[str, float], Awaitable[None]]`（async callback）
- 參數：`node_name: str`（執行完成的節點名）、`elapsed_ms: float`（該節點耗時 ms）
- 回傳：`None`（正常）或 raise `BudgetExhausted`（要求短路）
- 生命週期：由呼叫端在 `copy_context().run()` 內設定，invoke 結束後隨 context 消滅

### 1.2 便利函式

```python
def set_budget_callback(cb: BudgetCallback | None) -> None:
    """Set the per-step budget callback for the current context."""
    _budget_callback_var.set(cb)
```

只是 `ContextVar.set` 的薄包裝，便於 import。

### 1.3 BudgetExhausted 例外

```python
class BudgetExhausted(RuntimeError):
    """Raised by a budget callback to short-circuit a flow."""
    pass
```

新增於 `node_shell.py`，`harnessed()` 專門捕捉此例外。

### 1.4 harnessed() 修改

在 `harnessed()` 現有的 trace assembly 前（目前 L192 之前、L173 成功路徑之後），插入 callback 呼叫：

```python
# 節點成功完成後、trace assembly 前
cb = _budget_callback_var.get()
if cb is not None:
    try:
        await cb(node_name, elapsed_ms)
    except BudgetExhausted as exc:
        # 轉為 fatal_error，走既有 fatal 路徑
        return {
            "fatal_error": f"budget_exhausted:{node_name}",
            "__trace": [TraceEntry(
                node=node_name,
                status="error",
                error=str(exc),
                duration_ms=elapsed_ms,
                # ... 同現有 error trace 格式
            )],
        }
```

行為規格：

| 情境 | 行為 |
|------|------|
| `_budget_callback_var.get()` 回傳 `None` | 不呼叫 callback，行為與現有完全一致 |
| callback 正常回傳 | 繼續 trace assembly，行為不變 |
| callback raise `BudgetExhausted` | 設定 `fatal_error = "budget_exhausted:{node_name}"`，回傳 fatal trace entry；後續節點走既有 fatal 短路路徑 |
| callback raise 其他例外 | 不捕捉，讓 `harnessed()` 既有的 exception-to-fatal 區塊（L153-173）處理 |

### 1.5 對 compiler cache 的影響

**無影響。** ContextVar 在執行期（`graph.ainvoke`）讀取，不在編譯期（`compile()`）。cache key 仍為 `(name, revision, sha256, id(deps))`，callback 不進入 key。同一 `CompiledStateGraph` 物件可在不同 callback 下重複使用。

### 1.6 不新增 state channel

Phase 1 不修改任何 Harness state channel 或 config 欄位。`fatal_error` 是既有 channel。

---

## Phase 2: 移除 Legacy Flow 白名單限制

### 2.1 移除項目

| 項目 | 動作 |
|------|------|
| `SAFE_LEGACY_NODES`（L37-44） | 刪除整個 `frozenset` 常量 |
| `scripts_present` early reject（L91-92） | 刪除 `if artifact.scripts_present: raise` 兩行 |
| `_validate_steps` 中 script 拒絕（L181） | 刪除 `kind == "script"` 分支 |
| `_validate_steps` 中 tool 無條件拒絕（L183） | 替換為 effective tool set 檢查（見 §2.2） |
| `_validate_steps` 中 `SAFE_LEGACY_NODES` 檢查（L186-191） | 刪除；改為只檢查節點是否已在 registry 註冊 |

### 2.2 _validate_steps 重構後行為

```python
def _validate_steps(
    skill: Skill,
    steps: list[dict],
    effective_tools: frozenset[str],
    deps: _RuntimeDeps,
) -> None:
```

保留的驗證（不變）：
- 未 bound 的 `loop` → `LegacyFlowDenied("unbounded loop")`
- 遞迴走訪 `sequence` / `branch` / `loop` 結構

新行為：
- **tool 步驟：** `spec.tool_name not in effective_tools` → `LegacyFlowDenied(f"tool {spec.tool_name} not in effective set")`；在 effective set 內則通過
- **script 步驟：** 交由 Node Shell + `ISOLATED_SKILL_SCRIPTS_ENABLED` feature flag 治理，`_validate_steps` 不再拒絕
- **任意節點：** 交由 compiler（registry 查詢）+ Node Shell 治理，`_validate_steps` 不再依白名單拒絕

### 2.3 _RuntimeDeps 改為顯式白名單（RU-2）

```python
class _RuntimeDeps:
    _ALLOWED = frozenset({
        "node_registry", "tool_bag", "script_runner",
        "retrieval_client", "llm_client", "config_client",
    })

    def __init__(self, base: Any) -> None:
        self._base = base
        self._audit = _NoopAuditRepository()

    @property
    def audit_repo(self) -> _NoopAuditRepository:
        return self._audit

    def __getattr__(self, name: str) -> Any:
        if name not in self._ALLOWED:
            raise AttributeError(
                f"_RuntimeDeps does not expose '{name}'"
            )
        return getattr(self._base, name)
```

### 2.4 Budget callback 注入點

`invoke_pinned_legacy_flow` 在呼叫 `graph.ainvoke` 前注入 callback：

```python
from contextvars import copy_context
from workflow.app.engine.node_shell import set_budget_callback, BudgetExhausted

async def invoke_pinned_legacy_flow(*, ..., remaining_tool_rounds, remaining_steps):
    # ... validation ...

    async def _on_step(node_name: str, elapsed_ms: float) -> None:
        nonlocal remaining_steps, remaining_tool_rounds
        remaining_steps -= 1
        if node_name in _TOOL_NODES:
            remaining_tool_rounds -= 1
        if remaining_steps <= 0 or remaining_tool_rounds <= 0:
            raise BudgetExhausted(f"budget exhausted after {node_name}")

    ctx = copy_context()
    ctx.run(set_budget_callback, _on_step)
    output = await ctx.run(graph.ainvoke, state, config=config)
```

### 2.5 回報 Harness state

`invoke_pinned_legacy_flow` 回傳的 `LegacyFlowResult` 不變結構，但新增欄位：

```python
@dataclass(frozen=True)
class LegacyFlowResult:
    status: str
    content: str
    tool_calls_bound: int
    steps_bound: int
    steps_consumed: int     # 新增：實際消耗的 step 數
    tool_rounds_consumed: int  # 新增：實際消耗的 tool round 數
```

`_invoke_business_workflow`（graph.py）使用這些欄位更新 Harness state 的 budget 計數器。

### 2.6 LegacyFlowDenied 語義收窄

Phase 2 後 `LegacyFlowDenied` 只在以下情境拋出：

| 情境 | 訊息 |
|------|------|
| artifact 非 flow kind | `"not a flow skill"` |
| 未 bound 的 loop | `"unbounded loop in ..."` |
| input schema 驗證失敗 | `"input validation failed: ..."` |
| tool 不在 effective set | `"tool {name} not in effective set"` |

不再因 script 步驟、tool 步驟存在、或節點不在白名單而拋出。

---

## Phase 3: `/skills/{name}/invoke` 經 Harness Wrapper 執行

### 3.1 新模組：`workflow/app/runtime/flow_harness.py`

```python
async def invoke_flow_with_governance(
    *,
    skill: Skill,
    raw_input: dict,
    deps: Any,
    timeout_seconds: float,
    step_budget: int,
    tool_round_budget: int,
) -> FlowGovernanceResult:
    """Execute a flow skill with preflight/budget/finalize governance."""
    ...
```

回傳型別：

```python
@dataclass(frozen=True)
class FlowGovernanceResult:
    status: str          # "completed" | "budget_exhausted" | "timeout" | "error"
    output: dict         # public_output() 過濾後的結果
    governance: dict     # 治理 trace（不外漏給 API 回傳）
```

### 3.2 Harness Wrapper 包含的節點

| 節點 | 行為 | 來自 |
|------|------|------|
| `flow_preflight` | 驗證 skill 定義 hash 一致性 | 新增 |
| `[compiled flow nodes]` | compiler 產出的子圖，每節點有 Node Shell + budget callback | 既有 compiler + Phase 1 |
| `flow_finalize` | 產出 terminal event、清理 | 新增 |

**不包含的節點（與 Harness 差異）：**

| 節點 | 為什麼跳過 |
|------|-----------|
| `model_step` | flow 是確定性，不需 LLM |
| `policy_gate` | Business Rules 裁定是 agent loop 概念，flow 不需要 |
| `budget_gate`（Harness 層） | 由 Phase 1 的 per-step callback 替代 |
| `validate_output` | flow 的 output schema 由 compiler 定義的 state channel 控制，不需額外驗證 |

### 3.3 Preflight 行為

```python
async def _flow_preflight(skill: Skill) -> None:
    """Verify flow definition integrity before execution."""
    computed = sha256(canonical_json(skill.definition))
    if computed != skill.sha256:
        raise FlowPreflightError(f"definition hash mismatch: {computed} != {skill.sha256}")
```

失敗 → `FlowGovernanceResult(status="error", output={}, governance={...})`

### 3.4 Budget Gate 行為

使用 Phase 1 的 ContextVar callback（與 Phase 2 相同機制）：

| 預算類型 | 預設值 | 觸發行為 |
|---------|--------|---------|
| `step_budget` | `skill.step_limit or 100` | `BudgetExhausted` → `status="budget_exhausted"` |
| `timeout_seconds` | `skill.timeout_seconds or 30.0` | `asyncio.timeout` → `status="timeout"` |
| `tool_round_budget` | `len(effective_tools) * 10 or 50` | `BudgetExhausted` → `status="budget_exhausted"` |

### 3.5 Finalize 行為

無論執行成功或失敗，`flow_finalize` 必定執行：

- 記錄 `workflow_completed` 事件（event_type, skill_name, status, steps_consumed, elapsed_ms）
- 清理 ContextVar（由 `copy_context().run()` 自動處理）
- `governance` dict 包含完整 trace，但不進入 API 回傳（`public_output()` 過濾）

### 3.6 main.py 修改

```python
# invoke_skill endpoint, flow kind dispatch
if loaded.skill.kind == "agentic":
    # 不變：走 agent_skill_graph
    ...
else:
    if settings.FLOW_GOVERNANCE_ENABLED:
        result = await invoke_flow_with_governance(
            skill=loaded.skill,
            raw_input=clean,
            deps=loaded.deps,
            timeout_seconds=loaded.skill.timeout_seconds or timeout_default,
            step_budget=loaded.skill.step_limit or 100,
            tool_round_budget=50,
        )
        return SkillInvokeResponse(skill=name, output=result.output)
    else:
        # DC-4: 原路徑，flag off 時使用，Phase 4 移除
        ...
```

### 3.7 共用函式（RU-3）

`flow_harness.py` 導出兩個共用函式，供 `legacy_flow.py` 和 `main.py` 共用：

```python
def _prepare_flow_state(
    skill: Skill, raw_input: dict, deps: Any
) -> tuple[dict, RunnableConfig]:
    """Build initial state and config for a flow execution."""
    ...

async def _execute_compiled_flow(
    graph: CompiledStateGraph,
    state: dict,
    config: RunnableConfig,
    budget_callback: BudgetCallback | None,
    timeout_seconds: float,
) -> dict:
    """Run a compiled flow graph with optional budget callback and timeout."""
    ...
```

`invoke_pinned_legacy_flow`（Phase 2 重構後）和 `invoke_flow_with_governance` 都呼叫這兩個函式。

### 3.8 Feature Flag 規格

| Flag | 模組 | 預設值 | 行為 |
|------|------|--------|------|
| `FLOW_GOVERNANCE_ENABLED` | `workflow/app/config.py` | `False` | `True`：flow invoke 走 governance wrapper；`False`：走原路徑（DC-4） |

來源：環境變數 `FLOW_GOVERNANCE_ENABLED`，bool 解析（`"true"/"1"` → True）。

Phase 4 移除此 flag，governance wrapper 成為唯一路徑。

---

## Phase 4: 清理與統一

### 4.1 模組合併

`legacy_flow.py` 的剩餘邏輯合併進 `flow_harness.py`：

- `invoke_pinned_legacy_flow` → 改為 `flow_harness.py` 的內部函式（或刪除，以 `_execute_compiled_flow` 取代）
- `LegacyFlowResult` → 替換為 `FlowGovernanceResult`
- `LegacyFlowDenied` → 更名為 `FlowDenied`（保留語義）
- `_validate_steps` → 搬入 `flow_harness.py`
- `legacy_flow.py` 刪除

### 4.2 事件名稱更新

| 舊名 | 新名 | 過渡策略 |
|------|------|---------|
| `legacy_flow_completed` | `workflow_completed` | Phase 4 初期：兩個名稱都發送（Backend 允許清單先加新名）；確認無舊名消費者後移除舊名 |

**Backend 同步需求：** `AgentRunRepository.cs`（L22）和 `InMemoryAgentRunRepository.cs`（L18）的事件類型允許清單必須先加入 `workflow_completed`，在同一 PR 或先行 PR。

### 4.3 graph.py 修改

`_invoke_business_workflow` 改呼叫 `flow_harness._execute_compiled_flow`，消除與 `legacy_flow.py` 的耦合。

### 4.4 AGENTS.md 更新

root `AGENTS.md` 和 `workflow/AGENTS.md` 的路徑描述更新：

- 移除「路徑 C（無 Harness）」描述
- 更新「路徑 B」為「Harness 內 flow（全能力）」
- 新增「路徑 C'（standalone flow invoke，輕量 Harness wrapper）」

### 4.5 Step-bound 走訪統一（RU-1）

`_tool_call_bound` 和 `_step_bound`（legacy_flow.py:220-266）與 `compiler.py` 的 `recursion_limit` 有重複的 step 結構走訪。統一為 compiler 層的 `step_analysis()` 函式：

```python
# compiler.py
@dataclass(frozen=True)
class StepAnalysis:
    step_bound: int
    tool_call_bound: int
    recursion_limit: int

def step_analysis(skill: Skill) -> StepAnalysis:
    """Unified step structure analysis for budget and recursion."""
    ...
```

`flow_harness.py` 和 Harness 的 budget 計算都引用此函式。

---

## 錯誤處理規格（所有 Phase）

### 失敗情境矩陣

| 情境 | 觸發位置 | 錯誤類型 | 對外行為 |
|------|---------|---------|---------|
| callback 回報 budget 耗盡 | Node Shell（Phase 1） | `BudgetExhausted` → `fatal_error` | flow 終止，Harness finalize 正常執行 |
| flow 定義 hash 不符 | flow_preflight（Phase 3） | `FlowPreflightError` | `status="error"`，不執行 flow |
| tool 不在 effective set | `_validate_steps`（Phase 2） | `LegacyFlowDenied` | Harness: `_failure` → `finalize`；standalone: `status="error"` |
| 未 bound loop | `_validate_steps` | `LegacyFlowDenied` | 同上 |
| input schema 不符 | `_validate_steps` | `LegacyFlowDenied` | 同上 |
| 節點執行 exception | Node Shell（既有） | `fatal_error` | flow 終止，後續節點 skip |
| timeout | `asyncio.timeout`（Phase 2/3） | `asyncio.TimeoutError` | Harness: `fatal_error="timeout"`；standalone: `status="timeout"` |
| script 步驟且 `ISOLATED_SKILL_SCRIPTS_ENABLED=false` | ScriptRunner（既有） | `fatal_error` | 同節點執行 exception |
| compiler 無法編譯 | `compile()`（既有） | `CompilationError` | Harness: `_failure`；standalone: 500 |

### 不變的錯誤行為

- Node Shell 的 fatal 短路：`state["fatal_error"]` truthy 時所有後續節點 skip（`run_on_fatal=False`）
- Node Shell 的 immutable key 防護：非 `query_intake` 節點修改 immutable key 被靜默丟棄
- RUNTIME_AUTHORITY_KEYS 一律從節點 output 移除

---

## State Channel 與 Config 欄位變更

### 新增欄位

| Phase | 位置 | 欄位 | 型別 | 說明 |
|-------|------|------|------|------|
| 2 | `LegacyFlowResult` | `steps_consumed` | `int` | 實際消耗 step 數 |
| 2 | `LegacyFlowResult` | `tool_rounds_consumed` | `int` | 實際消耗 tool round 數 |
| 3 | `flow_harness.py` | `FlowGovernanceResult` | dataclass | 見 §3.1 |
| 3 | `config.py` | `FLOW_GOVERNANCE_ENABLED` | `bool` | 見 §3.8 |

### 不變的 Harness state channels

Harness 的 `StateGraph` channels（在 `graph.py` 定義）不新增、不修改。flow 的 budget 消耗透過 `_invoke_business_workflow` 的回傳值更新既有 budget 計數器（`tool_rounds_remaining`, `steps_remaining`），不新增 channel。
