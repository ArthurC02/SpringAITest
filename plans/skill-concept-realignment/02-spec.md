# Skill 概念重整 — 規格

> 狀態：規劃中。名詞、決策與契約形狀的權威文件；實作細節見 03-design。

## 1. 名詞定義（正式詞彙表）

| 詞 | 定義 | 禁止用法 |
| --- | --- | --- |
| **Skill / Agent Skill** | Anthropic Agent Skill 公規包（`SKILL.md` frontmatter + body + `references/`/`assets/`/`scripts/`）。執行語意 = 漸進揭露進 Agent context（scope 掛載），或經 invoke 端點由 `agent_skill_runner` 直接執行（legacy bridge，R6 管轄）。 | 不得再指宣告式 YAML 流程 |
| **Business Workflow** | 宣告式 YAML（node/sequence/branch/loop/script/tool steps）經引擎編譯成 LangGraph 子圖的業務流程。今天的 `kind='flow'` skill。 | 不得叫 Skill；不得與 D4 Workflow 混用 |
| **Harness** | 固定執行骨架：`workflow/app/runtime/graph.py` 硬編的 LangGraph。使用者不可見不可調。 | 不得指每節點執行殼 |
| **Harness Workflow** | D4 `workflow` 表的 Graph IR 宣告（kind `agent-runtime`/`orchestrator`）：Harness 的治理上限（`maxSteps`/`maxIterations`）與形狀斷言來源，**不產生執行圖**。 | 不得當成 Business Workflow |
| **Node Shell（節點執行殼）** | 現 `workflow/app/engine/harness.py`：計時/trace、fatal 短路、未宣告寫入剝除、身分鍵防寫。包住每個節點（flow 節點與 agentic runner 都經它）。 | 不得叫 Harness |
| **Skill Engine** | `workflow/app/engine/` 的編譯與治理層。重整後其編譯職責只針對 Business Workflow；名稱過渡期保留。 | — |

## 2. 核心決策

### D-1 Agent Skill 獨占 Skill 名；Business Workflow 取得新 API 面

`/api/skills*` 的長期語意 = Agent Skill 公規包的 CRUD/import/export/revisions。Business Workflow 遷至 `/api/business-workflows*`。過渡期雙軌（見 §4）。

### D-2 邏輯拆分、物理保留：不拆 `skill`/`skill_revision` 表

**決策：`skill` 表保留為兩種 artifact 的共同儲存，`kind` 欄位是 discriminator；拆分發生在 API/domain/UI 層，不在儲存層。**

理由（依平台不變式）：
- `agent_revision_skill`（`DbBootstrap.cs:227-234`）以 FK 指向 `skill(id)`（無 revision FK，`skill_revision` 是純整數欄）；`agent_run_skill`（`:686-698`）以複合 FK 指向 `skill_revision(skill_id, revision)`。兩者分別鎖住待拆的兩張表；已發布的 Agent revision 與已執行的 run 快照是**不可變 audit 記錄**（06-cleanup 規則 4：immutable revisions/snapshots 永不因程式路徑 cleanup 刪除）。物理拆表必須改寫或雙寫這些 FK 與快照 pin，等於重寫不可變歷史。
- D3 runtime 的 execution-artifact 端點（`SkillController.cs:124-156`）與快照 pin 驗證（`workflow/app/runtime/artifacts.py:80-191`）依賴單一 revision 座標系；拆表會分裂座標系。
- 邏輯拆分已足以消除概念漂移：漂移全部發生在 API 命名、model 分派、UI 分流，沒有一項是儲存布局造成的。

代價（接受）：`skill` 表名對 DBA 而言涵蓋兩種內容；以表註解與文件說明。

### D-3 Runtime wire 契約凍結（紅線）

以下形狀在本計畫內**一位元都不改**：
- `RuntimeCommand.kind` 詞彙（`workflow/app/runtime/models.py:413-426`）：`load_skill`/`exit_skill`/`tool_call`/`read_resource`/`request_input`/`final`。
- `ActiveSkillScope`（`models.py:429-437`）、`PinnedSkillSummary`（`models.py:149-171`）、`AgentRunSnapshotBuilder` 的 skill pin 形狀（`AgentRunSnapshotBuilder.cs:184-199`）。
- checkpoint state shape 與 thread id 規則、`agent_run_skill` 列形狀。
- **run event 的 `event_type` 字串與 payload 鍵集合**：`_load_skill` 產生 `legacy_flow_completed`/`skill_scope_entered`/`skill_scope_exited`（`graph.py:638-651,674-702`），backend `AgentRunRepository.cs:16-37` 有硬編 event-type 白名單且違反時整筆 transition 被拒（`:1015-1023`）——**正名工作絕不可觸碰這些字串**，`legacy_flow_completed` 即使名字帶 legacy 也凍結。

理由：in-flight run 的 checkpoint resume、不可變快照 hash、audit replay 都以這些形狀為準。`_load_skill` 的內部重構（03-design §3.3）必須是行為保持的純程式碼整理。

### D-4 invoke 面保持統一

`POST /skills/{name}/invoke`（workflow 內部）與 platform 的 invoke 代理**不拆**。理由：
- `SkillRoutingAgent`（`platform/src/Platform.Service/SkillRoutingAgent.cs:481-510`）以 catalog 名稱為鍵、不分 kind 地呼叫 invoke；chat-skill-routing 的前提「所有可路由能力都是同構的 `{skill, output}`」在三分法下仍然成立。
- 拆兩個 invoke 端點需要路由中間件先查 kind 再選端點——多一次往返、零收益。

invoke 是「執行一個已儲存的能力 artifact」的共用面；kind 分派留在引擎內部（顯式分流，不再藏在 `compiler.compile` 裡）。

### D-5 validate 面拆分

- `POST /skills/validate`（flow-only 文字驗證）→ 正名為 `POST /business-workflows/validate`；舊路徑保留 alias 一個相容窗口。
- `POST /skills/validate-package`（zip 匯入驗證）保留於 Skill 面；flow 包匯入驗證在過渡期仍可用，P5 後 flow 包歸 Business Workflow 匯入面。
- backend `WorkflowSkillValidator`（definition-only、flow-only）隨 Business Workflow 路由遷移。

### D-6 `kind` 成為一級可信契約欄位

現況修正（審查後）：backend `SkillInfo`/`Skill` DTO **已帶** `kind`（`SkillDtos.cs:19,39`，SELECT 已取回），platform 原樣穿透 JSON——backend/platform 側是零工作，只補迴歸測試。真正殘留的嗅探在兩處：
- 引擎 `custom.py:111-120` 的 `_entry()`：明明拿到 backend list 已含 `kind` 的 `info`，卻逐筆再 GET detail 並以 `_is_agentic(definition)` 重解 YAML 決定 kind（引擎 catalog 的 kind 就是這樣產生的）。改為直接取 `info['kind']`（`input_schema` 仍需 detail，該次 GET 保留，但 kind 判定不再嗅探）。
- 前端 `SkillHome.tsx:84-85` 的 `kindOf()` 以引擎 catalog（= 上述嗅探結果）優先、backend `s.kind` 只是 fallback；`:235` 的 `openCustomEdit` 還帶 `|| isAgenticDefinition(s.definition)`。改為 API `kind` 欄位為唯一來源，刪 `isAgenticDefinition`（`agenticPackage.ts:152`，唯一呼叫端即此處）。

### D-7 命名變更（程式碼層）

| 現名 | 新名 | 範圍 |
| --- | --- | --- |
| `workflow/app/engine/harness.py` | `workflow/app/engine/node_shell.py` | 5 個 import 點：`compiler.py:30`、`script_runner.py:56`、`tool_registry.py:23`、`skill.py:25`、`runtime/legacy_flow.py:14` |
| `platform/src/Platform.Service/WorkflowService.cs` | `WorkflowEngineClient.cs`（= 「workflow 引擎服務的 client」） | 介面 `IWorkflowService` 有 9 個方法（含 D2 Business Rules 四支與 node/tool catalog，不只 skill 三支）；呼叫端含 `AgentController`/`NodeController`/`ToolController`/`SkillController`/`SkillRoutingAgent`、兩份手寫 fake（`Platform.Service.Tests/Fakes.cs`、`Platform.Web.Tests/Fakes.cs`）與 `TestWebAppFactory`。純機械但約 9 檔；對外 API 不變。不取名 `SkillEngineClient`——它同時載運 Business Rules/nodes/tools，名字比職責窄就是本計畫要消滅的那種漂移 |
| `_build_agentic_graph` 本體（藏在 compiler） | `app/engine/agent_skill_graph.py`（獨立模組） | **分派留在 `_build_graph`（`compiler.py:570-571`）一行顯式呼叫 `agent_skill_graph.build(...)`**；`compile()` 維持唯一 cached 入口（32-slot 快取掛在 `compile()`，`custom.load()` 每次 invoke 都依賴它命中——把 agentic 踢出 `compile()` 會讓 agentic 每次 invoke 重建圖，或被迫複製快取邏輯，兩者都不可接受） |
| docs 中「Skill Engine 的 skill」泛稱 | 依 §1 詞彙表分寫 | 根/區域 AGENTS.md、README、coding-standards |

D4 `workflow`/`workflow_revision` 表與 `Workflows/` feature folder **不改名**（文件正名為 Harness Workflow 即可；改實體名會動 canonical bytes/SHA 契約，成本遠超收益）。

## 3. API 契約（目標態）

### 3.1 Business Workflow（backend，經 platform 代理）

```
GET    /api/business-workflows                 # list（USER）
GET    /api/business-workflows/{name}          # detail（USER）
POST   /api/business-workflows                 # create（ADMIN；201 + Location；軟刪同名列復活並 bump revision）
PUT    /api/business-workflows/{name}          # update（ADMIN；200；不復活軟刪列）
DELETE /api/business-workflows/{name}          # soft-delete（ADMIN）
GET    /api/business-workflows/{name}/export   # zip（USER）
```

**語意 = 現行 `POST /api/skills` 與 `PUT /api/skills/{name}` 逐條照搬、只換 path prefix**：create/update 兩條分開（`CreateAsync` 的 ON CONFLICT 復活語意 vs `UpdateAsync` 的 `AND enabled`）、`ReservedNames` 撞名 409、YAML name ≠ route name 422、請求時打引擎 validate 不可達 502——全部沿用，不發明第三種語意。回應 DTO 形狀 = 現行 skill DTO（snake_case、`required_role`、`current_revision`），僅語意收斂為 `kind='flow'` 列。`simple_form` 屬於此面。

**Revisions/restore 與 kind 轉換規則**：`ImportAsync` 的 `ON CONFLICT DO UPDATE SET kind = EXCLUDED.kind` 允許同一列經 import 換 kind，且同一 skill 的 revision 歷史可混 kind（`types.ts:280` 註解已承認）。因此：
- kind 轉換**只允許發生在 Skill 面的 import**（現況唯一入口，維持）。
- `revisions`/`restore` **一律留在 Skill 面**（歷史可能混 kind，restore 已依 revision kind 分流 validator，`SkillController.cs:350-389`）；Business Workflow 面只做 current-row CRUD 與 export，不提供 revisions 路由。
- 一列被 import 成 agentic 後，從 `/api/business-workflows*` 消失（404）是預期行為。

### 3.2 Agent Skill（`/api/skills*` 收斂後）

```
GET    /api/skills                    # list（USER；過渡期含 flow 列，P5 後僅 agentic）
GET    /api/skills/{name}             # detail（USER）
POST   /api/skills/import             # zip 匯入（ADMIN；agentic 包）
POST   /api/skills/{name}/import      # 同上指名版
GET    /api/skills/{name}/export      # 原始 package bytes（USER）
GET    /api/skills/{name}/revisions   # （USER）
POST   /api/skills/{name}/revisions/{rev}/restore  # （ADMIN）
DELETE /api/skills/{name}             # （ADMIN）
# internal-only（不經 platform）：GET package、GET revisions/{rev}/execution-artifact — 形狀不變
```

`POST/PUT` 的 definition-only 寫入（flow 專屬）於 P5 起回 `410`，錯誤訊息指向 `/api/business-workflows`。

### 3.3 引擎（workflow 內部路由，無 /api 前綴）

```
GET  /skills                         # 統一 catalog（含兩種 kind；D-4 統一 invoke 的前提）
POST /skills/{name}/invoke           # 統一 invoke（形狀 {skill, output} 不變）
POST /business-workflows/validate    # flow 文字驗證（原 /skills/validate；alias 保留至 P5）
POST /skills/validate-package        # zip 驗證（不變）
GET  /nodes、GET /tools              # 不變（Business Workflow 的節點/工具目錄）
```

### 3.4 相容窗口

P2–P5 之間：`/api/skills*` 全功能雙軌（flow 讀寫照舊），`/api/business-workflows*` 同步可用；兩面對同一列的讀取回應必須逐位元一致（同一 repository）。P5 收斂由 06-cleanup C8 gate 控制：`/api/business-workflows*` 流量承接完成、舊路徑 flow 寫入 usage=0、rollback window 結束。

## 4. Agent 綁定語意（`bindable`）

現況：`bindable` = 「backend 有 persisted 正整數 `current_revision`」（`custom.py:139-141`），與 kind 無關。**本計畫不改變 bindable 判準**——Agent revision 可以同時 pin Agent Skill 與 Business Workflow（`agent_revision_skill` 對兩者一視同仁），這是 D3 runtime `_load_skill` 兩分支存在的原因。前端 Agent Builder 的綁定 UI 在 P4 起以 `kind` 分組顯示（「技能」與「業務流程」兩組），但綁定機制不變。

## 5. Eval 契約補充

審查修正：`POST /evals/run` **已經**在載入階段顯式 fail-closed——`workflow/app/evals/api.py:69-79` 對 `kind != "flow"` 的 candidate 回 422 `workflow_eval_unsupported_candidate`，不會走到編譯。層次也正確（gate 在 API 層，`_execute_case` 不需 kind 檢查）。本計畫**不改**此行為（改成 per-case ERROR 會把「請求被拒」變成「被記錄的失敗 eval run」，汙染 backend regression gate 的 fail-closed 重算；且 `EvalCaseResult` 無 `error_code` 欄位，加欄位就是 Backend wire 變更）。P3 僅補一支 pytest 釘住 422 + `workflow_eval_unsupported_candidate` 這個既有契約。eval 對 agentic 的真支援是未來另案。
