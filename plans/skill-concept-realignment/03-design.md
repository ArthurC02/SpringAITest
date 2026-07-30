# Skill 概念重整 — 設計

> 狀態：規劃中。依 02-spec 決策展開各服務實作設計；行號依 `feat/dotnet-backend` HEAD（`49ead70`）。

## 1. P0 正名與文件（零行為變更）

### 1.1 workflow：`engine/harness.py` → `engine/node_shell.py`

純改名 + import 更新，符號名（`harnessed`、`describe`、`record`、`IMMUTABLE_KEYS`、`IDENTITY_KEYS`、`CONFIG_SEED_KEYS`、`RUNTIME_AUTHORITY_KEYS`）不變。觸及：

- `workflow/app/engine/compiler.py:30`
- `workflow/app/engine/script_runner.py:56`
- `workflow/app/engine/tool_registry.py:23`
- `workflow/app/engine/skill.py:25`
- `workflow/app/runtime/legacy_flow.py:14`

模組 docstring 更新為「Node Shell：節點執行殼」，並註明「Harness 指 `runtime/graph.py` 固定骨架」。`runtime/legacy_flow.py` 跨層 import `RUNTIME_AUTHORITY_KEYS` 的現況保留（治理鍵單一事實來源仍在 node_shell），不做套件搬移——搬 `app/harness/` 的收益是純美學，成本是更大 diff。

### 1.2 platform：`WorkflowService` → `WorkflowEngineClient`

`IWorkflowService` 有 9 個方法：skill 三支（invoke/validate/catalog）+ D2 Business Rules 四支（facts/actions/validate/simulate）+ node/tool catalog——它是整個 Python 引擎（:8001）的 client，不只 skill，故名 `WorkflowEngineClient`（不叫 `SkillEngineClient`，名字比職責窄就是本計畫要消滅的漂移）。改名觸及面經盤查實測為 **17 檔**（完整清單見 [05-dead-code-and-test-ledger.md](05-dead-code-and-test-ledger.md) §3.2）：原始碼 8（介面/實作/DI/五個呼叫端）+ fake/factory/被測類 4（含 `WorkflowServiceTests.cs` 19 法連檔名改）+ 純引用 `FakeWorkflowService` 型別名的測試檔 5。純機械、對外 HTTP 契約零變更；收尾必跑 `IWorkflowService`/`FakeWorkflowService` 全文搜尋核對。

### 1.3 文件修訂清單

| 檔案 | 修訂 |
| --- | --- |
| 根 `AGENTS.md:47`（Skill Engine 段） | 依 02-spec §1 詞彙表改寫：flow → Business Workflow；agentic → Agent Skill；補「Harness = runtime 固定骨架；Node Shell = 節點執行殼」 |
| `docs/coding-standards.md:21` | 「Skill 永遠是宣告式 YAML 組合」→「**Business Workflow** 永遠是宣告式 YAML 組合；Agent Skill 是 SKILL.md 公規包，不是 YAML 流程」 |
| `README.md:355` 技能段 | 補 Agent Skill 包的說明；現有五內建 skill 敘述正名為 Business Workflow |
| `plans/agent-platform-redesign/05-migration-and-rollout.md:193` | `NodeParamsTab` 自 flow 作者 UI 列移除（它是 Harness Configuration Set 配置）；補 agentic 編輯器（`AgentSkillEditor`）不在退場清單的說明 |
| `plans/agent-platform-redesign/05-migration-and-rollout.md:197-198` | 「legacy flow compiler/harness」表述正名（compiler 屬 Business Workflow 引擎；harness 一詞改 node shell）；TraceView 收斂條款補 agentic trace 的歸屬決定 |
| `plans/agent-architecture-improvements/06-cleanup-and-consolidation.md` | 新增 C8 列：本計畫 P5 收斂（前置 gate：雙軌流量承接、flow 寫入 usage=0、rollback window） |
| `plans/chat-skill-routing/01-plan.md` 頂部狀態區 | 補一句「路由不區分 kind；invoke 形狀由引擎保證一致」 |
| `workflow/AGENTS.md`、`backend/AGENTS.md`、`platform/AGENTS.md`、`frontend/AGENTS.md` | 隨各階段交付同步（docs-updater），P0 先改詞彙 |

## 2. P1 `kind` 貫通

審查修正後的實況：backend `SkillInfo`/`Skill` DTO **已帶** `kind`（`SkillDtos.cs:19,39`）、platform 原樣穿透 JSON、引擎 `GET /skills` 回應也有 kind 欄——但引擎那個 kind 是 `custom.py:111-120` `_entry()` 逐筆 GET detail 後用 `_is_agentic(definition)` **嗅探 YAML** 得來的，backend list 的 `info['kind']` 被無視。真正要做的：

- 引擎：`custom._entry()` 的 kind 判定改讀 `info['kind']`；detail GET 保留（`input_schema` 仍需 definition），但不再為了 kind 解 YAML。`custom.load()` 的分派同步改讀 DB 列 kind（P3 完整落地，P1 先做 `_entry`）。
- frontend：`SkillHome.tsx:84-85` `kindOf()` 的優先序反轉——backend `s.kind` 為唯一來源；`:218-244` `openCustomEdit` 刪 `|| isAgenticDefinition(s.definition)`；`agenticPackage.ts:152` 的 `isAgenticDefinition` 唯一呼叫端即此處，直接刪除。`types.ts:263` 的 `SkillInfo.kind?` 轉必填（`types.ts:315` 的 `SkillCatalogEntry.kind` 是另一個端點的 DTO，隨引擎修正一併轉正）。
- backend/platform：零程式碼變更，各補一支迴歸測試釘住 list 帶 kind。

## 3. P2/P3 服務拆分

### 3.1 backend（P2）

新 feature folder `BusinessWorkflows/`：
- `BusinessWorkflowController`：02-spec §3.1 路由（POST/PUT 語意逐條照搬現行 Skill 面，只換 prefix；**不提供 revisions 路由**——歷史可混 kind，revisions/restore 留在 Skill 面，見 02-spec §3.1 kind 轉換規則）。實作委派**同一個** `SkillRepository`（filter `kind='flow'`，現有方法無 kind 參數者補查詢條件）——不建第二張表、不複製 CTE；`ReservedNames` 名單（`SkillController.cs:30-35`）移到共用常數。
- `WorkflowSkillValidator`（definition-only、flow-only，`WorkflowSkillValidator.cs`）遷入此 folder，改打引擎新路徑 `/business-workflows/validate`。
- `SkillExporter` 的 flow 現場組包分支（`SkillExporter.cs:23`）：Business Workflow 面的 export 直接用它。**Skill 面 `SkillController.cs:80` 的 `skill.Package ?? SkillExporter.ToZip(skill)` fallback 在 P2 不動**（definition-only flow 列的 package 是 NULL，砍掉 fallback 會讓舊面匯出壞掉，違反 §3.4 全功能雙軌）；收斂移到 P5 與 410 同批。
- `SkillController` 過渡期不動（雙軌）；P5 對 flow 寫入回 `410`。
- `agent_revision_skill`/`agent_run_skill`/`AgentRunSnapshotBuilder`/`AgentRepository.ResolveReferences`：**全部不動**（02-spec D-3）。

### 3.2 platform（P2）

- 新 `BusinessWorkflowController` 代理（CRUD/export → backend；validate → 引擎 `/business-workflows/validate`；multipart/錯誤映射 helper 與既有 `SkillController` 共用，不複製）。
- 既有 `SkillController` 過渡期保留全功能。**P5 收斂時其 Create/Update/Validate 三個代理 action（`SkillController.cs:147-172`）與 backend 410 同批刪除**——否則變成穿透 410 的殭屍代理層（盤查補洞項）。
- `/api/business-workflows*` 與 `/api/skills*` 同樣不掛 feature flag（現況 `/api/skills*` 恆開，`Program.cs:479-491,575-590` 的 gate 不涉及）。

### 3.3 workflow（P3）

- `custom.py:147-201` 的 `load()` 拆成 `load_business_workflow()`（validate_source + parse_source + compile）與 `load_agent_skill()`（`skill_from_agentic_meta` + compile 的 agentic 路徑）；公用 `load()` 依 DB 列的 kind 分派（一行 if，取代 `_is_agentic` YAML 嗅探——嗅探刪除，P1 已先處理 `_entry`）。
- `compiler.py`：`_build_agentic_graph` **本體**（`:501-565`）移至新模組 `app/engine/agent_skill_graph.py`（依賴不變：`AGENT_RUNNER_NODE`、`harnessed`、audit 附加）；**分派留在 `_build_graph`（`:570-571`）**，改成一行顯式 `return agent_skill_graph.build(skill, deps)`。`compile()` 維持唯一 cached 入口、不 raise、cache key 零改動——審查確認 32-slot 快取只掛在 `compile()`，`custom.load()` 每次 invoke 靠它命中（`custom.py:43-44` 明文 <10ms 預算）；把 agentic 踢出 `compile()` 會讓 agentic 每次 invoke 重建圖或被迫複製快取。呼叫端全數不變：`evals`、`legacy_flow`、`custom`、`app/skills/__init__.py:109`（內建載入）、`main.py:529`（per-config 重編）、`tests/test_agent_skill_runner.py:113`（直接對 agentic 呼叫 compile 的測試**照舊通過**）。
- `main.py`：`/skills/{name}/invoke` 不動（kind 分派已在 compile 內解決）；`/skills/validate` 與 `/business-workflows/validate` 維持同 handler alias。**P5/C8 不刪 workflow-internal `/skills/validate`**；它的退場必須另立 consumer inventory、usage=0、rollback window 與驗收 gate。
- eval：**不改行為**——`evals/api.py:69-79` 已對 `kind != "flow"` 回 422 `workflow_eval_unsupported_candidate`（層次正確：gate 在 API 層）。只補一支 pytest 釘住此既有契約（02-spec §5）。
- `runtime/graph.py:585-712`：`_load_skill` 抽出 `_invoke_business_workflow(state, runtime, artifact, command)`（現 flow 分支 `:597-652`；需要 `command.arguments` 作 `raw_input`）與 `_enter_skill_scope(state, runtime, artifact)`（現 agentic 分支 `:653-712`；不需 command）兩個模組級函式；`_load_skill` 本體變成 command/pin 解析 + 一行分派。**節點名、command、state 寫入形狀、event_type 字串與 payload 鍵逐鍵不變**（02-spec D-3 紅線，含 `legacy_flow_completed`——backend `AgentRunRepository.cs:16-37` 白名單硬編此字串）；以既有 runtime 測試 + golden state-diff + checkpoint round-trip 測試護行為。
- `artifacts.py`、`models.py`、`legacy_flow.py`：不動。

### 3.4 依賴方向規則（引擎內部）

拆分後的 import 界線：`app/engine/compiler.py` 的宣告式 YAML schema/建圖職責只服務 Business Workflow；它只在顯式 kind 分派點 import/call `agent_skill_graph.build(...)`，以共用既有 `compile()` 快取並維持統一 direct-invoke。`agent_skill_graph` 是 Agent Skill bridge graph factory，可 import node_shell/tool_registry/node_registry，但不得 import Business Workflow schema/compiler internals，也不得讓 Agent Skill 被描述成 Business Workflow。`app/runtime/` 對兩者的依賴只允許經 `legacy_flow`（現況）與 artifact 讀取，不新增。

## 4. P4 前端資訊架構

- 頂層兩個平級入口（目標檔名定死，防止被實作成 SkillHome 內多包一層 if）：
  - **業務流程 `BusinessWorkflowHome.tsx`**：繼承 `SkillHome` 的 flow 部分——`SimpleSkillEditor`、`AdvancedSkillEditor`、`YamlEditor`、`NodeCatalog`；API 呼叫面切到 `/api/business-workflows*`。
  - **技能 `AgentSkillHome.tsx`**：`AgentSkillEditor` 升為獨立入口；上傳/下載沿用 `importSkill`/`exportSkill`/`getSkillPackage`（天然 agentic 形狀）。
- **拆分紀律（盤查補洞，違反即複製出會漂移的雙份邏輯）**：`SkillHome.tsx:66-78,117-161,199-343` 的併發/回溯狀態機（世代計數、mounted guard、restore 原子快照，約 180 行）**必須先抽成共用 hook（`useSkillSelection`）再拆入口**；`Row` 合併與 `template-*` 過濾抽 `useSkillRows(kindFilter)`；DTO 一律 import 現有 `types.ts` 型別，禁建 `BusinessWorkflow*` 平行型別。`openCustomEdit` 的 kind 分派 if/else 在拆分後要真的刪除，不是留一個永遠走一邊的分支。
- 衍生死碼同批清：`kindOf()` fallback 鏈（`SkillHome.tsx:84-85`）、`revision.ts:41-42` 的 `?? 'flow'`（kind 轉必填後不可達）、`types.ts:296-316` 過期註解。
- **`AppShell.tsx:333` 的 CopilotKit readable 文案**（「Skill 編輯在系統設定 › Skill」）改寫為兩入口敘述——這是餵 LLM 的 context 字串，lint/測試攔不到，明列 P4 checklist。
- `NodeParamsTab`：審查修正——它**已經**是與 `SkillHome` 平級的設定分頁（`ConfigView.tsx:173-175`），不在 Skill 樹裡；P4 只做分頁標籤正名（「執行參數／Harness 節點參數」），概念歸屬修正靠 P0 修訂 `05-migration-and-rollout.md:193` 的誤列。
- `SkillRunPanel`、`SkillHistory`、`TraceView` 保持共用（對 kind 無感是正確的，不拆）。
- `api/skills.ts` 拆 `api/businessWorkflows.ts`（create/update/validate 遷移）；`createSkill`/`updateSkill` 在 Skill 面刪除（本來對 agentic 無意義）。
- 無 router 無 UI 库的憲法不變：入口切換沿用現有 view-state 模式。

## 5. 測試策略

- **契約雙軌一致性（P2）**：backend xUnit——同一列經 `/api/skills/{name}` 與 `/api/business-workflows/{name}` 讀取，回應逐位元一致；flow 經新面寫入後舊面可讀（同 repo 保證）。
- **行為保持（P3）**：workflow pytest——`_load_skill` 重構前後，flow pin 與 agentic pin 的 state diff 逐鍵相等（既有 runtime 測試已覆蓋大半；補一支 golden test 固定兩分支的輸出鍵集合）；`compiler.compile` 對 agentic 的防呆 raise；invoke 端點對兩 kind 的 `{skill, output}` 形狀不變。
- **checkpoint 相容（P3）**：跑 `CHECKPOINT_DATABASE_URL` 那組手動測試（`tests/test_agent_runtime_postgres.py` — 預設 skip、無 CI 覆蓋，是本計畫觸碰 runtime 前的必跑項）。
- **前端（P1/P4）**：lint/build + 手測；P4 另有實質 gate——新增 1 支 Playwright mocked-browser spec（比照 `navGating.ui.spec.ts` 風格）釘住兩入口清單互斥、上傳鈕只在技能入口、restore/history/run 子流程仍可達（Skill 領域現況零測試覆蓋，狀態機拆分是最易悄悄壞掉的部分）；e2e-verifier 驗主流程。
- **文件（每階段）**：docs-updater 同步各 AGENTS.md；審查點——文件不得再出現「Skill 是宣告式 YAML workflow」的無限定語句。

## 6. 風險與緩解

| 風險 | 緩解 |
| --- | --- |
| `_load_skill` 重構破壞 checkpoint resume | D-3 紅線 + golden state-diff 測試 + 手動 PostgreSQL checkpoint 測試 |
| 雙軌期間兩面寫入互踩 | 同一 repository、同一 CTE、同一 revision 序列——雙軌只是路由別名，無第二寫入路徑 |
| `compiler.compile` 60 in-degree 改動波及 | 簽章、分派、快取全不變；只把 `_build_agentic_graph` 本體搬到新模組、`_build_graph` 一行顯式呼叫——所有呼叫端（evals/legacy_flow/custom/內建載入/per-config 重編/測試）零調整 |
| 前端 kind 欄位缺值（舊資料） | backend list 一律帶 kind（DB 欄 NOT NULL 有預設 flow）；前端不需 fallback 嗅探 |
| 名稱過渡期溝通混亂 | P0 先改文件與詞彙表；程式碼註解引用 02-spec §1 |
