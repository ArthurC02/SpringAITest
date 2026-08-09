# 非技術者視角產品審查:功能矩陣與 UX 分析

> Status: **informative record — 本文件是 `ux-core-journey`、`admin-experience-downshift`、`north-star-and-activation` 三個新計畫的 WHY 依據;程式碼與測試仍是現行行為的 authority。** 審查日期 2026-08-08,存檔日期 2026-08-09。依據:plans/ 全部 14 個計畫、frontend 46 個元件、platform 19 個 controller、backend 19 個 feature folder、workflow 引擎、docs/ 契約文件之逐檔盤點(6 個獨立調查線)。

審查前提:這款工具的目標使用者是**非技術人員**。任何出現在畫面或操作流程中的技術概念,都是使用者完成任務的阻力。本報告先重建全方案功能矩陣,再分析其合理性與矛盾,最後判定它距離「非技術人員可輕鬆操作的 Agentic 分析系統」還有多遠。

## 零、三句話結論

**能力金字塔是倒的。**
工程投資大量集中在底層治理引擎(D1–D7、E1/E3、Eval、Prompt 版本化——全部完成度高、安全設計成熟),但這些能力 **100% 藏在預設關閉的 feature flag 後面**;使用者每天面對的三個畫面(聊天/文件/分析)反而功能極薄。今天能推廣展示的,只有一層薄殼。

**最核心的旅程,入口是斷的。**
「上傳文件 → 針對文件提問」是這類產品的存在理由,後端整條鏈路都在(RAG、聊天 Skill 路由、副駕 `rag-qa`),但 UI 上沒有任何一個按鈕、連結或文案把兩端接起來——非技術使用者要靠運氣發現右下角那顆無文字的浮動按鈕。

**團隊證明過自己會做零術語 UX,但只做了一處。**
「業務流程簡單模式」(五範本、中文寫規則、當場試跑、錯誤人話化)是全站唯一刻意為非技術者設計的畫面,方向完全正確。其餘管理面卻是 canonical AST、Graph IR、裸 JSON textarea、整頁英文——同一個產品裡並存著兩種投資哲學。

## 一、功能矩陣

可用性評等(對非技術使用者):**◎** 可輕鬆完成 ・ **○** 可完成,需一次學習 ・ **△** 高摩擦,需技術詞彙理解 ・ **✕** 無法自行完成 ・ **—** 無 UI 入口

### A. 一般使用者(USER)層 — 今日實際可見

| 功能 | 現況 | UI 入口 | 可用性 | 備註 |
| --- | --- | --- | --- | --- |
| 聊天(SSE 逐字串流 + 歷史 + 分頁) | 上線 | 側欄「聊天」(預設頁) | ◎ | 空狀態印出 `POST /api/chat/stream`、Vite proxy 等除錯字串,扣分 |
| AI 副駕(建檔/刪檔確認/知識庫問答/切頁) | 上線 | 右下浮動鈕,**預設收合、無文字標籤** | ◎/✕ | 操作體驗全站最佳;發現性全站最差 |
| 文件上傳 | 上線 | 側欄「文件」 | △ | 僅 `.txt`/`.md` 純文字;處理期最長 60 秒無主動通知 |
| 文件問答(RAG) | 上線 | **無顯性入口**(副駕或聊天路由暗中命中) | ✕ | 能力在、入口斷,見發現 C1 |
| 分析頁 | 上線 | 側欄「分析」 | ○ | 實質內容僅「文件數/片段數」兩個計數卡 + 最近文件 |
| 協作模式(D6 Orchestrator 聊天) | flag 關 + 上線證據 blocked | 聊天頁下拉(canary 租戶才出現) | — | 下拉選項直接顯示英文「Orchestrator」名 |

### B. 管理者(ADMIN)層

| 功能 | 現況 | UI 入口 | 可用性 | 備註 |
| --- | --- | --- | --- | --- |
| 業務流程「簡單模式」(範本 + 中文規則 + 試跑) | 上線 | 系統設定 → 業務流程 | ○ | **全站唯一零術語外殼**;僅名稱欄要求英文 slug |
| 業務流程「進階模式」 | 上線 | 同上 → 進階編輯 | ✕ | 手寫 YAML、無語法高亮、引擎錯誤原文透傳 |
| Agent Skill 管理(SKILL.md 套件) | 上線 | 系統設定 → Agent Skills | ✕ | **無建立精靈**,只能上傳外部備妥的 zip;編輯 = 手寫 frontmatter + 內嵌 JSON 雙重語法 |
| Skill 試跑 / 版本控管 / 匯出 | 上線 | 同上 | △ | 試跑表單已依 schema 型別化(佳);list/dict 欄位仍要手寫 JSON |
| 執行參數(Configuration Set 多套切換) | 上線 | 系統設定 → 執行參數 | ○ | 分頁名即含「Harness 節點參數」術語 |
| 一般設定(key-value) | 上線 | 系統設定 → 一般設定 | △ | 表格欄位就叫「Key / Value」,含 `agent.defaults.system_prompt` 原始鍵 |
| Agent 建立/發布(D1)+ 商業規則(D2) | 完成,flag 預設關 | Agent 平台 → Agents | △ | 規則編輯器已表單化(佳),但 revision/slug/Audience/fail-closed 術語密集;Output contract 要手寫 JSON |
| Agent 測試 Run(D3) | 完成,flag 預設關 | Agent 編輯器 → 測試 Run | △ | 畫面出現「D3」內部代號、checkpoint、原始 JSON dump |
| 寫入核准 Approvals(D7) | 完成,flag 預設關 | 側欄 Approvals | ✕ | 整頁英文 + 需**手動輸入 Run ID**,見發現 C2 |

### C. 系統管理者(workflow.manage capability)層

| 功能 | 現況 | UI 入口 | 可用性 | 備註 |
| --- | --- | --- | --- | --- |
| Workflow Designer(D4 視覺化畫布) | 完成,flag 預設關 | Agent 平台 → Workflow Designer | ✕ | 節點屬性 = 裸 JSON textarea;文案含「Graph IR」編譯器術語 |
| Orchestrator 註冊/發布(D4/D5) | 完成,flag 預設關 | Agent 平台 → Orchestrators | ✕ | Worker pool / Context / Verifier / Budgets **四個欄位全是裸 JSON** |
| 多 Agent 協作執行 + 追蹤(D5) | 完成,flag 預設關 | Orchestrators 內 TestRunConsole | ✕ | 主控台與 trace 面板**完全英文** |
| Operations 治理面板(D7:指標/迴歸/灰度) | 完成,flag 預設關 | 側欄 Operations | ✕ | 整頁英文;Rollout 需手輸 GUID;「break-glass」直接入文案 |
| Eval 評測(E2:套件/執行/基線比對) | 完成,flag 預設關 | Operations 內 | ✕ | 候選 Skill 名靠手打;空狀態直接印環境變數名 `RUN_EVAL_ENABLED` |

### D. 已規劃但未交付 / 刻意未做(影響體驗閉環者)

| 能力 | 計畫出處 | 狀態 | 缺席造成的體驗後果 |
| --- | --- | --- | --- |
| Runs/Tasks 中心(不知 ID 也能查執行) | agent-architecture-improvements O2 | 未實作 | 營運只能拿著 GUID 逐筆查 |
| 「可核准給我」的核准佇列 | 同上 O3 | 未實作 | D7 核准機制已上,核准者卻找不到要核准什麼(C2) |
| 排程 / Webhook 觸發 | 同上 O5 | 未實作 | Agent 只能被人手動呼叫,無法「自動工作」 |
| 完成/升級通知收件匣 | 同上 §7 | 未實作 | 非同步結果(文件就緒、run 完成)全靠使用者自己回來看 |
| PDF / Word / Excel 解析 | 程式碼 ponytail 註記「後端支援時再加」 | 未排程 | 非技術者 9 成的文件進不了系統(C7) |
| 結構化指標/同業比較分析(E2) | context-enrichment E2 | blocked | 卡在資料源產品決策(Q1–Q3),「分析系統」的分析深度上不去 |
| Context Enrichment 前台(E1/E3) | context-enrichment | 後端完成、platform 連 controller 都沒有 | 純內部能力,對使用者不可見(合理,但說明投資去向) |
| MCP / 外部連接器 | agent-architecture-improvements S2/S3 | 需求驅動,刻意不做 | 合理的 YAGNI |

## 二、矛盾與衝突(依嚴重度排序)

### C1 — 核心價值鏈斷裂:能力都在,入口是斷的

文件頁空狀態寫著「尚無文件,新增一份讓 AI 檢索」——但上傳完之後,**畫面上沒有任何元素告訴使用者去哪裡問**。聊天頁的 Skill 路由其實會命中 `rag-qa`,副駕也有 `askKnowledgeBase`,但前者是不可見的暗機制,後者藏在預設收合、無文字的浮動按鈕裡。文件頁與聊天頁之間零串接。

證據 — 旅程 2 重建:非 ADMIN 使用者針對文件提問的唯一顯性路徑需先自行發現副駕;ADMIN 的替代路徑竟是「系統設定 → Agent Skills → rag-qa → 試跑」。

### C2 — 「業務核准者」拿到的是全站最工程化的頁面

D7 計畫明確把核准角色定義為**業務核准者**(separation of duties,刻意不要求技術 capability)——但 Approvals 頁整頁英文、文案含「anti-replay tokens」等安全術語,而且因為 O3 核准佇列未做,核准者必須**從別的管道拿到 Run ID(GUID)手動輸入**才能看到待核准項。角色設定與介面實作直接矛盾:為非技術角色設計的功能,做成了全站最需要技術背景的入口。

證據 — agent-platform-redesign 11.9(目標角色:業務核准者)× ApprovalInbox.tsx(全英文、Run ID 手輸)× O2/O3 未實作。

### C3 — 主打的 Agentic 能力 100% 預設關閉,且上線證據卡關

D1–D7、E1/E3、Eval、Prompt 版本化——方案的全部差異化賣點——每一個 flag 預設 false;D7 更是雙層白名單皆空(開了主旗標也沒有任何租戶被允許)。而讓一般使用者真正受惠的 D6(聊天走 Root Orchestrator)其 real-model 發布證據自 2026-07-29 對帳後仍 blocked。**從產品視角:「已交付」與「使用者可用」是兩張完全不同的表**,推廣時能現場展示的只有聊天 + 文件 + 副駕。安全上 fail-closed 全對;產品上意味著營收/推廣故事目前無法兌現。

證據 — backend/workflow settings 預設值盤點(10 個 flag 全 false);copilot-shared-core 7.6 與 agent-platform-redesign 11.8 的 release evidence blocked 記錄。

### C4 — 內部架構直接投影成資訊架構

「Agent Skill vs Business Workflow」是內部遷移期的雙軌架構決策(P0–P5,等 C8 證據才收斂),UI 卻把它原樣攤給管理者:兩個平級分頁,一個有建立精靈、一個只能上傳 zip。「Agents / Workflow Designer / Orchestrators」三分頁同樣是把 D1–D5 的系統分層當成選單。使用者被迫理解**系統的施工狀態**而非自己的任務。畫面上甚至出現「D3」「P1」等內部階段代號。

證據 — AgentPlatformView 三分頁純英文標籤;AgentTestConsole 文案「D3 測試主控台不提供 approval」;AgentSkillEditor 附件區「唯讀(P1 不執行)」。

### C5 — 同一站台三種語言帶,技術密度隨權限遞增

USER 層是打磨過的繁中人話 → Agent Builder 層中英混雜(「persisted immutable revision」嵌在中文句裡、「空集合會 fail-closed」)→ D4/D5/D7/Eval 層整頁英文。權限越高的使用者未必英文越好、越懂編譯器術語——尤其目標客群是非技術組織時,「管理者」多半是業務主管而非工程師。

證據 — 前端盤點:BuiltinAgentSkillView、ApprovalInbox、OperationsGovernanceView、EvaluationPanel、TestRunConsole/TraceOverlay 全英文;AgentEditor 術語混排實錄。

### C6 — 404 偽裝(安全正確)沒有配套的管理視角,變成「功能憑空消失」

Flag 關閉時路由回一般 404、入口整個不渲染——契約上刻意讓「沒開」「沒權限」「不存在」不可分辨,這在對外安全上是對的。但**系統內沒有任何一個畫面能讓有權限的管理者看到「哪些功能存在、目前開關狀態、為什麼我看不到」**;連 ADMIN 都得去讀部署文件和環境變數。安全契約(對外不可分辨)和管理可診斷性(對內可解釋)並不衝突,現在只做了前者。

證據 — cross-service-contracts「Feature-gate 404s」;旅程 5/6:「旗標未開時無任何提示解釋原因」;唯一例外是 Eval 空狀態——卻反而直接印出 `RUN_EVAL_ENABLED` 環境變數名,兩個極端。

### C7 — 「分析系統」的名實落差:格式天花板與計數器分析頁

整條文件鏈只吃純文字(`title` + `text`,前端只收 `.txt/.md`),PDF/Word/Excel 被檔案選擇器靜默過濾——非技術使用者手上的文件絕大多數進不了系統,且不會得到任何錯誤解釋。而名為「分析」的頁面實質是文件數/片段數兩張計數卡。同時檢索只有向量相似度一種(workflow 的 keyword/metadata/table/structured 檢索停在 Protocol 定義、無 adapter),E2 結構化分析 blocked。**以「Agentic 分析系統」為定位,分析的深度與廣度都還是骨架。**

證據 — DocumentsController(text ≤1M 字元、無解析步驟);SearchPort 五種檢索僅 vector 有實作;AnalysisView 盤點;context-enrichment E2 blocked(Q1–Q3 未決)。

### C8 — 最佳體驗藏最深,最差體驗掛「Designer」招牌

副駕是全站唯一能純自然語言完成任務、有 human-in-the-loop 確認、絕不代按發布的介面——卻預設收合、無標籤、無任何 onboarding。反之「Workflow Designer」聽起來是給人設計用的主打功能,實際是 Graph IR + 裸 JSON 屬性欄。**產品的能量分佈和名字給人的預期剛好相反。**

證據 — AppShell `defaultOpen=false`、無文字標籤;WorkflowDesigner 三欄「Node Catalog / Properties / Config (JSON)」。

### C9 — 表單化投資不一致:同一批使用者,兩種待遇

Business Rules 編輯器證明團隊能把 JSON AST 完全表單化(下拉選 Fact/運算子/動作、巢狀群組、模擬器)——但同一個 Agent 編輯器裡,Output contract 是裸 JSON textarea;同一個權限層的 Orchestrator 表單有四個裸 JSON 欄位;Eval 觸發要手打 Skill 名、Rollout 要手打 GUID——**明明系統都有對應的清單 API 可以做成選單**。降階(graceful degradation)不是原則,是各畫面自由發揮的結果。

證據 — BusinessRuleEditor(表單化)vs AgentEditor Output contract、OrchestratorsView 四個 JSON 欄、EvaluationPanel 手輸 candidate name。

### C10 — 展示即翻車的細節外洩

登入頁把種子帳號與密碼 `password123` 明碼印在卡片上;聊天空狀態印 API 端點與 proxy 細節;錯誤訊息一半人話化、一半原文透傳(pydantic/引擎/伺服器 message 直出);「session 已過期」不分原因。單項都小,但任何一場對客戶的 demo 都會同時踩到全部。

證據 — AuthPage 種子帳號區塊、ChatView 空狀態、錯誤處理雙軌並存盤點(旅程 7)。

## 三、合理且應保留的設計

### R1 — 安全與治理骨架是真材實料

fail-closed 全鏈(flag、白名單、unknown→deny)、404 不可探測、審批分權 + 防重播 + 一次性 effect、發布即不可變快照、ETag 樂觀鎖——這套治理是 B2B 銷售時的信任賣點,**審查中不建議為了 UX 動搖任何一條**;要改的是它們的「表達方式」,不是機制。

### R2 — 簡單模式雙門是正確的產品答案

五範本起手、中文寫規則(`nl_logic`)、存檔即試跑、驗證錯誤翻成人話、409 給「名稱已被使用,請換一個」——這一頁應該成為**全站管理面的設計規範**,而不是孤例。

### R3 — 副駕的權限邊界設計成熟

只改本地表單、發布三步永遠留給人按、刪除走對話內確認、token 不進 readable context、動作走既有 `apiFetch` 讓後端守真正的授權——這是「Agentic 但可控」的正確樣板,可直接沿用到更多場景。

### R4 — 幾處誠實的狀態設計值得推廣為全站標準

串流中斷保留已產出內容 + 錯誤氣泡;409 衝突橫幅講白話原因 + 重新載入按鈕;觀測數據缺失顯示「未知」而不假裝是 0;文件樂觀插入 + 輪詢。這些和 C10 的外洩並存,說明缺的不是能力,是一份被強制執行的文案/狀態規範。

## 四、判定:距離「非技術者可輕鬆操作的 Agentic 分析系統」有多遠

**現況一句話:這是一座工程品質很高的 Agent 治理平台,套著一層很薄的使用者殼。**「Agentic」的部分(多 Agent 協作、規則治理、審批、評測)已經建成但全數封存;「分析」的部分(格式、檢索模態、分析深度)還是骨架;「非技術者可輕鬆操作」目前只在三個地方成立:聊天、副駕、簡單模式。

差距不是「再加功能」,而是三件結構性的事:**把已建好的能力接上使用者(入口與旅程閉環)、把管理面翻譯成人話(術語降階規範)、把分析做成名符其實(格式與資料源)**。以下路線圖按此排序。

### P0 — 推廣前必修(不做,demo 就會失敗)

1. **接通核心旅程:** 文件列表每列加「問這份文件」動作(帶著文件脈絡開啟副駕或聊天);「就緒」狀態轉換給主動通知;聊天路由命中 Skill 時在氣泡上標示來源。
2. **PDF/Word 支援:** 格式天花板是硬傷,解析可先落在上傳端(前端/platform 轉純文字)以免動後端契約。
3. **副駕升格主入口:** 預設展開或首次登入引導、給按鈕文字標籤;它已是全站最好的介面,別藏。
4. **清除 USER 層外洩:** 種子帳號區塊移到開發模式限定、聊天空狀態改人話、「session 已過期」改「登入已逾時,請重新登入」。

### P1 — 管理面降階(讓「管理者=業務主管」成立)

1. **Approvals/Operations 中文化 + 佇列化:** 優先補 O3「可核准給我」清單(核准者不該手輸 GUID);Eval/Rollout 全部下拉選單化——清單 API 都已存在。
2. **訂立術語轉譯規範並全站套用:** revision→版本、Audience→誰可以使用、fail-closed→未設定時一律拒絕……以簡單模式的 `CODE_LABEL` 人話化機制為底,擴成全站錯誤與文案層。
3. **IA 去架構化:** 對使用者只呈現一個「技能」概念(雙軌收斂是 C8 gate 的內部事);Agent 平台三分頁改以任務命名;內部代號(D3/P1)全數移出畫面。
4. **功能開通狀態頁:** 給 ADMIN 一頁「本租戶已開通/未開通功能」視圖(內部可見,不影響對外 404 契約),終結「功能憑空消失」。
5. **裸 JSON 清零:** Output contract、Orchestrator 四欄、Designer 節點屬性,比照 Business Rules 編輯器表單化。

### P2 — 戰略決斷(PM 層要拍板的事)

1. **選定北極星:** 「數位員工平台」與「文件分析助手」是兩條不同的推廣故事;目前工程重心在前者、可用體驗只有後者。若主打分析,E2 的資料源三問(Q1–Q3)必須先有產品答案。
2. **D6 證據補齊:** real-model rerun + 迴歸 + 獨立 reviewer 完成前,Agentic 聊天無法對任何租戶開放——這是解鎖 C3 的唯一鑰匙,應列為工程最高優先。
3. **補齊 Agent 的「自動工作」故事:** O5 觸發器 + 通知收件匣,否則「數位員工」永遠是「要人叫才動的員工」。

方法:本審查由 6 條獨立調查線(plans 全量閱讀、前端逐元件盤點、platform API 面、backend/workflow 能力面、docs 契約、非技術者旅程重建)以逐檔閱讀方式完成,未使用文本關鍵字掃描;所有畫面文案與行為皆取自原始碼實錄,未執行系統。嚴重度標記(阻斷/高摩擦/小摩擦)沿用旅程調查線之定義。

## 五、後續計畫對照

本審查的發現轉入三個下游計畫,分工如下:

| 發現 | 下游計畫 |
| --- | --- |
| C1(核心價值鏈斷裂)、C7(格式天花板與分析深度)、C8(副駕發現性 vs Designer 招牌)、C10(展示外洩) | [plans/ux-core-journey](../ux-core-journey/) |
| C2(Approvals 工程化)、C4(內部架構投影)、C5(語言帶術語密度)、C6(404 偽裝無管理視角)、C9(表單化投資不一致) | [plans/admin-experience-downshift](../admin-experience-downshift/) |
| C3(Agentic 能力預設關閉/上線證據卡關)+ 第四節 P2 路線(北極星選定、D6 證據、自動工作故事) | [plans/north-star-and-activation](../north-star-and-activation/) |
