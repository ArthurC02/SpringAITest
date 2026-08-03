# Agent 平台重整 — 技術設計

> **Superseded retirement policy (2026-08-03):** R6 的共存、流量門檻與 rollback-window 退場策略已由 [Architecture Hard Reset](../architecture-hard-reset/01-plan.md) 取代；原文保留為歷史理由。現行程式仍遵守本文件，直到 hard-reset 對應 phase 實作並通過 gate。

> 狀態：**D1–D7 已依此設計交付**；本檔作為 HOW 的歷史決策記錄。承接 [01-plan.md](01-plan.md) 與 [02-spec.md](02-spec.md)，本檔定義 HOW。

## 1. 架構決策

| ID | 決策 | 理由 |
| --- | --- | --- |
| D1 | Agent 是獨立 aggregate，不擴充 Skill row 假裝 Agent | Agent 有 prompt、政策、bindings、發布生命週期，與可攜能力包不同 |
| D2 | Agent/Skill/Orchestrator/Workflow metadata 與 revisions 由 Backend/appdb 持久化 | 延續現有資料唯一事實來源與 Dapper/revision 模式 |
| D3 | Root Orchestrator 與 Agent-Runtime LangGraph 由 Workflow 編譯／執行 | AI orchestration、tool registry、Agent Skill loader、timeout/audit 已在此 |
| D4 | Platform 保留 auth、transport、session/memory 與 persistence | 避免 Workflow 成為公開服務或複製 JWT/聊天責任 |
| D5 | Business Rules 是版本化 JSON AST，由確定性 evaluator 執行 | 可視化、型別檢查、可測、不可由 prompt injection 改寫 |
| D6 | 每次 root run 固定 Orchestrator/Workflow、Worker/Verifier Agent、Agent-Runtime Workflow 與 Skill revisions | 可重現、可 rollback、避免執行中漂移 |
| D7 | SYSTEM_ADMIN 編輯受限視覺 Graph IR，Workflow server 驗證後編譯 LangGraph | 支援調節流程，同時不暴露 Python/LangGraph internals 或可移除的治理邊界 |
| D8 | 既有 `agent_skill_runner` 先保留作相容 executor，不升格為 Root Orchestrator 或 Worker Agent runtime | 它目前一次只執行一個 package instruction |
| D9 | 新舊聊天採 side-by-side migration | 不一次替換兩個已上線 chat transports |
| D10 | Workflow 是 system-owned Execution Harness，不是 Agent/Skill/Business Rule 的內容容器 | 讓同一 Harness 可重用於多個相容 Agent，避免 Graph 退化成 tenant-specific business automation |

## 2. 服務責任

```text
Frontend
  Agent Builder / Formula Editor / Workflow Designer / Test Console
        ↓ Bearer JWT
Platform :8080
  auth + public API + chat transports + session/memory + error mapping
        ↓ internal token + identity
Backend :8002
  Agent/Skill/Orchestrator/Workflow/revision/run metadata + appdb
        ↕
Workflow :8001
  Graph IR validation/compiler + rule evaluator + Root/Child LangGraph execution
        ↓
LiteLLM / Backend retrieval / integrated systems
```

### Backend

- 新增 `Agents`、`Orchestrators`、`Workflows` feature folders 與 Dapper/in-memory repositories。
- 保存 draft/current/published revision 指標。
- 原子寫入 Agent revision、bindings 與 hashes。
- 提供 tenant-scoped Agent catalog、Workflow/Node catalog 與 pinned root/child execution snapshots。
- 不解析 System Prompt 語意，不自行實作另一套 rule evaluator。

### Workflow

- 驗證 Orchestrator/Agent execution snapshot、Workflow Graph IR、Rule AST 與 Tool/Skill/Agent references。
- 將 published Workflow revision 編譯成 LangGraph 並執行 root/child runs。
- pinned Skill 必須使用 revision-aware execution artifact endpoint；現有只回 current package 的 endpoint 僅保留 legacy current invoke。
- 所有工具仍經 Tool Registry 與 `ToolContext`。
- 在 run 前執行 dependency/capability preflight，在 run 中提供 checkpoint、budget、retry、approval、cancellation、verification hooks，在所有 terminal path 執行 cleanup/audit。
- 不在 Graph IR 內儲存 Agent Prompt、租戶商業規則、Skill instruction 或 tenant-specific business action。

### Platform

- 對外代理 Agent、Orchestrator、Workflow CRUD/catalog/test/run；管理路徑強制 `workflow.manage`。
- 從 JWT 派生 tenant/user/role，不接受 body 偽造。
- P5 將 chat/AG-UI turn 委派給指定 Root Orchestrator 或 direct Agent runtime。
- 保留 SSE 格式、global 401 logout、memory 與 conversation persistence。
- 將短期 session、mem0 與 persisted messages 加上 Orchestrator/Agent/run 維度，避免主流程或多個數位員工共享未授權 Context。

### Frontend

- 新增 Agent 頂層工作區；Skill 管理保留為能力庫。
- Agent Builder 只顯示產品概念。
- Formula Editor 操作 typed catalogs，不拼接任意表達式字串。
- SYSTEM_ADMIN Workflow Designer 使用 `@xyflow/react` 呈現 typed nodes/ports；UI 只產生 Graph IR，不執行流程。

## 3. 資料模型

建議 schema；實際 SQL 命名遵循 Backend 既有慣例：

```text
orchestrator
  id uuid PK
  tenant_id text
  name text
  enabled boolean
  draft_version bigint
  draft_definition jsonb
  published_revision integer nullable

orchestrator_revision
  orchestrator_id uuid
  revision integer
  system_prompt text
  business_policy jsonb
  workflow_id uuid
  workflow_revision integer
  agent_pool_policy jsonb
  verifier_binding jsonb
  definition_sha256 text
  created_by / created_at

workflow
  id uuid PK
  tenant_id text
  name text
  kind text                 # orchestrator/agent-runtime/subflow
  enabled boolean
  draft_version bigint
  draft_definition jsonb   # semantic Graph IR
  draft_ui_metadata jsonb  # positions/viewport/groups
  published_revision integer nullable

workflow_revision
  workflow_id uuid
  revision integer
  schema_version integer
  definition jsonb
  ui_metadata jsonb
  definition_sha256 text
  ui_metadata_sha256 text
  compiler_contract_version text
  created_by / created_at

agent
  id uuid PK
  tenant_id text
  slug text
  name text
  description text
  enabled boolean
  draft_version bigint
  draft_definition jsonb
  draft_validated_version bigint nullable
  published_revision integer nullable
  created_at / updated_at
  UNIQUE(tenant_id, slug)

agent_revision
  agent_id uuid FK
  revision integer
  status text              # published/superseded
  system_prompt text
  execution_roles jsonb
  capabilities jsonb
  output_contract jsonb
  audience jsonb
  business_rules jsonb
  allowed_tools jsonb
  knowledge_sources jsonb
  runtime_limits jsonb
  runtime_workflow_id uuid
  runtime_workflow_revision integer
  definition_sha256 text
  created_by text
  created_at
  UNIQUE(agent_id, revision)

agent_revision_skill
  agent_id uuid
  agent_revision integer
  skill_id uuid
  skill_revision integer
  position integer
  enabled boolean
  PRIMARY KEY(agent_id, agent_revision, skill_id)
```

Agent run 若需 pause/resume 與完整營運稽核，新增：

```text
agent_run
  id uuid PK
  tenant_id
  root_run_id uuid
  parent_run_id uuid nullable
  task_id text nullable
  run_kind                # orchestrator/worker/verifier/direct-agent
  orchestrator_id / orchestrator_revision nullable
  workflow_id / workflow_revision
  agent_id / agent_revision nullable
  conversation_id / user_id
  status                  # running/waiting_input/waiting_approval/completed/failed/cancelled
  checkpoint_ref
  started_at / updated_at / completed_at

agent_run_event
  run_id
  sequence
  event_type
  payload jsonb           # 必須經敏感資料遮罩
  created_at

agent_run_approval
  id uuid PK
  run_id uuid
  action_fingerprint text
  requested_by text
  required_role text
  status                  # pending/approved/rejected/expired/consumed
  expires_at
  decided_by / decided_at / reason
  anti_replay_hash text
```

`agent.draft_definition` 是唯一 mutable 作者副本，以 `draft_version` 做 optimistic concurrency。`agent_revision` 只保存 publish 產生的 immutable snapshots；validate 將結果綁定到確切 `draft_validated_version`，publish 在同一交易驗證 expected version 未漂移。

大型 LangGraph checkpoint 不宜直接塞進 `agent_run_event`；P3 spike 後選擇 PostgreSQL checkpointer 或由 Backend 提供 opaque checkpoint blob。選定一種，不建立雙重事實來源。

Agent、Orchestrator、Workflow 三個 aggregate 採用相同的 draft/ETag/validate/publish/restore 狀態機；Backend 以單一共用的 revisioned-aggregate helper 實作（沿用 `SkillRepository` 的 revision/CTE 模式），禁止三份獨立拷貝。

## 4. Root／Child Execution Snapshot

Workflow 不在執行途中逐筆抓 Graph、Agent、rules、bindings 與 Skills。Backend 在 root run 開始時提供 tenant-scoped Orchestrator snapshot：

```json
{
  "orchestratorId": "…",
  "orchestratorRevision": 2,
  "workflow": {
    "id": "main-orchestration",
    "revision": 5,
    "definitionSha256": "…",
    "compilerContractVersion": "d4-graph-ir-1"
  },
  "agentPoolPolicy": {
    "requiredAudience": ["USER"],
    "allowedCapabilities": ["research", "analysis", "verification"]
  },
  "verifierBinding": {
    "agentId": "quality-verifier",
    "agentRevision": 3
  },
  "runtimeLimits": {
    "maxContextRounds": 4,
    "maxTasks": 12,
    "maxChildRuns": 12,
    "maxConcurrency": 4,
    "maxRepairRounds": 2,
    "timeoutSeconds": 180
  }
}
```

Snapshot 分成兩個不可混淆的固定點：

1. root start 原子固定 Orchestrator、Root Workflow、Verifier、policies/budgets，以及當時 eligible Worker Agent revision set。
2. task assignment 選定 Worker 後，於 child 建立前原子固定該 Worker 的 Agent revision、Agent-Runtime Workflow revision、Business Rules、effective tools/data scope 與 Skill revisions。

Verifier 使用 root start 已固定的 binding 建立同類 child snapshot，但套用 read-only scope 與固定 Verification Report contract。task assignment 一旦寫入就不可在 child start 前重新解析 `latest`。

每個 root/child run 的事件都記錄 snapshot hash。Workflow revision 只宣告 typed `AgentExecutionRef` input/port，不保存特定 Agent binding 或 `latest` selector。Orchestrator revision 負責 Agent pool/Verifier policy；root start 與 task assignment snapshot 負責解析並固定確切 revisions。

Backend 必須提供 revision-aware internal artifact contract，例如：

```text
GET /api/skills/{name}/revisions/{revision}/execution-artifact
```

它依 internal token、tenant 與 pinned revision 回傳不可變的 package 或 legacy flow definition，加上 kind/hash。Workflow 不得用現有 current-only package endpoint執行 pinned child Agent；`invoke_legacy_flow` 也必須帶 revision 或直接使用 artifact，不能只按名稱呼叫 current Skill。

## 5. Root Orchestrator 與 Child Agent Graphs

### 5.1 State

```text
identity                server-injected tenant/user/role
orchestrator_snapshot   immutable Orchestrator + Root Workflow configuration
effective_data_scope    server-computed knowledge/connector boundaries
messages                current conversation input/window
goal                    normalized user goal
context_items[]         value + source + timestamp + confidence + sensitivity
missing_context[]       required information gaps
context_round           bounded counter
task_assignments[]      bounded DAG + contracts + selected Agent revisions
child_runs[]            run ids/status/budgets/lineage
worker_results[]        untrusted structured outputs + evidence
verification_report    structured verifier verdict/repair requests
aggregation_result     accepted results only
fanout_budget          remaining child runs/concurrency/time
rule_decisions[]        deterministic policy results
pending_action          clarification or approval request
final_answer
errors[] / fatal_error / audit
```

Root Workflow revision、已選 Worker/Verifier Agent revisions、各 Agent-Runtime Workflow/Skill revisions、身分與預算是 immutable state。LLM 不得寫入或覆蓋。

### 5.2 Graph

```text
START
  → intake
  → preflight_rules
  → acquire_context_and_analyze_problem
  → sufficiency_gate
      ├─ ready → decompose_work
      ├─ acquire → call_governed_read_source
      │              → normalize_context
      │              → acquire_context_and_analyze_problem
      ├─ ask_user → persist_waiting_input → END
      └─ deny/escalate → compose_controlled_response → finalize
  → validate_task_graph_and_select_agents
  → dispatch_worker_children_bounded_parallel
  → join_worker_results
  → invoke_pinned_verifier_child
  → verification_gate
      ├─ PASS → aggregate_accepted_results
      ├─ NEEDS_REPAIR within budget → build_repair_tasks → dispatch_worker_children
      ├─ approval required → persist_waiting_approval → END
      └─ FAIL/INSUFFICIENT → aggregate_partial_with_limitations
  → compose_response
  → output_rules
  → finalize_audit
  → END
```

所有 loop 都有硬上限；條件式失敗走安全終止或人工介入，不繼續自由執行。

每個 Worker/Verifier child run 執行該 Agent pinned `agent-runtime` Workflow。標準 Skill activation 只發生在 child Agent scope 內；它不等於再分派一個 Agent。

有效 authority 必須逐層縮小：

```text
caller grants
∩ Root Orchestrator policy
∩ current Workflow Node policy
∩ selected Worker/Verifier Agent allowlist
∩ active Skill allowed-tools（若有）
∩ Business Rule decisions
```

Tool/Agent adapter 不接受模型擴張集合。Worker output 視為 untrusted data，不能變成 system instruction、Graph edge、Agent selection policy 或工具授權。

資料能力另以 `Effective Data Scope` 控制。`ToolContext` 或等價 server context 必須攜帶允許的 knowledge source、document collection 與 connector account 範圍；generic retrieve 不得僅憑 tenant 就搜尋全部資料，也不得相信模型傳入的 scope。

Context acquisition 發生在 Root scope，僅能使用 `caller grants ∩ Orchestrator context allowlist ∩ current Node policy ∩ Business Rules` 的唯讀工具與資料範圍。Root 不載入 Agent Skill；Skill 只在已選定 Worker/Verifier child scope 內 progressive disclosure。

### 5.3 LLM 與確定性節點

LLM 適合：

- goal normalization
- context gap inference
- task decomposition 與候選 Agent 排序
- 答案整合

程式／Rule Engine 必須負責：

- 身分與權限
- Workflow/Agent/tool/Skill allowlist
- 金額與角色門檻
- loop/budget/timeout
- rule priority/conflict
- citations/source metadata
- run/checkpoint 狀態
- 寫入型 action 的 approval/idempotency
- max fan-out/concurrency/delegation depth、child cancellation 與 join/partial-failure policy

### 5.4 Pause、resume 與 approval

- `(tenant_id, user_id, conversation_id, orchestrator_id)` 最多只能有一個 active 或 waiting root run；資料庫以 partial unique constraint 或交易鎖保證。
- `waiting_input` 只可由同一使用者／對話補充 Context 後 resume。
- `waiting_approval` 不接受 generic resume；必須走 approve/reject API。
- approval 決策重新檢查 tenant、required role、expiry、action fingerprint、run/checkpoint 版本與 separation of duties。
- approve 只把一次性 decision 寫入 checkpoint；真正 action 執行時原子 consume approval 與 idempotency key，避免重播或 retry 重複副作用。
- cancel 將 run 轉為 terminal `cancelled` 並傳遞 cancellation；取消後不可 resume。
- parent cancel/timeout 會取消所有未完成 children；已完成 write action 以 idempotency/audit 保留，不靠重跑回滾。

### 5.5 Agent-Runtime Harness

Worker 與 Verifier child 都由相同 compiler-owned governance wrapper 包覆，wrapper 不存在於可編輯 Graph IR：

```text
server identity/authority/budget wrapper
  → dependency_and_capability_preflight
  → inject_authorized_execution_context
  → checkpoint
  → bounded_agent_loop
      → model_step
      → optional_load_skill
      → tool_policy_and_approval_gate
      → tool_call_and_observation
      → checkpoint_and_budget_gate
  → validate_output_contract
  → bounded_repair_or_controlled_failure
  → final_checkpoint
  → cleanup_and_audit
  → RETURN
```

Graph compiler 必須證明 required visible stages 可達、所有 loop 有上限、每個 terminal path 經 cleanup/audit。Worker variant 依 snapshot 取得 Skill/tool scopes；Verifier variant 強制 read-only、independence policy 與固定 Verification Report output。SYSTEM_ADMIN 可調整 schema 允許的 loop、retry、timeout、validation/repair 參數與安全 edges，但不能移除 compiler wrapper 或降低 runtime authority。

## 6. Skill 執行模式

### 6.1 Progressive disclosure

Worker/Verifier Agent child run 的初始 prompt 只取得該 Agent 綁定 Skills 摘要。選中後透過受治理的 `load_skill` 取得完整 instruction；resources 仍按需用 package-scoped `read_resource`。

### 6.2 執行策略

標準 Agent Skill 不另啟動一顆 Agent child run。Worker/Verifier Agent 選中 Skill 後，把 pinned `SKILL.md` instruction 與按需 resources 載入同一個 child Agent context；工具仍由該 Agent-Runtime Workflow 呼叫。這與 Root Orchestrator 分派 Worker Agent 的 parent-child run 是不同層次。

Skill activation 必須是明確的 task/step scope：

1. 進入時建立 immutable `active_skill_scope`，包含 Skill name/revision、instruction hash、Effective Skill Tools 與允許 resources。
2. Skill instruction 只作為該 task 的 ephemeral model-input frame，不追加到 conversation history 或全域 messages。
3. scope 存在時，所有工具呼叫一律套用 `Effective Skill Tools`，不能退回較寬的 Agent direct-tool 集合。
4. task 完成、失敗、取消或切換 Skill 時清除 scope；後續 direct-tool step 不再帶該 Skill instruction。
5. trace 記錄 scope enter/exit 與其 revision/hash，resume 時由 checkpoint 精確恢復或清除。

現有 `agent_skill_runner` 僅保留 explicit invoke 與 legacy chat 相容，不是新 Agent path 的預設 executor。

Legacy Flow 則透過 revision-aware `invoke_legacy_flow` adapter 呼叫現有 compiled flow，並正規化輸出：

```json
{
  "status": "completed|needs_input|denied|failed",
  "content": {},
  "citations": [],
  "contextFacts": [],
  "audit": {
    "skill": "contract-review",
    "revision": 3
  }
}
```

標準 Agent Skill instruction 只影響同一 Worker/Verifier Agent loop 的工作方法，不得直接控制 Graph 跳轉或擴張權限；Legacy Flow 的 declared result 也必須回到該 Agent-Runtime Graph，再由確定性 gate 決定下一步。

## 7. Rule Engine

### 7.1 分層

```text
Rule Catalog       定義 facts/operators/actions 與 JSON schema
Rule Validator     儲存/發布前型別與 reference 驗證
Rule Evaluator     執行期純函式：facts + AST → decisions
Rule Simulator     編輯器 dry-run，使用相同 evaluator
Policy Gates       Graph 節點，在 action 前後呼叫 evaluator
```

Validator 與 Simulator 必須重用同一套 operator/action registry，不在 Frontend 複製語意。Frontend catalog 只用於 UI rendering。

Fact registry 在 P0/P2 前即固定每個 fact 的型別、來源、信任級別與 gate availability。Rule Editor 依 gate 過濾可選 facts；發布驗證拒絕在該 gate 永遠不可取得的 fact，避免 runtime 才全面變成 unknown。

### 7.2 安全性

- AST 最大節點數、巢狀深度、字串與 collection 大小需有限制。
- 不接受 `__`、任意 property traversal、函式名稱或 dynamic import。
- evaluator 無網路、檔案、資料庫與工具能力。
- Fact 值由 server adapters 產生，LLM 產生的 intent/confidence 必須標記為 inferred，政策可選擇不信任。
- 條件採 `true / false / unknown` 三值語意；unknown 的處置必須在 rule/action contract 中明確定義。
- evaluate error 產生 audit 並依 action class fail closed。

Agent/Skill/tool/data-scope 的集合欄位缺少、`null` 或空集合都按 empty 處理。Validator 將合法輸入 canonicalize 為明確陣列；任何服務不得實作 `null = unrestricted`。

上述安全限制清單與測試手法沿用 `workflow/app/engine/expressions.py` 既有的安全求值經驗（封鎖 `__`、白名單 operator、深度與大小上限）；但 Rule AST 結構與三值語意刻意獨立，不共用其字串條件求值器，兩者各自服務 flow 條件與 Business Rule 兩種契約。

## 8. Formula Editor 元件

建議元件：

```text
BusinessRuleEditor
  ├─ RuleList
  ├─ RuleCard
  │   ├─ ConditionGroup
  │   │   └─ ConditionRow(fact, operator, typed value)
  │   ├─ ActionList
  │   └─ NaturalLanguageSummary
  ├─ RuleTemplateGallery
  ├─ RuleValidationPanel
  └─ RuleSimulator
```

Editor state 直接對應 canonical AST；不先產生另一套 UI schema 再轉換，避免 round-trip 漂移。拖曳排序只改 `priority/position`，不可改 rule id。

## 9. Workflow Graph IR、Designer 與 Compiler

本節的 Workflow 等同 Execution Harness。Graph 描述 runtime control/data flow 與治理生命週期；業務語意透過 immutable Agent/Rule/Skill references 進入 run snapshot，不複製進 Graph。

### 9.1 技術選擇

- 畫布使用 `@xyflow/react`；它只負責節點、邊、handles、viewport 與互動。
- 自動排版使用 ELK.js，結果只更新 `ui_metadata`。
- 不 fork/embed n8n editor；其 Vue 技術棧與 Sustainable Use License 不適合作為本產品的 React 商業嵌入元件。
- 不使用前端或 Rete/LiteGraph engine 執行流程；唯一 runtime 是 Workflow server 編譯的 LangGraph。

完整比較見 [06-workflow-designer-evaluation.md](06-workflow-designer-evaluation.md)。

### 9.2 Node Type contract

```json
{
  "type": "dispatch_agents",
  "version": "1.0",
  "title": "分派 Agent",
  "kind": "control",
  "inputs": [{"id": "tasks", "dataType": "TaskAssignment[]", "required": true}],
  "outputs": [{"id": "results", "dataType": "WorkerResult[]"}],
  "configSchema": {},
  "risk": "orchestration",
  "authoringCapability": "workflow.manage",
  "catalogVisibility": "system-admin",
  "runtimePolicy": "orchestration.dispatch",
  "runtimeAdapter": "server-owned"
}
```

Frontend 依 Node Catalog 產生 node palette、typed handles 與 property inspector；client connection validation 只提供即時 UX，server validation/publish 永遠重新驗證。

`authoringCapability`/`catalogVisibility` 只控制誰能看見、編輯與發布 Node；正式 run 不要求 `workflow.manage`。`runtimePolicy` 由 server 使用 caller、Orchestrator、Workflow node policy、Agent、Rule 與 Tool authority 的有效交集判定。

`runtimePolicy` 契約定義：
- `runtimePolicy` 是封閉列舉值，與 Node Type 一同註冊於 system Node Catalog；v1 值域：`context.read`、`agent.step`、`tool.invoke`、`orchestration.dispatch`、`verification.execute`、`run.control`。
- 每個 Node Type 恰好宣告一個 runtimePolicy；validate/publish 遇到未註冊值一律拒絕。
- 正式 run 時 server 以「caller 的 tenant/role 與 run 授權 ∩ Orchestrator revision policy ∩ node runtimePolicy ∩ Agent effective tools ∩ Business Rule decisions」的有效交集判定，任一層缺席即 fail-closed 拒絕；交集判定只在 server，client 不參與。

Node Type 必須是跨 Agent 可重用的 Harness primitive。像 `load_context`、`invoke_agent_step`、`tool_gate`、`checkpoint`、`verify_output`、`cleanup` 合法；像 `approve_acme_invoice` 或內嵌某租戶 prompt 的 node 不合法。

### 9.3 Graph IR

```json
{
  "schemaVersion": 1,
  "kind": "orchestrator",
  "nodes": [
    {"id": "analyze", "type": "analyze_problem", "typeVersion": "1.0", "config": {}}
  ],
  "edges": [
    {
      "id": "e1",
      "source": {"nodeId": "analyze", "port": "result"},
      "target": {"nodeId": "decompose", "port": "analysis"}
    }
  ],
  "governance": {"maxSteps": 40, "maxConcurrency": 4}
}
```

- Semantic Graph IR 與 `ui_metadata`（x/y、viewport、visual groups）分開儲存與 hash。
- React Flow 的原生 nodes/edges serialization 不是執行契約。
- executable subflow 的未來契約使用 `Call Workflow` node + pinned revision；P3 MVP 尚不開放此 Node Type。visual group 永遠不具 runtime 語意。
- stable node IDs 用於 revision diff、validation errors 與 execution trace overlay。

### 9.4 Compile pipeline

```text
React Flow UI
  → Graph IR draft + ui_metadata
  → Workflow server schema/reference/dataflow/governance validation
  → canonical Graph IR + definition hash
  → published immutable Workflow revision
  → compile cache(workflow id/revision/hash/node-catalog-version)
  → LangGraph
  → root/child run with pinned snapshot
```

Server 強制 validation：未知／下架 node version、typed port 不相容、unreachable/dead-end、非法 cycle、缺必要 stage、fan-out 無 join/budget、跨租戶 Agent、Verifier 不相容、不可移除 governance shell。未來啟用 `Call Workflow` 時再加入 pinned revision 與 subflow recursion validation。

新 Graph IR compiler 與既有 `workflow/app/engine/compiler.py`（flow YAML）並存至 R6，不互相依賴。Node Catalog 的 server-side registry 重用 `node_registry.py` 既有的 contract 註冊基建，擴充 typed ports、configSchema 與 runtimePolicy，不另建第二套註冊機制。新 Execution Harness 相關元件命名必須迴避既有 `harness.py`（skill-engine 的 reads-enforcement），以 `runtime_` 或 `orchestration_` 前綴區隔。

### 9.5 Designer UX

P3 MVP 包含 node palette、搜尋、canvas、minimap、typed connection、property inspector、validation panel、auto-layout、undo/redo、draft autosave with ETag、simulate、publish、revision diff/restore 與 simulated trace overlay。P4 再接真實 root/child run events；P6 才提供跨版本營運比較。Proprietary/Pro 範例功能不得在未確認授權前直接複製；MVP history/clipboard 可在應用層自行實作。

## 10. API 與錯誤

- Public Agent API 維持現有 ApiError `{timestamp,status,message,fieldErrors}`。
- Agent validation 可比照 Skill validation回 `200 {valid,errors[]}`，供編輯器逐項標示；publish failure 則使用 `409/422`。
- Workflow internal error 要分類：invalid snapshot、rule violation、tool denied、skill unavailable、timeout、needs input、needs approval。
- 對使用者的 5xx 維持固定訊息，詳細執行資料只進受保護 log/trace。

## 11. Migration

### 11.1 Side-by-side

- 新增 Agents/Orchestrators/Workflows 功能，不先改 `/api/chat*`。
- 只有已有明確 published Worker pool 與 pinned Verifier 的 eligible tenant，才從 system template 建立 published Default Orchestrator Workflow rev1；其他 tenant 保持 legacy chat。另提供 Default Agent-Runtime Workflow template。
- P3 先交付 Designer/validate/publish、published graph 唯讀檢視與 simulated trace，不接正式 chat。
- P4 以獨立 root/direct-Agent run API 驗證多 Agent runtime，並把真實 root/child events 接到唯讀 trace overlay。
- P5 由 feature flag/tenant allowlist 將 chat 切到 Root Orchestrator。
- 對已完成遷移的 tenant，未指定 `orchestratorId` 解析到 Default Orchestrator；未完成者保持 legacy。直接選數位員工則走 direct Agent run。

### 11.2 Skill

- 現有 `agentic` package 直接成為可綁定 Agent Skill。
- 現有 flow Skill 以 `legacy-flow` adapter 暫時可綁定，但 UI 明確標示「舊版流程」。
- 新建 Skill UI 預設只建立／上傳 Agent Skill package。
- Advanced flow editor 先移到管理者／legacy 區，不立即刪除。
- `agent_skill_runner` 保留供 explicit legacy invoke；Root Orchestrator/Worker child 不以它作 Agent 分派機制。

### 11.3 Chat pipeline

P5 前先定義唯一 brain 邊界：Platform 的兩個 hosted agents不再各自含不同推理邏輯；它們成為 transport/session adapter，將 turn 交給 Workflow Root Orchestrator。mem0 recall、remember 與 conversation persistence 仍在 Platform pipeline 外框，避免跨服務重做。

記憶與持久化的 key/metadata：

```text
root session:       {tenant}:{user}:{orchestratorId}:{conversationId}
worker memory:      {tenant}:{user}:{agentId}
global user facts:  {tenant}:{user}:global
persisted message:  tenant/user/conversationId/orchestratorId/workflowRevision/rootRunId
child lineage:      rootRunId/taskId/agentId/agentRevision/agentWorkflowRevision/childRunId
```

新 Agent Runtime 不直接讀取舊 `{tenant}:{user}` mem0 bucket。若要遷移既有記憶，先分類成可共享 global facts 或指定已發布 Agent namespace；無法安全分類的內容保留在 legacy，不自動注入其他 Agent。

類別級對應：`ChatContextProvider` 與 `ChatTurnRecorder` 重用（recorder 改包 Orchestrator 轉接層）；`ChatMemoryKeyDerivation` 擴充為雙 key 規則（legacy 兩段、orchestrator 四段），legacy namespace 退場後收斂；`AguiWireDedupAgent` 與 `JwtTenantIsolationKeyProvider` 屬 transport/session 層，保留；`SkillRoutingAgent` 及其迴歸測試共存至退場條件成立後整批刪除（見 05-migration §10 退場清單）。

## 12. 建議變更位置

| Area | 新增／調整 |
| --- | --- |
| `backend/` | `Agents/`、`Orchestrators/`、`Workflows/`、DB migration、repos、root/child snapshot API |
| `platform/` | Agent/Orchestrator/Workflow proxy、`workflow.manage` policy、runtime authorization、P5 chat/AG-UI Orchestrator adapter |
| `workflow/` | Graph IR validator/compiler、Node Registry、root/child runs、policy、snapshot/checkpoint |
| `frontend/` | Agents workspace、Formula Editor、SYSTEM_ADMIN Workflow Designer、trace/test console |
| `infra/` | 僅在採用持久 LangGraph checkpointer 或新 sandbox 時調整 |

## 13. 決策前 Spike

實作 P3/P4 前必須用短 spike 回答：

1. LangGraph PostgreSQL checkpointer 與現有 appdb/async stack 的相容性、取消與租戶分區。
2. Workflow → Platform 的 streaming event protocol；選 SSE 或 NDJSON 一種。
3. Microsoft Agent Framework hosted agent 包裝遠端 Workflow runtime 時，session/memory 與 AG-UI event mapping 的最小介面。
4. 驗證單一 Worker child 內的 `load_skill` progressive disclosure；與 Root→Worker child dispatch 的 parent-child trace/預算清楚分層。
5. 以 P0 已定義的 fact provenance/trust/gate catalog 驗證 LLM-inferred facts 的實際品質；不得把 fact 契約本身延到 P3 才決定。
6. 用 20–100 node fixtures 驗證 React Flow + ELK.js 的互動、排版、序列化與 trace overlay 效能。
