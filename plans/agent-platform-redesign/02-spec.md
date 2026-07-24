# Agent 平台重整 — 產品與系統規格

> 狀態：規劃中。承接 [01-plan.md](01-plan.md)，本檔定義 WHAT 與邊界。

## 1. 名詞

- **Agent**：租戶內可建立、版本化、發布與停用的數位員工設定。
- **Agent Draft**：獨立、可編輯且帶 row version 的工作副本，不可作為正式聊天執行依據。
- **Agent Revision**：發布時從已驗證 draft 建立的不可變設定快照。
- **Published Revision**：目前對正式執行生效的 revision。
- **Root Orchestrator**：系統管理的主協調者；負責執行 published Orchestrator Workflow，不等於任何 Worker Agent。
- **Workflow Draft**：SYSTEM_ADMIN 可編輯的 Graph IR 與 UI metadata 工作副本。
- **Workflow Revision**：驗證／發布後不可變、可編譯為 LangGraph 的 workflow snapshot。
- **Execution Harness**：Workflow 的產品定位；為 Orchestrator/Agent 提供可重用的執行環境、生命週期與治理骨架，而不是保存業務流程內容。
- **Node Type**：server registry 中具版本、typed ports、config schema、風險與執行 adapter 的節點契約。
- **Root Run / Child Run**：一次 Orchestrator 執行與其分派的 Worker/Verifier Agent 執行。
- **Task Assignment**：分解後交給某個 pinned Worker Agent revision 的工作封包。
- **Verification Report**：Verifier Agent 回傳的結構化 verdict、證據、缺口與 repair requests。
- **Agent Skill**：符合 Agent Skills 規格、至少包含 `SKILL.md` 的 package。
- **Legacy Flow Skill**：現有以 flow YAML/Node 編譯的 Skill；保留相容，但不是新產品作者介面。
- **Business Rule**：由確定性 evaluator 執行的條件與動作。
- **Effective Tools**：本次執行真正可用的工具集合。
- **Agent Run**：direct 或 child 執行；使用固定 Agent、Agent-Runtime Workflow 與 Skill revisions。

## 2. Agent 設定

### 2.1 基本欄位

| 欄位 | 必填 | 說明 |
| --- | --- | --- |
| `name` | 是 | 租戶內顯示名稱 |
| `slug` | 是 | 租戶內唯一、穩定 API 識別字 |
| `description` | 是 | 用途與適用情境 |
| `systemPrompt` | 是 | 角色、目標、語氣、一般行為指引 |
| `executionRoles` | 是 | `worker`／`verifier` eligibility；可複選但 verifier run 預設唯讀 |
| `capabilities` | 是 | 供 Orchestrator 做 Agent discovery/selection 的 typed tags |
| `outputContract` | 是 | Worker/Verifier 的結構化輸出 schema |
| `audience` | 是 | 可使用此 Agent 的角色／群組；預設同 tenant 的 USER 與 ADMIN |
| `allowedTools` | 是 | Agent 層工具 allowlist；可為空陣列 |
| `skillBindings` | 是 | 已選 Skill 與 revision policy；可為空陣列 |
| `knowledgeSources` | 是 | 可用知識來源範圍；可為空陣列 |
| `businessRules` | 是 | typed rule AST；可為空 rules |
| `runtimeLimits` | 是 | 工具輪數、Context 輪數、逾時、token/step budget |
| `runtimeWorkflow` | 是 | pinned published `agent-runtime` Workflow revision |
| `enabled` | 是 | 軟停用 |

### 2.2 狀態

```text
Mutable Draft --validate(version N)--> valid result for version N
       └────────publish(version N)───> Immutable Published Revision
                                         └─ newer publish → Superseded
Agent resource: Enabled ↔ Disabled
```

- 儲存 draft 不等於建立 revision；draft 以 `draftVersion`/ETag 做 optimistic concurrency。
- validate 結果只適用於當時的 draft version；draft 再修改後必須重新驗證。
- publish 必須帶 `expectedDraftVersion`，並在同一交易確認版本、驗證結果與 bindings 後建立不可變 revision。
- stale `PUT draft` 或 publish 回 `409`，不得覆蓋他人的更新或發布不同於已驗證的內容。
- rollback 是把舊 revision 重新發布為一個新 revision，不改寫歷史。
- disabled Agent 不可開始新 run；既有 run 的取消策略由執行狀態決定。

### 2.3 Root Orchestrator 設定

Root Orchestrator 是 SYSTEM_ADMIN 管理的系統協調設定，不是租戶管理者建立的數位員工。每個 immutable Orchestrator revision 至少包含：

- `name`、`description`、system-owned orchestration instructions、deterministic business policy、`enabled` 與適用 tenant／audience policy。
- pinned published `orchestrator` Workflow revision。
- Root 可使用的唯讀 Context source/tool allowlist 與資料範圍上限。
- eligible Worker Agent capability/audience policy。
- pinned Verifier Agent revision；不得在執行中由模型替換。
- `maxContextRounds`、`maxTasks`、`maxChildRuns`、`maxConcurrency`、`maxRepairRounds`、token/time budgets。
- join/partial-failure、repair、approval、escalation 與 final-response policy。

Orchestrator 採與 Agent 相同的 mutable draft、ETag validation、immutable publish、restore-as-new-revision 與 soft disable 原則。Root run 開始後固定 Orchestrator revision，不因管理員發布新版而漂移。

## 3. Skill 綁定

### 3.1 發現與載入

Worker/Verifier Agent 啟動時只取得自身綁定 Skills 的 `name + description + version`，需要時才載入完整 `SKILL.md` 與 references/assets，符合 progressive disclosure。Root Orchestrator 不直接把全租戶 Skill catalog 載入主流程。

### 3.2 版本

- 草稿編輯時可顯示 Skill 最新 revision 並提示更新。
- Agent 發布時必須把每個 binding 固定到確切 Skill revision。
- Skill 更新不會改變已發布 Agent。
- 管理者可在 Agent 草稿中選擇升級並重新測試、發布。
- Skill 被停用後，既有 published Agent 顯示 degraded；是否允許既有 pinned revision 執行需由 tenant policy 決定，預設拒絕開始新 run。

### 3.3 權限

Worker/Verifier Agent 直接使用工具時的 `Effective Direct Tools` 必須是：

```text
platform registered tools
∩ caller role/tenant permissions
∩ Agent allowedTools
∩ current Business Rule decision
```

啟用某個 Skill 指引後，再計算該 Skill 脈絡中的 `Effective Skill Tools`：

```text
Effective Direct Tools
∩ selected Skill allowed-tools
```

任一層未允許即不可呼叫。Prompt、Skill 文件或模型輸出均不能新增權限。未選 Skill 時不套用 Skill allowlist，也不因此阻斷 Agent 本身已被允許的只讀 Context 工具。

權限集合一律 fail closed：省略、`null` 或空集合都代表「沒有授權」，絕不代表全部。建立／發布驗證會 canonicalize 成明確空陣列；任何 system-migrated Agent 也必須寫入實際集合，不允許 `null = all`。

工具名稱權限與資料範圍是兩個不同維度。每次執行還必須計算 `Effective Data Scope`：

```text
caller data grants
∩ Agent knowledgeSources
∩ selected Skill data requirements（如有）
∩ current Business Rule data restriction
```

檢索、文件與 connector tools 必須從 server-injected scope 取得限制；不得由模型在 tool args 自行指定更大的 tenant、collection、document 或 account 範圍。

## 4. Business Rule 規格

### 4.1 Rule AST

Rule 必須以 canonical JSON AST 儲存，不儲存可執行 Python 或任意 JavaScript。

```json
{
  "version": 1,
  "rules": [
    {
      "id": "refund-approval",
      "name": "高額退款需主管核准",
      "enabled": true,
      "priority": 100,
      "when": {
        "all": [
          {"fact": "action.type", "op": "eq", "value": "refund"},
          {"fact": "action.amount", "op": "gt", "value": 5000}
        ]
      },
      "then": [
        {"action": "require_approval", "role": "ADMIN"}
      ]
    }
  ]
}
```

### 4.2 Fact Catalog

公式編輯器只能引用 server 宣告的 typed facts，例如：

- `caller.role: enum`
- `caller.tenant_id: string`（UI 不顯示實值）
- `request.channel: enum`
- `request.intent: string`
- `context.confidence: number`
- `context.source_count: integer`
- `action.type: enum`
- `action.tool_name: string`
- `action.amount: decimal`
- `skill.name: string`
- `result.has_citations: boolean`

Fact 不存在、型別錯誤或來源不可信時，Rule Engine 必須 fail closed 或採該 action 類型定義的安全預設。

條件採三值語意 `true / false / unknown`。`unknown` 不可被悄悄當成 `false`；它必須依規則設定轉為 `require_context`、`ask_user`、`deny` 或其他明確的安全結果。

每個 fact catalog entry 必須宣告 `type`、`provenance`（system/tool/LLM-inferred/user）、`trustTier`，以及可使用的 gate（preflight/post-context/pre-action/post-action/pre-response）。Rule Editor 不得在不可能取得該 fact 的 gate 提供它。

### 4.3 Operators

第一版支援：

- string/enum：`eq`、`neq`、`in`、`contains`
- number：`eq`、`gt`、`gte`、`lt`、`lte`、`between`
- boolean：`is_true`、`is_false`
- collection：`contains`、`contains_any`、`is_empty`
- existence：`exists`、`not_exists`
- group：`all`、`any`、`not`

不支援 regex、任意函式、日期字串自由解析或跨 rule mutation；需要時以版本化 operator catalog 增加。

### 4.4 Actions

第一版支援：

- `deny`
- `require_approval`
- `ask_user`
- `require_context`
- `route_to_skill`
- `allow_read_tool`（只能縮小既有 allowlist，不可擴張）
- `escalate`
- `set_response_policy`
- `add_audit_tag`

衝突處理採明確優先序：`deny > require_approval > escalate > ask_user > route/continue`。同級按 rule priority，再按穩定 rule id 排序。

## 5. 公式編輯器 UX

### 5.1 建立流程

```text
當 [欄位] [運算子] [值]
而且／或者 [條件]
則 [動作] [參數]
```

UI 必須提供：

- 根據 fact 型別限制可選運算子與值元件。
- AND/OR 巢狀群組，但第一版最多三層。
- Skill、Tool、Role 等值由 catalog 選擇，不手打識別字。
- 每條規則即時產生自然語言摘要。
- 錯誤直接定位到條件／動作，不只顯示整體 JSON error。
- Advanced 檢視可看 canonical JSON，但預設唯讀。
- 內建範本：敏感資料、金額核准、證據不足、人工轉接、工具時段限制。

### 5.2 測試模式

使用者可填入模擬 facts 或選擇範例情境，取得：

- 命中與未命中的規則。
- 每個條件的實際值與判斷結果。
- 最終決策及優先序理由。
- 被允許／拒絕的工具或 Skill。
- 缺少 facts 與 fail-closed 結果。

測試模式不得真的執行寫入型工具。

## 6. Root Orchestrator 與多 Agent 行為

### 6.1 Context acquisition

Root Orchestrator 必須：

1. 從問題、對話、身分與既有 Context 建立需求摘要。
2. 列出完成任務所需資訊及缺口。
3. 優先使用已授權的唯讀 Context tools、knowledge sources 或 integrated systems 補足資訊。
4. 為每份 Context 記錄來源、取得時間、權限範圍與可信度。
5. 在達到充分條件、預算上限或無可行取得方式時停止。
6. 只有缺少關鍵資訊且系統無法取得時才詢問使用者。

Root Orchestrator 的 `Effective Context Tools` 必須是：

```text
platform registered read-only context tools
∩ caller role/tenant permissions
∩ Orchestrator context allowlist
∩ current Workflow Node policy
∩ current Business Rule decision
```

Root 不載入租戶 Skill catalog，也不直接啟用 Agent Skill；需要專業能力時，必須先形成 task，再分派 pinned Worker Agent child run。

### 6.2 工作拆解與 Agent 選擇

只有在 Context gate 判定 ready 後才可進入：

- 建立有上限且可形成 DAG 的 task list。
- 每個 task 指定目標、最小 Context、輸入／輸出 contract、候選 Agent capabilities、成功條件、依賴與副作用等級。
- root start 先固定當時 eligible Worker Agent revision set；從 caller/tenant/workflow policy 選定 Worker 後，再於 task assignment/child snapshot 原子固定確切 revision。
- 有副作用的 task 必須經 policy/approval gate。
- v1 只有 Root Orchestrator 可以分派 child runs；Worker Agent 不可遞迴委派其他 Agent。

### 6.3 平行分派與收斂

- 可並行的 tasks 以 bounded fan-out 執行，且受 `maxChildRuns`、`maxConcurrency`、timeout 與 total budget 限制。
- 每個 child run 只取得完成該 task 所需的最小 Context envelope，不取得完整 conversation 或其他 Agent memory。
- parent cancel/timeout 必須傳播到 children；child 的 tool/approval/idempotency 規則保持有效。
- join 必須有明確 partial failure 策略：fail-fast、allow-partial 或 repair；不可由模型臨時決定。
- 平行寫入預設禁止；開放前必須定義資源衝突、順序、approval 與 idempotency。

### 6.4 Verifier Agent

- Verifier 是獨立、pinned、具 `verifier` eligibility 的 Agent revision，不是一段自由 prompt。
- 預設只能讀取 task contract、worker output、證據與 citations，不得呼叫寫入型工具。
- 原則上不可驗證自己的 worker output；例外必須由明確 policy 允許並留下 audit。
- 輸出固定 `PASS / FAIL / NEEDS_REPAIR / INSUFFICIENT_EVIDENCE`、evidence checks、policy findings 與 repair requests。
- repair/re-dispatch 有硬上限；超過後透明回報未完成部分。

### 6.5 彙總與結果驗證

完成前至少檢查：

- 是否回答原始目標。
- 需要引用時是否有來源。
- 是否存在未解決衝突或低可信 Context。
- 是否有未完成／失敗 task。
- 是否命中 deny、approval、escalation 或 response policy。
- 只把 verifier 接受的 worker outputs 納入 aggregation；拒絕／未驗證內容不可當成可信指令。
- 彙總結果保留 task → worker Agent revision → verifier verdict → citations 的 lineage。

## 7. Workflow Designer 規格

Workflow Designer 在管理介面可標示為「Agent Runtime／Execution Harness」。它回答的是「Agent 如何被安全、可靠、可恢復地執行」，不是「這位 Agent 的工作內容是什麼」。

Harness 至少負責：

- 執行前 capability/configuration/dependency preflight。
- 注入經授權的 identity、Context、tools、Skill loader 與資料範圍。
- 狀態初始化、checkpoint、pause/resume、timeout、bounded retry 與 cancellation propagation。
- policy/approval gates、輸出 schema validation、Verifier hook、錯誤正規化、cleanup 與 audit。
- 外部系統不可用時受控降級、重試或終止，不讓 Agent 自行繞過環境限制。

Harness 能降低並隔離環境問題，但不宣稱外部資料庫、模型或整合系統永遠可用；它必須讓這些失敗可觀測、可恢復或安全終止。

### 7.1 Workflow kinds

- `orchestrator`：主流程，允許分解、Agent dispatch、join、verify、repair 與 aggregation。
- `agent-runtime`：單一 Worker/Verifier Agent 的受治理執行流程。
- `subflow`：保留的後續類型；未來以 `Call Workflow` node 引用 pinned revision，P3 MVP 不開放建立或執行。

React Flow 的視覺 grouping/`parentId` 只屬 UI metadata，不等於可執行 subflow。

### 7.2 v1 Orchestrator 必要階段

```text
Start
→ Acquire Context / Analyze Problem（bounded loop）
→ Sufficiency Gate
→ Decompose Work
→ Select/Dispatch Worker Agents（bounded fan-out）
→ Join
→ Invoke Verifier Agent
→ Repair Gate（bounded）
→ Aggregate Accepted Results
→ Respond
→ Audit / End
```

SYSTEM_ADMIN 可調整安全參數、節點設定、可選節點與允許的 edges；身份注入、權限交集、預算、approval、audit、terminal error handling 等 governance shell 由 server 強制，不可刪除或繞過。

Workflow definition 不得內嵌 Agent System Prompt、租戶商業規則或完整 Skill instruction，也不得新增「核准某客戶發票」等 tenant-specific Node Type。這些內容分別保存在 Agent revision、Business Rule AST、Skill package；Harness 只能透過 typed reference/port 使用它們。

### 7.3 v1 Agent-Runtime 必要階段

```text
Compiler-owned Governance Wrapper
  → Preflight Dependencies / Capabilities
  → Inject Authorized Identity / Context / Tool / Skill-loader Scope
  → Initial Checkpoint
  → Bounded Agent Loop
      Model Step
      → optional Load Skill
      → Tool Policy / Approval Gate
      → Tool Call / Observation
      → Checkpoint / Budget Gate
  → Validate Structured Output
  → Repair / Controlled Failure（bounded）
  → Final Checkpoint
  → Cleanup / Audit
  → Return
```

**Stage 分類權威對照表**（此表為唯一權威，其他章節與 06 以此為準）：

| Stage 類型 | 所屬 | Graph IR | Publish 驗證 | 編輯規則 |
| --- | --- | --- | --- | --- |
| **Dependency/Capability Preflight** | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| **Inject Authorized Context** | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| **Initial Checkpoint** | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| **Bounded Agent Loop** | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| - Model Step | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| - Load Skill | visible-optional | 是 | 可缺省 | 可停用；啟用時只能調參 |
| - Tool Gate | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| - Tool Call | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| - Checkpoint+Budget Gate | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| **Validate Structured Output** | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| **Bounded Repair / Controlled Failure** | visible-required | 是 | 缺少即拒絕 | 只能調參，不能刪除 |
| **Identity / Authority Intersection** | wrapper-owned | 否 | 不適用 | compiler 注入，Graph 不可覆寫 |
| **Global Budget** | wrapper-owned | 否 | 不適用 | compiler 注入，Graph 不可覆寫 |
| **Final Checkpoint** | wrapper-owned | 否 | 不適用 | compiler 注入，Graph 不可覆寫 |
| **Terminal Cleanup** | wrapper-owned | 否 | 不適用 | compiler 注入，Graph 不可覆寫 |
| **Audit** | wrapper-owned | 否 | 不適用 | compiler 注入，Graph 不可覆寫 |

Compiler-owned wrapper 永遠存在但不作為可刪除 UI nodes，負責 identity、authority intersection、global budget、terminal cleanup 與 audit。SYSTEM_ADMIN 可編輯的是 wrapper 內的受限 Harness stages/edges；required stages 不可刪除，只能在 schema 允許範圍內調參。

- Worker variant 可使用其 pinned Skills 與 effective tools；寫入工具仍須 action policy、approval、idempotency。
- Verifier variant 固定 read-only scope、Verification Report schema 與 independence policy；不得使用寫入工具，也不得把待驗 Worker output 當 instruction。
- Agent revision 由 run snapshot 注入 typed `AgentExecutionRef` port；Workflow revision 不保存特定 Agent binding 或 `latest` selector。

### 7.4 Graph validation

- 唯一 Start、合法 End、節點可達、無 dangling edge/dead-end。
- typed control/data ports 相容；node config 符合 versioned schema。
- cycle 只允許明示 loop node，且必須有上限。
- fan-out 必須有 join/termination 與 concurrency budget。
- owning Orchestrator/Agent revision 所提供的 Agent/Verifier/Workflow references 必須存在、同 tenant、published、角色相容；Graph 本身不保存特定 Agent binding。
- 必要階段與 governance invariants 完整。
- 不允許任意 Python、YAML node、browser-generated executable code 或未註冊 Node Type。
- Node Catalog 的 authoring capability 與 runtime authority 必須分欄；`workflow.manage` 只控制看見／編輯／發布，不得成為正式 run 的執行前提。

## 8. API 能力

路徑名稱在實作前可微調，但責任不得合併回 Skills API：

```text
GET    /api/agents
POST   /api/agents
GET    /api/agents/{id}
PUT    /api/agents/{id}/draft
POST   /api/agents/{id}/validate
POST   /api/agents/{id}/publish
GET    /api/agents/{id}/revisions
POST   /api/agents/{id}/revisions/{revision}/restore
DELETE /api/agents/{id}                    # soft disable
POST   /api/agents/{id}/enable

GET    /api/agents/catalog/tools
GET    /api/agents/catalog/skills
GET    /api/agents/catalog/rule-facts
GET    /api/agents/catalog/rule-actions
POST   /api/agents/rules/validate
POST   /api/agents/rules/simulate

POST   /api/agents/{id}/runs
POST   /api/orchestrators/{id}/runs
GET    /api/runs/{runId}
POST   /api/runs/{runId}/resume
POST   /api/runs/{runId}/cancel
POST   /api/runs/{runId}/approvals/{approvalId}/approve
POST   /api/runs/{runId}/approvals/{approvalId}/reject

GET    /api/admin/orchestrators
POST   /api/admin/orchestrators
GET    /api/admin/orchestrators/{id}
PUT    /api/admin/orchestrators/{id}/draft
POST   /api/admin/orchestrators/{id}/validate
POST   /api/admin/orchestrators/{id}/publish
GET    /api/admin/orchestrators/{id}/revisions
POST   /api/admin/orchestrators/{id}/revisions/{revision}/restore
POST   /api/admin/orchestrators/{id}/enable
DELETE /api/admin/orchestrators/{id}          # soft disable

GET    /api/admin/workflows
POST   /api/admin/workflows
GET    /api/admin/workflows/{id}
PUT    /api/admin/workflows/{id}/draft
POST   /api/admin/workflows/{id}/validate
POST   /api/admin/workflows/{id}/simulate
POST   /api/admin/workflows/{id}/publish
GET    /api/admin/workflows/{id}/revisions
POST   /api/admin/workflows/{id}/revisions/{revision}/restore
POST   /api/admin/workflows/{id}/enable
DELETE /api/admin/workflows/{id}             # soft disable
GET    /api/admin/workflows/catalog/nodes
```

正式 chat/AG-UI 整合放在 P5；P4 Orchestrator/direct-Agent run API 先提供可測的獨立邊界。

`resume` 只接受補充 Context，不得代表核准。Approval 是獨立、一次性的資源，必須包含要求角色、申請人、核准人、到期時間、決策、理由與 anti-replay token；預設禁止申請人核准自己的高風險 action。

`PUT draft`、`validate` 與 `publish` 必須帶 ETag/`expectedDraftVersion`。Agent/Orchestrator start 依 tenant、audience、enabled/published 狀態過濾並在伺服器再次授權，不只依 UI 隱藏。上述 `/api/admin/orchestrators*` 與 `/api/admin/workflows*` 均要求 `workflow.manage`；正式聊天透過一般 chat API 選取已發布 Orchestrator，不直接開放管理 API。

Runtime lifecycle 與管理權限分離：`/api/runs*` 依 tenant、run owner/participant、run state 與 action policy 授權；approval 另依 `required_role`、separation of duties、expiry 與 anti-replay 驗證，不要求 `workflow.manage`。P4 的 Orchestrator test-start 可額外要求 `workflow.manage`，但正式 chat 由 Platform 內部建立 root run。

正式多 Agent chat 使用 system-managed `orchestratorId`／published Workflow revision；child assignments 才使用 `agentId`。若使用者直接指定某個數位員工，走該 Agent 的 direct `agent-runtime` Workflow，不假裝執行多 Agent Orchestrator。

chat 以 `(tenant, user, conversationId, orchestratorId)` 維持「至多一個 active/waiting root run」不變式。下一輪訊息若存在 `waiting_input` run 就恢復它；若要改 Orchestrator 或開始新任務，必須先 cancel/complete 原 run。內部對話紀錄保存 opaque `rootRunId`。

多 Agent 的記憶不得沿用只有 `{tenant}:{user}` 的單一 namespace：

- Orchestrator 短期 session/root-run/checkpoint key 至少包含 `{tenant}:{user}:{orchestratorId}:{conversationId}`。
- direct Agent/child run state 另記 `agentId`、Agent revision 與 Agent-Runtime Workflow revision。
- Agent 長期記憶預設使用 `{tenant}:{user}:{agentId}` namespace。
- 若未來提供跨 Agent 的 global user facts，必須是獨立、明確分類且可治理的 namespace；不得把任一 Agent 的完整對話自動共享。
- 持久 conversation/message 必須記錄 `orchestratorId`、root Workflow revision、`rootRunId`；child lineage 另記 `agentId`、Agent/Workflow revision 與 childRunId。

## 9. 角色與安全

- tenant ADMIN：建立、修改、綁定、測試、發布與停用 Agent；不因此取得 Workflow 編輯權。
- SYSTEM_ADMIN：產品／UI 對「具 `workflow.manage` capability 的 principal」的稱呼，不新增可繞過 policy 的隱含超級角色。其可編輯／模擬／發布 tenant-scoped Orchestrator/Workflow revisions。現有 JWT 只有 ADMIN/USER，P0 必須新增 capability claim/policy；不得直接把所有 ADMIN 升格。P0 需一併定案 `runtimePolicy` v1 值域與 Node Catalog 註冊格式（參照 03-design §9.2）。
- USER：使用已發布且對其角色開放的 Agent；不可查看完整 System Prompt、內部政策 AST 或秘密資源。
- Agent/Skill/Run 全部 tenant-scoped；跨租戶一律 404。
- Backend 仍只信任通過 internal token 的 Platform/Workflow。
- 外部 Skill package 視為不可信；scripts 在 production isolation 完成前不可執行。
- 寫入型工具要有 action classification、idempotency key、approval 與完整 audit。
- Approval API 必須重新驗證核准者 tenant/role、run 狀態、expiry 與 separation of duties；過期、重播或已決策的 approval 一律拒絕。
- delegation 不可提升權限：caller grants、Orchestrator policy、Workflow node policy、Worker Agent、Skill 與 Business Rules 必須逐層相交。
- Worker output 視為不可信資料，不得被當成 system instruction、Workflow edge 或工具授權。

## 10. 非目標

- 讓一般使用者／Agent 作者編輯 LangGraph；SYSTEM_ADMIN 只能編輯受限視覺 Graph DSL。
- 把 Workflow Designer 當作通用 n8n 替代品或業務流程建模器。
- 允許 System Prompt 宣告新的工具權限。
- 把所有 legacy flow 自動轉成 Agent Skill。
- peer-to-peer 自治、無界遞迴委派或 Worker Agent 自行建立 child Agent；v1 多 Agent 只由 Root Orchestrator 分派。
- 第一版執行外部 package 內的 Python scripts。
- 以 LLM 自由文字判定硬性授權或財務門檻。
