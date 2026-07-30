# Skill 概念重整 — 驗收案例

> 狀態：規劃中。每階段的可驗證退出條件；「命令」欄為驗證入口，細部斷言寫在對應測試檔。

## P0 正名（零行為變更）

| # | 案例 | 驗證 |
| --- | --- | --- |
| A0-1 | `engine/node_shell.py` 改名後三服務全測綠 | `cd workflow && uv run pytest`；`cd backend && dotnet test`；`cd platform && dotnet test` |
| A0-2 | repo 內不再有 `from app.engine.harness import` | 符號搜尋為零命中（`app.engine.node_shell` 取代） |
| A0-3 | platform `SkillEngineClient` 改名後對外 HTTP 契約不變 | platform 既有 SkillRoutingAgent/Skill proxy 測試不改斷言全綠 |
| A0-4 | 文件修訂完成：`coding-standards.md:21`、根 `AGENTS.md` Skill Engine 段、`05-migration-and-rollout.md:193` NodeParamsTab 列、06-cleanup 新增 C8 | 人工審查：文件中「Skill 是宣告式 YAML workflow」無限定語句零殘留 |

## P1 kind 貫通

| # | 案例 | 驗證 |
| --- | --- | --- |
| A1-1 | `GET /api/skills`（backend/platform）每筆帶 `kind`——既有行為，補迴歸測試釘住 | backend xUnit：flow 與 agentic 列各一筆，斷言 kind 值 |
| A1-2 | 引擎 `custom._entry` 的 kind 來自 backend list 的 `info['kind']`，`_is_agentic` 不再用於 catalog kind 判定；**`test_skills_custom.py` 的 `FakeSkillBackend._info()`/`row()` fixture 同批補 `kind` 欄**（不補則既有 catalog 測試落地即紅） | workflow pytest：fake backend 回 `kind='agentic'` 但 definition 偽裝成 flow YAML，catalog 仍回 agentic |
| A1-3 | 前端 `kindOf` 以 backend `s.kind` 為唯一來源；`isAgenticDefinition` 已刪除 | 符號搜尋零命中 + lint/build 綠 |
| A1-4 | 舊資料（kind 欄預設 `flow`）在 list 中正確標示 | xUnit：不帶 kind 寫入的既有列 list 回 `flow` |

## P2 API 拆分（雙軌）

| # | 案例 | 驗證 |
| --- | --- | --- |
| A2-1 | 同一 flow 列經 `/api/skills/{name}` 與 `/api/business-workflows/{name}` 讀取，回應 JSON 逐位元一致 | backend xUnit 契約測試 |
| A2-2 | 經 `/api/business-workflows/{name}` PUT 更新的 flow，`/api/skills` list 可見且 revision 正確 bump | backend xUnit（同 repo、同 revision 序列） |
| A2-3 | 新面 POST/PUT 語意與舊面逐條一致：POST 201+Location、POST 復活軟刪同名列並 bump revision、PUT 不復活（404）、`ReservedNames` 409、YAML name ≠ route name 422 | backend xUnit（五條各一案例） |
| A2-4 | `/api/business-workflows/{name}` 寫入在引擎不可達時回 `502`（沿用既有語意） | backend xUnit（fake validator 模擬不可達） |
| A2-5 | agentic 列對 `/api/business-workflows/{name}` 讀取回 `404`；flow 列被 Skill 面 import 成 agentic 後從新面消失（404），其 revision 歷史仍由 Skill 面 `revisions`/`restore` 提供 | backend xUnit（kind 轉換規則，02-spec §3.1） |
| A2-6 | 引擎 `/business-workflows/validate` 與 `/skills/validate` 同 handler 同回應 | workflow pytest |
| A2-7 | platform `/api/business-workflows*` 代理逐路由通、角色閘與 `/api/skills*` 同款（寫 ADMIN、讀 USER） | platform 測試 |
| A2-8 | 跨租戶隔離不回歸：tenant A 的 business workflow 對 tenant B 是 404 | backend xUnit |

## P3 引擎拆分（行為保持）

| # | 案例 | 驗證 |
| --- | --- | --- |
| A3-1 | `_load_skill` 重構前後：flow pin 與 agentic pin 執行的 state 更新鍵集合與值語意逐鍵相等，**含 events 的 `event_type` 字串（`legacy_flow_completed`/`skill_scope_entered`/`skill_scope_exited`）與 payload 鍵集合** | workflow pytest golden test（重構前先落 golden） |
| A3-2 | `compiler.compile` 對 agentic 照舊命中快取回圖（分派移至 `agent_skill_graph.build` 後行為不變）；`tests/test_agent_skill_runner.py:113` 既有測試不改斷言全綠 | workflow pytest |
| A3-3 | `POST /skills/{name}/invoke` 對 flow 與 agentic 的 `{skill, output}` 回應形狀與重構前一致 | workflow pytest（既有 invoke 測試不改斷言） |
| A3-4 | `custom.load()` 分派依 DB 列 kind，`_is_agentic` YAML 嗅探已刪除 | 符號搜尋零命中 + pytest |
| A3-5 | eval 既有 422 gate 釘住：agentic candidate → 422 `workflow_eval_unsupported_candidate`（`evals/api.py:69-79`，行為不變） | workflow pytest（新增測試，非新行為） |
| A3-6 | checkpoint 相容：interrupt → reopen → resume 跨兩個 saver 實例照舊通過 | `CHECKPOINT_DATABASE_URL=... uv run pytest tests/test_agent_runtime_postgres.py tests/test_root_orchestrator_supervisor.py`（手動，真 PostgreSQL） |
| A3-7 | D3 runtime 快照 pin 驗證（definition_sha256/package_sha256 不匹配即拒）不回歸 | 既有 runtime 測試全綠 |

## P4 前端 IA

| # | 案例 | 驗證 |
| --- | --- | --- |
| A4-1 | 兩個平級入口：業務流程區列 flow、技能區列 agentic，互不出現 | e2e-verifier（Playwright）：登入 → 兩區清單斷言 |
| A4-2 | `NodeParamsTab` 分頁標籤正名後功能照舊（讀寫 configuration_set；掛載位置本已是平級分頁，不動） | e2e-verifier |
| A4-3 | Agent Skill 上傳 zip → 出現在技能區；flow YAML 建立 → 出現在業務流程區 | e2e-verifier |
| A4-4 | `SkillRunPanel` 在兩區都能試跑（invoke 統一面） | e2e-verifier |
| A4-5 | Agent Builder 綁定 UI 以 kind 分組顯示，綁定行為不變（bindable 判準不變） | 前端測試 + e2e |
| A4-6 | 新增 Playwright mocked-browser spec（比照 `navGating.ui.spec.ts`）：兩入口清單互斥、上傳鈕只在技能入口、restore/history/run 子流程在共用 hook 抽取後仍可達 | `frontend/tests/` 新 spec，P4 的實質 gate |
| A4-7 | `AppShell.tsx:333` CopilotKit readable 文案已改寫為兩入口敘述 | code review checklist（自動檢查攔不到） |

## P5 收斂（受 06-cleanup C8 gate）

| # | 案例 | 驗證 |
| --- | --- | --- |
| A5-1 | `/api/skills` 的 flow definition-only Create/Update 回 `410`，訊息指向 `/api/business-workflows`；platform 的 Create/Update/Validate 代理 action（`SkillController.cs:147-172`）同批刪除 | backend + platform xUnit |
| A5-2 | Skill 面 export 的 `SkillExporter.ToZip` fallback 收斂（definition-only flow 匯出僅由 Business Workflow 面提供）——與 410 同批，不得早於 P5 | backend xUnit |
| A5-3 | Skill 面**讀取**可見性收斂：`GET /api/skills` list/detail 不再回 flow 列；受影響的 flow-fixture 讀取測試（帳本 §2.3 (b) 清單）已改 agentic fixture 或搬遷 | backend xUnit |
| A5-4 | 雙軌一致性測試（P2 建的 `SkillsBusinessWorkflowsDualTrackConsistencyTests`）**已刪除**——它斷言的等價關係在收斂後不存在，不得被當契約測試保留 | 符號搜尋 |
| A5-5 | gate 證據齊備：新面流量承接、舊面 flow 寫入 usage=0、rollback window 結束 | C8 登記的 usage query + 人工簽核 |
| A5-6 | [05-dead-code-and-test-ledger.md](05-dead-code-and-test-ledger.md) 逐項勾銷；誤判澄清區（帳本 §5）10 項未被誤刪；`legacy_flow.py`/`agent_skill_runner.py` 未被本案觸碰 | code review + 符號搜尋 |

## 全程不變式（每階段回歸）

| # | 不變式 | 驗證 |
| --- | --- | --- |
| I-1 | `RuntimeCommand.kind` 詞彙、`ActiveSkillScope`、snapshot skill pin 形狀、`agent_run_skill` 列形狀、run event `event_type` 字串（含 `legacy_flow_completed`，backend `AgentRunRepository.cs:16-37` 白名單）零變更 | 既有 runtime/snapshot 測試不改斷言全綠 + A3-1 golden |
| I-2 | chat skill routing 行為不變（catalog 名稱路由、雙 kind 統一 invoke） | platform ChatSkillRouting 測試全綠 |
| I-3 | 不可變 revision/快照/audit 永不改寫 | A2-1/A3-7 + code review |
| I-4 | `/api/skills*` 與 `/api/business-workflows*` 都不掛 feature flag（維持現況恆開） | platform 路由測試 |
