# 計畫書 — start-lite（無容器啟動模式）

> 狀態: **已實作、e2e 驗證通過。** 整合在主分支;全綠無阻。

## 1. 問題 — 容器化的成本

現行 `start-full.ps1` 與 `start-full.sh` 需要 Docker Desktop 與完整 docker compose 棧，這對下列使用場景造成障礙：

| 場景 | 痛點 |
| --- | --- |
| 前端開發 | 改一行 CSS 卻要等 5 分鐘 docker build;容器 hot reload 不穩定 |
| 無容器環境(CI/WSL/公司機) | docker 裝不了或不授權;每個開發者各自調;無單一啟動指令 |
| 演示/教學 | 非技術主管裝 Docker 難;展示時網速差 pull image 慢;測試環境快速迭代 |
| 端到端驗證 | 對比 mode A(整合驗證)需要確認跨服務邊界,但逐行 debug 要改配置 |

**動機:** 備第三套啟動模式,不動現有容器化工作流 —— 開發者可按需選 `start-lite`(all-localhost，開發快速)或 `start-full`(docker compose，整合驗收)。

## 2. 現況盤點

### 2.1 服務啟動現狀

| 服務 | 目前啟動方式 | 內部狀態 | 外部依賴 | Lite 可行性 |
| --- | --- | --- | --- | --- |
| platform (:8080) | docker/dotnet 直跑 | 短期記憶 in-mem `InMemoryChatHistoryProvider` | HTTP(backend, workflow, mem0, RabbitMQ) | 可行,改 HTTP 客戶端配置 |
| backend (:8002) | docker/dotnet 直跑 | 資料層 6 repo 對 Dapper+PostgreSQL | PostgreSQL, RabbitMQ | 可行,用測試 Fakes 升格 |
| workflow (:8001) | docker/python 直跑 | LangGraph engine,Skill node registry | HTTP(backend), LLM_BASE_URL | 可行,設定切換 |
| frontend (:5173) | npm run dev | Vite dev server | HTTP proxy to :8080 | 零改動 |
| LiteLLM (:4000) | docker-compose | mock-gpt 模型路由 | OPENAI_API_KEY | 新增:uvx 本機跑 |

### 2.2 記憶與資料層拆解

**platform mem0 層** (platform/src/Platform.Service/Abstractions/IMem0Client.cs:12-15)
- 介面: `RecallAsync(userId, count?) → List<string>`，`RememberAsync(userId, fact)`
- 實作: `Mem0Client`(DI 於 Program.cs:207),POST /search、/memories
- 錯誤契約: 所有例外吞掉(Mem0Client 內部 try/catch,返 empty/no-op)
- 當前預設: `MEM0_BASE_URL=http://localhost:8000`(容器內 mem0 服務)

**backend 資料層** (backend/src/Backend.Api/Program.cs:33-38 DI,6 個 repository)
- `IAuthRepository`, `IConversationRepository`, `IRagRepository`, `IConfigRepository`, `ISkillRepository`, `IConfigurationSetRepository`
- 實作: 全走 Dapper 對 PostgreSQL(pgvector 用於 rag_chunks embedding 查詢)
- 測試版本: backend/tests/Backend.Api.Tests/Fakes.cs 已有 6 個 fake impl + FakeSkillValidator
- DbBootstrap(Program.cs:99-104): Testing 環境跳過，fail-fast 建 schema + 種子 tenant/user
- 嵌入提供: EMBEDDINGS_PROVIDER 預設 fake(FakeEmbeddingProvider:SHA-256 種子 PRNG，確定性 1536 維)

**platform RabbitMQ** (Platform.Web/Services/RabbitDocumentQueue.cs)
- Lazy singleton,首次 `PublishAsync` 才連線
- 失敗語義: broker 不在 → publish 回 502(既有契約)
- Lite 階段一不處理文件隊列

### 2.3 OTel 與 Langfuse 現狀

**platform OTel** (Platform.Web/Program.cs:312-334)
- 已引用 OpenTelemetry.Exporter.OpenTelemetryProtocol / Extensions.Hosting / Instrumentation.AspNetCore (1.16.0)
- Span sources: `chat.service`, `Experimental.Microsoft.Extensions.AI`, AspNetCore(排除 /actuator/health)
- OTLP 目標: `LANGFUSE_OTEL_ENDPOINT=http://localhost:3000/api/public/otel/v1/traces`(容器內 Langfuse)
- Testing 環境跳過

**backend/workflow 無 OTel** —— workflow/app/settings.py:13 的 `LANGFUSE_ENABLED=false` 預設,tracing 失敗靜默降級

**Lite 模式需求:** Console exporter + 自訂 ring-buffer(保留最近 N=200 spans),可供聊天中的 agent 讀取。

## 3. 方案選擇

### 3.1 容器 vs 無容器的對照表

| 職責 | 容器(現狀) | 無容器(Lite) | 理由 |
| --- | --- | --- | --- |
| 長期記憶 | mem0 容器(HTTP :8000) | InMemoryMem0Client(per-userId list) | 演示/開發場景無需真長期存儲 |
| 資料庫 | PostgreSQL 容器 | InMemory repositories | 測試已有 6 個 Fake impl,升格即可 |
| 文件隊列 | RabbitMQ 容器 + DocumentConsumerService | 階段一不處理,上傳回 502 | 階段二 backend Channel化(future) |
| OTel | Langfuse 容器 | Console + ring-buffer exporter | 開發者本機需要看 spans,Langfuse 不必跑 |
| LLM | LiteLLM 容器 | uvx 啟動 LiteLLM 本機 | mock-gpt 路由不需容器化 |
| 代碼編譯 | dotnet 在容器內 | dotnet 10 SDK 本機 | 更快、更好調試 |

### 3.2 為何拆成兩階段

**階段一(本規格範圍):** 短期記憶 + 無容器資料 + OTel Console + LLM 讀遙測
- 交付: `start-lite.ps1` / `start-lite.sh`,四個服務平行啟動,health check 無阻塞
- 定位: 前端開發、快速迭代演示

**階段二(future work,列於 §7):** 文件 Channel 化 + backend/workflow OTel
- backend Channel<DocumentMessage> hosted service,重用 DocumentProcessor
- platform 加 IDocumentQueue HTTP 版,保留 202→processing→ready 契約
- workflow/backend 加 OTel 套件與 traces
- 定位: 長期內容處理的完整流程

## 4. 定案設計(技術決策清單)

### 4.1 長記憶 in-memory(platform)

**新增:** `platform/src/Platform.Service/InMemoryMem0Client.cs`

實作 `IMem0Client`,per-userid (userMessage, aiReply) 對 list(最多 N=100 對):
- `RecallAsync(userId, query, ct)`: 依 query 關鍵字比對歷史訊息對,最近優先,回傳串接字串(`"- userMsg\n- aiReply\n..."` 或空字串)
- `RememberAsync(userId, userMessage, aiReply, ct)`: 追加 (userMessage, aiReply) 對至該 user,超 100 對時刪最舊

**配置:** 環變 `MEM0_MODE`
- `inmemory`: 改用 singleton InMemoryMem0Client(新建)
- 預設/其他: 沿用 `AddHttpClient<IMem0Client, Mem0Client>()`(現狀)

**契約:** 與 Mem0Client 完全相同(錯誤全吞,不丟例外);使用側(ChatService)零改動。

### 4.2 資料庫 in-memory(backend)

**搬家:** backend/tests/Backend.Api.Tests/Fakes.cs 的 6 個 fake repository → `backend/src/Backend.Api/Data/InMemory/`
- 測試改引用新位置(相同 namespace + 類名,消除漂移風險)

**補強 InMemory repos:**
1. InMemoryAuthRepository: 內建與 DbBootstrap 相同種子(demo-a/demo-b 租戶,admin-a/user-a/user-b 用戶,密碼 password123,BCrypt hash 共用),否則登入失敗
2. InMemoryRagRepository: 對儲存的 `float[]` 算真 cosine 相似度(FakeEmbeddingProvider 確定性),取代固定 score=1.0
3. SkillValidator: 不 fake,照走 WorkflowSkillValidator HTTP(workflow 在 lite 是活的,維持引擎唯一真理)

**配置:** 環變 `DB_PROVIDER`
- `inmemory`: 改 DI 註冊 singleton InMemory repos,跳過 DbBootstrap 與 AddNpgsqlDataSource
- 預設/其他: 沿用 Dapper + Npgsql (現狀)

### 4.3 RabbitMQ(階段一不處理)

**現狀:** platform lazy 連線,只要不上傳文件即無感
**階段一行為:** 上傳文件 → 502(既有契約)+ 腳本輸出註明「文件隊列未啟動」

**階段二(future):** backend 加 `POST /internal/documents/enqueue` + Channel hosted service,platform 加 HTTP IDocumentQueue

### 4.4 OTel Console exporter(platform)

**新增:** 
1. 套件: `OpenTelemetry.Exporter.Console`(nuget)
2. 自訂: `RingBufferActivityExporter : BaseExporter<Activity>`,bounded ConcurrentQueue,保留最近 N=200 spans (~30 行)

**配置:** 環變 `OTEL_MODE`
- `console`: 以 AddConsoleExporter() 取代 OTLP exporter,掛 RingBufferActivityExporter;span sources 不動
- 預設/其他: AddOtlpExporter(Langfuse endpoint)(現狀)

**明示:** Console exporter 是 verbose(每個 span 打 JSON),RingBufferExporter 的目的是給 agent 讀。

### 4.5 LLM 讀遙測(platform,lite-only)

**機制:** `OTEL_MODE=console` 時,對共用 ChatClientAgent 註冊原生 AIFunction `get_recent_traces`
```
get_recent_traces(filter?: string, limit?: int) → List<{timestamp, name, duration_ms, attributes}>
```

**實作地點:** platform/src/Platform.Web/Services/ChatService.cs 或 Platform.Service/下新檔

**契約:**
- 非 skill(不違反「skill 永不註冊為原生 tool」),會以 AG-UI TOOL_CALL_* 事件現形
- 僅存在於 lite 模式,不得漏到正式組態(環變判斷)
- 讀本進程 ring buffer,零新信任邊
- 兩條管線(chat + AG-UI)皆可呼叫

**捨棄方案 B(workflow @tool + skill):** 會引入 workflow→platform 反向信任邊,不取。

### 4.6 啟動腳本(scripts/start-lite.ps1 / .sh)

**流程:**
1. 前置檢查: dotnet 10 SDK、uv、node、python 3.12+
2. (可選)啟 LiteLLM: `uvx --from "litellm[proxy]" litellm --config infra/litellm-config.yaml --port 4000`
3. 平行啟動四個服務(各自新視窗/背景 job):
   - backend(:8002): `DB_PROVIDER=inmemory EMBEDDINGS_PROVIDER=fake`
   - workflow(:8001): `LLM_MODEL=mock-gpt BACKEND_BASE_URL=http://localhost:8002`
   - platform(:8080): `MEM0_MODE=inmemory OTEL_MODE=console CHAT_MODEL=mock-gpt`
   - frontend(:5173): `npm run dev`
4. 健康檢查輪詢: /health(:8002)、/health(:8001)、/actuator/health(:8080),每個 2 秒最多 30 次
5. 全綠後列出四個 service URL 與日誌文件位置
6. `stop-lite.ps1` / `.sh` 收攤所有 job

**順序:** DbBootstrap 被跳過所以無啟動順序依賴(platform 可先啟)。

### 4.7 環境變數矩陣

| 變數 | 預設值 | Lite 值 | 說明 |
| --- | --- | --- | --- |
| `MEM0_MODE` | 無(HTTP client) | `inmemory` | platform 記憶模式 |
| `DB_PROVIDER` | 無(Npgsql) | `inmemory` | backend 資料源 |
| `OTEL_MODE` | 無(Langfuse OTLP) | `console` | platform tracing 目標 |
| `EMBEDDINGS_PROVIDER` | `fake` | `fake` | 沿用(確定性) |
| `LANGFUSE_ENABLED` | `false` | `false` | 沿用(workflow 不 trace) |
| `CHAT_MODEL` | `gpt-4o-mini` | `mock-gpt` | platform 模型 |
| `LLM_MODEL` | `gpt-4o-mini` | `mock-gpt` | workflow 模型 |
| `LLM_BASE_URL` | `http://localhost:4000` | `http://localhost:4000` | LiteLLM endpoint |
| `JWT_SECRET` | `dev-jwt-secret-change-me-0123456789abcdef` | `dev-jwt-secret-change-me-0123456789abcdef` | 開發預設(容器與本機同值) |
| `INTERNAL_API_TOKEN` | `internal-dev-token` | `internal-dev-token` | 開發預設 |
| `RABBITMQ_URL` | `amqp://...` | `amqp://...` | 平台(Lite 文件上傳回 502) |
| `MEM0_BASE_URL` | `http://localhost:8000` | 無關(InMemory用) | HTTP mem0 endpoint(Lite 不用) |

## 5. 明示取捨與非目標

| 項目 | Lite 姿態 | 說明 |
| --- | --- | --- |
| 跨重啟持久化 | 無 | InMemory repos/mem0 重啟後清空(符合演示定位) |
| 資料庫完整性 | 簡化 | jsonb/advisory lock/ON CONFLICT/CTE 語意由 C# 邏輯重現,無 DB 約束 |
| 內容隸屬性 | 完整 | 租戶隔離仍存(session key = `{tenant}:{user}`) |
| mock-gpt 推理 | 假 | 所有 mock 回固定字串,內容無價值(換真模型只改 CHAT_MODEL/LLM_MODEL + 真金鑰) |
| LiteLLM 必要 | 是 | 即使無 RabbitMQ 也要啟 LiteLLM(:4000),因 mock-gpt 是 LiteLLM 功能(可改 OPENAI_API_KEY=dummy) |

## 6. 風險與緩解

| 風險 | 高/中/低 | 緩解 |
| --- | --- | --- |
| InMemory repos 與 Dapper 語意漂移 | 中 | 測試改引用同一份 Fake,新增 Lite 整合測試(e2e-verifier 跑 start-lite 驗) |
| ring-buffer capacity 不足導致 agent 讀不到要的 span | 低 | N=200 spans ≈ 20-50 輪聊天(每輪 3-5 spans),開發場景足用;超限加 debug 日誌(環變控) |
| 種子帳號不一致導致登入失敗 | 中 | InMemoryAuthRepository 與 DbBootstrap 必須 hardcode 同一份種子(hash 用同一 BCrypt salt) |
| 迴圈依賴或 circular HTTP 呼叫 | 低 | 現存架構已無環(platform→backend→workflow);verify 時檢 HTTP timeout 與死鎖 |

## 7. 分階與里程碑

### 本規格(階段一)

| 階段 | 內容 | 交付物 | 驗收 |
| --- | --- | --- | --- |
| **P1** | InMemoryMem0Client + 環變 MEM0_MODE | platform/src/Platform.Service/InMemoryMem0Client.cs | `MEM0_MODE=inmemory` 聊天時 agent recall/remember 走本機 |
| **P2** | InMemory repositories + 環變 DB_PROVIDER | backend/src/Backend.Api/Data/InMemory/ + 種子帳號 | 登入成功,資料查詢正確 |
| **P3** | RingBufferActivityExporter + ConsoleExporter | platform/src/Platform.Service/RingBufferActivityExporter.cs | `OTEL_MODE=console` 時 console 有 span 日誌 |
| **P4** | get_recent_traces 原生函式 + lite-only 限制 | platform/src/Platform.Web/Services/...AIFunction.cs | SSE 聊天中呼叫 `/tools/get_recent_traces` 能讀 ring buffer |
| **腳本** | start-lite.ps1 / .sh + stop-lite.* | scripts/ | 一鍵啟動四服務,健康檢查全綠 |

### 未來階段二(不在本規格)

- backend 加 Channel<DocumentMessage> hosted service
- platform 加 HTTP IDocumentQueue
- workflow/backend OTel 套件與 trace
- 文件 202→processing→ready 完整流程 Lite 支援

## 8. 與現有計畫的關係

- **chat-skill-routing**(已交付): Lite 複用 SkillRoutingAgent 邏輯(不新增)
- **copilot-shared-core**(進行中): Lite 仍需 AG-UI 認證 + SessionIsolationKeyProvider,共用記憶層(P2-P3);可並行實作
- **settings-skill-redesign**(進行中): Lite 中 Skill CRUD/validate/invoke 逕走活 workflow(:8001),不動 UI

---

下一步: [02-spec.md](02-spec.md)(可交付規格) → 實作 → [e2e-verifier](../../.claude/agents/e2e-verifier.md) 驗證
