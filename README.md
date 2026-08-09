# springaitest

ASP.NET Core 10 + Python LangGraph + React 19 **monorepo**：平台層（.NET 閘道 + Agent Framework）、核心服務層（.NET Dapper + PostgreSQL）、前端（React SPA）、工作流層（Python 引擎），基礎設施經 Docker Compose 管理。

## 使用者操作手冊

給非技術使用者的圖文操作手冊，包含登入、聊天、上傳文件、管理員專區與常見問題，含實機截圖與步驟標註：  
**[線上閱讀](https://claude.ai/code/artifact/6281e2f3-3822-4bcf-9491-dd1766a83bb6)**

## 架構與服務

| 區域 | 技術 | 埠號 | 職責 | 文件 |
|------|------|------|------|------|
| **platform** | ASP.NET Core 10 前置閘道 | `:8080` | JWT 驗證、SSE 聊天、Agent Framework、mem0 proxy | [文件](platform/AGENTS.md) |
| **backend** | ASP.NET Core 10 核心服務 | `:8002` | 驗證、檔案處理、向量檢索、Agent CRUD、規則 | [文件](backend/AGENTS.md) |
| **frontend** | React 19 + Vite SPA | `:5173` | 聊天、文件管理、Admin 工作區 | [文件](frontend/AGENTS.md) |
| **workflow** | Python LangGraph 引擎 | `:8001` | Skill 編譯、多 Agent 協調、工作流執行 | [文件](workflow/AGENTS.md) |

基礎設施：LiteLLM（`:4000`）、Langfuse（`:3000`）、mem0（`:8000`）、PostgreSQL、RabbitMQ。

## 前置需求

- **Docker** — LiteLLM、Langfuse、mem0、PostgreSQL
- **.NET SDK 10** — `dotnet build`、`dotnet run`
- **Node.js 20.19+** 或 **22.12+** — 前端開發
- **Python 3.12+** 和 **uv** — Lite 模式下本機起 workflow

## 快速開始

| 模式 | 啟動 | infra | backend | platform | frontend |
|------|------|-------|---------|----------|----------|
| **A 開發** | `./scripts/start-infra.ps1` | 容器 | 容器 | 主機 `dotnet run` | 主機 `npm run dev` |
| **B 全容器** | `./scripts/start-full.ps1` | 容器 | 容器 | 容器 | 容器 nginx |
| **C 無容器** | `./scripts/start-lite.ps1` | 本機 | 本機 | 主機 `dotnet run` | 主機 `npm run dev` |

Windows 用 `.ps1`（需 PowerShell 7+），Linux/macOS 用同名 `.sh`（首次需 `chmod +x scripts/*.sh`）。

模式 A 預設不重建容器 image（多數人只在主機上改 platform/frontend）；backend/ 或 workflow/ 原始碼有異動時，加 `-Build`（`./scripts/start-infra.ps1 -Build`）或 `--build`（`./scripts/start-infra.sh --build`）才會重建。

**停止：** 模式 A/B → `cd infra && docker compose --profile full down` | 模式 C → `./scripts/stop-lite.ps1`

## 設定金鑰

複製 `infra/.env.example` → `infra/.env` 並填入真正的 OpenAI 金鑰。真正的金鑰由 **LiteLLM 保管**，App 只用虛擬金鑰 `sk-1234`。

非 Development 環境會自動拒絕開發預設值；正式環境須另行提供 JWT、內部 token、資料庫與 RabbitMQ 憑證。

## 各服務位置

| 服務 | URL |
|------|-----|
| 前端聊天 | http://localhost:5173 |
| Platform API | http://localhost:8080 |
| Backend API | http://localhost:8002 |
| Langfuse | http://localhost:3000（帳號見 `.env`） |
| LiteLLM | http://localhost:4000 |
| mem0 | http://localhost:8000（`/docs` 查閱 API） |
| RabbitMQ 管理 | http://localhost:15672（app / app-dev-password） |
| PostgreSQL | localhost:5433（postgres / postgres） |

## 認證與種子帳號

| 帳號 | 密碼 | 角色 | 租戶 |
|------|------|------|------|
| admin-a | password123 | ADMIN | demo-a |
| user-a | password123 | USER | demo-a |
| user-b | password123 | USER | demo-b |

邀請碼：`demo-a-invite`（demo-a 租戶）、`demo-b-invite`（demo-b 租戶）。

自助註冊：`POST /api/auth/register` + tenantCode + inviteCode。

## 功能開通旗標

旗標預設 false（fail-closed），需在 `.env` 設為 `true` 才開啟：

| 旗標 | 開啟後功能 | 權限需求 |
|------|-----------|---------|
| `AGENT_BUILDER_ENABLED` | Agent 平台 Agents 標籤 | ADMIN |
| `WORKFLOW_DESIGNER_ENABLED` | Workflow Designer | `workflow.manage` |
| `MULTI_AGENT_DISPATCH_ENABLED` | Root Orchestrator 執行 | `workflow.manage` |
| `AGENT_CHAT_ENABLED` | Chat 路由至 Orchestrator | (需 allowlist) |
| `AGENT_WRITE_TOOLS_ENABLED` | 寫入工具 + 批准工作流 | 批准者無需 `workflow.manage` |
| `RUN_DISCOVERY_ENABLED` | 執行總覽 | `workflow.manage` |
| `AGENT_TRIGGERS_ENABLED` | 排程觸發管理 | `workflow.manage` |

## 文件導覽

**頂層指引：**
- [Repository AGENTS.md](AGENTS.md) — monorepo 地圖、跨服務契約、編碼風格
- [docs/coding-standards.md](docs/coding-standards.md) — Karpathy 四原則、開發心法
- [docs/cross-service-contracts.md](docs/cross-service-contracts.md) — 完整跨服務契約（SSE 格式、API 錯誤、JWT）
- [docs/agent-platform-contracts.md](docs/agent-platform-contracts.md) — D1–D7 Agent 平台契約完整細節

**區域文件：**
- [platform/AGENTS.md](platform/AGENTS.md) — Gateway、可觀測性、mem0、ChatService、Agent Framework 整合、D6 路由、D7 批准
- [backend/AGENTS.md](backend/AGENTS.md) — JWT 簽發、Dapper + 向量檢索、RabbitMQ 消費、Agents/Rules CRUD、隔離
- [frontend/AGENTS.md](frontend/AGENTS.md) — React 19 約定、Config 分頁、Workflow Designer UI、證據測試
- [workflow/AGENTS.md](workflow/AGENTS.md) — Skill 編譯、node/tool registry、LangGraph、隔離執行

**發佈驗證：**
- [plans/copilot-shared-core/05-release-evidence-plan.md](plans/copilot-shared-core/05-release-evidence-plan.md) — Copilot Shared Core sign-off 狀態、E1–E8 證據清單

## 安全提醒

絕不提交 `.env` 或 `JWT_PRIVATE_KEY_PEM_BASE64`（已 gitignore）。未明確設為 Development 的環境會在啟動時拒絕空白或公開開發值的憑證。詳見 [infra/AGENTS.md](infra/AGENTS.md) 環境變數清冊。
