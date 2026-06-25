# springaitest

Spring Boot 3.5 + Spring AI(OpenAI)範例專案,採用 **Maven 多模組** 落實三層式架構。

## 技術棧

- **JDK 25 (LTS)** — 任一 OpenJDK 發行版皆可（Temurin / Microsoft / Corretto / Zulu …）
- Spring Boot 3.5.15
- Spring AI 1.1.8（`spring-ai-starter-model-openai`）
- Spring Data JPA + H2 記憶體資料庫
- Bean Validation、Maven Wrapper（跨平台,團隊不需各自安裝 Maven）

> 專案 `<java.version>` 設為 25,需 JDK 25 以上才能建置。確認:`java -version`（應顯示 `25.x`）。

## 多模組結構與依賴方向

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

## 設定金鑰

把真正的 OpenAI 金鑰填進專案根目錄的 **`.env`**(docker compose 會讀取,已被 gitignore):

```dotenv
OPENAI_API_KEY=sk-你的金鑰
```

> Langfuse 的專案金鑰、登入帳密、加密金鑰也都在 `.env`,開發用預設值即可,正式環境請更換。

## 前置需求

- **Docker**（跑 Langfuse + LiteLLM；`docker compose` 各平台指令相同）
- **JDK 25**（任一 OpenJDK 發行版即可；Maven 不用裝,用內附 Wrapper）

安裝 JDK 25 參考:
- macOS / Linux:`sdk install java 25-tem`（SDKMAN）或 `brew install openjdk@25`
- Windows:`winget install Microsoft.OpenJDK.25`

## 啟動

順序:先起基礎設施,再起 App。

**1) 啟動 LiteLLM + Langfuse 全套（postgres / clickhouse / redis / minio）**

```bash
docker compose up -d
```

**2) 建置並啟動 Spring 應用程式（在主機上跑）**

macOS / Linux:
```bash
./mvnw clean install
./mvnw -pl springaitest-web spring-boot:run
```

Windows（PowerShell）:
```powershell
.\mvnw.cmd clean install
.\mvnw.cmd -pl springaitest-web spring-boot:run
```

> **macOS / Linux 首次取得專案**若 `./mvnw` 不能執行,補上權限:`chmod +x mvnw`。
> 初始化 git 的人執行一次 `git update-index --chmod=+x mvnw`,之後 clone 即自帶執行權限。

各服務位置:
- API:`http://localhost:8080`
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
