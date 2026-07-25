# Agent 平台重整 — 計畫

> 狀態：規劃中，尚未實作。
>
> 本計畫調整的是產品領域模型與執行責任，不否定既有 Skill Engine 的工程價值。現有 flow Skill、Agent Skill package、聊天與記憶能力先保持相容，再逐步把使用者心智模型改為「建立 Agent、替 Agent 綁定 Skills」。

## 1. 產品目標

讓租戶管理者可以：

1. 建立一個 Agent（數位員工）。
2. 設定 Agent 的 System Prompt、工具限制、知識來源與商業邏輯。
3. 從租戶已上傳且通過驗證的 Agent Skills 中挑選能力並綁定。
4. 使用公式編輯器建立可驗證、可測試、可稽核的商業規則，不必撰寫 Python、YAML 或 LangGraph。
5. 在發布前用測試對話預覽 Agent 的行為、工具呼叫、Skill 選擇與規則命中結果。
6. 發布後，以版本化設定穩定重現每次執行所使用的 Agent 與 Skill 版本。
7. 讓具 `workflow.manage` capability 的 SYSTEM_ADMIN 以 n8n-like 視覺介面調整、驗證、測試與發布主 Orchestrator Workflow。

## 2. 新的產品心智模型

| 概念 | 面向使用者的意義 | 系統責任 |
| --- | --- | --- |
| Agent | 一位有職責、政策與能力的數位員工 | 身分、System Prompt、政策、工具權限、Skill 綁定、執行限制、版本 |
| Agent Skill | 可攜、可重用的能力說明包 | `SKILL.md`、references/assets、允許工具、版本與驗證 |
| Root Orchestrator | 系統管理的主協調者，不是使用者建立的 Worker Agent | 問題分析、工作拆解、Agent 選擇、分派、驗證、彙總 |
| Workflow / Execution Harness | 包覆 Orchestrator 或 Agent 的系統執行骨架，不是使用者的商業流程 | 準備 Context/工具環境、驅動步驟、狀態與 checkpoint、預算、重試、驗證、錯誤收斂、清理與稽核 |
| Tool | Agent 或 Skill 可呼叫的受治理系統能力 | 輸入 schema、身分傳遞、租戶隔離、allowlist、審計 |
| Business Rule | 必須由系統確定性執行的政策 | typed rule AST、優先序、決策結果、命中紀錄 |
| Orchestrator Runtime | 執行主協調流程的內部引擎 | LangGraph 狀態機、預算、重試、暫停、恢復、稽核 |

核心關係：

```text
Root Orchestrator ──執行──> Published Orchestrator Workflow
  ├─ 分派 ──> N 個 Worker Agents
  └─ 驗證 ──> 1 個 pinned Verifier Agent

Worker Agent 1 ──綁定──> Published Agent-Runtime Workflow
  └─ 綁定 ──> N 個 Agent Skills
  ├─ System Prompt（軟性行為指引）
  ├─ Business Rules（系統強制政策）
  ├─ Allowed Tools（能力上限）
  ├─ Knowledge Sources（可讀資料範圍）
  └─ Runtime Limits（工具輪數、逾時、Context 預算）
```

Workflow 的目的是讓 Agent「有條件完成任務，而且在環境、權限或外部依賴異常時受控失敗」。它不保存 Agent 的職責、商業規則或 Skill 內容，也不是讓業務使用者畫出「寄信 → 開單 → 請款」的 n8n 自動化流程。n8n-like 僅描述 SYSTEM_ADMIN 的視覺編輯體驗。

## 3. 現況判定

### 已有、應保留

- Agent Skill package 的上傳、驗證、儲存、版本、匯出與 package-scoped resources。
- `SKILL.md` instruction 與 `allowed-tools` 的解析。
- Tool Registry、tenant/user/role 注入、allowlist、timeout、trace 與 audit。
- Workflow 的 LangGraph、Harness、條件式安全求值與受限迴圈。
- Platform 的 JWT、聊天傳輸、短期記憶、mem0、對話持久化與 AG-UI。
- Backend 的 Dapper/PostgreSQL/in-memory repository 雙路徑與 revision 模式。

### 缺少、需新增

- `Agent`、`AgentRevision`、`AgentSkillBinding` 領域模型。
- `Orchestrator`、`WorkflowDefinition`、`WorkflowRevision` 與視覺 Graph IR。
- Agent CRUD、發布、測試與執行 API。
- Agent Builder UI 與 Skill 挑選器。
- 以 Root Orchestrator 為中心的主 LangGraph，以及 Worker/Verifier 共用契約的 Agent-Runtime Workflow。
- 可視化 Business Rule DSL、公式編輯器、型別檢查與模擬器。
- Agent 執行版本與 Skill revision 的固定策略。
- 每次執行的規則命中、Skill 選擇、工具呼叫與 Context 來源稽核。

### 應降級或重新限縮

- 一般 Agent 作者／終端使用者直接編輯 flow YAML 或 LangGraph node 組合；SYSTEM_ADMIN 則使用受限的視覺 Workflow DSL。
- 把一個 `agentic` Skill 當作完整 Agent 的單節點 ReAct runner。
- 由聊天層直接對全租戶 Skill catalog 做動態路由，而沒有先選定 Agent。
- 把 Workflow Designer 擴張成通用商業流程／整合自動化產品，或把特定客戶的商業步驟寫死成 Node Type。

## 4. 不可混淆的兩種邏輯

System Prompt 與商業規則不能放在同一個文字欄位：

| 類型 | 例子 | 執行方式 |
| --- | --- | --- |
| 軟性指引 | 使用繁體中文、先摘要再說明、保持專業語氣 | 注入模型 instructions |
| 硬性政策 | USER 不可查薪資；退款超過 5,000 元需主管核准 | Rule Engine 在工具／Skill 呼叫前後強制執行 |

System Prompt 不得被用來取代授權、租戶隔離、金額門檻、敏感資料保護或人工確認。

## 5. 目標執行流程

```text
使用者訊息
  → 載入 Root Orchestrator 與 published Workflow revision
  → 套用 request/identity/context policy
  → 主動蒐集 Context 並分析問題
  → 在預算內透過 Orchestrator Workflow 的受治理唯讀 Context nodes 查詢資料庫／整合系統
  → Context 是否足夠？
      ├─ 否，仍可自行取得 → 繼續補資料
      ├─ 否，且只能由使用者提供 → 暫停並提出最小澄清問題
      └─ 是 → 拆解成具輸入、輸出與成功條件的 tasks
  → 從 eligible Agent pool 選擇 published Worker Agent revisions
  → 在 fan-out/concurrency 預算內平行分派 child runs
  → 專用 Verifier Agent 驗證結果與證據
  → 在 repair budget 內補做／重派，或標記無法完成
  → 只彙總已通過驗證的結果
  → 回答、轉人工或要求確認
```

## 6. 交付階段

### P0：領域契約與相容邊界

- 定義 Agent、Agent revision、Skill binding、Business Rule AST 與 execution trace 契約。
- 定義 Orchestrator、Workflow Draft/Revision、Node Type、typed ports、root/child run lineage 與 verifier contract。
- 新增 `workflow.manage` capability claim/policy 與 SYSTEM_ADMIN UI 映射；tenant ADMIN 不自動取得。新增 claim 必須遵守 backend `JwtService` 與 platform 驗證端的 claim 位元相容約束,兩端同一變更內同步。
- 定義 Rule facts 的 provenance、trust tier 與各 policy gate 可用範圍。
- 決定「草稿追蹤最新 Skill、發布時固定 Skill revision」的版本策略。
- 將 Agent Skills 定義為唯一公開 Skill 作者格式；flow YAML 保留為 legacy/internal。
- 釘住既有 `/api/skills*` 行為，避免重整期間破壞已上傳 package。
- 依 Backend 既有 `DbBootstrap` idempotent 建表慣例擴充 schema(不引入 migration framework),並建立 in-memory repository 契約測試。
- 種子一筆 system-owned Default Agent-Runtime Workflow（rev1，published、不可編輯），作為 P1 Agent 發布時 `runtimeWorkflow` 的預設 pin 目標，解除 P1 對 P3 Designer/registry 的依賴。D1 因 Workflow Graph compiler 尚未交付，bootstrap fixture 先由 Backend 內嵌並以唯一 Start/End、可達性、必要 stage 與 bounded-loop 結構守衛驗證；D4 必須將同一 fixture 納入 Workflow-owned Node/port/schema compiler 驗證，不能把 Backend 守衛誤稱為完整 compiler validation。

### P1：Agent Registry 與 Builder

- Backend 新增 Agent CRUD、revision、publish、enable/disable 與 Skill binding。
- Platform 新增同形狀 proxy 與 JWT/role/error mapping。
- Frontend 增加 Agents 清單、建立精靈、System Prompt、工具、Skill 選擇與發布預覽。
- 本期可先用只讀 preview 驗證組態；不切換正式聊天入口。

### P2：Business Rule DSL 與公式編輯器

- 建立 typed JSON AST、欄位目錄、運算子目錄、動作目錄與 server-side validator。
- 前端提供條件群組、AND/OR、型別化輸入、自動完成、自然語言摘要與測試案例。
- Workflow 實作確定性 rule evaluator；所有強制政策不經 LLM 判斷。
- 提供 dry-run API，回傳規則是否命中、取值、結果與錯誤位置。

### P3：Orchestrator／Workflow Registry、Designer 與 Compiler

- Backend 新增 Orchestrator/Workflow draft、revision、publish、rollback 與 Node Catalog 契約。
- Frontend 新增 Orchestrator 設定面板，管理 Root instructions/policy、Context allowlist、Worker pool、Verifier binding 與 budgets。
- Frontend 以 `@xyflow/react` 建立 SYSTEM_ADMIN Workflow Designer，ELK.js 提供可選自動排版。
- UI 編輯並提交產品自有 Graph IR；Backend/appdb 是 durable source of truth，Workflow server 是語意驗證、canonicalization 與 LangGraph 編譯的唯一權威。
- v1 先提供受治理模板與有限可調節邊；不可刪除的 governance shell 由 server 強制。
- Node Catalog 只提供可重用的 Harness primitives，例如 preflight、Context、Agent step、tool/approval gate、checkpoint、retry、verify、cleanup 與 terminal handling；商業動作仍由 Agent 經受治理 Tool 完成。
- Node 的 `workflow.manage` 只限制 SYSTEM_ADMIN authoring；正式 run 另依 runtime authority 執行，不能要求一般使用者具 Workflow 編輯權。

### P4：多 Agent Orchestrator Runtime 與測試控制台

- 實作 Context acquisition/problem analysis → decomposition → bounded parallel dispatch → verifier → repair gate → aggregation → response。
- Worker Agent 使用各自 pinned runtime Workflow、Agent revision 與 Skill revisions。
- 加入 fan-out/concurrency/delegation-depth/step/tool/token/time budget、取消傳播與 parent-child audit。
- 先新增獨立 Orchestrator run/test API；既有聊天保持不變。

### P5：聊天整合、治理與漸進遷移

- 正式協作 chat request 可指定 `orchestratorId`；伺服器依 tenant 驗證並解析 published Orchestrator/Workflow revision。
- 若產品提供「直接與單一數位員工互動」，該獨立入口才接受 `agentId`，並執行其 published Agent-Runtime Workflow，不假裝進入多 Agent 主流程。
- Chat 與 AG-UI 共用同一個遠端 Agent runtime，不再有不同 brain。
- 已完成遷移的 tenant 以 Default Orchestrator 處理未傳 `orchestratorId` 的舊 client；尚未具備安全 Worker/Verifier 設定的 tenant 保持 legacy chat。
- Root Orchestrator 只從 eligible Agent pool 分派；Worker Agent 只看自身綁定的 Skills。
- 釘住 SSE、AG-UI、memory、persistence 與 401 logout 現有契約。

### P6：發布治理與營運能力

- Agent 測試集、回歸評估、版本比較、灰度發布與 rollback。
- Workflow revision 差異、shadow run、canary、跨版本營運級 trace 比較與 rollback。
- 執行觀測：Context 來源、Skill 選擇理由、工具與規則命中、成本、延遲、失敗分類。
- 敏感工具的人工確認與可恢復執行。
- legacy flow Skill 的使用量盤點、唯讀化與長期退場決策。

### 6.1 交付切分(D1–D7)

P0–P6 是範圍分類法，本檔與 02/03/04 的引用維持不變；D1–D7 是實際交付順序，每個 D 里程碑結束時系統必須：build 綠、全測試綠、可部署、feature flag 之外的使用者零變化，且至少包含一個使用者可感知的完整價值增量。純基建一律併入首個消費它的切片，不獨立成里程碑。

| 里程碑 | 完成後的可用狀態 | 含原 P 工作項 | Flag | 驗收 | 量級 |
| --- | --- | --- | --- | --- | --- |
| D1 Agent Registry 與 Builder — **已交付並完成安全加固(2026-07-24)** | flag 開啟後，租戶 ADMIN 可完整走完建立→選取可固定的 persisted Skill/Tool→validate→publish→restore；Builder API 全部 ADMIN-only。Agent/revision/capability persistence、Default Agent-Runtime Workflow 結構守衛與種子在此落地；catalog-only builtin Skill 明示不可綁。一般 USER 的 redacted published Agent catalog、audience run-time enforcement 與正式 capability grant 管理 UI/API 尚未交付 | P0 的 agent schema/capability persistence/種子/契約測試 + P1 authoring/build path；A-UI-08 的 audience 編輯/保存已完成，catalog/run enforcement 分別延至 D6/D3 | `AGENT_BUILDER_ENABLED`（預設 false） | backend 326、platform 509、frontend Agent Builder 8 tests 全綠；A-DATA-01~09、11、14、15；A-UI-01~07；A-UI-08 僅 authoring/storage | L |
| D2 Business Rules 與模擬器 — **已交付並完成安全加固(2026-07-25)** | flag 開啟後，ADMIN 可在 Agent Builder 以 catalog-driven 公式編輯器撰寫 canonical typed AST、即時驗證並以正式 deterministic evaluator 模擬命中、precedence、unknown 與 fail-closed trace；Agent validate/publish/restore 均重新驗證並固定 canonical rules。v1 persisted rules 固定 `pre-action`；正式 Agent run policy gates 與 provenance-bearing runtime facts留 D3 | P2 全部 + P0 的 fact catalog 定案；Spike 5 結論為 LLM-inferred facts 維持低信任 metadata，D3 runtime 只能由 server adapter 提供受信 facts | 同 D1 | A-RULE-01~09；backend 304 passed/45 PostgreSQL-dependent skipped、platform Service 300/Web 231、workflow 702、frontend 17 tests 全綠 | L |
| D3 Direct Agent 測試執行 — **已交付並完成安全加固(2026-07-25)** | ADMIN 可對 immutable published snapshot 啟動 durable 真實測試對話；完成 audience/group/capability 與 tool/knowledge 交集、provenance rule gate、PostgreSQL checkpoint、pause/resume/cancel/deadline、restart recovery、cursor events、trace/output contract/provider budget。採輪詢制，仍不含 streaming/approval/寫入工具 | P4 direct-agent 半邊 + P0 execution-artifact/run lineage；Spike 1、4 結論落地為 generation-fenced lease/thread、HMAC checkpoint identity、durable command claim/recovery 與 exact snapshot hash | `AGENT_TEST_RUN_ENABLED`（預設 false；Workflow 另要求 `CHECKPOINT_DATABASE_URL`、`CHECKPOINT_HMAC_KEY`） | A-RUN-01~12、16~19、22~25；A-DATA-10；backend 443、platform Service 313/Web 254、workflow 800 passed/1 skipped、frontend 31；D3 evidence verifier wait/resume、restart recovery、cancel、unbound grant、rule deny 全 PASS | XL |
| D4 Workflow Designer 與 Orchestrator Registry — **已交付並完成安全加固(2026-07-25)** | SYSTEM_ADMIN 可視覺建立、重載、validate、write-safe simulate、publish、diff、restore typed Harness，並管理固定 Root Workflow、Context allowlist、Worker pool、Verifier、budgets 與 policies 的 Orchestrator revisions。Backend/appdb 是 draft/revision/ETag 權威；Workflow 是 Node Catalog/Graph compiler 權威；Frontend 嚴格分離 semantic Graph IR 與 `ui_metadata`。Agent-runtime 明示 worker/verifier variant，Root context tool 依 registry risk fail-closed | P3 全部 + P0 的 orchestrator/workflow 契約；Spike 6 凍結 typed ports、bounded-loop、fan-out/join 與 variant 契約 | `WORKFLOW_DESIGNER_ENABLED`（預設 false；另要求 exact `workflow.manage`，ADMIN 不隱含） | A-WF-01~28；A-DATA-12、13；Backend 466、Platform 579、Workflow 817 passed/1 skipped、Frontend 44；共享 Default Agent-Runtime fixture 由 Backend 精確比對、Workflow compiler 驗證，並通過 direct-run snapshot preflight | XL |
| D5 多 Agent Root Orchestrator 測試執行 — **已交付並完成安全加固(2026-07-25)** | SYSTEM_ADMIN 由獨立 API 執行 context acquisition→decompose→bounded dispatch→verify→repair→PASS-only aggregate；root/child snapshot、command lease/recovery、token/child/concurrency budget、cancel cascade 與 server-redacted trace durable，真實 stable-node lineage 進 Designer read-only overlay | P4 多 Agent 半邊；D3 durable child runtime + D4 immutable Orchestrator/Graph IR contracts | `MULTI_AGENT_DISPATCH_ENABLED`（預設 false；test-start需 exact `workflow.manage`） | A-MA-01~12；A-RUN-14、15；Backend 470、Platform 584、Workflow 841 passed/1 skipped、Frontend 48；D5 deterministic evidence verifier 8 checks PASS | L |
| D6 聊天/AG-UI 整合(canary) | allowlist 租戶終端 USER 由 Root Orchestrator 服務；未遷移租戶與匿名零感知；回切=關 flag | P5 全部；Spike 2、3 於期初 go/no-go | `AGENT_CHAT_ENABLED` | A-CHAT-01~13 + 05 §7 全部 release blockers；e2e 必跑兩輪 | L(高風險) |
| D7 寫入工具、Approval 與營運治理 | 核准者處理 waiting_approval；逐工具開放寫入(idempotency)；canary/rollback 與成本觀測；啟動 05 §10 退場清單第一批 | P6 + 05 R5/R6 | `AGENT_WRITE_TOOLS_ENABLED` | A-RUN-13、20、21、26；A-OPS-01~07；e2e 必跑 | M–L |

依賴：D1 → D2 → D3 → D5 → D6 → D7；D4 只依賴 D1，可與 D2/D3 平行(Node Catalog 契約凍結後 frontend 可先行)。

與 05 R 階段映射：R0≈D1–D2、R1≈D3–D4、R2≈D5、R3/R4≈D6、R5/R6≈D7。

## 7. 主要遷移原則

1. 不將既有 flow YAML 自動轉譯成自然語言 Skill；語意不一定等價。
2. 不刪除既有 Skill/revision/package；先標示 `legacy-flow` 或 `agent-skill` 能力類型。
3. 新 Agent 只能綁定通過驗證且 enabled 的 Skills。
4. 發布 Agent 時固定 Agent revision 與 Skill revisions；執行不得默默漂移到新版 Skill。
5. Agent 的 System Prompt 不得擴張工具或資料權限。
6. Skill 的 `allowed-tools` 是該 Skill 的上限，不是權限授予。
7. LangGraph 永遠是內部 runtime。Agent Builder 不顯示 node/edge；SYSTEM_ADMIN Workflow Designer 顯示受限 Node/Edge DSL，但不顯示或接受 Python、內部 state key、任意 reducer 或 recursion internals。
8. Worker Agent 不得 peer-to-peer 自由委派；v1 只有 Root Orchestrator 可建立受治理 child runs。
9. Workflow 只定義「如何安全可靠地執行」，Agent/Business Rule/Skill 才定義「由誰、遵守什麼商業政策、以什麼方法完成什麼工作」。

## 8. 完成定義

本重整完成時，管理者可以建立並發布 Worker Agent、綁定標準 Agent Skills、以公式編輯器定義強制商業規則；SYSTEM_ADMIN 可以視覺化調節並發布 Root/Agent-Runtime Workflows。Root Orchestrator 會在有限預算與權限內補 Context、拆解問題、平行分派 Worker Agents、交由獨立 Verifier 驗證、彙總結果並留下可重現的 parent-child 稽核資料。

詳細需求、技術設計與驗收分別見：

- [02-spec.md](02-spec.md)
- [03-design.md](03-design.md)
- [04-acceptance-tests.md](04-acceptance-tests.md)
- [05-migration-and-rollout.md](05-migration-and-rollout.md)
- [06-workflow-designer-evaluation.md](06-workflow-designer-evaluation.md)
