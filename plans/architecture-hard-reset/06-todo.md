# Architecture Hard Reset — Implementation Todo List

> **維護規則(強制):** 每完成一項工作,實作該項的變更必須在**同一個 commit** 將對應條目從 `- [ ]` 改為 `- [x]`,條目尾端註記完成日期(commit hash 由該條目的 git 歷史可溯,不必手寫)。任何 session 開始實作前先讀本檔確認下一個未完成項;發現與現況不符的條目,先修正條目再動工。Phase gate 條目未全勾之前,不得開始下一 phase 的破壞性工作(P1 內六個工作包可並行)。
>
> Baseline: historical P0 evidence is `8652528`(2026-08-03); P3 planning baseline is now `44f9de4`(2026-08-06). Existing uncommitted Wave4/5 work is outside P3 and must be preserved. See [08-p3-reconciliation-44f9de4.md](08-p3-reconciliation-44f9de4.md).

## P0 — 計畫收尾
- [x] P0-A1 Ledger 依 8652528 偵察結果修正(50 表、14/32/5 檔數、SkillHash 10 模組、新增 AgentEditor/ChatOrchestratorController/ensure-mem0-db/correlation-ID 條目)— 2026-08-03
- [x] P0-A2 agent-platform-redesign 01/02/03/04/06 補 superseded 標頭 — 2026-08-03
- [x] P0-A3 02-spec/04-acceptance/05-ledger 補互鏈;三份索引補 06-todo 連結 — 2026-08-03
- [x] P0-B4 記錄 baseline 測試基準 — 2026-08-03 於 commit `8652528`(appdb 容器在線,Postgres 測試真跑無 skip):

  | Suite | 指令 | 結果 |
  | --- | --- | --- |
  | backend | `dotnet test` | **1240 passed**, 0 failed, 0 skipped(4m37s) |
  | platform | `dotnet test` | **886 passed**(Service 474 + Web 412), 0 failed, 0 skipped |
  | workflow | `uv run pytest -q` | **1629 passed**, 5 skipped, 16 warnings(4m57s) |
  | frontend lint | `npm run lint` | 通過(5 個 fast-refresh warning:Toast.tsx、AgentBuilderCopilot.tsx) |
  | frontend build | `npm run build` | 通過(chunk >500kB warning) |
  | frontend logic | `test:unit:logic` | **109 passed**(9 檔) |
  | frontend UI | `test:unit:ui` | **88 passed / 6 failed** |

  **Baseline 既有失敗(6,全在 frontend UI)— 已確認為 flaky,非穩定紅:單獨重跑兩個 spec 檔 23/23 全過(53.9s),失敗只在全套 `test:unit` 並行時出現(資源競爭)。P1-G 比對時視為已知 flaky;WP1-FE 動 `AgentEditor`/`runWithToast` 時注意這些案例對 conflict 行為有斷言:**
  - `agentBuilder.ui.spec.ts:72` governed catalogs/ETag/conflict lock/dialog keyboard
  - `agentBuilder.ui.spec.ts:238` collapsed advanced group validation
  - `agentBuilder.ui.spec.ts:320` still-loading Tool Catalog
  - `agentBuilder.ui.spec.ts:424` catalog rule limits nesting depth
  - `agentBuilder.ui.spec.ts:482` rule-fact catalog depth fallback
  - `agentRuns.ui.spec.ts:322` start retries logical key rotation

## P1 — 正確性與部署穩定(六包可並行;每包完成後 code-reviewer,全部合併後 e2e-verifier)
### WP1-FE(frontend-implementer)
- [x] P1-FE1 `runWithToast` 新增 `onConflict` 共通層(409/412 鎖編輯器、零成功 toast),覆蓋 Workflows/Orchestrators 並擴及 validate/simulate/publish;守衛不成立改 `requireLoaded` 明確報錯;conflict 出口按鈕的 `load` 失敗可見化。**更正:AgentEditor 經實作驗證無此 bug**(不走 runWithToast),以回歸斷言釘住(P1-01, P1-02)— 2026-08-03
- [x] P1-FE2 `JsonField` 受控化(`{text, error}` 由父層持有),invalid 阻擋 save/validate/publish/建立,`load()` re-seed 取代 key-remount 保留刷新行為(P1-03, P1-04)— 2026-08-03
### WP1-PLAT(dotnet-implementer)
- [x] P1-PL1 ApiError envelope 6 欄(+`code`+`correlationId`),7 出口全改、status→code 對照表兩服務逐字一致、correlationId 進 log 模板;409 於已知 ETag 路徑帶 ETag(ObjectResult 直回——UseExceptionHandler 的 ClearCacheHeaders 使例外路徑永遠帶不出 ETag);draft PUT 409 亦帶 ETag(repo 的 SELECT EXISTS 改 SELECT draft_version,零額外查詢)(P1-10)— 2026-08-03
- [x] P1-PL2 dispatch 與 designer gate 解耦(platform `Program.cs:67` **與 backend `Program.cs:42`**,後者為實作期新發現);保留 chat→dispatch、context→dispatch;admin 路由(`/api/admin/workflows*`、`/api/admin/orchestrators*` 含 test-run)仍在 designer gate 後——此為規格 §8 刻意行為,需寫入 runbook(P1-08)— 2026-08-03
- [x] P1-PL3 公開端點契約測試全面遷移到完整 envelope(`AssertApiError` helper 兩側鏡像)— 2026-08-03
### WP1-BE(dotnet-implementer)
- [x] P1-BE1 InMemory `CancelAsync` 對齊 all-or-nothing:cascade 抽共用 helper(`CascadeChildCancelLocked`)與 deadline 路徑共用防止三度 drift;任一 child 失敗 → root 零半提交 + 非 terminal `root_cancel_cascade_incomplete` 事件 + 原例外穿出(同 Dapper 語意);caller cancellation 原樣傳播;全成才 staged commit;2 個權威狀態回歸測試(P1-06, P1-07)— 2026-08-03

### P1 遺留觀察(不擋 gate,擇機處理)
- e2e 環境遺留:app 容器仍是 pre-P1 舊 image(daemon 過載致 build EOF ×2,workflow image 有建成);daemon 空閒時重跑 `docker compose build backend platform frontend` + `--profile full up -d --no-build`,並補一次瀏覽器層驗證(login、Workflows/Orchestrators view、CopilotKit sidebar);容器驗新鮮度須比對 image ID,非只看 CreatedAt
- `WorkflowRepository.UpdateDraftAsync` 對 system-owned workflow:Dapper 回 409、InMemory 回 403 —— 既有 Dapper↔InMemory 漂移,修法是判別查詢多帶 `system_owned` 一欄,但需先定對外契約(403 或 409)
- `OrchestratorsView`/`WorkflowsView` 未渲染 `revisions.error`(revision 清單載入失敗靜默變空)—— 既有讀路徑缺口,各補一個 `<ErrorText>` 即可
- designer gate 罩住 `/api/admin/orchestrators/{id}/runs`(關 designer = 關 admin test-run)為規格 §8 刻意行為 —— 待 docs 批次寫入旗標說明
### WP1-WF(python-implementer)
- [x] P1-WF1 correlation ID 機制(ASGI middleware + contextvar + 兜底 catch,罩住未修補路徑);外洩點實際 **5 處**非 3:任務原列 3 處 + flow 主路徑 `governance["error"]=str(exc)` + agentic 200 回應的 `fatal_error`/`errors`/`audit_trail`(根因修法:agentic 複用 flow 的 `PUBLIC_DENY_KEYS`);FlowDenied 保留列舉式訊息不當未預期例外(P1-05)— 2026-08-03
### WP1-INFRA
- [x] P1-IN1 compose:backend 補五 gate(經程式碼實讀逐一確認);workflow 補 CONTEXT_ENRICHMENT_ENABLED。AGENT_CHAT_ENABLED 經實讀確認 workflow 不讀取、刻意不加;workflow 原有的 inert `WORKFLOW_DESIGNER_ENABLED` 宣告一併移除(P1-08)— 2026-08-03
- [x] P1-IN2 `start-lite.ps1`/`start-lite.sh` 健康檢查失敗收集後 exit 1 並指名失敗服務(P1-09)— 2026-08-03
- [x] P1-IN3 `ensure-mem0-db.ps1` CREATE DATABASE 加 `$LASTEXITCODE` 檢查 — 2026-08-03
- [x] P1-IN4 docs 同步:`infra/AGENTS.md` gate 敘述與 compose 收斂一致(D5 的 backend gate 疊層敘述改由 P1-PL2 對應的 docs 批次處理)— 2026-08-03
### WP1-CI
- [x] P1-CI1 GitHub Actions:四服務 build/test(backend 帶 pgvector service container + psql 可達性防呆,杜絕誠實-skip 假綠)+ compose 展開驗證 + shell 語法/LF + diff hygiene + `permissions: contents: read` — 2026-08-03
- [ ] P1-CI2 契約 snapshot 測試:各服務內以 snapshot test 形式實作(platform 公開路由、backend 路由、workflow FastAPI 路由對 checked-in 清單比對),落在既有 test 步驟內(自 P1-CI1 拆出)
- [ ] P1-CI3(選配)compose env 矩陣可執行驗證:`docker compose config --format json` 對 checked-in 清單比對各服務 gate(審查 L2;成本/價值待評)
### P1 gate
- [x] P1-G 四套件全綠(backend 1243、platform 893、workflow 1643、frontend logic 115/UI 98);e2e-verifier 對 HEAD 原生程序驗證 API 層全 PASS(envelope 6 欄、409+ETag、workflow 安全 500 真實觸發、RBAC、SSE/AG-UI、202→ready、rag-qa);orchestrator ETag 同機制與 SSE error frame 為 covered-by-unit — 2026-08-03

## P2 — 遷移地基(生產 0001–0003 不出貨)
- [x] P2-1 `DbMigrationRunner`:embedded-resource manifest(LF 正規化、SHA-256、建構期拒絕非交易/交易控制語句與環境替換)、`pg_try_advisory_xact_lock` 輪詢(key 823746292,有界 5–300s、取消傳播)、鎖內四態分類、applied-row 驗證、顯式 `BundleThroughVersion` 原子 bundle + 後續一檔一交易、KnownCurrent no-op 前仍驗 postcondition、runner 寫 completion rows — 2026-08-03
- [x] P2-2 fixture migrations 五組;P2-17 三重證據(Production manifest 空、assembly 零 .sql resource、runner 不在 DI)+ 第四道保險(空 manifest 使既有 ledger 庫被 version-ahead 拒絕)— 2026-08-03
- [x] P2-3 `migrate-db.*` / `reset-development-data.*` 四支:先印精確目標、字面確認 token(Ordinal)、拒空變數/系統庫、遠端雙重確認、`.lite` 實體路徑圍堵、checkpoint db 名 regex、ALLOW_DESTRUCTIVE_MIGRATION 僅限專用行程 — 2026-08-03
- [x] P2-4 P2-01~P2-17 矩陣 + 決策表收尾測試全綠(一次性庫零殘留;fingerprint 為 versioned SQL 資產,排序全在 SQL 端 COLLATE "C");LangGraph checkpoint 四表納入 legacy 白名單(決策記於 05-ledger §1.1)— 2026-08-03
- [x] P2-5 runner 不含任何 seed;`DbBootstrap.SeedAsync` 維持生產 seed 權威且冪等(P3 再搬出為獨立命令)— 2026-08-03
- [x] P2-G backend 1308/1308(含全部 SkippableFact 真跑)、platform 893/893;code-reviewer 3 中 7 低全數修復;正常啟動路徑無任何生產 schema 變更可達 — 2026-08-03

## P3 — Reconciliation、readiness 與延後的硬切換
### P3-R0 — complete
- [x] P3-R0 authority reconciliation: `44f9de4` current contracts supersede old destructive route/alias claims; original P3 becomes deferred P3-X. Independent planning review PASS — 2026-08-06

### 下一批可執行規劃工作
- [x] P3-R1（COMPLETE）機械化 current-baseline inventory/fingerprint validation：固定 52 張 application tables、5 張可選 Workflow checkpoint tables（補列 `workflow_root_context_checkpoint`）、`conversations_history_page_idx` 與 `plpgsql`/`vector` extension allowlist；production manifest 維持 bundle 0、無 SQL。Migration suite 66 passed / 1 Docker dump-restore skipped，code review PASS — 2026-08-07。
- [ ] P3-R2（DESIGN COMPLETE，EVIDENCE PENDING）[runtime evidence contract](09-p3-r2-runtime-evidence-contract.md) 已固定 bounded telemetry、計數責任、consumer baseline、evidence bundle、觀察/rollback 與人工核准條件；尚未完成 instrumentation、production observation、外部 consumer attestation 或 C8 approval。C8 僅能移除 `/api/skills*` 的 Business Workflow 相容操作；alias 與 unified invoke 仍各自需要獨立 gate。
- [ ] P3-R3（BLOCKED until P5/C8）post-C8 target decision sheet：typed table/FK/snapshot/eval/operations identity、跨 type 同名政策、seed authority、package hash、canary retention，以及必須保留的 alias/unified-invoke facade；未凍結前不寫 SQL。

### P3-X — BLOCKED until P3-R3 and applicable route gates
- [ ] P3-X atomic destructive execution：只有 post-C8 P3-R3 與其 facade 設計適用的 route gates 通過後，才在一個 tranche 實作 production `0001`–`0003`、runner/fixture switch、完整 consumers、seed、fresh/reset fingerprint、restore 與 full-chain gates。不得 partial deploy；C8 僅能收斂 public `/api/skills*`，alias/unified invoke 必須保留或各自通過後續獨立核准。

## P4 — 單一聊天 runtime
- [ ] P4-1 chat/stream/history/AG-UI 強制 JWT;移除 body userId 與匿名連續性;`turnId` 必填 + `X-Conversation-Id`(注意 `ChatServiceTests` 匿名連續性測試同 tranche 處理)
- [ ] P4-2 Root Orchestrator 唯一路由;fail-closed 錯誤碼;刪 D6 canary 集群(AgentChatRuntime/RoutingAgent/ChatOrchestratorController/兩 flag/`agentChatEnabled`)
- [ ] P4-3 `withIsolation:true` + 刪 `ChatMemoryKeyDerivation` 匿名分支
- [ ] P4-4 `X-Client-Schema-Version` + 426 + storage schema version(全新機制)
- [ ] P4-5 Workflow 公開 engine 契約;刪 flow_harness 的 kb-query import 與 `compiler._script_contract` 私有依賴
- [ ] P4-G P4 gate:P4-01~P4-15 全過;legacy 路徑搜尋零殘留

## P5 — 內部模組化與營運硬化
- [ ] P5-1 Backend 模組拆分(command/query/coordinator)
- [ ] P5-2 Platform 組合根拆分
- [ ] P5-3 Workflow lifecycle/recovery/execution 拆分;測試 registry invocation-local(conftest `skills._SKILLS` 直寫改 fixture 隔離)
- [ ] P5-4 Frontend feature-local types/API/state 拆分
- [ ] P5-5 image digest pin(現況零 digest,含註解記錄的三個)+ 生產 profile 拒絕開發預設
- [ ] P5-G 最終 release gate(04-acceptance「Final release gate」全項)
