# 計畫書 — 副駕共用核心層(Copilot Shared Core)

> 狀態: **已實作，持續 hardening(2026-07-21)。** P0–P4 已完成；本計畫以下的原始決策已由 02-spec／04-acceptance-test 回填為目前契約：isolation key 為 `{tenant}:{user}`；鏈路 A(`ChatAssistant`)刻意 `withIsolation:false`，因其 session `conversationId` 已在推導層加上登入身分前綴，並保留匿名短期連續性；`AguiWireDedupAgent` 必須處理 AG-UI 重送完整陣列及 assistant ID 不一致；mem0 的 best-effort 由共用 pipeline 邊界保證，即使 `IMem0Client` 實作擲例外也不得中斷聊天；匿名聊天不 recall/remember mem0。
> 關聯:[chat-skill-routing](../chat-skill-routing/01-plan.md) —— 本計畫實質上是該計畫 03-design §10 標為「P4 / 非目標」的那一列(AG-UI 側掛同批能力),外加兩條鏈路的共用層抽取。
> 前提知識:使用者多為非技術人員、以自然語言在聊天中提問(專案記憶 non-technical-users-chat-first)。

## 1. 問題

這個 app 有**兩個聊天面**,而它們的腦不一樣:鏈路 A `/api/chat/stream` 有動態 skill 路由、角色過濾、數字護欄、短期記憶、mem0、持久化、JWT 身分、串流錯誤降級;鏈路 B `/api/copilot/agui` 這些都沒有,只能操作前端 UI(切視圖、刪除確認)。

根因是一行:`platform/src/Platform.Web/Program.cs:269` 的 `AsAIAgent(instructions, name)` 是**沒有任何依賴注入的裸 agent**,`ChatService` 那整套 memory / mem0 / 持久化 / 路由 / 數字護欄一個都沒接上;而 `Program.cs:275` 的 `AllowAnonymous` 讓它連接上去的前提(身分)都沒有。

對非技術使用者而言,現況是兩個外觀相同的聊天框,一個記得你、會查數字、能用你的 skill,另一個失憶、會瞎掰數字、只會查一種東西。**這比只有一個更糟。**

> 兩條鏈路完整能力對照表已由 [03-design.md](03-design.md) §0/§1 取代(更嚴謹版本),本檔不再重列。

## 2. 前置探查結論(已完成)

三輪調查已完成,結論直接約束本計畫的設計空間:

- **e2e 實測**:AG-UI 的 client tools 迴路**是通的**。`tools` 非空時,gpt-4o-mini 正確呼叫 `switchView({"view":"documents"})`;`tools: []` 的對照組則退化成純文字口頭承諾。原生 function calling 在 UI 操作類工具上可靠。
- **spike 實測**:AG-UI 目前**沒有伺服器端記憶**。`MapAGUI(pattern, aiAgent)` 這個 overload 不含 session store,同一 `threadId` 第二次提問不記得第一次。前端看似有記憶純粹是 CopilotKit 每次重送完整 message 陣列。
- **spike 實測**:框架 `AgentSessionStore` 是**字串 key**(`GetSessionAsync(agent, string conversationId, ct)`),現有 `cid` 可直接當 key,不需自建映射表。
- **spike 實測**:`SlidingWindowCompactionStrategy` **以整個 turn 為原子單位**裁切;現有 `InMemoryChatMemoryStore` 的 `RemoveAt(0)` 會拆散 `FunctionCallContent` / `FunctionResultContent` 配對 —— 一顆尚未引爆的雷。
- **spike 實測**:client tools 與 server tools 在 `ChatClientAgent.CreateConfiguredChatOptions` 是**相加**;server tool 在 process 內執行且前端看不到,client tool 才冒泡成 `TOOL_CALL_*`。
- **spike 實測**:`AgentSkillsProvider` **不適用**。它是 Claude Skills 式漸進揭露(skill = 要載入 context 的知識文件),我們的 skill 是可執行的遠端 workflow;且名稱格式禁止底線(現有 7/10 個 builtin 非法)、`input_schema` / `required_role` 進不了 prompt。
- **考古**:原生 function calling 於 `71998b6` 被刻意關閉,理由在原始碼寫了四次 —— **gpt-4o-mini 會自己心算且會竄改工具回傳的數字**。這是模型級限制,會再犯,不可盲目回頭。但該證據**只涵蓋數字類 skill**,不涵蓋 UI 操作類工具(已由 e2e 反證)。

## 3. 方案選擇

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

> skill 路由維持短路語意、不與 client tools 共存的「刻意天花板」決策,以及認證必須先於記憶落地的取捨,已定案並收斂進 [02-spec.md](02-spec.md) §5.2 與 [03-design.md](03-design.md) §3.2;本檔只保留其唯一根因說明(§2 考古結論那一條 —— 原生 function calling 對數字類 skill 不可靠)。

## 4. 明確排除的相鄰議題

以下皆為真實缺口,但**不在本計畫**,各自需要獨立決策:

1. `SingleRequiredStringKey` 單參天花板 —— 與 `CHAT_MODEL` 是否升級強綁。
2. skill catalog 缺 `output_schema`,前端只能靠 `frontend/src/skills/answerOf.ts` 猜答案鍵。
3. 慢 skill(`kb_query` 最長阻塞 120 秒)無進度回報;workflow 用 `ainvoke` 非 `astream`。
4. 前端 `useCopilotChatSuggestions` / 多參補問 / generative UI 渲染 skill 結果。
5. 正式環境的 credential provisioning 與 ES256 key rotation（現由 deployment contract 與 startup fail-fast 管理）。

> 分階落地順序表已由 [03-design.md](03-design.md) §11 取代(14 步,含每步動哪個檔);風險表帶【核】/【推】證據分級的版本已在 [04-acceptance-test.md](04-acceptance-test.md),本檔不再重列。

> 下一步:02-spec(決策細化)→ 03-design(簽章與落地順序)→ 04-acceptance-test。
