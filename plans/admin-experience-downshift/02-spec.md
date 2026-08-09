# 規格書 — P1 管理面降階

> 對應 [01-plan.md](01-plan.md) 五個 workstream 的逐項規格。每節格式:現況(引實際檔案與文案原文)/ 目標行為 / 不做什麼 / 舊碼盤點 / 契約守則。所有「現況」皆已對照原始碼複核,行號可能隨後續改動漂移,但引用文字為當時原文。

## W1 — Approvals 佇列化與中文化

### 1.1 現況

`frontend/src/components/ApprovalInbox.tsx`(74 行,全元件)是 D7 寫入核准的唯一 USER-safe 前端入口,由 `AppShell.tsx` 在 `flags.agentWriteToolsEnabled` 為真時掛載到側欄「Approvals」。逐句核對原文:

- 標題與說明(第 55-56 行):`<h2>Run approvals</h2>` / `<p className="muted">Enter a run ID you are allowed to review. Sensitive inputs, tool arguments, secrets, and anti-replay tokens are never displayed.</p>` —— 整段英文,且「anti-replay tokens」是安全術語直接入文案。
- 唯一輸入動線(第 57-58 行):`<label htmlFor="approval-run-id">Run ID</label><input .../>` + `<button ...>Load approvals</button>`——**沒有任何清單或搜尋入口**,核准者必須已經知道要核准哪個 run 的 GUID。
- 決策按鈕(第 68-69 行):`Approve` / `Reject`,卡片欄位 `Required role` / `Expires` / `Decision`(第 62-65 行)全英文。
- 空狀態(第 72 行):`No visible approvals for this run.`
- 後端只有單一 run 的查詢端點:`frontend/src/api/runApprovals.ts:31` `GET /api/runs/{runId}/approvals`,沒有任何「跨 run 找出我能核准什麼」的端點。`backend/src/Backend.Api/AgentRuns/AgentRunApprovalController.cs` 目前的路由結構也只以 runId 為錨點(對應 Backend 側尚無 §1.3 所需的新查詢端點)。
- [plans/README.md](../README.md) 記錄 agent-architecture-improvements 的 Phase O1 已實作、**O2–O5 未實作**;[04-operations-trigger-plan.md §4](../agent-architecture-improvements/04-operations-trigger-plan.md#4-phase-o3discoverable-approval-queue) 完整定義了本該存在但尚未建置的佇列端點規格。

### 1.2 目標行為

分兩步(對應 01-plan §4 Phase A / Phase B):

**Phase A(先行,不等後端):** 把 `ApprovalInbox.tsx` 現有文案全面中文化——標題、說明、欄位標籤、按鈕、空狀態、決策按鈕、toast 訊息(現況 `toast((error as Error).message, 'error')` 直接透傳伺服器訊息,見 §2.3)。手輸 Run ID 動線保留,但重新定位為畫面上明確標示的「進階查詢」區塊(例如摺疊在 `<details>` 或副標題標明「已知 Run ID 時可直接查詢」),不再是唯一動線。

**Phase B(依賴 O3 端點):** 新增佇列為預設主畫面——載入即列出「visible to me」(含自己啟動的 run)與「actionable to me」(server 判定當前使用者可核准)兩種清單,逐句沿用 O3 §4 定義的欄位邊界(只含 run/Agent identity、created/expiry、required role、server-authored safe action summary、status;不暴露 raw arguments/fingerprints/checkpoint/effect identity/lease)。決策動作(Approve/Reject)沿用現有 `decideRunApproval()` 呼叫路徑,只是觸發點從「先手輸 ID 查詢」變成「從佇列列點擊」。§1.1 描述的進階查詢區塊在 Phase B 之後仍保留,作為佇列之外的手動備援(例如佇列因租戶/分頁限制未顯示、或核准者想直接核對特定 run)。

### 1.3 不做什麼

- 不在本計畫重新定義 O3 的授權判斷(same tenant、required role、未過期、exact waiting state、self-approval 拒絕)——直接引用 04-operations-trigger-plan §4 既有規格。
- 不做 O2 全量 Runs/Tasks 中心;佇列只服務「approval」這一個 predicate,不是通用 run 搜尋頁。
- 不新增決策路徑或改變 once-only semantics;佇列只是發現層。
- 不移除手輸 Run ID 的進階查詢能力。

### 1.4 舊碼盤點

- `ApprovalInbox.tsx` 現有的 `runId`/`load()`/`decide()` 邏輯與 `attemptRef`(`LogicalAttemptKey` idempotency 機制,第 22-28 行)在 Phase B 後**不刪除**,改標記為進階查詢子元件,與新佇列元件並存於同一視圖(可拆成 `ApprovalQueue.tsx` + `ApprovalAdvancedLookup.tsx` 兩個子元件,或維持單檔但明確分區——實作階段依 dotnet/frontend-implementer 判斷,不在本計畫拍板檔案切法)。
- `frontend/src/api/runApprovals.ts` 的 `listRunApprovals(runId)` 保留給進階查詢使用;Phase B 新增的佇列查詢函式是新增,不是取代。
- 04-operations-trigger-plan §10 cleanup inventory 已預留「Structured cockpit 上線後移除 raw JSON production view」——與 W1 無直接重疊(那是 Operations,不是 Approvals),但屬同一輪治理 UI 現代化的姊妹清理項,列在 01-plan §6 一併追蹤。

### 1.5 契約守則

- Backend 新端點必須逐句重用既有 `decideRunApproval` 端點已實作的授權判斷(tenant、required role、expiry、action fingerprint、waiting state、separation of duties),不得引入第二套判斷邏輯造成 drift。
- DTO 絕不包含 raw arguments、fingerprints、checkpoint、effect identity 或 lease(O3 §4 明文)。
- 端點必須維持 `AGENT_WRITE_TOOLS_ENABLED` 一致的 fail-closed posture(флаг 關閉時的行為與現有 `/api/runs/{runId}/approvals` 一致,不得對外呈現「佇列功能存在但空」與「佇列功能不存在」的可分辨差異,除非現有端點本身就有這種可分辨性——需與現況比對,不得新引入不一致)。
- Decision race 仍只有一個成功且 audit 完整(O3 §4 驗收標準)。

---

## W2 — 術語轉譯規範

### 2.1 現況

全站已有一個成熟的翻譯機制可擴充,而非要從零設計:`frontend/src/skills/validationLabels.ts` 的 `CODE_LABEL`(第 5-19 行)把 workflow 引擎的錯誤碼(`unknown_node`、`forbidden_script`、`invalid_expression`……)一一翻成不含行號/AST/引擎術語的中文句子,供 `SimpleSkillEditor.tsx` 的 `humanErrors()`(第 40-44 行)使用。这是「業務流程簡單模式」全站唯一零術語外殼的落地機制之一。

同時,術語密度隨權限遞增的證據(逐檔核對):

- `AgentEditor.tsx` 中英混排:「無 Audience principal,任何人都不可啟動」(第 297-298 行附近 `AudienceEditor` 元件)、「以 namespaced principal 授權;角色使用 role:,群組使用 group:。空集合會 fail-closed。」(第 238-240 行)——中文句子裡直接嵌「Audience」「principal」「fail-closed」等英文術語。
- `ApprovalInbox.tsx`、`OperationsGovernanceView.tsx`、`EvaluationPanel.tsx` 整頁英文(已在 W1/W3 引用具體行號,此處不重複)。
- `OperationsGovernanceView.tsx:334-337` 的 override 確認文案:`Override the failed regression gate? This is a break-glass action and is permanently audited.`——「break-glass」是安全術語直接入使用者可見的確認對話框。
- `AgentTestConsole.tsx:45`:`waiting_approval: '等待核准(D3 不提供核准操作)'`——內部階段代號混入狀態文字(此項同時是 W3 的證據,見 §3.1)。

### 2.2 目標行為

建立一份**共用詞彙對照表**(文件層,不強行做成前端型別/常數匯出物——見 01-plan §9 開放問題 3),以 `CODE_LABEL` 的翻譯粒度為範本擴充到全站錯誤碼與常見安全/治理術語。草案(核准後定案,示例非窮舉):

| 原文(英文/術語) | 建議中文 | 使用情境 |
| - | - | - |
| revision | 版本 | Agent/Workflow/Orchestrator revision 顯示 |
| Audience | 誰可以使用 | AudienceEditor 標籤與說明 |
| fail-closed | 未設定時一律拒絕 | 任何 fail-closed 語意的說明文字 |
| slug | 系統識別碼 | AgentSlugSection 等欄位標籤 |
| principal | 授權對象 | Audience/capability 相關說明 |
| capability(`workflow.manage`) | 系統管理權限 | 與 ADMIN 角色明確區分時的說明文字 |
| canary / allowlist | 白名單開放中 | D6 canary 相關 UI |
| break-glass override | 緊急覆蓋(全程留痕) | RegressionPanel override 確認文案 |
| ETag / optimistic locking / conflict | (沿用既有先例)「已被其他人更新,請重新載入」 | 已有良好先例(`AgentEditor.tsx:1499`「此 Agent 已被其他人更新,你的變更未套用」、`WorkflowsView.tsx:56`「草稿已由其他人更新;為避免覆寫,所有操作已鎖定」),W2 只需把此模式套用到尚未套用的畫面(如 `OrchestratorsView.tsx:426` 已有類似文案,需盤點是否所有 409 衝突路徑都已一致) |
| idempotency key | (內部概念,不對使用者呈現) | — |
| anti-replay token | 防重放驗證碼(預設不解釋,只在需要時說明) | ApprovalInbox 說明文案 |
| observed usage/cost units | 已量測用量/成本 | OperationsGovernanceView 表格欄位 |
| regression gate | 品質迴歸關卡 | RegressionPanel |
| rollout / rollback | 上線 / 回退 | RolloutPanel |
| D1–D7、P0–P5、O1–O5、E1–E3 等內部階段代號 | 一律移除,不翻譯 | 見 W3 |

**錯誤訊息透傳點盤點(哪些畫面直接把伺服器 message 丟給使用者)**——這是 W2 的第二個交付物,逐一列出後才能判斷「人話化」還是「維持原樣但確認伺服器 message 本身已是人話」:

| 位置 | 現況程式碼 | 處置方向 |
| - | - | - |
| `ApprovalInbox.tsx:50` | `toast((error as Error).message, 'error')` | 需盤點 backend `/api/runs/{runId}/approvals` 的錯誤訊息是否已是人話(ApiError.message 是否面向使用者設計);若否,補一層轉譯 |
| `AgentEditor.tsx` `createErrorMessage()`(第 99-102 行) | 已對 409 特化(`slug 已被使用,請換一個`),其餘 `return (e as Error).message` 透傳 | 盤點非 409 情境的實際訊息內容,決定是否需要更多特化分支 |
| `OrchestratorsView.tsx:520` | `{errors.map((error) => <li key={error}>{error}</li>)}` 直接列出 `validateOrchestrator`/`publishOrchestrator` 回傳的原始錯誤陣列 | 需盤點 backend validate 回傳的 message 語言與可讀性 |
| `EvaluationPanel.tsx` 空狀態 | `Evaluation is not enabled for this tenant (RUN_EVAL_ENABLED off).`——直接印環境變數名 | 改為不透露變數名的中文說明,同時不得讓「關閉」與「不存在」在此畫面內變得可分辨(見契約守則) |
| `OperationsGovernanceView.tsx` 多處 `toast((error as Error).message, ...)`(RegressionPanel/RolloutPanel 的 catch 路徑經 `runWithToast` 統一處理) | 同上,需盤點 `runWithToast` 的預設行為是否已有轉譯掛鉤點,或需在此新增 | — |

已有的正面先例(不需改動,作為規範參考):`SimpleSkillEditor.tsx` 的 `saveErrorMessage()`(409→「這個名稱已被使用,請換一個。」、422→「設定有誤,請調整規則或欄位後再試。」)、`humanErrors()` 透過 `CODE_LABEL` 翻譯。

### 2.3 不做什麼

- 不建立一個集中式 i18n 框架或字串抽換機制(現況沒有國際化需求,單一語言中文——過度工程)。
- 不改動任何 API payload 的欄位命名或值,只翻譯顯示層文案(根 AGENTS.md 明文:snake/camel 混用是刻意設計,不得「修正」)。
- 不翻譯 `code`(ApiError 的機器可讀欄位)——它的用途是程式判斷,不是顯示。
- 不強行合併 `CODE_LABEL`/`saveErrorMessage`/`createErrorMessage`/`DELTA_LABEL` 四個各自獨立的翻譯字典成單一巨集元件——它們的錯誤碼空間本來就不同(引擎驗證碼 vs HTTP 狀態碼 vs eval delta 狀態),合併是投機抽象。

### 2.4 舊碼盤點

- `CODE_LABEL`(`validationLabels.ts`)、`saveErrorMessage()`(`SimpleSkillEditor.tsx`)、`createErrorMessage()`(`AgentEditor.tsx`)、`DELTA_LABEL`(`EvaluationPanel.tsx`)四個既有翻譯字典**保留、擴充**,不刪除、不合併。
- §2.2 表格列出的透傳點是本計畫要**新增**的轉譯邏輯,不是要刪除的舊碼——舊碼盤點的意義在此是「確認沒有已存在但被本計畫重複實作的翻譯邏輯」,盤點結果是沒有重複,可以安全新增。

### 2.5 契約守則

- 翻譯安全語意詞彙時不得失真:「fail-closed」譯為「未設定時一律拒絕」時,若原文語意是「發生任何驗證失敗都拒絕」(不只是「未設定」),需依實際語意調整措辭,不可為了統一詞彙表而套錯情境(逐處核對 docs/agent-platform-contracts.md 與 docs/cross-service-contracts.md 的精確定義)。
- 錯誤訊息轉譯只能發生在前端顯示層,不得反向要求 backend/workflow 修改 ApiError.message 的既有格式或語言(那會影響其他消費者,如日誌/監控),除非該訊息從未被設計給終端使用者看(需先確認消費端只有這一個 UI)。
- Feature-gate 相關文案(如 `RUN_EVAL_ENABLED` 空狀態)翻譯後,仍不得讓「功能關閉」與「租戶無權限」在同一畫面裡變得可分辨,除非現有 API 回應本身就有意讓已認證的 ADMIN 分辨這兩者(需對照 W4 的 `/api/features` 契約邊界判斷)。

---

## W3 — IA 去架構化

### 3.1 現況

**內部代號直接出現在使用者可見文字**(已對照原始碼複核,見 01-plan §1 引用):

- `frontend/src/components/AgentTestConsole.tsx:45`:`waiting_approval: '等待核准(D3 不提供核准操作)'`;同檔案第 681 行附近:`此 Run 正在等待核准。D3 測試主控台不提供 approval 或寫入操作;可取消 Run。`
- `frontend/src/components/AgentSkillEditor.tsx:235`:`<span className="badge badge--user">唯讀(P1 不執行)</span>`;同檔案註解(第 24、35 行)也用「P1」指涉內部階段,但那些是註解不影響使用者,第 235 行的 badge 才是使用者可見的。

**架構直接攤平成分頁結構:**

- `frontend/src/components/AgentPlatformView.tsx:7-11` 的 `LABEL` 對照表:`{ agents: 'Agents', workflows: 'Workflow Designer', orchestrators: 'Orchestrators' }`——三個純英文分頁標籤,直接對應 D1(Agent Registry)/D4(Workflow Designer)/D4(Orchestrator Registry)三個內部分層,而非任務導向命名。
- `frontend/src/agentPlatformTabs.ts` 的閘門邏輯本身(`agentBuilderEnabled && isAdmin` 給 `agents`;`workflowDesignerEnabled && canManageWorkflow` 同時給 `workflows`+`orchestrators`)是**要保留**的安全判斷,不是要改的部分——W3 只動 `LABEL`,不動這個函式。
- `frontend/src/components/ConfigView.tsx:15-22` 的四分頁:`業務流程` / `Agent Skills` / `執行參數/Harness 節點參數` / `一般設定`——「業務流程」與「Agent Skills」是兩個平級 tab,對應 P0–P5 遷移期的雙軌架構決策(見根 AGENTS.md「Skill concepts and engine boundary」),但呈現成使用者需要理解的兩套心智模型;「執行參數/Harness 節點參數」分頁標籤本身就含「Harness」術語。
- `frontend/src/components/WorkflowsView.tsx` 整頁英文技術詞彙密度最高的畫面之一:建立表單的 `<option value="orchestrator">Orchestrator</option>`/`<option value="agent-runtime">Agent Runtime</option>`、`Runtime variant` 下拉的 `Worker Harness`/`Read-only Verifier Harness`、章節標題 `Validation`/`Simulation trace`/`Revisions / Semantic Diff`,以及說明文字「只可編輯 Harness graph;Prompt、Rule、Skill instruction 與 Agent binding 一律不在 Graph IR。」——中文句子裡直接嵌「Harness graph」「Graph IR」。

### 3.2 目標行為

1. **移除內部階段代號。** `AgentTestConsole.tsx` 的 `waiting_approval` 狀態標籤與說明文字改為不含「D3」的表述,例如「等待核准(此測試主控台不提供核准操作)」;`AgentSkillEditor.tsx:235` 的徽章改為「唯讀(附件於此階段不會被執行)」或等義說法,不出現「P1」。
2. **Agent 平台三分頁改任務導向命名。** `LABEL` 對照表從系統分層命名改為使用者任務命名,方向示例(核准後定案,不在本計畫鎖死措辭):`agents`→「Agent 管理」、`workflows`→「流程設計」或併入更大情境說明、`orchestrators`→「協作流程」或「多 Agent 協作」。閘門邏輯(`agentPlatformTabs()`)不動,只改 `LABEL` 物件的值。
3. **「業務流程 vs Agent Skills」收斂為單一「技能」心智模型——僅 UI 呈現,不動 API 雙軌。** 具體做法待實作階段設計(可能是:同一個父層級的視覺分組下,用子分類而非平級 tab 呈現;或用統一的「技能」列表 + 每列標示「流程」/「套件」來源徽章,取代兩個獨立 tab)。**明確邊界:`BusinessWorkflowHome.tsx`/`AgentSkillHome.tsx` 兩個元件、`kind` discriminator、`/api/skills*`/`/api/business-workflows*` 兩條路由完全不動**——雙軌收斂是 Architecture Hard Reset P5/C8 gate 的事,本計畫只改呈現層。
4. **`WorkflowsView.tsx` 與 `OrchestratorsView.tsx` 的英文技術詞彙全面中文化**,套用 W2 詞彙表(Harness→執行骨架、Graph IR→視內容決定是否需要對外呈現此詞或直接省略、Orchestrator→協作流程、Worker/Verifier→執行者/查核者)。此項與 W2 共用同一份詞彙表,實作時應在 W2 定案後統一套用,避免兩個 workstream 各自發明不一致的譯法。

### 3.3 不做什麼

- 不合併 `BusinessWorkflowHome`/`AgentSkillHome` 元件或其底層資料模型。
- 不改變 `agentPlatformTabs()`/`AppShell.tsx` 導覽項目的閘門邏輯(`agentBuilderEnabled && isAdmin`、`workflowDesignerEnabled && canManageWorkflow` 等判斷式本身)。
- 不移除或重新設計 D1–D7 的內部分層架構本身——只移除**呈現給使用者的**代號與純技術分頁命名;程式碼內部註解、變數命名、AGENTS.md/docs 契約文件中的 D1–D7 代號完全不受影響(那是給開發代理看的,不是給終端使用者看的)。
- 不對 `Graph IR`/`Node Catalog`/`configSchema` 等 Workflow Designer(`workflow.manage` 層,系統管理者專屬)的深層技術詞彙做過度稀釋——這一層的使用者是系統管理者(具備 `workflow.manage` capability 的技術角色),不是業務主管;W3 的「去架構化」重點是移除**代號**與**跨層洩漏**(如 D3/P1 出現在 ADMIN 甚至 USER 可能路過的畫面),不是把 Workflow Designer 整頁改成零術語(那不現實,也超出 findings C4 的實際訴求——C4 針對的是「內部架構投影成 IA」,不是「技術畫面該完全去技術化」)。

### 3.4 舊碼盤點

- `AgentPlatformView.tsx` 的 `LABEL` 物件字面值(`'Agents'`/`'Workflow Designer'`/`'Orchestrators'`)被新字串取代,舊字串本身無其他引用處(需 implementer 落地時以 `search_code`/`trace_path` 複核 `LABEL` 是否被其他檔案引用;依目前讀碼結果只在本檔內使用)。
- `AgentTestConsole.tsx:45` 的 `waiting_approval` 標籤字串、`AgentSkillEditor.tsx:235` 的徽章字串為單點替換,無下游耦合(純字面量,未被其他邏輯依賴其確切文字內容——需 implementer 落地時確認沒有測試斷言這些確切英文/代號字串,若有需同步更新測試而非放寬斷言)。
- `ConfigView.tsx` 的 `TABS`/`LABEL` 若因 §3.2.3 的呈現調整而改變分頁結構,需同步檢查 `frontend/AGENTS.md` 中「ConfigView 現況為四分頁」的既有文件敘述——若本計畫落地後分頁呈現改變,`docs-updater` subagent 需同步更新該檔案,避免文件與程式碼再度漂移(這正是 `settings-skill-redesign/01-plan.md` 開頭「稽核追加」記錄過的同類問題——四分頁曾經被文件誤記為三分頁)。

### 3.5 契約守則

- `agentPlatformTabs()` 是 AppShell 側欄入口與 `AgentPlatformView` 內層分頁共用的唯一閘門判斷來源(`frontend/AGENTS.md`:「入口與內層分頁共用同一份閘門判斷,避免『側欄有入口、進去卻是空分頁』的漂移」)——W3 絕不能讓 `LABEL` 改動連帶影響到這份判斷式,兩者必須維持解耦(顯示字串 vs 邏輯判斷分離)。
- Verifier/Worker(`runtimeVariant`)的語意邊界(verifier Agent 必須 pin 已發布 read-only verifier harness;Worker harness 被拒絕)是 D4 契約明定的安全邊界(見 docs/agent-platform-contracts.md),W3 只翻譯下拉選單顯示文字,不得讓翻譯後的措辭模糊掉「read-only」這個關鍵安全屬性。
- P0–P5/C8 雙軌收斂 gate 的既定條件(Business Workflow 流量 cutover 證明、legacy flow-write usage = 0、rollback window 結束)不受本計畫影響;W3 呈現層改動完成後,若使用者能「感覺」兩者已經統一,必須在計畫記錄或程式碼註解中明確區分「使用者感知的統一」與「系統仍雙軌運作」,避免未來開發者誤讀為 C8 已達成。

---

## W4 — 功能開通狀態頁

### 4.1 現況

`platform/src/Platform.Web/Program.cs:624-627`:

```csharp
app.MapGet(
    "/api/features",
    () => Results.Ok(new { agentBuilderEnabled, agentTestRunEnabled, workflowDesignerEnabled, multiAgentDispatchEnabled, contextEnrichmentEnabled, agentChatEnabled, agentWriteToolsEnabled }))
    .AllowAnonymous();
```

註解(第 622-623 行)明確記載其設計意圖:「Feature flags(D1):AllowAnonymous、只暴露布林旗標,供前端決定是否顯示 Agent Builder 入口。刻意不受上面的 `/api/agents*` 404 中介軟體影響(路徑不同),也不揭露任何其他組態。」

此端點已被 `frontend/src/components/AppShell.tsx:87-109` 消費——登入後取一次,依回傳布林值決定側欄哪些入口要顯示,失敗或任一值缺失一律 fail-closed 回到 `FLAGS_OFF`(第 41-48 行)。目前**沒有任何畫面把這份已經在拿的資料整理成可讀視圖**——findings C6:「連 ADMIN 都得去讀部署文件和環境變數」。

### 4.2 目標行為

在系統設定(`ConfigView.tsx`)新增一個 ADMIN-only 分頁或區塊,呈現目前租戶(嚴格說是這個 Platform 部署實例——這些旗標不是 per-tenant,是 process 啟動時讀 env 決定,見 `Program.cs` 第 61-77 行都是 `cfg["XXX_ENABLED"]` 這種全域環境變數讀取)已開通/未開通的功能清單,直接複用 `getFeatures()`(`frontend/src/api/agents.ts` 既有函式,`AppShell.tsx` 已在用)拿到的同一份資料,不重新呼叫或新建端點。呈現形式:功能名稱(中文,套用 W2 詞彙表——例如 `agentWriteToolsEnabled`→「寫入核准與治理」)+ 開通狀態(是/否)的簡單表格或清單,不解讀成因果(不寫「因為 XXX 所以看不到」這種可能洩漏內部依賴關係的推論文字,只呈現「目前狀態」)。

### 4.3 不做什麼

- 不新增後端端點——`GET /api/features` 已存在且已回傳所需的全部布林值,重複造一個「詳細版」端點是不必要的新增認證面風險。
- 不讓這個頁面揭露除了 `/api/features` 既有回傳值以外的任何組態(例如環境變數原始值、白名單租戶清單、內部依賴關係圖)——那超出「已開通/未開通」這個布林視圖的範圍,且可能反過來造成資訊洩漏。
- 不因為要做「租戶已開通」視圖就把這些全域旗標改造成 per-tenant——那是資料模型層級的變動,不在本計畫範圍,且與 D6 `AGENT_CHAT_TENANT_ALLOWLIST`(canary 租戶白名單,是 Platform 額外疊加在全域旗標上的第二層判斷,不等於旗標本身變成 per-tenant)是兩回事,不可混淆。

### 4.4 舊碼盤點

- 無需刪除或取代既有程式碼,`getFeatures()`/`FLAGS_OFF`/`AppShell.tsx` 現有邏輯完全不動,本 workstream 純屬新增一個唯讀呈現視圖,復用同一份已取得的資料(若後續發現 `AppShell.tsx` 取得旗標的時機/生命週期與新頁面需求不符,例如新頁面需要手動重新整理而非只在登入時取一次,才需要局部調整——列為實作階段判斷,非本計畫預先決定)。

### 4.5 契約守則

- **不得新增任何未認證面。** 新頁面本身是 ADMIN-only(比照 `ConfigView` 現有 `adminOnly` 側欄過濾模式),但它底層呼叫的 `/api/features` 端點本來就是 `AllowAnonymous`——這不構成新增認證面,因為資料本來就已經公開可讀(任何人打這個端點都能拿到,登入頁之前就會被呼叫)。W4 不得因為想讓頁面「看起來更安全」而误导地把它包裝成需要特殊權限才能看到的敏感資訊——它本質上是把已公開的資料整理得更好讀,權限管控只是 UX 慣例(比照 AGENTS.md「ADMIN-only Config view is hidden by sidebar filtering; backend role enforcement is the real boundary」的既有原則)。
- **不得破壞對外 404 偽裝。** 新頁面的存在與否、其呈現內容,不得反過來影響任何其他路由的 404 行為;尤其不可以讓這個頁面成為「藉由觀察 UI 差異推斷哪些功能存在」的旁路(它本身已經是公開資訊,不構成旁路,但實作時仍需確認沒有意外新增任何需要認證才能看到的欄位混入這個本質公開的視圖)。
- 若未來 `/api/features` 新增旗標(如本計畫或其他計畫新增 flag),此頁面應該是「自然跟著顯示」而非需要逐一硬編碼——若目前的 `getFeatures()` 回傳型別是固定欄位的 interface,實作時應評估是否值得改成迭代物件鍵值(YAGNI 判斷:若目前只有 7 個固定旗標且變動不頻繁,維持顯式列舉可能比泛型迭代更清楚,不必為了「未來可能新增」預先抽象)。

---

## W5 — 裸 JSON 清零

### 5.1 現況(逐欄位盤點)

| 檔案:欄位 | 現況(原文/型別) | 既有可用資料源 |
| - | - | - |
| `AgentEditor.tsx` `AgentCapabilityOutputSection` 的 Output contract(第 731-741 行) | `<textarea id="agent-output-contract" className="textarea code-textarea" value={contractText} ...>`,自由 JSON object 文字 | **無現成 schema catalog**——output contract 是任意鍵值物件,沒有像 skill/tool catalog 這種列舉式資料源(見 §5.2 說明) |
| `OrchestratorsView.tsx` `Editor` 的四個 `JsonField`:`context`(第 486 行)、`workerPool`(第 487-494 行)、`verifier`(第 496 行)、`budgets`(第 497 行) | 四個 `<textarea>`,`editJson()` 用 `JSON.parse` 解析,失敗即欄位錯誤鎖住寫入(第 412-417 行) | `workerPool`/`verifier` 引用的是已發布 Agent(`agentId`+`revision`)——`listAgents`/`getAgent`(`frontend/src/api/agents.ts`)可提供選單;`budgets` 是純數值欄位集合(`maxContextRounds`/`maxTasks`/`maxChildRuns`/`maxConcurrency`/`maxRepairRounds`/`tokenBudget`/`timeoutSeconds`),結構已知、可直接比照 `AgentEditor.tsx` 的 `AgentRuntimeLimitsSection`(第 829-873 行,同樣是「數字輸入格 grid」模式)表單化;`context` 的 `readOnly`/`allowedTools`/`knowledgeSources` 中 `allowedTools` 可用 `listAgentToolCatalog()` 提供選單 |
| `OrchestratorsView.tsx` 建立表單的 `orchestrator-create-worker-pool`(第 601 行) | 同上 workerPool,建立時也是裸 JSON | 同上 |
| `frontend/src/workflowDesigner/WorkflowDesigner.tsx` 節點 Config(第 108-112 行) | `<textarea id="workflow-node-config" ...>`,`patchConfig()` 用 `JSON.parse`(第 73-79 行),失敗時「靜默保留在 textarea,不寫回 definition」 | `WorkflowNodeType.configSchema: Record<string, unknown>`(`frontend/src/types.ts:53`)——**型別上存在但本次讀碼未確認其執行期實際形狀是否為可驅動表單的 JSON Schema 子集**(見 01-plan §9 開放問題 1) |
| `EvaluationPanel.tsx` `EvalTriggerForm` 的 Candidate skill name(第 360-367 行) | `<input id="eval-candidate-name" ...>`,自由文字輸入 skill 名稱 | `listSkillCatalog()`(已被 `AgentEditor.tsx` 用於 Skill 綁定選單)可直接提供下拉選項 |
| `OperationsGovernanceView.tsx` `RolloutPanel` 的 Orchestrator ID(第 500-515 行) | `<input id="ops-orch-id" ...>` + `GUID_PATTERN` 正則客戶端驗證(第 36 行、449-450 行),手打 GUID | `listOrchestrators()`(`OrchestratorsView.tsx` 已用)可提供下拉選項——選了目標後 revision 欄位甚至可以連動預填「目前發布版本」 |
| `OperationsGovernanceView.tsx` `RegressionPanel` 的 Eval run ID(第 374-393 行) | `<input id="ops-eval-run-id" ...>` + `GUID_PATTERN` 驗證,手打 GUID | `listEvalRuns()`(`EvaluationPanel.tsx` 已用)可提供下拉選項——這是 §5.1 補充項,findings 原文未點名但屬同類問題,一併列入 |

正面對照組(**已表單化,不需改動,作為範本**):`BusinessRuleEditor.tsx` 透過 `listRuleFacts()`/`listRuleActions()` 驅動下拉選單、巢狀群組建構、`simulateBusinessRules()` 模擬器——這是 C9 明確點名「團隊證明過自己會做」的範本,W5 的表單化方向應盡量比照其模式(catalog-driven select + typed builder),而非重新發明。

### 5.2 目標行為

逐欄位改造,依 §5.1 資料源盤點分三類:

1. **有現成清單 API 可直接下拉的欄位**(立即可做,Phase A):Rollout Orchestrator ID → `listOrchestrators()` 下拉;Regression eval run ID → `listEvalRuns()` 下拉;Eval candidate skill name → `listSkillCatalog()` 下拉;Orchestrator `workerPool`/`verifier` 的 Agent 引用 → `listAgents()` 下拉(+ revision 欄位維持數字輸入或連動預填目前發布版);Orchestrator `context.allowedTools` → `listAgentToolCatalog()` 多選。
2. **結構已知、可直接表單化(不需外部 catalog,純數值/簡單欄位)**(立即可做,Phase A):Orchestrator `budgets`(比照 `AgentRuntimeLimitsSection` 的數字 grid 模式);`context.readOnly`(checkbox);`workerPool` 中除了 agentId/revision 以外的其餘欄位(視實際型別,可能是簡單的角色/數量標記)。
3. **Output contract(AgentEditor)**:因為是任意鍵值物件、沒有 catalog 可驅動選單,改造方向是**鍵值對編輯器**(比照 `StringSetEditor`/`AudienceEditor` 已有的「新增鍵 + 值 + 移除」互動模式,而非下拉選單),讓使用者不需要理解 JSON 語法本身(大括號、逗號、引號),但仍能表達任意鍵值——這比純 JSON textarea 進一步,但不假裝它有一個固定 schema(它本來就沒有)。
4. **WorkflowDesigner 節點 Config**(Phase A',視 `configSchema` 實際形狀而定):若 `configSchema` 的執行期形狀是可映射到「欄位名→型別」的簡單物件,採用與 `NodeParamsTab.tsx`(`CONFIG_FIELDS`/`draftToValues`/`validateConfigValues` 模式,`frontend/src/nodeParams.ts`)類似的生成式表單;若形狀複雜(巢狀、條件式 schema),保留 JSON textarea 作為逃生口,但至少加上結構驗證錯誤的行內提示(現況 `patchConfig()` 解析失敗時是完全靜默的,見 §5.1,這本身也是一個該修的小缺陷,不論是否做表單化都該修)。

### 5.3 不做什麼

- 不建立通用「JSON Schema → React 表單」生成框架去覆蓋所有欄位——依 §5.2 分類分別處理,是逐欄位的具體轉換,不是投機的通用抽象。
- 不假造 Output contract 的固定 schema(它就是任意鍵值物件),不勉強套用下拉選單模式。
- 不改變任何後端 validate/publish 的驗證邏輯——伺服器仍是驗證權威,前端表單化只是輸入方式改變,`validateOrchestrator`/`publishAgent` 等既有 API 呼叫與其回傳的 field errors 處理邏輯不變。
- 不移除 `GUID_PATTERN` 等既有客戶端驗證——即使欄位改成下拉選單讓使用者理論上不會再打錯格式,伺服器仍是權威,保留客戶端驗證作為防禦深度(defense in depth)沒有壞處,除非它因為欄位型別改變(如從 `<input>` 變成 `<select>`)而變得邏輯上不可觸達,那時才移除死路徑。

### 5.4 舊碼盤點

- `OrchestratorsView.tsx` 的 `JsonField` 元件(第 376-381 行)在 §5.2 分類 1/2 完成後,對 `context`/`workerPool`/`verifier`/`budgets` 四個既有呼叫點(第 486、487-494、496、497 行)**不再需要**,但 `JsonField` 元件本身**不刪除**——它仍被 `orchestrator-create-worker-pool`(第 601 行,建立表單)使用,除非該處也一併表單化(依 §5.2 應該一併做,因為資料源相同);若最終所有呼叫點都被表單取代,`JsonField` 元件本身才連帶變成死碼,需要 implementer 落地時用 `search_graph`/`trace_path` 複核所有引用點清空後才刪除,不可只看單一畫面就假設已無用途。
- `GUID_PATTERN` 正則(`OperationsGovernanceView.tsx:36`)與其兩處引用(`orchestratorIdInvalid`/`evalRunIdInvalid`)在改成下拉選單後,理論上輸入值永遠合法——但保留作防禦深度(見 §5.3),不視為死碼。
- `WorkflowDesigner.tsx` 的 `patchConfig()` 若被 Phase A' 的生成式表單取代,`textarea`/`JSON.parse` 路徑需視 §5.2.4 的調查結果決定去留(全取代 or 保留為逃生口分支)——不在本計畫預先拍板,列為 01-plan §9 開放問題 1。

### 5.5 契約守則

- 所有下拉選單的資料源必須是既有已認證 catalog 端點(`listOrchestrators`/`listAgents`/`listSkillCatalog`/`listEvalRuns`/`listAgentToolCatalog`),前端不得快取、硬編碼或跨租戶推導這些選項——沿用現有各自的租戶隔離與角色過濾行為(如 `listAgents` 已經是「同租戶、ADMIN 可見」的既有語意,表單化不改變這個邊界)。
- Orchestrator 建立/編輯表單化後,`refsReady` 這類既有前置檢查(`OrchestratorsView.tsx:548`:「建立前需提供 pinned Workflow、至少一個 pinned Worker 與 pinned read-only Verifier;revision 必須明確指定,不使用 latest 或硬編碼」)的語意不得被表單化稀釋——選單選出的仍必須是明確 revision,不可以讓下拉選單暗中引入「不寫 revision 就用 latest」的行為(那會違反 D5 契約「Worker delegation and parallel writes are rejected」「Every child pins Agent/Skill/runtime Workflow revisions」的不可變快照原則)。
- Verifier 下拉選單若要做,必須只列出符合 `runtimeVariant: 'verifier'`、已發布、read-only 的 Agent——不可以讓使用者透過表單選到一個 Worker Harness 當 Verifier(D4 契約:「verifier Agents must pin a published read-only verifier harness, while Worker harnesses are rejected」),表單化不能繞過這個既有校驗,伺服器 validate 端點仍是最終權威,但前端選單本身也應該只呈現合法選項以減少無謂的 422 往返。
