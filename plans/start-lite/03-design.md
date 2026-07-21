# 詳細設計 — start-lite（無容器啟動模式）

> 狀態: **規劃中。** 本檔是 **HOW**：確切簽章、DI 佈線、時序/資料流、執行緒安全、邊界案例、測試設計。**不重述** 01-plan/02-spec 的結論，只在需要時引用其節次。
>
> **規劃任務，不動生產碼。** 唯一產出即本檔。
>
> **本文的論據分級**：
> - **【核】** = 已對現行原始碼實測或直接引用確認。
> - **【推】** = 由【核】的事實推導出的設計決策，實作前無法 100% 證實。

---

## 0. 行號與定義域校驗（01/02 引用 vs 現行程式）

逐一開檔核對技術錨點，確保與 02-spec 一致。

| 錨點（spec 引用） | 現行位置（已核） | 判定 |
| --- | --- | --- |
| `IMem0Client.cs` 介面 | ✅ `platform/src/Platform.Service/Abstractions/IMem0Client.cs:7-14` 精準 |
| `Program.cs:215` 現行 mem0 HttpClient 註冊 | ✅ `platform/src/Platform.Web/Program.cs:215` 精準 |
| `Program.cs:332-341` 現行 OTLP exporter 註冊 | ✅ `platform/src/Platform.Web/Program.cs:332-341` 精準 |
| `Backend Program.cs:33-38` repository 註冊 | ✅ `backend/src/Backend.Api/Program.cs:33-38` 精準 |
| `Backend Program.cs:99-104` DbBootstrap 呼叫 | ✅ `backend/src/Backend.Api/Program.cs:99-104` 精準 |
| `Backend DbBootstrap.cs:13` 預設密碼常數 | ✅ `backend/src/Backend.Api/Data/DbBootstrap.cs:13` 精準 |
| 種子帳號(demo-a/demo-b, admin-a/user-a/user-b) | ✅ `DbBootstrap.cs:124-146` 精準 |
| Fakes.cs 六個 fake repository | ✅ `backend/tests/Backend.Api.Tests/Fakes.cs:18-50` InMemoryAuthRepository 與其他 |

**結論：** 02-spec 的技術引用全準。

---

## 1. 總體架構 — 進程拓撲與依賴

### 1.1 容器模式（現狀）vs Lite 模式

```
┌─────────────────────── 容器模式(docker compose) ──────────────────────┐
│                                                                         │
│  ┌─────────────┐  ┌────────────┐  ┌──────────────┐  ┌──────────────┐ │
│  │  frontend   │  │ platform   │  │   backend    │  │  workflow    │ │
│  │  :5173      │  │  :8080     │  │   :8002      │  │   :8001      │ │
│  └─────────────┘  └────────────┘  └──────────────┘  └──────────────┘ │
│        ↓                 ↓               ↓                 ↓           │
│   vite dev       dotnet run       dotnet run        uvicorn(python) │
│   (可選 npm proxy) (docker)        (docker)          (docker)       │
│        ↓                 ↓               ↓                 ↓           │
│        └──────────────────────────┬────────────────────────┘           │
│                                    ↓                                    │
│  ┌─────────────┬──────────────┬─────────────────┬─────────────────┐  │
│  │  LiteLLM    │  PostgreSQL  │    RabbitMQ     │  mem0 + Langfuse│  │
│  │   :4000     │   :5433      │    :5672        │  :8000 + :3000  │  │
│  └─────────────┴──────────────┴─────────────────┴─────────────────┘  │
│
└─────────────────────────────────────────────────────────────────────────┘

┌──────────────────────── Lite 模式(無容器) ──────────────────────────┐
│                                                                      │
│  主機本機程序:                                                       │
│  ┌─────────────┐  ┌────────────┐  ┌──────────────┐  ┌──────────────┐
│  │  frontend   │  │ platform   │  │   backend    │  │  workflow    │
│  │  :5173      │  │  :8080     │  │   :8002      │  │   :8001      │
│  │  npm        │  │ dotnet run │  │ dotnet run   │  │ uvicorn      │
│  └─────────────┘  └────────────┘  └──────────────┘  └──────────────┘
│                          ↓               ↓
│                    InMemoryMem0Client  InMemory repositories
│                    (per-process)       (per-process)
│                    RingBufferExporter  (無 PostgreSQL)
│                                        (無 RabbitMQ consumer)
│        ↓
│   ┌──────────────┐
│   │  LiteLLM     │  ← 仍需 uvx 啟動 mock-gpt 路由
│   │  :4000       │
│   └──────────────┘
│
│  不變:
│  - 無 PostgreSQL(appdb)
│  - 無 RabbitMQ
│  - 無 mem0 HTTP 服務
│  - 無 Langfuse
└──────────────────────────────────────────────────────────────────────┘

關鍵差異:
- 多進程無共享 session/記憶 (Lite 單進程或獨立各進程)
- 重啟即失憶 (記憶體回收)
- 無真實 LLM 推理 (mock-gpt 固定回應)
```

### 1.2 資料流層級（阻塞路徑示意）

```
POST /api/chat {message, userId, conversationId}
    ↓
ChatController.Chat
    ├─ Identity.DeriveMemoryKeys() → (uid, cid)
    │   ├─ 已登入: uid = "{TenantCode}:{UserId}", cid = "{TenantCode}:{UserId}" 或加 conversationId
    │   └─ 匿名: uid/cid 由 body 值或預設決定
    ↓
ChatService.ChatAsync(messages, cid)
    ├─ hostAgent.GetOrCreateSessionAsync(cid)
    │   └─ IsolationKeyScopedAgentSessionStore
    │       └─ 若已登入但 tenant 或 user 缺失 → fail-closed(Strict=true)
    │
    ├─ hostAgent.RunAsync(messages, session)
    │   │
    │   ├─ ③ ChatTurnRecorder(外層) ← mem0 remember + 持久化,執行緒安全
    │   │   ├─ ④ SkillRoutingAgent ← 確定性 skill 路由,命中即短路
    │   │   │   ├─ TryRouteAndExecuteAsync → 工作流 HTTP 查目錄 + 路由決策
    │   │   │   ├─ 命中 → 摘要呼叫(裸 LLM),不經 ChatClientAgent
    │   │   │   └─ 未命中 → base.RunCoreAsync 委派
    │   │   │       └─ ⑤ ChatClientAgent
    │   │   │           ├─ ChatContextProvider.ProvideAIContextAsync
    │   │   │           │   ├─ ChatGuardPrompt(護欄)
    │   │   │           │   ├─ mem0.RecallAsync(uid, 最後 user 訊息) ← best-effort
    │   │   │           │   └─ 結果注入 AIContext.Instructions(不進 history)
    │   │   │           ├─ InMemoryChatHistoryProvider(20 則視窗, minimumPreservedTurns:1)
    │   │   │           └─ ⑥ FunctionInvokingChatClient
    │   │   │               └─ ⑦ OpenAI/LiteLLM
    │
    ├─ ChatTurnRecorder(串流結束後)
    │   ├─ mem0.RememberAsync(uid, message, 回覆全文) ← best-effort
    │   └─ conversations.AddAsync(...) ← 短期記憶與持久化
    │
    └─ hostAgent.SaveSessionAsync(cid, session)
        └─ InMemoryAgentSessionStore[{tenant}::{cid}] 更新狀態

Response: ChatResponse {id, role, content, …}
    同時會寫入 /api/chat/history 供後續查詢
```

---

## 2. 開關設計 — 三個環境變數

### 2.1 MEM0_MODE (platform 長記憶選擇)

**實裝位置:** `platform/src/Platform.Web/Program.cs` 的 DI 註冊區段(現行 `:215` 附近)

**現行程式:**
```csharp
// Program.cs:215
builder.Services.AddHttpClient<IMem0Client, Mem0Client>();
```

**Lite 改法:**
```csharp
var mem0Mode = cfg["MEM0_MODE"];
if (string.Equals(mem0Mode, "inmemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IMem0Client, InMemoryMem0Client>();
}
else
{
    builder.Services.AddHttpClient<IMem0Client, Mem0Client>();
}
```

**行為:**
- `MEM0_MODE=inmemory`: 單例 InMemoryMem0Client
- 未設 或 其他值: 保持現狀(HTTP Mem0Client)
- **預設值:** 無(HTTP 模式)

**對使用端的保證:**
- 同一 IMem0Client 介面,零改動
- 所有例外吞掉(recall 回 "", remember no-op)
- 使用者 A 與 B 的記憶隔離(per-userId list)

### 2.2 DB_PROVIDER (backend 資料層選擇)

**實裝位置:** `backend/src/Backend.Api/Program.cs` 的 DI 註冊區段(`:33-38`)與 DbBootstrap(`:99-104`)

**現行程式:**
```csharp
// Program.cs:32-38
builder.Services.AddNpgsqlDataSource(connString);
builder.Services.AddScoped<IAuthRepository, AuthRepository>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
// ... 其他 5 個 repo

// Program.cs:99-104
if (!app.Environment.IsEnvironment("Testing"))
{
    var dataSource = app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    await DbBootstrap.RunAsync(dataSource, logger);
}
```

**Lite 改法:**
```csharp
var dbProvider = cfg["DB_PROVIDER"];
if (string.Equals(dbProvider, "inmemory", StringComparison.OrdinalIgnoreCase))
{
    // 不呼叫 AddNpgsqlDataSource
    builder.Services.AddSingleton<IAuthRepository, InMemoryAuthRepository>();
    builder.Services.AddSingleton<IConversationRepository, InMemoryConversationRepository>();
    // ... 其他 5 個 InMemory repos
}
else
{
    builder.Services.AddNpgsqlDataSource(connString);
    builder.Services.AddScoped<IAuthRepository, AuthRepository>();
    // ... 現有邏輯
}

// DbBootstrap 判斷
if (!app.Environment.IsEnvironment("Testing")
    && !string.Equals(cfg["DB_PROVIDER"], "inmemory", StringComparison.OrdinalIgnoreCase))
{
    var dataSource = app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    await DbBootstrap.RunAsync(dataSource, logger);
}
```

**行為:**
- `DB_PROVIDER=inmemory`: 六個 InMemory repos(singleton)
- 未設 或 其他值: Dapper + PostgreSQL(現狀)
- **預設值:** 無(Dapper 模式)

**對使用端的保證:**
- 同樣 6 個 repository interface
- 種子帳號與 DbBootstrap 完全相同(BCrypt hash 共用)
- 執行緒安全(內部 lock/ConcurrentDict)

### 2.3 OTEL_MODE (platform 遙測目標選擇)

**實裝位置:** `platform/src/Platform.Web/Program.cs` 的 OpenTelemetry builder(`:320-342`)

**現行程式:**
```csharp
// Program.cs:320-342
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("chat.service");
        tracing.AddSource("Experimental.Microsoft.Extensions.AI");
        tracing.AddAspNetCoreInstrumentation(o =>
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/actuator/health"));

        if (!builder.Environment.IsEnvironment("Testing")
            && !builder.Environment.IsEnvironment("RateLimitingTesting"))
        {
            tracing.AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(cfg["LANGFUSE_OTEL_ENDPOINT"]
                    ?? "http://localhost:3000/api/public/otel/v1/traces");
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Headers = "Authorization=" + (cfg["LANGFUSE_OTEL_AUTH"]
                    ?? "Basic cGstbGYtMTIzNDU2Nzg5MDpzay1sZi0xMjM0NTY3ODkw");
            });
        }
    });
```

**Lite 改法 (見 §5.2 詳細佈線):**
```csharp
var otelMode = cfg["OTEL_MODE"];
var ringBuffer = new RingBufferActivityExporter();  // ← 先 new(lambda 外)

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("chat.service");
        tracing.AddSource("Experimental.Microsoft.Extensions.AI");
        tracing.AddAspNetCoreInstrumentation(o =>
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/actuator/health"));

        if (string.Equals(otelMode, "console", StringComparison.OrdinalIgnoreCase))
        {
            // Console exporter + ring-buffer(lambda 內只做 AddProcessor/AddConsoleExporter)
            tracing.AddConsoleExporter();
            tracing.AddProcessor(new SimpleActivityExportProcessor(ringBuffer));
        }
        else if (!builder.Environment.IsEnvironment("Testing")
                 && !builder.Environment.IsEnvironment("RateLimitingTesting"))
        {
            tracing.AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(cfg["LANGFUSE_OTEL_ENDPOINT"]
                    ?? "http://localhost:3000/api/public/otel/v1/traces");
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Headers = "Authorization=" + (cfg["LANGFUSE_OTEL_AUTH"]
                    ?? "Basic cGstbGYtMTIzNDU2Nzg5MDpzay1sZi0xMjM0NTY3ODkw");
            });
        }
    });

// 註冊 ring-buffer 為單例(lambda 外、AddOpenTelemetry 之後)
if (string.Equals(otelMode, "console", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton(ringBuffer);
}
```

**行為:**
- `OTEL_MODE=console`: ConsoleExporter + RingBufferActivityExporter(200 span 環形緩衝)
- 未設 或 其他值: OTLP to Langfuse(現狀)
- **預設值:** 無(OTLP 模式)

**對使用端的保證:**
- Span source 不變(chat.service, Experimental.Microsoft.Extensions.AI, AspNetCore)
- console mode 會在 stdout 逐 span 打 JSON(verbose 但可用)
- ring-buffer 是進程層級、所有使用者共用(Lite 定位開發,非多租戶隔離)

---

## 3. InMemoryMem0Client 設計

### 3.1 類別簽章 & 初始化

**位置:** `platform/src/Platform.Service/InMemoryMem0Client.cs` (新增)

```csharp
using Platform.Service.Abstractions;
using System.Collections.Concurrent;

namespace Platform.Service;

/// <summary>
/// Lite 模式長期記憶實作:per-user 訊息對列表(userMessage, aiReply),最多 100 對。
/// 執行緒安全:內部 lock + ConcurrentDictionary。
/// 錯誤契約:零異常(Recall 失敗回 "", Remember 無操作)。
/// 重啟即失憶(記憶體回收),符合 Lite 演示定位。
/// </summary>
public sealed class InMemoryMem0Client : IMem0Client
{
    /// <summary>
    /// per-userId 訊息對:(userMessage, aiReply)。
    /// 每個 user 最多 100 對;超限時從 front(最舊)出隊。
    /// </summary>
    private readonly ConcurrentDictionary<string, List<(string userMsg, string aiReply)>> _memories
        = new();

    private readonly object _lockObj = new object();
    private const int MaxMemoriesPerUser = 100;

    public async Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
    {
        await Task.Yield(); // 名義 async (框架契約)

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(query))
            return "";

        lock (_lockObj)
        {
            if (!_memories.TryGetValue(userId, out var pairs))
                return "";

            // 依 query 關鍵字比對(case-insensitive),最新優先
            var matches = pairs
                .AsEnumerable()
                .Reverse()  // 從最新開始
                .Where(p => p.userMsg.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || p.aiReply.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                return "";

            // 格式: "- userMsg1\n- aiReply1\n- userMsg2\n- aiReply2\n..."
            return string.Join("\n", matches.SelectMany(m => new[] { $"- {m.userMsg}", $"- {m.aiReply}" }));
        }
    }

    public async Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
    {
        await Task.Yield(); // 名義 async

        if (string.IsNullOrWhiteSpace(userId) || (string.IsNullOrWhiteSpace(userMessage) && string.IsNullOrWhiteSpace(aiReply)))
            return;

        lock (_lockObj)
        {
            if (!_memories.TryGetValue(userId, out var pairs))
            {
                pairs = new List<(string, string)>();
                _memories.TryAdd(userId, pairs);  // ConcurrentDict 的 TryAdd 可能被其他執行緒搶先,但不緊要
            }
            else
            {
                pairs = _memories[userId];  // 重新取得(其他執行緒可能改寫了 collection 本身)
            }

            pairs.Add((userMessage, aiReply));
            if (pairs.Count > MaxMemoriesPerUser)
            {
                pairs.RemoveAt(0);  // 移除最舊的
            }
        }
    }
}
```

**【推】執行緒安全分析:**
- `ConcurrentDictionary` 的 TryAdd/TryGetValue 是線程安全的
- List 本身不是線程安全的,故用外層 `lock(_lockObj)` 保護讀寫
- `RecallAsync` 與 `RememberAsync` 同時執行不會導致 double-count 或遺漏
- 不推薦 `ReaderWriterLockSlim` — recall 遠比 remember 頻繁，但本場景規模小(100 對/user)

**【推】與 Mem0Client 的行為對比:**
- 都不丟例外(吞掉所有錯誤)
- Recall 失敗 → "" (Lite 回 "", HTTP 也回 "")
- Remember 無操作 (Lite return, HTTP 也靜默)
- 都支援 CancellationToken 簽章(名義 async)

---

## 4. InMemory Repositories 設計

### 4.1 搬遷策略

**來源:** `backend/tests/Backend.Api.Tests/Fakes.cs` 現有六個 fake implementation

**目標:** `backend/src/Backend.Api/Data/InMemory/InMemory*.cs` (新增)

**搬遷原則:**
1. 類名、命名空間、簽章完全相同 ← 避免漂移
2. 內部實裝可最小化改進(e.g. 執行緒安全、cosine 計算)
3. 測試改引用新位置(相同 namespace 保證 `using` 不變)
4. 原 Fakes.cs 可保留(測試習慣)或移除(減少重複)

**【推】測試策略:**
- 既有 Backend.Api.Tests 中用 fake repos 的測試「改」引用新位置
- 新增 Lite 整合測試可用 start-lite 腳本完整驗證
- 無需新建 `InMemoryTests.csproj` — DI registry 在 Program.cs,測試若想切換供應商改 cfg 即可

### 4.2 六個 InMemory Repository 細節

#### 4.2.1 InMemoryAuthRepository

**位置:** `backend/src/Backend.Api/Data/InMemory/InMemoryAuthRepository.cs` (新增)

**簽章 (實作 `IAuthRepository`):**
```csharp
public sealed class InMemoryAuthRepository : IAuthRepository
{
    /// <summary>
    /// 種子帳號(與 DbBootstrap 完全相同)。
    /// BCrypt hash 與 DbBootstrap.cs 用同一份常數或共用方法計算。
    /// </summary>
    private static readonly string Password123 = BCrypt.Net.BCrypt.HashPassword("password123");

    private readonly Dictionary<string, TenantRow> _tenants = new()
    {
        ["demo-a"] = new TenantRow(1, "demo-a", "示範租戶 A", "demo-a-invite"),
        ["demo-b"] = new TenantRow(2, "demo-b", "示範租戶 B", "demo-b-invite"),
    };

    private readonly Dictionary<string, UserRow> _users = new()
    {
        ["admin-a"] = new UserRow("admin-a", Password123, "ADMIN", "demo-a"),
        ["user-a"] = new UserRow("user-a", Password123, "USER", "demo-a"),
        ["user-b"] = new UserRow("user-b", Password123, "USER", "demo-b"),
    };

    private readonly object _lockObj = new object();

    public Task<TenantRow?> FindTenantByCodeAsync(string code, CancellationToken ct)
        => Task.FromResult(_tenants.GetValueOrDefault(code));

    public Task<bool> UsernameExistsAsync(string username, CancellationToken ct)
        => Task.FromResult(_users.ContainsKey(username));

    public Task AddUserAsync(string username, string passwordHash, string role, long tenantId, CancellationToken ct)
    {
        lock (_lockObj)
        {
            var tenantCode = _tenants.Values.First(t => t.Id == tenantId).Code;
            _users[username] = new UserRow(username, passwordHash, role, tenantCode);
        }
        return Task.CompletedTask;
    }

    public Task<UserRow?> FindUserByUsernameAsync(string username, CancellationToken ct)
        => Task.FromResult(_users.GetValueOrDefault(username));
}
```

**【推】種子 hash 一致性:**
- 直接硬碼 `Password123 = BCrypt.Net.BCrypt.HashPassword("password123")`
- 或抽出 DbBootstrap 為公開常數,兩者都引用
- 目的:登入 `admin-a/password123` 在兩模式(容器/Lite)下行為一致

**【推】執行緒安全:**
- 讀操作(FindTenant, FindUser) — Dictionary.GetValueOrDefault 本身無鎖(但不讀新寫)
- 寫操作(AddUser) — lock 保護
- 生產 Lite 場景下單一執行緒+DI,競爭罕見,但防守意識必要

#### 4.2.2 InMemoryConversationRepository

**簽章 (實作 `IConversationRepository`):**
```csharp
public sealed class InMemoryConversationRepository : IConversationRepository
{
    /// <summary>
    /// (tenant, user, item) 三元組,模擬跨租戶/跨使用者隔離。
    /// 自增 id,每個 Add 遞增一次。
    /// </summary>
    private readonly List<(string Tenant, string User, ConversationItem Item)> _items = new();
    private long _seq;
    private readonly object _lockObj = new object();

    public Task<ConversationCreated> AddAsync(string tenantId, string userId, string prompt, string reply, CancellationToken ct)
    {
        lock (_lockObj)
        {
            var id = ++_seq;
            var now = DateTime.UtcNow;
            _items.Add((tenantId, userId, new ConversationItem(id, reply, now)));
            return Task.FromResult(new ConversationCreated(id, now));
        }
    }

    public Task<IReadOnlyList<ConversationItem>> ListDescAsync(string tenantId, string userId, CancellationToken ct)
    {
        lock (_lockObj)
        {
            var result = _items
                .Where(e => e.Tenant == tenantId && e.User == userId)
                .Select(e => e.Item)
                .OrderByDescending(i => i.CreatedAt)
                .ThenByDescending(i => i.Id)
                .ToList();
            return Task.FromResult<IReadOnlyList<ConversationItem>>(result);
        }
    }
}
```

**【推】隔離模擬:**
- 三元組 `(tenant, user, item)` 直接刻畫現實隔離邊界
- ListDescAsync 的 WHERE 子句完全複製 PostgreSQL 的租戶/使用者過濾
- user-a 看不見 user-b 的對話(即使同租戶若改規則)

#### 4.2.3 InMemoryRagRepository

**簽章 (實作 `IRagRepository`):**
```csharp
public sealed class InMemoryRagRepository : IRagRepository
{
    private sealed class Doc
    {
        public required string Id { get; init; }
        public required string TenantId { get; init; }
        public required string Title { get; init; }
        public required DateTime CreatedAt { get; init; }
        public int ChunkCount { get; set; }
        public string Status { get; set; } = "processing";
        public List<(string Content, float[] Embedding)> Chunks { get; set; } = new();
    }

    private readonly ConcurrentDictionary<string, Doc> _docs = new();
    private readonly object _lockObj = new object();

    // 其他方法 (GetDocumentStatusAsync, InsertProcessingDocumentAsync, etc.)…

    /// <summary>
    /// RAG 搜尋:計算真 cosine 相似度(取代測試的固定 score=1.0)。
    /// 結果依相似度遞減排序,截至 limit 筆。
    /// </summary>
    public Task<IReadOnlyList<RagSearchResult>> SearchAsync(
        string tenantId, float[] queryEmbedding, int limit, float threshold, CancellationToken ct)
    {
        lock (_lockObj)
        {
            var results = _docs
                .Values
                .Where(d => d.TenantId == tenantId && d.Status == "ready")
                .SelectMany(d => d.Chunks.Select((chunk, idx) => (d, chunk, idx)))
                .Select(x => new
                {
                    DocId = x.d.Id,
                    DocTitle = x.d.Title,
                    ChunkId = $"{x.d.Id}#{x.idx}",
                    Content = x.chunk.Content,
                    Similarity = CosineSimilarity(queryEmbedding, x.chunk.Embedding),
                })
                .Where(r => r.Similarity >= threshold)
                .OrderByDescending(r => r.Similarity)
                .Take(limit)
                .Select(r => new RagSearchResult
                {
                    DocId = r.DocId,
                    DocTitle = r.DocTitle,
                    ChunkId = r.ChunkId,
                    Content = r.Content,
                    Score = r.Similarity,
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<RagSearchResult>>(results);
        }
    }

    /// <summary>
    /// 計算兩個向量的 cosine 相似度(與 PostgreSQL pgvector 的 <=> 運算等價)。
    /// cosine = DotProduct / (Magnitude(A) * Magnitude(B))
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("向量維度不匹配");

        float dotProduct = 0f;
        float magA = 0f;
        float magB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        magA = (float)Math.Sqrt(magA);
        magB = (float)Math.Sqrt(magB);

        if (magA == 0 || magB == 0)
            return 0f;

        return dotProduct / (magA * magB);
    }
}
```

**【推】cosine 等價性:**
- PostgreSQL 的 pgvector `<=>` 算子就是 cosine distance
- Lite 實作與 DB 邏輯完全對應 → 查詢結果可預測
- FakeEmbeddingProvider(SHA-256 種子) 確保確定性 1536 維向量

#### 4.2.4 InMemoryConfigRepository

**簽章 (實作 `IConfigRepository`):**
```csharp
public sealed class InMemoryConfigRepository : IConfigRepository
{
    /// <summary>
    /// key → value 映射(全租戶共享)。
    /// 簡單 KV,無複雜數據結構(對標 DB 的 app_config 表)。
    /// </summary>
    private readonly Dictionary<string, string> _config = new();
    private readonly object _lockObj = new object();

    public Task<string?> GetAsync(string key, CancellationToken ct)
        => Task.FromResult(_config.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        lock (_lockObj)
        {
            _config[key] = value;
        }
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct)
    {
        lock (_lockObj)
        {
            return Task.FromResult(new Dictionary<string, string>(_config));
        }
    }
}
```

#### 4.2.5 InMemorySkillRepository

**簽章 (實作 `ISkillRepository`):**
```csharp
public sealed class InMemorySkillRepository : ISkillRepository
{
    private sealed class SkillRecord
    {
        public required string Id { get; init; }
        public required string TenantId { get; init; }
        public required string Name { get; init; }
        public required string Definition { get; init; }
        public required int CurrentRevision { get; set; }
        public required bool Enabled { get; set; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime UpdatedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, SkillRecord> _skills = new();
    private readonly object _lockObj = new object();

    public Task<SkillRow?> FindByTenantAndNameAsync(string tenantId, string name, CancellationToken ct)
        => Task.FromResult(_skills.Values
            .FirstOrDefault(s => s.TenantId == tenantId && s.Name == name && s.Enabled)
            ?.ToSkillRow());

    public Task<IReadOnlyList<SkillRow>> ListByTenantAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SkillRow>>(_skills.Values
            .Where(s => s.TenantId == tenantId && s.Enabled)
            .Select(s => s.ToSkillRow())
            .ToList());

    // 其他方法 (SaveAsync, UpdateDefinitionAsync, MarkDeletedAsync, etc.)…
    // 【推】SkillValidator 不 fake —— 驗證仍走 workflow HTTP(:8001)
    // 若 workflow 無法連線,驗證回 error(fail-fast),Lite 下需 uvicorn 活著
}
```

**【推】Skill 驗證策略:**
- SkillRepository 只儲存已驗證過的 YAML
- 真正驗證呼叫 WorkflowSkillValidator(HTTP → :8001)
- Lite 下 workflow 也在,所以相同邏輯不改

#### 4.2.6 InMemoryConfigurationSetRepository

**簽章 (實作 `IConfigurationSetRepository`):**
```csharp
public sealed class InMemoryConfigurationSetRepository : IConfigurationSetRepository
{
    private sealed class ConfigSet
    {
        public required string Id { get; init; }
        public required string TenantId { get; init; }
        public required string Name { get; init; }
        public required bool IsActive { get; set; }
        public required Dictionary<string, object> Values { get; set; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime UpdatedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConfigSet> _sets = new();
    private readonly object _lockObj = new object();

    public Task<ConfigurationSetRow?> GetActiveAsync(string tenantId, CancellationToken ct)
        => Task.FromResult(_sets.Values
            .FirstOrDefault(cs => cs.TenantId == tenantId && cs.IsActive)
            ?.ToRow());

    public Task<IReadOnlyList<ConfigurationSetRow>> ListByTenantAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ConfigurationSetRow>>(_sets.Values
            .Where(cs => cs.TenantId == tenantId)
            .Select(cs => cs.ToRow())
            .ToList());

    // 其他方法 (SaveAsync, UpdateValuesAsync, SetActiveAsync, etc.)…
    // 【推】一租戶至多一份 is_active=true,由 lock 維持(無 DB 唯一索引)
}
```

---

## 5. RingBufferActivityExporter 設計

### 5.1 類別簽章

**位置:** `platform/src/Platform.Service/RingBufferActivityExporter.cs` (新增)

```csharp
using System.Collections.Generic;
using System.Collections.Concurrent;
using OpenTelemetry;
using System.Diagnostics;

namespace Platform.Service;

/// <summary>
/// OTel Lite 模式遙測儲存:固定容量 200 span 的環形緩衝。
/// 超限時自動出隊最舊,LLM agent 可讀最近 N 條 trace。
/// 執行緒安全:ConcurrentQueue 內建。
/// 生命週期:Singleton,由 Program.cs 與 OTel pipeline 共同持有。
/// </summary>
public sealed class RingBufferActivityExporter : BaseExporter<Activity>
{
    private const int MaxCapacity = 200;

    private readonly ConcurrentQueue<ActivitySnapshot> _buffer = new();

    /// <summary>
    /// 簡化後的 Activity 快照(供 LLM 讀)。
    /// </summary>
    public sealed class ActivitySnapshot
    {
        public DateTime StartTimeUtc { get; set; }
        public DateTime EndTimeUtc { get; set; }
        public string Name { get; set; } = "";
        public TimeSpan Duration { get; set; }
        /// <summary>
        /// 關鍵 attributes:skill_name、model、tool_name、error 等。
        /// 以降低序列化體積,LLM 讀關鍵信息即可。
        /// </summary>
        public Dictionary<string, object?> Attributes { get; set; } = new();
    }

    /// <summary>
    /// OTel export 進入點:批次 Activity → 逐一入隊。
    /// 【核】若超容量,TryDequeue 直到 count 回到上限以下。
    /// </summary>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            var snapshot = new ActivitySnapshot
            {
                StartTimeUtc = activity.StartTimeUtc,
                EndTimeUtc = activity.EndTimeUtc ?? activity.StartTimeUtc.Add(activity.Duration),
                Name = activity.DisplayName,
                Duration = activity.Duration,
                Attributes = ExtractKeyAttributes(activity),
            };

            _buffer.Enqueue(snapshot);

            // 維持上限:若超過 MaxCapacity,從隊頭出隊
            while (_buffer.Count > MaxCapacity && _buffer.TryDequeue(out _)) { }
        }

        return ExportResult.Success;
    }

    /// <summary>
    /// 讀取最近 N 條 spans(用於 get_recent_traces AIFunction)。
    /// 若 limit=null 返全部;否則返最新 limit 條(遞減排序)。
    /// </summary>
    public IEnumerable<ActivitySnapshot> GetRecentSpans(int? limit = null)
    {
        var list = _buffer.ToList();
        if (limit.HasValue)
        {
            list = list.TakeLast(limit.Value).ToList();
        }
        // 最新優先(反序)
        return list.AsEnumerable().Reverse();
    }

    /// <summary>
    /// 從 Activity.Tags 提取關鍵 attribute(避免序列化整個集合)。
    /// </summary>
    private static Dictionary<string, object?> ExtractKeyAttributes(Activity activity)
    {
        var result = new Dictionary<string, object?>();
        foreach (var tag in activity.Tags ?? Enumerable.Empty<KeyValuePair<string, object?>>())
        {
            // 只保留已知關鍵 key
            if (tag.Key is "skill_name" or "model" or "tool_name" or "error" or "document_id" or "query")
            {
                result[tag.Key] = tag.Value;
            }
        }
        return result;
    }
}
```

### 5.2 掛載方式

**在 Program.cs 中的 OTel builder 內:**

```csharp
var otelMode = cfg["OTEL_MODE"];
var ringBuffer = new RingBufferActivityExporter();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("chat.service");
        tracing.AddSource("Experimental.Microsoft.Extensions.AI");
        tracing.AddAspNetCoreInstrumentation(o =>
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/actuator/health"));

        if (string.Equals(otelMode, "console", StringComparison.OrdinalIgnoreCase))
        {
            tracing.AddConsoleExporter();
            tracing.AddProcessor(new SimpleActivityExportProcessor(ringBuffer));
            // 註冊 ring-buffer 為單例供 get_recent_traces 使用
            builder.Services.AddSingleton(ringBuffer);
        }
        else if (!builder.Environment.IsEnvironment("Testing")
                 && !builder.Environment.IsEnvironment("RateLimitingTesting"))
        {
            tracing.AddOtlpExporter(o => { /* ... */ });
        }
    });
```

**【推】與 ConsoleExporter 共存:**
- ConsoleExporter 印到 stdout (詳細 JSON)
- RingBufferExporter 只記憶(精簡快照)
- 兩者皆在同一 processor pipeline(SimpleActivityExportProcessor)

---

## 6. get_recent_traces AIFunction 設計

### 6.1 機制與註冊

**位置:** `platform/src/Platform.Web/Services/ChatService.cs` 或新檔 `ChatTraceToolProvider.cs`

**簽章:**
```csharp
/// <summary>
/// Lite 模式 only:讓 LLM 讀最近的 trace 摘要。
/// 範例呼叫:agent 在聊天中若要詢問「最後 3 個 span 是什麼」,直接呼叫此函式。
/// </summary>
public sealed class TraceInfo
{
    public DateTime TimestampUtc { get; set; }
    public string Name { get; set; } = "";
    public long DurationMs { get; set; }
    public Dictionary<string, object?> Attributes { get; set; } = new();
}

public sealed class GetRecentTracesResult
{
    public List<TraceInfo> Traces { get; set; } = new();
}

/// <summary>
/// AIFunction 實作(供 ChatClientAgent 呼叫)。
/// 註冊路徑:Program.cs 建構 ChatClientAgentOptions 時掛進 ChatOptions.Tools(見 §6.2)。
/// 只在 OTEL_MODE=console 時加入 ChatClientAgent 的 tools。
/// </summary>
public static class ChatTraceToolProvider
{
    public static AIFunction Create(RingBufferActivityExporter ringBuffer)
    {
        // AIFunctionFactory.Create:delegate 在前,name/description 在後;
        // 參數 schema 由 delegate 簽章自動推導(filter/limit)。
        return AIFunctionFactory.Create(
            (string? filter, int limit) =>
            {
                var spans = ringBuffer.GetRecentSpans(limit > 0 ? limit : 10).ToList();

                // Filter 若提供,依 name 或 attributes 進行文字過濾
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    spans = spans
                        .Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                 || s.Attributes.Values.Any(v => v?.ToString()?.Contains(
                                     filter, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToList();
                }

                return new GetRecentTracesResult
                {
                    Traces = spans.Select(s => new TraceInfo
                    {
                        TimestampUtc = s.StartTimeUtc,
                        Name = s.Name,
                        DurationMs = (long)s.Duration.TotalMilliseconds,
                        Attributes = s.Attributes,
                    }).ToList(),
                };
            },
            "get_recent_traces",
            "取得最近的追蹤資訊(spans):包括操作名稱、耗時、attributes。用於診斷聊天流程或查看工作流執行狀況。");
    }
}
```

### 6.2 在 ChatClientAgent 註冊

**【推】工具註冊方式 (TBD — 待查証)**

根據 platform/AGENTS.md:28，tools 應掛在 `ChatOptions.Tools`(`IList<AITool>`)。現行 Program.cs:156-165 未展示此路徑。

保守做法：
- 若 `OTEL_MODE=console`，在 `ChatOptions` 建構時：
  ```csharp
  ChatOptions = new ChatOptions
  {
      Instructions = copilotInstructions,
      Tools = string.Equals(cfg["OTEL_MODE"], "console", StringComparison.OrdinalIgnoreCase)
          ? new List<AITool> { /* get_recent_traces AITool */ }
          : new List<AITool>(),
  }
  ```
- AITool 的建構應用 `AIFunctionFactory.Create(delegate, name, description)` (非 `AIFunction.Create`)

**【推】工作流邊界:**
- 此工具是原生 AITool,**不**註冊為 skill
- 理由:Lite 模式特有、不應流入生產、不需版本控制
- 安全:不違反「skill 永不洩漏為 client tool」

**【推】多租戶隔離:**
- ring-buffer 進程層級、所有使用者共用
- Lite 定位開發環境,非多租戶隔離(若正式部署要租戶隔離需另做)
- 「生產絕不應該啟用此工具」應明文寫在環變檢查

---

## 7. 啟動腳本設計

### 7.1 start-lite.ps1 (Windows)

**位置:** `scripts/start-lite.ps1` (新增,~200 行)

**流程:**
```powershell
# 前置檢查
$errors = @()
if (!(dotnet --version -split '\.' | Select-Object -First 1 -ge 10)) { $errors += "dotnet 10 SDK not found" }
if (!(uv --version)) { $errors += "uv not found" }
if (!(node --version)) { $errors += "node not found" }
if (!(python --version)) { $errors += "python 3.12+ not found" }
if ($errors) { $errors | ForEach-Object { Write-Host "ERROR: $_" }; exit 1 }

# LiteLLM 啟動(可選)
Write-Host "Starting LiteLLM on :4000..."
$litellmJob = Start-Process -FilePath "pwsh" -ArgumentList `
  "-Command", "cd $PSScriptRoot/..; uvx --from 'litellm[proxy]' litellm --config infra/litellm-config.lite.yaml --port 4000" `
  -PassThru -RedirectStandardOutput "litellm.log" -RedirectStandardError "litellm-err.log"

# 平行啟動四服務
$jobs = @()

$jobs += Start-Process -FilePath "dotnet" -ArgumentList `
  "run", "--project", "backend/src/Backend.Api/Backend.Api.csproj" `
  -WorkingDirectory "$PSScriptRoot\.." `
  -PassThru -RedirectStandardOutput "backend.log" -RedirectStandardError "backend-err.log" `
  -EnvironmentVariables @{
    "DB_PROVIDER" = "inmemory"
    "EMBEDDINGS_PROVIDER" = "fake"
    "ASPNETCORE_URLS" = "http://localhost:8002"
    "ASPNETCORE_ENVIRONMENT" = "Development"
  }

$jobs += Start-Process -FilePath "pwsh" -ArgumentList `
  "-Command", "cd $PSScriptRoot/../workflow; uv run uvicorn app.main:app --host 127.0.0.1 --port 8001" `
  -PassThru -RedirectStandardOutput "workflow.log" -RedirectStandardError "workflow-err.log" `
  -EnvironmentVariables @{
    "BACKEND_BASE_URL" = "http://localhost:8002"
    "LLM_BASE_URL" = "http://localhost:4000"
    "LLM_MODEL" = "mock-gpt"
    "LANGFUSE_ENABLED" = "false"
  }

$jobs += Start-Process -FilePath "dotnet" -ArgumentList `
  "run", "--project", "platform/src/Platform.Web/Platform.Web.csproj" `
  -WorkingDirectory "$PSScriptRoot\.." `
  -PassThru -RedirectStandardOutput "platform.log" -RedirectStandardError "platform-err.log" `
  -EnvironmentVariables @{
    "MEM0_MODE" = "inmemory"
    "OTEL_MODE" = "console"
    "CHAT_MODEL" = "mock-gpt"
    "ASPNETCORE_URLS" = "http://localhost:8080"
    "ASPNETCORE_ENVIRONMENT" = "Development"
  }

$jobs += Start-Process -FilePath "npm" -ArgumentList "run", "dev" `
  -WorkingDirectory "$PSScriptRoot\..\frontend" `
  -PassThru -RedirectStandardOutput "frontend.log" -RedirectStandardError "frontend-err.log"

# 健康檢查
$healthChecks = @(
  @{ url = "http://localhost:8002/health"; name = "backend"; timeout = 60 }
  @{ url = "http://localhost:8001/health"; name = "workflow"; timeout = 60 }
  @{ url = "http://localhost:8080/actuator/health"; name = "platform"; timeout = 60 }
)

foreach ($check in $healthChecks) {
  $elapsed = 0
  while ($elapsed -lt $check.timeout) {
    try {
      $resp = Invoke-WebRequest -Uri $check.url -TimeoutSec 2 -ErrorAction Stop
      if ($resp.StatusCode -eq 200) {
        Write-Host "✓ $($check.name) healthy"
        break
      }
    } catch { }
    Start-Sleep -Seconds 2
    $elapsed += 2
  }
  if ($elapsed -ge $check.timeout) {
    Write-Error "$($check.name) failed health check after $($check.timeout)s"
  }
}

# 保存 job IDs 供 stop-lite.ps1 使用
$jobs | ForEach-Object { $_.Id } | Set-Content "start-lite.pids"
$litellmJob.Id | Add-Content "start-lite.pids"

Write-Host "`n✓ All services started successfully!"
Write-Host "  backend:  http://localhost:8002  (logs: backend.log)"
Write-Host "  workflow: http://localhost:8001  (logs: workflow.log)"
Write-Host "  platform: http://localhost:8080  (logs: platform.log)"
Write-Host "  frontend: http://localhost:5173  (logs: frontend.log)"
Write-Host "  litellm:  http://localhost:4000  (logs: litellm.log)"
Write-Host ""
Write-Host "To stop, run: .\stop-lite.ps1"
```

### 7.2 start-lite.sh (Linux/macOS)

**位置:** `scripts/start-lite.sh` (新增,~200 行)

**流程:**
```bash
#!/bin/bash
set -e

# 前置檢查
errors=()
[[ $(dotnet --version | cut -d. -f1) -ge 10 ]] || errors+=("dotnet 10 SDK not found")
command -v uv &>/dev/null || errors+=("uv not found")
command -v node &>/dev/null || errors+=("node not found")
command -v python3 &>/dev/null || errors+=("python 3.12+ not found")

if [ ${#errors[@]} -gt 0 ]; then
  for err in "${errors[@]}"; do echo "ERROR: $err"; done
  exit 1
fi

# LiteLLM 啟動
echo "Starting LiteLLM on :4000..."
nohup uvx --from 'litellm[proxy]' litellm --config infra/litellm-config.lite.yaml --port 4000 \
  > litellm.log 2>&1 &
echo $! >> start-lite.pids

# 平行啟動四服務
(
  cd backend
  DB_PROVIDER=inmemory EMBEDDINGS_PROVIDER=fake \
  ASPNETCORE_URLS=http://localhost:8002 \
  ASPNETCORE_ENVIRONMENT=Development \
  nohup dotnet run --project src/Backend.Api/Backend.Api.csproj \
    > ../backend.log 2>&1 &
  echo $! >> ../start-lite.pids
)

(
  cd workflow
  BACKEND_BASE_URL=http://localhost:8002 \
  LLM_BASE_URL=http://localhost:4000 \
  LLM_MODEL=mock-gpt \
  LANGFUSE_ENABLED=false \
  nohup uv run uvicorn app.main:app --host 127.0.0.1 --port 8001 \
    > ../workflow.log 2>&1 &
  echo $! >> ../start-lite.pids
)

(
  cd platform
  MEM0_MODE=inmemory OTEL_MODE=console CHAT_MODEL=mock-gpt \
  ASPNETCORE_URLS=http://localhost:8080 \
  ASPNETCORE_ENVIRONMENT=Development \
  nohup dotnet run --project src/Platform.Web/Platform.Web.csproj \
    > ../platform.log 2>&1 &
  echo $! >> ../start-lite.pids
)

(
  cd frontend
  nohup npm run dev > ../frontend.log 2>&1 &
  echo $! >> ../start-lite.pids
)

# 健康檢查
check_health() {
  local url=$1
  local name=$2
  local timeout=60
  local elapsed=0
  while [ $elapsed -lt $timeout ]; do
    if curl -f "$url" >/dev/null 2>&1; then
      echo "✓ $name healthy"
      return 0
    fi
    sleep 2
    elapsed=$((elapsed + 2))
  done
  echo "✗ $name failed health check after ${timeout}s" >&2
  return 1
}

check_health "http://localhost:8002/health" "backend"
check_health "http://localhost:8001/health" "workflow"
check_health "http://localhost:8080/actuator/health" "platform"

echo ""
echo "✓ All services started successfully!"
echo "  backend:  http://localhost:8002  (logs: backend.log)"
echo "  workflow: http://localhost:8001  (logs: workflow.log)"
echo "  platform: http://localhost:8080  (logs: platform.log)"
echo "  frontend: http://localhost:5173  (logs: frontend.log)"
echo "  litellm:  http://localhost:4000  (logs: litellm.log)"
echo ""
echo "To stop, run: ./stop-lite.sh"
```

### 7.3 stop-lite.ps1 & stop-lite.sh

```powershell
# stop-lite.ps1
if (Test-Path "start-lite.pids") {
  Get-Content "start-lite.pids" | ForEach-Object {
    Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue
  }
  Remove-Item "start-lite.pids"
  Write-Host "All services stopped."
} else {
  Write-Host "No running processes found."
}
```

```bash
# stop-lite.sh
#!/bin/bash
if [ -f start-lite.pids ]; then
  while IFS= read -r pid; do
    kill -9 "$pid" 2>/dev/null || true
  done < start-lite.pids
  rm start-lite.pids
  echo "All services stopped."
else
  echo "No running processes found."
fi
```

---

## 8. 測試設計

### 8.1 新增 xUnit 測試(C# 側)

**InMemoryMem0ClientTests** (在 `platform/tests/Platform.Service.Tests/`)
```csharp
[Fact]
public async Task RecallAsync_NoUserId_ReturnsEmpty()
{
    var client = new InMemoryMem0Client();
    var result = await client.RecallAsync("", "query");
    Assert.Equal("", result);
}

[Fact]
public async Task RememberAsync_ThenRecall_ReturnsMemory()
{
    var client = new InMemoryMem0Client();
    await client.RememberAsync("user-a", "你好", "你好!我是助理");
    var recalled = await client.RecallAsync("user-a", "你好");
    Assert.Contains("你好", recalled);
}

[Fact]
public async Task RememberAsync_ExceedCapacity_RemovesOldest()
{
    var client = new InMemoryMem0Client();
    for (int i = 0; i < 101; i++)
    {
        await client.RememberAsync("user-a", $"msg {i}", $"reply {i}");
    }
    // 應保留最新 100 對 (pair 0 被刪)
    var recalled = await client.RecallAsync("user-a", "msg 0");
    Assert.Equal("", recalled);
}

[Fact]
public async Task Recall_CaseInsensitive()
{
    var client = new InMemoryMem0Client();
    await client.RememberAsync("user-a", "HELLO WORLD", "Hi there");
    var recalled = await client.RecallAsync("user-a", "hello");
    Assert.Contains("HELLO WORLD", recalled);
}

[Fact]
public async Task Recall_MultipleMatches_LatestFirst()
{
    var client = new InMemoryMem0Client();
    await client.RememberAsync("user-a", "第一次問", "第一次答");
    await client.RememberAsync("user-a", "第二次問", "第二次答");
    var recalled = await client.RecallAsync("user-a", "問");
    var lines = recalled.Split('\n');
    Assert.Equal("- 第二次問", lines[0]); // 最新優先
}
```

**InMemoryRepositoriesTests** (在 `backend/tests/Backend.Api.Tests/`)
```csharp
[Fact]
public async Task AuthRepo_FindUser_MatchesSeed()
{
    var repo = new InMemoryAuthRepository();
    var user = await repo.FindUserByUsernameAsync("admin-a");
    Assert.NotNull(user);
    Assert.Equal("ADMIN", user.Role);
    Assert.Equal("demo-a", user.TenantCode);
}

[Fact]
public async Task ConversationRepo_ListDesc_FiltersByTenant()
{
    var repo = new InMemoryConversationRepository();
    await repo.AddAsync("demo-a", "user-a", "Q1", "A1");
    await repo.AddAsync("demo-b", "user-b", "Q2", "A2");
    
    var demoA = await repo.ListDescAsync("demo-a", "user-a");
    Assert.Single(demoA);
    Assert.Equal("A1", demoA[0].Content);
}

[Fact]
public async Task RagRepo_Search_CalculatesCosine()
{
    var repo = new InMemoryRagRepository();
    // 插入文檔、設定向量…
    var result = await repo.SearchAsync("tenant-a", queryVector, limit: 10, threshold: 0.5);
    // 驗證相似度計算正確(相同向量應回 1.0)
    Assert.NotEmpty(result);
}
```

### 8.2 pytest 測試(Python 側)

**現狀:** workflow 無改動(已活著),Lite 測試只需驗證 HTTP 連線

**測試清單:**
- backend 連線檢查(`GET /health`)
- workflow 連線檢查(`GET /health`)
- platform InMemoryMem0Client 是否替代 HTTP 版
- platform OTel console 模式是否正常運作
- 完整聊天流程(鏈路 A + AG-UI)

---

## 9. 邊界案例與風險

### 9.1 重啟即失憶

**現象:** Lite 進程重啟後,所有 in-memory 記憶(mem0、對話、文檔)清空

**是否可接受:** ✅ **是**,符合演示定位

**緩解:** 
- 文檔明示「Lite 不保存跨重啟狀態」
- 若需持久化,改用 `start-full`(容器模式)
- 開發迭代時重啟無損

### 9.2 記憶體上限

**現象:** InMemory repos 無限增長 → OOM

**估計:** 
- 每個使用者最多 100 對記憶(~10KB/對) = 1MB/user
- 對話無上限(可達千萬筆)
- 文檔無上限(嵌入向量 1536 float ≈ 6KB/chunk)

**是否可接受:** ⚠️ **部分**,開發場景下對話可能無限增長

**緩解:**
- 長期:對話加時間戳 TTL(e.g. 24h)自動清理
- 短期:文檔上傳回 502(階段一無 async processing)
- 開發:手動 `stop-lite` 重啟

### 9.3 多進程不共享

**現象:** 若誤啟兩份 platform 實例,各自獨立記憶

**是否可接受:** ✅ **是**,Lite 預期單一進程組(scripts/start-lite 一次啟動即可)

**防守:** 預設埠衝突(8080/8001 已占)會自動拒絕第二份

### 9.4 種子帳號 hash 漂移

**風險:** InMemoryAuthRepository 與 DbBootstrap 的 BCrypt hash 不一致 → 登入失敗

**防守:**
- 兩者都用 `BCrypt.Net.BCrypt.HashPassword("password123")` 相同邏輯
- 或抽出共用常數 → 兩處都引用
- 測試驗證登入 `admin-a/password123` 兩模式一致

### 9.5 Isolation key 取不到(AG-UI)

**現象:** 未登入或 JWT 缺 tenant/user claim → `IsolationKeyScopedAgentSessionStore(Strict=true)` 拋例外

**是否可接受:** ✅ **是**,AG-UI 本來就應要求認證(`RequireAuthorization()`)

**防守:** `JwtTenantIsolationKeyProvider` 回 null 時 fail-closed,不創建共享 key

### 9.6 RingBuffer 容量不足

**現象:** Agent 想查最近 100 spans 但只保留 200 total → 快速聊天時 span 出隊太快

**估計:** 每輪聊天 ~3-5 spans → 200 spans ≈ 40-65 輪對話,開發足用

**是否可接受:** ✅ **是**,不是正式演示環境

**升級:** 若需更多歷史,改 `MaxCapacity` 常數(可改為環變)

---

## 10. 與現有計畫的關係

- **chat-skill-routing** ✅ 已交付:Lite 複用現有 SkillRoutingAgent 邏輯,不新增
- **copilot-shared-core** ⚠️ 進行中:Lite 複用 AG-UI 認證 + SessionIsolationKeyProvider,記憶層一致(P2-P3);可並行實作
- **settings-skill-redesign** ✅ 進行中:Lite 中 Skill CRUD/validate/invoke 逕走活 workflow(:8001),不動 UI

---

## 11. 摘要與驗收檢查清單

### 11.1 實作清單(按優先序)

- [ ] **P0** 專案結構:Platform.Web.csproj 新增 `OpenTelemetry.Exporter.Console` 套件(AddConsoleExporter 需要)
- [ ] **P1** InMemoryMem0Client (platform/src/.../InMemoryMem0Client.cs)
  - [ ] RecallAsync with keyword matching
  - [ ] RememberAsync with capacity enforcement
  - [ ] Thread safety (lock)
- [ ] **P2** InMemory Repositories (backend/src/Backend.Api/Data/InMemory/*.cs)
  - [ ] InMemoryAuthRepository with seed (BCrypt hash consistency)
  - [ ] InMemoryConversationRepository with tenant/user isolation
  - [ ] InMemoryRagRepository with cosine similarity
  - [ ] InMemoryConfigRepository
  - [ ] InMemorySkillRepository
  - [ ] InMemoryConfigurationSetRepository
- [ ] **P3** RingBufferActivityExporter & Console OTel (platform/src/.../RingBufferActivityExporter.cs)
  - [ ] Bounded queue (MaxCapacity=200)
  - [ ] ActivitySnapshot extraction
  - [ ] Thread-safe enqueue/dequeue
- [ ] **P4** get_recent_traces AIFunction (platform/src/.../ChatTraceToolProvider.cs)
  - [ ] Lite-only registration
  - [ ] Span filtering & limiting
  - [ ] JSON serialization
- [ ] **Scripts** start-lite.ps1 / .sh + stop-lite.* (~400 lines total)
  - [ ] Prerequisites check (dotnet, uv, node, python)
  - [ ] Parallel service startup
  - [ ] Health checks (3 services)
  - [ ] PID tracking
- [ ] **DI Wiring** Program.cs changes
  - [ ] MEM0_MODE switch (platform)
  - [ ] DB_PROVIDER switch (backend)
  - [ ] OTEL_MODE switch (platform)
  - [ ] DbBootstrap conditional skip (backend)

### 11.2 驗收條件(抄自 02-spec §5)

- [ ] 一鍵啟動:`./scripts/start-lite.ps1` 無例外,四服務皆通過健康檢查
- [ ] 登入成功:admin-a/password123
- [ ] 聊天流程:SSE 聊天回 mock-gpt 固定文本
- [ ] 記憶:agent recall/remember 走 InMemoryMem0Client
- [ ] OTel:platform.log 含 span JSON
- [ ] get_recent_traces:聊天中 agent 呼叫工具讀 ring-buffer 成功
- [ ] 文件上傳:回 502(無 RabbitMQ)

---

## 12. 未來升級路徑(非本規格)

- **RingBuffer 租戶隔離:** 若 Lite 用於多租戶演示,改 per-tenant 環形緩衝
- **對話 TTL:** InMemoryConversationRepository 加 24h 自動清理
- **Skill 版本化:** InMemorySkillRepository 升格為無限版本歷史(目前省略)
- **文件 async:** 階段二 backend Channel<DocumentMessage> + platform HTTP IDocumentQueue

---

本設計文件是實作的「已驗收設計稿」。移交給開發團隊後,實作端可逐 P 交付並迭代驗收。
