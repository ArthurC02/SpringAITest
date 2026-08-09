# Agent 平台重整 — 計畫

> **Superseded retirement policy (2026-08-03):** R6 的共存、流量門檻與 rollback-window 退場策略已由 [Architecture Hard Reset](../architecture-hard-reset/01-plan.md) 取代；原文保留為歷史理由。現行程式仍遵守本文件，直到 hard-reset 對應 phase 實作並通過 gate。

> 狀態：D1–D7 code/contracts 已交付；R6 legacy cleanup 尚未完成。D6 real-model release evidence 已完成 reconciliation 並 sign-off：2026-08-09 第五跑六關（E-01～E-06）同輪全 PASS，四項技術條件與具名核准（ArthurC）均已達成，release **unblocked**（見 `copilot-shared-core/05-release-evidence-plan.md`）。
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

| 概念                         | 面向使用者的意義                                                | 系統責任                                                                                   |
| ---------------------------- | --------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| Agent                        | 一位有職責、政策與能力的數位員工                                | 身分、System Prompt、政策、工具權限、Skill 綁定、執行限制、版本                            |
| Agent Skill                  | 可攜、可重用的能力說明包                                        | `SKILL.md`、references/assets、允許工具、版本與驗證                                        |
| Root Orchestrator            | 系統管理的主協調者，不是使用者建立的 Worker Agent               | 問題分析、工作拆解、Agent 選擇、分派、驗證、彙總                                           |
| Workflow / Execution Harness | 包覆 Orchestrator 或 Agent 的系統執行骨架，不是使用者的商業流程 | 準備 Context/工具環境、驅動步驟、狀態與 checkpoint、預算、重試、驗證、錯誤收斂、清理與稽核 |
| Tool                         | Agent 或 Skill 可呼叫的受治理系統能力                           | 輸入 schema、身分傳遞、租戶隔離、allowlist、審計                                           |
| Business Rule                | 必須由系統確定性執行的政策                                      | typed rule AST、優先序、決策結果、命中紀錄                                                 |
| Orchestrator Runtime         | 執行主協調流程的內部引擎                                        | LangGraph 狀態機、預算、重試、暫停、恢復、稽核                                             |

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

| 類型     | 例子                                         | 執行方式                                   |
| -------- | -------------------------------------------- | ------------------------------------------ |
| 軟性指引 | 使用繁體中文、先摘要再說明、保持專業語氣     | 注入模型 instructions                      |
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

| 里程碑                                                                               | 完成後的可用狀態                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 | 含原 P 工作項                                                                                                                                                                                   | Flag                                                                                                                                                                                   | 驗收                                                                                                                                                                                                                                                                                                                                                                                                  | 量級      |
| ------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------- |
| D1 Agent Registry 與 Builder — **已交付並完成安全加固(2026-07-24)**                  | flag 開啟後，租戶 ADMIN 可完整走完建立→選取可固定的 persisted Skill/Tool→validate→publish→restore；Builder API 全部 ADMIN-only。Agent/revision/capability persistence、Default Agent-Runtime Workflow 結構守衛與種子在此落地；catalog-only builtin Skill 明示不可綁。一般 USER 的 redacted published Agent catalog、audience run-time enforcement 與正式 capability grant 管理 UI/API 尚未交付                                                                                                                                                                                                   | P0 的 agent schema/capability persistence/種子/契約測試 + P1 authoring/build path；A-UI-08 的 audience 編輯/保存已完成，catalog/run enforcement 分別延至 D6/D3                                  | `AGENT_BUILDER_ENABLED`（預設 false）                                                                                                                                                  | backend 326、platform 509、frontend Agent Builder 8 tests 全綠；A-DATA-01~09、11、14、15；A-UI-01~07；A-UI-08 僅 authoring/storage                                                                                                                                                                                                                                                                    | L         |
| D2 Business Rules 與模擬器 — **已交付並完成安全加固(2026-07-25)**                    | flag 開啟後，ADMIN 可在 Agent Builder 以 catalog-driven 公式編輯器撰寫 canonical typed AST、即時驗證並以正式 deterministic evaluator 模擬命中、precedence、unknown 與 fail-closed trace；Agent validate/publish/restore 均重新驗證並固定 canonical rules。v1 persisted rules 固定 `pre-action`；正式 Agent run policy gates 與 provenance-bearing runtime facts留 D3                                                                                                                                                                                                                             | P2 全部 + P0 的 fact catalog 定案；Spike 5 結論為 LLM-inferred facts 維持低信任 metadata，D3 runtime 只能由 server adapter 提供受信 facts                                                       | 同 D1                                                                                                                                                                                  | A-RULE-01~09；backend 304 passed/45 PostgreSQL-dependent skipped、platform Service 300/Web 231、workflow 702、frontend 17 tests 全綠                                                                                                                                                                                                                                                                  | L         |
| D3 Direct Agent 測試執行 — **已交付並完成安全加固(2026-07-25)**                      | ADMIN 可對 immutable published snapshot 啟動 durable 真實測試對話；完成 audience/group/capability 與 tool/knowledge 交集、provenance rule gate、PostgreSQL checkpoint、pause/resume/cancel/deadline、restart recovery、cursor events、trace/output contract/provider budget。採輪詢制，仍不含 streaming/approval/寫入工具                                                                                                                                                                                                                                                                        | P4 direct-agent 半邊 + P0 execution-artifact/run lineage；Spike 1、4 結論落地為 generation-fenced lease/thread、HMAC checkpoint identity、durable command claim/recovery 與 exact snapshot hash | `AGENT_TEST_RUN_ENABLED`（預設 false；Workflow 另要求 `CHECKPOINT_DATABASE_URL`、`CHECKPOINT_HMAC_KEY`）                                                                               | A-RUN-01~12、16~19、22~25；A-DATA-10；backend 443、platform Service 313/Web 254、workflow 800 passed/1 skipped、frontend 31；D3 evidence verifier wait/resume、restart recovery、cancel、unbound grant、rule deny 全 PASS                                                                                                                                                                             | XL        |
| D4 Workflow Designer 與 Orchestrator Registry — **已交付並完成安全加固(2026-07-25)** | SYSTEM_ADMIN 可視覺建立、重載、validate、write-safe simulate、publish、diff、restore typed Harness，並管理固定 Root Workflow、Context allowlist、Worker pool、Verifier、budgets 與 policies 的 Orchestrator revisions。Backend/appdb 是 draft/revision/ETag 權威；Workflow 是 Node Catalog/Graph compiler 權威；Frontend 嚴格分離 semantic Graph IR 與 `ui_metadata`。Agent-runtime 明示 worker/verifier variant，Root context tool 依 registry risk fail-closed                                                                                                                                 | P3 全部 + P0 的 orchestrator/workflow 契約；Spike 6 凍結 typed ports、bounded-loop、fan-out/join 與 variant 契約                                                                                | `WORKFLOW_DESIGNER_ENABLED`（預設 false；另要求 exact `workflow.manage`，ADMIN 不隱含）                                                                                                | A-WF-01~28；A-DATA-12、13；Backend 466、Platform 579、Workflow 817 passed/1 skipped、Frontend 44；共享 Default Agent-Runtime fixture 由 Backend 精確比對、Workflow compiler 驗證，並通過 direct-run snapshot preflight                                                                                                                                                                                | XL        |
| D5 多 Agent Root Orchestrator 測試執行 — **已交付並完成安全加固(2026-07-25)**        | SYSTEM_ADMIN 由獨立 API 執行 context acquisition→decompose→bounded dispatch→verify→repair→PASS-only aggregate；root/child snapshot、command lease/recovery、token/child/concurrency budget、cancel cascade 與 server-redacted trace durable，真實 stable-node lineage 進 Designer read-only overlay                                                                                                                                                                                                                                                                                              | P4 多 Agent 半邊；D3 durable child runtime + D4 immutable Orchestrator/Graph IR contracts                                                                                                       | `MULTI_AGENT_DISPATCH_ENABLED`（預設 false；test-start需 exact `workflow.manage`）                                                                                                     | A-MA-01~12；A-RUN-14、15；Backend 470、Platform 584、Workflow 841 passed/1 skipped、Frontend 48；D5 deterministic evidence verifier 8 checks PASS                                                                                                                                                                                                                                                     | L         |
| D6 聊天/AG-UI 整合(canary) — **code/contracts 已交付並完成七輪 review(2026-07-25)**  | allowlist 租戶的 authenticated Chat/AG-UI 共用 durable pinned Root Orchestrator；匿名、未 allowlist、disabled 或 Backend `legacy` resolution 保持既有 shared-core，explicit unavailable selection fail-closed。Platform best-effort kick 後由 durable command recovery；回切先關 Platform flag/移除 tenant，再關下游 gates，audit records 保留                                                                                                                                                                                                                                                   | P5 全部；Spike 2、3 契約已落地                                                                                                                                                                  | Platform `AGENT_CHAT_ENABLED` + `AGENT_CHAT_TENANT_ALLOWLIST`；Backend effective gate 另依賴 D5；Workflow 無獨立 chat flag，只執行 `MULTI_AGENT_DISPATCH_ENABLED` 後的 Backend command | A-CHAT-01~13；Backend 479/479、Platform Service 335/335/Web 269/269、Workflow 862 passed/2 skipped、Frontend 50/50 + build/lint；D6 deterministic verifier 13 PASS。**RealModel evidence 已完成 reconciliation 並 sign-off（2026-08-09）**：2026-08-09 第五跑六關（E-01～E-06）同輪全 PASS，四項技術條件與具名核准（ArthurC）均已達成，release **unblocked**；歷史對帳過程見 [Reconciliation Status](../copilot-shared-core/05-release-evidence-plan.md#reconciliation-status-2026-08-09) | L(高風險) |
| D7 寫入工具、Approval 與營運治理 — **已交付並完成 review/e2e(2026-07-25)**           | `waiting_approval` 由 durable approval record 驅動；符合 tenant/role、expiry、action/checkpoint identity 與 separation-of-duties 的業務核准者可 idempotent approve/reject（不要求 `workflow.manage`）。唯一 shipped write tool 為 tenant-allowlisted `runtime.write_evidence`；Backend 以 server-issued one-time effect identity 原子寫入 evidence + outbox，Workflow 僅 claim/consume/recovery，重播或 retry 不重複副作用。`workflow.manage` operations API 提供 regression gate/稽核 override、future-root-only rollout、redacted metrics/version comparison/legacy inventory；legacy 尚未移除 | P6 + 05 R5/R6                                                                                                                                                                                   | `AGENT_WRITE_TOOLS_ENABLED`（Platform/Backend/Workflow 預設 false）                                                                                                                    | A-RUN-13、20、21、26；A-OPS-01~07；Backend 499 total（498 passed/1 skipped）+ isolated PostgreSQL D7 coverage、Platform Service 335/335/Web 277/277、Workflow 867 passed/2 skipped、Frontend 52/52 + build/lint；hybrid cross-service verifier 9 PASS；final review `FINDINGS: 0`                                                                                                                     | M–L       |

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

詳細需求、技術設計與驗收已整併入本檔附錄：

- [§A 規格摘要](#a-規格摘要併自-02-specmd2026-08-09-整併)
- [§B 架構決策](#b-架構決策併自-03-designmd2026-08-09-整併)
- [§C 驗收可追溯性附錄](#c-驗收可追溯性附錄併自-04-acceptance-testsmd2026-08-09-整併)
- [05-migration-and-rollout.md](05-migration-and-rollout.md)
- [§D 畫布技術評估](#d-畫布技術評估併自-06-workflow-designer-evaluationmd2026-08-09-整併)

---

## §A 規格摘要(併自 02-spec.md,2026-08-09 整併)

> 來源狀態：D1–D7 已依此規格交付；以下為 WHAT/邊界的歷史決策摘要,實作偏離處已在各里程碑驗收欄註記。

### A.1 API 能力(02-spec §8)

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

# Tool/Skill picker 重用既有 GET /api/tools、GET /api/skills/catalog（帶 bindable 欄位），不新增 catalog 專屬路由。
GET    /api/agents/catalog/rule-facts
GET    /api/agents/catalog/rule-actions
POST   /api/agents/rules/validate
POST   /api/agents/rules/simulate

# 已落地的選擇：root run 生命週期與 direct/child Agent run 走不同路由前綴，見下方兩組。
POST   /api/admin/orchestrators/{id}/runs  # 啟動，需 workflow.manage
GET    /api/orchestrator-runs/{id}
GET    /api/orchestrator-runs/{id}/events
POST   /api/orchestrator-runs/{id}/cancel  # root run 生命週期

POST   /api/agents/{id}/runs
GET    /api/runs/{runId}
GET    /api/runs/{runId}/events
POST   /api/runs/{runId}/resume
POST   /api/runs/{runId}/cancel            # direct/child Agent run
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

### A.2 Rule AST 值域(02-spec §4.3–§4.4)

第一版 Operators：

- string/enum：`eq`、`neq`、`in`、`contains`
- number：`eq`、`gt`、`gte`、`lt`、`lte`、`between`
- boolean：`is_true`、`is_false`
- collection：`contains`、`contains_any`、`is_empty`
- existence：`exists`、`not_exists`
- group：`all`、`any`、`not`

不支援 regex、任意函式、日期字串自由解析或跨 rule mutation；需要時以版本化 operator catalog 增加。

第一版 Actions：

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

### A.3 角色與安全的權限交集規則摘要(02-spec §9)

- tenant ADMIN：建立、修改、綁定、測試、發布與停用 Agent；不因此取得 Workflow 編輯權。
- SYSTEM_ADMIN：產品／UI 對「具 `workflow.manage` capability 的 principal」的稱呼，不新增可繞過 policy 的隱含超級角色。其可編輯／模擬／發布 tenant-scoped Orchestrator/Workflow revisions。現有 JWT 只有 ADMIN/USER，P0 必須新增 capability claim/policy；不得直接把所有 ADMIN 升格。
- USER：使用已發布且對其角色開放的 Agent；不可查看完整 System Prompt、內部政策 AST 或秘密資源。
- Agent/Skill/Run 全部 tenant-scoped；跨租戶一律 404。
- Backend 仍只信任通過 internal token 的 Platform/Workflow。
- 外部 Skill package 視為不可信；scripts 在 production isolation 完成前不可執行。
- 寫入型工具要有 action classification、idempotency key、approval 與完整 audit。
- Approval API 必須重新驗證核准者 tenant/role、run 狀態、expiry 與 separation of duties；過期、重播或已決策的 approval 一律拒絕。
- delegation 不可提升權限：caller grants、Orchestrator policy、Workflow node policy、Worker Agent、Skill 與 Business Rules 必須逐層相交。
- Worker output 視為不可信資料，不得被當成 system instruction、Workflow edge 或工具授權。

### A.4 非目標清單(02-spec §10)

- 讓一般使用者／Agent 作者編輯 LangGraph；SYSTEM_ADMIN 只能編輯受限視覺 Graph DSL。
- 把 Workflow Designer 當作通用 n8n 替代品或業務流程建模器。
- 允許 System Prompt 宣告新的工具權限。
- 把所有 legacy flow 自動轉成 Agent Skill。
- peer-to-peer 自治、無界遞迴委派或 Worker Agent 自行建立 child Agent；v1 多 Agent 只由 Root Orchestrator 分派。
- 第一版執行外部 package 內的 Python scripts。
- 以 LLM 自由文字判定硬性授權或財務門檻。

---

## §B 架構決策(併自 03-design.md,2026-08-09 整併)

> 來源狀態：D1–D7 已依此設計交付；以下為 HOW 的歷史決策摘要。

### B.1 架構決策表 D1–D10(03-design §1)

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

### B.2 記憶／持久化 key 規則(03-design §11.3)

```text
root session:       {tenant}:{user}:{orchestratorId}:{conversationId}
worker memory:      {tenant}:{user}:{agentId}
global user facts:  {tenant}:{user}:global
persisted message:  tenant/user/conversationId/orchestratorId/workflowRevision/rootRunId
child lineage:      rootRunId/taskId/agentId/agentRevision/agentWorkflowRevision/childRunId
```

新 Agent Runtime 不直接讀取舊 `{tenant}:{user}` mem0 bucket。若要遷移既有記憶，先分類成可共享 global facts 或指定已發布 Agent namespace；無法安全分類的內容保留在 legacy，不自動注入其他 Agent。

### B.3 決策前 Spike 清單與結論(03-design §13)

實作 P3/P4 前必須用短 spike 回答，其結論已落地為對應里程碑的實作依據：

結論摘要見 §6.1 對應里程碑列；此處只列 spike 要回答的問題本身：

1. **LangGraph PostgreSQL checkpointer** 與現有 appdb/async stack 的相容性、取消與租戶分區 → 結論見 §6.1 D3。
2. **Workflow → Platform 的 streaming event protocol**；選 SSE 或 NDJSON 一種 → 結論見 §6.1 D6。
3. **Microsoft Agent Framework hosted agent** 包裝遠端 Workflow runtime 時，session/memory 與 AG-UI event mapping 的最小介面 → 結論見 §6.1 D6。
4. 驗證單一 Worker child 內的 `load_skill` progressive disclosure；與 Root→Worker child dispatch 的 parent-child trace/預算清楚分層 → 結論同 Spike 1，見 §6.1 D3。
5. 以 P0 已定義的 fact provenance/trust/gate catalog 驗證 LLM-inferred facts 的實際品質；不得把 fact 契約本身延到 P3 才決定 → 結論見 §6.1 D2。
6. 用 20–100 node fixtures 驗證 React Flow + ELK.js 的互動、排版、序列化與 trace overlay 效能 → 結論見 §6.1 D4。

---

## §C 驗收可追溯性附錄(併自 04-acceptance-tests.md,2026-08-09 整併)

> 來源狀態：D1–D7 交付完成；以下 ID→情境→驗收清單為完整歷史驗收依據。D1 已覆蓋 A-DATA-01~09、11、14、15 與 A-UI-01~07；A-UI-08 當時只有 audience authoring/storage，published catalog 與 run-time enforcement 分別於 D6/D3 完成驗收；其餘條目在對應里程碑（見上方 §6.1）陸續驗收。

### C.1 P0：資料與契約

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-DATA-01 | 同 tenant 建立兩個不同 slug 的 Agent | 成功，revision 狀態正確 |
| A-DATA-02 | 同 tenant 重複 slug | 409；不同 tenant 可使用相同 slug |
| A-DATA-03 | 讀取其他 tenant Agent | 404，不洩漏存在性 |
| A-DATA-04 | 發布 Agent | 建立不可變 revision，固定 Skill revisions 與 definition hash |
| A-DATA-05 | Skill 更新 | 已發布 Agent snapshot 不變 |
| A-DATA-06 | rollback | 建立新 revision；歷史不被改寫 |
| A-DATA-07 | Dapper 與 in-memory repository | CRUD、revision、soft disable 與 binding 行為一致 |
| A-DATA-08 | 兩位 ADMIN 同時編輯同一 draft | stale ETag/expectedDraftVersion 回 409，不覆蓋新版本 |
| A-DATA-09 | validate 後 draft 被修改再 publish | publish 拒絕，必須重新驗證相同 draft version |
| A-DATA-10 | 讀取 pinned Skill artifact | 精確回指定 revision/hash；Skill current 更新不影響 |
| A-DATA-11 | allowedTools/knowledgeSources 省略、null 或空陣列 | 全部 canonicalize 為無權限，publish/run 不取得預設全開能力 |
| A-DATA-12 | Orchestrator/Workflow contract round-trip | draft/revision/node ports、root-child lineage 與 Verifier Report 可無損序列化 |
| A-DATA-13 | Orchestrator/Workflow published revision | immutable；restore 建立新 revision，active run 不漂移 |
| A-DATA-14 | `workflow.manage` claim/policy | capability principal 通過；單純 tenant ADMIN 與 USER 均拒絕 |
| A-DATA-15 | SYSTEM_ADMIN 標籤 | 只映射 capability policy，不形成繞過 tenant/policy 的隱含角色 |

### C.2 P1：Agent Builder

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-UI-01 | ADMIN 建立 Agent | 可輸入名稱、說明、System Prompt，儲存 draft |
| A-UI-02 | USER 進入 Agent 設定 | 不可建立、修改或發布 |
| A-UI-03 | Skill picker | 只有同 tenant、enabled、驗證通過且具 persisted immutable revision 的 Skills 可選；catalog-only builtin 可唯讀顯示，但必須標成不可綁並說明原因 |
| A-UI-04 | Tool picker | 顯示描述與風險分類，不顯示內部 token/endpoint |
| A-UI-05 | Agent draft 有失效 Skill | 顯示定位錯誤，禁止發布 |
| A-UI-06 | 發布預覽 | 顯示固定的 Agent revision、Skill revisions、工具與規則摘要 |
| A-UI-07 | Agent Builder | 一般 ADMIN 不出現 LangGraph node/edge/state key；Workflow Designer 是獨立 SYSTEM_ADMIN 功能 |
| A-UI-08 | Agent audience | catalog 與 run 都拒絕不在 allowed role/group 的使用者 |

### C.3 P2：Business Rules

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-RULE-01 | number fact 搭配 string operator | server validation 拒絕並定位該 condition |
| A-RULE-02 | 巢狀超過上限或 AST 過大 | 拒絕，沒有 evaluator 資源放大 |
| A-RULE-03 | 高額退款規則 | `amount > 5000` 回 `require_approval: ADMIN` |
| A-RULE-04 | deny 與 route 同時命中 | deny 依固定 precedence 勝出 |
| A-RULE-05 | 缺少安全關鍵 fact | fail closed 並留下 decision trace |
| A-RULE-06 | UI round-trip | 編輯、儲存、重載後 canonical AST 語意相同 |
| A-RULE-07 | 自然語言摘要 | 與 AST 對應，摘要不作執行權威 |
| A-RULE-08 | simulation | 使用正式 evaluator；不呼叫真實寫入工具 |
| A-RULE-09 | prompt injection 要求忽略規則 | evaluator 結果不受模型文字影響 |

### C.4 P3：Orchestrator／Workflow Registry 與 Designer

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-WF-01 | SYSTEM_ADMIN 建立 Workflow draft | 可從 Node Catalog 拖放、連線、設定、儲存與重載 |
| A-WF-02 | tenant ADMIN/USER 呼叫 Workflow 管理 API | 403；ADMIN 不自動取得 `workflow.manage` |
| A-WF-03 | React Flow UI round-trip | Semantic Graph IR 不因座標/viewport 改變；`ui_metadata` 可獨立更新 |
| A-WF-04 | typed ports 不相容 | client 即時阻擋且 server validate 再次拒絕 |
| A-WF-05 | 未知 node/version、dangling edge、unreachable/dead-end | server 回定位至 stable node/edge id 的錯誤 |
| A-WF-06 | 一般 cycle 或無界 loop | publish 拒絕；明示 bounded loop 才可通過 |
| A-WF-07 | 缺 Start/End/Verifier/Join/Audit 等必要階段 | publish 拒絕；governance shell 不可刪除 |
| A-WF-08 | fan-out 無 join/concurrency budget | publish 拒絕 |
| A-WF-09 | stale draft ETag | 409，不覆蓋他人修改 |
| A-WF-10 | publish | 建立 immutable Workflow revision，固定 node versions/semantic hash；P3 不開放 executable subflow |
| A-WF-11 | published rev1 後發布 rev2 | rev1 active runs 不漂移；新 run 依 rollout policy 使用 rev2 |
| A-WF-12 | restore/rollback | 建立新 revision，不原地修改歷史 Graph |
| A-WF-13 | simulate | 使用 server compiler/evaluator，不執行寫入型工具 |
| A-WF-14 | simulated trace overlay | 以 stable node IDs 呈現模擬狀態，敏感資料遮罩；真實 root/child trace 於 P4 接入 |
| A-WF-15 | Orchestrator/Agent revision 所引用的 Workflow/Agent/Verifier reference 跨 tenant 或未發布 | 該 Orchestrator/Agent 的 validate/publish 拒絕；Workflow Graph 本身不保存 Agent binding（見 A-WF-28） |
| A-WF-16 | SYSTEM_ADMIN 發布 Orchestrator | 固定 Root Workflow、Context allowlist、Agent pool、Verifier revision、budgets 與 policies |
| A-WF-17 | active root run 期間發布新版 Orchestrator | active run 保持原 revision；只有新 run 使用新版 |
| A-WF-18 | Root Context acquisition 嘗試載入 Skill 或寫入型工具 | 拒絕；Root 只可使用有效交集內的唯讀 Context node/tool |
| A-WF-19 | Orchestrator 設定面板 | 可管理 Root instructions/policy、Context allowlist、Worker pool、pinned Verifier 與 budgets |
| A-WF-20 | Graph IR 內嵌 System Prompt、Business Rule 或 Skill instruction | validate/publish 拒絕；只能使用 pinned typed reference |
| A-WF-21 | tenant-specific business Node Type | 不進入 system Node Catalog；商業動作必須由 Agent 經受治理 Tool/Rule 完成 |
| A-WF-22 | dependency preflight 失敗 | Agent 尚未執行即受控終止或降級，回可觀測錯誤且不遺留資源 |
| A-WF-23 | timeout/cancel/error terminal path | checkpoint、child/tool cancellation、cleanup 與 audit 依 Harness 契約完成 |
| A-WF-24 | 兩個契約相容的 Agent 使用同一 Agent-Runtime Workflow revision | Harness 可重用；各 run 仍固定自己的 Agent/Rule/Skill snapshot，不互相污染 |
| A-WF-25 | 一般 USER 執行含 `dispatch_agents` 的已發布 Root Harness | 不要求 `workflow.manage`；依 runtime effective authority 正常執行或拒絕 |
| A-WF-26 | Agent-Runtime 缺 preflight/output validation 等 visible-required stage | publish 拒絕；cleanup/audit 屬 compiler-owned wrapper，不可由 Graph 覆寫，所有 terminal path 必經（以 run-time 驗證，非 publish 驗證） |
| A-WF-27 | Verifier 使用 Worker Harness variant 或寫入工具 | publish/run 拒絕；必須使用 read-only Verifier variant 與固定 report contract |
| A-WF-28 | Workflow Graph 保存特定 Agent binding／`latest` selector | validate 拒絕；Agent reference 由 Orchestrator/Agent revision 與 run snapshot 注入 |

### C.5 P4：Root Orchestrator 與 Agent Runtime

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-RUN-01 | Context 已足夠 | 不做多餘檢索，直接進入拆解 |
| A-RUN-02 | Context 不足但可查 Backend | 主動取得資料，不先詢問使用者 |
| A-RUN-03 | 只能由使用者提供關鍵資訊 | run 進入 `waiting_input`，只問最小澄清問題 |
| A-RUN-04 | resume | 使用相同 Root Workflow、Agent-Runtime、Agent/Skill revisions 恢復，不重跑已完成副作用 |
| A-RUN-05 | 重複查詢 | 被 dedupe 或預算拒絕，不形成無限循環 |
| A-RUN-06 | 超過 Context/tool/step/time budget | 受控終止、audit 完整、無背景工具殘留 |
| A-RUN-07 | Agent 綁定 Skill A/B，未綁定 C | catalog/load/invoke 均不可看見或呼叫 C |
| A-RUN-08 | Skill 要求未允許工具 | 工具未被呼叫，回 tool denied decision |
| A-RUN-09 | caller 無資料權限 | 即使 Agent/Skill allowlist 有工具仍拒絕 |
| A-RUN-10 | pinned Skill 被更新 | run 使用 snapshot 固定 revision |
| A-RUN-11 | package resource | 只可讀同 package 的受允許相對路徑與大小 |
| A-RUN-12 | external script | production isolation 未完成前不可執行 |
| A-RUN-13 | 規則要求 approval | 寫入型工具未執行，run 進入 `waiting_approval` |
| A-RUN-14 | result 驗證失敗 | 在 repair budget 內重派 child task；超限後透明說明不足 |
| A-RUN-15 | audit | 記錄 Root Workflow、parent-child、Agent/Skill revisions、rules、tools、Context、budget 與結果 |
| A-RUN-16 | 標準 Agent Skill 執行 | Skill instruction 載入同一 Worker child context，不建立額外 Agent child run |
| A-RUN-17 | legacy flow 綁定 pinned revision | adapter 執行指定 revision，不按名稱落到 current flow |
| A-RUN-18 | 同 tenant、不同 knowledge source | retrieve 只能讀 Agent 的 Effective Data Scope，越界來源拒絕 |
| A-RUN-19 | 模型在 tool args 偽造較大 scope | server 忽略／拒絕，不擴張 ToolContext scope |
| A-RUN-20 | approval 核准與拒絕 | 只有正確 tenant/role 且非禁止 self-approval 的核准者可決策 |
| A-RUN-21 | approval 過期、跨 tenant、重播或重複決策 | 一律拒絕，write tool 未執行 |
| A-RUN-22 | cancel waiting/running run | 進入 cancelled；in-flight tool 被取消且不可 resume |
| A-RUN-23 | Skill A scope 內呼叫未允許工具 | 即使 Agent direct allowlist 較寬仍拒絕 |
| A-RUN-24 | Skill A 完成後進入 direct-tool step | A 的 instruction frame 已移除，不影響後續決策或工具授權 |
| A-RUN-25 | Skill scope 中 pause/resume/cancel | 精確恢復相同 revision/scope 或在取消時清除，不殘留到其他 task |
| A-RUN-26 | 業務核准者沒有 `workflow.manage` | 若 tenant、`required_role`、separation-of-duties 與 run policy 均通過，仍可透過 runtime approval API 決策 |
| A-MA-01 | Context 不足 | Root 先主動取得足夠資料，再進入工作拆解 |
| A-MA-02 | 兩個獨立 tasks | 分派到兩個 pinned Worker Agent children，實際平行且不超過 concurrency cap |
| A-MA-03 | child Context | 只收到最小 task envelope，不含完整 conversation／其他 Agent memory |
| A-MA-04 | worker 嘗試委派另一 Agent | v1 拒絕；delegation depth 不得提升 |
| A-MA-05 | 一個 child timeout/fail | 依 published join policy fail-fast/partial/repair，不由模型臨時改策略 |
| A-MA-06 | parent cancel/timeout | 取消所有未完成 children，無背景 child/tool 殘留 |
| A-MA-07 | verifier selection | 使用獨立 pinned verifier revision，預設不可等於被驗 Worker 且無 write tools |
| A-MA-08 | verification report | 符合固定 verdict/evidence/repair schema；prompt prose 不可偽造 PASS |
| A-MA-09 | repair loop | 只在 maxRepairRounds 內重派；超限後停止 |
| A-MA-10 | aggregation | 只使用 PASS outputs，保留 worker/verifier/citation lineage |
| A-MA-11 | delegation authority | child effective authority 不超過 caller/root workflow/Agent/Skill/rule 交集 |
| A-MA-12 | parallel write tasks | v1 拒絕，或必須有已發布的衝突/approval/idempotency policy |

### C.6 P5：聊天與 AG-UI

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-CHAT-01 | request 指定同 tenant published Orchestrator | 使用該 Root Workflow snapshot |
| A-CHAT-02 | Orchestrator 不存在、跨 tenant、disabled 或未發布 | 404/409，不能 fallback 到其他 tenant/default |
| A-CHAT-03 | 舊 client 未帶 orchestratorId | 已遷移 tenant 使用 Default Orchestrator；未完成安全設定者保持 legacy，既有聊天仍可用 |
| A-CHAT-04 | `/api/chat/stream` | `data:` 無空格格式、flush 與 mid-stream error frame 不變 |
| A-CHAT-05 | AG-UI | `data: ` 標準格式、JWT fail-closed 與 session isolation 不變 |
| A-CHAT-06 | 401 | 兩種 transport 都觸發同一 global logout |
| A-CHAT-07 | memory | recall 在 Agent run 前，remember/persistence 在完整 turn 後 |
| A-CHAT-08 | shared brain | Chat 與 AG-UI 對同 Orchestrator、輸入與 Context 使用相同 Root Workflow |
| A-CHAT-09 | clarification | 多輪補充後 resume 同一 run，不建立失去上下文的新計畫 |
| A-CHAT-10 | 同 conversation 有 waiting root run | 下一輪依 tenant/user/conversation/orchestrator 唯一鍵恢復；切換前先取消 |
| A-CHAT-11 | 同一使用者交錯使用 Orchestrator A/B | root session/checkpoint 不污染；Worker Agent memory 仍依 agentId 隔離 |
| A-CHAT-12 | conversation history | 訊息帶 orchestrator/workflow/rootRun；child lineage 帶 Agent/Workflow revisions |
| A-CHAT-13 | legacy mem0 migration | 未分類的 `{tenant}:{user}` 舊內容不自動注入新 Agent |

### C.7 P6：發布與營運

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-OPS-01 | 發布前 regression set 有失敗 | 依 tenant policy 阻擋或要求明確 override |
| A-OPS-02 | rollback | 新 turn 使用 rollback revision，舊 run 保持原 snapshot |
| A-OPS-03 | 敏感 trace | UI/API 遮罩 secrets、token、未授權 Context |
| A-OPS-04 | 成本/延遲 | 可依 Agent、revision、Skill、tool 聚合 |
| A-OPS-05 | 人工核准 | approval 有核准人、時間、決策與 idempotency audit |
| A-OPS-06 | Workflow canary/rollback | 新 root runs依 rollout revision；active runs 保持原 snapshot |
| A-OPS-07 | 多 Agent 指標 | 可觀察 node latency、fan-out、child success、verifier reject/repair、aggregation |

### C.8 回歸命令(04-acceptance-tests §8)

每一垂直切片至少執行：

```powershell
cd backend
dotnet build
dotnet test

cd ../platform
dotnet build
dotnet test

cd ../workflow
uv run pytest

cd ../frontend
npm run lint
npm run build
```

P4/P5 必須再由 full-chain E2E 驗證：

- PostgreSQL 真實 revision/binding/snapshot。
- Platform → Backend/Workflow 的 internal token 與 identity headers。
- LiteLLM mock model 的 decomposition/dispatch/verifier/aggregation 與 Worker Skill loop。
- chat streaming、AG-UI、pause/resume、approval 與跨租戶隔離。

手寫 fake 只能驗證局部契約，不能取代上述跨服務真實 JSON、stream 與資料庫行為。

---

## §D 畫布技術評估(併自 06-workflow-designer-evaluation.md,2026-08-09 整併)

> 決策：v1 選用 `@xyflow/react`（React Flow）作視覺畫布，搭配 ELK.js 自動排版；Backend/appdb 持久化 Graph IR drafts/revisions，Workflow server 是語意驗證、canonicalization、LangGraph 編譯與執行的唯一權威。產品定位：這是 SYSTEM_ADMIN 使用的 Agent Execution Harness Designer；n8n-like 指操作方式，不代表建立通用商業自動化平台。

### D.1 候選比較結論(06 §2)

比較過 React Flow (`@xyflow/react`)、Rete.js v2、JointJS/`@joint/react`、BaklavaJS、LiteGraph.js、n8n editor 與 LangSmith Studio：React Flow 是原生 React/TypeScript、支援 custom nodes、多 handles、subflow grouping、connection validation、save/restore 且為 MIT license，與現有前端最貼合，定為**首選**；Rete.js v2 因其 engine 會與 LangGraph 形成雙 runtime、部分 advanced plugins 非商用授權，列為第二候選但不採其 engine/scopes；JointJS 核心為 MPL-2.0，多數 n8n-like 完整 editor 功能集中在商業版 JointJS+，待未來採購企業工具時再評估；BaklavaJS 官方 renderer 是 Vue，需混載或自寫 renderer，不選；LiteGraph.js 無原生 React 且自帶 engine 與 LangGraph 重疊，不選；n8n editor 是 Vue 應用，其 Sustainable Use License 對商業嵌入有限制，只借鏡 UX 不 fork/embed；LangSmith Studio 定位是開發／觀測工具而非可嵌入產品的拖拉拓樸 authoring SDK，僅可作 trace UX 參考。

### D.2 選擇 @xyflow/react + ELK.js 的理由(06 §3)

React Flow 官方將它定位為建立 node-based UI 的可客製 React component，核心包含拖曳、縮放、平移、選取與增刪；custom nodes 可放任意 React 內容，多個 handles 可表達不同 ports。官方也提供 connection validation、save/restore、subflow/grouping 與 ELK/Dagre layout 範例。它沒有宣稱執行 workflow，反而符合本案邊界：畫布只產生產品自有 Graph IR，Python Workflow service 才做權威驗證與 LangGraph 編譯。自動排版選用 ELK.js，結果只更新 `ui_metadata`，不影響 semantic Graph IR。

### D.3 儲存邊界原則(06 §5)

不把 React Flow state 直接當 runtime definition，semantic Graph IR 與 `ui_metadata` 分開儲存：

```text
WorkflowDefinition
  semantic Graph IR:
    nodes(type/version/config)
    edges(source port/target port)
    governance

WorkflowUiMetadata
  positions
  viewport
  visual groups
  collapsed state
```

- Semantic definition 與 UI metadata 分開 hash/diff，但共用 draft ETag/version；只移動節點不會改變 semantic definition hash。
- React Flow `parentId` 只代表視覺 grouping；未來真正 subflow 使用 `Call Workflow` + pinned revision，P3 MVP 不開放。
- 瀏覽器不產生 Python、LangGraph code 或可執行 script。
