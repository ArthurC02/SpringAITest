# 規格 — start-lite（無容器啟動模式）

> 狀態: **規劃中。** 承接 [01-plan.md](01-plan.md);本檔是 **WHAT**(定案的行為與契約),實作順序見 03-design(待起草)。

---

## 1. 可交付物清單

### 1.1 新增檔案

| 檔案路徑 | 用途 | 估行數 |
| --- | --- | --- |
| `platform/src/Platform.Service/InMemoryMem0Client.cs` | 長記憶 in-memory 實作 | ~100 |
| `platform/src/Platform.Service/RingBufferActivityExporter.cs` | OTel ring-buffer exporter | ~80 |
| `platform/src/Platform.Web/Services/ChatTraceToolProvider.cs` | get_recent_traces AIFunction 工廠(或內嵌於 ChatService) | ~60 |
| `backend/src/Backend.Api/Data/InMemory/InMemoryAuthRepository.cs` | 認證 in-memory repository | ~150 |
| `backend/src/Backend.Api/Data/InMemory/InMemoryConversationRepository.cs` | 對話 in-memory repository | ~100 |
| `backend/src/Backend.Api/Data/InMemory/InMemoryRagRepository.cs` | RAG 搜尋 in-memory repository(cosine calc) | ~120 |
| `backend/src/Backend.Api/Data/InMemory/InMemoryConfigRepository.cs` | 配置 in-memory repository | ~80 |
| `backend/src/Backend.Api/Data/InMemory/InMemorySkillRepository.cs` | Skill 元資料 in-memory repository | ~100 |
| `backend/src/Backend.Api/Data/InMemory/InMemoryConfigurationSetRepository.cs` | 配置集 in-memory repository | ~80 |
| `scripts/start-lite.ps1` | Windows 一鍵啟動腳本 | ~200 |
| `scripts/start-lite.sh` | Linux/macOS 一鍵啟動腳本 | ~200 |
| `scripts/stop-lite.ps1` | Windows 收攤腳本 | ~50 |
| `scripts/stop-lite.sh` | Linux/macOS 收攤腳本 | ~50 |

### 1.2 修改檔案

| 檔案路徑 | 修改內容 | 行數 |
| --- | --- | --- |
| `platform/src/Platform.Web/Program.cs` | (P1) 環變 MEM0_MODE 判斷註冊 singleton/HttpClient | +15 |
| | (P3) 環變 OTEL_MODE 判斷註冊 Console/OTLP exporter | +15 |
| | (P4) `OTEL_MODE=console` 時註冊 get_recent_traces | +5 |
| `platform/src/Platform.Web/Program.cs` | 新增 `OpenTelemetry.Exporter.Console` nuget ref | +1 |
| `backend/src/Backend.Api/Program.cs` | (P2) 環變 DB_PROVIDER 判斷註冊 InMemory/Dapper repos | +20 |
| | (P2) 環變 DB_PROVIDER=inmemory 時 DbBootstrap 跳過 | +5 |
| `backend/tests/Backend.Api.Tests/Fakes.cs` | 改 using 引入 `Backend.Api.Data.InMemory;` 命名空間 | 無改動(類名、簽章同) |
| `infra/litellm-config.lite.yaml` | 移除 langfuse callback,避免 lite 模式每次 LLM 呼叫噴錯誤日誌 | 新檔 |
| `platform/tests/` | 新增 Lite integration test(可選,階段二) | 待定 |

### 1.3 環境配置

**nuget 新增包:**
- platform: `OpenTelemetry.Exporter.Console` (latest compatible with 1.16.0 exporter family)

**無需 nuget 變動:**
- backend: 現有 repo interface 與 fake impl 已足

---

## 2. 環境變數精確行為

### 2.1 MEM0_MODE (platform 長記憶)

| 屬性 | 值 |
| --- | --- |
| **環變名** | `MEM0_MODE` |
| **預設值** | (無,默認 http client 模式) |
| **Lite 值** | `inmemory` |
| **生效範圍** | platform/src/Platform.Web/Program.cs DI 註冊處 |
| **精確位置** | ~Program.cs:207(現有 `AddHttpClient<IMem0Client, Mem0Client>()` 附近) |

**語意:**
- `MEM0_MODE=inmemory`: 改註冊 `services.AddSingleton<IMem0Client, InMemoryMem0Client>()`
- 其他/未設: 保持現有 `AddHttpClient<IMem0Client, Mem0Client>()`(HTTP 版)

**InMemoryMem0Client 行為規格:**
- 儲存結構: `Dictionary<string, List<(string userMessage, string aiReply)>>` (per-userId 訊息對)
- Capacity: 每個 user 最多 100 對訊息
- `RecallAsync(string userId, string query, CancellationToken ct)`:
  - 未知 userId 回空字串(`""`)
  - 已知 userId: 依 query 關鍵字比對歷史訊息對(userMessage + aiReply),**最近優先**
  - 返格式: `"- userMsg1\n- aiReply1\n- userMsg2\n- aiReply2"` (逐行列舉相關記憶)
  - 無符合項: 回空字串
- `Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct)`:
  - userMessage/aiReply 為 `null` 或空: 無操作(return immediately)
  - 有效時: 將 `(userMessage, aiReply)` 對加入,超 100 對時先移除最舊
  - 不丟例外(與 Mem0Client 同)
- 執行緒安全: 內部 lock 保護 Dictionary 與各 List

**與使用端契約:**
- ChatService 調用者零改動
- 語義不變: 錯誤全吞(不丟例外),Recall 無結果回空字串(可直接當 prompt injection 安全)

### 2.2 DB_PROVIDER (backend 資料層)

| 屬性 | 值 |
| --- | --- |
| **環變名** | `DB_PROVIDER` |
| **預設值** | (無,默認 Dapper+Npgsql 模式) |
| **Lite 值** | `inmemory` |
| **生效範圍** | backend/src/Backend.Api/Program.cs DI 註冊處 + DbBootstrap |
| **精確位置** | ~Program.cs:33-38(現有 repo 註冊)、~Program.cs:99-104(DbBootstrap) |

**語意:**
- `DB_PROVIDER=inmemory`:
  1. 註冊 6 個 InMemory*Repository singleton(而非 transient Dapper repos)
  2. DbBootstrap 完全跳過(`if (app.Environment.IsProduction() || env.IsEnvironment("Testing")) { /* skip */ }` 改為 `|| DB_PROVIDER == "inmemory"`)
  3. 不呼叫 `AddNpgsqlDataSource`
- 其他/未設: 現有邏輯(Dapper + DbBootstrap + Npgsql connection pool)

**種子帳號(InMemoryAuthRepository 必須內建同一份,與 DbBootstrap 共用):**

DbBootstrap.cs:124-146 的種子(backend/src/Backend.Api/Data/DbBootstrap.cs):
```
Tenants:
  - code: "demo-a", name: "示範租戶 A"
  - code: "demo-b", name: "示範租戶 B"

Users (密碼統一為 "password123"):
  - username: "admin-a", role: "ADMIN", tenant: "demo-a"
  - username: "user-a", role: "USER", tenant: "demo-a"
  - username: "user-b", role: "USER", tenant: "demo-b"
```

**確保 BCrypt hash 一致:**
- DbBootstrap 內 `DefaultPassword = "password123"`(L:13),呼叫 `BCrypt.Net.BCrypt.HashPassword(DefaultPassword)` 生成 hash
- InMemoryAuthRepository 初化時:**不重新計算 hash**,而是**抽出 DbBootstrap 的同一份常數**,改為共用方法或 hardcode 常數
- 驗收: 登入 `admin-a/password123` 或 `user-a/password123` 成功(兩模式下行為一致)

### 2.3 OTEL_MODE (platform OTel 目標)

| 屬性 | 值 |
| --- | --- |
| **環變名** | `OTEL_MODE` |
| **預設值** | (無,默認 Langfuse OTLP 模式) |
| **Lite 值** | `console` |
| **生效範圍** | platform/src/Platform.Web/Program.cs OTel 建構處 |
| **精確位置** | ~Program.cs:312-334(現有 AddOpenTelemetry builder) |

**語意:**
- `OTEL_MODE=console`:
  1. 以 `.AddConsoleExporter()` 取代現有 `.AddOtlpExporter(...Langfuse...)`
  2. 必須註冊 `.AddProcessor(new SimpleActivityExportProcessor(new RingBufferActivityExporter()))` 掛到 tracer provider
  3. Span sources / sampling 不動(chat.service, Experimental.Microsoft.Extensions.AI, AspNetCore 照舊)
- 其他/未設: 現有邏輯(OTLP 送 LANGFUSE_OTEL_ENDPOINT=http://localhost:3000)

**RingBufferActivityExporter 行為規格:**
- 繼承: `BaseExporter<Activity>`
- 大小: 最多保留最近 N=200 activities(spans)
- 實現: `ConcurrentQueue<Activity>` + 大小檢查(OnExport 時超限從頭出隊)
- 執行緒安全: ConcurrentQueue 內建
- 讀取介面: 公開方法 `IEnumerable<Activity> GetRecentSpans(int? limit = null)` 返現有 span 快照
- 屬性提取: 每個 span 回傳 `{StartTimeUtc, EndTimeUtc, Name, Duration, Attributes}` (足 LLM 讀)

### 2.4 其他沿用變數

| 變數 | 預設 | Lite 值 | 說明 |
| --- | --- | --- | --- |
| `EMBEDDINGS_PROVIDER` | `fake` | `fake` | 沿用(FakeEmbeddingProvider 決定性,RAG 不依賴真模型) |
| `LANGFUSE_ENABLED` | `false` | `false` | workflow 設定,不 trace(workflow/app/settings.py:13) |
| `CHAT_MODEL` | `gpt-4o-mini` | `mock-gpt` | platform 呼叫 LiteLLM 時的模型路由(litellm-config.yaml 內已定義) |
| `LLM_MODEL` | `gpt-4o-mini` | `mock-gpt` | workflow 呼叫 LiteLLM 時的模型路由 |
| `LLM_BASE_URL` | `http://localhost:4000` | `http://localhost:4000` | LiteLLM 端點(Lite 仍需起 uvx LiteLLM) |
| `JWT_SECRET` | `dev-jwt-secret-change-me-0123456789abcdef` | `dev-jwt-secret-change-me-0123456789abcdef` | 平台簽名(dev 容器與本機同值,無需改) |
| `INTERNAL_API_TOKEN` | `internal-dev-token` | `internal-dev-token` | backend 認證令牌(同上) |
| `BACKEND_BASE_URL` | `http://localhost:8002` | `http://localhost:8002` | platform/workflow 呼叫 backend 位置 |
| `RABBITMQ_URL` | `amqp://...@localhost:5672` | `amqp://...@localhost:5672` | 平台(Lite 上傳文件回 502,無約束改變) |
| `MEM0_BASE_URL` | `http://localhost:8000` | (無關) | HTTP mem0 endpoint(Lite 用 inmemory,此變數不讀) |
| `LANGFUSE_OTEL_ENDPOINT` | `http://localhost:3000/...` | (無關) | Langfuse OTLP(Lite console mode,此變數不讀) |

---

## 3. 元件規格

### 3.1 InMemoryMem0Client

**位置:** `platform/src/Platform.Service/InMemoryMem0Client.cs`

**簽章 (實作 IMem0Client 介面):**
```csharp
public class InMemoryMem0Client : IMem0Client
{
    public Task<string> RecallAsync(string userId, string query, CancellationToken ct = default);
    public Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default);
}
```

**初始化:**
- 單例註冊時建 `Dictionary<string, List<(string userMsg, string aiReply)>>` 欄位
- 欄位 lock object 用於執行緒同步

**RecallAsync 詳細:**
- 若 userId 為 null 或空: 返 `""`(空字串)
- 若 query 為 null 或空: 返 `""`
- 若 userId 已在字典:
  - 逐條檢查儲存的 `(userMsg, aiReply)` 對
  - userMsg 或 aiReply **包含** query(case-insensitive): 視為符合
  - 將所有符合項格式化為 `"- <userMsg>\n- <aiReply>\n..."` 逐行串接,**最新優先**
- 無符合項: 返 `""`

**RememberAsync 詳細:**
- 若 userId 為 null 或空: return 無操作
- 若 userMessage/aiReply 皆為 null 或空: return 無操作
- 有效時(鎖定):
  - 若 userId 不在字典中,建新 List
  - 將 `(userMessage, aiReply)` 對加入列表末尾
  - 若 list.Count > 100: 移除第一筆(最舊)
- 不丟例外

### 3.2 InMemory Repositories (backend)

**位置:** `backend/src/Backend.Api/Data/InMemory/InMemory*.cs`

**共同特性:**
- 命名空間: `Backend.Api.Data.InMemory`
- 類名與公開 API: 與 Fakes.cs 中的完全同(如 `InMemoryAuthRepository`)
- 初始化: Singleton 註冊(以 lambda factory 或構造函式)
- 執行緒安全: 內部 List/Dictionary 用 lock 或 ConcurrentCollections

**InMemoryAuthRepository 特需:**
1. 種子初化(constructor 內提取自 DbBootstrap):
   - Tenants: `{ Code: "demo-a", Name: "示範租戶 A" }`, `{ Code: "demo-b", Name: "示範租戶 B" }`
   - Users(密碼皆為 "password123"):
     - `admin-a` (ADMIN, demo-a 租戶)
     - `user-a` (USER, demo-a 租戶)
     - `user-b` (USER, demo-b 租戶)
2. BCrypt hash 值:**與 DbBootstrap 共用,不重新計算**
   - 提取 DbBootstrap.cs 中 `BCrypt.Net.BCrypt.HashPassword("password123")` 的結果
   - 或抽出共用常數供兩者使用(避免漂移)

**InMemoryRagRepository 特需:**
- RAG 查詢時(`SearchAsync(query, limit, threshold)`):**計算真 cosine 相似度**
- 實現: 
  ```csharp
  cosine = DotProduct(queryEmbedding, storedEmbedding) 
           / (Magnitude(queryEmbedding) * Magnitude(storedEmbedding))
  ```
- 取代測試中的固定 `score = 1.0`

**InMemorySkillRepository 特需:**
- 不 mock `WorkflowSkillValidator` —— Skill 驗證仍走 HTTP 呼叫 workflow(:8001)
- 若無真 workflow,驗證回 error(fail-fast)

### 3.3 RingBufferActivityExporter

**位置:** `platform/src/Platform.Service/RingBufferActivityExporter.cs`

**簽章:**
```csharp
public class RingBufferActivityExporter : BaseExporter<Activity>
{
    private readonly ConcurrentQueue<ActivitySnapshot> _buffer;
    private const int MaxCapacity = 200;
    
    public override ExportResult Export(in Batch<Activity> batch);
    public IEnumerable<ActivitySnapshot> GetRecentSpans(int? limit = null);
    
    public class ActivitySnapshot
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string Name { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, object> Attributes { get; set; }
    }
}
```

**Export 行為:**
1. 批次中各 Activity 轉 ActivitySnapshot
2. 逐一 enqueue 到 `_buffer`
3. 若 Count > MaxCapacity: `TryDequeue()` 移除舊項直至 Count <= MaxCapacity
4. 返 `ExportResult.Success`

**GetRecentSpans 行為:**
- 返現有 buffer 快照(`_buffer.ToList()`)
- 若傳入 limit: 取最後 limit 條(最新優先)
- 用於 agent 讀遙測

### 3.4 get_recent_traces AIFunction

**位置:** platform/src/Platform.Web/Services/ 或 ChatService.cs 內

**簽章 (LLM 呼叫規格):**
```
get_recent_traces(filter?: string, limit?: int = 10)
    → { traces: [ { timestamp_utc, name, duration_ms, attributes } ] }
```

**實現細節:**
1. 條件: 僅當 `OTEL_MODE=console` 時註冊(Program.cs 內判斷)
2. 呼叫者: ChatClientAgent builder `.WithTools(...)` 新增一個 `AIFunction`
3. 邏輯:
   - 從 ring buffer (`IEnumerable<ActivitySnapshot>`)讀
   - `filter` 為 null: 返全部
   - `filter` 非空: name 或 attributes 內容包含 filter 字串(case-insensitive)
   - 返最新 limit 條,timestamp 遞減
4. 錯誤: 若 ring buffer 未初化(不該發生),返空 list

**JSON 序列化(寫給 LLM):**
```json
{
  "traces": [
    {
      "timestamp_utc": "2026-07-21T10:30:45.123Z",
      "name": "chat.service/run",
      "duration_ms": 1234,
      "attributes": {
        "skill_name": "rag_qa",
        "model": "mock-gpt"
      }
    }
  ]
}
```

**scope 與隔離:**
- ring buffer 是 singleton(進程層級)
- 不分租戶(所有使用者共用一個 buffer) —— Lite 定位是開發機,不是多租戶隔離環境
- 明示:生產環境絕不應用此 function

---

## 4. 啟動腳本流程

### 4.1 start-lite.ps1 (Windows)

**前置檢查:**
```powershell
# 檢查 dotnet 10 SDK
dotnet --version  # 應為 10.*

# 檢查 uv(Python 版本管理)
uv --version

# 檢查 node
node --version  # >=18

# 檢查 python
python --version  # >=3.12
```

失敗時迴圈輸出:
```
ERROR: dotnet 10 SDK not found. Install from https://dotnet.microsoft.com/...
ERROR: uv not found. Install from https://astral.sh/uv
ERROR: node not found. Install from https://nodejs.org/...
ERROR: Python 3.12+ not found. ...
```

**LiteLLM 啟動(可選提示用戶):**
```powershell
Write-Host "Starting LiteLLM on :4000..."
Start-Process -FilePath "pwsh" -ArgumentList "-Command", `
  "uvx --from 'litellm[proxy]' litellm --config infra/litellm-config.yaml --port 4000" `
  -NoNewWindow -PassThru -RedirectStandardOutput "litellm.log"
```

**四服務平行啟動:**

```powershell
$jobs = @()

# backend
$jobs += Start-Process -FilePath "dotnet" -ArgumentList `
  "run", "--project", "backend/src/Backend.Api/Backend.Api.csproj" `
  -WorkingDirectory $PSScriptRoot/.. `
  -NoNewWindow -PassThru `
  -EnvironmentVariables @{
    "DB_PROVIDER" = "inmemory"
    "EMBEDDINGS_PROVIDER" = "fake"
    "ASPNETCORE_URLS" = "http://localhost:8002"
    "ASPNETCORE_ENVIRONMENT" = "Development"
  } `
  -RedirectStandardOutput "backend.log"

# workflow
$jobs += Start-Process -FilePath "pwsh" -ArgumentList `
  "-Command", "uv run uvicorn app.main:app --port 8001" `
  -WorkingDirectory $PSScriptRoot/../workflow `
  -NoNewWindow -PassThru `
  -EnvironmentVariables @{
    "BACKEND_BASE_URL" = "http://localhost:8002"
    "LLM_BASE_URL" = "http://localhost:4000"
    "LLM_MODEL" = "mock-gpt"
    "LANGFUSE_ENABLED" = "false"
  } `
  -RedirectStandardOutput "workflow.log"

# platform
$jobs += Start-Process -FilePath "dotnet" -ArgumentList `
  "run", "--project", "platform/src/Platform.Web/Platform.Web.csproj" `
  -WorkingDirectory $PSScriptRoot/.. `
  -NoNewWindow -PassThru `
  -EnvironmentVariables @{
    "MEM0_MODE" = "inmemory"
    "OTEL_MODE" = "console"
    "CHAT_MODEL" = "mock-gpt"
    "ASPNETCORE_URLS" = "http://localhost:8080"
    "ASPNETCORE_ENVIRONMENT" = "Development"
  } `
  -RedirectStandardOutput "platform.log"

# frontend
$jobs += Start-Process -FilePath "npm" -ArgumentList "run", "dev" `
  -WorkingDirectory $PSScriptRoot/../frontend `
  -NoNewWindow -PassThru `
  -RedirectStandardOutput "frontend.log"
```

**健康檢查:**
```powershell
$healthChecks = @(
  @{ url = "http://localhost:8002/health"; name = "backend" }
  @{ url = "http://localhost:8001/health"; name = "workflow" }
  @{ url = "http://localhost:8080/actuator/health"; name = "platform" }
)

foreach ($check in $healthChecks) {
  $attempt = 0
  while ($attempt -lt 30) {
    try {
      $resp = Invoke-WebRequest -Uri $check.url -TimeoutSec 2
      if ($resp.StatusCode -eq 200) { break }
    } catch { }
    $attempt++
    Start-Sleep -Seconds 2
  }
  if ($attempt -eq 30) {
    Write-Error "$($check.name) failed health check after 60s"
  }
}
```

**完成輸出:**
```
✓ All services started successfully!
  backend:  http://localhost:8002  (logs: backend.log)
  workflow: http://localhost:8001  (logs: workflow.log)
  platform: http://localhost:8080  (logs: platform.log)
  frontend: http://localhost:5173  (logs: frontend.log)
  litellm:  http://localhost:4000  (logs: litellm.log)

To stop, run: .\stop-lite.ps1
```

**保存 job IDs 到檔案** (供 stop-lite.ps1 收攤):
```powershell
$jobs | ForEach-Object { $_.Id } | Set-Content "start-lite.pids"
```

### 4.2 start-lite.sh (Linux/macOS)

類似邏輯,改用 bash:
- `command -v dotnet` 檢查工具
- `nohup dotnet run ... > backend.log 2>&1 &` 後臺啟
- 用 `curl -f http://localhost:8002/health` 健康檢查
- `echo $! >> start-lite.pids` 保存進程 ID

### 4.3 stop-lite.ps1

```powershell
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

### 4.4 stop-lite.sh

```bash
if [ -f start-lite.pids ]; then
  while IFS= read -r pid; do
    kill -9 "$pid" 2>/dev/null
  done < start-lite.pids
  rm start-lite.pids
  echo "All services stopped."
else
  echo "No running processes found."
fi
```

---

## 5. 驗收條件

### 5.1 環境啟動驗收

- [ ] `./scripts/start-lite.ps1` 執行無例外,四個服務皆通過健康檢查
- [ ] 日誌檔 backend.log / workflow.log / platform.log 無 error/exception (warning 可接受)
- [ ] 四個 port 監聽確認: `netstat -ano | findstr "(8002|8001|8080|5173|4000)"`

### 5.2 認證與隔離

- [ ] 訪問 frontend :5173,登入 admin-a/password123 成功,session 包含 JWT
- [ ] 登入後 X-Tenant-Id(=`demo-a`), X-User-Id(=`admin-a`) 正確轉發到 backend(可看日誌或 network tab)
- [ ] 登出後無法訪問 /api/copilot/agui(回 401)
- [ ] 用 user-a/password123 與 user-b/password123 各登入,同一 threadId 提問,兩者 session 隔離無洩漏

### 5.3 記憶層驗收

**短期記憶(滑動窗口 vs 持久化):**
- [ ] SSE 聊天 N 輪后(N>20):
  - `GET /api/chat/history` 仍回**全部**已登入訊息(backend in-memory repo 完整持久化)
  - 但 agent 回覆時 context 內容被 `SlidingWindowCompactionStrategy` 壓縮至 20 則(agent 看到限縮內容,但 history API 回完整)
- [ ] 聊天中提及超過 20 輪之外的話題,agent 無法參考(滑動窗口已刪除,符合預期)

**長期記憶(mem0):**
- [ ] `MEM0_MODE=inmemory` 時,agent 呼叫 recall 回**非空字串**(至少曾經 remember 的事實,格式 `"- userMsg\n- aiReply\n..."`)
- [ ] 重啟後記憶清空(符合 in-memory 特性,無跨程序持久化)

### 5.4 資料庫驗收

- [ ] 登入成功(InMemoryAuthRepository 種子帳號生效)
- [ ] 查詢文檔/config 回正確值(InMemory repos 初化邏輯無誤)
- [ ] RAG 搜尋返回相關文檔(cosine 計算準確)

### 5.5 OTel 與遙測驗收

- [ ] `OTEL_MODE=console` 時,platform.log 內容包含 span JSON(每次 LLM 呼叫有痕跡)
- [ ] ring buffer 最多 200 spans(超限自動出隊)

### 5.6 LLM 讀遙測驗收

- [ ] SSE 聊天中使用者要求 agent「顯示最近的查詢」或「告訴我剛才的 spans」
- [ ] agent 呼叫 get_recent_traces 工具,回傳最近 10 條 spans 的摘要(含 name, duration)
- [ ] 返回內容準確(timestamp 遞減,name 對應實際操作)

### 5.7 跨服務流程驗收

- [ ] SSE 聊天輸入 mock 問題,mock-gpt 回固定文本
- [ ] Skill CRUD:建新 skill,驗證走 workflow /api/skills/validate(live HTTP),成功即驗工作流通信
- [ ] Skill invoke:調用 built-in rag_qa,無錯誤(即使返回無結果)
- [ ] 文件上傳:回 **502** ApiError(RabbitMQ broker 未啟;日誌可見 `publish failed` 通知)

### 5.8 UI 驗收

- [ ] 前端 4 個 view(Chat, Documents, Analysis, Config)可訪問無 JS 錯誤
- [ ] CopilotKit sidebar 可見,ag-ui copilot 可交互(回 mock 文本)
- [ ] 登出 button 可用

---

## 6. 非目標

| 項目 | 原因 |
| --- | --- |
| 資料跨重啟持久化 | Lite 定位是開發/演示,不需持久 |
| RabbitMQ consumer | 階段二工作,現階段文件上傳回 502 + 說明即可 |
| workflow/backend OTel | 階段二,當前 platform OTel 已足 |
| 多租戶真實隔離驗證 | 開發環境足,正式驗證在 mode A(docker)進行 |
| 性能基準 | Lite 非性能測試環境 |
| mock-gpt 真實推理 | 故意虛假,用 real LLM 需改 CHAT_MODEL + 金鑰 |

---

## 7. 疑難排解指南(文件交付時補充)

_(待後續補) 含常見問題如「dotnet not found」、「port 8080 already in use」、「health check timeout」等_

---

下一步: 03-design(實作順序與簽章) → 開發者實作 → e2e-verifier 驗證 → 交付
