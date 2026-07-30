# Harness 包覆 Business Workflow — 驗收測試

> 依據 [01-plan.md](01-plan.md) 和 [02-spec.md](02-spec.md)。
> 每個測試案例標注 `[HP]` = happy path、`[EC]` = edge case、`[REG]` = regression。

---

## Phase 1: Node Shell Budget Callback

**測試檔：`tests/test_engine_node_shell.py`（擴充）**

### P1-T01: callback 正常回報 [HP]

```
test_budget_callback_invoked_with_node_name_and_elapsed

Given: ContextVar 設定了一個紀錄型 callback（收集呼叫參數）
When:  harnessed node 成功執行
Then:  callback 被呼叫一次
       callback 收到 (node_name, elapsed_ms)
       node_name 與 harnessed() 的第一個參數一致
       elapsed_ms > 0
       node output 正常回傳（與無 callback 時相同）
Verify: assert captured == [(expected_name, ANY_POSITIVE_FLOAT)]
```

### P1-T02: callback raise BudgetExhausted → fatal_error [HP]

```
test_budget_exhausted_converts_to_fatal_error

Given: ContextVar 設定了 raise BudgetExhausted("step limit") 的 callback
When:  harnessed node 成功執行
Then:  回傳 dict 包含 fatal_error="budget_exhausted:{node_name}"
       回傳 dict 包含 __trace entry with status="error"
       trace entry 的 error 欄位包含 "step limit"
Verify: assert result["fatal_error"].startswith("budget_exhausted:")
        assert result["__trace"][0].status == "error"
```

### P1-T03: BudgetExhausted 後後續節點 skip [EC]

```
test_fatal_from_budget_skips_subsequent_nodes

Given: 兩個 harnessed nodes in sequence
       第一個 node 的 callback raises BudgetExhausted
When:  依序執行兩個 nodes
Then:  第二個 node 的 fn 未被呼叫
       第二個 node 回傳 status="skipped"
Verify: assert second_fn.call_count == 0
```

### P1-T04: 無 callback 時行為不變 [REG]

```
test_no_callback_preserves_existing_behavior

Given: ContextVar 未設定（default None）
When:  harnessed node 執行
Then:  行為與 Phase 1 修改前完全一致
       output 正常回傳
       trace entry status="ok"
       無 fatal_error
Verify: 與既有 test 的 assert 完全相同
```

### P1-T05: callback raise 非 BudgetExhausted 例外 [EC]

```
test_callback_non_budget_exception_becomes_fatal

Given: ContextVar 設定了 raise ValueError("oops") 的 callback
When:  harnessed node 成功執行
Then:  走既有 exception-to-fatal 路徑
       fatal_error 包含 "oops"
       trace entry status="error"
Verify: assert "oops" in result["fatal_error"]
```

### P1-T06: compiler cache 與 callback 正交 [REG]

```
test_compiled_graph_reusable_across_callbacks

Given: compile(skill, deps, cache=True) 產出 graph G
When:  G.ainvoke() 在 callback_A context 執行
       G.ainvoke() 在 callback_B context 執行
       G.ainvoke() 在無 callback context 執行
Then:  三次都成功
       callback_A 和 callback_B 各自收到正確的呼叫
       compiler cache size 不增加（cache hit）
Verify: assert len(compiler._CACHE) == initial_size
```

### P1-T07: callback 在 run_on_fatal=True 節點也觸發 [EC]

```
test_callback_fires_on_run_on_fatal_node

Given: ContextVar 設定了紀錄型 callback
       harnessed node 有 run_on_fatal=True
       state 已有 fatal_error（前序節點設的）
When:  node 執行（run_on_fatal=True 所以不 skip）
Then:  callback 被呼叫
Verify: assert len(captured) == 1
```

---

## Phase 2: 移除 Legacy Flow 白名單

**測試檔：`tests/test_agent_runtime_legacy_flow.py`（重構 + 擴充）**

### P2-T01: 含 script 步驟的 flow 在 Harness 內執行 [HP]

```
test_flow_with_script_step_executes_in_harness

Given: flow 定義包含 script 步驟
       ISOLATED_SKILL_SCRIPTS_ENABLED=true
       從 D3 Agent Run 路徑進入 Harness
When:  _invoke_business_workflow 執行
Then:  script 步驟成功執行（不被 _validate_steps 拒絕）
       Node Shell 的 script 治理生效
       LegacyFlowResult.status == "completed"
Verify: assert result.status == "completed"
        assert trace 包含 script node entry
```

### P2-T02: 含 tool 步驟且 tool 在 effective set 內 → 通過 [HP]

```
test_flow_with_tool_in_effective_set_passes

Given: flow 定義包含 tool 步驟 "search_documents"
       snapshot.effective_tools 包含 "search_documents"
When:  _validate_steps + invoke_pinned_legacy_flow
Then:  tool 步驟成功執行
       LegacyFlowResult.status == "completed"
Verify: assert result.status == "completed"
```

### P2-T03: tool 不在 effective set → LegacyFlowDenied [EC]

```
test_flow_with_tool_not_in_effective_set_denied

Given: flow 定義包含 tool 步驟 "delete_all"
       snapshot.effective_tools 不包含 "delete_all"
When:  _validate_steps
Then:  raise LegacyFlowDenied("tool delete_all not in effective set")
Verify: with pytest.raises(LegacyFlowDenied, match="not in effective set")
```

### P2-T04: 非白名單節點可執行 [HP]

```
test_flow_with_arbitrary_registered_node_executes

Given: flow 定義包含 "custom_analysis" 節點（已在 node_registry）
       Phase 1 前此節點被 SAFE_LEGACY_NODES 拒絕
When:  invoke_pinned_legacy_flow
Then:  custom_analysis 成功執行
Verify: assert result.status == "completed"
```

### P2-T05: per-step budget 扣減 [HP]

```
test_per_step_budget_decrements_correctly

Given: flow 定義包含 3 個步驟
       remaining_steps=5
When:  invoke_pinned_legacy_flow
Then:  result.steps_consumed == 3
       callback 被呼叫 3 次
Verify: assert result.steps_consumed == 3
```

### P2-T06: budget 耗盡 → fatal_error [EC]

```
test_budget_exhausted_terminates_flow

Given: flow 定義包含 5 個步驟
       remaining_steps=2
When:  invoke_pinned_legacy_flow
Then:  第 3 步觸發 BudgetExhausted
       result.status == "fatal_error"
       result.steps_consumed == 2
       後續步驟未執行
Verify: assert result.status == "fatal_error"
        assert "budget_exhausted" in trace
```

### P2-T07: _RuntimeDeps 白名單防護 [EC]

```
test_runtime_deps_blocks_unauthorized_attribute

Given: _RuntimeDeps 包裝了一個含 _dangerous_method 的 deps
When:  deps._dangerous_method
Then:  raise AttributeError("_RuntimeDeps does not expose '_dangerous_method'")
Verify: with pytest.raises(AttributeError)
```

### P2-T08: _RuntimeDeps 白名單允許的屬性可存取 [HP]

```
test_runtime_deps_allows_whitelisted_attributes

Given: _RuntimeDeps 包裝了含 tool_bag 和 script_runner 的 deps
When:  deps.tool_bag / deps.script_runner
Then:  回傳 base deps 的對應屬性
Verify: assert deps.tool_bag is base.tool_bag
```

### P2-T09: 未 bound loop 仍被拒 [REG]

```
test_unbounded_loop_still_denied

Given: flow 定義包含未設 max_iterations 的 loop
When:  _validate_steps
Then:  raise LegacyFlowDenied("unbounded loop")
Verify: with pytest.raises(LegacyFlowDenied, match="unbounded")
```

### P2-T10: 原四個安全節點仍可執行 [REG]

```
test_original_safe_nodes_still_work

Given: flow 定義只含 query_intake + retrieval_planner + evidence_verification + answer_composer
When:  invoke_pinned_legacy_flow
Then:  全部成功執行（向下相容）
Verify: assert result.status == "completed"
```

### P2-T11: Harness event trail 完整 [REG]

```
test_harness_event_trail_includes_flow_result

Given: D3 Agent Run 載入 flow skill
When:  完整 Harness 執行
Then:  event trail 包含 preflight → model_step → policy_gate → load_skill →
       (flow execution) → budget_gate → finalize
Verify: assert event_types == expected_sequence
```

### 退役測試交叉參照

| 被退役測試 | 取代者 |
|-----------|--------|
| `test_pinned_flow_rejects_scripts_before_execution` | **P2-T01**（script 步驟現在通過） |
| `test_pinned_flow_rejects_any_tool_step_regardless_of_risk` | **P2-T02** + **P2-T03**（tool 改為 effective set 檢查） |
| `test_pinned_flow_denials_before_execution[unsafe-node]` | **P2-T04**（非白名單節點現在通過） |

---

## Phase 3: Standalone Flow Invoke 經 Governance Wrapper

**測試檔：`tests/test_flow_harness.py`（新增）**

### P3-T01: flow invoke 產出治理事件 [HP]

```
test_flow_governance_produces_preflight_and_finalize

Given: 合法的 flow skill
       FLOW_GOVERNANCE_ENABLED=true
When:  invoke_flow_with_governance(skill, raw_input, deps, ...)
Then:  result.status == "completed"
       result.governance 包含 preflight entry
       result.governance 包含 finalize entry（workflow_completed event）
Verify: assert result.status == "completed"
        assert "preflight" in result.governance
        assert "workflow_completed" in result.governance["events"]
```

### P3-T02: step budget 超限 → 受控失敗 [EC]

```
test_flow_governance_step_budget_exhausted

Given: flow 定義包含 10 個步驟
       step_budget=3
When:  invoke_flow_with_governance(step_budget=3)
Then:  result.status == "budget_exhausted"
       result.output 為空或包含部分結果
       result.governance 包含 finalize entry（即使失敗也 finalize）
Verify: assert result.status == "budget_exhausted"
```

### P3-T03: timeout → 受控失敗 [EC]

```
test_flow_governance_timeout

Given: flow 定義包含一個耗時 5s 的節點
       timeout_seconds=1.0
When:  invoke_flow_with_governance(timeout_seconds=1.0)
Then:  result.status == "timeout"
       result.governance 包含 finalize entry
Verify: assert result.status == "timeout"
```

### P3-T04: hash 不符 → preflight 失敗 [EC]

```
test_flow_governance_hash_mismatch

Given: skill.sha256 與 skill.definition 的實際 hash 不符
When:  invoke_flow_with_governance
Then:  result.status == "error"
       result.governance 包含 preflight failure
       flow 未執行
Verify: assert result.status == "error"
        assert "hash mismatch" in str(result.governance)
```

### P3-T05: agentic kind 不受影響 [REG]

```
test_agentic_invoke_unchanged

Given: agentic skill
When:  POST /skills/{name}/invoke
Then:  走 agent_skill_graph 路徑（不走 governance wrapper）
       回傳格式不變
Verify: assert response == {"skill": name, "output": expected}
```

### P3-T06: 回傳格式不變 [REG]

```
test_flow_invoke_response_shape_unchanged

Given: flow skill, FLOW_GOVERNANCE_ENABLED=true
When:  POST /skills/{name}/invoke
Then:  回傳 {"skill": name, "output": {...}}
       output 不含 __-prefixed keys
       output 不含 governance trace
Verify: assert set(response.keys()) == {"skill", "output"}
        assert not any(k.startswith("__") for k in response["output"])
```

### P3-T07: feature flag off → 原路徑 [REG]

```
test_flow_governance_flag_off_uses_legacy_path

Given: flow skill, FLOW_GOVERNANCE_ENABLED=false
When:  POST /skills/{name}/invoke
Then:  走原路徑（DC-4 的 graph.ainvoke）
       回傳格式不變
Verify: 與 flag on 的回傳內容等價
```

### P3-T08: 共用函式被兩端使用 [HP]

```
test_shared_prepare_and_execute_used_by_both_paths

Given: 同一個 flow skill
When:  透過 Harness (invoke_pinned_legacy_flow) 執行
       透過 standalone (invoke_flow_with_governance) 執行
Then:  兩者都呼叫 _prepare_flow_state + _execute_compiled_flow
       兩者的 flow 執行部分行為一致（相同 output）
Verify: assert harness_output == standalone_output
```

**整合測試：`tests/test_skills_api.py`（擴充）**

### P3-T09: API 整合 — flow invoke 含治理 [HP]

```
test_skill_invoke_flow_with_governance_integration

Given: 已建立的 flow skill, FLOW_GOVERNANCE_ENABLED=true
When:  POST /skills/{name}/invoke {"input": {...}}
Then:  200 OK, {"skill": name, "output": {...}}
       workflow 內部有 preflight + budget + finalize 事件
Verify: HTTP 200, response shape correct
```

### 退役測試交叉參照

| 被退役測試 | 取代者 |
|-----------|--------|
| `test_invoke_timeout_returns_504` | **P3-T03**（timeout 由 governance wrapper 管控） |
| `test_invoke_unexpected_exception_returns_500` | **P3-T01** + **P3-T04**（exception 由 governance wrapper 處理） |

---

## Phase 4: 清理與統一

**影響多個測試檔**

### P4-T01: 無 legacy 前綴事件名 [HP]

```
test_no_legacy_prefix_in_event_types

Given: 完整 Harness 執行（D3 Agent Run + flow skill）
When:  收集所有 event_type
Then:  無任何 event_type 包含 "legacy" 子字串
       包含 "workflow_completed"
Verify: assert not any("legacy" in e for e in event_types)
        assert "workflow_completed" in event_types
```

### P4-T02: legacy_flow.py 不存在 [HP]

```
test_legacy_flow_module_removed

Verify: assert not Path("workflow/app/runtime/legacy_flow.py").exists()
```

### P4-T03: flow_harness 是唯一入口 [HP]

```
test_flow_harness_is_single_entry_point

Given: grep "from.*legacy_flow" across workflow/
Then:  無任何 import
       graph.py imports from .flow_harness
       main.py imports from .runtime.flow_harness
Verify: grep 結果為空
```

### P4-T04: step_analysis 統一 [HP]

```
test_step_analysis_matches_recursion_limit

Given: 任意 flow skill
When:  analysis = compiler.step_analysis(skill)
Then:  analysis.recursion_limit == compiler.recursion_limit(skill)
       analysis.step_bound > 0
       analysis.tool_call_bound >= 0
Verify: assert analysis.recursion_limit == compiler.recursion_limit(skill)
```

### P4-T05: Harness 內與 standalone 共用邏輯 [REG]

```
test_harness_and_standalone_share_execution_logic

Given: 同一 flow skill
When:  透過 Harness 執行
       透過 standalone invoke 執行
Then:  相同 input → 相同 output
       兩者 trace 格式一致（都有 workflow_completed）
Verify: assert harness_output == standalone_output
```

### P4-T06: Backend 事件允許清單已更新 [HP]

```
test_backend_event_allowlist_includes_workflow_completed

Verify: grep "workflow_completed" in:
        - backend/.../AgentRunRepository.cs
        - backend/.../InMemoryAgentRunRepository.cs
        結果非空
```

### P4-T07: 全 pytest 綠燈 [REG]

```
test_full_suite_green

When:  uv run pytest
Then:  exit code 0
       無 failure 或 error
Verify: pytest return code
```

### 需更新的既有測試（非退役）

| 測試 | 更新內容 |
|------|---------|
| `test_graph_loads_exact_flow_as_opaque_same_run_step` | 事件名 `legacy_flow_completed` → `workflow_completed` |
| `test_load_skill_preserves_golden_wire_for_both_kinds_and_scope_switch` | 同上 |

### 退役測試交叉參照

| 被退役測試 | 取代者 |
|-----------|--------|
| `test_pinned_flow_executes_and_returns_only_public_state` | **P3-T06** + **P4-T05**（flow_harness 的 happy path） |
| `test_legacy_flow_output_excludes_authority_and_audit_keys` | **P3-T06**（output 過濾由統一模組測試） |
| `test_pinned_flow_denials_during_execution` | **P1-T02** + **P2-T06** + **P3-T03**（timeout/fatal 由 callback + governance 管控） |

---

## 跨 Phase 回歸測試

### REG-01: Node Shell 核心治理不受影響 [REG]

```
驗證方法: uv run pytest tests/test_engine_node_shell.py — 全綠
覆蓋: writes 契約、immutable keys、fatal 短路、reads 過濾、authority keys
```

### REG-02: Compiler 行為不受影響 [REG]

```
驗證方法: uv run pytest tests/test_engine_compiler.py — 全綠
覆蓋: sequence/branch/loop 編譯、cache、audit 終端節點、recursion_limit
```

### REG-03: Script 步驟治理不受影響 [REG]

```
驗證方法: uv run pytest tests/test_engine_script_steps.py — 全綠
覆蓋: timeout、size limit、immutable keys、AST 深度
```

### REG-04: Agent Skill 路徑不受影響 [REG]

```
驗證方法: D3 Agent Run 載入 agentic skill → 完整 Harness 執行
覆蓋: scope enter/exit、agent loop、tool invoke、budget gate
對應測試: 既有 test_agent_runtime.py 中 agentic 相關測試
```

### REG-05: API 契約不變 [REG]

```
驗證方法: uv run pytest tests/test_skills_api.py — 全綠（扣除退役測試）
覆蓋: API shape、auth、422/404、回傳格式
```

### REG-06: 租戶隔離不受影響 [REG]

```
驗證方法: uv run pytest tests/test_skills_custom.py — 全綠
覆蓋: 租戶隔離、角色 gate、backend 容錯
```

---

## 退役清單完整對照

| # | 退役測試 | Phase | 取代測試 |
|---|---------|-------|---------|
| 1 | `test_pinned_flow_rejects_scripts_before_execution` | 2 | P2-T01 |
| 2 | `test_pinned_flow_rejects_any_tool_step_regardless_of_risk` | 2 | P2-T02 + P2-T03 |
| 3 | `test_pinned_flow_denials_before_execution[unsafe-node]` | 2 | P2-T04 |
| 4 | `test_invoke_timeout_returns_504` | 3 | P3-T03 |
| 5 | `test_invoke_unexpected_exception_returns_500` | 3 | P3-T01 + P3-T04 |
| 6 | `test_pinned_flow_executes_and_returns_only_public_state` | 4 | P3-T06 + P4-T05 |
| 7 | `test_legacy_flow_output_excludes_authority_and_audit_keys` | 4 | P3-T06 |
| 8 | `test_pinned_flow_denials_during_execution` | 4 | P1-T02 + P2-T06 + P3-T03 |
