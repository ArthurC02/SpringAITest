# springaitest

Spring Boot 3.5 + Spring AI（OpenAI）範例專案,採 **monorepo**:後端為 Maven 多模組三層式架構,前端為 React + Vite SPA,基礎設施（LiteLLM 閘道 + Langfuse 可觀測性）以 Docker Compose 管理。

## 專案結構

```
SpringAITest/
├── scripts/                    跨平台啟動腳本（.sh 給 Linux/macOS、.ps1 給 Windows）
│   ├── start-infra.sh / .ps1   模式 A：只起基礎設施
│   └── start-full.sh  / .ps1   模式 B：全容器（infra + 前端 + 後端）
├── backend/                    後端：Spring Boot 多模組（三層式）
│   ├── pom.xml                 springaitest-parent（聚合 + 版本管理 spring-ai-bom）
│   ├── mvnw / mvnw.cmd / .mvn  Maven Wrapper（免各自安裝 Maven）
│   ├── Dockerfile              選用：僅 --profile full 用到
│   ├── springaitest-domain     資料層：entity、repository 介面
│   ├── springaitest-service    業務層：service、dto、呼叫 LLM
│   └── springaitest-web        展示層 + 啟動模組：controller、例外處理、設定檔
├── frontend/                   前端：React 19 + Vite + TypeScript（串流聊天 UI）
│   ├── vite.config.ts          dev 時把 /api proxy 到 :8080（免 CORS）
│   ├── Dockerfile / nginx.conf 正式：多階段 build → nginx 靜態檔 + /api 反代（SSE 關緩衝）
│   └── src/                    api/chat.ts、hooks/useChat.ts、components/、App.tsx
└── infra/
    ├── docker-compose.yml      LiteLLM + Langfuse（自架 v3）；frontend / backend 為 profile「full」
    ├── litellm-config.yaml     LiteLLM 路由（含免額度測試用的 mock-gpt）
    ├── .env.example            金鑰範本
    └── .env                    真正的金鑰（自建，已 gitignore）
```

## 技術棧

- **後端**:JDK 25 (LTS)、Spring Boot 3.5.15、Spring AI 1.1.8（`spring-ai-starter-model-openai`）、Spring Data JPA + H2、Bean Validation、Maven Wrapper。
- **前端**:React 19 + Vite + TypeScript;dev 時 Vite proxy `/api` → `:8080`,瀏覽器同源免 CORS。

> 後端 `<java.version>` 為 25,需 JDK 25 以上才能建置（`java -version` 應顯示 `25.x`）。

## 前置需求

- **Docker**（跑 LiteLLM + Langfuse）
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

打幾次 `/api/chat` 後,到 Langfuse UI 即可看到 trace、token 與成本。

## API

```bash
# 送出訊息（非串流）
curl -X POST http://localhost:8080/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "用一句話介紹 Spring Boot"}'

# 串流（SSE，逐字回傳）
curl -N -X POST http://localhost:8080/api/chat/stream \
  -H "Content-Type: application/json" \
  -d '{"message": "用一句話介紹 Spring Boot"}'

# 查詢歷史
curl http://localhost:8080/api/chat/history
```

> Windows PowerShell 內 JSON 的雙引號需轉義:`-d '{\"message\": \"...\"}'`。

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

## 後續工作

串流回覆已完成:`ChatServiceImpl.streamChat()` 以 `ChatClient.stream()` 回 `Flux<String>`,經 SSE 端點 `POST /api/chat/stream` 推給前端逐字顯示;前端 `useChat` 即走此端點（非串流的 `POST /api/chat` 仍保留供 curl / 其他客戶端使用）。

要做成完整的 Agent 對話體驗,後端還有兩個缺口:

1. **多輪記憶 / sessionId**:目前每次只送單句,LLM 收不到前文;加入 Spring AI `ChatMemory` 與 `conversationId`。
2. **history 補回使用者訊息**:`ChatResponse` 目前只含 `reply`;DTO 補上 `prompt` 才能重建完整對話。
