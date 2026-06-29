# springaitest

Spring Boot 3.5 + Spring AI(OpenAI)範例專案,採 **monorepo**:後端為 Maven 多模組三層式架構,前端為 React + Vite SPA,基礎設施(LiteLLM 閘道 + Langfuse)用 Docker Compose。

## 專案結構（monorepo）

```
SpringAITest/
├── backend/                    後端：Spring Boot 多模組（三層式）
│   ├── pom.xml                 springaitest-parent（聚合 + 版本管理 spring-ai-bom）
│   ├── mvnw / mvnw.cmd / .mvn  Maven Wrapper（團隊不需各自安裝 Maven）
│   ├── springaitest-domain     資料層：entity、repository 介面
│   ├── springaitest-service    業務層：service、dto、呼叫 LLM
│   └── springaitest-web        展示層 + 啟動模組：controller、例外處理、設定檔
│
├── frontend/                   前端：React 19 + Vite + TypeScript（串流聊天 UI）
│   ├── vite.config.ts          dev 時把 /api proxy 到 :8080（免 CORS）
│   ├── Dockerfile              正式：多階段 build → nginx 提供靜態檔 + 代理 /api
│   ├── nginx.conf              nginx 設定（SPA fallback + /api 反代，SSE 關緩衝）
│   └── src/
│       ├── api/chat.ts         與後端 /api/chat、/api/chat/stream 對接（含 SSE 解析）
│       ├── hooks/useChat.ts    對話狀態 + 串流逐字填入 + localStorage
│       ├── components/         Markdown、ChatBubble、MessageList、Composer
│       └── App.tsx             聊天介面
│
└── infra/                      基礎設施（docker compose）
    ├── docker-compose.yml      frontend（nginx）+ LiteLLM 閘道 + Langfuse（自架 v3）
    ├── litellm-config.yaml     LiteLLM 路由設定（含免額度測試用的 mock-gpt）
    ├── .env.example            金鑰範本（複製成 .env 後填入）
    └── .env                    真正的金鑰（自行建立，已被 gitignore）
```

## 技術棧

**後端**
- **JDK 25 (LTS)** — 任一 OpenJDK 發行版皆可（Temurin / Microsoft / Corretto / Zulu …）
- Spring Boot 3.5.15
- Spring AI 1.1.8（`spring-ai-starter-model-openai`）
- Spring Data JPA + H2 記憶體資料庫
- Bean Validation、Maven Wrapper

**前端**
- React 19 + Vite + TypeScript
- 開發時用 Vite proxy 轉發 `/api` → `http://localhost:8080`，瀏覽器同源、不需 CORS

> 後端 `<java.version>` 設為 25,需 JDK 25 以上才能建置。確認:`java -version`（應顯示 `25.x`）。

## 後端：多模組結構與依賴方向

```
springaitest-parent (pom)              聚合 + 版本管理（spring-ai-bom）
│
├── springaitest-domain                資料層：entity、repository 介面
│      com.example.springaitest.domain        ← 不依賴任何內部模組
│
├── springaitest-service               業務層：service、dto、呼叫 LLM
│      com.example.springaitest.service       依賴 → domain
│
└── springaitest-web                   展示層 + 啟動模組：controller、例外處理、設定檔
       com.example.springaitest(.controller…) 依賴 → service（傳遞帶入 domain）
```

依賴在各模組的 `pom.xml` **單向宣告**,由 Maven 在編譯期強制:

- `web` 沒有宣告 `domain`,Controller 連 import Repository 都 import 不到。
- `service` 沒有宣告 `web`,Controller 無法滲入業務層。
- `domain` 不依賴任何人,可獨立編譯與測試。

> 所有 package 都掛在 `com.example.springaitest.*` 之下,因此 Spring Boot 的
> 元件掃描、Entity 掃描、Repository 掃描會自動跨模組生效,啟動類不需額外
> `@ComponentScan` / `@EntityScan` 設定。

## 各模組測試

| 模組 | 測試 | 方式 |
|---|---|---|
| domain | `ConversationRepositoryTest` | `@DataJpaTest` + H2（測試啟動類 `TestDomainApplication`） |
| service | `ChatServiceImplTest` | Mockito 純單元測試 |
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
- **Spring AI → Langfuse**:App 層的 trace 與延遲,經 Micrometer Tracing + OTLP 匯出。

真正的 OpenAI 金鑰由 LiteLLM 保管,App 只持有虛擬金鑰(`sk-1234`)。

## 前置需求

- **Docker**（跑 Langfuse + LiteLLM；`docker compose` 各平台指令相同）
- **JDK 25**（任一 OpenJDK 發行版即可；Maven 不用裝,用內附 Wrapper）
- **Node.js 20+**（跑前端 Vite dev server）

安裝 JDK 25 參考:
- macOS / Linux:`sdk install java 25-tem`（SDKMAN）或 `brew install openjdk@25`
- Windows:`winget install Microsoft.OpenJDK.25`

## 設定金鑰

在 **`infra/`** 目錄建立 **`.env`**(docker compose 會讀取,已被 gitignore):

```dotenv
OPENAI_API_KEY=sk-你的金鑰
```

> Langfuse 的專案金鑰、登入帳密、加密金鑰也都可放 `.env`,開發用預設值即可,正式環境請更換。

## 啟動

順序:先起基礎設施 → 再起後端 → 最後起前端。

**1) 啟動全套基礎設施 + 前端（在 `infra/` 目錄）**

```bash
cd infra
docker compose up -d
```

這會起 **前端（nginx，:5173）**、LiteLLM（:4000）、Langfuse（:3000）等。首次會建置前端映像，需幾分鐘。

**2) 建置並啟動後端 Spring 應用程式（在 `backend/` 目錄，跑在主機上）**

macOS / Linux:
```bash
cd backend
./mvnw clean install
./mvnw -pl springaitest-web spring-boot:run
```

Windows（PowerShell）:
```powershell
cd backend
.\mvnw.cmd clean install
.\mvnw.cmd -pl springaitest-web spring-boot:run
```

> **JAVA_HOME**:`mvnw` 會依 `JAVA_HOME` 找 JDK。若指向不存在的舊版會報
> 「JAVA_HOME is not defined correctly」,請設成 JDK 25 安裝路徑
> （例如 Windows:`C:\Program Files\Microsoft\jdk-25.0.3.9-hotspot`）。
>
> **macOS / Linux 首次取得專案**若 `./mvnw` 不能執行,補上權限:`chmod +x mvnw`。

**3)（選用）前端開發伺服器（在 `frontend/` 目錄，需要熱更新時）**

步驟 1 的 `docker compose` 已用 nginx 跑起「正式建置版」前端於 :5173。
若要邊改邊看（HMR），改用 Vite dev server——它同樣用 :5173，請先停掉前端容器避免衝突：

```bash
docker compose -f ../infra/docker-compose.yml stop frontend   # 釋放 :5173
cd frontend
npm install
npm run dev
```

兩種模式都開 `http://localhost:5173`：容器版由 nginx 反代 `/api`，dev 版由 Vite proxy `/api`（見 `frontend/vite.config.ts`），都連到主機後端 :8080、免 CORS。

各服務位置:
- **前端聊天 UI**:`http://localhost:5173`
- 後端 API:`http://localhost:8080`
- H2 主控台:`http://localhost:8080/h2-console`(JDBC URL:`jdbc:h2:mem:springaitest`,使用者 `sa`,無密碼)
- **Langfuse UI**:`http://localhost:3000`(帳號見 `.env` 的 `LANGFUSE_INIT_USER_*`)
- LiteLLM:`http://localhost:4000`

打幾次 `/api/chat` 後,到 Langfuse UI 即可看到 trace、token 與成本。

## API

送出訊息 —— macOS / Linux:
```bash
curl -X POST http://localhost:8080/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "用一句話介紹 Spring Boot"}'
```

送出訊息 —— Windows（PowerShell）:
```powershell
curl -X POST http://localhost:8080/api/chat `
  -H "Content-Type: application/json" `
  -d '{\"message\": \"用一句話介紹 Spring Boot\"}'
```

查詢歷史（各平台相同）:
```bash
curl http://localhost:8080/api/chat/history
```

## 後續工作（前端對接 Agent 對話）

目前前端對接的是既有的**非串流、單輪**端點。要做成完整的 Agent 對話體驗,後端還有三個缺口待補:

1. **串流回覆**:`ChatServiceImpl` 目前用 `.call().content()` 一次回傳;改用 `ChatClient.stream()` 回 `Flux<String>`，新增 SSE 端點（`text/event-stream`），前端即可逐字顯示。
2. **多輪記憶 / sessionId**:目前每次只送單句,LLM 收不到前文;加入 Spring AI `ChatMemory` 與 `conversationId`。
3. **history 補回使用者訊息**:`ChatResponse` 目前只含 `reply`,歷史無法重建完整對話;DTO 補上 `prompt`。
