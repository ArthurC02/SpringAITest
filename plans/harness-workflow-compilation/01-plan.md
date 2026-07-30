# Harness 包覆 Business Workflow（動態編譯）

> 狀態：Planning（2026-07-30）

## 1. 現狀分析

系統目前有三條 Business Workflow / Agent Skill 執行路徑，Harness 包覆程度各異：

### 路徑 A — Harness 內的 Agent Skill（kind=agentic）

```
D3 Agent Run → Harness graph.py
  → preflight → model_step → policy_gate → load_skill
    → _enter_skill_scope（掛入 scope）
    → model_step ⟲ invoke_tool ⟲ budget_gate（agent loop）
  → validate_output → finalize
```

**完整包覆。** Agent Skill 被 Harness 的 preflight、policy\_gate、budget\_gate、finalize 完整治理。model\_step 每輪都經 policy 裁定，工具呼叫受 tool\_boundary 管制，token/step/tool budget 持續扣減。

### 路徑 B — Harness 內的 Business Workflow（kind=flow，半包覆）

```
D3 Agent Run → Harness graph.py
  → preflight → model_step → policy_gate → load_skill
    → _invoke_business_workflow
      → invoke_pinned_legacy_flow（legacy_flow.py）
        → SAFE_LEGACY_NODES 白名單檢查
        → compiler.compile → graph.ainvoke（子圖，只有 Node Shell）
      → 結果回寫 Harness state
  → budget_gate → model_step（繼續 agent loop）
```

**半包覆。** flow 被 Harness 視為一次性子任務：有 preflight 和 budget 扣減，但 flow 的**內部**節點只受 Node Shell 治理，不受 Harness 的 policy\_gate。更嚴重的限制是 `SAFE_LEGACY_NODES` 白名單：只允許 `query_intake`、`retrieval_planner`、`evidence_verification`、`answer_composer` 四個確定性節點，script 和 tool 步驟一律被拒。這意味著絕大多數 Business Workflow 無法在 Harness 路徑內執行。

### 路徑 C — 直接 `/skills/{name}/invoke`（完全繞過 Harness）

```
POST /skills/{name}/invoke → main.py::invoke_skill
  → custom.load → compiler.compile → graph.ainvoke
    → Node Shell（node_shell.py）包覆每個節點
    → 強制 audit 終端節點
```

**無 Harness。** 由 FastAPI endpoint 直接編譯並執行，只有 Node Shell 層級治理（fatal 短路、trace、immutable keys、writes 契約）。沒有 policy\_gate、budget\_gate、preflight、validate\_output、finalize 等 Harness 節點。角色檢查和 input 驗證由 endpoint handler 自己做，不走統一管線。

### 問題總結

| 治理能力 | 路徑 A (Agent Skill) | 路徑 B (Flow in Harness) | 路徑 C (Direct Invoke) |
|---|---|---|---|
| Preflight (snapshot/hash 驗證) | ✅ | ✅ | ❌ |
| Policy gate (Business Rules 裁定) | ✅ | ❌ (子圖內部不經 policy) | ❌ |
| Budget gate (token/step/tool 扣減) | ✅ 持續 | ⚠️ 事後批次扣減 | ❌ |
| Tool boundary (風險分級 + 核准閘) | ✅ | ❌ (tool 步驟被禁) | ❌ |
| Output contract validation | ✅ | ❌ | ❌ |
| Finalize (scope 清理 + terminal event) | ✅ | ⚠️ 外層有 | ❌ |
| Node Shell (trace/fatal/immutable) | ✅ | ✅ | ✅ |
| Audit 終端節點 | ✅ (scope exit) | ✅ (子圖末端) | ✅ (compiler 強制) |

核心矛盾：Business Workflow 在路徑 B 被過度限制（只能跑四個節點），在路徑 C 又完全缺乏 Harness 治理。沒有一條路徑能讓 Business Workflow 既保有完整語義（script、tool、所有節點類型）又受到 Harness 統一治理。

---

## 2. 目標架構

### 設計原則

1. **所有 workflow 執行都經 Harness 節點鏈** — 消除「繞過」路徑
2. **compiler 產出的 graph 嵌入為 Harness 的子圖** — 不改變 compiler 本身，在 Harness 層注入治理
3. **保持 Node Shell 作為節點級治理** — Harness 是外層，Node Shell 是內層，兩層正交
4. **向下相容** — 路徑 A（Agent Skill in Harness）不受影響

### 目標拓撲

```
Harness graph (graph.py)
  ├─ preflight
  ├─ model_step ──────────────────────────────┐
  ├─ policy_gate                              │ agent loop
  │   ├─ load_skill (kind=agentic) → scope    │ (已有行為，不變)
  │   ├─ load_skill (kind=flow)               │
  │   │   → compile_and_embed_workflow ────────┤
  │   │     ├─ workflow_preflight              │ ← 新：驗證 flow 定義 hash
  │   │     ├─ [compiled flow nodes]           │ ← 每節點仍有 Node Shell
  │   │     │   每步增加 policy_check +        │ ← 新：Node Shell 內注入
  │   │     │   budget_charge                  │     per-step budget 扣減
  │   │     ├─ workflow_audit                  │ ← 已有：compiler 強制
  │   │     └─ workflow_exit                   │ ← 新：結果回寫 Harness state
  │   ├─ invoke_tool                          │
  │   ├─ read_resource                        │
  │   └─ ...                                  │
  ├─ budget_gate ─────────────────────────────┘
  ├─ validate_output
  └─ finalize
```

### 關鍵決策

**D1: 嵌入策略 — 子圖 vs. 展平**

選擇 **子圖（sub-invocation）**：在 `_load_skill`（或新的 `_run_workflow`）節點內編譯並以 `graph.ainvoke` 執行 flow，但移除 `SAFE_LEGACY_NODES` 限制，改為在每步經 Harness budget/policy 回報。

理由：展平（把 compiler 產出的所有節點直接加入 Harness StateGraph）需要在 Harness compile time 知道所有可能的 flow 定義，與動態載入矛盾，且 branch/loop 的引擎內部鍵會污染 Harness state。

**D2: Policy 注入方式 — per-step callback vs. 批次**

選擇 **per-step budget callback**：在 Node Shell（`harnessed()`）增加可選的 `budget_callback` 參數，每步完成後呼叫，由外層 Harness 決定是否短路。Policy 裁定仍在外層 Harness 的 `policy_gate` 完成（flow 作為一個整體動作被裁定），不在每個 flow 內部節點重複裁定。

理由：Business Workflow 是宣告式確定性流程，不需要 per-step 的 LLM policy 裁定；但需要 per-step 的 budget 扣減以防止 loop 失控。

**D3: `/skills/{name}/invoke` 處理**

保留 endpoint，但 flow kind 的執行改為透過 **lightweight Harness wrapper**：一個精簡版 Harness 提供 preflight、budget、finalize 治理，無 agent loop（flow 不需要 LLM model\_step）。

理由：`/skills/{name}/invoke` 是已發布 API 契約，不能拿掉；但可以讓它的內部從「直接 `graph.ainvoke`」改為「經 Harness wrapper 執行」。

---

## 3. 實作步驟

### Phase 1: Node Shell budget callback（基礎設施）

**目標：** Node Shell 支援 per-step 的 budget 回報機制，為 Phase 2/3 的治理注入做準備。

**修改範圍：**
- `workflow/app/engine/node_shell.py` — `harnessed()` 增加可選 `on_step_complete` callback
- `workflow/app/engine/compiler.py` — **不改 `_Builder` 或 `compile()`**；callback 不進入編譯產物

**Callback 注入機制（與 compiler cache 正交）：**

compiler cache（`compiler.py:522-551`）以 `(name, revision, sha256, id(deps))` 為鍵快取 `CompiledStateGraph`。budget callback 是 runtime-specific 的（每次 invoke 的 Harness state 不同），若嵌進 deps 或 `_Builder` 會導致每次呼叫都 cache miss，等同廢掉快取。

解法：使用 **`contextvars.ContextVar`** 在執行期注入 callback，與編譯期解耦：
1. `node_shell.py` 宣告 `_budget_callback_var: ContextVar[Callable | None]`（module-level，default `None`）
2. `harnessed()` 包裝函式在 node 執行完畢後讀取 `_budget_callback_var.get()`，非 None 則呼叫
3. 呼叫端（`legacy_flow.py` 的 `invoke_pinned_legacy_flow`、Phase 3 的 `flow_harness.py`）在 `graph.ainvoke` 前以 `copy_context().run()` 設定 ContextVar，invoke 結束後自動清理——不影響其他 coroutine
4. compiled graph 本身不含 callback 參照，快取完全不受影響

**完成定義：**
- [ ] `node_shell.py` 新增 `_budget_callback_var: ContextVar` 和 `set_budget_callback()` 便利函式
- [ ] `harnessed()` 在節點完成後讀取 ContextVar，若非 None 則呼叫（節點名、耗時 ms）
- [ ] callback raise `BudgetExhausted` 時，Node Shell 將其轉為 `fatal_error`（走既有 fatal 路徑）
- [ ] 無 callback（ContextVar 未設定）時行為不變（完整向下相容）
- [ ] compiler cache 測試：同一 compiled graph 在不同 callback 下重複使用，驗證 cache hit
- [ ] 現有 Node Shell 測試全綠
- [ ] 無死碼退役義務（Phase 1 只新增基礎設施）

### Phase 2: 移除 legacy\_flow 白名單限制（Harness 內 flow 全能力）

**目標：** Harness 內的 `_load_skill` 能執行含 script、tool、所有節點類型的 Business Workflow，取代 `SAFE_LEGACY_NODES` 白名單。

**修改範圍：**
- `workflow/app/runtime/legacy_flow.py` — 重構 `invoke_pinned_legacy_flow`：
  - 移除 `SAFE_LEGACY_NODES` 白名單
  - 移除 `_validate_steps` 中對 script/tool 的拒絕
  - 保留 bounded timeout、recursion cap、input validation
  - 增加：透過 Phase 1 的 ContextVar 機制在 `graph.ainvoke` 前注入 budget callback
  - 增加：tool 步驟的 tool 白名單改為 snapshot 的 effective tool set（與 Agent Skill 等級一致）
- `workflow/app/runtime/graph.py` — `_invoke_business_workflow` 傳入 budget callback

**完成定義：**
- [ ] Harness 內的 flow 可包含 script 步驟（受 `ISOLATED_SKILL_SCRIPTS_ENABLED` + Node Shell 治理）
- [ ] Harness 內的 flow 可包含 tool 步驟（受 snapshot effective tool set 限制）
- [ ] Harness 內的 flow 可包含所有已註冊節點（不限 `SAFE_LEGACY_NODES`）
- [ ] 每步完成後向 Harness budget 回報 step\_count 和 tool\_rounds
- [ ] budget 耗盡時 flow 以 fatal\_error 終止，Harness 正常 finalize
- [ ] `LegacyFlowDenied` 只在 artifact 本身非法時拋出（非 flow kind、未 bound 的 loop 等），不因節點類型拒絕
- [ ] 退役死碼 DC-1（`SAFE_LEGACY_NODES`）、DC-2（`_validate_steps` 拒絕邏輯）、DC-3（`scripts_present` early reject）
- [ ] 完成 reuse 整合 RU-2（`_RuntimeDeps` 改為顯式白名單）
- [ ] 退役 3 個過時測試（`test_pinned_flow_rejects_scripts_before_execution`、`test_pinned_flow_rejects_any_tool_step_regardless_of_risk`、`test_pinned_flow_denials_before_execution[unsafe-node]`）

### Phase 3: `/skills/{name}/invoke` 經 Harness wrapper 執行

**目標：** 消除路徑 C 的「無 Harness」問題，flow 的直接 invoke 也有統一治理。

**修改範圍：**
- `workflow/app/runtime/` — 新增 `flow_harness.py`：提供 `invoke_flow_with_governance()` 函式
  - 不含 model\_step / agent loop（flow 是確定性，不需 LLM）
  - 包含：preflight（定義 hash 驗證）、budget gate（step + timeout）、finalize（terminal event）
  - 內部使用 `compiler.compile()` + Phase 1 的 budget callback
- `workflow/app/main.py` — `invoke_skill` 對 flow kind 改呼叫 `invoke_flow_with_governance()`
- `workflow/app/skills/custom.py` — `_compile_loaded` 不需改動（只負責編譯，不負責執行）

**完成定義：**
- [ ] `POST /skills/{name}/invoke` 對 flow kind 產出含 preflight/budget/finalize 的 audit trail
- [ ] flow 的 step 和 timeout budget 被強制執行（與 Phase 2 的 Harness 內行為一致）
- [ ] agentic kind 的 invoke 不受影響（繼續走 `agent_skill_graph.py` 路徑）
- [ ] 回傳格式不變：`{skill, output}`
- [ ] 退役死碼 DC-4（`main.py` flow kind 直接 `graph.ainvoke` 路徑）
- [ ] 完成 reuse 整合 RU-3（共用 `_prepare_flow_state()` + `_execute_compiled_flow()`）
- [ ] 退役 2 個過時測試（`test_invoke_timeout_returns_504`、`test_invoke_unexpected_exception_returns_500`）

### Phase 4: 清理與統一

**目標：** 移除過渡程式碼，統一審計事件格式。

**修改範圍：**
- `workflow/app/runtime/legacy_flow.py` — 可改名或合併至 `flow_harness.py`（語意更清楚）
- `workflow/app/runtime/graph.py` — `_invoke_business_workflow` 改用 `flow_harness.py` 的共用函式
- 審計事件：從 `legacy_flow_completed` 改為 `workflow_completed`
- 文件更新：AGENTS.md 更新三條路徑的描述

**完成定義：**
- [ ] 不存在 `legacy` 前綴的函式名或事件名（除向下相容別名）
- [ ] Harness 內和直接 invoke 共用同一套 flow 執行邏輯
- [ ] AGENTS.md（root + workflow）更新反映新架構
- [ ] 退役 4 項死碼 + 合併 4 項 reuse 整合 + 移除 8 個過時測試（見 §3.5 清單）

---

### §3.5 Dead Code / Reuse / Test 退役清單

計畫各 Phase 完成後會產生死碼、重複邏輯、與過時測試。此清單是 Phase 4 完成定義的一部分，每個 Phase 的完成定義也包含對應的退役義務。

#### Dead Code（計畫完成後變死碼）

| # | 項目 | 檔案:行 | 退役時機 | 說明 |
|---|------|---------|---------|------|
| DC-1 | `SAFE_LEGACY_NODES` 常量 | `legacy_flow.py:37-44` | Phase 2 | 白名單移除後整個 `frozenset` 與依賴它的 `:186` 分支判斷全部死碼 |
| DC-2 | `_validate_steps()` 中 script/tool/node 安全性拒絕邏輯 | `legacy_flow.py:169-208`（body 內 `:180-191` 的 `kind == "script"`、`kind == "tool"`、`spec.name not in SAFE_LEGACY_NODES` 分支） | Phase 2 | 驗證邏輯被 Harness 治理取代：script 受 `ISOLATED_SKILL_SCRIPTS_ENABLED`、tool 受 effective tool set、節點受 Node Shell；`_validate_steps` 本身可精簡為只驗 loop bounded 與 step 合法性 |
| DC-3 | `scripts_present` early reject | `legacy_flow.py:91-92` | Phase 2 | Phase 2 允許 script 步驟後，`scripts_present` 不再是拒絕理由 |
| DC-4 | `main.py` flow kind 的直接 `graph.ainvoke()` 路徑 | `main.py:558-564`（`else` 分支：flow kind 直接 `graph.ainvoke` 無 Harness 治理） | Phase 3 | 改走 `invoke_flow_with_governance()` 後，此路徑變死碼 |

#### Reuse 整合

| # | 項目 | 現況 | 目標 | 退役時機 |
|---|------|------|------|---------|
| RU-1 | step-bound traversal 重複 | `legacy_flow.py:220-266`（`_tool_call_bound` + `_step_bound`）與 `compiler.py` 的 `recursion_limit` 各自走訪 step 結構 | 統一為 compiler 層的 step 分析，legacy\_flow 直接引用 | Phase 4 |
| RU-2 | `_RuntimeDeps` 全代理 | `legacy_flow.py:64-75`（`__getattr__` 無限制代理） | 改為顯式屬性白名單（allowlist delegation），或合併進 flow\_harness 的 deps 組裝 | Phase 2（安全前置作業） |
| RU-3 | flow 執行 setup patterns 重複 | `legacy_flow.py:107-135`（state 組裝 + timeout + compile + ainvoke）與 Phase 3 的 `flow_harness.py` 會重複相同模式 | Phase 3 建立共用的 `_prepare_flow_state()` + `_execute_compiled_flow()`，兩處共用 | Phase 3–4 |
| RU-4 | `legacy_flow.py` → `flow_harness.py` 合併 | Phase 4 時 `legacy_flow.py` 整檔語意已被 `flow_harness.py` 覆蓋 | **必做**（不是 "maybe"）：合併為單一模組，消除 `legacy` 概念 | Phase 4 |

#### Test 退役清單

以下測試在對應 Phase 完成後會變得過時、重複或測試不再存在的行為，應在該 Phase 退役：

**Phase 2 退役（移除白名單後）：**

| 檔案 | 測試函數 | 退役原因 |
|------|---------|---------|
| `test_agent_runtime_legacy_flow.py:376` | `test_pinned_flow_rejects_scripts_before_execution` | 測試 `scripts_present` early reject（DC-3）；Phase 2 允許 script 後行為不再存在 |
| `test_agent_runtime_legacy_flow.py:392` | `test_pinned_flow_rejects_any_tool_step_regardless_of_risk` | 測試 `_validate_steps` 無條件拒絕 tool step（DC-2）；Phase 2 改為 effective tool set 管控，此測試被新的「tool 在 effective set 內 → 通過 / 不在 → 拒絕」測試取代 |
| `test_agent_runtime_legacy_flow.py:470`（`ids=["unsafe-node"]` 參數化案例） | `test_pinned_flow_denials_before_execution[unsafe-node]` | 測試 `SAFE_LEGACY_NODES` 白名單拒絕非安全節點（DC-1）；Phase 2 移除白名單後行為不再存在。注意：同函數的其他三個案例（`not-a-flow`、`unbounded-loop`、`input-schema`）仍然有效，不退役 |

**Phase 3 退役（直接 invoke 改走 Harness wrapper 後）：**

| 檔案 | 測試函數 | 退役原因 |
|------|---------|---------|
| `test_skills_api.py:263` | `test_invoke_timeout_returns_504` | 測試 `main.py` 的 `_run_with_timeout` 直接 `ainvoke` 的 timeout 行為（DC-4）；Phase 3 改走 `invoke_flow_with_governance()` 後，timeout 由 governance wrapper 的 budget gate 管控，此測試被 `test_flow_harness.py` 的 timeout 測試取代 |
| `test_skills_api.py:289` | `test_invoke_unexpected_exception_returns_500` | 同上：直接 `ainvoke` 的 exception 路徑被 governance wrapper 接管 |

**Phase 4 退役（legacy 概念消除後）：**

| 檔案 | 測試函數 | 退役原因 |
|------|---------|---------|
| `test_agent_runtime_legacy_flow.py:97` | `test_pinned_flow_executes_and_returns_only_public_state` | 被 `test_flow_harness.py` 的新 happy path 測試取代（合併後同一行為由統一模組測試覆蓋） |
| `test_agent_runtime_legacy_flow.py:445` | `test_legacy_flow_output_excludes_authority_and_audit_keys` | 同上：output 過濾邏輯合併進 `flow_harness.py`，測試隨之遷移 |
| `test_agent_runtime_legacy_flow.py:549` | `test_pinned_flow_denials_during_execution` | timeout / fatal\_error 的處理改由 governance wrapper + budget callback 管控，此測試被 Phase 1–3 的新測試組合取代 |

**不退役的測試（確認留存）：**

以下測試在所有 Phase 完成後仍然有效，因為它們測試的是持續存在的治理行為：

- `test_engine_node_shell.py` — 全部保留：Node Shell 的 writes 契約、immutable keys、fatal 短路、reads 過濾是正交於 Harness 的內層治理，不受本計畫影響
- `test_engine_compiler.py` — 全部保留：compiler 的 sequence/branch/loop/cache/audit 行為不變
- `test_engine_script_steps.py` — 全部保留：script 步驟經 Node Shell 的治理行為（timeout、size limit、immutable keys、AST 深度）不受本計畫影響
- `test_engine_guardrails.py` — 全部保留：AST 深度上限和 runtime authority keys 防護是獨立護欄
- `test_skills_api.py` 的其餘測試 — 保留：驗證 API 形狀、auth、422/404 等契約不變
- `test_skills_custom.py` — 全部保留：自訂 skill 的租戶隔離、角色 gate、backend 容錯等行為不受影響
- `test_agent_runtime_legacy_flow.py:121` `test_graph_loads_exact_flow_as_opaque_same_run_step` — 保留但需更新：事件名從 `legacy_flow_completed` 改為 `workflow_completed`（Phase 4）
- `test_agent_runtime_legacy_flow.py:174` `test_load_skill_preserves_golden_wire_for_both_kinds_and_scope_switch` — 保留但需更新：同上事件名更新

---

## 4. 影響面分析

### Workflow 服務內部

| 模組 | 改動程度 | 說明 |
|---|---|---|
| `runtime/graph.py` | 低 | `_invoke_business_workflow` 傳 callback；Phase 4 改用共用函式 |
| `runtime/legacy_flow.py` | 高 | 移除白名單、增加 callback、Phase 4 可能改名 |
| `engine/node_shell.py` | 低 | 增加可選 callback 參數 |
| `engine/compiler.py` | 無 | callback 透過 ContextVar 注入，不改編譯器或快取 |
| `main.py` | 中 | flow invoke 改呼叫 governance wrapper |
| `skills/custom.py` | 無 | 只負責載入與編譯 |
| `engine/agent_skill_graph.py` | 無 | Agent Skill 路徑不受影響 |
| `runtime/artifacts.py` | 無 | 讀取層不變 |

### 跨服務契約

| 契約 | 受影響 | 說明 |
|---|---|---|
| `/skills/{name}/invoke` 回傳格式 | **否** | `{skill, output}` 不變 |
| `POST /skills/{name}/invoke` 語義 | **微調** | flow 的 output 可能多出 `_governance` 欄位（Phase 3），但在 `public_output` 過濾後不外漏 |
| Harness event schema | **新增** | `workflow_completed` 取代 `legacy_flow_completed`（Phase 4，可保留舊名別名） |
| D3 snapshot 契約 | **否** | snapshot 結構不變，flow artifact 的 pin 格式不變 |
| Platform/Backend 的 proxy | **否** | 上游只看到 `invoke` 的 `{skill, output}` 回傳，不感知內部治理 |
| Skill YAML 格式 | **否** | 定義格式不變，是執行管線的改變 |

### 前端

無影響。前端不直接呼叫 `workflow/` 的 endpoint，所有互動都經 platform 的 proxy。

---

## 5. 風險與降低措施

| 風險 | 嚴重度 | 降低措施 |
|---|---|---|
| Phase 2 移除白名單後，Harness 內 flow 執行不安全的節點 | 高 | Node Shell 已有 fatal 短路 + writes 契約 + immutable keys 防護；script 步驟受 `ISOLATED_SKILL_SCRIPTS_ENABLED` 和 ScriptRunner 治理；tool 步驟受 snapshot effective tool set 限制（與 Agent Skill 等級一致）。Phase 2 不繞過任何現有治理，只放寬 Harness 入口限制。 |
| Phase 1 的 budget callback 引入 flow 內部 await 延遲 | 低 | callback 是純記帳邏輯（累加計數器 + 比較），無 I/O，延遲可忽略。 |
| Phase 3 改變 `/skills/{name}/invoke` 的行為 | 中 | 用 feature flag `FLOW_GOVERNANCE_ENABLED`（default false）控制切換；flag off 時走原路徑。Phase 4 確認穩定後移除 flag。 |
| 子圖 `ainvoke` 的 exception 導致 Harness 未 finalize | 中 | `_invoke_business_workflow` 已有 try/except → `LegacyFlowDenied` → Harness `_failure` → `finalize`。Phase 2 保留此模式。 |
| 與 D5 Root Orchestrator 的交互 | 低 | Root Orchestrator 透過 D3 Agent Run 路徑觸發，Agent Run 已經走 Harness；flow 在 Harness 內的改動對 Root 透明。 |
| Eval runner (`POST /evals/run`) 路徑 | 低 | Eval 走 `compiler.compile(cache=False)` + `graph.ainvoke`（路徑 C）；Phase 3 的 governance wrapper 可選擇不套用在 eval（eval 有自己的 fixture deps 和 timeout）。 |
| Phase 4 事件重命名 `legacy_flow_completed` → `workflow_completed` 需 Backend 同步 | 高 | Backend 的 `AgentRunRepository.cs`（L22）和 `InMemoryAgentRunRepository.cs`（L18）在事件類型允許清單中硬編碼 `legacy_flow_completed`；改名前必須先在 Backend 兩處加入 `workflow_completed` 到允許清單（同一 PR 或先行 PR），否則 workflow 送出的新事件名會被 Backend 拒絕。Phase 4 完成定義增加：Backend 允許清單已更新且包含新舊兩個名稱（過渡期），待確認舊名無殘留 emitter 後再移除舊名。 |
| Phase 2 開放 tool/script 後 `_RuntimeDeps` 代理表面積擴大 | 中 | `_RuntimeDeps`（`legacy_flow.py:64-75`）以 `__getattr__` 無限制地代理底層 deps 的所有屬性（只替換 `audit_repo`）。目前 `SAFE_LEGACY_NODES` 限制只有四個確定性節點，deps 的暴露面有限。Phase 2 移除白名單後，tool 和 script 步驟將透過 `_RuntimeDeps` 存取 `ToolBag`、`ScriptRunnerPort` 等安全敏感物件——需在 Phase 2 實作前 audit `_RuntimeDeps`，確認：(1) 代理不應洩漏 `audit_repo` 替換以外的 deps 原始屬性；(2) 考慮改為顯式屬性白名單（allowlist delegation）而非 `__getattr__` 全代理，以符合最小權限原則。 |

---

## 6. 測試策略

### Phase 1 測試

- 單元測試：`test_node_shell.py`
  - callback 正常回報：驗證 callback 被呼叫且參數正確
  - callback raise `BudgetExhausted`：驗證轉為 `fatal_error` + trace status=error
  - callback=None（預設）：驗證行為與現有完全一致

### Phase 2 測試

- 單元測試：`test_legacy_flow.py`（可能更名 `test_flow_in_harness.py`）
  - 含 script 步驟的 flow 在 Harness 內成功執行
  - 含 tool 步驟的 flow 在 Harness 內成功執行（tool 在 effective set 內）
  - tool 不在 effective set → `LegacyFlowDenied`
  - budget 耗盡 → flow 以 fatal\_error 終止
  - 已有 SAFE\_LEGACY\_NODES 範圍的測試仍然通過（向下相容）
- 整合測試：`test_agent_runtime.py`
  - D3 Agent Run 載入 flow skill → 完整 Harness event trail（含 `workflow_completed`）
  - D3 Agent Run 載入含 script 的 flow skill → 執行成功 + budget 正確扣減

### Phase 3 測試

- 單元測試：`test_flow_harness.py`
  - flow 的直接 invoke 產出 preflight + budget + finalize 事件
  - step budget 超限 → 受控失敗
  - timeout 超限 → 受控失敗
- 整合測試：`test_skill_invoke.py`
  - `POST /skills/{flow_name}/invoke` 回傳含治理 trace 的 output
  - agentic kind 不受影響

### Phase 4 測試

- 事件名稱一致性：所有 event\_type 不含 `legacy` 前綴
- 回歸：全 `uv run pytest` 綠燈

---

## 附錄：現有程式碼參照

| 檔案 | 職責 |
|---|---|
| `workflow/app/runtime/graph.py` | Harness：固定執行骨架 |
| `workflow/app/runtime/legacy_flow.py` | Harness 內 flow 子任務執行（半包覆路徑 B） |
| `workflow/app/engine/compiler.py` | Business Workflow YAML → LangGraph 圖 |
| `workflow/app/engine/node_shell.py` | 節點級治理殼 |
| `workflow/app/engine/agent_skill_graph.py` | Agent Skill 圖工廠（路徑 A） |
| `workflow/app/skills/custom.py` | 租戶自訂 artifact 載入 |
| `workflow/app/main.py:invoke_skill` | `/skills/{name}/invoke` endpoint |
| `workflow/app/workflow_contracts.py` | Harness stage 契約版本 |
