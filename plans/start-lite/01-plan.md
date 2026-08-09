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

**backend/workflow 無 OTel** —— workflow/app/settings.py:13 的 `LANGFUSE_ENABLED=false` 預設；tracing 在啟用後若 handler 初始化失敗，會降級但只發出一則固定、無內容警告，並累計程序內失敗次數。

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
2. (可選)啟 LiteLLM: `uvx --from "litellm[proxy]" litellm --config infra/litellm-config.lite.yaml --port 4000`(移除 langfuse callback,見 02-spec §1.2)
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
| JWT ES256 設定 | `JWT_ISSUER` / `JWT_AUDIENCE` / active kid + private key / public ring | 同一組公開開發 key pair | Backend 簽名、Platform 只驗公鑰；Lite helper 明確選擇 Development |
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

詳見下方附錄:疑難排解指南、邊界案例與風險、未來升級路徑、最小驗證集合、非驗收範圍。e2e 驗證見 [e2e-verifier](../../.claude/agents/e2e-verifier.md)。

---

## 附錄 A — 疑難排解指南(併自 02-spec.md,2026-08-09 整併)

### 腳本啟動坑

1. **`dotnet run` 綁定錯誤埠 (e.g. :5008 instead of :8080)**
   - 原因: launchSettings.json 的 profile 優先於環境變數 `ASPNETCORE_URLS`
   - 解決: 在啟動腳本中加 `--no-launch-profile` 旗標，or 臨時改 launchSettings.json

2. **Windows 上 npm 子行程孤兒(佔住 :5173)**
   - 原因: `npm run dev` 生衍的 node 子行程收不到信號
   - 解決: 用 `taskkill /T /F /PID <pid>` 殺掉進程樹 (stop-lite.ps1 已實裝)

3. **Linux/macOS 上終端背景工作未正確等待**
   - 原因: `nohup ... &` 後迴圈檢查埠連線失敗
   - 解決: 加重試邏輯與延遲，或用 `pgrep -P` 建立進程樹追蹤 (start-lite.sh 已實裝)

### LiteLLM 工具鏈限制

4. **`uvx --from "litellm[proxy]"` 安裝失敗 (missing Rust/MSVC)**
   - 原因: litellm 最新版本含 Rust 依賴，無預建 wheel 時會觸發 `pip install` 編譯
   - 症狀: Windows 上缺 Visual Studio Build Tools 時，`error: Microsoft Visual C++ 14.0 or greater is required`
   - 解決 (a): 裝 Visual Studio Build Tools (C++ workload)
   - 解決 (b): 在 start-lite 腳本中改用舊版 litellm(有預建 wheel),如 `uvx --from "litellm[proxy]==1.x.y" litellm ...`
   - **注意**: 此為環境依賴問題,非腳本 bug;本機開發環境應備足工具

---

## 附錄 B — 邊界案例與風險(併自 03-design.md,2026-08-09 整併)

### B.1 重啟即失憶

**現象:** Lite 進程重啟後,所有 in-memory 記憶(mem0、對話、文檔)清空

**是否可接受:** ✅ **是**,符合演示定位

**緩解:**
- 文檔明示「Lite 不保存跨重啟狀態」
- 若需持久化,改用 `start-full`(容器模式)
- 開發迭代時重啟無損

### B.2 記憶體上限

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

### B.3 多進程不共享

**現象:** 若誤啟兩份 platform 實例,各自獨立記憶

**是否可接受:** ✅ **是**,Lite 預期單一進程組(scripts/start-lite 一次啟動即可)

**防守:** 預設埠衝突(8080/8001 已占)會自動拒絕第二份

### B.4 種子帳號 hash 漂移

**風險:** InMemoryAuthRepository 與 DbBootstrap 的 BCrypt hash 不一致 → 登入失敗

**防守:**
- 兩者都用 `BCrypt.Net.BCrypt.HashPassword("password123")` 相同邏輯
- 或抽出共用常數 → 兩處都引用
- 測試驗證登入 `admin-a/password123` 兩模式一致

### B.5 Isolation key 取不到(AG-UI)

**現象:** 未登入或 JWT 缺 tenant/user claim → `IsolationKeyScopedAgentSessionStore(Strict=true)` 拋例外

**是否可接受:** ✅ **是**,AG-UI 本來就應要求認證(`RequireAuthorization()`)

**防守:** `JwtTenantIsolationKeyProvider` 回 null 時 fail-closed,不創建共享 key

### B.6 RingBuffer 容量不足

**現象:** Agent 想查最近 100 spans 但只保留 200 total → 快速聊天時 span 出隊太快

**估計:** 每輪聊天 ~3-5 spans → 200 spans ≈ 40-65 輪對話,開發足用

**是否可接受:** ✅ **是**,不是正式演示環境

**升級:** 若需更多歷史,改 `MaxCapacity` 常數(可改為環變)

---

## 附錄 C — 未來升級路徑(併自 03-design.md,2026-08-09 整併)

- **RingBuffer 租戶隔離:** 若 Lite 用於多租戶演示,改 per-tenant 環形緩衝
- **對話 TTL:** InMemoryConversationRepository 加 24h 自動清理
- **Skill 版本化:** InMemorySkillRepository 升格為無限版本歷史(目前省略)
- **文件 async:** 階段二 backend Channel<DocumentMessage> + platform HTTP IDocumentQueue

---

## 附錄 D — 最小驗證集合(併自 04-acceptance-test.md,2026-08-09 整併)

只能跑一組時,跑這 6 條:

| # | 案例 | 單獨守住什麼 |
| --- | --- | --- |
| 1 | `A-01` + `A-02` | 預設模式零污染(既有 .NET 測試就是行為快照):三個環變皆未設,`platform/` 與 `backend/` 下 `dotnet test` 全綠、案數不減,且 backend 側 Fakes 搬遷後未改任何斷言 |
| 2 | `A-06` | 開關只認暗語,亂設不會半開:環變設為其他值(`MEM0_MODE=banana`、`DB_PROVIDER=postgres`、`OTEL_MODE=otlp`)時行為與未設完全相同(現行路徑) |
| 3 | `B-D-01` | singleton —— 錯成 scoped 時 lite「能啟動但什麼都存不住」,最隱蔽的失敗型態:`DB_PROVIDER=inmemory` 下解析六個 repository 介面兩次,須為同一 singleton 實例 |
| 4 | `B-D-03` | 種子帳號 + BCrypt Verify,lite 的第一個使用者動作(登入)成敗在此:`FindUserByUsernameAsync("admin-a"/"user-a"/"user-b")` 角色與租戶齊全,且 `BCrypt.Verify("password123", hash)` 為 true(不比對 hash 字面值) |
| 5 | `B-T-02` | get_recent_traces 絕不進生產:`OTEL_MODE` 未設時,ChatClientAgent 的 `ChatOptions.Tools` 不含 `get_recent_traces` |
| 6 | `C-01` + `C-10` | 腳本真的起得來、收得掉,整個計畫的對外承諾:乾淨 shell 執行 `start-lite.ps1`/`.sh`,前置檢查通過,五個進程全起,三個健康檢查在逾時內綠,產生 `start-lite.pids`,全程未觸碰 Docker;`stop-lite.ps1` 收攤後所有 PID 終止、port 釋放、pids 檔刪除,重複執行不報錯 |

---

## 附錄 E — 非驗收範圍(YAGNI)(併自 04-acceptance-test.md,2026-08-09 整併)

- RabbitMQ in-memory 替代(階段二;文件上傳只驗 502 契約不變)。
- backend/workflow 的 OTel(階段二;lite 只有 platform 有遙測)。
- mem0 recall 品質(關鍵字比對是刻意的低配,不驗語意相關性)。
- ring buffer 的租戶隔離(進程級共用是明文設計)。
- 效能/壓力(lite 是開發機模式)。
- 20/21 滑動窗口邊界 —— 屬 copilot-shared-core 的既有驗收案例,不重複。
- 真模型下的工具呼叫體驗(mock-gpt 不會主動呼叫工具;要真模型驗收需改 `CHAT_MODEL` + 金鑰,屬手動加測)。
