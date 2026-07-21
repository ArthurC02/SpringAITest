# 規格 — 副駕共用核心層(Copilot Shared Core)

> 狀態: **規劃中。** 承接 [01-plan.md](01-plan.md);本檔是 **WHAT**(定案的行為與契約),`03-design.md` 才給簽章與落地順序。
> 本檔不重述 01-plan 的探查證據,只在需要時引用其節次。

---

## 1. 範圍界線

**只動 `platform/`,加上 `frontend/` 一處(HttpAgent 帶 JWT)。** `backend/`、`workflow/`、`infra/` 零改動。

對外契約**完全不變**:

- `POST /api/chat`、`POST /api/chat/stream`、`GET /api/chat/history` 的 request / response 形狀不變。
- 鏈路 A 的 SSE 仍是手刻 `data:`(**無空格**),`event:error` 幀語意不變。
- AG-UI 仍輸出協定標準 `data: `(**有空格**),事件型別集合不變。
- ApiError `{timestamp, status, message, fieldErrors}` 不變。

**新增 hardening 的對外行為**:AG-UI 收到 `401` 時前端必須沿用全域登出流程清除 session；匿名 `/api/chat*` 保留短期對話，但不再 recall/remember mem0。其餘協定格式不變。

### 1.1 P0 前置(03-design 核對後補)

`Platform.Service` **完全不引用 `Microsoft.Agents.AI`**(現只有 Logging.Abstractions + RabbitMQ.Client)。本規格假設「`ChatService` 改為對共用 agent 呼叫」——**現況編譯不過**。

落地順序上須先加 `PackageReference`(兩行),**不搬檔** —— 搬檔會逼 57 個測試遷專案,代價遠大於收益。另注意 `Microsoft.Agents.AI.Hosting` 是 preview 且目前僅靠傳遞相依進來,正面使用必須明示版本。

---

## 2. P1 — 認證與租戶隔離

### 2.1 端點認證

`/api/copilot/agui` 改為要求認證。未帶 / 帶無效 JWT → `401`。

> 這與 `/api/chat` 的 `AllowAnonymous` 姿態**刻意不同**。`/api/chat` 允許匿名裸聊(不路由 skill、不寫記憶、不持久化,見 `ChatService.cs:249-252`);副駕的存在意義是操作應用程式與查租戶資料,匿名副駕沒有有意義的行為,不需保留該姿態。

### 2.2 前端帶 JWT

`frontend/src/App.tsx:12` 的 `new HttpAgent({ url: '/api/copilot/agui' })` 目前在模組層級建立,**拿不到 session**。須改為在元件內依 `session.token` 建立,並帶 `Authorization: Bearer`。

- token 變動(登入 / 換帳號)時 agent 必須重建,不可沿用舊 token。
- 既有禁令維持:**不得**把 token 放進 `useCopilotReadable`(見 `frontend/AGENTS.md`)。
- `HttpAgent` 的 fetch wrapper 收到 `401` 時，必須呼叫既有全域 logout，清除 session 與 chat localStorage 後回登入頁；不得留下「看似登入、每次副駕請求都失敗」的狀態。

### 2.3 未登入時的副駕

`CopilotKit` provider 本就掛在登入判斷之後(守衛在 `App.tsx:18`,`<CopilotKit>` 在 `App.tsx:21`),未登入使用者看不到副駕。因此 §2.1 的 `401` 在正常流程中不會發生;它是防止直接打端點的邊界防護,不是使用者可見路徑。

### 2.4 租戶隔離

註冊 `SessionIsolationKeyProvider`,isolation key 固定為 **JWT 的 `{tenant}:{user}`**，不得取自 wire 上的 `threadId` 或任何 request body 欄位。

- `Strict = true`(fail-closed):tenant 或 user 任一缺失／空白時視為無身分並拋例外；不得產生 `":"` 等共享 key，也不得退回全域命名空間。
- 驗收必須驗**內容隔離**:不同租戶以同一 `threadId` 提問,第二個租戶不得讀到第一個的內容。**不可用 nullity 判定** —— `GetSessionAsync` 對未知 key 會自動建立空 session 而非回 `null`(01-plan §2 spike 實測)。

### 2.5 匿名與 `Strict=true` 的衝突(03-design 核對後補)

`/api/chat` 維持 `AllowAnonymous`，但不使用 AG-UI 的 strict isolation store。`ChatAssistant` 明確 `withIsolation:false`：登入請求的 session `conversationId` 已由推導層前綴為 `{tenant}:{user}:...`，匿名則保留既有 caller-scoped conversationId 短期連續性。這不是放寬 AG-UI 隔離；兩條鏈路的隔離責任分別在 strict provider 與 key derivation。

---

## 3. P2 — 記憶收斂

### 3.1 刪除自寫實作

`platform/src/Platform.Service/InMemoryChatMemoryStore.cs` 與 `Abstractions/IChatMemoryStore.cs` 刪除,改用框架 session store。

### 3.2 記憶鍵

沿用 `ChatService.cs:202-213` `DeriveMemoryKeys` 的防 IDOR 語意,但拆成兩層:

- **AG-UI 隔離維度** → `SessionIsolationKeyProvider` 的 `{tenant}:{user}`(§2.4)。
- **ChatAssistant conversation 維度** → 已登入為 `"{tenant}:{user}:{conversationId}"`，當作 session `conversationId`；匿名維持既有可連續的 client conversation key。

已登入時**不信任 request body 的 `userId`**(現行語意,不得放寬)。

### 3.3 視窗語意

`SlidingWindowCompactionStrategy` 以 `CompactionTriggers.MessagesExceed(20)` 觸發,保留現行「20 則」語意。

- `minimumPreservedTurns` 是**下限**不是上限,須設得夠低才不會抑制觸發(spike 實測:設 20 時 26 則完全不觸發)。
- system 訊息不進 history(走 `ChatOptions.Instructions`),因此不會被裁 —— 與現行行為一致。
- **裁切以整個 turn 為原子單位**,tool call 與其 result 不會被拆散。這是相對現行實作的**行為改善**,不是回歸。

### 3.4 AG-UI 側的記憶

`MapAGUI` 須改用能從 DI 解析 agent 的 overload(`MapAGUI(IHostedAgentBuilder, pattern)`),搭配 session store。

副駕取得伺服器端記憶後,前端仍會重送完整 message 陣列 —— **兩者不得重複累加**。去重優先採 message ID；對 assistant 訊息必須有保守的 role/content/tool-call fingerprint fallback，以處理 client 重建 assistant ID。user 訊息不得因內容相同而被錯誤吞掉。驗收須確認同一 `threadId` 多輪對話不會出現訊息重複。

---

## 4. P3 — 共用 context

> **本節於 03-design 核對後修正。** 原規格寫「以**一個** `AIContextProvider` 承接三件事」,經實測不成立,理由見 §4.0。

### 4.0 兩處修正(實測結論,不得回退)

1. **mem0 recall 必須走 `Instructions`,不可走 `Messages`。** 實測 `AIContext.Messages` 注入的訊息**會被寫進持久化 chat history 且每輪重複累積**,prompt 會逐輪膨脹。`Instructions` 則不進 history,且與 agent 自身 instructions 換行共存(`copilotInstructions` 不會被蓋掉)。
2. **職責必須拆成兩個元件。** 路由短路發生在 `ChatClientAgent` **之外**(§5.2),因此命中 skill 的那一輪 `StoreAIContextAsync` 根本不會執行 —— 若把 remember / 持久化放在 provider,該輪不會被 mem0 記住也不會持久化,是**鏈路 A 的直接回歸**。

### 4.1 拆分後的職責

| 元件 | 位置 | 職責 |
| --- | --- | --- |
| `AIContextProvider` | `ChatClientAgent` **內** | 僅已登入者的 mem0 recall → 注入 `Instructions`;護欄 prompt(`ChatGuardPrompt` 語意)→ 注入 `Instructions` |
| `ChatTurnRecorder : DelegatingAIAgent` | `ChatClientAgent` **外** | 僅已登入者的 mem0 remember + 對話持久化到 backend(短路輪也會執行) |

### 4.2 不得改變的語意

- 已登入的非路由主 run：mem0 recall 在 prompt 前、remember 在**完整回覆後**(含工具融合結果)。路由命中時，路由／summary 呼叫刻意不帶 history 或 mem0；但已登入使用者的最終摘要仍會在 recorder 被 remember。
- mem0 全程 **best-effort**，由 `ChatContextProvider`／`ChatTurnRecorder` 的 pipeline 邊界保證；即使任一 `IMem0Client` 實作擲例外，也必須記錄後降級（recall 視為空、remember 視為 no-op），不得讓聊天中斷。
- 對話持久化:鏈路 A 阻塞路徑失敗**往上拋 500**、串流路徑 best-effort 只記 warning —— 此差異是現行行為,P3 維持。
- 匿名(鏈路 A 才可能出現)不 recall/remember mem0、不持久化、`/api/chat/history` 回空陣列。

### 4.3 副駕的對話會進歷史

P3 之後,副駕對話與 ChatView 對話**同樣持久化到 `(tenant_id, user_id)`**,因此會一起出現在 `GET /api/chat/history`。

這是刻意的:對非技術使用者而言「我跟這個系統講過什麼」應該只有一份,不該因為用了哪個聊天框而分裂。**若驗收發現這造成混淆,退路是在持久化時加來源標記,而非分兩張表。**

---

## 5. P4 — 路由共用

skill 路由(`TryRouteAndExecuteAsync` / `BuildToolsAsync` / `SkillCatalogToTools` / `RouteAsync` / `InvokeSkillToolAsync` / `BuildSummaryMessages`)抽成 `AIAgentBuilder.Use(...)` middleware。

### 5.1 不得改變的語意

以下全部維持現行行為,**逐項不得放寬**:

- 匿名不路由。
- 角色過濾(`required_role` 空 / `USER` 視為無限制)。
- 跳過 `source == "builtin" && name.StartsWith("template_")`。
- `SingleRequiredStringKey`:恰好一個必填字串參數才成為工具,其餘**靜默跳過**。
- 路由最多重試兩次;`MatchTool` 先全等再寬鬆包含(取最長名)。
- 路由決策**刻意不帶短期歷史與 mem0**。
- `kb_query` ABSTAIN → `rag_qa` 確定性兜底。
- 目錄取得失敗 → best-effort 退回,聊天不炸。
- 單一工具呼叫失敗 → 回錯誤字串給模型,不炸整輪。
- **summary prompt 常數不得修改**(現行測試以字串相等斷言)。

### 5.2 短路語意(已知天花板)

路由命中時 middleware 直接回傳,不委派內層 agent,因此該輪 client tools 不會被呼叫。理由與代價見 01-plan §5.2。

須留 `ponytail:` 註解標明天花板與升級路徑。

### 5.3 副駕的工具組成

P4 之後副駕同時具備:

- **server tools** — skill 路由(middleware,不冒泡到前端)
- **client tools** — 前端 `useCopilotAction`(冒泡成 `TOOL_CALL_*`)

兩者在框架內是**相加**關係(01-plan §2 spike 實測),不需額外合併邏輯。

---

## 6. 前端改動範圍

**只有一處**:`App.tsx` 的 HttpAgent 建立方式(§2.2)。

`useCopilotReadable`(4 處)、`useCopilotAction`(4 個)、`renderAndWaitForResponse` 的刪除確認、`CopilotSidebar` 的 `instructions` / `labels` **全部不動**。01-plan §6 列的前端增強(suggestions、多參補問、generative UI)不在本計畫。

---

## 7. 測試要求

### 7.1 先補行為測試,再動實作

> **本節於 04-acceptance-test 核對後修正。** 原寫「現行行為沒有回歸測試」**偏重**:`ChatSkillRoutingTests`(37 案)+ `ChatServiceTests`(24 案)實質覆蓋了角色過濾、`template_*` 過濾、6 種 skill invoke 例外、4 種 catalog 例外、重試耗盡、mem0 順序、串流半截不持久化。

真正的問題是**斷言面綁在即將被移除的接縫上**,因此這些測試無法作為等價證明:

1. 失敗路徑測試直接呼叫 `svc.BuildToolsAsync(...)` → `tool.InvokeAsync(...)`,而 §5 明文要把它抽進 middleware。安全網若跟著實作一起被重寫,就失去證明效力 —— A 組須改由 `ChatAsync` / HTTP 驅動同樣行為。
2. `GuardPrompt` 逐字常數存了兩份。§4 把護欄搬到 `Instructions` 後這兩份**必然紅**,而一紅就會被順手改掉 —— 這正是「無法證明等價」的具體機制。

在任何實作開始前,須先補上行為層測試,涵蓋:

- 路由命中 / 未命中 / 目錄失敗 / 工具失敗四條路徑的**可觀察結果**(不是 prompt 內容)。
- mem0 recall / remember 的呼叫順序。
- 匿名與登入的分歧。

否則無法證明重構前後行為等價。

### 7.2 AG-UI 側須補的測試

現行 `platform/tests/Platform.Web.Tests/CopilotAguiApiTests.cs` 只送 `tools: []` 且只斷言兩個事件型別,`FakeChatClient` 的 `GetService` 回 `null` 故結構上不可能產生 tool call。須擴充至能覆蓋:

- 非空 `tools` → `TOOL_CALL_START` / `ARGS` / `END`,含工具名與參數。
- 認證:無 JWT → 401。
- 租戶隔離:同 `threadId` 跨租戶不互見(驗**內容**,非 nullity)。
- 多輪:同 `threadId` 第二次提問記得第一次。

### 7.3 真鏈路驗證

每個 phase 收尾派 `e2e-verifier` 打真服務一次。專案記憶 fakes-hide-real-behavior:全手寫 fake 會掩蓋真實序列化與跨服務差異,單元全綠不等於能動。`scripts/verify-copilot-shared-core.ps1` 可作為 black-box smoke companion，但不能檢查模型輸入、session 去重/工具配對、mem0 儲存或 chunk 時序；其 `-Rebuild` 使用 `mock-gpt`，不能證明 routing。C-03/C-04/C-05/C-07/C-08 仍須具名 integration tests、`e2e-verifier` trace 與必要的 browser/proxy 檢查，routing 使用真實模型。

---

## 8. 開放問題(03-design 須拍板)

1. 路由 middleware 掛在 `AIAgentBuilder` 的哪一層?與 `FunctionInvokingChatClient`(執行 client tools 的那層)的相對順序為何?
2. mem0 的 `uid` 在 P3 後由誰推導 —— 沿用 `DeriveMemoryKeys`,還是也交給 isolation key?
3. `ChatController` 改為對共用 agent 呼叫後,`event:error` 幀與 `X-Auth-Invalid` 的產生點落在哪?
4. 鏈路 A 的阻塞 / 串流兩種持久化失敗語意差異(§4.1),在共用的 `StoreAIContextAsync` 裡如何表達?
5. P2 刪除 `IChatMemoryStore` 後,現有以該介面為斷言對象的測試如何改寫?
