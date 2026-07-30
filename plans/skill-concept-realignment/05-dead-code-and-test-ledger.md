# Skill 概念重整 — 死碼與測試帳本

> 狀態：執行中（2026-07-30，基準 HEAD `49ead70`）。P0 的 `node_shell.py` 與 `WorkflowEngineClient` 正名已落地；其餘項目仍以各 Phase 驗收 gate 為準。本帳本是 01-plan §5 的完整展開，也是 06-cleanup C8 gate 的證據基礎：每一項死碼/重複碼/孤兒測試都登記位置、死亡 Phase、處置與風險。**執行規則：每個 Phase 的 PR 必須對照本帳本勾銷該 Phase 到期的項目；「死亡時點未到」的項目嚴禁提前刪除（雙軌期依賴它們）。P5 不得因雙軌路由已存在而勾銷：必須先有新面流量承接、舊面 flow-write usage `= 0`、rollback window 結束、deletion evidence 與人工簽核。**

## 1. workflow（Python）

### 1.1 死碼

| 項目 | 位置 | 死亡 Phase | 處置 | 風險備註 |
| --- | --- | --- | --- | --- |
| `_is_agentic()` | `app/skills/custom.py:60-70` | P3（P1 先拔 `_entry` 呼叫、P3 拔 `load()` 呼叫） | 刪除 | 呼叫端經查證僅 `custom.py:115`、`custom.py:164` 兩處。**P1–P3 之間同檔兩種 kind 判定並存是預期中繼態，P1 不可先刪** |
| `_entry()` 中僅為 kind 而做的 YAML 解析 | `app/skills/custom.py:101-144`（`_is_agentic`/`parsed.kind` 兩行） | P1 | 改讀 `info['kind']`；**detail GET 本身不刪**（`input_schema` 仍需 definition） | ⚠️ `FakeSkillBackend._info()`（`tests/test_skills_custom.py:119-127`）與 `row()` helper（`:130-145`）**沒有 `kind` 欄**——P1 不同批補 fixture，`test_custom_catalog_declares_flow_and_agentic_kind`（`:208-227`）落地當下即紅 |
| `engine/harness.py` 舊名 | `app/engine/harness.py` | P0 | 改名 `node_shell.py`；5 個生產 import + **5 個測試 import**（`test_agent_skill_runner.py:15`、`test_engine_guardrails.py:26`、`test_engine_harness.py:10`、`test_engine_script_steps.py:14`、`test_kbquery_nodes.py:11`）；`test_engine_harness.py` 檔名同步改 `test_engine_node_shell.py` | 漏改任一測試 import = collection error |

### 1.2 重複碼與架構債

| 項目 | 位置 | 性質 | 處置 |
| --- | --- | --- | --- |
| kind 判定雙軌（DB 欄 vs YAML 嗅探） | `custom.py:60-70` vs `info['kind']` | P1/P3 分兩批收斂為單一事實來源 | 見 1.1 |
| 兩個 package 讀取端點的 revision-pin 落差 | `app/skills/package_reader.py:22-43`（`BackendPackageReader.read(name)` **永遠抓當前版**，供 `/skills/{name}/invoke` 直接編譯）vs `app/runtime/artifacts.py:215-233`（`RevisionArtifactReader` 依 pin 讀不可變快照，供 Harness 掛載） | **架構債，本計畫不動**（`artifacts.py` 在 D-3 紅線內） | 登記：同一 agentic skill 經 Harness 掛載是 revision-pinned、經直接 invoke 讀最新版。若要收斂另立案 |
| `AGENTIC_REQUIRES_IMPORT` 的 API 層 + 引擎層雙重拒絕 | backend 新 BusinessWorkflow 面（P2 起）+ `engine/skill.py:551-559` | P2 起的**刻意縱深防禦，不是待收斂的重複** | 兩層都保留；01-plan §5 原「收斂為端點層拒絕」的說法已修正——引擎層 fail-closed 防線不刪，語意從主防線降為縱深防禦 |

### 1.3 測試

| 類別 | 測試 | 處置 |
| --- | --- | --- |
| (a) P0/P1 必同批改 | 5 個 `from app.engine.harness import` 測試（見 1.1）；`test_skills_custom.py` 的 fake fixture 補 `kind` 欄 | 與生產碼同 PR |
| (a) docstring 更新 | `test_engine_skill_validation.py:449-467`（`test_definition_declaring_agentic_kind_rejected` 等） | **斷言保留**（縱深防禦仍在），P2/P3 後補註「主防線已移至 API 層」 |
| (b) P5 整檔可刪 | **無**——P5 收斂是 backend 層動作，workflow 53 個測試檔無一因 P5 失去意義 | — |
| (c) P5 明確保留 | `/skills/validate` 與 `/business-workflows/validate` 同 handler alias 的 API 測試 | P5/C8 不刪；alias 退場須另立 consumer inventory、usage-zero、rollback gate 與驗收 |
| (c) 升級為紅線迴歸（凍結斷言） | `test_agent_runtime.py:1199-1292`（`active_skill_scope` 形狀 + `skill_scope_entered` event/payload 鍵——現成 golden，P3 後標註斷言值不可改）；`test_agent_runtime_legacy_flow.py:158`（`legacy_flow_completed` 字串，backend 白名單對應的唯一 Python 釘子）；`test_agent_skill_runner.py:178-200,390-417`（invoke `{skill, output}` 形狀 + audit 終端，驗證 P3 搬家不改行為的黑盒）；`test_engine_skill_validation.py` AT-GOV-02 區塊（compiler 防呆對照組） | 不可修改斷言值，只能改實作 |
| 不刪（R6 擁有） | `test_agent_runtime_legacy_flow.py`、`test_agent_skill_runner.py` 全檔 | 本案不動 |

## 2. backend（.NET）

### 2.1 死碼

| 項目 | 位置 | 死亡 Phase | 處置 | 風險備註 |
| --- | --- | --- | --- | --- |
| `SkillController.Create`/`Update`（flow definition-only） | `Skills/SkillController.cs:159-182,189-225` | P5（410） | 語意遷 `BusinessWorkflowController`，原 action 改 410 | P2–P5 雙軌期是唯一寫入口，不可提前刪 |
| `SkillController.RejectAgenticDefinitionOnly` | `SkillController.cs:451-459` | P5 | 唯一呼叫端是 `Update`（`:197`），隨之孤兒化，一併刪 | 漏刪留未使用 private 方法 |
| `SkillExporter.ToZip` 的 Skill 面 fallback 呼叫點 | `SkillController.cs:80` | P5（與 410 同批） | 只死這一個呼叫點；`ToZip` **類別本體不死**（Business Workflow 面 export 沿用） | — |
| `WorkflowSkillValidator` 舊位置 | `Skills/WorkflowSkillValidator.cs` | P2（**搬遷非死亡**） | 移入 `BusinessWorkflows/` 並改打 `/business-workflows/validate`；**同一 commit 刪舊檔** | 只加新檔忘刪舊檔 = 兩個同名類別的意外重複 |

### 2.2 重複碼

| 項目 | 性質 | 處置 |
| --- | --- | --- |
| Create/Update 路由層：`SkillController` vs `BusinessWorkflowController` | **P2 刻意暫時重複，P5 收斂**（02-spec §3.1 明文照搬語意） | 只複製 controller action 層；`ReservedNames` 抽共用常數（03-design 已列）、validator/repo 走同一實例。**若 domain 邏輯也被複製，P5 收斂必漏改** |
| `WorkflowSkillValidator` vs `WorkflowSkillPackageValidator` 的成功契約檢查重疊 | 既有重疊（name 非空/kind 值域/required_role 檢查交集），**計畫原文未列** | 登記入 C8：抽共享檢查至 `Common/` 或接受並註記；防未來 role 值域改動只改一邊 |
| Dapper ↔ InMemory 對偶：BusinessWorkflow 面需要 kind 過濾的方法對 | P2 起，漏一邊即 lite 模式行為分岔 | 必同批：`ListAsync`（`SkillRepository.cs:37-48` / `InMemorySkillRepository.cs:28-40`）、`GetAsync`（`:50-56` / `:42-50`，**02-spec「import 成 agentic 後從新面 404」的不變式由它單點保證**）、`DeleteAsync`（`:181-189` / `:174-186`）。`CreateAsync`/`UpdateAsync` 已硬編 `kind='flow'`，**不需**額外同步 |

### 2.3 測試

| 類別 | 測試 | 處置 |
| --- | --- | --- |
| (a) P1 補釘 | `SkillsApiTests.Get_ResponseFields_AreSnakeCase`（`:257-281`）補斷言 `kind` 欄（= A1-1） | 補一行 |
| (a) P2 強制連動 | `SkillValidatorTests` 全類 8 法（`:22-132`）——URL 斷言打 `/skills/validate`，validator 搬家改打 `/business-workflows/validate` 後全紅 | 測試檔隨源碼同 PR 搬遷 + 改斷言 |
| (a) P2 新增 | `SkillRepositoryTests` 補 kind-filter 變體（agentic 列不出現在過濾後結果） | 新增非重寫 |
| (b) P5 搬遷 | `SkillsApiTests` 約 20 個 flow 寫入測試（`Post_CallsWorkflowValidate...:48-76` 起，逐一清單見盤查記錄）→ 搬 `BusinessWorkflowControllerTests` 改路徑；原檔留 `Post_FlowWrite_Returns410...`（= A5-1） | — |
| (b) P5 重寫 | `SkillsApiTests` 的 flow fixture **讀取**測試（`Get_ByName...:228-240`、`CrossTenant...:285-309` 等 7 項）——P5 後 flow 列從 `/api/skills` 消失，語意失真 | 改 agentic fixture 重寫或搬遷；**已補驗收 A5-3（Skill 面讀取可見性收斂）** |
| (b) P5 搬遷 | `SkillExportTests` flow 匯出格式 10 項（`:79-334`）→ 隨 A5-2 搬 BusinessWorkflow export 測試；`ToZip` 純函式測試留原檔 | — |
| (b) P5 改斷言 | `SkillImportTests.DefinitionOnlyUpdate_OfAgenticSkill_Rejected...:476-496`、`DefinitionOnlyCreate_AgenticKind_Rejected:505-527`——測的正是 P5 死亡的拒絕邏輯 | 改斷 410 或刪 |
| (c) 凍結（全程不得改斷言） | `SkillExecutionArtifactTests` 全 5 法（D3 wire 紅線）；`AgentRunRepositoryTests` 中 event-type 白名單相關（`legacy_flow_completed`/`skill_scope_entered`/`skill_scope_exited`）；`SkillImportTests` Restore 系列 7 法（`:683-878`，**永久留 Skill 面**，02-spec §3.1 revisions/restore 歸屬）；`InMemorySkillRepositoryConcurrencyTests`（repo 層，與 controller 拆分無關） | 升級為契約迴歸釘子 |
| 特殊：雙軌一致性測試自身 | P2 新建（建議名 `SkillsBusinessWorkflowsDualTrackConsistencyTests`） | **生命週期綁定雙軌期：P2 生、P5 死**——收斂後它斷言的等價關係不存在，P5 必刪，登記於此防止被當契約測試保留 |

## 3. platform（.NET）

### 3.1 死碼

| 項目 | 位置 | 死亡 Phase | 處置 | 風險備註 |
| --- | --- | --- | --- | --- |
| `SkillController` 的 Create/Update/Validate 代理 action | `Platform.Web/Controllers/SkillController.cs:147-153,155-159,169-172` | P5 | **原 03-design 遺漏，已補**：backend 410 後這三個 action 只是穿透 410 的殭屍代理，P5 同批刪除 | 不刪會誤導後續開發者以為 platform 仍支援 flow 寫入 |

### 3.2 `WorkflowService`→`WorkflowEngineClient` 改名完整觸及面（P0）

盤查實測 **17 檔**（03-design 原估 9 檔只算了介面注入點）：

- 原始碼 8：`WorkflowService.cs`、`Abstractions/IWorkflowService.cs`、`SkillController.cs`、`AgentController.cs`、`NodeController.cs`、`ToolController.cs`、`SkillRoutingAgent.cs`、`Program.cs`（DI）
- fake/factory/被測類 4：`Platform.Service.Tests/Fakes.cs:162-254,458-508`、`Platform.Web.Tests/Fakes.cs:213-331`、`TestWebAppFactory.cs:117-118`、`WorkflowServiceTests.cs`（19 法，檔名同步改 `WorkflowEngineClientTests.cs`）
- 純引用 `FakeWorkflowService` 型別名 5：`ChatSkillRoutingTests.cs`、`ChatServiceTests.cs`、`ChatBehaviorBaselineTests.cs`、`ChatContextProviderAndRecorderTests.cs`、`CopilotAguiApiTests.cs`

改名 PR 收尾必跑一次 `IWorkflowService`/`FakeWorkflowService` 全文搜尋核對 17 檔全覆蓋。

## 4. frontend

### 4.1 死碼

| 項目 | 位置 | 死亡 Phase | 處置 | 風險備註 |
| --- | --- | --- | --- | --- |
| `isAgenticDefinition` | `skills/agenticPackage.ts:152-154`（唯一呼叫端 `SkillHome.tsx:235`） | P1 | 刪函式與 import | 低 |
| `openCustomEdit` 的 kind 分派分支 | `SkillHome.tsx:218-244` | P1（刪嗅探子句）+ **P4（結構性死亡）** | P4 兩入口分家後單一入口只遇單一 kind，整個 if/else 退化——**必須真拆掉，不是留一個永遠走一邊的 if** | 中：03-design 原文未明講，易留死分支 |
| `kindOf()` 的 fallback 鏈 | `SkillHome.tsx:84-85`（`?? fallback ?? 'flow'`） | P1（kind 轉必填後不可達） | 摺成直讀 `s.kind`/`c.kind` | 計畫原文未提的衍生死碼 |
| `revision.ts` 的 `?? 'flow'` 防呆 | `skills/revision.ts:41-42` | P1（同上） | 同批清掉 | **計畫原文完全沒提的新發現**，漏掉留誤導性防呆碼 |
| `api/skills.ts` 的 `createSkill`/`updateSkill`/`validateSkill` | `api/skills.ts:46-51,54-63,116-121` | P4（隨呼叫端 `SimpleSkillEditor`/`AdvancedSkillEditor` 整包搬遷） | 遷 `api/businessWorkflows.ts`；呼叫端經查證 100% 是兩個編輯器的 onSave，無第三方 | 低，純搬遷 |
| `types.ts` 過期註解 | `types.ts:313`（不存在的 `skillKind()`）、`:296-316`（`SkillCatalogEntry.kind` doc）、`:280`（混 kind 註解） | P1 | 隨 kind 明文化整段重寫 | 文件債 |
| `ConfigView` Tab 型別與註解 | `ConfigView.tsx:15,13` | P4 | Tab 拆兩 id + 註解改「Business Workflow 管理區」 | 機械 |
| CopilotKit readable 文案 | `AppShell.tsx:333`（「Skill 編輯在系統設定 › Skill」） | P4 | 改寫為兩入口敘述 | ⚠️ **餵 LLM 的 context 字串，lint/build/測試都攔不到**，漂移只會讓 Copilot 誤導使用者——明列 P4 checklist |

### 4.2 重複碼（P4 拆分紀律）

| 項目 | 位置 | 規則 |
| --- | --- | --- |
| DTO 型別 | `types.ts:253-329` | 新 `api/businessWorkflows.ts` **必須 import 現有型別，禁止**另建 `BusinessWorkflow*` 平行型別（backend 同一張表 → 前端單一事實來源） |
| **併發/回溯狀態機（全案最高風險複製點）** | `SkillHome.tsx:66-78`（`mountedRef`/`skillRequestGenerationRef`/`restorePendingRef`）+ `:117-161,199-343`（約 180 行：世代計數、mounted guard、restore 原子快照） | **必須抽共用 hook（如 `useSkillSelection`）再拆入口**；複製兩份 = 兩套會各自漂移的併發保護 |
| `Row` 合併與 `template-*` 過濾 | `SkillHome.tsx:32-43,80-112` | 抽參數化 `useSkillRows(kindFilter)` 或共用合併函式，禁止整段複製改一行 filter |
| `fetch`/toast helper | `api/http.ts`、`Toast.tsx` | 已是全站單例，直接復用（列出防呆） |
| export/import/delete 薄包裝 | `SkillHome.tsx:164-196` | 3–10 行 toast 包裝，兩入口各持可接受；`importSkill` 天然只留技能入口 |
| `SkillRunPanel`/`SkillHistory`/`TraceView` | props 只吃 `name`/`inputSchema`/`output`，對 kind 無感（已驗證） | 共用同一份，不拆不複製 |

### 4.3 測試/檢查

- **現況零覆蓋**：Skill 領域無任何 `*.spec.ts`（`frontend/tests/` 17 檔皆與此無關）。
- **P4 實質 gate（已補入 A4-6）**：新增 1 支 Playwright mocked-browser spec（比照 `navGating.ui.spec.ts` 風格）釘住：兩入口清單互斥、上傳鈕只在技能入口、restore/history/run 子流程在拆分後仍可達（正是 4.2 狀態機最易悄悄壞掉又無測試攔截的部分）。
- `scripts/agenticPackage.selfcheck.ts` 存在但未接入 `package.json` 任何 script——本計畫未使其惡化，選擇性掛入（非必須項）。
- 文件同步：`frontend/AGENTS.md` 的 ConfigView 三分頁敘述（P4 後失真）；`.claude/agents/e2e-verifier.md:21,24` 的 `/api/skills` 端點敘述（**P5 才需改**，雙軌期不動）；根 `AGENTS.md:11` 核心四 view 敘述**不需改**（Config 仍是同一頂層 view）。

## 5. 誤判澄清區（看似死碼但不是——嚴禁誤刪）

| 項目 | 位置 | 為什麼不是死碼 |
| --- | --- | --- |
| `SkillController.ValidateAsync` | `SkillController.cs:415-430` | P5 後仍被 `Restore` 的 flow 分支呼叫（`:378`）；呼叫點從 3 減 1，不歸零 |
| `SkillController.ToSkill` | `SkillController.cs:472-482` | Import/Restore 仍用 |
| `WorkflowSkillPackageValidator` | 全檔 | agentic 匯入驗證器，永久留 Skill 面（02-spec D-5） |
| `SkillRepository.CreateAsync`/`UpdateAsync` + InMemory 對偶 | `SkillRepository.cs:58-127` 等 | 方法不死，只是呼叫者從 SkillController 換成 BusinessWorkflowController |
| `AGENTIC_REQUIRES_IMPORT` 路徑 + 其測試 | `engine/skill.py:47,551-559`；`test_engine_skill_validation.py:449-467` | 縱深防禦，fail-closed 保底不刪（見 1.2） |
| `skill_from_agentic_meta` | `engine/package.py:669` | P3 後仍有兩個呼叫端：`load_agent_skill()` 與 `package._parse_agentic()`（`:823`） |
| `MemoryPackageReader` | `engine/package_reader.py:32-53` | 測試替身非孤兒；與 `BackendPackageReader` 是刻意的 port/adapter 分層 |
| workflow `/skills/validate` handler | `main.py:405-423` | P5 收斂的是 backend `/api/skills*` 寫入面；workflow 端 alias 並存，本計畫不下架（真要清需先確認 backend validator 已全面切新路徑，另立 C8 子項） |
| `legacy_flow.py`、`agent_skill_runner.py` 及其測試 | — | R6 擁有，本案只改歸屬文件 |
| `SkillExporter.ToZip` 類別本體 | `SkillExporter.cs` | P5 只死 Skill 面呼叫點；Business Workflow 面 export 沿用本體 |

## 6. 本帳本對計畫文件的回饋（已同批修訂）

1. 01-plan §5 的 `AGENTIC_REQUIRES_IMPORT` 列「收斂為端點層拒絕」→ 修正為「保留為縱深防禦」（§1.2）。
2. 03-design §3.2 補 platform 三個代理 action 的 P5 刪除（§3.1）。
3. 03-design §1.2 的「約 9 檔」→ 實測 17 檔（§3.2）。
4. 03-design §4 補：拆分目標檔名（`BusinessWorkflowHome.tsx`/`AgentSkillHome.tsx`）、共用 hook 抽取紀律、`AppShell.tsx:333`、`revision.ts` fallback。
5. 04-acceptance 補：A1 fixture 同批修、A4-6 Playwright gate、A5-3 Skill 面讀取可見性收斂、雙軌一致性測試 P5 必刪。
6. 03-design §3.3/§3.4 的依賴切分實作出一個計畫原文未列的新生產檔：`app/engine/graph_primitives.py`（kind-neutral 建圖原語：`build_state_schema`/`AUDIT_NODE`/`SkillCompileError`/`add_contract_node`），現由 `compiler.py` 與 `agent_skill_graph.py` 共同依賴。存在理由：讓 `agent_skill_graph.py` 不必 import compiler internals，滿足 §3.4「依賴方向規則」（避免 Agent Skill bridge 反向依賴 Business Workflow schema/compiler，形成循環依賴）；行為與原 compiler.py 內對應邏輯等價，非新增或變更行為。
