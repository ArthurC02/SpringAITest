# 計畫書 — 副駕共用核心層(Copilot Shared Core)

> 狀態: **已實作，持續 hardening(2026-07-21)。** P0–P4 已完成；本計畫以下的原始決策已由 02-spec／04-acceptance-test 回填為目前契約：isolation key 為 `{tenant}:{user}`；鏈路 A(`ChatAssistant`)刻意 `withIsolation:false`，因其 session `conversationId` 已在推導層加上登入身分前綴，並保留匿名短期連續性；`AguiWireDedupAgent` 必須處理 AG-UI 重送完整陣列及 assistant ID 不一致；mem0 的 best-effort 由共用 pipeline 邊界保證，即使 `IMem0Client` 實作擲例外也不得中斷聊天；匿名聊天不 recall/remember mem0。
> 關聯:[chat-skill-routing](../chat-skill-routing/01-plan.md) —— 本計畫實質上是該計畫 03-design §10 標為「P4 / 非目標」的那一列(AG-UI 側掛同批能力),外加兩條鏈路的共用層抽取。
> 前提知識:使用者多為非技術人員、以自然語言在聊天中提問(專案記憶 non-technical-users-chat-first)。

## 1. 問題

這個 app 有**兩個聊天面**,而它們的腦不一樣:

| 能力 | 鏈路 A `/api/chat/stream` | 鏈路 B `/api/copilot/agui` |
| --- | --- | --- |
| 動態 skill 路由(租戶目錄) | 有 | 無,前端硬編碼 1 顆 `rag_qa` |
| 角色過濾 | 有 | 無 |
| 「禁止心算、禁改工具數字」護欄 | 有 | 無 |
| 短期記憶 20 則 | 有 | **無(見 §2)** |
| mem0 長期記憶 | 有 | 無 |
| 對話持久化 / 歷史 | 有 | 無 |
| 身分 / 租戶 | JWT | 無,`AllowAnonymous` |
| 串流錯誤幀、三層降級 | 有 | 無 |
| 操作前端 UI(切視圖、刪除確認) | 無 | 有 |

根因是一行:`platform/src/Platform.Web/Program.cs:269` 的 `AsAIAgent(instructions, name)` 是**沒有任何依賴注入的裸 agent**,`ChatService` 那整套 memory / mem0 / 持久化 / 路由 / 數字護欄一個都沒接上;而 `Program.cs:275` 的 `AllowAnonymous` 讓它連接上去的前提(身分)都沒有。

對非技術使用者而言,現況是兩個外觀相同的聊天框,一個記得你、會查數字、能用你的 skill,另一個失憶、會瞎掰數字、只會查一種東西。**這比只有一個更糟。**

## 2. 前置探查結論(已完成)

三輪調查已完成,結論直接約束本計畫的設計空間:

- **e2e 實測**:AG-UI 的 client tools 迴路**是通的**。`tools` 非空時,gpt-4o-mini 正確呼叫 `switchView({"view":"documents"})`;`tools: []` 的對照組則退化成純文字口頭承諾。原生 function calling 在 UI 操作類工具上可靠。
- **spike 實測**:AG-UI 目前**沒有伺服器端記憶**。`MapAGUI(pattern, aiAgent)` 這個 overload 不含 session store,同一 `threadId` 第二次提問不記得第一次。前端看似有記憶純粹是 CopilotKit 每次重送完整 message 陣列。
- **spike 實測**:框架 `AgentSessionStore` 是**字串 key**(`GetSessionAsync(agent, string conversationId, ct)`),現有 `cid` 可直接當 key,不需自建映射表。
- **spike 實測**:`SlidingWindowCompactionStrategy` **以整個 turn 為原子單位**裁切;現有 `InMemoryChatMemoryStore` 的 `RemoveAt(0)` 會拆散 `FunctionCallContent` / `FunctionResultContent` 配對 —— 一顆尚未引爆的雷。
- **spike 實測**:client tools 與 server tools 在 `ChatClientAgent.CreateConfiguredChatOptions` 是**相加**;server tool 在 process 內執行且前端看不到,client tool 才冒泡成 `TOOL_CALL_*`。
- **spike 實測**:`AgentSkillsProvider` **不適用**。它是 Claude Skills 式漸進揭露(skill = 要載入 context 的知識文件),我們的 skill 是可執行的遠端 workflow;且名稱格式禁止底線(現有 7/10 個 builtin 非法)、`input_schema` / `required_role` 進不了 prompt。
- **考古**:原生 function calling 於 `71998b6` 被刻意關閉,理由在原始碼寫了四次 —— **gpt-4o-mini 會自己心算且會竄改工具回傳的數字**。這是模型級限制,會再犯,不可盲目回頭。但該證據**只涵蓋數字類 skill**,不涵蓋 UI 操作類工具(已由 e2e 反證)。

## 3. 目標 / 非目標

### 目標

1. 兩條鏈路共用同一顆腦:身分、記憶、mem0、持久化、數字護欄、skill 路由只有一份實作。
2. 副駕取得伺服器端記憶與租戶隔離,且**認證先於記憶**落地(見 §5)。
3. 刪掉自寫的 `InMemoryChatMemoryStore`,改用框架抽象,順帶修掉 tool-call 配對被拆散的未爆彈。
4. 共用層一律使用框架**為該職責設計**的抽象,不自創 `IChatPipeline` / `ITurnHandler` 之類的中介層。

### 非目標

- 不改公開聊天 API、SSE `data:` 格式、AG-UI 事件契約(對外行為相容)。
- **不刪 ChatView**,兩條鏈路長期並存(此為使用者決策)。
- 不動 skill 撰寫端、不動 workflow、不動 backend。
- 不解除 `SingleRequiredStringKey` 單參天花板(獨立議題,見 §6)。
- 不改 `CHAT_MODEL`。

## 4. 方案選擇

共用層的形狀有三個候選,各自對應框架的不同接縫:

| 方案 | 作法 | 判定 |
| --- | --- | --- |
| **A. 自寫 `IChatPipeline` 抽象** | 定義自己的介面,兩條鏈路各自實作 transport | **否決。** 框架已有為此設計的接縫,自創一層等於重造且與框架語意打架 |
| **B. `DelegatingAIAgent` 全包** | 用一個裝飾 agent 攔截所有行為 | **部分採用。** 只適合「攔整個 run」的職責(skill 路由),不適合逐次 LLM 呼叫的職責 |
| **C. 框架原生組合** | session store + `AIContextProvider` + `AIAgentBuilder.Use` 各司其職 | **採用** |

**採用 C**,每一塊用框架為那件事設計的抽象:

| 職責 | 用什麼 | 兩鏈路共用 |
| --- | --- | --- |
| 短期記憶 20 則 | 框架 session store + `SlidingWindowCompactionStrategy(MessagesExceed(20))` | 是 |
| 租戶隔離 | `SessionIsolationKeyProvider`(從 JWT 取 `{tenant}:{user}`),fail-closed | 是 |
| mem0 recall + 對話持久化 | `AIContextProvider`(`ProvideAIContextAsync` 注入 / `StoreAIContextAsync` 寫入) | 是 |
| 數字護欄 prompt | `ChatOptions.Instructions` | 是 |
| 確定性 skill 路由 | `AIAgentBuilder.Use(...)` middleware(攔整個 run,正是其用途) | 是 |
| 事件輸出格式 | 各自保留:手刻 `data:` vs AG-UI `data: ` | **否,本來就該不同** |

## 5. 定案的關鍵取捨

### 5.1 認證必須先於記憶

框架 XML doc 明確警告:AG-UI 的 `ThreadId` 來自 wire,**不是授權憑證**。目前 threadId 猜測攻擊不成立,**不是因為擋住了,而是因為伺服器端沒東西可偷**。一旦加上記憶而未配 `SessionIsolationKeyProvider`,漏洞立刻成真。

框架此處是 fail-closed(未註冊 provider 時每個請求直接 500,不會默默共用全域命名空間),但不可依賴這道保險 —— **認證與隔離必須與記憶同一批落地,不得分兩個 phase**。

### 5.2 skill 路由維持短路語意,不與 client tools 共存(刻意天花板)

路由 middleware 攔整個 run。若路由命中 skill,則 invoke → 受約束的 summary → 直接回傳,**不委派給內層 agent**,因此該輪的 client tools 沒有機會被呼叫。

考慮過的替代方案:改成「路由 → 把 skill 結果注入 messages → 正常 run」,如此 client tools 可共存。**否決**,因為那會把現行受約束的 summary 步驟併回自由的主 run,**同時改變鏈路 A 既有的數字行為** —— 而數字正確性正是當初關閉原生 FC 的唯一理由(§2)。為一個罕見情境冒這個險不划算。

代價:「查一下營收然後切到分析視圖」這類混合指令只會執行前者。失敗是優雅的(使用者得到答案,再問一次即可切換)。**列為已知天花板,量到痛再解。**

### 5.3 工具依風險分類,不做二選一

- **UI 操作類**(切視圖、開文件、刪除確認)→ 原生 FC,已由 e2e 證實可靠,且只有這條路能走。
- **數字 / 資料類 skill** → 維持確定性路由 + 禁改數字的 summary 護欄。

## 6. 明確排除的相鄰議題

以下皆為真實缺口,但**不在本計畫**,各自需要獨立決策:

1. `SingleRequiredStringKey` 單參天花板 —— 與 `CHAT_MODEL` 是否升級強綁。
2. skill catalog 缺 `output_schema`,前端只能靠 `frontend/src/skills/answerOf.ts` 猜答案鍵。
3. 慢 skill(`kb_query` 最長阻塞 120 秒)無進度回報;workflow 用 `ainvoke` 非 `astream`。
4. 前端 `useCopilotChatSuggestions` / 多參補問 / generative UI 渲染 skill 結果。
5. `JWT_SECRET` 與 `INTERNAL_API_TOKEN` 的公開開發預設值。

## 7. 分階

> **逐步的落地順序以 [03-design.md](03-design.md) §11 為準**(14 步,含每步動哪個檔)。本表只給階段輪廓。

| Phase | 內容 | 可獨立出貨 |
| --- | --- | --- |
| **P0 前置** | 補行為基線測試(證明重構前後等價)、兩個 csproj 加 `Microsoft.Agents.AI` `PackageReference`、`IChatIdentityAccessor` 承接 `DeriveMemoryKeys` | 否,是其餘一切的前提 |
| **P1 認證與隔離** | AG-UI 上認證、前端 HttpAgent 帶 JWT、`JwtTenantIsolationKeyProvider`、`MapAGUI` 換 DI overload | 是 |
| **P2 記憶收斂** | 框架 session store + `SlidingWindowCompactionStrategy` 取代 `InMemoryChatMemoryStore`;兩鏈路共用 | 是(依賴 P1) |
| **P3 共用 context** | `ChatContextProvider`(護欄 + mem0 recall → `Instructions`)+ `ChatTurnRecorder`(mem0 remember + 持久化) | 是 |
| **P4 路由共用** | skill 路由抽成 `SkillRoutingAgent`,掛 `.Use` 第二層;副駕取得同批 skill 能力 | 是 |

**順序硬約束**

- P0 是所有東西的前提;其中「補行為基線測試」必須**早於任何實作**,否則無從證明等價(§8)。
- **P1 不可與 P2 對調** —— 認證與隔離必須與記憶同一批落地(§5.1)。
- P3、P4 之間無強制順序,但 `ChatTurnRecorder` 必須早於 `SkillRoutingAgent`:recorder 要在 routing 外側,先建外層再插內層。
- 每個 phase 收尾派 `e2e-verifier` 打真鏈路一次(§8)。

> P3 為何是**兩個**元件而非原本規劃的一個 `AIContextProvider`:路由短路發生在 `ChatClientAgent` 之外,`StoreAIContextAsync` 在短路輪不會執行 → 命中 skill 的那一輪不會被 mem0 記住也不會持久化。詳見 [02-spec.md](02-spec.md) §4.0。

## 8. 風險

| 風險 | 緩解 |
| --- | --- |
| 現行行為**沒有任何回歸測試**,只靠 prompt 字串相等斷言撐著 | 04-acceptance-test 必須先補行為層測試,再動實作 |
| 手寫 fake 掩蓋真實序列化差異(專案記憶 fakes-hide-real-behavior) | 每個 phase 收尾派 `e2e-verifier` 打真鏈路一次 |
| `AgentSession` 生命週期與現有 `conversationId` 語意不同 | spike 已實測可對接;驗收須驗**內容隔離**而非 nullity(`GetSessionAsync` 對未知 key 會自動建空 session,不回 null) |
| 動到 `ChatService` 可能回歸鏈路 A 的數字行為 | P4 維持短路語意不變(§5.2);summary prompt 常數不得修改 |

> 下一步:02-spec(決策細化)→ 03-design(簽章與落地順序)→ 04-acceptance-test。
