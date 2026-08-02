# Harness 包覆 Business Workflow — 設計文件

> **Historical delivery record (2026-08-02):** 本文件描述已交付演進；未來結構調整以 [Architecture Hard Reset design](../architecture-hard-reset/03-design.md) 為準。

> 依據 [01-plan.md](01-plan.md) 和 [02-spec.md](02-spec.md)。

---

## 1. 架構圖：修改前 vs 修改後

### 修改前：三條執行路徑

```
路徑 A: Agent Skill in Harness（完整治理）
==========================================================

  D3 Agent Run
       |
       v
  +--[preflight]--+
  |                |
  v                |
  [model_step]<----+--------+
       |                    |
       v                    |
  [policy_gate]             |
       |                    |
       +-- kind=agentic --> [_enter_skill_scope]
       |                    |     |
       |                    |     +--> model_step ⟲ invoke_tool
       |                    |     |    (agent loop, budget_gate)
       |                    |     |
       |                    |     +--> [_exit_skill_scope]
       |                    |                |
       v                    v                |
  [budget_gate] <----------------------------+
       |
       v
  [validate_output]
       |
       v
  [finalize]


路徑 B: Flow in Harness（半包覆）
==========================================================

  D3 Agent Run
       |
       v
  +--[preflight]--+
  |                |
  v                |
  [model_step]<-+  |
       |        |  |
       v        |  |
  [policy_gate] |  |
       |        |  |
       +-- kind=flow --> [_invoke_business_workflow]
       |        |             |
       |        |             v
       |        |        invoke_pinned_legacy_flow()
       |        |             |
       |        |             +-- SAFE_LEGACY_NODES check  <-- !!阻擋!!
       |        |             |   (只允許 4 個節點)
       |        |             |
       |        |             +-- compile() --> graph.ainvoke()
       |        |             |   (子圖，只有 Node Shell)
       |        |             |
       |        |             +-- 結果回寫 Harness state
       |        |                        |
       v        +------------------------+
  [budget_gate] (事後批次扣減)
       |
       v
  [finalize]

  問題：script/tool 步驟被白名單拒絕，flow 內部無 policy/budget 治理


路徑 C: 直接 /skills/{name}/invoke（無 Harness）
==========================================================

  POST /skills/{name}/invoke
       |
       v
  main.py::invoke_skill()
       |
       +-- kind=agentic --> agent_skill_graph
       |
       +-- kind=flow -----> custom.load()
                                |
                                v
                           compile() --> graph.ainvoke()
                                |         (只有 Node Shell)
                                |
                                v
                           _run_with_timeout()
                                |
                                v
                           public_output() --> 回傳

  問題：無 preflight、無 budget gate、無 finalize、無 audit trail
```

### 修改後：統一治理

```
路徑 A: Agent Skill in Harness（不變）
==========================================================

  （同上，完全不動）


路徑 B': Flow in Harness（全能力，Phase 2）
==========================================================

  D3 Agent Run
       |
       v
  +--[preflight]--+
  |                |
  v                |
  [model_step]<-+  |
       |        |  |
       v        |  |
  [policy_gate] |  |
       |        |  |
       +-- kind=flow --> [_invoke_business_workflow]
       |        |             |
       |        |             v
       |        |        invoke_pinned_legacy_flow()
       |        |             |
       |        |             +-- _validate_steps()
       |        |             |   (只驗 loop bound + tool in effective set)
       |        |             |
       |        |             +-- copy_context().run():
       |        |             |     set_budget_callback(_on_step)
       |        |             |
       |        |             +-- compile() --> graph.ainvoke()
       |        |             |   (Node Shell + ContextVar callback)
       |        |             |         |
       |        |             |   每步: cb(node, ms)
       |        |             |     -> budget 扣減
       |        |             |     -> BudgetExhausted? fatal_error
       |        |             |
       |        |             +-- LegacyFlowResult(steps_consumed, ...)
       |        |                        |
       v        +------------------------+
  [budget_gate] (扣減 steps_consumed + tool_rounds_consumed)
       |
       v
  [finalize]

  改善：所有節點類型可用，per-step budget 治理


路徑 C': Standalone Flow Invoke（輕量 Harness，Phase 3）
==========================================================

  POST /skills/{name}/invoke
       |
       v
  main.py::invoke_skill()
       |
       +-- kind=agentic --> agent_skill_graph（不變）
       |
       +-- kind=flow -----> invoke_flow_with_governance()
                                |
                                v
                           [flow_preflight]
                              hash 驗證
                                |
                                v
                           _prepare_flow_state()
                                |
                                v
                           _execute_compiled_flow()
                              +-- copy_context().run():
                              |     set_budget_callback(...)
                              |
                              +-- compile() --> graph.ainvoke()
                              |   (Node Shell + ContextVar callback)
                              |         |
                              |   每步: cb(node, ms)
                              |     -> budget 扣減
                              |     -> BudgetExhausted? fatal
                              |
                              +-- asyncio.timeout 包覆
                                |
                                v
                           [flow_finalize]
                              workflow_completed event
                                |
                                v
                           FlowGovernanceResult
                                |
                                v
                           public_output() --> 回傳

  改善：有 preflight、budget gate、finalize、audit trail


Phase 4 最終態：
==========================================================

  路徑 B' 和 C' 共用 flow_harness.py 的核心函式：
    _prepare_flow_state()
    _execute_compiled_flow()
    _validate_steps()

  legacy_flow.py 刪除，LegacyFlowDenied → FlowDenied
  legacy_flow_completed → workflow_completed
```

---

## 2. 模組職責劃分

```
+-------------------------------------------------------------------+
|                        呼叫端                                      |
|  graph.py (Harness)         main.py (endpoint)                    |
|  _invoke_business_workflow  invoke_skill                          |
|         |                        |                                |
|         v                        v                                |
|  +--- flow_harness.py ----------------------------------------+  |
|  |  invoke_pinned_legacy_flow  invoke_flow_with_governance     |  |
|  |         |                        |                          |  |
|  |         +------+  共用  +--------+                          |  |
|  |                |        |                                   |  |
|  |         _prepare_flow_state()                               |  |
|  |         _execute_compiled_flow()                            |  |
|  |         _validate_steps()                                   |  |
|  +-------|-----------------------------------------------------+  |
|          |                                                        |
|          v                                                        |
|  +--- compiler.py ---+    +--- node_shell.py ------+              |
|  | compile()         |    | harnessed()            |              |
|  | _build_graph()    |    | _budget_callback_var   |              |
|  | step_analysis()   |    | set_budget_callback()  |              |
|  | cache (不動)      |    | BudgetExhausted        |              |
|  +-------------------+    +------------------------+              |
+-------------------------------------------------------------------+
```

### 各模組職責

| 模組 | 管什麼 | 不管什麼 |
|------|--------|---------|
| **graph.py** (Harness) | 14 節點的固定骨架、agent loop、policy gate、全局 budget gate、preflight/finalize | flow 內部節點的執行（委託 flow_harness） |
| **flow_harness.py** | flow 執行的治理包覆（preflight、budget callback 注入、finalize）、state 組裝、timeout、validation | 節點級治理（委託 Node Shell）、flow 編譯（委託 compiler） |
| **compiler.py** | YAML → LangGraph 圖的編譯、cache、step 結構分析、audit 終端節點注入 | 執行期行為（不知道 budget、policy、任何 runtime state） |
| **node_shell.py** | 節點級治理殼：fatal 短路、trace、writes/reads 契約、immutable keys、authority keys、**budget callback dispatch** | 決定 budget 是否耗盡（callback 決定）、policy 裁定 |

### 新增 vs 修改

| 項目 | Phase | 動作 |
|------|-------|------|
| `node_shell.py` 的 `_budget_callback_var` + `BudgetExhausted` + `set_budget_callback` | 1 | 新增 |
| `node_shell.py` 的 `harnessed()` callback 呼叫 | 1 | 修改（插入 ~10 行） |
| `legacy_flow.py` 白名單移除 + validation 重構 + callback 注入 | 2 | 修改 |
| `legacy_flow.py` 的 `_RuntimeDeps` allowlist 化 | 2 | 修改 |
| `flow_harness.py` | 3 | 新增 |
| `main.py` flow dispatch 改呼叫 governance wrapper | 3 | 修改 |
| `compiler.py` 的 `step_analysis()` | 4 | 新增 |
| `legacy_flow.py` → 合併入 `flow_harness.py` 後刪除 | 4 | 刪除 |

---

## 3. 資料流：Business Workflow YAML 從提交到執行完成

```
[1] YAML 定義撰寫
     |
     | POST /api/skills  (platform → backend)
     v
[2] Backend 儲存
     skill / skill_revision 表
     kind="flow", sha256=hash(definition)
     |
     | D3 Agent Run 或 POST /skills/{name}/invoke
     v
[3] Artifact 載入
     Backend GET /api/skills/{name}/revisions/{rev}
     → platform/workflow 取得 Skill 物件
     |
     v
[4] 驗證
     _validate_steps():
       - loop bounded?
       - tool in effective set?
     |
     v
[5] 編譯
     compiler.compile(skill, deps, cache=True)
       - cache key: (name, rev, sha256, id(deps))
       - hit → 直接回傳 CompiledStateGraph
       - miss → _build_graph():
           _scan() → 收集 specs/keys
           _Builder.emit_steps() → 產出 LangGraph nodes + edges
           每個 node 包裹 node_shell.harnessed()
           末端強制 audit_feedback node
     |
     v
[6] Budget callback 注入
     ctx = copy_context()
     ctx.run(set_budget_callback, _on_step)
     |
     v
[7] 執行
     ctx.run(graph.ainvoke, state, config)
       |
       +-- Node 1: harnessed("query_intake", fn)
       |     fn(filtered_state) → output
       |     writes contract → strip unauthorized keys
       |     cb = _budget_callback_var.get()
       |     await cb("query_intake", elapsed_ms)
       |       → remaining_steps -= 1
       |       → OK or BudgetExhausted
       |     trace entry appended
       |
       +-- Node 2: harnessed("retrieval_planner", fn)
       |     ... 同上 ...
       |
       +-- ... (所有 compiled nodes) ...
       |
       +-- Node N: harnessed("audit_feedback", fn)
       |     (compiler 強制的終端節點)
       |
       v
[8] 結果收集
     output = graph output state
     public_output(output) → strip __-prefixed keys
     |
     v
[9] 治理記錄
     路徑 B': _invoke_business_workflow 回寫 Harness state
       → steps_consumed, tool_rounds_consumed
       → Harness budget_gate 扣減
       → Harness finalize 產出 terminal event
     路徑 C': flow_finalize 產出 workflow_completed event
     |
     v
[10] 回傳
     路徑 B': Harness 繼續 agent loop 或 finalize
     路徑 C': SkillInvokeResponse(skill=name, output=public_output)
```

---

## 4. §3.5 Dead Code / Reuse / Test 退役的重構設計

### DC-1: SAFE_LEGACY_NODES 移除

```
Before:                              After:
  legacy_flow.py L37-44              (deleted)
  SAFE_LEGACY_NODES = frozenset({    
    "answer_composer",               _validate_steps():
    "evidence_verification",           - unbounded loop check (kept)
    "query_intake",                    - tool effective set check (new)
    "retrieval_planner",               - (no whitelist)
  })                                 
                                     
  _validate_steps L186:              
    if spec.name not in              
      SAFE_LEGACY_NODES: raise       (deleted)
```

### DC-2: _validate_steps script/tool/node 拒絕

```
Before:                              After:
  L180: if kind == "script":         (deleted — script 交 Node Shell)
          raise LegacyFlowDenied     
  L183: if kind == "tool":           if kind == "tool":
          raise LegacyFlowDenied       if spec.tool_name not in
                                         effective_tools:
                                         raise LegacyFlowDenied
  L186-191: SAFE_LEGACY_NODES        (deleted — 見 DC-1)
            + requires_tools         
            + dynamic_reads          
            + llm deps checks        
```

### DC-3: scripts_present early reject

```
Before:                              After:
  L91-92:                            (deleted — 兩行直接移除)
  if artifact.scripts_present:       
    raise LegacyFlowDenied(          
      "legacy flow contains          
       external code")               
```

### DC-4: main.py flow direct ainvoke

```
Before:                              After:
  L555-567:                          if loaded.skill.kind == "agentic":
  if loaded.skill.kind == "agentic":   ... (不變)
    ...                              else:
  else:                                result = await
    timeout = ...                        invoke_flow_with_governance(...)
    config = ...                       return SkillInvokeResponse(
    output = await _run_with_timeout(    skill=name,
      graph.ainvoke(state, config),      output=result.output,
      timeout, name                    )
    )                                
    return SkillInvokeResponse(...)  (DC-4 路徑刪除)
```

### RU-1: step-bound 走訪統一

```
Before:                              After:
  legacy_flow.py:                    compiler.py (新增):
    _tool_call_bound() L220-243       step_analysis(skill) -> StepAnalysis
    _step_bound() L246-266              .step_bound
                                        .tool_call_bound
  compiler.py:                          .recursion_limit
    recursion_limit() L487-510       
                                     三者共用同一走訪邏輯
  (兩處各自走訪 step 結構)           legacy_flow→flow_harness 引用
                                       compiler.step_analysis()
```

### RU-2: _RuntimeDeps allowlist

```
Before:                              After:
  L64-75:                            class _RuntimeDeps:
  class _RuntimeDeps:                  _ALLOWED = frozenset({...})
    def __getattr__(self, name):       def __getattr__(self, name):
      return getattr(                    if name not in self._ALLOWED:
        self._base, name)                  raise AttributeError(...)
                                         return getattr(self._base, name)
  (全代理，無限制)                   (顯式白名單)
```

### RU-3: flow 執行 setup 共用

```
Before:                              After:
  legacy_flow.py L107-135:           flow_harness.py:
    state 組裝                         _prepare_flow_state()
    timeout 設定                         → state + config
    compile()                          _execute_compiled_flow()
    graph.ainvoke()                       → cb 注入 + timeout + ainvoke
                                     
  main.py L555-567:                  兩個呼叫端:
    state 組裝                         invoke_pinned_legacy_flow
    config 設定                          → 呼叫共用函式
    _run_with_timeout(ainvoke)         invoke_flow_with_governance
                                         → 呼叫共用函式
  (兩處重複相同模式)                 
```

### RU-4: legacy_flow.py 合併

```
Phase 4:
  legacy_flow.py  ─────merge───→  flow_harness.py
                                    (單一模組)
  
  imports 更新:
    graph.py: from .legacy_flow → from .flow_harness
    test files: 同步更新
  
  名稱更新:
    LegacyFlowDenied  → FlowDenied
    LegacyFlowResult  → (移除，用 FlowGovernanceResult)
    invoke_pinned_legacy_flow → (內部函式，不導出)
    legacy_flow_completed → workflow_completed
```

---

## 5. 跨模組介面定義

### node_shell ← flow_harness 介面

```
node_shell.py exports:
  BudgetCallback = Callable[[str, float], Awaitable[None]]
  BudgetExhausted(RuntimeError)
  set_budget_callback(cb: BudgetCallback | None) -> None

flow_harness.py consumes:
  from .engine.node_shell import (
      set_budget_callback, BudgetExhausted, BudgetCallback,
  )
```

### compiler ← flow_harness 介面

```
compiler.py exports (Phase 4 新增):
  StepAnalysis(frozen dataclass):
    step_bound: int
    tool_call_bound: int
    recursion_limit: int
  step_analysis(skill: Skill) -> StepAnalysis

compiler.py exports (既有，不變):
  compile(skill, deps, *, cache=True) -> CompiledStateGraph
  public_output(state: dict) -> dict

flow_harness.py consumes:
  from .engine.compiler import compile, public_output, step_analysis
```

### flow_harness ← graph.py 介面

```
flow_harness.py exports:
  FlowDenied(RuntimeError)                      # Phase 4 更名
  FlowGovernanceResult(frozen dataclass):
    status: str
    output: dict
    governance: dict
  invoke_pinned_legacy_flow(                     # Phase 2-3，Phase 4 內部化
    *, artifact, raw_input, snapshot,
    rule_tools, deps, timeout_seconds,
    recursion_cap, remaining_tool_rounds,
    remaining_steps,
  ) -> LegacyFlowResult

graph.py consumes:
  from .flow_harness import (
      invoke_pinned_legacy_flow, FlowDenied,
  )
```

### flow_harness ← main.py 介面

```
flow_harness.py exports:
  invoke_flow_with_governance(                   # Phase 3
    *, skill, raw_input, deps,
    timeout_seconds, step_budget, tool_round_budget,
  ) -> FlowGovernanceResult

main.py consumes:
  from .runtime.flow_harness import (
      invoke_flow_with_governance,
  )
```

### Backend 事件介面（Phase 4 同步）

```
Backend 允許清單 (AgentRunRepository.cs, InMemoryAgentRunRepository.cs):
  過渡期: {"legacy_flow_completed", "workflow_completed", ...}
  最終態: {"workflow_completed", ...}  (移除 legacy_flow_completed)

Workflow 發送端:
  Phase 1-3: event_type = "legacy_flow_completed" (不變)
  Phase 4 過渡: 兩個 event_type 都發送
  Phase 4 完成: event_type = "workflow_completed" only
```
