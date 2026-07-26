# springaitest

ASP.NET Core 10 WebAPI + Microsoft Agent Framework（OpenAI）範例專案,採 **monorepo**:後端為 .NET 三層式架構，前端為 React + Vite SPA,基礎設施（LiteLLM 閘道 + Langfuse 可觀測性 + mem0 長期記憶）以 Docker Compose 管理。

## 專案結構

```
SpringAITest/
├── scripts/                    跨平台啟動腳本（.sh 給 Linux/macOS、.ps1 給 Windows）
│   ├── start-infra.sh / .ps1   模式 A：只起基礎設施
│   ├── start-full.sh  / .ps1   模式 B：全容器（infra + 前端 + 後端）
│   ├── verify-copilot-shared-core.ps1  Copilot Shared Core 可重跑 black-box smoke companion
│   └── ensure-mem0-db.* 　　　  備妥 mem0 的 postgres 前置（建 mem0_app、刷新 collation）；由上面兩腳本自動呼叫
├── platform/                    前置閘道：ASP.NET Core 10 WebAPI（Gateway + LLM 編排）
│   ├── Platform.sln             .NET 方案檔
│   ├── Dockerfile              選用：僅 --profile full 用到
│   ├── src/
│   │   ├── Platform.Service     業務層：Agent Framework 聊天、mem0 長期記憶、滑動視窗短期記憶、BackendClient 代理
│   │   └── Platform.Web         展示層 + 啟動模組：controller、JWT 驗證、SSE 串流、例外處理、設定檔
│   └── tests/                  xUnit 測試專案（Service.Tests、Web.Tests）
├── backend/                     核心服務層：ASP.NET Core 10 WebAPI（驗證、檔案、檢索、分析、技能、組態）
│   ├── Backend.sln              .NET 方案檔
│   ├── Dockerfile              選用：容器模式用到
│   ├── src/
│   │   └── Backend.Api/         單一專案（feature folders：Auth、Conversations、Files、Retrieval、Analysis、Skills、Config、Agents）
│   └── tests/                  xUnit 測試專案 326 個（Backend.Api.Tests）
├── frontend/                   前端：React 19 + Vite + TypeScript（登入入口 + 聊天、文件、分析、系統設定、Agents 工作區）
│   ├── vite.config.ts          dev 時把 /api proxy 到 :8080（免 CORS）
│   ├── Dockerfile / nginx.conf 正式：多階段 build → nginx 靜態檔 + /api 反代（SSE 關緩衝）
│   └── src/                    api/{http,auth,chat,documents,skills,analysis,config}.ts、hooks/{useAuth,useChat,useDocuments}.ts、components/{AuthPage,AppShell,ChatView,...}.tsx
├── workflow/                   工作流：Python + LangGraph + FastAPI（Skill 引擎、多個具名工作流、向量檢索、分析工作流、權限邊界）
│   ├── app/
│   │   ├── engine/             Skill 引擎層（@node、@tool 裝飾器、YAML 編譯器、表達式求值器、沙箱執行器）
│   │   ├── skills/             Skill 定義（kb-query.yaml 內建範例、custom.py 自訂載入）
│   │   ├── tools.py            四個初始 tool（retrieve、embed、chunk、rerank）
│   │   ├── nodes/              可重用節點（retrieve：租戶過濾向量檢索）、kb_query 節點家族（十個細粒度節點）
│   │   └── main.py             FastAPI 進入點（內部密鑰驗證 + 多租戶 context）
│   ├── pyproject.toml          uv 管理依賴（langgraph、langchain-openai、fastapi、httpx…）
│   └── Dockerfile              正式：容器內經服務名連 LiteLLM，檢索經 HTTP 呼叫 backend
└── infra/
    ├── docker-compose.yml      LiteLLM + Langfuse（自架 v3）+ mem0 + backend + workflow + appdb（pgvector）；frontend / platform 為 profile「full」
    ├── mem0.Dockerfile         mem0 官方映像的薄封裝（補上缺的 libpq，見下方長期記憶章節）
    ├── litellm-config.yaml     LiteLLM 路由（chat 模型、embedding、免額度測試用的 mock-gpt）
    ├── .env.example            金鑰範本
    └── .env                    真正的金鑰（自建，已 gitignore）
```

## 技術棧

- **平台閘道**(.NET):.NET SDK 10、ASP.NET Core 10、Microsoft Agent Framework（`Microsoft.Agents.AI`，經 LiteLLM 閘道連 LLM）、AG-UI 協定端點、Skill CRUD/invoke proxy、**Agent Registry proxy**、OpenTelemetry、RabbitMQ.Client 7.2.1（非同步佇列）;測試用 xUnit 660 個（Service 334 + Web 326）+ 手寫 fake（未引入 mocking 套件）。
- **核心服務**(.NET):.NET SDK 10、ASP.NET Core 10、Dapper 2.x + Npgsql 9.x（直連 PostgreSQL，無 ORM）、pgvector 向量操作、RabbitMQ.Client 7.2.1（消費文件佇列）、Skills 功能、**Agent Registry CRUD/publish/revisions**、appdb 永久儲存;測試用 xUnit 694 個（所有 PostgreSQL 相依測試現在都跑，採租戶前綴隔離 + IAsyncLifetime 清理，0 個 skipped）、手寫 fake repository（未引入 mocking 套件）。
- **前端**:React 19 + Vite + TypeScript;dev 時 Vite proxy `/api` → `:8080`,瀏覽器同源免 CORS。系統設定視圖內含三分頁（Skill 管理、工作流節點參數、一般設定），`AGENT_BUILDER_ENABLED` 開啟時提供 ADMIN-only Agents workspace；`WORKFLOW_DESIGNER_ENABLED` 開啟且帳號具 `workflow.manage` 時，另提供 Workflow Designer 與 Orchestrator Registry；再開啟 `MULTI_AGENT_DISPATCH_ENABLED` 後可執行 durable Root Orchestrator 測試並在 Designer 查看唯讀 root/child trace。CopilotKit 副駕（@copilotkit/react-* 1.62.3）經 `@ag-ui/client` 的 HttpAgent **直連** platform 的 `/api/copilot/agui`（`agents__unsafe_dev_only`,POC 接法,無 Node 橋接）;品質門禁：oxlint + vite build + Vitest logic tests 42 個 + Playwright UI regression tests 35 個（合計 77 個 unit tests）+ 4 個 evidence tests。
- **工作流**:Python 3.12+ + uv、LangGraph（工作流圖）+ FastAPI、Skill 引擎層（P1–P4 節點、@node/@tool 裝飾器、YAML 編譯器）、安全 Skill/Tool catalogs、langchain-openai（經 LiteLLM 閘道連 LLM）、httpx（呼叫 backend 服務）、Langfuse callback（env 開關）;測試用 pytest 1135 個（3 個 skipped）。

> .NET 後端需 .NET SDK 10 以上才能建置（`dotnet --version` 應顯示 `10.x`）。

## 前置需求

- **Docker**（跑 LiteLLM + Langfuse + mem0）
- **.NET SDK 10**（任一發行版）— 安裝:`winget install Microsoft.DotNet.SDK.10`（Windows）、`brew install dotnet@10`（macOS）或官網（Linux）
- **Node.js 20+**（跑前端 Vite dev server）
- **Python 3.12+**、**uv**（僅模式 C／Lite 需要:本機起 workflow 服務與其依賴；安裝 uv 見 https://astral.sh/uv）

## 設定金鑰

在 `infra/` 建立 `.env`（docker compose 會自動讀取,已 gitignore）:

```dotenv
OPENAI_API_KEY=sk-你的金鑰
```

> 真正的 OpenAI 金鑰由 **LiteLLM 保管**,App 只持有虛擬金鑰 `sk-1234`。Langfuse 的金鑰 / 帳密 / 加密金鑰也可放 `.env`,開發用預設值即可,正式環境請更換。

## 啟動

前後端**對稱**——可各自跑主機（開發)或進容器,基礎設施與核心服務一律用 compose 起。前端 `:5173` / 平台閘道 `:8080`,**請擇一,別同時跑**。

| 模式              | 啟動腳本      | infra    | 核心服務（:8002） | 平台閘道（:8080）             | 前端（:5173）             | 適用              |
| ----------------- | ------------- | -------- | ----------------- | ----------------------------- | ------------------------- | ----------------- |
| **A 開發**（預設) | `start-infra` | 容器     | 容器              | 主機 `dotnet run`（可 debug） | 主機 `npm run dev`（HMR） | 日常開發          |
| **B 全容器**      | `start-full`  | 容器     | 容器              | 容器                          | 容器（nginx）             | 展示 / 部署       |
| **C 無容器**      | `start-lite`  | 本機服務 | 主機 in-memory    | 主機 `dotnet run`（可 debug） | 主機 `npm run dev`（HMR） | 前端開發 / 快速迭代 |

### 快速啟動腳本（跨平台）

`scripts/` 內的腳本會自動檢查依賴、再起對應服務（從哪個目錄執行都可以）:

```bash
# Linux / macOS（首次需 chmod +x scripts/*.sh）
./scripts/start-infra.sh     # 模式 A：只起 docker infra（前端後端本機 run）
./scripts/start-full.sh      # 模式 B：全容器化
./scripts/start-lite.sh      # 模式 C：無容器（四服務本機平行啟動）
```
```powershell
# Windows（PowerShell）
.\scripts\start-infra.ps1
.\scripts\start-full.ps1
.\scripts\start-lite.ps1     # 模式 C：無容器
```

**停止服務:**
- 模式 A / B：`cd infra && docker compose --profile full down`
- 模式 C：`.\scripts\stop-lite.ps1` (Windows) 或 `./scripts/stop-lite.sh` (Linux/macOS)

### Copilot Shared Core black-box smoke 驗證

服務已啟動後，可執行這支可重跑的 black-box smoke companion；它不會刪除容器或 volume：

```powershell
pwsh -File scripts\verify-copilot-shared-core.ps1
```

參數如下：

- `-BaseUrl http://localhost:8080`：platform base URL（預設值）。
- `-ProxyBaseUrl http://localhost:5173`：Vite/nginx proxy base URL（預設值）。
- `-Rebuild`：以 `CHAT_MODEL=mock-gpt` 啟動／重建 full mode，再開始驗證。
- `-IncludeMem0Outage`：執行 C-06；此案例會暫停 mem0，並在 `finally` 保證重啟，因此僅在可接受短暫 mem0 中斷時使用。

這支腳本提供 C cases 的部分外部 smoke 訊號，**不是** C-01～C-08 的充分 release 證據。它無法檢查模型實際輸入、session 中的重複訊息計數、tool call/result 配對完整性、mem0 是否真的寫入，也無法量測逐 chunk 到達時間；`-Rebuild` 使用 `mock-gpt`，因此不能驗證 skill routing。

release evidence 仍須保留 [plans/copilot-shared-core/04-acceptance-test.md](plans/copilot-shared-core/04-acceptance-test.md) 定義的 C gates：`C-03`/`C-04`/`C-05`/`C-07`/`C-08` 必須由具名 integration tests，加上 `e2e-verifier` 的真服務 trace／必要時手動 browser proxy 檢查完成；routing 驗證必須使用真實模型，不能以 `mock-gpt` 取代。此 smoke script 只能作為它們的補充。

### Copilot Shared Core release evidence（目前 blocked/failed）

release-evidence harness 已執行，但**尚未達成 release sign-off**。最新 Deterministic lane 的 `E-01`、`E-02`、`E-03`、`E-06` 已全部通過；E-06 以完整 content SSE events（不是 TCP chunks 或其他 AG-UI protocol events）驗證 nginx/Vite 未緩衝。Real-model lane 的 `E-04` 未在時限內由 mem0 取回 authenticated fact；`E-05` 第一輪最終回覆未包含 fixture 數字，且該 failure bundle 尚未保存可稽核的 routing capture。不得以 deterministic PASS 取代這兩個 gate 發布。

```powershell
# 首次或 evidence image / profile 有變更時加 -BuildEvidenceProfile；兩個 lane 都會輸出 bundle。
pwsh -File scripts\verify-copilot-shared-core-evidence.ps1 `
  -Lane Deterministic -StartEvidenceProfile -BuildEvidenceProfile

# 真模型 lane 使用固定 snapshot aliases；不可改用 mock-gpt。
pwsh -File scripts\verify-copilot-shared-core-evidence.ps1 `
  -Lane RealModel -StartEvidenceProfile -BuildEvidenceProfile
```

每次輸出位於被 Git 忽略的 `artifacts/copilot-shared-core/<UTC-run-id>/`。最新 deterministic PASS bundle 為 `artifacts/copilot-shared-core/20260721T083532847Z-f8054fd1/`；real-model failure bundle 為 `artifacts/copilot-shared-core/20260721T073849789Z-5000aa13/`。內容僅含 HMAC 投影與結果，不應加入版本控制。完整狀態與修復門檻見 [release evidence plan](plans/copilot-shared-core/05-release-evidence-plan.md)。

### 模式 A:在主機補起平台與前端

`start-infra` 起基礎設施與核心服務容器；平台閘道與前端在主機跑（享熱重載 / HMR）:

```bash
# 平台閘道（platform/）
dotnet build                              # 首次
dotnet run --project src/Platform.Web      # :8080

# 前端（frontend/）
npm install && npm run dev                # :5173（Vite proxy /api → :8080）
```

核心服務已在容器內執行於 `:8002`（無需本機起）。

> 若 `dotnet` 命令不認得，請確認 .NET SDK 10 已安裝（`dotnet --version`）。

### 模式 B:備註

`start-full` ＝ `docker compose --profile full up -d --build`。全容器模式：核心服務 `:8002` 與平台閘道 `:8080` 容器均發佈到主機；**容器內前端 nginx 反代** `/api` **走 compose service DNS** `http://platform:8080` **（不經主機，於容器間直連）**，單一 nginx 配置同時支援兩種模式（模式 A 時 Vite dev proxy 連 host :8080，模式 B 時 nginx 連 service DNS）。改碼後重建:`docker compose --profile full up -d --build platform`（或 `backend` 或 `frontend`）。

### 各服務位置

| 服務                    | 位置                                                                                       |
| ----------------------- | ------------------------------------------------------------------------------------------ |
| 前端聊天 UI             | http://localhost:5173                                                                      |
| 平台閘道 API            | http://localhost:8080                                                                      |
| 核心服務 API            | http://localhost:8002                                                                      |
| Langfuse UI             | http://localhost:3000 （帳號見 `.env` 的 `LANGFUSE_INIT_USER_*`）                          |
| LiteLLM                 | http://localhost:4000                                                                      |
| RabbitMQ 管理 UI        | http://localhost:15672 （帳號 `app`,密碼 env `RABBITMQ_PASSWORD` 預設 `app-dev-password`） |
| mem0（長期記憶）        | http://localhost:8000 （API 文件 `/docs`）                                                 |
| 工作流服務（LangGraph） | http://localhost:8001 （主機埠 8001 → 容器 8000,避開 mem0）                                |
| 應用資料庫（pgvector）  | localhost:5433 （使用者 `postgres`,密碼 `postgres`,資料庫 `springaitest`）                 |

打幾次 `/api/chat` 後,到 Langfuse UI 即可看到 trace、token 與成本。

## API

```bash
# 送出訊息（非串流）。userId / conversationId 皆選填：
#   userId         → 匿名短期對話的 caller key；已登入時由 JWT 身分覆蓋。
#   conversationId → 短期記憶分群（哪一「串」對話），省略則退回以 userId 分群。
curl -X POST http://localhost:8080/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "你好，今天天氣如何？", "userId": "alice", "conversationId": "c-001"}'

# 串流（SSE，逐字回傳）
curl -N -X POST http://localhost:8080/api/chat/stream \
  -H "Content-Type: application/json" \
  -d '{"message": "你好，今天天氣如何？", "userId": "alice", "conversationId": "c-001"}'

# 查詢歷史
curl http://localhost:8080/api/chat/history
```

> Windows PowerShell 內 JSON 的雙引號需轉義:`-d '{\"message\": \"...\"}'`。
> 前端登入後，`userId` 不受 request body 信任，長期記憶與持久化均由 JWT `{tenant}:{user}` 歸戶；`conversationId` 存 localStorage 並隨請求帶上。同一登入身分跨對話穩定；按「新建對話」時換新 UUID，讓短期記憶重新開始。匿名 `/api/chat*` 只保留短期對話，不 recall/remember mem0，也不持久化。

## 後端:兩層服務架構

```
platform/Platform.sln                     前置閘道層
├── Platform.Service                      業務層：Agent Framework、mem0 長期記憶、滑動視窗短期記憶、SkillService、BackendClient 代理
└── Platform.Web                          展示層 + 啟動：ChatController、SkillController、NodeController、JWT 驗證、SSE 串流、例外處理

backend/Backend.sln                       核心服務層
└── Backend.Api/                          單一項目：Auth、Conversations、Files、Retrieval、Analysis、Skills、Config（feature folders）
                                         Dapper 2.x + Npgsql 直連 appdb postgresql（含 skill / skill_revision 表）

workflow                                  Skill 引擎層
└── app/engine/                           @node、@tool 裝飾器、YAML 編譯器、沙箱執行器
```

依賴方向：`platform` 無本地資料層，聊天歷史、文件、組態全經 BackendClient 代理呼叫 `backend` 的 API；`backend` 獨立執行核心業務邏輯與資料持久化。**文件處理非同步化**：`platform` 收到文件上傳後發佈訊息到 RabbitMQ 佇列 `documents.process` 並立即回 `202 Accepted`，由 `backend` 的消費者負責切塊、嵌入與向量存儲。

> 所有 C# namespace 都在 `Platform.*`（平台）或 `Backend.*`（核心）之下。依賴注入分別在 `Platform.Web/Program.cs` 與 `Backend.Api/Program.cs` 統一設定。

### 各模組測試

| 層級 | 模組        | 測試套                | 方式                                                                                          |
| ---- | ----------- | --------------------- | --------------------------------------------------------------------------------------------- |
| 平台 | Service     | `ChatServiceTests`    | xUnit + 手寫 fake HttpMessageHandler（BackendClient 代理行為）                                |
| 平台 | Web         | `ChatControllerTests` | xUnit + WebApplicationFactory（整合測試）                                                     |
| 核心 | Backend.Api | 694 個                | xUnit + 手寫 fake repository、test fixture；內含 Auth、Retrieval、Config、Chunking、Agent Registry 等單元測試（所有 PostgreSQL 相依測試現在都跑，0 個 skipped） |

## 可觀測性架構（LiteLLM 閘道 + Langfuse）

App 不直連 OpenAI,而是透過 **LiteLLM 閘道**;觀測走兩條路匯入自架的 **Langfuse**:

```
ChatService (.NET HttpClient, OpenAI 協定)
   │  BaseAddress → LiteLLM         ┌─ .NET OpenTelemetry（App 視角：Activity、延遲、business span）
   │                                │        │ OTLP → http://localhost:3000/api/public/otel
   ▼                                │        ▼
LiteLLM :4000 ──success_callback──► Langfuse :3000 ◄────────────────┘
   │  路由到真正供應商                （閘道視角：token、成本、prompt/completion）
   ▼
OpenAI / ...（未來可加 Claude 等）
```

- **LiteLLM → Langfuse**:閘道層自動記錄 token、成本、輸入輸出（零改碼）。
- **.NET → Langfuse**:App 層的 trace 與延遲，經 OpenTelemetry + OTLP 匯出（串流與非串流路徑皆有業務 Activity span）。
- **workflow → Langfuse**:workflow 服務同樣經 LiteLLM 閘道呼叫 LLM，並額外掛 Langfuse LangChain callback，一併被 Langfuse 記錄。

## 長期記憶（mem0）

跨對話、跨 session 的長期記憶由 **mem0**（官方 `mem0-api-server`）提供,後端經 REST 呼叫:

```
已登入使用者訊息（由 JWT `{tenant}:{user}` 歸戶）
   │
   ▼  ① 呼叫 LLM 前：POST mem0 /search → 取回相關記憶,塞進 system prompt
共用 pipeline（ChatContextProvider）─────────────────────────► LLM 回覆
   │  ② 回覆後：POST mem0 /memories → mem0 用 LLM 自行抽取事實並存入
   ▼
mem0 :8000 ──(LLM 抽取 + embedding 都走 LiteLLM :4000)──► 向量存進 postgres/pgvector
```

- **接點是共用的 pipeline，不是單一 controller**:`ChatContextProvider`（呼叫 LLM 前 `recall`，記憶注入 Instructions）與 `ChatTurnRecorder`（完整回覆後 `remember`）掛在兩條聊天鏈路共用的 Agent Framework pipeline 上，已登入的 `ChatView`（`/api/chat*`）與 CopilotKit 副駕（`/api/copilot/agui`）都會用到。匿名 `ChatView` 僅保留短期連續性，既不 recall/remember mem0，也不持久化。mem0 的 best-effort 由 pipeline 邊界保證：任何 `IMem0Client` 例外都會記錄並降級為空 recall/no-op remember，**聊天主流程不受影響**。
- **記憶按 JWT 身分分群**:登入後以 `{tenant}:{user}` 分群，request body 的 `userId` 不能指定或冒用其他人的長期記憶（見 [API](#api)）。
- **不另接 OpenAI / 不另加向量庫**:mem0 的 LLM 與 embedder 都指向現有 LiteLLM,向量存進現有 postgres 的 pgvector(故 `postgres` 映像用 `pgvector/pgvector:pg17`)。embedding 模型需在 `litellm-config.yaml` 註冊(`text-embedding-3-small`)。

> **官方映像的兩個坑**(已由 `infra/mem0.Dockerfile` 與 `scripts/ensure-mem0-db.*` 處理,啟動腳本會自動套用):
> 1. `mem0-api-server` 內建 psycopg 卻缺 `libpq`,一啟動就 crash → 薄封裝映像補上 `psycopg[binary]`。
> 2. mem0 需要一個關聯狀態庫 `mem0_app`(它不會自建)、且換 pgvector 映像後沿用舊 volume 會 collation 版本不符 → `ensure-mem0-db` 在 postgres 起來後補建與刷新。

## 短期記憶（對話脈絡）

同一次對話的近期來回，由 **Microsoft Agent Framework 的 session store**（`InMemoryChatHistoryProvider` + `SlidingWindowCompactionStrategy`）提供，和 mem0 互補:

- **兩種記憶各司其職**:mem0 存「跨 session 的長期事實」(走 system prompt 注入);短期記憶存「這一串對話的近期訊息」(直接把前幾輪對話補回 prompt),讓 LLM 認得「上一句」。
- **框架實作、兩條聊天鏈路共用**:per-conversation 保留最近 20 則訊息、以「輪」為單位裁切（不會拆散工具呼叫/結果配對）。AG-UI 用 JWT `{tenant}:{user}` strict isolation（任一 claim 缺失即 fail-closed）；ChatView 的登入 session key 已前綴同一身分、匿名則保留自己的短期 conversation key。AG-UI 重送完整 message 陣列時先按 ID 去重，assistant ID 重建時以保守 fingerprint 避免重複歷史。
- **記憶體儲存、重啟即清**:短期記憶在應用程序記憶體中，重啟平台閘道後對話脈絡歸零屬預期。若要跨重啟保留完整對話，須改為在資料庫（如 backend appdb）持久化。

## 認證與多租戶

專案支持 JWT 認證（由核心服務 backend 簽發）與租戶隔離。使用者、文件、聊天歷史與檢索都以租戶代碼隔離，資料均存儲在 appdb（postgres）。

### 種子帳號與租戶

| 帳號    | 密碼        | 角色  | 租戶代碼 |
| ------- | ----------- | ----- | -------- |
| admin-a | password123 | ADMIN | demo-a   |
| user-a  | password123 | USER  | demo-a   |
| user-b  | password123 | USER  | demo-b   |

種子租戶的邀請碼：`demo-a` → `demo-a-invite`、`demo-b` → `demo-b-invite`。

自助註冊需要提供該租戶的邀請碼（防止任意加入他人租戶讀取其文件）：

```bash
curl -X POST http://localhost:8080/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"newuser","password":"password123","tenantCode":"demo-a","inviteCode":"demo-a-invite"}'
```

邀請碼錯誤回 `403 邀請碼無效`；租戶代碼不存在回 404；帳號重複回 409。

### 認證流程

1. **登入 —— 取得 JWT token**

```bash
curl -X POST http://localhost:8080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"user-a","password":"password123"}'
# 回應：{"token":"eyJhbGc...","username":"user-a","role":"USER","tenantCode":"demo-a"}
```

2. **使用 token 呼叫受保護的端點** —— 在 `Authorization` header 帶上 `Bearer <token>`:

```bash
curl -H "Authorization: Bearer eyJhbGc..." http://localhost:8080/api/documents
```

### 受保護的 API

| 端點                            | 方法 | 權限  | 說明                                   |
| ------------------------------- | ---- | ----- | -------------------------------------- |
| `/api/documents`                | POST | USER  | 新增文件（切塊 + 嵌入,存入租戶向量庫） |
| `/api/documents`                | GET  | USER  | 列出文件（租戶隔離）                   |
| `/api/skills`                   | GET  | USER  | 列出技能（內建 + 自訂，含 `required_role`） |
| `/api/skills/{name}/invoke`     | POST | USER  | 執行技能（rag-qa、summarize 等）      |
| `/api/skills/validate`          | POST | ADMIN | 驗證 YAML 技能定義（語法 + schema）   |

```bash
# 新增文件（非同步）—— 立即回 202，含文件 id、status 為 "processing"
curl -X POST http://localhost:8080/api/documents \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"title":"我的文件","text":"這是文件內容"}'
# 回應：{"id":"doc-uuid","title":"我的文件","status":"processing"}

# 輪詢文件清單，檢查處理進度
curl -H "Authorization: Bearer <token>" http://localhost:8080/api/documents
# 回應：[{"id":"doc-uuid","title":"我的文件","status":"ready"},{...}]
# 待 status 變 "ready" 或 "failed" 表示完成

# 列出所有技能
curl -H "Authorization: Bearer <token>" http://localhost:8080/api/skills

# 執行 rag-qa 技能（RAG 問答）
curl -X POST http://localhost:8080/api/skills/rag-qa/invoke \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"input":{"question":"文件裡提到什麼？"}}'

# 執行 analyze-report 技能（分析報告,ADMIN only）
curl -X POST http://localhost:8080/api/skills/analyze-report/invoke \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"input":{"topic":"市場分析"}}'
```

## 技能（LangGraph Skill 引擎）

### 內建技能

| 技能             | 說明                        | 權限  |
| ---------------- | --------------------------- | ----- |
| `summarize`      | 文本摘要                    | USER  |
| `triage`         | 問題分流分類                | USER  |
| `rag-qa`         | RAG 問答（向量檢索 + 生成） | USER  |
| `kb-query`       | 向量知識庫檢索              | USER  |
| `analyze-report` | 生成分析報告                | ADMIN |

> 上述技能透過 `/api/skills/{name}/invoke` 端點執行，平台端一律要求 JWT 認證（見「認證與多租戶」），
> 需帶 `Authorization: Bearer <token>`；`summarize`、`triage`、`rag-qa`、`kb-query` 任一登入使用者（USER）
> 皆可呼叫，`analyze-report` 則限 ADMIN。

### 技能（Skill）管理與執行

前端「Chat」視圖中，聊天會自動根據內容選擇合適的技能；另有「Config」視圖新增三個分頁：

1. **執行技能** — 手動執行上述內建技能或自訂技能（需登入）。
2. **技能管理** [ADMIN] — CRUD 自訂技能；上傳 YAML 定義；自動語法驗證（呼叫 LangGraph 引擎）。
3. **節點目錄** — 瀏覽所有註冊節點的輸入/輸出契約與工具庫。

新增 API 端點（皆需 JWT 認證 + `X-Internal-Token`）：

- `GET /api/skills` — 列出所有技能（內建 + 自訂）。
- `POST /api/skills` — 新增技能（含上傳 YAML）。
- `PUT /api/skills/{name}` — 更新技能（驗證後存新 revision）。
- `DELETE /api/skills/{name}` — 軟刪除技能（disabled=false）。
- `POST /api/skills/{name}/invoke` — 執行技能，回傳 `{skill, output}`。
- `POST /api/skills/validate` — 驗證 YAML 語法（寫入前檢查）。
- `GET /api/nodes` — 節點目錄（契約清單）。

```bash
# 執行 summarize 技能（文本摘要）
curl -X POST http://localhost:8080/api/skills/summarize/invoke \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"input":{"text":"..."}}'

# 執行 triage 技能（問題分流）
curl -X POST http://localhost:8080/api/skills/triage/invoke \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"input":{"question":"退款要多久？"}}'

# 驗證 Skill YAML 定義
curl -X POST http://localhost:8080/api/skills/validate \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"yaml":"name: my_skill\ninput_schema: {...}"}'
```

> 平台收到請求後會轉呼叫 LangGraph 引擎服務（`:8001`）；若要略過平台直接測技能本身，也可以打 `http://localhost:8001/skills/{name}/invoke`
> （但工作流服務本身走的是服務間認證 `X-Internal-Token`／租戶標頭，不是 JWT，見 `workflow/README.md`）。

### 注意事項

- **前端認證**:登入頁面支持使用種子帳號或自助註冊（需租戶邀請碼），登入後存 token 於 localStorage，所有 API 請求皆帶 `Authorization: Bearer <token>` header。`apiFetch` 與 CopilotKit `HttpAgent` 的 custom fetch 都必須把任何 401 送入同一全域 logout，清空 session 與 chat localStorage 後回登入頁。
- **向量嵌入**:開發預設 `WORKFLOW_EMBEDDINGS_PROVIDER=fake`，無需 OpenAI 額度，可驗證整條 RAG 鏈。正式環境改為 `openai` 並確保 `OPENAI_API_KEY` 有效。
- **多租戶隔離**:文件與向量檢索結果都按租戶代碼隔離，使用者只能看到同租戶的資料。
- **appdb 資料庫**:核心服務 backend 連接的生產資料庫（`localhost:5433`），存儲使用者、租戶、聊天歷史、文件、向量與組態；重啟後資料持久保留。

## 後續工作

串流回覆已完成：`ChatService.StreamChatAsync()` 以 `IAsyncEnumerable<string>` 逐字推送，經 SSE 端點 `POST /api/chat/stream` 推給前端逐字顯示；前端 `useChat` 走此端點（非串流 `POST /api/chat` 仍保留供 curl 等客戶端）。

記憶已完備：長期（mem0）與短期（滑動視窗）皆已接上（見上）。剩下的缺口：

1. **聊天歷史補回使用者訊息**：`GET /api/chat/history` 目前只返回 `reply`；若要重建完整對話需補 `prompt` 欄位（存在 backend appdb 的 conversations 表中）。
2. **短期記憶跨重啟保留**（可選）：目前短期記憶為 in-memory，重啟平台即清；要跨重啟保留可在 backend appdb 新增 `chat_memory` 表並改為從 DB 讀取最近 N 條訊息。

## D6 Agent Chat canary

D6 is delivered behind fail-closed configuration. Set `AGENT_CHAT_ENABLED=true` in Platform, Backend, and Workflow, and set Platform `AGENT_CHAT_TENANT_ALLOWLIST` to the exact tenant codes being migrated. Eligible authenticated users are resolved through the tenant runtime binding; Chat and AG-UI then use the same durable, revision-pinned Root Orchestrator path. Anonymous users, tenants outside the allowlist, and tenants resolved as `legacy` remain on the existing shared-core path. An explicitly requested unavailable Orchestrator fails closed.

Before adding a tenant, publish and pin its Root Workflow, Worker Agent, independent read-only Verifier Agent, and tenant runtime binding. Canary one tenant at a time and retain the D6 verifier bundle. To roll back, first disable Platform `AGENT_CHAT_ENABLED` or remove the tenant from the allowlist, then disable the Workflow and Backend flags. Routing changes immediately; durable run and event records remain audit-retained.

Verification snapshot: Backend 479/479 with PostgreSQL coverage; Platform Service 335/335 and Web 269/269; Workflow 862 passed/2 skipped; Frontend 50/50 plus build/lint; deterministic D6 verifier 13/13 PASS. RealModel bundle `20260725T111722672Z-90c9119f` passed E-04 and E-05. The seventh independent review completed with zero findings.

## D7 approved write tools and operations

D7 is delivered behind the independent, fail-closed `AGENT_WRITE_TOOLS_ENABLED=true` flag in Platform, Backend, and Workflow. Its only shipped writable capability is the tenant-allowlisted `runtime.write_evidence`; enabling the flag does not make arbitrary registered tools writable. A gated write enters durable `waiting_approval`. An authenticated qualifying approver may inspect `GET /api/runs/{runId}/approvals` and submit an idempotent approval or rejection at `POST /api/runs/{runId}/approvals/{approvalId}/approve|reject`. Approval is not generic resume and does not require `workflow.manage`; Backend rechecks tenant, role, expiry, action/checkpoint identity, replay, and separation of duties.

The approved effect uses a Backend-issued one-time identity. Backend atomically writes its evidence and outbox record, while Workflow only claims and executes that durable effect, so retries and recovery cannot duplicate it. Operators with the exact `workflow.manage` capability use `/api/admin/operations` for regression-gate evidence, audited override, future-roots-only rollout, aggregate metrics, version comparison, and legacy inventory. A failed regression gate blocks rollout absent an audited override. To roll back, disable the write flag to block new work and change the future runtime binding/revision as needed; active runs remain pinned and all approval/effect/outbox records remain audit-retained.

Verification snapshot: Backend 499 total (498 passed, 1 skipped) plus isolated PostgreSQL D7 coverage; Platform Service 335/335 and Web 277/277; Workflow 867 passed/2 skipped; Frontend 52/52 plus build/lint; hybrid cross-service D7 verifier 9/9 PASS; independent review `FINDINGS: 0`.
