# Architecture Hard Reset — Implementation Todo List

> **維護規則(強制):** 每完成一項工作,實作該項的變更必須在**同一個 commit** 將對應條目從 `- [ ]` 改為 `- [x]`,條目尾端註記完成日期(commit hash 由該條目的 git 歷史可溯,不必手寫)。任何 session 開始實作前先讀本檔確認下一個未完成項;發現與現況不符的條目,先修正條目再動工。Phase gate 條目未全勾之前,不得開始下一 phase 的破壞性工作(P1 內六個工作包可並行)。
>
> Baseline: commit `8652528`(2026-08-03),四服務測試基準見 P0-B4。

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
- [ ] P1-FE1 `runWithToast`/conflict 共通層修 409 假成功,覆蓋 Workflows/Orchestrators/AgentEditor 三編輯器(P1-01, P1-02)
- [ ] P1-FE2 `JsonField` 改受控 `{text, parsedValue, error}`,invalid 阻擋 create/save/validate/publish,保留 reload 後刷新行為(P1-03, P1-04)
### WP1-PLAT(dotnet-implementer)
- [ ] P1-PL1 ApiError envelope 加 `code` + `correlationId`,7 個出口全改(含 `ChatOrchestratorController` 裸 NotFound),backend `Common/` 鏡像同步(P1-10)
- [ ] P1-PL2 dispatch 與 designer gate 解耦(`Program.cs:67`),驗證 E1/E3 三層鏈行為,對齊 02-spec §8(P1-08 相關)
- [ ] P1-PL3 公開端點契約測試全面遷移到完整 envelope
### WP1-BE(dotnet-implementer)
- [ ] P1-BE1 InMemory `CancelAsync` 對齊 `ExpireLockedAsync`:先 cascade、逐 child try/catch、caller cancellation 傳播、全成才落 terminal(P1-06, P1-07)
### WP1-WF(python-implementer)
- [ ] P1-WF1 新建 correlation ID 機制;3 處 `str(e)` 外洩點改固定安全訊息 + log 落例外(P1-05)
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
- [ ] P1-G 全部既有測試 + 各包新增回歸測試綠;e2e-verifier 跨服務鏈路通過

## P2 — 遷移地基(生產 0001–0003 不出貨)
- [ ] P2-1 `DbMigrationRunner`:資源探索、checksum、advisory lock、交易(03-design §1.2)
- [ ] P2-2 測試用 fixture migrations;確認生產 hard-reset SQL 不在註冊 manifest(P2-17)
- [ ] P2-3 守護式 `migrate-db.*` / `reset-development-data.*`(印出精確 host/db/project、確認 token、拒絕空變數與系統庫)
- [ ] P2-4 P2-01~P2-16 安全矩陣 against 一次性 PostgreSQL 全綠(含 fingerprint 比對 P2-13、snapshot/restore P2-14)
- [ ] P2-5 seed 獨立於 schema migration 且可重跑冪等
- [ ] P2-G P2 gate:安全套件全綠;正常啟動路徑無任何生產 schema 變更可達

## P3 — Schema 與 artifact 硬切換(單一 tranche;先決策後動工)
### 前置規格決策(定案回寫 02-spec 後才可動工)
- [ ] P3-D1 `skill.simple_form` 去向(含 `SkillDtos.cs:23,48,62` wire 欄位)
- [ ] P3-D2 `GET /skills` catalog 拆或不拆(前端三處無條件呼叫點的遷移方案)
- [ ] P3-D3 一份 execution snapshot 能否同時 pin 兩種 artifact(決定 `PinnedSkillSummary`/`ActiveSkillScope` 拆分形狀)
- [ ] P3-D4 `SkillMetadata` 歸屬(保留側 `ISkillPackageValidator` 同時使用的衝突)
### Tranche 分支內順序(每段 commit 必須可編譯)
- [ ] P3-1 runner + 生產 0001–0003 SQL bundle + lock/checksum(含 P3-00 重放 P2 矩陣)
- [ ] P3-2 fixture 切換到 runner + 一次性開發者 migrate 步驟(14 個 Postgres 測試檔、32 處 RunAsync 直呼、刪 DbBootstrap)
- [ ] P3-3 Backend schema/repositories/controllers/FK 重建(50 表 allowlist;SkillHash 搬遷 24 檔 10 模組;SkillNameRules 含 DbBootstrap 使用點)
- [ ] P3-4 Workflow 路由與 loader 拆分(agent-skills/business-workflows 四路由;kind dispatch 五縫;Harness kind routing)
- [ ] P3-5 Platform services/controllers(SkillDtos 拆兩 record family;刪 /api/skills/validate;BusinessWorkflow proxy 補齊)
- [ ] P3-6 Frontend types/API/元件拆分(types.ts Skill* 家族;三處 catalog 呼叫點)
- [ ] P3-7 測試清理 + 文件同步(刪 dual-track/410/alias 測試;seed 命令含 SeedAsync 全量內容 + D5 Root/Worker/Verifier/binding)
- [ ] P3-G P3 gate:fresh/reset fingerprint 相等;P3-01~P3-11 全過;e2e-verifier 通過

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
