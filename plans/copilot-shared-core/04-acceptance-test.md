# 驗收測試 — 副駕共用核心層(Copilot Shared Core)

> 狀態: **已實作，持續 hardening。** 依據: [01-plan.md](01-plan.md)、[02-spec.md](02-spec.md)(驗收條件來源,特別是 §7)。
>
> 本檔是本計畫的**安全網規格**。02-spec §7.1 指出的核心風險是:現行行為沒有回歸測試,只靠 prompt 字串相等斷言撐著(`platform/tests/Platform.Service.Tests/ChatServiceTests.cs:68`、`ChatSkillRoutingTests.cs:483`),因此**無法證明重構前後行為等價**。A 組的存在就是為了先把這件事補起來。

---

## 0. 三組的分工與硬順序

| 組 | 意義 | 何時必須綠 |
| --- | --- | --- |
| **A — 重構前行為安全網** | 釘住**現行**可觀察行為。重構前跑得過、重構後也必須**一行不改**跑得過 | **任何實作動工前**全綠;之後每個 phase 收尾重跑 |
| **B — 各 phase 新能力驗收** | P1 認證隔離、P2 記憶、P3 context、P4 路由共用各自的**新**行為 | 各 phase exit gate |
| **C — 真鏈路 e2e** | 手寫 fake 掩蓋不了的部分(序列化、跨服務、真 DB) | 每個 phase 收尾派 `e2e-verifier` 打一次 |

**硬順序**:A 全綠 → P1 → P2(§5.1 認證先於記憶,不可對調)→ P3 / P4(無強制順序)。

---

## 1. 驗收原則(本案專用,違反即不算驗收)

1. **斷言可觀察行為,不斷言 prompt 內容。** 合法斷言對象:HTTP status / body / raw SSE bytes、`FakeWorkflowService.SkillInvokes`(哪個 skill、什麼參數、什麼順序)、`FakeMem0Client.Remembered`、`FakeConversationStore.Saved`、送進 `IChatClient` 的 message 清單、AG-UI 事件序列。**不合法**:`agent.CompleteCalls[0][0].Content` 的字串相等、private method 有沒有被呼叫。
   - 唯一例外:02-spec §5.1 最後一條明文要求「summary prompt 常數不得修改」,該條**允許**保留字串相等斷言(見 `B-P4-11`),但它是**護欄**不是行為驗收,不可拿來取代 `A-01`。
2. **租戶隔離必須驗內容,不可驗 nullity。** ⚠️ **本案最大的坑**:框架的 `GetSessionAsync` 對未知 key **會自動建立一個空 session 而不回 `null`**(01-plan §2 spike 實測)。若用 `Assert.Null(session)` 判定隔離,**無論隔離有沒有生效都會通過** —— spike 階段已因此得過一次假警報。隔離驗收一律寫成:租戶 B 讀到的 session **內容不含**租戶 A 寫入的字串。
3. **規格裡的數字要 on-point + off-point。** 視窗 20 → 測 20(保留全部)與 21(裁最舊);路由重試「最多兩次」→ 測第 2 次命中(成功)與第 2 次仍 NONE(耗盡兜底)。等價類安全內部的值(例如用 5 則測 20 視窗)抓不到打錯的數字。
4. **失敗注入含「沒有回應」的等價類。** 不只 HTTP 錯誤碼,還要有傳輸例外(`HttpRequestException` / `TaskCanceledException`)與**串流中途爆炸**(半截回覆不得持久化)。
5. **一等價類一代表值。** 同分支多輸入併 `[Theory]`;不為覆蓋率測 DTO / getter / 框架自身行為。
6. **xUnit + 手寫 fake,不引入 mocking 套件。** 服務層測試在 `Platform.Service.Tests`,端點層在 `Platform.Web.Tests`(`WebApplicationFactory`)。
7. **A 組的斷言面必須跨得過重構。** 見 §2「既有測試的結構性問題」——現行測試大量綁在 `ILlmAgent` / `BuildToolsAsync` / `LlmTool.InvokeAsync` 這些**會在 P2/P4 被刪掉**的接縫上,直接沿用等於安全網跟著實作一起消失。

---

## 2. 現況基線(實際讀過的覆蓋現況)

> 本節為**重構前**基線快照,現行覆蓋見 `platform/tests/Platform.Web.Tests/CopilotAguiApiTests.cs`(現有 27 個 `[Fact]`/`[Theory]`)與 `ChatSkillRoutingTests.cs`,不再是現況。

### 2.1 已有的覆蓋

| 檔案 | 內容 | 評價 |
| --- | --- | --- |
| `Platform.Service.Tests/ChatSkillRoutingTests.cs`(37 案) | 角色過濾、匿名不讀目錄、builtin/custom 平等、`SingleRequiredStringKey` 跳過、`template_*` 過濾、輸出鍵萃取、skill invoke 失敗 6 種例外 `[Theory]`、catalog 失敗 4 種例外 `[Theory]`、路由命中/NONE/寬鬆比對/重試/耗盡、mem0 順序、串流摘要 | **實質覆蓋比 01-plan §8 的描述好**。真正的問題不是「沒測」,是**斷言面綁在會被刪掉的接縫上**(見 2.2) |
| `Platform.Service.Tests/ChatServiceTests.cs`(24 案) | 護欄為第一則 system、mem0 前言、短期記憶跨輪、阻塞持久化失敗拋、串流持久化失敗 best-effort、串流中途失敗不持久化半截、記憶鍵 fallback 三態、登入時不信任 body `userId`、匿名不持久化/history 空 | 同上 |
| `Platform.Service.Tests/InMemoryChatMemoryStoreTests.cs`(3 案) | 20 on-point、21 off-point、未知對話回空 | 邊界值做對了。但 **P2 要刪掉這個類別**,測試需等價搬遷(02-spec §8 開放問題 5) |
| `Platform.Web.Tests/ChatApiTests.cs`(16 案) | `data:` 無空格的 raw bytes 契約、`event:error` 幀、chunk 含換行拆多行、400 空訊息、`X-Auth-Invalid`、匿名 history 空陣列 | **A 組最可靠的地基**,這批斷言天生跨得過重構 |
| `Platform.Web.Tests/CopilotAguiApiTests.cs`(1 案) | 冒煙:200 + `text/event-stream` + 含 `RUN_STARTED` / `TEXT_MESSAGE_CONTENT` | 見 2.3,幾乎等於沒測 |

### 2.2 結構性問題(A 組必須繞開的,不是「補幾條」就好)

- **既有路由/記憶斷言綁在 P2/P4 會刪除的接縫上。** `ChatSkillRoutingTests` 的失敗路徑測試直接呼叫 `svc.BuildToolsAsync(...)` 再 `tool.InvokeAsync(...)`(`:379-383`、`:408-409`);02-spec §5 明文要把 `BuildToolsAsync` / `SkillCatalogToTools` 抽進 middleware。同理 `agent.CompleteCalls`(`FakeLlmAgent`)在 P2/P3 改用框架 session store + `AIContextProvider` 後,`ILlmAgent` 這層是否還存在由 03-design 決定。**結論:這些測試不能當安全網用** —— 安全網若跟著被重寫,就沒有證明等價的效力。A 組必須把同樣的行為**改由 `ChatAsync` / `StreamChatAsync` 或 HTTP 端點**驅動,只斷言 fake 下游記到什麼。
- **prompt 字串相等斷言重複兩份且與程式碼同步。** `ChatServiceTests.cs:67-68` 與 `ChatSkillRoutingTests.cs:482-483` 各存了一份逐字 `GuardPrompt` 常數。P3 要把護欄從 messages 搬到 `ChatOptions.Instructions`(02-spec §4),這兩份斷言**必然紅**,而它們一紅就會被順手改掉 —— 這正是「無法證明等價」的具體機制。A 組要在它們之外,建立不依賴護欄落點的行為斷言。

### 2.3 明確缺口

| # | 缺口 | 影響 |
| --- | --- | --- |
| G1 | **`FakeMem0Client` 無法擲例外**(`Fakes.cs:201-214`,`RecallAsync`/`RememberAsync` 皆無失敗開關) | 根 AGENTS.md 與 platform/AGENTS.md 都宣稱「mem0 best-effort,錯誤吞掉聊天不中斷」,**這個安全語義目前零測試背書**,只寫在註解裡。A 組必須先擴充 fake 才能寫 `A-14` |
| G2 | **`FakeChatClient.GetService` 回 `null`**(`FakeChatClient.cs:26`) | 框架的 `FunctionInvokingChatClient` 取不到 `ChatClientMetadata` / function-invocation 支援,**結構上不可能產生 tool call**。也就是 AG-UI 的 client tools 迴路在單元層**完全未測**(e2e 測過,單元沒有) |
| G3 | **`CopilotAguiApiTests` 只送 `tools: []`,只斷言 2 個事件型別** | 沒有 `RUN_FINISHED`、沒有 `TEXT_MESSAGE_START/END`、沒有多輪、沒有身分、沒有租戶。P1–P4 的 AG-UI 側幾乎是白紙 |
| G4 | AG-UI 端點**零認證測試** | 現行是 `AllowAnonymous`(`Program.cs:275`),P1 改 401 後無舊測試可比對 |
| G5 | **`FakeLlmAgent` 是 singleton、`FakeWorkflowService` 是 scoped**(`TestWebAppFactory.cs:35,52`) | 跨請求測試(多輪記憶、租戶隔離)拿得到同一顆 `ILlmAgent` 但**拿不到同一顆 `FakeWorkflowService`** 來累積 `SkillInvokes`。B-P4 的 Web 層驗收需要先解決這件事(改 singleton 或加共用收集器) |
| G6 | 短期記憶只在 `InMemoryChatMemoryStore` 層測邊界,**未在 `ChatService` 層測 20/21 邊界** | `ChatServiceTests.cs:104` 只驗「第二輪看得到第一輪」,刪掉 store 後 20 這個數字沒有任何端到端背書 |
| G7 | **tool call / tool result 配對從未被測** | `InMemoryChatMemoryStore` 的 `RemoveAt(0)` 會拆散配對(01-plan §2),是未爆彈;現行 3 條 store 測試只用純 user 訊息,測不到 |

---

## 3. A 組 — 重構前必須先補的行為安全網(24 案)

> **全部在任何實作動工前寫完並全綠。** 斷言面一律選在「重構不會移動」的位置:HTTP 端點、`FakeWorkflowService` 的呼叫記錄、`FakeMem0Client.Remembered`、`FakeConversationStore.Saved`、送進底層 chat client 的 message 清單。
>
> 前置(A 組共用):`FakeMem0Client` 擴充失敗開關(補 G1);`FakeChatClient` 擴充為可錄製收到的 messages 並可腳本化多輪回覆(補 G2 的前半)。

| ID | Phase | 前置條件 | 動作 | 預期結果(可判定斷言) |
| --- | --- | --- | --- | --- |
| `A-01` | 全 | 目錄含 `kb_query`(單必填 `query`);路由回 `kb_query`;skill 回 `{output:{business_result:"毛利率 32.8%"}}` | `ChatAsync("這季毛利率多少?", ..., UserA)` | `SkillInvokes` 恰 1 筆,`Name=="kb_query"`、`Input["query"]=="這季毛利率多少?"`;回覆 `Reply` 是摘要輸出**而非** skill 原始 JSON;`Remembered` 記的是最終摘要 |
| `A-02` | 全 | 同上,路由回 `NONE` | `ChatAsync` | `SkillInvokes` 為空;`Reply` 非空純聊天回覆;不擲例外 |
| `A-03` | 全 | `ThrowOnCatalog` `[Theory]`:`WorkflowInvocationException(502)` / `HttpRequestException` / `TaskCanceledException` / 壞 JSON | `ChatAsync` | 四例皆回正常 `Reply`、不擲例外、`SkillInvokes` 為空(best-effort 退化) |
| `A-04` | 全 | `ThrowOnSkillInvoke` `[Theory]`:`WorkflowNotFound` / `Forbidden` / `BadInput` / `InvocationException` / `HttpRequestException` / `TaskCanceledException` | `ChatAsync`,路由命中該 skill | 六例皆**不擲例外**,對外仍得到一則回覆(單一工具失敗不炸整輪)。**經 `ChatAsync` 驅動,不得直接呼叫 `tool.InvokeAsync`** |
| `A-05` | 全 | 目錄含 USER skill + ADMIN skill;路由都回 ADMIN skill 名 | (a) `Role=USER` (b) `Role=ADMIN` | (a) ADMIN skill **從未出現在** `SkillInvokes`;(b) 出現。`required_role` 為空 / `"USER"` 皆視為無限制 |
| `A-06` | 全 | 目錄有料、mem0 有既存資料;`userCtx = null` | `ChatAsync` 與 `StreamChatAsync` | `CatalogContexts` 為空(從未取目錄)、`SkillInvokes` 為空、**`RecallAsync`／`RememberAsync` 均從未呼叫**、`FakeConversationStore.Saved` 為空。匿名不得共享或污染 mem0。 |
| `A-07` | 全 | 目錄含 `template_infer`(`source=="builtin"`)與 `template_x`(`source=="custom"`);路由分別回這兩個名字 | `ChatAsync` ×2 | builtin `template_*` **不在** `SkillInvokes`;custom 同前綴者**在** |
| `A-08` | 全 | `[Theory]` 目錄項:`input_schema=null` / 兩個必填 / 必填非字串 / 單必填字串+多選填 | 路由回該 skill 名後 `ChatAsync` | 前三者 `SkillInvokes` 為空(靜默跳過,不擲例外、不寫 error);第四者被呼叫且 `Input` **只含**該必填鍵 |
| `A-09` | 全 | 路由腳本 `[Theory]`:(a) 1st NONE / 2nd 命中 (b) 1st NONE / 2nd NONE (c) 1st 命中 | `ChatAsync` | (a) `SkillInvokes` 恰 1 筆;(b) 為空且走純聊天兜底;(c) 恰 1 筆。**(a)(b) 是「最多重試兩次」的 on/off-point** |
| `A-10` | 全 | 目錄同時含 `kb` 與 `kb_query`;路由回覆是含 `kb_query` 的整句話(非全等) | `ChatAsync` | 被呼叫的是 `kb_query`(**最長名**),不是 `kb` |
| `A-11` | 全 | `SkillOutputByName`:`kb_query` 回 ABSTAIN 形狀、`rag_qa` 回正常答案;路由命中 `kb_query` | `ChatAsync` | `SkillInvokes` 恰 2 筆且**順序為** `kb_query` → `rag_qa`;回覆帶誠實標示 |
| `A-12` | 全 | `kb_query` 回正常答案 | `ChatAsync` | `SkillInvokes` 恰 1 筆,**不含** `rag_qa`(兜底不誤觸發) |
| `A-13` | 全 | 已登入；`RecallResult="- 使用者是租戶 A\n"`;路由未命中、正常主 run | `ChatAsync` | recall 內容出現在送進 chat client 的 **Instructions**（非 history messages）中；`Remembered` 恰 1 筆且內容是完整最終回覆。**順序**:recall 早於模型呼叫,remember 晚於最終回覆。skill 命中時的「不 recall、仍 remember 最終摘要」另由 `B-P4-07` 驗。 |
| `A-14` | 全 | `[Theory]`:(a) `RecallAsync` 擲例外 (b) `RememberAsync` 擲例外，fake 直接違反 `IMem0Client` 契約 | `ChatAsync` 與 `StreamChatAsync` | 兩例四路徑皆**正常回覆、不擲例外**；(a) Instructions 不含 mem0 前言但其餘不變。此案釘住 best-effort 位於 **pipeline 邊界**，而非只依賴 `Mem0Client` 的內部慣例。 |
| `A-15` | 全 | `FakeConversationStore.ThrowOnAdd = true`;已登入 | `POST /api/chat`(HTTP 層) | `500`,body 為 ApiError `{timestamp,status,message,fieldErrors}`,`message` 為固定中文泛化訊息(不洩漏例外細節) |
| `A-16` | 全 | 同上 | `POST /api/chat/stream` | chunks **照常全數送達**、**無** `event:error` 幀、回應正常結束;`Remembered` 仍有 1 筆。與 `A-15` 合為決策表兩半,**此差異是刻意的,P3 不得抹平** |
| `A-17` | 全 | `ThrowAfterChunks = 1`(串流吐第 1 塊後爆) | `POST /api/chat/stream` | 已送出的 chunk 保留 → 接 `event:error\ndata:<固定中文>\n\n` → 正常結束(非斷線);`FakeConversationStore.Saved` 為空(**半截回覆不得持久化**) |
| `A-18` | 全 | 同一 `conversationId` 連續兩輪 | `ChatAsync` ×2 | 第二輪送進 chat client 的 messages 含第一輪的 user 與 assistant 內容,且最後一則是本輪 user 訊息 |
| `A-19` | 全 | 同一 `conversationId` 累積出 (a) 20 則 (b) 21 則歷史 | 再發一輪 | (a) 20 則全在;(b) 最舊一則不在、其餘 20 在。**on-point / off-point,是 P2 等價的唯一數字證據(補 G6)** |
| `A-20` | 全 | 已登入 `UserA`,request body 帶 `userId="別人"`;另一使用者同 `conversationId` | `ChatAsync` ×2 | body 的 `userId` 被忽略;兩使用者互不看見對方訊息;mem0 uid 取自 JWT 身分(`"{tenant}:{user}"` 形狀) |
| `A-21` | 全 | 兩個租戶各有歷史 | `GET /api/chat/history` (a) 匿名 (b) 租戶 A JWT | (a) `200` + 空陣列(**不是 401**);(b) 只含租戶 A 的資料,不含租戶 B |
| `A-22` | 全 | 串流 chunk 含換行 | `POST /api/chat/stream` raw bytes | 每幀為 `data:<value>`(**冒號後無空格**)、以空行結尾;含換行的 chunk 拆成多行 `data:` |
| `A-23` | 全 | AG-UI 冒煙(現行 `AllowAnonymous` 姿態) | `POST /api/copilot/agui` raw bytes | 幀為 `data: `(**冒號後有空格**);事件序列含 `RUN_STARTED` → `TEXT_MESSAGE_START` → `TEXT_MESSAGE_CONTENT` → `TEXT_MESSAGE_END` → `RUN_FINISHED`(補 G3 的基線半邊) |
| `A-24` | 全 | 空白 `message` | `POST /api/chat` 與 `/api/chat/stream` | 皆 `400` + ApiError 四鍵齊全 |

---

## 4. B 組 — 各 phase 新能力驗收

### 4.1 P1 — 認證與租戶隔離(8 案)

| ID | 前置條件 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `B-P1-01` | 無 | `POST /api/copilot/agui` **不帶** `Authorization` | `401`；`A-23` 需同批改為帶 JWT。 |
| `B-P1-02` | 過期 / 簽章錯誤 / 格式錯誤 / 缺 tenant claim / 缺 user claim token(`[Theory]`) | 同上 | 全部 `401` 或在 strict store 以受控錯誤 fail-closed；不進入 agent、不建立可共享 session，尤其不得產生 `":"` 或部分 identity key。 |
| `B-P1-03` | 有效 JWT | 同上 | `200`,`text/event-stream`,事件序列同 `A-23` |
| `B-P1-04` | ⚠️ **本案最重要的一條。** 租戶 A 與租戶 B 各持有效 JWT,**使用同一個 `threadId="t1"`** | A 先說「我的密語是 XYZZY」,B 再以同 `threadId` 問「我的密語是什麼」 | B 這一輪送進模型的 messages **不含** `"XYZZY"`,B 的回覆也不含。isolation key 為 JWT `{tenant}:{user}`；**禁止以 `Assert.Null(session)` 判定**。 |
| `B-P1-05` | 同租戶不同使用者,同 `threadId` | 同 `B-P1-04` 手法 | 同樣不互見；驗證 `{tenant}:{user}` 的 user 維度不可省略。 |
| `B-P1-06` | DI 容器**未註冊** `SessionIsolationKeyProvider` 的組態 | 帶有效 JWT 打 AG-UI | `500`(fail-closed)。**不得**回 `200` 而默默共用全域命名空間。此案證明 `Strict=true` 真的開著 |
| `B-P1-07` | 有效 JWT(租戶 A);request body 的 `threadId` / `state` / `forwardedProps` 塞入偽造的租戶 B 識別 | 打 AG-UI | isolation key 取自 **JWT**,偽造欄位完全無效:租戶 B 的內容仍讀不到(以 `B-P1-04` 手法驗) |
| `B-P1-08` | 前端 `App.tsx` | (a) 登入 → 登出 → 換帳號登入 (b) AG-UI 請求回 `401` (c) 檢查 `useCopilotReadable` | (a) `HttpAgent` 隨 `session.token` 重建,不沿用舊 token；(b) 走既有全域 logout，清 session/chat localStorage 並回登入頁；(c) token **不在**任何 `useCopilotReadable`。層級:frontend lint/build + browser e2e |

### 4.2 P2 — 記憶收斂(6 案)

| ID | 前置條件 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `B-P2-01` | 有效 JWT,同一 `threadId` | 第一輪「我叫小明」,第二輪「我叫什麼」 | 第二輪送進模型的 messages 含第一輪內容。**現行 production 此項為 False**(01-plan §2 spike),這是新行為,不是回歸 |
| `B-P2-02` | 框架 session store + `MessagesExceed(20)` | 同 `A-19` 的 (a) 20 (b) 21 | 結果與 `A-19` **逐項相同**(語意等價)。兩鏈路各驗一次 |
| `B-P2-03` | ⚠️ 釘住未爆彈(G7)。歷史中**最舊的那一輪**是「tool call + tool result」配對,總長度剛好越過 20 | 觸發裁切 | 裁切後**不存在孤兒**:沒有無對應 `FunctionCallContent` 的 `FunctionResultContent`(反之亦然)。裁切以整個 turn 為原子單位。**這是行為改善不是回歸**,但必須有測試釘住,否則新實作退化無人察覺 |
| `B-P2-04` | 前端 CopilotKit 重送完整 message 陣列 + 伺服器端已有同一批訊息；assistant message ID 可被 client 重建 | 同 `threadId` 連發三輪 | 送進模型的 messages 中，每則 user 訊息恰出現一次；同一邏輯 assistant 回覆即使 ID 不同也恰出現一次（以保守 role/content/tool-call fingerprint 驗）；總長度不隨輪次呈平方成長。**不得重複累加**。 |
| `B-P2-05` | `minimumPreservedTurns` 設定 | 累積 26 則後檢查 | 裁切**確實觸發**(訊息數 ≤ 20 級距)。spike 實測陷阱:`minimumPreservedTurns` 設成 20 時 26 則完全不觸發 —— 它是**下限**不是上限(02-spec §3.3)。此案就是為了抓那個打錯的數字 |
| `B-P2-06` | `InMemoryChatMemoryStore` / `IChatMemoryStore` 已刪除 | 跑 `dotnet test` | `InMemoryChatMemoryStoreTests.cs` 的三條斷言**語意已由 `A-19` + `B-P2-02` 承接**後才可刪檔;不得只刪不搬(02-spec §8 開放問題 5) |

### 4.3 P3 — 共用 context(8 案)

| ID | 前置條件 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `B-P3-01` | 已登入、mem0 有記憶；路由未命中的正常主 run | 兩鏈路各跑一輪 | recall 注入在模型呼叫**前**的 Instructions；`Remembered` 在**完整回覆後**寫入且內容是最終文字。路由命中不做 recall 的例外由 `B-P4-07` 驗。 |
| `B-P3-02` | `RecallAsync` 擲例外 | 兩鏈路 | 聊天正常完成、Instructions 無 mem0 前言、不擲例外（承接 `A-14`；pipeline 邊界吞錯）。 |
| `B-P3-03` | `RememberAsync` 擲例外 | 兩鏈路 | 對外回覆已正常送出、不轉成錯誤；串流不寫 `event:error`（pipeline 邊界吞錯）。 |
| `B-P3-04` | 持久化失敗 | `POST /api/chat`(阻塞) | `500` + ApiError(等同 `A-15`) |
| `B-P3-05` | 持久化失敗 | `POST /api/chat/stream` | 只記 warning、chunks 照常、無 `event:error`(等同 `A-16`)。**`B-P3-04`/`B-P3-05` 必須成對驗**,證明共用的 `StoreAIContextAsync` 沒有把兩種語意抹平(02-spec §8 開放問題 4) |
| `B-P3-06` | 匿名(僅鏈路 A 可能發生)，mem0 fake 具既存資料 | `ChatAsync` / `StreamChatAsync` / `GET /api/chat/history` | 不 recall、不 remember mem0、不持久化、history 回 `200` + 空陣列（等同 `A-06` + `A-21`）。 |
| `B-P3-07` | 租戶 A 使用者 U 先用副駕聊一輪,再用 ChatView 聊一輪 | `GET /api/chat/history` | **兩輪都在同一份歷史裡**,依 `(tenant_id, user_id)` 歸戶。這是 02-spec §4.2 的刻意決策,不是 bug;若造成混淆,退路是加來源標記而非分表 |
| `B-P3-08` | 護欄 prompt 改走 `ChatOptions.Instructions` | 累積超過 20 則後再發一輪 | 護欄**仍生效**且**未被視窗裁掉**(system 訊息不進 history,02-spec §3.3)。斷言對象:模型收到的 instructions 非空 |

### 4.4 P4 — 路由共用(14 案)

> `B-P4-01` ~ `B-P4-11` 逐條對映 02-spec §5.1 的「不得改變的語意」清單。它們與 A 組同名項目**斷言相同**,差別在於必須在**兩條鏈路上**都成立。

| ID | 對應 §5.1 條目 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `B-P4-01` | 匿名不路由 | 鏈路 A 匿名一輪 | 同 `A-06`。AG-UI 側 P1 後不存在匿名情境 |
| `B-P4-02` | 角色過濾 | 兩鏈路各跑 USER / ADMIN | 同 `A-05` |
| `B-P4-03` | 跳過 builtin `template_*` | 兩鏈路 | 同 `A-07` |
| `B-P4-04` | `SingleRequiredStringKey` | 兩鏈路 `[Theory]` 四型 | 同 `A-08`,**靜默跳過** |
| `B-P4-05` | 最多重試兩次 | 兩鏈路 `[Theory]` 三腳本 | 同 `A-09` |
| `B-P4-06` | `MatchTool` 先全等再寬鬆取最長名 | 兩鏈路 | 同 `A-10` |
| `B-P4-07` | 路由不帶歷史 / mem0 | 已登入、有 5 輪歷史 + mem0 有記憶,再問一輪並命中 skill | 路由與 summary 的模型呼叫不含任何前輪內容、也不含 mem0 前言（刻意設計）；但 recorder 在完整 skill 摘要後仍對登入身分 remember 最終回覆。 |
| `B-P4-08` | `kb_query` ABSTAIN → `rag_qa` | 兩鏈路 | 同 `A-11`(順序斷言)+ `A-12`(不誤觸發) |
| `B-P4-09` | 目錄失敗 best-effort | 兩鏈路 `[Theory]` 四例外 | 同 `A-03` |
| `B-P4-10` | 單一工具失敗不炸整輪 | 兩鏈路 `[Theory]` 六例外 | 同 `A-04` |
| `B-P4-11` | summary prompt 常數不得修改 | — | 保留現行字串相等斷言(`ChatSkillRoutingTests.cs:514` 的「一字都不得更改」語意)。**唯一被允許的 prompt 字串斷言**,定位是護欄不是行為驗收 |
| `B-P4-12` | 副駕取得同批能力 | 同一份目錄,分別經 ChatView 與副駕提同一個數字問題 | 兩邊 `SkillInvokes` 的 `(Name, Input)` **完全相同**;角色過濾結果也相同。⚠️ 需先解 G5(`FakeWorkflowService` 在 Web 測試是 scoped,跨請求收集不到) |
| `B-P4-13` | **已知限制的驗收(非 bug)** | 副駕該輪同時提供非空 client tools,且路由**命中** skill | 回應中**沒有** `TOOL_CALL_START`/`ARGS`/`END` 事件;使用者仍得到 skill 的答案。**短路天花板是刻意行為**(01-plan §5.2 / 02-spec §5.2):改成「注入 messages 後正常 run」會把受約束的 summary 併回自由主 run,連帶改變鏈路 A 的數字行為。本案的作用是**釘住這個決策**,一旦有人「順手修好」它會紅。實作端須留 `ponytail:` 註解標明天花板與升級路徑 |
| `B-P4-14` | client tools 迴路(補 G2/G3) | 副駕提供非空 client tools(如 `switchView`),路由**未命中**(NONE) | 出現 `TOOL_CALL_START`(含工具名 `switchView`)→ `TOOL_CALL_ARGS`(含 `{"view":"documents"}` 參數)→ `TOOL_CALL_END`。⚠️ 前置:`FakeChatClient` 須能實際產生 function call —— 現行 `GetService` 回 `null` 使這在結構上不可能(G2) |

### 4.5 跨鏈路不變式(每個 phase 收尾都要重跑,4 案)

| ID | 動作 | 預期結果 |
| --- | --- | --- |
| `B-X-01` | `POST /api/chat/stream` raw bytes | 仍是 `data:`(**無空格**),空行結尾,含換行 chunk 拆多行。等同 `A-22`,**每個 phase 重跑** |
| `B-X-02` | `POST /api/copilot/agui` raw bytes | 仍是 `data: `(**有空格**),事件型別集合不變。等同 `A-23`(P1 後帶 JWT) |
| `B-X-03` | 串流中途失敗 | `event:error` 幀語意不變、半截不持久化。等同 `A-17` |
| `B-X-04` | 各類錯誤(400 / 401 / 500) | ApiError `{timestamp,status,message,fieldErrors}` 四鍵不變、camelCase 不變、5xx 仍回固定泛化中文訊息 |

---

## 5. C 組 — 真鏈路 e2e(8 案)

> 依專案記憶 **fakes-hide-real-behavior**:全手寫 fake 單元全綠 ≠ 能動。每個 phase 收尾派 `e2e-verifier` 用 docker compose 打真服務一次。以下每案的**真依賴**欄位標明「哪一環不得被 stub」。
>
> `scripts/verify-copilot-shared-core.ps1` 是可重跑的 **black-box smoke companion**，不是 C-01～C-08 的充分證明：它無法檢查模型輸入、session 重複計數、tool-call/result 配對、mem0 實際儲存或 chunk 到達時序；`-Rebuild` 的 `mock-gpt` 也不能驗 routing。**C gates 不得因此降級**：`C-03`/`C-04`/`C-05`/`C-07`/`C-08` 的 release evidence 仍須由下列具名 integration tests 與 `e2e-verifier` 真服務 trace／必要時手動 browser proxy 檢查提供；routing 必須使用真實模型。
>
> 上述缺口的自動化、artifact、PASS/FAIL 判準與執行順序，見 [05-release-evidence-plan.md](05-release-evidence-plan.md)。
>
> 最低補強對照：`C-03` → `CopilotAguiApiTests.Agui_TenantIsolation_SameThreadId_TenantB_CannotSeeTenantASecret` 與 `Agui_SameTenantDifferentUsers_SameThreadId_UserBCannotSeeUserASecret`；`C-04` → `Agui_SameThreadId_ThreeRounds_FullArrayResend_MismatchedAssistantId_UserMessagesStillAppearExactlyOnce` + `ChatSessionWindowTests.Window_ToolCallAndResult_NotSplitByCompaction`；`C-05` → `Agui_ChatTurn_PersistsToConversationStore_AppearsInChatHistory` + 真 mem0 儲存 trace；`C-07` → `Agui_And_ChatView_SameCatalog_SameQuestion_SkillInvokes_NameAndInputMatch` + 真實模型／workflow trace；`C-08` → e2e-verifier 的 proxy trace 與手動 browser timing 檢查。這些是 smoke script 以外、不可省略的 release 證據。

| ID | Phase | 真依賴(不得 stub) | 動作 | 預期結果 |
| --- | --- | --- | --- | --- |
| `C-01` | 全 | LiteLLM(`CHAT_MODEL=mock-gpt`)、backend、appdb | 真 JWT 打 `POST /api/chat/stream` | 真 SSE 逐塊到達;`data:` 無空格;對話真的落 appdb,`GET /api/chat/history` 撈得到 |
| `C-02` | P1 | 同上 + 真 AG-UI 序列化 | (a) 無 JWT (b) 有 JWT 打 `/api/copilot/agui` | (a) `401`;(b) `200` + 完整事件序列。**AG-UI 的 JSON 事件序列化最容易被 fake 掩蓋**(G2/G3) |
| `C-03` | P1 | 真 backend 簽兩個不同租戶的 JWT | 兩租戶用同一 `threadId` 各發一輪 | 第二租戶的回覆與模型輸入**都不含**第一租戶的內容。**必驗內容,不驗 nullity** |
| `C-04` | P2 | 真 session store 生命週期 | 同 `threadId` 連發三輪,含一輪會呼叫 client tool | 第三輪記得第一輪;訊息不重複累加;tool call/result 配對完整 |
| `C-05` | P3 | 真 mem0 容器 + 真 appdb | 副駕聊一輪,再打 `GET /api/chat/history` | 副駕那輪出現在歷史中(02-spec §4.2);mem0 真的寫入 |
| `C-06` | P3 | mem0 容器**故意停掉** | 兩鏈路各發一輪 | 兩邊都正常回覆、無 5xx(best-effort 真的吞掉)。單元的 `A-14` 只能證明程式碼吞了例外,這條才能證明**連線層級**的失敗也吞得掉 |
| `C-07` | P4 | 真 workflow(真 skill invoke)+ 真 backend 目錄 | 副駕問一個數字問題 | skill 真的被呼叫、真 JSON 形狀被正確解析、回覆數字與 skill 輸出一致。**真 JSON 形狀差異是 fake 最常掩蓋的東西** |
| `C-08` | 全 | 真 nginx / Vite dev proxy | 經 proxy 打兩條 SSE 路徑 | `proxy_buffering off` 仍生效:chunk 逐塊到達而非一次吐完;兩種 `data:` 格式各自不變 |

---

## 6. 證明等價的最小集合

如果只能跑**一組**測試來確認重構沒有破壞現有行為,是這 10 條。挑選標準:每一條都覆蓋一個**獨立的失效模式**,且斷言面跨得過 P1–P4 的所有結構變動。

| # | 案例 | 它單獨守住什麼(拿掉它就沒人守) |
| --- | --- | --- |
| 1 | `A-01` | 路由命中的完整因果鏈:選對 skill → 帶對參數 → 回覆是摘要而非原始 JSON。**整個 P4 的核心** |
| 2 | `A-02` | 路由未命中的另一半決策表:零 invoke + 正常回覆 |
| 3 | `A-03` | 目錄失敗 best-effort(含傳輸例外等價類) |
| 4 | `A-04` | 工具失敗不炸整輪。與 3 合起來是「下游壞掉」的兩個獨立等價類 |
| 5 | `A-11` | `kb_query` ABSTAIN → `rag_qa` 兜底**與其順序**。這條最容易在重寫路由時整段掉光 |
| 6 | `A-13` | mem0 recall-before / remember-after **含工具融合結果**。P3 搬進 `AIContextProvider` 後最可能錯位的就是順序 |
| 7 | `A-15` + `A-16` | 阻塞 500 / 串流只記 warning 的**刻意差異**。共用 `StoreAIContextAsync` 最可能把兩者抹平(02-spec §8 開放問題 4)。**兩條必須成對,單跑任一條都證明不了差異存在** |
| 8 | `A-17` | 串流中途爆炸:`event:error` 幀 + 半截不持久化。資料完整性,不可簡化 |
| 9 | `A-19` | 20 則視窗的 on-point / off-point。刪掉 `InMemoryChatMemoryStore` 後,這是「20」這個數字**唯一**的端到端證據 |
| 10 | `A-22` + `A-21` | 對外契約底線:`data:` 無空格 + history 依 JWT 身分過濾 / 匿名回空陣列(**非 401**) |

> 這 10 條**不能**取代 `B-P1-04`(租戶隔離)與 `B-P2-03`(tool call 配對)—— 那兩條守的是**新引入**的風險,不在「等價」的範疇內,但同屬 release blocker。

---

## 7. Phase exit gate

| Gate | 條件 |
| --- | --- |
| **實作動工前** | A-01 ~ A-24 全綠(含補完 G1 的 `FakeMem0Client` 失敗開關)。**未達成不得動 `platform/src/`** |
| **P1 exit** | `B-P1-01` ~ `08` + A 組重跑全綠 + `C-02`/`C-03`。`B-P1-06`(fail-closed 500)與 `B-P1-04`(內容隔離)任一紅即 blocker |
| **P2 exit** | `B-P2-01` ~ `06` + A 組重跑 + `C-04`。`B-P2-03`(配對不拆散)紅即 blocker |
| **P3 exit** | `B-P3-01` ~ `08` + A 組重跑 + `C-05`/`C-06`。`B-P3-04`/`05` 必須成對綠 |
| **P4 exit** | `B-P4-01` ~ `14` + A 組重跑 + `C-07`。`B-P4-13` 是**刻意行為的驗收**,它綠代表天花板還在,不是缺陷 |
| **全案 release gate** | 兩個方案 `dotnet test` 全綠、frontend lint/build 綠、`B-X-01` ~ `04` 綠、`C-01` ~ `C-08` 綠。任何跨租戶可見、SSE 格式變更、ApiError 形狀變更皆為 blocker |

---

## 8. 明確非驗收範圍(YAGNI)

以下不寫成缺陷、不阻擋 exit:

- `SingleRequiredStringKey` 單參天花板(01-plan §6.1)—— `A-08`/`B-P4-04` 只驗「靜默跳過」,不驗多參可路由。
- 路由命中時 client tools 不被呼叫(`B-P4-13`)—— **已驗收的天花板**,非 bug。
- skill catalog 缺 `output_schema`、慢 skill 無進度回報、`useCopilotChatSuggestions` / 多參補問 / generative UI(01-plan §6.2–6.4)。
- `JWT_SECRET` / `INTERNAL_API_TOKEN` 公開開發預設值(01-plan §6.5)。
- 前端除 `App.tsx` 一處外的任何改動(02-spec §6)——`useCopilotReadable` / `useCopilotAction` / `renderAndWaitForResponse` / `CopilotSidebar` 文案皆不在驗收範圍,只需 lint/build 不退化。
- 不改 `CHAT_MODEL`;不刪 ChatView(兩鏈路長期並存)。

---

## 附錄:案例數統計

| 組 | 案例數 | 編號範圍 |
| --- | --- | --- |
| **A — 重構前行為安全網** | 24 | `A-01` ~ `A-24` |
| **B — phase 新能力** | 40 | P1 `B-P1-01`~`08`(8)、P2 `B-P2-01`~`06`(6)、P3 `B-P3-01`~`08`(8)、P4 `B-P4-01`~`14`(14)、跨鏈路不變式 `B-X-01`~`04`(4) |
| **C — 真鏈路 e2e** | 8 | `C-01` ~ `C-08` |
| **合計** | **72** | — |
