# springaitest

Spring Boot 3.5 + Spring AI（OpenAI）範例專案,採 **monorepo**:後端為 Maven 多模組三層式架構,前端為 React + Vite SPA,基礎設施（LiteLLM 閘道 + Langfuse 可觀測性 + mem0 長期記憶）以 Docker Compose 管理。

## 專案結構

```
SpringAITest/
├── scripts/                    跨平台啟動腳本（.sh 給 Linux/macOS、.ps1 給 Windows）
│   ├── start-infra.sh / .ps1   模式 A：只起基礎設施
│   ├── start-full.sh  / .ps1   模式 B：全容器（infra + 前端 + 後端）
│   └── ensure-mem0-db.* 　　　  備妥 mem0 的 postgres 前置（建 mem0_app、刷新 collation）；由上面兩腳本自動呼叫
├── backend/                    後端：Spring Boot 多模組（三層式）
│   ├── pom.xml                 springaitest-parent（聚合 + 版本管理 spring-ai-bom）
│   ├── mvnw / mvnw.cmd / .mvn  Maven Wrapper（免各自安裝 Maven）
│   ├── Dockerfile              選用：僅 --profile full 用到
│   ├── springaitest-domain     資料層：entity、repository 介面
│   ├── springaitest-service    業務層：service、dto、呼叫 LLM、記憶（mem0 長期 + ChatMemory 短期）
│   └── springaitest-web        展示層 + 啟動模組：controller、例外處理、設定檔
├── frontend/                   前端：React 19 + Vite + TypeScript（串流聊天 UI）
│   ├── vite.config.ts          dev 時把 /api proxy 到 :8080（免 CORS）
│   ├── Dockerfile / nginx.conf 正式：多階段 build → nginx 靜態檔 + /api 反代（SSE 關緩衝）
│   └── src/                    api/chat.ts、hooks/useChat.ts、components/、App.tsx
└── infra/
    ├── docker-compose.yml      LiteLLM + Langfuse（自架 v3）+ mem0；frontend / backend 為 profile「full」
    ├── mem0.Dockerfile         mem0 官方映像的薄封裝（補上缺的 libpq，見下方長期記憶章節）
    ├── litellm-config.yaml     LiteLLM 路由（chat 模型、mem0 用的 embedding、免額度測試用的 mock-gpt）
    ├── .env.example            金鑰範本
    └── .env                    真正的金鑰（自建，已 gitignore）
```

## 技術棧

- **後端**:JDK 25 (LTS)、Spring Boot 3.5.15、Spring AI 1.1.8（`spring-ai-starter-model-openai`）、Spring Data JPA + H2、Bean Validation、Maven Wrapper。
- **前端**:React 19 + Vite + TypeScript;dev 時 Vite proxy `/api` → `:8080`,瀏覽器同源免 CORS。

> 後端 `<java.version>` 為 25,需 JDK 25 以上才能建置（`java -version` 應顯示 `25.x`）。

## 前置需求

- **Docker**（跑 LiteLLM + Langfuse + mem0）
- **JDK 25**（任一 OpenJDK 發行版;Maven 用內附 Wrapper）— 安裝:`winget install Microsoft.OpenJDK.25`（Windows）、`sdk install java 25-tem` 或 `brew install openjdk@25`（macOS / Linux）
- **Node.js 20+**（跑前端 Vite dev server）

## 設定金鑰

在 `infra/` 建立 `.env`（docker compose 會自動讀取,已 gitignore）:

```dotenv
OPENAI_API_KEY=sk-你的金鑰
```

> 真正的 OpenAI 金鑰由 **LiteLLM 保管**,App 只持有虛擬金鑰 `sk-1234`。Langfuse 的金鑰 / 帳密 / 加密金鑰也可放 `.env`,開發用預設值即可,正式環境請更換。

## 啟動

前後端**對稱**——可各自跑主機（開發)或進容器,基礎設施一律用 compose 起。兩種模式都佔 `:5173` / `:8080`,**請擇一,別同時跑**。

| 模式 | 啟動腳本 | infra | 前端 | 後端 | 適用 |
|---|---|---|---|---|---|
| **A 開發**（預設) | `start-infra` | 容器 | 主機 `npm run dev`（HMR）| 主機 `mvnw`（可 debug）| 日常開發 |
| **B 全容器** | `start-full` | 容器 | 容器（nginx）| 容器 | 展示 / 部署 |

### 快速啟動腳本（跨平台）

`scripts/` 內的腳本會自動切到 `infra/`、檢查 `.env`、再起對應服務（從哪個目錄執行都可以）:

```bash
# Linux / macOS（首次需 chmod +x scripts/*.sh）
./scripts/start-infra.sh     # 模式 A：只起 infra
./scripts/start-full.sh      # 模式 B：全容器
```
```powershell
# Windows（PowerShell）
.\scripts\start-infra.ps1
.\scripts\start-full.ps1
```

> 停止:`cd infra && docker compose --profile full down`。

### 模式 A:在主機補起前後端

`start-infra` 只起基礎設施;前後端自己跑（享熱重載 / HMR）:

```bash
# 後端（backend/）— Windows 用 .\mvnw.cmd
./mvnw clean install                          # 首次
./mvnw -pl springaitest-web spring-boot:run   # :8080

# 前端（frontend/）
npm install && npm run dev                    # :5173（Vite proxy /api → :8080）
```

> **JAVA_HOME** 若指向舊版會報「JAVA_HOME is not defined correctly」,請設成 JDK 25 路徑（如 `C:\Program Files\Microsoft\jdk-25.0.3.9-hotspot`）。macOS / Linux 若 `./mvnw` 不能執行:`chmod +x mvnw`。

### 模式 B:備註

`start-full` ＝ `docker compose --profile full up -d --build`。後端容器把 `:8080` 發佈到主機,前端 nginx 經 `host.docker.internal:8080` 反代 `/api`（設定不必改,單一 nginx 設定通吃兩種模式）。改碼後重建:`docker compose --profile full up -d --build backend`（或 `frontend`）。

### 各服務位置

| 服務 | 位置 |
|---|---|
| 前端聊天 UI | http://localhost:5173 |
| 後端 API | http://localhost:8080 |
| H2 主控台 | http://localhost:8080/h2-console （JDBC `jdbc:h2:mem:springaitest`,使用者 `sa`,無密碼）|
| Langfuse UI | http://localhost:3000 （帳號見 `.env` 的 `LANGFUSE_INIT_USER_*`）|
| LiteLLM | http://localhost:4000 |
| mem0（長期記憶）| http://localhost:8000 （API 文件 `/docs`）|

打幾次 `/api/chat` 後,到 Langfuse UI 即可看到 trace、token 與成本。

## API

```bash
# 送出訊息（非串流）。userId / conversationId 皆選填：
#   userId         → mem0 長期記憶分群（哪個「人」），省略歸 default 使用者。
#   conversationId → 短期記憶分群（哪一「串」對話），省略則退回以 userId 分群。
curl -X POST http://localhost:8080/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "用一句話介紹 Spring Boot", "userId": "alice", "conversationId": "c-001"}'

# 串流（SSE，逐字回傳）
curl -N -X POST http://localhost:8080/api/chat/stream \
  -H "Content-Type: application/json" \
  -d '{"message": "用一句話介紹 Spring Boot", "userId": "alice", "conversationId": "c-001"}'

# 查詢歷史
curl http://localhost:8080/api/chat/history
```

> Windows PowerShell 內 JSON 的雙引號需轉義:`-d '{\"message\": \"...\"}'`。
> 前端會自動為每個瀏覽器產生 `userId` 與 `conversationId`（皆存 localStorage）並隨請求帶上。`userId` 跨對話穩定;`conversationId` 在按「清除」時換新,讓短期記憶重新開始。

## 後端:多模組結構與依賴方向

```
springaitest-parent (pom)        聚合 + 版本管理（spring-ai-bom）
├── springaitest-domain          資料層            ← 不依賴任何內部模組
├── springaitest-service         業務層            依賴 → domain
└── springaitest-web             展示層 + 啟動      依賴 → service（傳遞帶入 domain）
```

依賴在各 `pom.xml` **單向宣告**,由 Maven 在編譯期強制:`web` 沒宣告 `domain`,Controller 連 import Repository 都做不到;`service` 沒宣告 `web`,Controller 無法滲入業務層;`domain` 可獨立編譯與測試。

> 所有 package 都在 `com.example.springaitest.*` 之下,故元件 / Entity / Repository 掃描自動跨模組生效,啟動類不需額外 `@ComponentScan` / `@EntityScan`。

### 各模組測試

| 模組 | 測試 | 方式 |
|---|---|---|
| domain | `ConversationRepositoryTest` | `@DataJpaTest` + H2 |
| service | `ChatServiceImplTest` | Mockito 純單元測試（含串流）|
| web | `ChatControllerTest`、`SpringaitestApplicationTests` | `@WebMvcTest` / `@SpringBootTest` |

## 可觀測性架構（LiteLLM 閘道 + Langfuse）

App 不直連 OpenAI,而是透過 **LiteLLM 閘道**;觀測走兩條路匯入自架的 **Langfuse**:

```
ChatServiceImpl (ChatClient, OpenAI 協定)
   │  base-url → LiteLLM            ┌─ Spring AI OTel（App 視角：span、延遲、business trace）
   │                                │        │ OTLP → http://localhost:3000/api/public/otel
   ▼                                │        ▼
LiteLLM :4000 ──success_callback──► Langfuse :3000 ◄────────────────┘
   │  路由到真正供應商                （閘道視角：token、成本、prompt/completion）
   ▼
OpenAI / ...（未來可加 Claude 等）
```

- **LiteLLM → Langfuse**:閘道層自動記錄 token、成本、輸入輸出（零改碼）。
- **Spring AI → Langfuse**:App 層的 trace 與延遲,經 Micrometer Tracing + OTLP 匯出（串流與非串流路徑皆有業務 span）。

## 長期記憶（mem0）

跨對話、跨 session 的長期記憶由 **mem0**（官方 `mem0-api-server`）提供,後端經 REST 呼叫:

```
使用者訊息（帶 userId）
   │
   ▼  ① 呼叫 LLM 前：POST mem0 /search → 取回相關記憶,塞進 system prompt
ChatServiceImpl ──────────────────────────────────────────► LLM 回覆
   │  ② 回覆後：POST mem0 /memories → mem0 用 LLM 自行抽取事實並存入
   ▼
mem0 :8000 ──(LLM 抽取 + embedding 都走 LiteLLM :4000)──► 向量存進 postgres/pgvector
```

- **接點只在 `ChatServiceImpl`**:`Mem0Client`（`RestClient`,零新依賴）在呼叫 LLM 前 `recall`、回覆後 `remember`;blocking 與 stream 兩條路徑共用。mem0 掛掉時 `recall` 回空、`remember` 無動作,**聊天主流程不受影響**。
- **記憶按 `userId` 分群**:前端每個瀏覽器自帶一組 `userId`(見 [API](#api));查無記憶時行為與未整合前完全相同。
- **不另接 OpenAI / 不另加向量庫**:mem0 的 LLM 與 embedder 都指向現有 LiteLLM,向量存進現有 postgres 的 pgvector(故 `postgres` 映像用 `pgvector/pgvector:pg17`)。embedding 模型需在 `litellm-config.yaml` 註冊(`text-embedding-3-small`)。

> **官方映像的兩個坑**(已由 `infra/mem0.Dockerfile` 與 `scripts/ensure-mem0-db.*` 處理,啟動腳本會自動套用):
> 1. `mem0-api-server` 內建 psycopg 卻缺 `libpq`,一啟動就 crash → 薄封裝映像補上 `psycopg[binary]`。
> 2. mem0 需要一個關聯狀態庫 `mem0_app`(它不會自建)、且換 pgvector 映像後沿用舊 volume 會 collation 版本不符 → `ensure-mem0-db` 在 postgres 起來後補建與刷新。

## 短期記憶（對話脈絡）

同一次對話的近期來回,由 Spring AI 內建的 **`MessageChatMemoryAdvisor`** 提供,和 mem0 互補:

- **兩種記憶各司其職**:mem0 存「跨 session 的長期事實」(走 system prompt 注入);`ChatMemory` 存「這一串對話的近期訊息」(直接把前幾輪對話補回 prompt),讓 LLM 認得「上一句」。
- **零額外依賴 / 零 bean 設定**:`ChatMemory` bean 由 Spring AI auto-config 提供(預設 `MessageWindowChatMemory`,in-memory、保留最近 20 則);`ChatServiceImpl` 只把 advisor 掛成 default advisor,並依 `conversationId` 分群(見 [API](#api))。
- **記憶體儲存、重啟即清**:與 H2 一致(見上,重啟後對話脈絡歸零屬預期)。要跨重啟保留,換成 `JdbcChatMemoryRepository` 即可,不改業務碼。

## 後續工作

串流回覆已完成:`ChatServiceImpl.streamChat()` 以 `ChatClient.stream()` 回 `Flux<String>`,經 SSE 端點 `POST /api/chat/stream` 推給前端逐字顯示;前端 `useChat` 即走此端點（非串流的 `POST /api/chat` 仍保留供 curl / 其他客戶端使用）。

記憶已完備:長期（mem0）與短期（`ChatMemory`）皆已接上（見上）。剩下的缺口:

1. **history 補回使用者訊息**:`ChatResponse` 目前只含 `reply`;DTO 補上 `prompt` 才能重建完整對話。
2. **短期記憶跨重啟保留**（選用）:目前 `ChatMemory` 為 in-memory,重啟即清;要保留改用 `JdbcChatMemoryRepository`。
