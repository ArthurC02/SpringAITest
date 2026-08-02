# 詳細設計 — 副駕共用核心層(Copilot Shared Core)

> 狀態: **已實作，持續 hardening。** 本檔保留原始 HOW 與探查證據；目前生效的行為契約以已回填的 [02-spec.md](02-spec.md) 與 [04-acceptance-test.md](04-acceptance-test.md) 為準。
> 本文件是 **HOW**:確切簽章、層級順序、呼叫鏈、await 傳染面、落地順序、刻意簡化總表。**不重述** 01/02 的結論,只在需要時引用其節次。
> **規劃任務,不動生產碼。** 唯一產出即本檔。
>
> **本文的證據分級**(誠實要求):
> - **【核】** = 已對現行原始碼 / 1.13.0 DLL 反射 / 本次寫的最小驗證程式實測確認。
> - **【推】** = 由【核】的事實推導出的設計決策,未實作前無法 100% 證實。
> - **【卡】** = 無法在不寫實作的情況下拍板者,明確標出卡點。全文僅一處(§9.3)。
>
> 驗證程式寫在系統暫存目錄的 scratchpad,已刪除,repo 內零殘留。

---

## 0.2 已實作後的 hardening 補充（優先於舊示意碼）

- AG-UI strict isolation key 是 JWT `{tenant}:{user}`；tenant 或 user 缺失時 fail-closed，絕不可生成共享 key。
- `ChatAssistant` 使用 `withIsolation:false`，但登入 session key 已在 `ChatMemoryKeyDerivation` 前綴 `{tenant}:{user}`；匿名保留既有短期 session continuity，且不進長期 mem0。
- mem0 的 best-effort 是 `ChatContextProvider` 與 `ChatTurnRecorder` 的邊界保證：任何 `IMem0Client` 例外都記錄並降級，不能依賴特定 `Mem0Client` 實作自行吞錯。
- `AguiWireDedupAgent` 先按 ID 去重；assistant ID 不一致時以保守 role/content/tool-call fingerprint 補救，不能讓重送陣列重複寫入 history。
- `HttpAgent` 收到 AG-UI `401` 必須觸發既有全域 logout。這是前端 session 一致性要求，不影響 AG-UI 端點本身的認證邊界。
- `ServiceLifetime.Scoped`(§4.6 原案)已在實測中發現會讓 Development 啟動崩潰(root provider 於啟動期解析 MapAGUI agent),改為兩顆 Singleton hosted agent + `ChatContextProvider`/`SkillRoutingAgent`/`ChatTurnRecorder` 各自持 `IServiceScopeFactory`、每次呼叫開新 scope 解析 Scoped 依賴，避免 captive dependency。

---

## 0. 行號校驗(01/02-spec 引用 vs 現行程式)

逐一開檔核對,**不是抄的**。結論:**02-spec 的錨點 12 準 11 漂 1**;01-plan 的錨點全準。

| 錨點(spec 引用) | 現行位置(已核) | 判定 |
| --- | --- | --- |
| `ChatService.cs:202-213` `DeriveMemoryKeys` | `:202` 簽章 → `:213` 收尾 | ✅ 精準 |
| `ChatService.cs:249-252` 匿名不路由 | `:249 if (userCtx is null)` → `:251 return null` → `:252 }` | ✅ 精準 |
| `ChatController.cs:71-78` `event:error` 幀 | `:71 catch (Exception)` → `:75-77` 三行寫入 → `:78 }` | ✅ 精準 |
| `ChatController.cs:90-96` `X-Auth-Invalid` | `:90 SetAuthInvalidHeaderIfNeeded()` → `:96 }` | ✅ 精準 |
| `Program.cs:269` 裸 `AsAIAgent(instructions, name)` | `:269-271`(跨三行) | ✅ 起始行精準 |
| `Program.cs:275` `MapAGUI(...).AllowAnonymous()` | `:275` | ✅ 精準 |
| `Platform.Service/InMemoryChatMemoryStore.cs` | 存在,44 行,`MaxMessages=20`,`RemoveAt(0)` 在 `:40` | ✅ |
| `Platform.Service/Abstractions/IChatMemoryStore.cs` | 存在,14 行,兩個方法 | ✅ |
| 02-spec §5 列的六個私有方法 | `TryRouteAndExecuteAsync:247`、`RouteAsync:299`、`BuildSummaryMessages:363`、`BuildToolsAsync:383`、`SkillCatalogToTools:411`、`InvokeSkillToolAsync:517` | ✅ 全部存在 |
| `CopilotAguiApiTests.cs` 只送 `tools: []`、只斷言兩個事件型別 | `:22 tools = Array.Empty<object>()`;`:45 RUN_STARTED`、`:46 TEXT_MESSAGE_CONTENT`,全檔僅 1 個 `[Fact]` | ✅ 精準 |
| `FakeChatClient.GetService` 回 `null` | `Platform.Web.Tests/FakeChatClient.cs:26` | ✅ 精準 |
| `frontend/src/App.tsx:12` `new HttpAgent({url:'/api/copilot/agui'})` | `:12` | ✅ 精準 |
| **`frontend/src/App.tsx:18` 「CopilotKit provider 掛在登入判斷之後」** | `:18` 是登入守衛 `if (!session) return <AuthPage .../>`;`<CopilotKit>` 元素在 **`:21`** | ⚠️ **輕微漂移**。結論(未登入看不到副駕)正確,但錨點該指 `:21`,或改述為「`:18` 的守衛在 `:21` 的 provider 之前」 |

### 0.1 兩項與 01/02 不符的**結構性**事實(比行號重要)

**(A)【核】`ChatService` 所在的 `Platform.Service` 專案目前完全不引用 Microsoft.Agents.AI。**

```
platform/src/Platform.Service/Platform.Service.csproj
  → Microsoft.Extensions.Logging.Abstractions, RabbitMQ.Client   ← 就這兩個
platform/src/Platform.Web/Platform.Web.csproj
  → Microsoft.Agents.AI 1.13.0, ...AGUI.AspNetCore 1.13.0-preview.260703.1, ...OpenAI, OpenAI 2.12.0
```

`AgentFrameworkLlmAgent` 刻意住在 `Platform.Web/Infrastructure/`,`Platform.Service` 只認薄介面 `ILlmAgent`。
02-spec 假設 `ChatService`「改為對共用 agent 呼叫」,但**這在現況下編譯不過** —— `AIAgent` / `AgentSession` / `AgentSessionStore` 都在 Web 才有的套件裡。這件事 01/02 都沒提。處置見 §1.1。

**(B)【核】`Microsoft.Agents.AI.Hosting`(`AgentSessionStore`、`IsolationKeyScopedAgentSessionStore`、`SessionIsolationKeyProvider`、`AIHostAgent`、`AddAIAgent`)是 preview 套件 `1.13.0-preview.260703.1`,目前只以 AGUI.AspNetCore 的傳遞相依進來,沒有直接 `PackageReference`。**

本計畫要正面使用它 → 必須在兩個 csproj 明確列出版本,否則哪天 AGUI 套件換版就靜默漂移。

---

## 1. 框架事實核對(spike 主張 vs 1.13.0 DLL 實測)

主控代理給的八條事實,逐條驗。**七條完全成立,一條需要補充限制條件。**

| # | 主張 | 判定 | 證據 |
| --- | --- | --- | --- |
| 1 | `AgentSessionStore.GetSessionAsync(AIAgent, string, CancellationToken)` 是字串 key | ✅【核】 | `ValueTask<AgentSession> GetSessionAsync(AIAgent, System.String, CancellationToken)`,XML doc 稱該參數 `conversationId` |
| 2 | 對未知 key 自動建空 session,不回 null | ✅【核】 | 實測 `AIHostAgent.GetOrCreateSessionAsync("t1")` 首次即回非 null。**注意**:回 null 的是**底層** `AgentSessionStore.GetSessionAsync`(XML: "or null if not found");不回 null 的是**上層** `AIHostAgent.GetOrCreateSessionAsync`。驗收要驗**內容**不驗 nullity,這點 02-spec §2.4 完全正確 |
| 3 | `minimumPreservedTurns` 是下限,觸發靠 `CompactionTriggers.MessagesExceed(n)`;裁切以 turn 為原子單位 | ✅【核】 | XML doc 逐字:"This is a **hard floor** — compaction will not exclude turns within this range, **regardless of the target condition**";ctor 為 `(CompactionTrigger trigger, int minimumPreservedTurns, CompactionTrigger target)`;`CompactionMessageGroup` 有 `TurnIndex` 與 `CompactionGroupKind.ToolCall`,`GetTurnGroups(int)` 以 turn 分組 |
| 4 | client tools 與 server tools 在 `ChatClientAgent.CreateConfiguredChatOptions` 相加 | ✅【核】(採信 spike,未重測) | 與 `AIContext.Tools` / `ChatOptions.Tools` 的形狀一致,無矛盾證據 |
| 5 | `FilterServerToolsFromMixedToolInvocationsAsync` 把 server tool 呼叫事件濾出串流 | ✅【核】,**但與本設計無關** | 存在於 `AGUI.AspNetCore` 的 `AGUIChatResponseUpdateStreamExtensions`,**internal**,簽章 `(IAsyncEnumerable<ChatResponseUpdate>, List<AITool>, CancellationToken)`。本設計**不註冊任何 server 端 `AITool`**(§3.2),所以這道濾網對我們是 no-op —— 我們的安全性不靠它 |
| 6 | `MapAGUI(IHostedAgentBuilder, pattern)` 從 DI 解析;`MapAGUI(pattern, aiAgent)` 啟動期 capture | ✅【核】 | 三個 overload 俱在;`AGUIEndpointRouteBuilderExtensions` 內有 internal `SaveSessionAfterStreamingAsync(..., AIHostAgent, string, AgentSession, ct)` —— 證明 builder overload 這條路才會載入/保存 session |
| 7 | `IsolationKeyScopedAgentSessionStore` + `SessionIsolationKeyProvider`,`Strict=true` fail-closed | ✅【核】 | `IsolationKeyScopedAgentSessionStoreOptions.Strict` XML: "**If true (default)**, the store will throw an `InvalidOperationException` when `GetSessionIsolationKeyAsync` returns null"。**隔離機制實作細節**:`GetScopedConversationIdAsync` 把 key 前綴成 `{escapedKey}::{conversationId}`(`EscapeIsolationKey`:先 `\`→`\\` 再 `:`→`\:`)。也就是**隔離只發生在 session store 的 key 改寫**,不影響任何其他子系統 —— 這點直接決定 §9.2 的答案 |
| 8 | 1.13.0 沒有 `AgentThread`,是 `AgentSession` | ✅【核】 | 全組件無 `AgentThread` 型別;`AgentSession`、`ChatClientAgentSession`、`AgentSessionStateBag` 俱在 |

### 1.1 本次新測出、01/02 未涵蓋、且**改變設計**的四條【核】事實

| # | 事實 | 對設計的衝擊 |
| --- | --- | --- |
| **N1** | **`AIAgent.RunAsync` / `RunStreamingAsync` 全部非 virtual。** 唯一覆寫點是 `protected abstract Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken)` 與 `protected abstract IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(同參)` | 所有 middleware 必須繼承 `DelegatingAIAgent` 覆寫 `*Core*` 版本,而非 `RunAsync`。`AIContextProvider` 同理:公開的是 `InvokingAsync`/`InvokedAsync`,可覆寫的是 **`protected virtual ProvideAIContextAsync` / `StoreAIContextAsync`** —— 02-spec §4 的表格用對了名字 ✅ |
| **N2** | **`.Use(A).Use(B)` → A 在外、B 在內。** 實測輸出 `enter FIRST -> enter SECOND -> exit SECOND -> exit FIRST` | 決定 §3 的層級疊法 |
| **N3** | **`AIContext.Messages` 注入的訊息會被寫進持久化的 chat history,且每輪重複累積。** 實測:第 1 輪後 history = `user:Q1 \| system:MEM0-PREAMBLE \| assistant:OK`;第 2 輪 chat client 收到 `... \| system:MEM0-PREAMBLE \| user:Q2 \| system:MEM0-PREAMBLE`(兩份)。**內掛 `ChatClientAgentOptions.AIContextProviders` 與外掛 `UseAIContextProviders` 都一樣會累積** | ⚠️ **02-spec §4 的「mem0 recall → 注入 `Messages`」會製造記憶體洩漏式的 prompt 膨脹。** 必改,見 §5.1 |
| **N4** | **`AIContext.Instructions` 反之:每輪送達 `ChatOptions.Instructions`,且完全不進 history。** 實測第 2 輪 history 乾淨(`user:Q1 \| assistant:OK`),instructions 每輪重新組。且與 agent 自身 instructions **以換行串接共存**(實測 `AGENT-INSTRUCTIONS\nCTX-INSTRUCTIONS`) | 護欄 prompt **與 mem0 recall 兩者都該走 `Instructions`**。副駕的 `copilotInstructions` 不會被蓋掉 ✅ |
| **N5** | **串流中途拋例外時 `StoreAIContextAsync` 不會被呼叫**,例外直達呼叫端;串流正常結束才呼叫,且 `ResponseMessages` 是**完整組合後**的文字(實測 `partial-rest`) | 「半截回覆不得持久化」是框架天然保證,不需自己防 ✅;`event:error` 的例外傳播路徑不受影響(§9.3) |

---

## 2. 專案結構前置(P0,一次做完)

【推】基於 §0.1(A)(B)。這是所有 phase 的前提,單獨列為 P0。

```xml
<!-- platform/src/Platform.Service/Platform.Service.csproj — 新增 -->
<PackageReference Include="Microsoft.Agents.AI" Version="1.13.0" />
<PackageReference Include="Microsoft.Agents.AI.Hosting" Version="1.13.0-preview.260703.1" />
```

```xml
<!-- platform/src/Platform.Web/Platform.Web.csproj — 新增(把傳遞相依變明示) -->
<PackageReference Include="Microsoft.Agents.AI.Hosting" Version="1.13.0-preview.260703.1" />
```

**為什麼是加套件而不是把 `ChatService` 搬到 `Platform.Web`:** 搬檔會把 `ChatSkillRoutingTests`(33 個 `[Fact]`/`[Theory]`)與 `ChatServiceTests`(24 個)整批遷到 `Platform.Web.Tests`,diff 大一個量級,且 `WebApplicationFactory` 跑純邏輯測試比直接 new 慢得多。加兩行 `PackageReference` 不違反 `platform/AGENTS.md` 的「Web → Service 單向」—— 那條講的是**專案間**相依方向,對 NuGet 無約束。

> `// ponytail: Platform.Service 只吃 Agents.AI 的抽象(AIAgent/AgentSession/AgentSessionStore),
> // OpenAI SDK 與 AGUI 仍只在 Platform.Web。若哪天 Service 開始 new OpenAIClient 就是越線了。`

---

## 3. 【拍板 Q1】層級順序 —— 最關鍵的一題

### 3.1 目標拓樸(外 → 內)

```
① AG-UI endpoint handler / ChatController          ← transport,SSE 格式各自為政
     │   (AG-UI: MapAGUI(IHostedAgentBuilder,…) 內部自行 GetOrCreateSession + SaveSessionAfterStreaming)
     │   (鏈路 A: ChatService 手動 GetOrCreateSessionAsync / SaveSessionAsync)
     ▼
② AIHostAgent : DelegatingAIAgent                  ← 框架提供,持 AgentSessionStore
     │   └ IsolationKeyScopedAgentSessionStore(Strict=true)
     │        └ JwtTenantIsolationKeyProvider : SessionIsolationKeyProvider   ← 租戶,fail-closed
     │             └ InMemoryAgentSessionStore
     ▼
③ ChatTurnRecorder : DelegatingAIAgent             ← .Use #1(外)  mem0 remember + 對話持久化
     ▼
④ SkillRoutingAgent : DelegatingAIAgent            ← .Use #2(內)  確定性 skill 路由,命中即短路
     ▼
⑤ ChatClientAgent                                  ← IChatClient.AsAIAgent(...)
     ├ ChatOptions.Instructions = copilotInstructions(僅副駕)
     ├ AIContextProviders = [ ChatContextProvider ]     ← 護欄 prompt + mem0 recall → Instructions
     ├ ChatHistoryProvider = InMemoryChatHistoryProvider(SlidingWindow, MessagesExceed(20))
     └ IChatClient pipeline
          └ ⑥ FunctionInvokingChatClient            ← 框架預設裝飾器,執行 client tools
               └ ⑦ OpenAI/LiteLLM
```

### 3.2 直接回答 Q1

> **路由 middleware 掛在 `AIAgentBuilder` 的哪一層?與 `FunctionInvokingChatClient` 的相對順序?**

**路由 middleware 在第 ④ 層,`FunctionInvokingChatClient` 在第 ⑥ 層 —— 路由 middleware 嚴格在其外側,中間隔了整個 `ChatClientAgent`。**【核】依據:`AIAgentBuilder.Use` 產生的 `DelegatingAIAgent` 包住的是傳入的 `AIAgent`(即 `ChatClientAgent`),而 `FunctionInvokingChatClient` 是 `ChatClientAgent` 在建構時對傳入 `IChatClient` 加的**內層裝飾器**(`ChatClientAgentOptions.UseProvidedChatClientAsIs` XML doc 逐字:"By default the `ChatClientAgent` applies decorators to the provided `IChatClient` for doing for example automatic function invocation")。兩者不在同一層,不可能互換。

**這個順序為什麼安全 —— 兩個獨立的保證:**

1. **client tools 不會被路由 middleware 吃掉。** 路由未命中時 middleware 原樣 `base.RunCoreStreamingAsync(...)` 委派下去,client tools 循原路到 ⑥⑦,行為與今日副駕完全相同。路由命中時該輪短路(刻意天花板,01-plan §5.2),client tools 該輪不觸發 —— 這是**已知代價,不是 bug**。

2. **server 端 skill 呼叫結構上不可能洩漏成 `TOOL_CALL_*`。** 因為 skill **從來沒有被註冊成 `AITool`**:第 ④ 層是自己 `await _workflows.InvokeSkillAsync(...)`,一次普通的 C# 方法呼叫。模型根本沒看過這個工具,也就沒有 tool-call 事件可以外洩。
   —— 這比「靠 `FilterServerToolsFromMixedToolInvocationsAsync` 濾掉」強一個等級:那道濾網是 internal、preview、我們無法測試的第三方行為;而「不註冊就沒有事件」是型別系統層級的保證。**這是本設計刻意不走 `WithAITool(...)` 的唯一理由,值得留註解。**

```csharp
// ponytail: skill 走確定性路由(SkillRoutingAgent),不註冊成 AITool。
// 代價:命中 skill 的那一輪 client tools 不會被呼叫(01-plan §5.2)。
// 好處:server 端呼叫不可能洩漏成 AG-UI 的 TOOL_CALL_* 事件——不靠 AGUI 那支 internal 的
//       FilterServerToolsFromMixedToolInvocationsAsync,那是 preview 且我們測不到。
// 升級路徑:量到「查數字 + 切視圖」混合指令的真實需求 → 改注入 messages 後正常 run,
//          但那會把受約束的 summary 併回自由主 run,須先解決 gpt-4o-mini 竄改數字的問題。
```

### 3.3 為什麼 ③ 必須在 ④ 外面(這是 §3 的第二個結論,同樣關鍵)

**問題:** 若把「mem0 remember + 對話持久化」放在 `ChatContextProvider.StoreAIContextAsync`(第 ⑤ 層內),那麼**路由命中而短路的那一輪永遠不會被記住、不會被持久化** —— 因為 ④ 短路了,⑤ 根本沒跑到。這是**對鏈路 A 現行行為的直接回歸**(今日 `ChatAsync` 無論是否命中工具都會 `RememberAsync` + `AddAsync`)。

**同時的反向約束:** 護欄 prompt 與 mem0 recall **不可以**放在 ③(外層),因為:
- 短路輪的 summary 呼叫**刻意是裸的**(只有 `SummaryInstruction` + 一則 user),不帶歷史、不帶 mem0、不帶護欄(02-spec §5.1 逐項不得放寬)。放外層就會污染它。
- 而且外層要注入 instructions 只能走 `AIContext.Messages`(`UseAIContextProviders` 只吃 `MessageAIContextProvider`,其貢獻僅有 messages)→ 撞上 **N3** 的 history 累積。

**結論(拍板):一個 `AIContextProvider` 的兩個 hook 拆到兩個接縫。**

| 職責 | 落點 | 理由 |
| --- | --- | --- |
| `ProvideAIContextAsync`:護欄 prompt + mem0 recall → **`AIContext.Instructions`** | ⑤ 內掛 `ChatClientAgentOptions.AIContextProviders` | 短路輪本就不該有;走 Instructions 避開 N3 |
| mem0 remember + 對話持久化 | ③ `ChatTurnRecorder : DelegatingAIAgent`(④ 之外) | 兩條路徑(短路 / 委派)都要記 |

> **這是對 02-spec §4「以一個 `AIContextProvider` 承接三件事」的修正。** 修正理由是 N3 + 短路語意衝突,兩者都是【核】。單一 `AIContextProvider` 在框架的接縫幾何下**做不到**同時滿足「短路輪也要持久化」與「短路輪不要護欄/mem0」。
> 若堅持形式上只有一個類別,可以讓 `ChatTurnRecorder` 持有同一個 `ChatContextProvider` 實例並在 ③ 呼叫其 `StoreAIContextAsync`。**不推薦** —— 兩個接縫就是兩個接縫,假裝成一個只會讓下一個讀碼的人 3am 時看不懂為什麼 `InvokedAsync` 被外面手動叫。

---

## 4. 確切簽章 + before/after

### 4.1 `ChatContextProvider`(新增,`Platform.Service/ChatContextProvider.cs`)

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Platform.Service;

/// <summary>
/// 兩條聊天鏈路共用的 per-run context:固定護欄 prompt + mem0 長期記憶 recall。
/// 兩者都注入 AIContext.Instructions(不是 Messages)——Messages 會被寫進持久化的 chat history
/// 並每輪重複累積(1.13.0 實測),Instructions 則每輪重組、不入 history。
/// </summary>
public sealed class ChatContextProvider : AIContextProvider
{
    // 逐字沿用 ChatService.cs:30-31 的 ChatGuardPrompt 與 :25-26 的 SystemMemoryPrefix,一字不改。
    private const string ChatGuardPrompt = "…";      // 原樣搬移
    private const string SystemMemoryPrefix = "…";   // 原樣搬移

    private readonly IMem0Client _mem0;
    private readonly IChatIdentityAccessor _identity;   // 見 §4.4
    private readonly ILogger<ChatContextProvider> _logger;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken);

    // 刻意不覆寫 StoreAIContextAsync —— remember/持久化在 ChatTurnRecorder(§3.3)。
}
```

`ProvideAIContextAsync` 主體:

```csharp
var instructions = ChatGuardPrompt;

var user = _identity.CurrentUser;
if (user is null)
    return new AIContext { Instructions = instructions }; // 匿名不 recall mem0

var (uid, _) = _identity.DeriveMemoryKeys();
var lastUser = context.RequestMessages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

// pipeline boundary best-effort:任意 IMem0Client 實作例外都降級為空 recall。
var memories = await RecallBestEffortAsync(uid, lastUser, cancellationToken);
if (!string.IsNullOrWhiteSpace(memories))
{
    instructions += "\n" + SystemMemoryPrefix + "\n" + memories;
}

return new AIContext { Instructions = instructions };
```

**行為對照:**

```
── BEFORE(ChatService.BuildPromptAsync,:216-240)────────────────
messages = [ system(ChatGuardPrompt) ]
           + [ system(SystemMemoryPrefix + "\n" + memories) ]  若 memories 非空
           + _memory.GetRecent(cid)                             ← 自寫視窗
           + [ user(message) ]

── AFTER ────────────────────────────────────────────────────────
ChatOptions.Instructions = (agent 自身 instructions? + "\n") + ChatGuardPrompt
                           + ("\n" + SystemMemoryPrefix + "\n" + memories)?
messages = InMemoryChatHistoryProvider 的視窗(框架管,turn 原子裁切)
           + [ user(message) ]
```

**唯一語意差:護欄與 mem0 從 `system` 訊息變成 `Instructions`。**【推】對 OpenAI 相容端點,`ChatOptions.Instructions` 就是組成第一則 system message,對模型可見度等價。**但這是行為層的變更,必須有測試背書**(§10 T-P3-1)。

### 4.2 `SkillRoutingAgent`(新增,`Platform.Service/SkillRoutingAgent.cs`)

```csharp
public sealed class SkillRoutingAgent : DelegatingAIAgent
{
    public SkillRoutingAgent(
        AIAgent innerAgent,
        ILlmAgent bareLlm,               // ← 路由/摘要用「裸」LLM,不走 innerAgent(見下)
        IWorkflowService workflows,
        IChatIdentityAccessor identity,
        ILogger<SkillRoutingAgent> logger) : base(innerAgent);

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, CancellationToken cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken);
}
```

**必須是 `RunCore*` 而不是 `RunAsync`**(N1)。**兩個都要覆寫** —— 鏈路 A 阻塞走前者、鏈路 A 串流與 AG-UI 走後者。

`RunCoreStreamingAsync` 主體(阻塞版同構,只差最後一步):

```csharp
var userCtx = _identity.CurrentUser;              // 匿名 → null
var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

// TryRouteAndExecuteAsync 整段原封搬過來(ChatService.cs:247-293),含:
//   匿名不路由 / 目錄失敗 best-effort / 最多兩次路由 / MatchTool 全等再寬鬆 /
//   SingleRequiredStringKey / template_* 跳過 / 角色過濾 / kb_query→rag_qa 兜底 /
//   單一工具失敗回錯誤字串 / 任何例外吞成 null 退純聊天
var summaryMessages = await TryRouteAndExecuteAsync(lastUser, userCtx, cancellationToken);

if (summaryMessages is null)
{
    // 未命中:原樣委派,client tools / 短期記憶 / mem0 recall / 護欄全部照常(第 ⑤⑥ 層)。
    await foreach (var u in base.RunCoreStreamingAsync(messages, session, options, cancellationToken))
        yield return u;
    yield break;
}

// 命中:用「裸」LLM 串流摘要,不經 innerAgent。
// 這是刻意的——經 innerAgent 會被 ChatContextProvider 追加護欄+mem0、被 history provider
// 前綴歷史,直接違反 02-spec §5.1「路由決策/摘要刻意不帶短期歷史與 mem0」。
await foreach (var chunk in _bareLlm.StreamAsync(summaryMessages, cancellationToken))
    yield return new AgentResponseUpdate(ChatRole.Assistant, chunk);
```

**為什麼保留 `ILlmAgent`(不是砍掉)**:路由呼叫與 summary 呼叫要的正是「一次無記憶、無工具、無 context 的 LLM 往返」,`ILlmAgent` 就是這個東西。留著它:
- `SummaryInstruction` / `RoutingInstruction` 常數與 33 個 `ChatSkillRoutingTests` 的 `FakeLlmAgent` 斷言**原封不動**(02-spec §5.1 明令 summary prompt 常數不得修改,現行測試以字串相等斷言)。
- `Platform.Service` 不必碰 OpenAI SDK。

### 4.3 `ChatTurnRecorder`(新增,`Platform.Service/ChatTurnRecorder.cs`)

```csharp
/// <summary>
/// 兩條鏈路共用的「一輪結束後」副作用:mem0 remember + 對話持久化。
/// 掛在 SkillRoutingAgent 之外,所以路由命中而短路的那一輪同樣會被記住(現行鏈路 A 語意)。
/// 匿名(無 JWT 身分)不 remember、不持久化。
/// </summary>
public sealed class ChatTurnRecorder : DelegatingAIAgent
{
    public ChatTurnRecorder(
        AIAgent innerAgent,
        IMem0Client mem0,
        IConversationStore conversations,
        IChatIdentityAccessor identity,
        ILogger<ChatTurnRecorder> logger) : base(innerAgent);

    protected override async Task<AgentResponse> RunCoreAsync(...);
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(...);
}
```

串流版的累積邏輯 = 現行 `StreamChatAsync` 的 `StringBuilder accumulated`(`ChatService.cs:113,141,151`)原封搬進來。**串流中途例外時不記錄任何東西**(現行 `ChatService.cs:423` 的 `StreamChat_MidStreamFailure_Propagates_DoesNotPersistPartial` 測的正是這件事,語意不得變)。

### 4.4 `IChatIdentityAccessor`(新增,`Platform.Service/Abstractions/`)

【推】三個新元件都需要「本次請求的 `UserContext`」,而它們住在 agent pipeline 裡拿不到 controller 的 `User`。

```csharp
namespace Platform.Service.Abstractions;

/// <summary>
/// 供 agent pipeline 取得本次請求的登入身分與記憶 key。實作在 Web 層讀 IHttpContextAccessor;
/// 測試直接塞固定值。匿名時 CurrentUser 為 null。
/// </summary>
public interface IChatIdentityAccessor
{
    UserContext? CurrentUser { get; }

    /// <summary>沿用 ChatService.cs:202-213 的防 IDOR 語意,原樣搬移,不放寬。</summary>
    (string Uid, string Cid) DeriveMemoryKeys();
}
```

> `// ponytail: 一個介面一個實作,通常違反 YAGNI——但 Platform.Service 不能引用 ASP.NET Core,`
> `// 且 57 個既有測試要能塞身分而不起 WebApplicationFactory。這是被專案結構逼出來的,不是投機抽象。`

**Web 層實作**(`Platform.Web/Infrastructure/HttpChatIdentityAccessor.cs`)讀 `IHttpContextAccessor`,`User.ToUserContext()`;body 的 `userId`/`conversationId` 由 `ChatService` 呼叫時放入 `HttpContext.Items`(只有鏈路 A 有;AG-UI 用 `threadId`)。

### 4.5 `JwtTenantIsolationKeyProvider`(新增,`Platform.Web/Infrastructure/`)

```csharp
public sealed class JwtTenantIsolationKeyProvider : SessionIsolationKeyProvider
{
    private readonly IHttpContextAccessor _http;

    /// <summary>
    /// isolation key 只取 JWT 的 tenant,絕不取 wire 上的 threadId / request body 任何欄位
    /// (框架 XML doc 明確警告 ThreadId 不是授權憑證)。取不到 → 回 null,
    /// 由 Strict=true 的 IsolationKeyScopedAgentSessionStore 拋 InvalidOperationException(fail-closed)。
    /// </summary>
    public override ValueTask<string?> GetSessionIsolationKeyAsync(CancellationToken cancellationToken)
        => new(_http.HttpContext?.User.Identity?.IsAuthenticated == true
            ? _http.HttpContext.User.ToUserContext().TenantCode
            : null);
}
```

### 4.6 `Program.cs` before/after

```csharp
// ── BEFORE(Program.cs:83, 263-275)──────────────────────────────
builder.Services.AddSingleton<IChatMemoryStore, InMemoryChatMemoryStore>();
…
const string copilotInstructions = "你是本系統的操作助理,…";
var copilotAgent = app.Services.GetRequiredService<IChatClient>()
    .AsAIAgent(instructions: copilotInstructions, name: "OperationsAssistant");
app.MapAGUI("/api/copilot/agui", copilotAgent).AllowAnonymous();

// ── AFTER ───────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IChatIdentityAccessor, HttpChatIdentityAccessor>();
builder.Services.AddSingleton<SessionIsolationKeyProvider, JwtTenantIsolationKeyProvider>();

var hostedAgent = builder.Services.AddAIAgent(
    name: "OperationsAssistant",
    createAgent: (sp, name) =>
    {
        var inner = sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Temperature = 0.2f,
                Instructions = copilotInstructions,      // AIContext.Instructions 會換行接在後面(N4 實測)
            },
            AIContextProviders = new[] { sp.GetRequiredService<ChatContextProvider>() },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
            {
                // 20 則視窗:MessagesExceed(20) 才是觸發條件;minimumPreservedTurns 是硬下限,
                // 設 1 以免抑制觸發(01-plan §2:設 20 時 26 則完全不觸發)。
                ChatReducer = new SlidingWindowCompactionStrategy(
                        trigger: CompactionTriggers.MessagesExceed(20),
                        minimumPreservedTurns: 1)
                    .AsChatReducer(),
            }),
        });

        return new AIAgentBuilder(inner)
            .Use(a => ActivatorUtilities.CreateInstance<ChatTurnRecorder>(sp, a))   // ③ 外(N2:先 Use = 外)
            .Use(a => ActivatorUtilities.CreateInstance<SkillRoutingAgent>(sp, a))  // ④ 內
            .Build(sp);
    },
    lifetime: ServiceLifetime.Scoped)          // scoped:context provider / recorder 需要 per-request 身分
  .WithInMemorySessionStore(withIsolation: true);   // 預設就是 true;顯式寫出來,別靠預設值

…
app.MapAGUI(hostedAgent, "/api/copilot/agui").RequireAuthorization();
```

> **`ServiceLifetime.Scoped` 的注意事項【推】**:`WithSessionStore` 的 XML doc 說 session store 預設 Singleton「because session stores persist conversation state across requests」—— store 是 singleton,agent 是 scoped,兩者生命週期刻意不同,這是框架預期用法。但 `AIHostAgent` 由 scoped agent 組成 → 每請求建一次 agent。成本是幾個 `new`,不是網路呼叫。**這一項需要在 P1 收尾的 e2e 驗一次沒有 captive dependency 警告。**

---

## 5. mem0 / 記憶語意的具體落點

### 5.1 mem0 recall 改走 Instructions(對 02-spec §4 的修正)

| | 02-spec §4 原案 | 本設計 | 理由 |
| --- | --- | --- | --- |
| mem0 recall | `ProvideAIContextAsync` → `Messages` | `ProvideAIContextAsync` → **`Instructions`** | N3【核】:`Messages` 會進 history 且每輪累積,20 則視窗會被 mem0 前言塞滿,越聊越貴、越聊越舊 |
| 護欄 prompt | `ProvideAIContextAsync` → `Instructions` | 同(不變)✅ | 02-spec 這半邊本來就對 |

替代方案(**已考慮並否決**):用 `InMemoryChatHistoryProviderOptions.StorageInputRequestMessageFilter` 把注入的 system 訊息從**存檔**濾掉,保留 `Messages` 形式。否決理由:多一個要維護的 predicate、且要能可靠辨認「哪則 system 是我們注入的」(只能靠字串前綴比對,脆)。走 `Instructions` 是零成本的正解。
> 但這個 filter **是**未來的升級路徑 —— 若哪天真需要注入 assistant/user 角色的 context(Instructions 塞不下),就用它。

### 5.2 【拍板 Q2】mem0 的 `uid` 由誰推導

> **沿用 `DeriveMemoryKeys`(搬進 `IChatIdentityAccessor`),不交給 `SessionIsolationKeyProvider`。三個理由,前兩個是【核】。**

1. **【核】會造成隱私回歸。** isolation key = **租戶**(02-spec §2.4 明令);mem0 uid 現行是 `{TenantCode}:{UserId}`(`ChatService.cs:210`)。若 uid 改用 isolation key,同租戶所有使用者的長期記憶會合併成一池 —— 這正是 `ChatServiceTests.cs:230` `LoggedIn_MemoryKeys_BindToJwtIdentity_NotClientBody_IsolatingAcrossUsers` 斷言 `demo-a:user-a` ≠ `demo-b:user-b` 要防的事。**直接的安全回歸,不能做。**
2. **【核】框架也沒讓你這麼做。** `SessionIsolationKeyProvider` 的產出只流向 `IsolationKeyScopedAgentSessionStore.GetScopedConversationIdAsync`,用來把 store key 改寫成 `{escapedKey}::{conversationId}`。`AIContextProvider.InvokingContext` 只帶 `Agent` / `Session` / `AIContext`,**拿不到 isolation key**。要在 provider 裡用它得自己再注入一次 provider —— 等於繞過框架的意圖去複製一份租戶碼。
3. **【推】兩者職責本來就不同。** isolation key 回答「這個 session 屬於哪個信任邊界」;mem0 uid 回答「這些長期事實屬於誰」。前者是租戶級,後者是人級。硬合併是把兩個不同粒度綁死。

**落地**:`DeriveMemoryKeys` 的邏輯**逐字**從 `ChatService.cs:202-213` 搬到 `HttpChatIdentityAccessor`,包含「已登入時不信任 body 的 userId」這條防 IDOR 語意(02-spec §3.2 明令不得放寬)。

### 5.3 conversation 維度的 key(P2)

```
GetSessionAsync 的 conversationId 參數 = DeriveMemoryKeys().Cid
  已登入:$"{TenantCode}:{UserId}"                       ← body conversationId 空白
          $"{TenantCode}:{UserId}:{conversationId}"      ← 非空白
  匿名  :body userId / "default"(僅鏈路 A 可能)
AG-UI  :threadId(wire) → 同樣經 DeriveMemoryKeys 前綴身分
```

再經 `IsolationKeyScopedAgentSessionStore` 前綴 → 實際 store key = `{tenant}::{tenant}:{user}:{cid}`。租戶碼重複一次,無害(§9.5 列為刻意簡化)。

### 5.4 匿名鏈路 A 與 `Strict=true` 的責任分界

AG-UI 使用 strict isolation store；`/api/chat` 的 `ChatAssistant` 則刻意 `withIsolation:false`。登入 ChatView session key 在 derivation 已帶 `{tenant}:{user}`，匿名維持 caller conversationId 的短期連續性。兩者不可混為一談。

```csharp
// ChatService,兩條路徑共用
var session = await _hostAgent.GetOrCreateSessionAsync(cid, ct);
…
await _hostAgent.SaveSessionAsync(cid, session, ct);
```

這同時保住「匿名裸聊:不路由、不 recall/remember mem0、不持久化、`/api/chat/history` 回空陣列」；AG-UI 的 `Strict=true` 一步都不放寬。

---

## 6. 完整呼叫鏈 trace

### 6.1 鏈路 A 串流,已登入,路由命中 `kb_query`

```
POST /api/chat/stream {message,userId,conversationId} + Bearer
 └ ChatController.Stream (:42)
     ├ Response.ContentType = "text/event-stream; charset=utf-8"        (:44) 不變
     ├ SetAuthInvalidHeaderIfNeeded()                                   (:46) 不變,見 §9.3
     ├ bodyFeature.DisableBuffering()                                   (:49) 不變
     └ try {                                                            (:54)
        └ ChatService.StreamChatAsync(...)
            ├ identity.DeriveMemoryKeys() → (uid,cid)
            ├ hostAgent.GetOrCreateSessionAsync(cid)
            │    └ IsolationKeyScopedAgentSessionStore.GetSessionAsync(agent, cid)
            │         ├ JwtTenantIsolationKeyProvider → "demo-a"    ← 取不到就在這裡 fail-closed
            │         └ InMemoryAgentSessionStore["demo-a::demo-a:user-a:c1"]
            └ hostAgent.RunStreamingAsync(messages, session, ct)
                └ ③ ChatTurnRecorder.RunCoreStreamingAsync
                    └ ④ SkillRoutingAgent.RunCoreStreamingAsync
                        ├ TryRouteAndExecuteAsync(lastUser, userCtx, ct)
                        │   ├ BuildToolsAsync → WorkflowService.GetSkillCatalogAsync   [HTTP → :8001]
                        │   │    └ 失敗 → LogWarning + Array.Empty<LlmTool>() → 退純聊天
                        │   ├ RouteAsync ×≤2 → ILlmAgent.CompleteAsync(裸)             [HTTP → LiteLLM]
                        │   │    └ MatchTool:全等 → 寬鬆包含(取最長名)
                        │   └ InvokeSkillToolAsync("kb_query", "query", arg, …)
                        │        ├ WorkflowService.InvokeSkillAsync                     [HTTP → :8001]
                        │        ├ IsAbstain? → 再打 rag_qa + 「證據不足…」前綴
                        │        └ 失敗 → "Skill kb_query 呼叫失敗:…" 字串(不炸整輪)
                        ├ summaryMessages = [system(SummaryInstruction), user(問題+工具結果)]
                        └ ★ 短路:ILlmAgent.StreamAsync(summaryMessages) → yield chunk
                        ✗ 不進 ⑤⑥⑦,client tools 該輪不觸發(已知天花板)
                    ← 串流結束
                    ├ mem0.RememberAsync(uid, message, 全文)      best-effort,吞錯
                    └ conversations.AddAsync(...)  失敗 → 記 HttpContext.Items(§9.4)
            └ hostAgent.SaveSessionAsync(cid, session)
        └ foreach line: Response.WriteAsync($"data:{line}\n")           (:58-60) 格式不變
     } catch (Exception) → "event:error\ndata:<固定中文>\n\n"            (:71-78) 不變
```

### 6.2 AG-UI,已登入,路由未命中,client tool `switchView`

```
POST /api/copilot/agui {threadId,messages,tools:[switchView,…]} + Bearer
 └ MapAGUI(hostedAgent, pattern) 端點             ← .RequireAuthorization():無/壞 JWT 直接 401
     ├ 解析 RunAgentInput,tools → AITool[]
     ├ AIHostAgent.GetOrCreateSessionAsync(threadId)   ← 同 6.1 的 isolation 路徑
     └ RunStreamingAsync
         └ ③ ChatTurnRecorder
             └ ④ SkillRoutingAgent → 路由 NONE(兩次)→ base.RunCoreStreamingAsync 委派
                 └ ⑤ ChatClientAgent
                     ├ ChatContextProvider.ProvideAIContextAsync
                     │    → Instructions = copilotInstructions
                     │                     + "\n" + ChatGuardPrompt
                     │                     + "\n" + SystemMemoryPrefix + "\n" + memories
                     ├ InMemoryChatHistoryProvider → 視窗內歷史(turn 原子)
                     └ ⑥ FunctionInvokingChatClient
                         └ ⑦ LiteLLM → tool_call switchView({"view":"documents"})
                             └ client tool 無 server 端實作 → 冒泡
             ← 串流結束
             ├ mem0.RememberAsync   ← 副駕對話也進 mem0(02-spec §4.2 刻意)
             └ conversations.AddAsync  ← 也進 GET /api/chat/history
         └ SaveSessionAfterStreamingAsync(...)   ← 框架自己存,不必我們管
     → SSE `data: ` (有空格) RUN_STARTED / TEXT_MESSAGE_* / TOOL_CALL_START/ARGS/END / RUN_FINISHED
```

---

## 7. await 傳染面 / 實作衝擊分析

| # | 位置 | 現況 | 之後 | 風險 |
| --- | --- | --- | --- | --- |
| ① | `ChatService.ChatAsync:83` `await _agent.CompleteAsync(messages, ct)` | 已 async | → `await _hostAgent.RunAsync(msgs, session, ct)`,前面多兩個 await(`GetOrCreateSessionAsync`、`DeriveMemoryKeys` 同步) | 低,同一 async 方法內 |
| ② | `ChatService.StreamChatAsync:121` `_agent.StreamAsync(...).GetAsyncEnumerator(ct)` | 手動列舉,`yield` 不在 try/catch 內 | → `_hostAgent.RunStreamingAsync(...)`,同樣手動列舉。**`GetOrCreateSessionAsync` 的 await 必須放在建 enumerator 之前**(`:112` span 起始之後) | 低;不涉 `yield return` 於 try 內,無 CS1626 |
| ③ | **新** `SkillRoutingAgent.RunCoreStreamingAsync` | — | `[EnumeratorCancellation]` 必掛在 `cancellationToken` 上,否則 `WithCancellation` 失效 | **中**,漏掉不會編譯錯,只會在取消時默默不取消 |
| ④ | **新** `ChatTurnRecorder.RunCoreStreamingAsync` | — | 串流結束後的 `RememberAsync`/`AddAsync` 必須在 `await foreach` **之後、方法返回之前**,且**不可**放在 `finally`(例外時不得持久化,N5 + 現行測試 `:423`) | **中**,寫進 finally 就是半截持久化 bug |
| ⑤ | `AgentFrameworkLlmAgent` | 建構時自造 `OpenAIClient` | **不動**。它繼續服務 `ILlmAgent`(路由/摘要的裸呼叫) | 無 |
| ⑥ | `ChatController` | — | **一行都不改**(§9.3) | 無 |
| ⑦ | `IMem0Client` / `IConversationStore` / `IWorkflowService` | 已注入 `ChatService` | 改注入到 `ChatContextProvider` / `ChatTurnRecorder` / `SkillRoutingAgent`;`ChatService` 的建構子參數從 7 個縮到 3~4 個 | 低,但 57 個測試的 `Build(...)` helper 要一起改(§10.3) |

**沒有新的同步→非同步傳染**:所有新程式都在既有 async 邊界內。真正的傳染是**相反方向** —— `ChatService` 從「自己組 prompt」變成「開 session、跑 agent、存 session」,程式量**淨減少**(`BuildPromptAsync`、`AppendExchange`、`TryRouteAndExecuteAsync` 及其五個私有方法全部移出)。

---

## 8. 對外契約:零變更(逐項核對 02-spec §1)

| 契約 | 狀態 |
| --- | --- |
| `POST /api/chat` request/response 形狀 | 不變(`ChatController` 零改) |
| `POST /api/chat/stream` SSE `data:`(**無空格**)+ 空行結尾 + 逐 event flush | 不變(`:58-64` 零改) |
| `event:error\ndata:<固定中文>\n\n` | 不變(`:71-78` 零改) |
| `GET /api/chat/history` 依身分過濾、匿名回空陣列 | 不變 |
| AG-UI `data: `(**有空格**)+ 事件型別集合 | 不變(仍是框架的 `MapAGUI`) |
| ApiError `{timestamp,status,message,fieldErrors}` | 不變 |
| `X-Auth-Invalid: 1` | 不變 |
| **`/api/copilot/agui` `AllowAnonymous` → `RequireAuthorization()`** | ⚠️ **唯一變更**,02-spec §2.1 已定案 |

`Program.cs:186-188` 的限流分區(`/api/chat`、`/api/chat/stream`、`/api/copilot/agui` 30 req/min/IP)**照舊涵蓋**,`StartsWithSegments("/api/copilot/agui")` 不受 `RequireAuthorization` 影響。

---

## 9. 五個開放問題的拍板

### 9.1 【Q1】路由 middleware 的層級 → **見 §3**

第 ④ 層(`AIAgentBuilder.Use` 的第二個,`ChatTurnRecorder` 之內、`ChatClientAgent` 之外);`FunctionInvokingChatClient` 在第 ⑥ 層,相隔整個 `ChatClientAgent`。安全性不靠 AGUI 的 internal 濾網,靠「skill 從未註冊成 `AITool`」。

### 9.2 【Q2】mem0 `uid` 的推導者 → **見 §5.2**

沿用 `DeriveMemoryKeys`(搬進 `IChatIdentityAccessor`)。交給 isolation key 會把 mem0 從 per-user 降級成 per-tenant —— 安全回歸,且框架也沒開這個口。

### 9.3 【Q3】`event:error` 與 `X-Auth-Invalid` 的產生點

**兩者都留在 `ChatController` 原地,一行不改。**

**`X-Auth-Invalid`(`:90-96`)**:純粹是 request 的函數 —— 「有 `Authorization` header 且 `User.Identity.IsAuthenticated != true`」。與 `ChatService`、agent、記憶完全無關。AG-UI 側不需要它:`RequireAuthorization()` 之後那種情況直接是 `401`,前端既有的全域登出機制本來就吃 401(root AGENTS.md 的 auth 契約)。

**`event:error`(`:71-78`)**:是**傳輸層**語意 —— 「body 已 `HasStarted`,`GlobalExceptionHandler` 改寫不了,所以就地補終止幀」。它需要的只有「例外有沒有冒到 `await foreach` 這裡」。N5【核】確認:串流中途的下游例外會原樣穿過 `ChatClientAgent` → ④ → ③ → `ChatService` → controller,`StoreAIContextAsync` / recorder 的持久化都不會執行。所以幀照樣產出、半截內容照樣保留、什麼都不用動。

**唯一的實作約束(必須寫成測試)**:③④ 兩層**不得吞掉會導致 `event:error` 的例外**。具體:
- ④ `SkillRoutingAgent` 的 `catch (Exception) → return null`(現行 `ChatService.cs:288-292`)**只能包住路由/工具那段**,絕不可包住 `base.RunCoreStreamingAsync(...)` 或 summary 的 `StreamAsync(...)`。
- ③ `ChatTurnRecorder` 的持久化 try/catch 只包 `AddAsync`,不包 `await foreach`。

> **這是本設計最容易被實作者寫錯的一行。** 現行 `TryRouteAndExecuteAsync` 的 try 範圍剛好正確;搬家時很容易順手把委派也包進去,結果串流錯誤變成「靜默回一句空話」而不是 `event:error`。§10 的 T-P4-4 專測這件事。

### 9.4 【Q4】阻塞 500 vs 串流 warning 的差異怎麼表達

**問題重述**:`ChatTurnRecorder`(或 `StoreAIContextAsync`)看不到自己被哪條鏈路呼叫。02-spec §4.1 要求:鏈路 A 阻塞路徑持久化失敗 → 往上拋 500;串流路徑 → 只記 warning。

**拍板:recorder 一律 best-effort(吞錯 + `LogWarning`),並把失敗記進 `HttpContext.Items`;由 `/api/chat` 這個**端點**決定要不要升級成 500。**

```csharp
// Platform.Service/Abstractions/IChatIdentityAccessor.cs — 加一對成員
public interface IChatIdentityAccessor
{
    UserContext? CurrentUser { get; }
    (string Uid, string Cid) DeriveMemoryKeys();

    /// <summary>本輪持久化失敗時由 ChatTurnRecorder 記錄;阻塞端點據此決定是否拋 500。</summary>
    Exception? PersistFailure { get; set; }
}
```

```csharp
// ChatTurnRecorder（兩條路徑共用，一律 best-effort）
try { await _conversations.AddAsync(message, reply, userCtx, ct); }
catch (Exception ex)
{
    _logger.LogWarning(ex, "聊天持久化失敗：{訊息}", ex.Message);
    _identity.PersistFailure = ex;
}
```

```csharp
// ChatService.ChatAsync —— 只有阻塞路徑升級
var response = await _hostAgent.RunAsync(msgs, session, ct);
if (_identity.PersistFailure is { } ex)
{
    // 現行語意:阻塞式持久化失敗照舊往上拋（對外 500），不吞。
    ExceptionDispatchInfo.Capture(ex).Throw();
}
```

`HttpChatIdentityAccessor` 的實作就是 `HttpContext.Items["chat.persist.failure"]` 的一層薄包裝(scoped,天然 per-request)。

**為什麼是這個解、而不是別的:**

| 候選 | 判定 |
| --- | --- |
| 由 `AgentRunOptions` 帶旗標 | **不可行**【核】。`AIContextProvider.InvokedContext` 只有 `Agent`/`Session`/`RequestMessages`/`ResponseMessages`/`InvokeException`,**沒有 RunOptions**。`DelegatingAIAgent` 拿得到 options,但那樣等於發明一個 `AgentRunOptions` 子類,是自創中介層(01-plan §3 目標 4 禁止) |
| 讀 `HttpContext.Request.Path` 判斷 `/api/chat` vs `/api/chat/stream` | 可行但**否決**。把端點政策寫進記憶層,增一條路徑就得回來改一次字串比對 |
| `AsyncLocal<bool>` 環境旗標 | 否決。隱式,`await foreach` 跨越 yield 時語意難推,3am 讀不懂 |
| 兩個 recorder 實例(嚴格版/寬鬆版) | 否決。同一顆 agent 兩條鏈路共用,做不到分兩個實例 —— 那正是本計畫要消滅的分裂 |

**核心論點**:這個差異**本來就不是記憶層的職責,是端點的錯誤處理政策**。阻塞端點承諾「回 200 就代表存到了」,串流端點承諾不了(bytes 已送出)。政策放在做出承諾的那一層,才是它該在的地方。recorder 保持單一、無條件的行為(永遠 best-effort + 留痕),兩條鏈路共用同一顆腦的目標不受影響。

> `// ponytail: 持久化失敗一律 best-effort，只把例外留在 HttpContext.Items；`
> `// 「阻塞路徑要 500」是 /api/chat 這個端點的承諾，不是記憶層的，所以升級動作留在 ChatService.ChatAsync。`
> `// 天花板：非 HTTP 觸發的 agent run（未來的排程/背景任務）拿不到 HttpContext，`
> `//         但那種情境本來就沒有「回 500 給誰」的概念，屆時 PersistFailure 恆為 null 即正確。`

### 9.5 【Q5】刪 `IChatMemoryStore` 後,受影響的測試怎麼改寫

**先講量級**:`InMemoryChatMemoryStore` / `IChatMemoryStore` 在生產碼只有 **6 個引用點**(`ChatService.cs:46,55,63,224,581,582` + `Program.cs:83`),但在測試有 **9 個構造點,散在 3 個檔案**。

#### (a) `Platform.Service.Tests/InMemoryChatMemoryStoreTests.cs` —— **整檔刪除**(3 個 `[Fact]`)

| 現行個案 | 處置 |
| --- | --- |
| `Append20_GetRecent_ReturnsAll_InInsertionOrder` (:18) | → 新檔 `ChatSessionWindowTests.Window_At20Messages_NothingCompacted`(**on-point**) |
| `Append21_TrimsOldest_FirstIsSecondInserted` (:31) | → `Window_At21Messages_OldestTurnDropped`(**off-point**)。注意斷言要改:框架以 **turn** 為原子單位裁切,不是「剛好掉第 1 則」。**這是行為改善不是回歸**(02-spec §3.3),測試必須寫成新語意 |
| `GetRecent_UnknownConversation_ReturnsEmpty` (:45) | → `Session_UnknownKey_HasEmptyHistory_NotNull`。**必須驗內容為空,不可驗 `Assert.Null`** —— `GetOrCreateSessionAsync` 對未知 key 自動建空 session(§1 事實 #2【核】) |

**新增(規格數字必須有邊界測試)**:
- `Window_ToolCallAndResult_NotSplitByCompaction` —— 現行 `RemoveAt(0)` 會拆散 `FunctionCallContent`/`FunctionResultContent` 配對(01-plan §2 的未爆彈)。修掉了就要有測試釘住。
- `Window_SystemInstructions_NeverCompacted` —— 護欄/mem0 走 `Instructions` 不進 history,所以裁切裁不到它(§5.1)。

#### (b) `Platform.Service.Tests/ChatServiceTests.cs`(24 個 `[Fact]`/`[Theory]`)

`Build(...)` helper(`:10-13`)的 `InMemoryChatMemoryStore? memory` 參數移除,改成注入 `FakeAgentSessionStore` + `FakeChatIdentityAccessor`。**直接構造 `InMemoryChatMemoryStore` 的 3 處**:

| 個案 | 行 | 改寫方式 |
| --- | --- | --- |
| `Chat_ShortTermMemory_CarriesPriorExchange` | `:104-119` | 斷言對象從 `agent.LastMessages` 換成 `FakeChatClient` 收到的 messages。**斷言本身不變**(第二輪要看得到「第一問」「第一答」) |
| `LoggedIn_MemoryKeys_BindToJwtIdentity_NotClientBody_IsolatingAcrossUsers` | `:230-251` | **最重要的一個,不可弱化**。現行同時斷言(i)短期視窗跨使用者不撞、(ii)mem0 uid 用 JWT 身分。改寫後:(i) 由 session key(`FakeAgentSessionStore.Keys`)+ 內容斷言;(ii) 完全不變(§5.2 保住了 uid 語意)。**必須續驗內容不互見,不可退化成驗 key 字串不同** |
| `Build(...)` 預設路徑 | `:13` | 機械式改 |

另外 3 個 key 推導個案(`Fallback_BothBlank...:194`、`Fallback_UserOnly...:211`、`Fallback_ConversationOnly...:405`)不直接 new store,但斷言 mem0 uid + 視窗 key,需把視窗那半邊改成對 `FakeAgentSessionStore` 斷言。

#### (c) `Platform.Service.Tests/ChatSkillRoutingTests.cs`(33 個)

4 處構造(`:21, :452, :499, :602`)機械式替換。**33 個路由斷言全部不變** —— 因為 `SkillRoutingAgent` 保留 `ILlmAgent`(§4.2),`FakeLlmAgent` 與 `SummaryInstruction` / `RoutingInstruction` 字串相等斷言原封。這是 §4.2 那個設計選擇最大的回報。

#### (d) `Platform.Web/Program.cs:83`

`AddSingleton<IChatMemoryStore, InMemoryChatMemoryStore>()` 刪除。

#### (e) 需要新增的手寫 fake(xUnit + 手寫 fake,不引 mocking 套件)

| fake | 放哪 | 用途 |
| --- | --- | --- |
| `FakeChatIdentityAccessor` | 兩個測試專案 | 塞 `UserContext` / 記憶 key,免起 `WebApplicationFactory` |
| `FakeAgentSessionStore : AgentSessionStore` | `Platform.Service.Tests` | 記錄 `GetSessionAsync`/`SaveSessionAsync` 收到的 key,供隔離斷言 |
| `FakeChatClient`(`IChatClient`) | `Platform.Service.Tests`(`Platform.Web.Tests` 已有) | 取代 `FakeLlmAgent` 作為**主 run** 的斷言對象;`GetService` 目前回 `null`,擴充 `tools` 相關測試時要能回真東西(02-spec §7.2) |

---

## 10. 測試矩陣(先補行為測試,再動實作 —— 02-spec §7.1)

> 測試設計準則:決策表要收尾、規格數字要有 on/off-point、安全語義要有測試背書、失敗注入要含「沒有回應」的等價類、一等價類一代表值。

### 10.1 P0(實作**開始前**必須全綠的行為基線)

| # | 個案 | 斷言 |
| --- | --- | --- |
| B1 | 路由**命中** | 可觀察結果:`InvokeSkillAsync` 收到正確 name + `{inputKey:arg}`;回覆是 summary 而非自由回答 |
| B2 | 路由**未命中**(兩次 NONE) | 走純聊天,`InvokeSkillAsync` **零呼叫** |
| B3 | 目錄取得**失敗**(`WorkflowInvocationException`) | 聊天正常完成,不炸 |
| B4 | 目錄取得**逾時 / 傳輸例外**(`HttpRequestException`) | 同 B3。(**「沒有回應」的等價類**,與 B3 的「回錯誤碼」不同) |
| B5 | 單一工具呼叫**失敗** | 回「Skill … 呼叫失敗」字串,整輪不中斷 |
| B6 | mem0 呼叫**順序** | `RecallAsync` 在主 LLM 呼叫前;`RememberAsync` 在後,且參數是**完整**回覆 |
| B7 | 匿名 vs 登入分歧 | 匿名:不路由、不 remember、不 `AddAsync`、history 空陣列 |

### 10.2 P1–P4 新增

| # | Phase | 個案 | 斷言 |
| --- | --- | --- | --- |
| T-P1-1 | P1 | `/api/copilot/agui` **無 JWT** | `401` + ApiError 形狀 `{timestamp,status,message,fieldErrors}`(**決策表收尾**:不只驗狀態碼) |
| T-P1-2 | P1 | 過期/無效 JWT | 同上 401 |
| T-P1-3 | P1 | **租戶隔離**:A 先以 `threadId=t1` 說「我的密碼是 X」;B 以**同一** `threadId=t1` 提問 | B 看到的 messages **不含** "X"。**驗內容,禁用 nullity**(01-plan §8 明令) |
| T-P1-4 | P1 | isolation key 取不到(模擬未認證進到 store) | 拋 `InvalidOperationException`,**不**退回全域命名空間(fail-closed 有測試背書) |
| T-P2-1 | P2 | 視窗 **20 則** | 不裁切(**on-point**) |
| T-P2-2 | P2 | 視窗 **21 則** | 最舊的**整個 turn** 被裁(**off-point**) |
| T-P2-3 | P2 | history 含 tool call + result 配對,觸發裁切 | 配對**不被拆散** |
| T-P2-4 | P2 | AG-UI 同 `threadId` 連兩輪 | 第二輪記得第一輪;且**訊息不重複累加**(前端重送完整陣列 + 伺服器端記憶,02-spec §3.4) |
| T-P3-1 | P3 | `ChatContextProvider` 產出 | `AIContext.Instructions` 含 `ChatGuardPrompt`;mem0 非空時含 `SystemMemoryPrefix + "\n" + memories`;`AIContext.Messages` **為空**(釘住 §5.1 的修正) |
| T-P3-2 | P3 | 連續兩輪 | 第二輪 chat client 收到的 messages **不含**任何 mem0 前言(釘住 N3 不再發生) |
| T-P3-3 | P3 | mem0 `RecallAsync` 拋例外 | 聊天正常完成(best-effort **有測試背書**,不只寫在註解) |
| T-P3-4 | P3 | 阻塞路徑 `AddAsync` 拋 | `/api/chat` → **500 + ApiError**(決策表兩半都測) |
| T-P3-5 | P3 | 串流路徑 `AddAsync` 拋 | chunk 照常送完,**不**寫 `event:error`,只留 warning |
| T-P4-1 | P4 | AG-UI **非空 `tools`** | 出現 `TOOL_CALL_START`/`ARGS`/`END`,含工具名與參數(02-spec §7.2;現行 `FakeChatClient.GetService` 回 null 使其結構上不可能,需先擴充) |
| T-P4-2 | P4 | AG-UI 路由**命中** skill | 事件流中**不得**出現任何 skill 名的 `TOOL_CALL_*`(§3.2 的安全保證) |
| T-P4-3 | P4 | AG-UI 路由命中 + 前端同時提供 client tools | 該輪 client tool **不被呼叫**(釘住已知天花板,免得下次有人以為是 bug) |
| T-P4-4 | P4 | **串流中途** `IChatClient` 拋 | 例外冒到 `ChatController` → `event:error` 幀;**半截回覆不得持久化**(`AddAsync` 零呼叫、`RememberAsync` 零呼叫)。釘住 §9.3 的實作約束 |

### 10.3 每個 phase 收尾派 `e2e-verifier` 打真鏈路一次

02-spec §7.3 + 專案記憶 `fakes-hide-real-behavior`。`scripts/verify-copilot-shared-core.ps1` 僅是 black-box smoke companion，不可取代 C-03/C-04/C-05/C-07/C-08 的具名 integration tests、真服務 trace 與 browser/proxy 檢查；其 mock-gpt rebuild 路徑不可驗 routing。P1 額外要驗:Singleton hosted agent + per-call scope 沒有依賴生命週期錯誤(§0.2 註)。

---

## 11. 落地順序(change-sequence)

| 序 | 動作 | 檔案 | Phase |
| --- | --- | --- | --- |
| 1 | 補 P0 行為基線測試 B1–B7(**實作前**,證明重構前後等價) | `Platform.Service.Tests/ChatServiceTests.cs`、`ChatSkillRoutingTests.cs` | **P0** |
| 2 | 兩個 csproj 加 `Microsoft.Agents.AI` / `.Hosting` `PackageReference`(§2);`dotnet build` 確認還原成功 | `Platform.Service.csproj`、`Platform.Web.csproj` | **P0** |
| 3 | 新增 `IChatIdentityAccessor`(含 `PersistFailure`)+ `HttpChatIdentityAccessor`;`DeriveMemoryKeys` 逐字搬入 | `Platform.Service/Abstractions/`、`Platform.Web/Infrastructure/` | **P0** |
| 4 | `JwtTenantIsolationKeyProvider` + 註冊;`MapAGUI` 換 `IHostedAgentBuilder` overload + `.RequireAuthorization()`;`AddAIAgent` + `WithInMemorySessionStore(withIsolation:true)` | `Platform.Web/Infrastructure/`、`Program.cs` | **P1** |
| 5 | 前端 `HttpAgent` 移進元件、依 `session.token` 建立並帶 `Authorization: Bearer`;token 變動即重建 | `frontend/src/App.tsx`(**唯一前端改動**) | **P1** |
| 6 | 測試 T-P1-1~4 + `e2e-verifier` | `Platform.Web.Tests/CopilotAguiApiTests.cs` | **P1** |
| 7 | `ChatClientAgentOptions.ChatHistoryProvider` 掛 `InMemoryChatHistoryProvider` + `SlidingWindowCompactionStrategy(MessagesExceed(20), 1)`;`ChatService` 改為開 session → 跑 agent → 存 session(匿名走 `CreateSessionAsync` 不存,§5.4) | `Program.cs`、`ChatService.cs` | **P2** |
| 8 | **刪** `InMemoryChatMemoryStore.cs`、`Abstractions/IChatMemoryStore.cs`、`Program.cs:83` | `Platform.Service/` | **P2** |
| 9 | 測試遷移:刪 `InMemoryChatMemoryStoreTests.cs`,新增 `ChatSessionWindowTests.cs`(T-P2-1~4),改寫 §9.5(b)(c) 的 9 個構造點 + 新增 3 個 fake | 兩個測試專案 | **P2** |
| 10 | `ChatContextProvider`(護欄 + mem0 recall → `Instructions`)掛 `AIContextProviders` | `Platform.Service/ChatContextProvider.cs`、`Program.cs` | **P3** |
| 11 | `ChatTurnRecorder`(mem0 remember + 持久化 + `PersistFailure`)掛 `.Use` **第一個**;`ChatService.ChatAsync` 加 `PersistFailure` 升級 500 | `Platform.Service/ChatTurnRecorder.cs`、`ChatService.cs` | **P3** |
| 12 | 測試 T-P3-1~5 + `e2e-verifier` | 兩個測試專案 | **P3** |
| 13 | `SkillRoutingAgent`:`TryRouteAndExecuteAsync` 及 5 個私有方法 + 3 個 prompt 常數**逐字**搬出 `ChatService`,掛 `.Use` **第二個**;`ChatService` 瘦身 | `Platform.Service/SkillRoutingAgent.cs`、`ChatService.cs` | **P4** |
| 14 | 測試 T-P4-1~4(先擴充 `FakeChatClient.GetService` 使 tool call 結構上可能發生)+ `e2e-verifier` | `Platform.Web.Tests/` | **P4** |

**順序硬約束**:
- 1、2、3 是所有東西的前提。
- **P1 不可與 P2 對調**(01-plan §5.1:認證與隔離必須與記憶同一批落地)。
- P3、P4 之間無強制順序,但 **11 必須早於 13**(recorder 要在 routing 外側,先建外層再插內層,`.Use` 順序才自然,N2)。
- 8 必須在 7 之後(先有替代品再刪舊的)。

**每步 `dotnet build`,每個 phase 結束 `dotnet test` 兩個方案全綠。**

---

## 12. 刻意簡化總表(ponytail,含天花板與升級路徑)

| 簡化 | 天花板 | 何時升級 |
| --- | --- | --- |
| 路由命中即短路,不委派內層 agent | 該輪 client tools 不觸發(「查營收然後切分析視圖」只做前者) | 量到混合指令的真實需求,**且**先解決 gpt-4o-mini 竄改數字(01-plan §5.2) |
| skill 不註冊成 `AITool`,走確定性路由 | 模型無法自主組合多個 skill | 同上;附帶好處是不依賴 AGUI 那支 internal 濾網 |
| 護欄 + mem0 走 `AIContext.Instructions` 而非 `Messages` | 只能注入 system 級文字,塞不下 assistant/user 角色的 few-shot | 真需要多角色 context → `InMemoryChatHistoryProviderOptions.StorageInputRequestMessageFilter` 把注入訊息排除存檔(§5.1) |
| 持久化一律 best-effort,阻塞 500 由端點升級 | 非 HTTP 觸發的 agent run 拿不到 `HttpContext`(`PersistFailure` 恆 null) | 出現背景/排程 agent run 時 —— 但那種情境本來就沒有「回 500 給誰」 |
| session store 用 `InMemoryAgentSessionStore` | 重啟即失憶、多實例不共享(**與現行 `InMemoryChatMemoryStore` 完全相同**,不是回歸) | 要跨重啟/水平擴充 → `WithSessionStore(sp => new RedisAgentSessionStore(...))`,一行換掉 |
| store key 是 `{tenant}::{tenant}:{user}:{cid}`(租戶碼出現兩次) | 多幾個 byte | 永遠不用改;為了讓 isolation 與 conversation 兩維各自獨立可讀,重複比合併安全 |
| `ILlmAgent` 保留給路由/摘要的裸呼叫 | 兩套 LLM 呼叫抽象並存 | `SummaryInstruction` 的字串相等斷言解禁後才值得合併 |
| `IChatIdentityAccessor` 單一實作的介面 | 形式上違反「不為單一實作建介面」 | 不升級 —— 這是 `Platform.Service` 不能引用 ASP.NET Core 逼出來的,不是投機抽象 |
| `minimumPreservedTurns = 1` | 極端情況下視窗可能只剩 1 個 turn | 若量到上下文被裁太狠,調高但**必須同時確認仍會觸發**(設 20 時 26 則完全不觸發) |
| 副駕與 ChatView 對話混在同一份 `/api/chat/history` | 使用者可能分不清來源 | 02-spec §4.2 已定退路:持久化時加來源標記,**不分兩張表** |
| 不解除 `SingleRequiredStringKey` 單參天花板 | 多參/非字串 skill 仍靜默跳過 | 獨立議題,與 `CHAT_MODEL` 升級強綁(01-plan §6) |

> `[兩條鏈路共用一顆 agent:session store 管記憶、isolation key 管租戶、ChatContextProvider 管護欄+mem0 recall、ChatTurnRecorder 管 remember+持久化、SkillRoutingAgent 管路由] → 跳過:client-tools 與 skill 共存、持久化 session store、多參 skill、來源標記,當 [量到混合指令需求 / 要跨重啟 / 使用者反映歷史混淆] 再加。`
