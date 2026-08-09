# 計畫書 — P1 管理面降階(讓「管理者 = 業務主管」成立)

> **狀態:Phase A、Phase A'、Phase B 已於 2026-08-09 全部實作交付並通過 code review。** W1 佇列化含 backend O3 端點 `GET /api/runs/approvals?scope=visible|actionable` + platform 代理 + ApprovalInbox 佇列主動線;W2/W3 中文化與去代號;W4 功能開通狀態頁;W5 五處表單化含 WorkflowDesigner schema 驅動表單。測試:backend 1447(含真實 Postgres 輪)、platform 986、frontend 全量 UI 132 + logic 151 全綠。本計畫是 [plans/product-review-2026-08/00-findings.md](../product-review-2026-08/00-findings.md) 第四節 P1 路線的落地規格,對應該文件 C2、C4、C5、C6、C9 五項發現。findings 文件本身是 informative record(非執行計畫),本計畫與 `ux-core-journey`(P0)、`north-star-and-activation`(P2)為同一次審查拆出的三個平行下游計畫,互不重疊——P0 管 USER 層旅程與外洩,P2 管路線圖層級戰略決斷,本計畫只管**管理面(ADMIN / workflow.manage 兩層)的表達方式**。
>
> **範圍邊界(一句話):改表達,不改機制。** 本計畫不新增、不放寬、不繞過任何既有安全機制——404 偽裝、`workflow.manage` 精確 capability、D7 separation of duties、fail-closed 預設全部原樣保留;動的只是畫面文案、資訊架構呈現、與「裸資料 vs 表單」的輸入方式。
>
> **已於 2026-08-09 關閉的延後項:** (a) Verifier/Worker 下拉已按角色過濾(backend `GET /api/agents` 新增選填 `published_execution_roles` 欄位,前端 fail-open:欄位缺席不過濾);(b) output_contract 伺服器端驗證已補(詳見下段)。第三輪 e2e 對兩項皆真瀏覽器驗證通過。仍記錄不修的邊界情況:run 取消請求的極短競態視窗內佇列 actionable 可能誤標 true,decide 端點仍正確拒絕(409),自癒性邊界情況。
>
> 2026-08-09 已完成兩輪 docker compose 全鏈路驗證(P0 一輪、P1+O3 一輪,含 Development flags 開啟後的閘控畫面、404 偽裝逐位元組對照、workflow.manage 真實 capability 驗證),全項 PASS。
>
> e2e 額外發現並已修復:Agent 編輯器 Output contract 原宣稱自由格式,實際 D3 執行期(workflow/app/runtime/output_contract.py)僅接受 JSON-Schema 子集關鍵字(type/properties/required/additionalProperties/items/enum),非白名單鍵發布無錯、測試 Run 才以通用 preflight 錯誤失敗——前端已改為誠實文案 + 非阻斷警告(白名單常數需與 workflow 端同步);伺服器端 validate/publish 時的 output_contract 關鍵字檢查已於 2026-08-09 補上:backend `AgentCanonicalizer` 鏡射 workflow `_KEYS` 白名單,validate/publish/restore 期回傳 422 fieldErrors;白名單三處需同步(workflow `output_contract.py`、backend `AgentCanonicalizer`、前端 `AgentEditor`)。

## 1. 背景與動機(WHY)

findings §二逐條指出管理面的五個矛盾,現況引用皆已對照原始碼複核(見 [02-spec.md](02-spec.md) 各節「現況」):

- **C2 — 業務核准者拿到全站最工程化頁面。** D7 把核准角色明確定義為業務核准者(separation of duties,刻意不要求技術 capability),但 `ApprovalInbox.tsx` 整頁英文文案,且因為 [O3「可核准給我」佇列](../agent-architecture-improvements/04-operations-trigger-plan.md#4-phase-o3discoverable-approval-queue)未實作,核准者必須先從別的管道拿到 Run ID(GUID)手動輸入才看得到待核准項。
- **C4 — 內部架構直接投影成資訊架構。** 使用者被迫理解系統的施工狀態而非自己的任務:Agent 平台三分頁(Agents/Workflow Designer/Orchestrators)是 D1/D4 系統分層的直接攤平;`AgentTestConsole.tsx:45,681` 與 `AgentSkillEditor.tsx:235` 把「D3」「P1」等內部階段代號直接印在使用者看得到的狀態文字與徽章上;「Agent Skill vs Business Workflow」雙軌是遷移期架構決策,UI 卻呈現成兩個對等的平級分頁。
- **C5 — 同一站台三種語言帶,技術密度隨權限遞增。** USER 層是打磨過的繁中,Agent Builder 層中英混雜,D4/D5/D7/Eval 層(`ApprovalInbox`、`OperationsGovernanceView`、`EvaluationPanel`、`AgentTestConsole`/`TraceOverlay`)整頁英文——而目標客群「管理者」多半是業務主管而非工程師。
- **C6 — 404 偽裝(安全正確)沒有管理配套。** Flag 關閉時對外不可分辨是刻意的安全設計,但系統內沒有任何畫面讓有權限的管理者看到「哪些功能存在、目前開關狀態」;連 ADMIN 都得去讀部署文件和環境變數。
- **C9 — 表單化投資不一致。** `BusinessRuleEditor.tsx` 證明團隊能把 JSON AST 完全表單化(下拉選 Fact/運算子/動作、巢狀群組、模擬器),但同一批使用者在 `AgentEditor.tsx` 的 Output contract、`OrchestratorsView.tsx` 的四個 JSON 欄位(context/workerPool/verifier/budgets)、`EvaluationPanel.tsx` 的 candidate skill 名、`OperationsGovernanceView.tsx` 的 Rollout GUID,全部要手打——而系統都已有對應的清單 API 可以做成選單。

## 2. 範圍:五個 Workstream

| # | Workstream | 對應發現 | 詳見 02-spec |
| - | - | - | - |
| W1 | Approvals 佇列化與中文化 | C2 | §1 |
| W2 | 術語轉譯規範 | C5(+ C4 錯誤訊息透傳) | §2 |
| W3 | IA 去架構化 | C4 | §3 |
| W4 | 功能開通狀態頁 | C6 | §4 |
| W5 | 裸 JSON 清零 | C9 | §5 |

五個 workstream 互相獨立可並行規劃,但落地順序見 §4 階段切分——W2 的詞彙表是 W1/W3/W5 產出文案時的共用依據,建議先定案。

## 3. 與 agent-architecture-improvements O2/O3 的關係

[plans/README.md](../README.md) 记录 agent-architecture-improvements 的 O1 已實作,**O2–O5 未實作**。本計畫 W1 不是重新設計核准佇列,而是**接上並排優先**既有規劃:

- **O3(Discoverable approval queue)** 是 W1 的直接前置依賴。[04-operations-trigger-plan.md §4](../agent-architecture-improvements/04-operations-trigger-plan.md#4-phase-o3discoverable-approval-queue) 已定義好 DTO 邊界(只含 run/Agent identity、created/expiry、required role、server-authored safe action summary、status,不暴露 raw arguments/fingerprints/checkpoint/effect identity/lease)、visible vs actionable 兩種 predicate、驗收標準(expired/replayed/self-approval/cross-tenant 全部 server-side fail;non-owner 業務核准者可見 actionable item)。W1 直接引用這份設計作為後端新端點的規格來源,**不在本計畫重新定義佇列語意**。
- **O2(Unified Runs and Tasks center)** 與本計畫範圍有交集但不等同:O2 是「不知道 ID 也能查詢所有 run/task」的通用營運中心,涵蓋範圍比 Approvals 佇列更廣(含 Root/child runs 的全域 list/search)。W1 只取 O2 精神中「approval 不需要先知道 run ID」這一個切面,不做 O2 全量的 runs/tasks list UI——那屬於 north-star-and-activation(P2)「自動工作故事」或未來獨立排期的範圍。W1 完成後,ApprovalInbox 的手輸 Run ID 動線改列為「進階備援」(見 02-spec §1.2),不刪除;O2 若之後落地,ApprovalInbox 應改為消費 O2 的 list 端點而非重複造輪子——**列入本計畫舊碼盤點的前瞻項**,但不阻塞 W1 本身。
- O5(Durable triggers)、O4(Recovery operations)、Notifications 與本計畫無直接依賴,不在範圍內。

## 4. 階段切分

| 階段 | 內容 | 依賴 | 服務 |
| - | - | - | - |
| **Phase A(可立即動工,純前端)** | W2 詞彙表定案 + 套用;W3 IA 重新命名與去代號化;W4 功能開通狀態頁;W5 中「資料源已存在」的欄位表單化(Rollout GUID→下拉、Eval candidate→下拉、Orchestrator context/workerPool/verifier/budgets→結構化表單、AgentEditor Output contract→鍵值表單) | 無新後端端點 | frontend |
| **Phase A'(需要 catalog 補一個欄位)** | W5 中 WorkflowDesigner 節點 Config 表單化,若 `configSchema`(`WorkflowNodeType.configSchema`)的既有形狀不足以驅動泛型表單,需先確認/補齊該欄位的結構化程度 | 視現況而定,可能是純前端或小型 workflow 端調整 | frontend(+ workflow 視調查結果) |
| **Phase B(依賴後端 O3 端點)** | W1 佇列化主體:backend 新增 visible/actionable 兩種 predicate 的分頁查詢端點(依 04-operations-trigger-plan §4 規格)、platform 代理、frontend 消費佇列取代手輸 Run ID 為主動線 | O3 backend 端點 | backend + platform + frontend |
| **Phase A(先行,W1 子項)** | ApprovalInbox 全面中文化(不等 O3):把現有手輸 Run ID 動線的英文文案全部翻譯,先解決 C5 語言帶問題,不解決 C2 的「找不到要核准什麼」問題 | 無新後端端點 | frontend |

Phase A 與 Phase A' 可與 Phase B 並行規劃(不互相阻塞);Phase B 完成前,Phase A 已完成的中文化 ApprovalInbox 以「進階/手動查詢」形式與新佇列共存(見 02-spec §1.2)。

## 5. 契約邊界總則

所有 workstream 動工前後都必須成立(詳細版本見各 workstream 的「契約守則」,此處只列跨 workstream 共通的紅線):

1. **Feature-gate 404 偽裝不可退化。** W4 功能開通狀態頁只能讀取既有 `GET /api/features`(`platform/src/Platform.Web/Program.cs:624-627`,`AllowAnonymous`,只回布林旗標)並在前端渲染;不得新增任何未認證面,不得讓其他任何路由的 404 訊息因此變得可分辨「是關閉還是不存在」(見根 AGENTS.md「Feature-gate 404s are indistinguishable from route-not-found」)。
2. **`workflow.manage` 精確 capability 不可被稀釋。** W3 重新命名 Agent 平台三分頁時,`agentPlatformTabs()`(`frontend/src/agentPlatformTabs.ts`)這個唯一閘門判斷式的邏輯本身不得更動,只能改 `LABEL` 顯示字串;`AppShell.tsx` 的 `AGENT_PLATFORM_NAV`/`APPROVALS_NAV`/`OPERATIONS_NAV` 同理。
3. **D7 separation of duties 與核准語意不可簡化。** W1 佇列化必須逐句重用 [O3 §4](../agent-architecture-improvements/04-operations-trigger-plan.md#4-phase-o3discoverable-approval-queue) 既定的授權判斷(same tenant、required role、未過期、exact waiting state、self-approval 拒絕),佇列本身只是「找得到什麼可以核准」的發現層,決策仍走既有 `POST .../{approvalId}/approve|reject` 與 once-only semantics,不得新增決策路徑。
4. **雙軌架構(Business Workflow / Agent Skill、`/api/skills*` vs `/api/business-workflows*`)只做 UI 呈現收斂,不動 API 或資料模型。** W3 把「業務流程 vs Agent Skills」對使用者收斂為單一心智模型時,`BusinessWorkflowHome.tsx`/`AgentSkillHome.tsx` 兩個元件、其 `kind` discriminator、`/api/skills*`/`/api/business-workflows*` 雙軌全部不動——雙軌收斂是 Architecture Hard Reset P5/C8 gate 的事,本計畫明確不越界(見根 AGENTS.md「Skill concepts and engine boundary」)。
5. **表單化不得引入前端偽事實來源。** W5 任何下拉/選單的選項一律來自既有已認證 catalog 端點(如 `listAgents`、`listSkillCatalog`、`listOrchestrators`、`listAgentToolCatalog`),前端不得硬編碼或推導 ID;伺服器仍是驗證權威,client 端檢查(如既有 `GUID_PATTERN`)僅為 UX 提示,不能取代後端驗證。
6. **Response 欄位命名的 snake/camel 混用不得「順手修掉」。** W2 詞彙表只翻譯**顯示層文案**,不得改動任何 API payload 的欄位命名或值(根 AGENTS.md 明文:「Response field naming is mixed by design... do not "fix" either side」)。

## 6. 舊碼盤點(總覽)

依 [docs/coding-standards.md](../../docs/coding-standards.md)「每次計畫必含清理盤點」要求,逐 workstream 明細見 02-spec 各節;此處列跨 workstream 的共通項:

- **散落的 ad hoc 錯誤翻譯函式會被詞彙表工作(W2)部分取代但不刪除**:`AgentEditor.tsx` 的 `createErrorMessage()`、`SimpleSkillEditor.tsx` 的 `saveErrorMessage()`、`EvaluationPanel.tsx` 的 `DELTA_LABEL`、`frontend/src/skills/validationLabels.ts` 的 `CODE_LABEL` 各自是獨立的按檔案翻譯字典。W2 不會把它們合併成一個巨集元件(那是投機抽象,四處的錯誤碼空間本來就不同),而是把它們共同缺的「原始 server message 直接 toast」的透傳點(見 02-spec §2.3 清單)一一補上人話對應,舊字典保留、擴充。
- **ApprovalInbox 的手輸 Run ID 主流程在 Phase B 落地後降級為進階分支,不刪除**(04-operations-trigger-plan §10 cleanup inventory 也预留了「Structured cockpit 上線後移除 raw JSON production view」——本計畫的 debug JSON view 清理項與此呼應,見 02-spec §1.2 與 §5.2)。
- **`OperationsGovernanceView.tsx` 底部 `<details><summary>Raw JSON (debug)</summary>` 除錯區塊**(第 619-622 行)在 W5 把結構化表單/表格覆蓋到所有欄位後,語意上與 04-operations-trigger-plan §10「Structured cockpit 上線後移除 raw JSON production view」的既定清理項一致;本計畫執行時一併處理或明確記錄為延後項,不得留下兩份互相矛盾的資料呈現卻不說明取捨。

## 7. 驗收關卡

- **W2/W3/W4/W5(Phase A/A'):** `frontend/` `npm run lint` + `npm run build` 全綠;既有 Vitest/Playwright mocked-browser 回歸(`npm run test:unit`)不因文案/表單改動而破——尤其是任何測試若斷言了英文字串或特定 DOM 結構,需同步更新斷言而非放寬。
- **W1(Phase B):** backend 新端點的 xUnit 測試涵蓋 O3 §「驗收」列出的矩陣(expired/replayed/self-approval/cross-tenant decision server-side fail;queue 與 direct-decision 共用 policy 有 parity tests;合法 non-owner 業務核准者可見 actionable item);platform 代理層測試確認未認證/非 ADMIN(如適用)行為與既有 `AGENT_WRITE_TOOLS_ENABLED` 404 fail-closed 一致;frontend 佇列 UI 的 empty/loading/error/large-list 狀態測試。
- **跨 workstream 通則:** `code-reviewer` subagent 過一輪,特別確認「改表達不改機制」——任何 diff 若觸及授權判斷式、flag 讀取邏輯、`workflow.manage` 比對、404 中介軟體,一律視為越界,打回。UI 文案改動涉及安全語意詞彙(如 fail-closed、anti-replay)的翻譯要交叉核對 §5「契約邊界總則」與 docs/agent-platform-contracts.md,確保翻譯後語意不失真(尤其「未設定時一律拒絕」不能被簡化成聽起來像「預設允許」的說法)。
- **E2E:** 涉及跨服務行為的 W1 Phase B 完成後,交 `e2e-verifier` 走 docker compose full profile,確認 Approvals 佇列從空到有資料、核准/拒絕決策全鏈路正確,且既有以 Run ID 直查的路徑仍可用(進階備援未被破壞)。

## 8. 明確不做(YAGNI 邊界)

- 不做 O2 全量 Runs/Tasks 中心(見 §3);不做 O4 Recovery operations UI;不做 O5 Durable triggers 或通知收件匣——這些是獨立計畫的範圍。
- 不合併 `/api/skills*` 與 `/api/business-workflows*`,不合併 `BusinessWorkflowHome`/`AgentSkillHome` 兩個元件或其資料模型——雙軌收斂是 Architecture Hard Reset P5/C8 的事。
- 不新增 SYSTEM_ADMIN 分層或任何新角色/capability——術語與 IA 調整只作用在既有 ADMIN / `workflow.manage` 兩層之上。
- 不建立通用「JSON Schema → 表單」框架。W5 的表單化是逐欄位、逐資料源盤點後的具體轉換(見 02-spec §5),WorkflowDesigner 節點 Config 若 `configSchema` 形狀不足以驅動泛型表單,保留 JSON 逃生口而非強行造一個通用生成器去覆蓋所有 node type——那是投機抽象。
- 不新增認證面或放寬既有 gate 的預設值(所有 flag 預設維持 false/fail-closed)。
- 不處理 findings C1/C7/C8/C10(屬 `ux-core-journey`)或 C3 及第四節 P2 路線(屬 `north-star-and-activation`)。

## 9. 開放問題(待核准後定案)

1. **W5 WorkflowDesigner 節點 Config 表單化的可行範圍**:需要先讀 `configSchema` 在各 node type 的實際填值形狀(目前只確認型別是 `Record<string, unknown>`),才能判斷是「所有 node 都能表單化」還是「只有形狀規則的子集可以,其餘保留 JSON 逃生口」。建議列為 Phase A' 的第一個調查任務,調查結果回寫本文件再往下設計,不在核准前臆測範圍。
2. **W1 Phase B 的端點掛載位置**:04-operations-trigger-plan 未指定佇列端點的確切路由字串與是否掛在 `/api/admin/operations` 下(沿用 D7 既有 `workflow.manage` 網關)或另立路徑;需 dotnet-implementer 落地前與 backend/platform 既有路由慣例對齊,建議沿用 `/api/admin/operations` 家族以重用既有 `AGENT_WRITE_TOOLS_ENABLED` 404 gate,但這是實作階段決策,非本計畫拍板範圍。
3. **W2 詞彙表的最終版是否需要納入前端型別/常數(如做成一個 `glossary.ts` 匯出物)還是保持文件層對照表、各檔案各自套用**:偏向後者起手(呼應 R2/R4「規範但不強行抽象」),詳見 02-spec §2。
