# Agent 平台重整 — 遷移與發布

> **Superseded retirement policy (2026-08-02):** R6 的共存、流量門檻與 rollback-window 退場策略已由 [Architecture Hard Reset](../architecture-hard-reset/01-plan.md) 取代；原文保留為歷史理由。現行程式仍遵守本文件，直到 hard-reset 對應 phase 實作並通過 gate。

> 狀態：D1–D7 已完成產品交付與 D7 hybrid cross-service e2e 驗證。本檔仍定義 coexistence、feature flag、觀測、rollback 與 legacy 退場門檻；R6 的 legacy 移除尚未執行。

## 1. 遷移目標

在不破壞既有聊天、Skill CRUD/invoke、package round-trip、SSE、AG-UI、memory 與 persistence 的前提下，逐步導入 Agent Profile、Root Orchestrator、versioned Execution Harness Workflows、視覺 Designer 與多 Agent Runtime。

新表、新 API 與新 runtime 全部先採 additive 方式；沒有通過 release gate 前，不移除舊路徑。

遷移期間也不得把 Workflow 變成新的業務內容容器：Agent Prompt、Business Rules、Skill packages 與 Harness Graph IR 維持獨立 revision。Legacy flow YAML 若含特定商業流程，只能留在 `legacy-flow` adapter，不得直接升格成通用 Agent-Runtime Harness template。

## 2. 共存模型

| 現有能力 | 遷移期間定位 | 長期方向 |
| --- | --- | --- |
| `ChatAssistant` / `OperationsAssistant` | Default/legacy chat transport 與 brain | 成為 thin transport/session adapter，共用 Root Orchestrator Workflow |
| `SkillRoutingAgent` | 舊聊天的 deterministic routing | 由 Root Orchestrator 的 Agent selection/dispatch 取代 |
| `agent_skill_runner` | agentic Skill explicit invoke 相容 | 保留 legacy adapter；Skill activation 不建立 child Agent |
| flow YAML compiler/Harness | Legacy Flow executor | 內部自動化與相容，不再是主要使用者作者模型 |
| Agent Skill package | 新舊路徑共用能力資產 | 新 Agent 的標準 Skill 來源 |

## 3. 資料遷移

### 3.1 Additive schema

- 新增 `agent*`、`orchestrator*`、`workflow*`、binding 與 root/child `agent_run*`。
- 不修改或搬移既有 `skill`、`skill_revision` 與 package bytes。
- Agent 與 Skill 使用不同 namespace；不以 Skill name 推導 Agent。

### 3.2 Default Orchestrator 與 Workflows

每 tenant 從 system-owned template 建立：

- 只有已明確具備 published Worker pool 與 pinned Verifier 的 tenant，才建立 Default Orchestrator + published Orchestrator Workflow rev1；其對話表現以目前 ChatAssistant 為相容基線。
- Default Agent-Runtime Workflow rev1，供 user-created Worker/Verifier Agents 使用。此 template 於 P0 即種子（見 01-plan P0），非 P3 才建立。
- 初始工具與能力只包含現有聊天已能使用的集合。
- `allowedTools`、`knowledgeSources` 與 bindings 全部寫入明確集合；禁止用 null/省略表示 unrestricted。
- 不自動把租戶所有 Skills 綁入 Default Orchestrator；Skill 只能由 tenant ADMIN 明確綁定到 Worker/Verifier Agent。完成 Agent bindings 前，既有 Skill routing 留在 legacy path。
- 記錄 source=`system-migrated`，後續修改仍走正常 draft/publish/revision。

若無法安全取得某租戶已明確發布的 Agent pool、Verifier 或 bindings，該租戶繼續走 legacy chat，不推導、不自動建立含糊的 Default Agent／Orchestrator。

### 3.3 Skill compatibility overlay

不重寫 Agent Skill package bytes。另建立可計算或持久化的 compatibility 狀態：

```text
format_valid
runtime_compatible
binding_eligible
issues[]
```

- `format_valid`：Agent Skills frontmatter、archive 與安全限制通過。
- `runtime_compatible`：所需工具、環境與 scripts 能力可由目前 host 提供。
- `binding_eligible`：符合 tenant policy、角色與啟用狀態，可綁定發布。

`allowed-tools` 表示能力需求與上限，不是授權。

## 4. Feature flags

建議至少有：

- `AGENT_BUILDER_ENABLED`
- `AGENT_TEST_RUN_ENABLED`
- `AGENT_CHAT_ENABLED`
- `AGENT_WRITE_TOOLS_ENABLED`
- `WORKFLOW_DESIGNER_ENABLED`
- `MULTI_AGENT_DISPATCH_ENABLED`
- `WORKFLOW_REVISION_CANARY`

正式環境以 tenant allowlist/canary 控制，不以 client 可偽造 header 或 query string 決定。

每個 flag 關閉時：

- 新資料保持不刪除。
- Agent Builder 可依需求轉唯讀。
- chat 立即回 legacy/default path。
- 已開始的 write action 不可因切 flag 而重複執行；依 idempotency/run status 完成或取消。

## 5. Rollout 階段

D1–D7 交付切分定義見 01-plan §6.1;每個 D 里程碑結束時系統皆為可部署、flag 外零變化的可用狀態。

### R0：Shadow validation (對應 D1–D2)

- Agent/Orchestrator/Workflow CRUD、publish、rules 與 snapshots 上線，但不接正式 chat。
- Designer 先以 published Workflow 唯讀 visualization 與 simulated trace overlay 上線；真實 root/child trace 留到 R2/P4。
- 用 production-like fixtures 驗證 tenant、revision、Graph、binding 與 policy。

### R1：SYSTEM_ADMIN Designer 與 test console (對應 D3–D4)

- 只允許 `workflow.manage` 對 Orchestrator/Workflow draft validate/simulate/publish；tenant ADMIN 不自動取得權限。
- Agent ADMIN 可對 draft/published Agent 測試。
- 工具限 read-only；不執行外部 scripts。
- 收集 node validation、Context loop、Skill load、token、latency 與 clarification 指標。

### R2：多 Agent shadow/canary run (對應 D5)

- 少數 tenant 以獨立 API 執行 Root Workflow，啟用 bounded Worker dispatch + Verifier，不接正式 chat。
- 比較 task decomposition、child results、verifier/aggregation 與 legacy final answer。
- shadow 對照不得執行寫入型動作。

### R3：Tenant canary chat (對應 D6)

- 少數 tenant/user 顯式使用 Orchestrator。
- 未選 Orchestrator 繼續 legacy。
- 同步比較 legacy/new 的成功率、成本與延遲，不將同一使用者 turn 同時執行兩套有副作用 runtime。

### R4：Default Orchestrator (對應 D6)

- canary 通過後，已完成安全遷移的 tenant 對未傳 `orchestratorId` 的 client 解析到 Default Orchestrator；其他 tenant 保持 legacy。
- 保留快速回切 legacy flag。
- Chat 與 AG-UI 必須同時通過 shared-brain gate，不能只遷其中一條後宣稱完成。

### R5：Write tools (對應 D7)

- 完成人工確認、idempotency、resume、取消與稽核後才開。
- 先按 tool capability allowlist，再擴 tenant。

### R6：Legacy deprecation (對應 D7)

- 觀測到 legacy 使用量低於門檻且新 runtime 達標後，停止一般使用者建立 flow YAML。
- Advanced/legacy editor 改唯讀前提供匯出與人工遷移指引。
- compiler/Harness 是否移除另立計畫；本案不直接刪除。

## 6. 觀測指標

至少依 tenant、Orchestrator、Workflow revision、root/child run、Agent/Skill revision 統計：

- task success / user correction / escalation rate
- clarification rate 與平均補問輪數
- Context acquisition rounds、重複查詢與來源命中率
- Skill discovery/load/use rate
- tool calls、deny、approval、timeout、cancel
- token、成本、首 token 與總延遲
- rule hit、unknown facts、衝突與 fail-closed
- legacy fallback rate
- 每 node latency/error、fan-out/concurrency、child success/timeout/cancel
- verifier reject/repair rate、repair rounds、aggregation accepted/partial rate

Trace/UI 必須遮罩 secrets、internal token、未授權 Context 與敏感工具結果。

## 7. Release blockers

任一項存在即不可擴大 rollout：

- 跨 tenant Workflow、Agent、Skill、checkpoint、child Context 或 memory 洩漏。
- 未知 node、非法 edge、無界 cycle、缺必要 stage 或不相容 node version 可發布。
- Graph IR 可內嵌 Agent Prompt、Business Rule、Skill instruction 或 tenant-specific business node，導致 Harness 與業務內容重新耦合。
- Prompt/Skill 能繞過有效工具交集。
- Root/child run 漂移到未固定 Workflow、Agent 或 Skill revision。
- delegation 提升權限、Worker 自由遞迴委派或 worker output 被當成 trusted instruction。
- child 取消後仍運作，或平行 writes 產生重複／衝突副作用。
- Context/plan/tool loop 無硬上限或取消後仍有背景動作。
- write tool retry 造成重複副作用。
- Rule `unknown` 被靜默當 false，造成權限或金額政策漏判。
- Chat SSE、AG-UI、401 logout、history/memory isolation 回歸。
- Chat 與 AG-UI 仍由不同 brain 決策。

## 8. Rollback

- 第一選擇：關閉 tenant 的 `AGENT_CHAT_ENABLED`，回 legacy chat。
- Agent/Orchestrator/Workflow/Skill 新資料不刪除、不倒回 schema。
- 正在 `waiting_input`／`waiting_approval` 的 run 標記 suspended；不自動改用 legacy 繼續。
- Workflow/Agent rollback 都以新 revision 表達；active runs 仍使用原 snapshot，不修改歷史。
- 若 package/runtime compatibility 有問題，停用 binding eligibility，不改寫原始 package。

記憶 rollback 不合併 namespace：Root 使用 `{tenant}:{user}:{orchestratorId}:{conversationId}` session、Worker 使用 `{tenant}:{user}:{agentId}` memory；切回 legacy 時只讀 legacy namespace。

## 9. 文件同步

每一 phase 交付後：

- 更新 `plans/README.md` 狀態與 code authority。
- 架構跨服務契約更新 root `AGENTS.md`。
- 單區 layout/API/env/gotcha 更新對應 area `AGENTS.md`。
- README 只在使用者可操作能力實際交付後更新。
- 測試數量以實際 test runner 結果更新，不從計畫推測。

## 10. Legacy 退場清單

降級不等於刪除。下表是本計畫的唯一移除清單：每一列都有觸發條件與時點；觸發指標優先採用 § 6 的 `legacy fallback rate`。凡表中未列的既有程式碼一律視為保留。測試資產退場規則：保護 legacy 路徑的測試在該路徑刪除的同一變更中一併刪除，期間視為迴歸保護資產；文件中的測試數量以實際 runner 結果同步。

| 項目 | 位置 | 命運 | 觸發條件與時點 |
| --- | --- | --- | --- |
| SkillRoutingAgent(含 BuildToolsAsync/SkillCatalogToTools/RouteAsync/MatchTool 建構鏈) | `platform/src/Platform.Service/SkillRoutingAgent.cs` | 刪除 | P5 完成且全 tenant 遷移、legacy fallback rate 低於門檻(R4 gate)後整批移除 |
| Chat routing 迴歸測試(ChatSkillRoutingTests、ChatBehaviorBaselineTests 等) | `platform/tests/` | 隨 SkillRoutingAgent 同批刪除 | 同上；移除前是 legacy 路徑的迴歸保護 |
| ChatAssistant/OperationsAssistant 舊推理路徑(platform 端 ChatClientAgent brain) | `platform/src/Platform.Web/Program.cs` | 改造為 transport adapter，舊 brain 與第 1 列同批移除 | P5 起 side-by-side，R4 gate 後收斂 |
| ChatMemoryKeyDerivation | `platform/src/Platform.Service/` | 擴充為雙 key 規則(legacy 兩段 + orchestrator 四段)，legacy namespace 退場後收斂回單一規則 | R4 gate 後 |
| AguiWireDedupAgent、JwtTenantIsolationKeyProvider、ChatTurnRecorder、ChatContextProvider | `platform/src/Platform.Service/` | 保留重用(transport/session/memory 外框)，不刪 | — |
| flow YAML 作者 UI：SimpleSkillEditor、YamlEditor 與 Advanced flow editor | `frontend/src/components/` | 移入 legacy/管理者區→停建→唯讀 | P5 前後降級；R6 條件成立後移除。`NodeParamsTab` 不在此列：它是 Harness Configuration Set 的平級設定分頁，非 flow 作者 UI；AgentSkillEditor 亦不隨此列退場。 |
| flow 模板 | `workflow/app/skills/template-*.yaml` | 隨第 6 列同批處理 | R6 |
| agent_skill_runner | `workflow/app/nodes/agent_skill_runner.py` | 保留為 explicit legacy invoke executor；R6 盤點時重新決策(吸收或移除)，不再無限期擱置 | R6 |
| current-only package endpoint(GET /api/skills/{name}/package) | `backend/src/Backend.Api/Skills/SkillController.cs` | 保留供 legacy current invoke；由 revision-aware execution artifact endpoint 全面取代後移除 | R6 |
| legacy Business Workflow compiler / Node Shell | `workflow/app/engine/` | 與新 Graph IR compiler 並存；Node Shell 是每節點治理殼，Harness 是 `runtime/graph.py` 固定骨架；移除另立計畫(維持 R6 原則) | R6 之後 |
| TraceView vs Designer trace overlay | `frontend/src/components/TraceView.tsx` | 收斂：TraceView 保留 Agent Skill 與 Business Workflow 的統一 invoke trace；orchestrator root/child trace 一律用 Designer overlay，不得出現第三套 | P4 |
