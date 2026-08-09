# Skill 概念重整（Concept Realignment）— 計畫

> **Superseded P5 policy (2026-08-02):** P5 的雙軌、410 與 C8 gate 已由 [Architecture Hard Reset](../architecture-hard-reset/01-plan.md) 的直接 schema/API cutover 取代；P0–P4 與本文件的歷史理由保留。現行程式仍是 P0–P4 狀態，直到 hard-reset P3 實作並通過 gate。

> 狀態：P0–P4 已交付（2026-08-09 confirmed；程式碼佐證：`backend/src/Backend.Api/BusinessWorkflows/BusinessWorkflowController.cs`、`workflow/app/engine/agent_skill_graph.py`、`frontend/src/components/AgentSkillHome.tsx`）。P5 **未完成**，且不得在 cleanup C8 的流量、usage、rollback 與簽核證據齊備前宣告完成，見 [06-cleanup-and-consolidation.md](../agent-architecture-improvements/06-cleanup-and-consolidation.md) 的 C8 gate。本計畫是概念/命名/API 邊界的重整，不是新功能；現況以程式碼為準。

## 1. 動機：Re-Architecture 後的概念漂移

系統經過 Agent 平台重整（D1–D7）後，執行模型已經變成「**Harness（固定 LangGraph 骨架）+ 動態載入的能力**」，但程式碼與 API 仍殘留舊概念「**Skill == Workflow**」（Skill 是一張可執行的宣告式流程圖）。具體證據：

- `workflow/app/runtime/graph.py:585-712` 的 `_load_skill` 一個節點兩種語意：`kind=='flow'` 分支同步跑完整段子圖（`invoke_pinned_legacy_flow`）；`kind=='agentic'` 分支只掛載漸進揭露 scope（`ActiveSkillScope`），實際執行留給後續 ReAct 迴圈。兩種控制流完全不同的行為共用同一個 command（`load_skill`）、同一個 tool name。
- （歷史）曾有 `legacy_flow.py` 的 4 節點白名單，已於 `02dde09` 被 Harness 治理統一取代為 `flow_harness.py`；flow skill 現可依 effective-tool 治理執行，非結構性跑不動。
- `workflow/app/engine/compiler.py:501-565` 的 `_build_agentic_graph` 把符合 Anthropic Agent Skill 公規的 SKILL.md 包**反向包裝成單節點 LangGraph**，只為了共用 `/skills/{name}/invoke`；公規的東西被塞進工作流的殼，方向剛好相反。
- `workflow/app/skills/custom.py:147-201` 的 `load()` 在單一函式內重現兩條完全不同的載入管線（agentic 繞過 `validate_source`、flow 走靜態驗證），只因兩者共享 `Skill` model 與 `LoadedSkill` 回傳型別。
- backend 一張 `skill` 表（`backend/src/Backend.Api/Data/DbBootstrap.cs:110-158`）混裝兩種本質不同的內容；`SkillRepository` 的 Create/Update 寫死 `kind='flow', package=NULL`，Import 才允許 agentic——**寫入路徑本身已經分岔，概念層卻沒跟上**。
- 命名撞車：backend 已有一張 `workflow` 表，但那是 D4 的 **Harness 宣告**（kind 只允許 `agent-runtime`/`orchestrator`，`WorkflowCanonicalizer.cs:16-17`），不是業務流程；platform 的 `WorkflowService.cs` 打的其實是 Python 引擎的 `/skills/*` 端點；`workflow/app/engine/harness.py` 不是 Harness 骨架、是每節點執行殼，卻與真正的 Harness（`runtime/graph.py` 硬圖）撞名。

## 2. 目標概念模型（三分法）

| 概念 | 定義 | 今天住在哪 | 重整後 |
| --- | --- | --- | --- |
| **Agent Skill** | Anthropic Agent Skill 公規：`SKILL.md` + frontmatter + `references/`/`assets/`/`scripts/` 的能力包；由 Agent 在執行期**漸進揭露**進 context，自行決定怎麼用。不是可執行圖。 | `skill` 表 `kind='agentic'` + `package` bytes；`_load_skill` 的 agentic 分支（已正確實作） | **獨占 `Skill` 這個名字**與 `/api/skills*` 語意 |
| **Business Workflow** | 宣告式 YAML 定義、由引擎編譯成 LangGraph 子圖的業務流程（今天的 `kind='flow'` skill）。 | `skill` 表 `kind='flow'` + `engine/skill.py`/`compiler.py`；新 runtime 全拒 | 自己的名字與 API 面（`/api/business-workflows*`）；儲存沿用（見 02-spec D-2） |
| **Harness** | 使用者不可見、不可調整的固定執行骨架：`runtime/graph.py` 硬編 LangGraph（14 節點）+ 每節點治理執行殼。D4 `workflow` 表是它的**宣告與治理上限**（`maxSteps`/`maxIterations`），不產生執行圖。 | `runtime/graph.py` + `engine/harness.py`（撞名）+ D4 `workflow` 表 | `Harness` 一詞保留給固定骨架；`engine/harness.py` 改名為節點執行殼（node shell）；D4 表文件正名 **Harness Workflow** |

三者關係：Harness 是唯一的執行容器；Agent Skill 以 scope 掛載進 Harness 的 context；Business Workflow 以受控橋接（現為 `legacy_flow` 安全子集，長期由 R6 另案決定）進入 Harness、或經 `/skills/{name}/invoke` 直接執行。

## 3. 範圍

**做：**
1. 正名與文件對齊（零行為變更）：docs、AGENTS.md、既有計畫的錯誤引用、`engine/harness.py` 與 platform `WorkflowService` 改名。
2. `kind` 貫通契約：catalog/list 回應明確攜帶可信 `kind`，前端停止以 definition 字串嗅探。
3. API 邏輯拆分：backend/platform 新增 Business Workflow 路由面；`/api/skills*` 語意收斂為 Agent Skill；過渡期相容。
4. 引擎內部拆分：`custom.load()` 分成兩條載入管線；`_build_agentic_graph` 本體移出 compiler 成獨立模組（分派留在 `_build_graph` 一行顯式呼叫，`compile()` 簽章/快取/呼叫端零變更）；`_load_skill` 抽出兩個具名 helper；eval 的 422 fail-closed gate 已存在（`evals/api.py:69-79`），僅補測試釘住。
5. 前端資訊架構重組：Business Workflow 管理區與 Agent Skill 區成為兩個平級入口；`NodeParamsTab` 已是平級設定分頁（`ConfigView.tsx:173-175`），僅正名標籤 + 修訂 R6 清單誤列。
6. 死碼盤點與清理登記（06-cleanup 的 C8）。

**不做（非目標）：**
- 不動 D3/D5/D7 runtime 的 wire 契約：`RuntimeCommand.kind` 詞彙、`ActiveSkillScope`/snapshot pin 形狀、checkpoint state shape 一律不變（不可變快照、audit replay、in-flight checkpoint 相容是紅線）。
- 不拆 `skill`/`skill_revision` 實體表（理由見 02-spec D-2）。
- 不讓 flow skill 在新 runtime 跑起來——該白名單機制已被 `flow_harness.py` 取代（`02dde09`），非本案改動，亦非仍待 R6（`agent-platform-redesign/05-migration-and-rollout.md`）處理的既有物。
- 不做租戶建立（CreateTenant）與預設種子——另案，且依賴本計畫先定名。
- 不新建第二套編輯器/schema/runtime（遵守 `plans/README.md:35` 紅線）。
- 不動 D4 `workflow`/`workflow_revision`/`orchestrator` 表的實體名稱與契約。

## 4. 階段總覽

| 階段 | 內容 | 風險 | Gate |
| --- | --- | --- | --- |
| P0 | 正名與文件（零行為變更）：docs/計畫修訂、`engine/harness.py`→`engine/node_shell.py`（5 個生產 import + 5 個測試 import）、platform `WorkflowService`→`WorkflowEngineClient`（完整觸及面 17 檔，含介面、DI、呼叫端、fakes/factory 與測試檔改名） | 低（純機械，已落地；文件持續同步） | 三服務測試全綠 |
| P1 | `kind` 貫通：backend/platform 已帶 kind（僅補迴歸測試）；引擎 `custom._entry` 改讀 `info['kind']` 刪 YAML 嗅探；前端 `kindOf` 優先序反轉、刪 `isAgenticDefinition` | 低 | 前端與引擎無任何 definition 嗅探判 kind |
| P2 | API 拆分：backend `BusinessWorkflowController`（`/api/business-workflows*`，POST/PUT 語意照搬、無 revisions 路由）+ platform 代理 + workflow `/business-workflows/validate` alias；`/api/skills*` 過渡期全功能雙軌 | 中 | 新舊路由回應一致（契約測試） |
| P3 | 引擎拆分：`custom.load()` 分裂、`_build_agentic_graph` 本體移出（分派/快取/簽章不變）、`_load_skill` helper 抽取、eval 422 契約補測試 | 中 | workflow 全測綠 + invoke 行為位元級不變 |
| P4 | 前端 IA：兩個平級入口 + `NodeParamsTab` 標籤正名 | 低 | lint/build 綠 + e2e 主流程 |
| P5 | 收斂：關閉 `/api/skills*` 的 flow 寫入路徑（410）、刪死碼 | 中 | 06-cleanup C8 gate（usage=0、rollback window 過） |

P0/P1 可並行；P2 依賴 P1；P3 與 P2 可並行（不同服務）；P4 依賴 P1+P2；P5 依賴全部且受 C8 gate。

## 5. 死碼與重複盤點（coding-standards 要求）

> **完整帳本在 [05-dead-code-and-test-ledger.md](05-dead-code-and-test-ledger.md)**：三個 codebase 的死碼、雙軌期刻意重複、孤兒測試（含逐測試方法清單）、凍結測試、誤判澄清區（看似死碼但嚴禁誤刪的 10 項）。本表只留概念級摘要；執行時以帳本為準，每個 Phase 的 PR 對照帳本勾銷。

| 項目 | 位置 | 何時死亡 | 處置 |
| --- | --- | --- | --- |
| definition 嗅探 `_is_agentic()` | `workflow/app/skills/custom.py:60-70` | P1（`_entry` 改讀 `info['kind']`）/P3（`load()` 分派改讀 DB 列） | 刪除 |
| 前端 `isAgenticDefinition` 路由判斷 | `frontend/src/skills/agenticPackage.ts:152`（唯一呼叫端 `SkillHome.tsx:235`） | P1（API 帶可信 kind） | 直接刪除（無其他用途） |
| compiler 內 agentic 建圖實作 | `workflow/app/engine/compiler.py:501-565`（`_build_agentic_graph` 本體；分派在 `_build_graph` `:570-571`） | P3 | 本體移至 `agent_skill_graph.py`；分派保留為一行顯式呼叫（快取與呼叫端不動） |
| `AGENTIC_REQUIRES_IMPORT` 驗證錯誤路徑 | `workflow/app/engine/skill.py:47,551-559` | **不死**（盤查修正） | 保留為縱深防禦：P2 起 API 層先擋，引擎層 fail-closed 保底不刪；測試斷言保留、docstring 補註 |
| platform `WorkflowService` 名稱 | `platform/src/Platform.Service/WorkflowService.cs` | P0 | 改名 `WorkflowEngineClient`（它載運 skill+Business Rules+node/tool catalog，不叫 SkillEngineClient） |
| `engine/harness.py` 名稱 | `workflow/app/engine/harness.py` | P0 | 改名 `node_shell.py`；「Harness」保留給 `runtime/graph.py` |
| `/api/skills*` 的 flow Create/Update 路徑 | `backend/src/Backend.Api/Skills/SkillController.cs:159-225`、`SkillRepository.cs:58,99` | P5（雙軌收斂後） | 遷移至 BusinessWorkflowController，原路徑 410 |
| `SkillExporter` flow 分支 | `backend/src/Backend.Api/Skills/SkillExporter.cs` | P2（隨 flow 匯出遷移） | 隨 Business Workflow 路由遷移，不刪功能 |
| R6 清單將 `NodeParamsTab` 誤列為 flow 作者 UI | `plans/agent-platform-redesign/05-migration-and-rollout.md:193` | P0 | 修訂該列（NodeParamsTab 是 Harness 配置，不隨 flow 編輯器退場） |
| `flow_harness.py`（原 `legacy_flow.py`） | `workflow/app/runtime/flow_harness.py` | 已於本案範圍外的 `02dde09` 統一治理 | 非本案改動；本文件路徑引用需更新為 `flow_harness.py` |
| `agent_skill_runner.py` | `workflow/app/nodes/agent_skill_runner.py` | **不在本案刪除** | R6 擁有；本案只修正歸屬文件 |

## 6. 與既有計畫的關係

- `agent-skill-standard`：本計畫的直接前身；其「agentic = 受 Harness 管理的單節點」定位是當時刻意的最小落地，本計畫把它升級為平級概念。其 kind 對照表沿用。
- `settings-skill-redesign`：其「Skill 功能樹」心智模型在本計畫後重定位為「Business Workflow 管理區」；`NodeParamsTab` 移出該樹。
- `chat-skill-routing`：路由不分 kind、以 catalog 名稱為鍵、統一走 invoke——**前提在三分法下仍成立，不需改行為**；僅在文件補一句避免誤讀。
- `agent-platform-redesign` R6：legacy flow 退場門檻不變、owner 不變；本計畫修訂 05 文件第 193 行的分類錯誤與第 197-198 行的表述。
- `agent-architecture-improvements/06-cleanup-and-consolidation.md`：新增 C8（本計畫的收斂與死碼刪除）。

> 死碼/重複碼/孤兒測試完整帳本與誤判澄清區交叉參照：[05-dead-code-and-test-ledger.md § 5 誤判澄清區](05-dead-code-and-test-ledger.md#5-誤判澄清區看似死碼但不是嚴禁誤刪)。

## 附錄

### §A 02-spec 核心決策併入（併自 02-spec.md，2026-08-09 整併）

> Superseded 註記：02-spec 舊 §3.1「revisions/restore 一律留在 Skill 面、Business Workflow 面不提供 revisions 路由」的歸屬決策已由 [Architecture Hard Reset §02-spec](../architecture-hard-reset/02-spec.md) 取代 —— hard-reset 為 Business Workflow 建立獨立 revision 表與 `/api/business-workflows*` revisions 路由，以 hard-reset 為準。（此為 `architecture-hard-reset/07-p3-inventory.md:42` 的 2026-08-05 預決,原擬加在 02-spec 上,現落於此。）

以下為 02-spec 的核心決策，原文逐字保留（僅編號沿用來源檔）：

#### D-2 邏輯拆分、物理保留：不拆 `skill`/`skill_revision` 表

**決策：`skill` 表保留為兩種 artifact 的共同儲存，`kind` 欄位是 discriminator；拆分發生在 API/domain/UI 層，不在儲存層。**

理由（依平台不變式）：
- `agent_revision_skill`（`DbBootstrap.cs:227-234`）以 FK 指向 `skill(id)`（無 revision FK，`skill_revision` 是純整數欄）；`agent_run_skill`（`:686-698`）以複合 FK 指向 `skill_revision(skill_id, revision)`。兩者分別鎖住待拆的兩張表；已發布的 Agent revision 與已執行的 run 快照是**不可變 audit 記錄**（06-cleanup 規則 4：immutable revisions/snapshots 永不因程式路徑 cleanup 刪除）。物理拆表必須改寫或雙寫這些 FK 與快照 pin，等於重寫不可變歷史。
- D3 runtime 的 execution-artifact 端點（`SkillController.cs:124-156`）與快照 pin 驗證（`workflow/app/runtime/artifacts.py:80-191`）依賴單一 revision 座標系；拆表會分裂座標系。
- 邏輯拆分已足以消除概念漂移：漂移全部發生在 API 命名、model 分派、UI 分流，沒有一項是儲存布局造成的。

代價（接受）：`skill` 表名對 DBA 而言涵蓋兩種內容；以表註解與文件說明。

#### D-3 Runtime wire 契約凍結（紅線）

以下形狀在本計畫內**一位元都不改**：
- `RuntimeCommand.kind` 詞彙（`workflow/app/runtime/models.py:413-426`）：`load_skill`/`exit_skill`/`tool_call`/`read_resource`/`request_input`/`final`。
- `ActiveSkillScope`（`models.py:429-437`）、`PinnedSkillSummary`（`models.py:149-171`）、`AgentRunSnapshotBuilder` 的 skill pin 形狀（`AgentRunSnapshotBuilder.cs:184-199`）。
- checkpoint state shape 與 thread id 規則、`agent_run_skill` 列形狀。
- **run event 的 `event_type` 字串與 payload 鍵集合**：`_load_skill`/`_invoke_business_workflow` 產生的 event_type 為 **`workflow_completed`**（非 `legacy_flow_completed`——該字串已於 `02dde09` 統一治理時淘汰，並立回歸測試 `AgentRunEventContractTests.cs` 禁止復現）/`skill_scope_entered`/`skill_scope_exited`（`graph.py:638-651,674-702`），backend `AgentRunRepository.cs:16-37` 有硬編 event-type 白名單且違反時整筆 transition 被拒（`:1015-1023`）——此三字串為凍結範圍，不含已淘汰的 `legacy_flow_completed`。

理由：in-flight run 的 checkpoint resume、不可變快照 hash、audit replay 都以這些形狀為準。`_load_skill` 的內部重構（03-design §3.3）必須是行為保持的純程式碼整理。

**執行記錄（P0，審查認可，非改名產物）**：`node_shell.py`（原 `harness.py`，D-7 改名）本輪新增剝除 `RUNTIME_AUTHORITY_KEYS`（`run_id`/`agent_id`/`agent_revision`/`knowledge_sources`/`enforce_data_scope`，`node_shell.py:49-55`）的邏輯——這些鍵正是本節 `AgentRunSnapshotBuilder` skill pin 形狀經 legacy-flow adapter 注入 state 的同一批鍵；舊 `harness.py` 只剝 `IMMUTABLE_KEYS`，從未真正落實其「must never be overwritten」的既有承諾（見 `node_shell.py:45-48` 註解）。此修補堵住「未宣告 writes 契約的 node/匿名節點可偽造 D3 授權鍵」的殘餘路徑（tool 的 `save_as` 在 compile 期已擋、script 在 `script_runner` 的 `FORBIDDEN_WRITE_KEYS` 已擋，此為第三處、也是最後一處缺口），三支測試覆蓋：`test_engine_node_shell.py::test_node_and_tool_shell_outputs_cannot_replace_runtime_authority`、`test_engine_script_steps.py::test_script_cannot_replace_runtime_authority_seen_by_downstream_node`、`test_engine_tool_registry.py::test_tool_step_cannot_save_into_runtime_authority_channel`。這是 P0 執行過程中順手修補的既有安全缺口，屬**計畫外但經審查認可**的行為變更：本節凍結的是「形狀」（鍵存在與意義不變），這裡新增的是「防寫執行」，兩者不衝突。

#### D-6 `kind` 成為一級可信契約欄位（現況說明）

現況修正（審查後）：backend `SkillInfo`/`Skill` DTO **已帶** `kind`（`SkillDtos.cs:19,39`，SELECT 已取回），platform 原樣穿透 JSON——backend/platform 側是零工作，只補迴歸測試。真正殘留的嗅探在兩處：
- 引擎 `custom.py:111-120` 的 `_entry()`：明明拿到 backend list 已含 `kind` 的 `info`，卻逐筆再 GET detail 並以 `_is_agentic(definition)` 重解 YAML 決定 kind（引擎 catalog 的 kind 就是這樣產生的）。改為直接取 `info['kind']`（`input_schema` 仍需 detail，該次 GET 保留，但 kind 判定不再嗅探）。
- 前端 `SkillHome.tsx:84-85` 的 `kindOf()` 以引擎 catalog（= 上述嗅探結果）優先、backend `s.kind` 只是 fallback；`:235` 的 `openCustomEdit` 還帶 `|| isAgenticDefinition(s.definition)`。改為 API `kind` 欄位為唯一來源，刪 `isAgenticDefinition`（`agenticPackage.ts:152`，唯一呼叫端即此處）。

### §B 03-design 依賴方向規則摘要（併自 03-design.md，2026-08-09 整併）

拆分後的引擎內部依賴界線：`agent_skill_graph` 是 Agent Skill bridge graph factory，可 import node_shell/tool_registry/node_registry，但**不得 import Business Workflow 的 schema/compiler internals**，也不得讓 Agent Skill 被描述成 Business Workflow（`app/engine/compiler.py` 的宣告式 YAML schema/建圖職責只服務 Business Workflow，只在顯式 kind 分派點呼叫 `agent_skill_graph.build(...)`）。

### §C 04-acceptance-tests 防呆規則併入（併自 04-acceptance-tests.md，2026-08-09 整併）

workflow-internal `/skills/validate` alias（與 `/business-workflows/validate` 同 handler）**不得被 P5/C8 自動刪除**；其退場必須另立 consumer inventory、usage-zero 與 rollback gate 的獨立驗收，不得借既有收斂批次順手移除。
