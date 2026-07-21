# workflow —— LangGraph 工作流服務

「AI 數據檢索和分析平台」的資料面：以 **FastAPI + LangGraph** 承載向量檢索（RAG）與分析類工作流，
並負責服務間認證、角色權限邊界、多租戶資料隔離；透過內建的註冊表機制可隨時擴充工作流，
對外開放 HTTP 端點，供 platform 端（.NET，env 驅動）同步觸發並取回結果。

LLM 呼叫一律經由既有的 LiteLLM 閘道，觀測性沿用 LiteLLM → Langfuse，額外支援以環境變數開關的
Langfuse LangChain callback，讓「圖的執行過程」本身也能在 Langfuse 上形成 trace。

## 核心能力

- **向量檢索（RAG）**：`/documents` 負責文件的切塊、嵌入、儲存；`app/nodes/retrieve.py` 提供共用的
  LangGraph 檢索節點，各工作流可直接掛用。
- **分析工作流**：`rag_qa`（檢索增強問答）、`analyze_report`（主題分析報告，管理員限定）。
- **服務間認證**：所有 `/skills*`、`/nodes`、`/documents*` 端點都要求 `X-Internal-Token` 與 platform 端（.NET）共享的密鑰吻合。
- **角色權限邊界**：每個工作流宣告 `required_role`（`USER` 或 `ADMIN`），由 `X-User-Role` 標頭核對。
- **多租戶隔離**：所有文件與檢索操作皆以 `X-Tenant-Id` 為第一層邊界，租戶之間資料互不可見。

## API

### 認證與租戶 context

除了 `GET /health`，所有端點都必須帶上以下標頭：

| Header | 說明 | 缺失／不符時的行為 |
| --- | --- | --- |
| `X-Internal-Token` | 與 platform 端（.NET）共享的內部密鑰（`INTERNAL_API_TOKEN`） | **401** `{"detail": {"error": "unauthorized", "message": "..."}}` |
| `X-Tenant-Id` | 呼叫者所屬的租戶代碼（如 `demo-a`） | 缺少 → **400** `{"detail": {"error": "missing_context", "message": "..."}}` |
| `X-User-Id` | 呼叫者的使用者名稱 | 可省略，僅供追蹤用途，不影響授權判斷 |
| `X-User-Role` | `USER` 或 `ADMIN` | 缺少 → **400**（同上） |

### 技能（Skill）與節點

| 方法 | 路徑 | 說明 | 請求 | 回應 |
| --- | --- | --- | --- | --- |
| GET | `/health` | 健康檢查（無需任何標頭） | 無 | `{"status": "ok"}` |
| GET | `/skills` | 列出所有已註冊技能 | 無 | `[{"name", "description", "required_role", "input_schema", "output_schema"}, ...]`（依 name 排序） |
| POST | `/skills/{name}/invoke` | 同步觸發指定技能並取回結果 | `{"input": {...}}` | `{"skill": "...", "output": {...最終 state...}}` |
| POST | `/skills/validate` | 驗證 YAML 技能定義（語法 + schema 檢查） | `{"yaml": "..."}` | `{"valid": true}` 或 `{"error": "..."}` |
| GET | `/nodes` | 列出所有已註冊節點及其 I/O 契約 | 無 | `[{"name", "reads", "writes", "deps", ...}, ...]` |

`input` 裡的 `tenant_id`／`user_id`／`role` 為保留鍵，一律會被忽略並由伺服器依標頭覆寫，
避免呼叫端夾帶假的租戶／使用者資訊破壞隔離邊界。

`POST /skills/{name}/invoke` 的驗證順序與對應錯誤：

1. 技能是否存在 → 否則 **404** `{"error": "skill_not_found", "message": "...", "skills": [...]}`
2. 呼叫者角色是否足夠（`ADMIN` 可執行一切，`USER` 只能執行 `required_role=USER` 的技能）
   → 否則 **403** `{"error": "skill_forbidden", "message": "..."}`
3. `input` 是否符合該技能宣告的 `input_schema`（若有宣告）
   → 否則 **422** `{"error": "skill_input_invalid", "message": "..."}`
4. 編譯並執行 YAML 定義的技能圖，套用逾時保護（預設 `WORKFLOW_TIMEOUT_SECONDS`，技能可自行覆蓋）
   → 逾時 **504** `{"error": "skill_timeout", "message": "..."}`；
     其他例外 **500** `{"error": "skill_execution_failed", "message": "..."}`

### 文件（RAG 資料來源）

| 方法 | 路徑 | 說明 | 請求 | 回應 |
| --- | --- | --- | --- | --- |
| POST | `/documents` | 新增文件：切塊 → 嵌入 → 存入向量庫 | `{"title", "text"}`（皆須非空字串） | **201** `{"id", "title", "chunk_count"}` |
| GET | `/documents` | 列出呼叫者所在租戶的所有文件 | 無 | **200** `[{"id", "title", "chunk_count", "created_at"}, ...]`（僅本租戶） |
| DELETE | `/documents/{id}` | 刪除指定文件 | 無 | **204**；非本租戶或不存在 → **404** `{"error": "document_not_found", "message": "..."}` |

### 內建技能一覽

| 名稱 | 說明 | 權限 | input |
| --- | --- | --- | --- |
| `summarize` | 將輸入文字做三句以內的摘要（線性流程） | USER | `{"text": "..."}` |
| `triage` | 依問題複雜度分流回答（條件分支流程） | USER | `{"question": "..."}` |
| `rag_qa` | 檢索增強問答：以租戶內文件回答問題並附引用 | USER | `{"question": "..."}`（非空） |
| `kb_query` | 向量知識庫檢索：查詢租戶文件內容 | USER | `{"query": "..."}`（非空） |
| `analyze_report` | 檢索租戶文件並產出主題分析報告 | **ADMIN** | `{"topic": "..."}`（非空） |

`rag_qa` 與 `analyze_report` 的狀態都含 `docs`（檢索到的片段列表，每筆有 `document_id`／`title`／
`content`／`score`）。查無任何片段時：`rag_qa` 直接給固定文案「在你的租戶資料中找不到相關內容，
請先上傳文件。」且 `citations` 為空陣列；`analyze_report` 的 `insights` 固定為「（無資料）」——
兩者皆不會在查無資料時呼叫 LLM。`rag_qa` 的 `citations` 一律由程式從 `docs` 產生（非 LLM 輸出），
避免引用內容與實際檢索結果不一致。

## 本機開發

需求：Python 3.12+、[uv](https://docs.astral.sh/uv/)。

```bash
# 安裝相依套件（含 dev group）
uv sync

# 跑測試（全程使用 stub LLM + fake 嵌入 + 記憶體向量庫，不會打真的網路）
uv run pytest

# 本機啟動（建議先指定 mock-gpt，避免需要真的 OpenAI 額度；
# LiteLLM 網關需先啟動於 LLM_BASE_URL 指向的位址）
$env:LLM_MODEL="mock-gpt"
# （用 8001 與 compose 的主機埠一致，也避開 mem0 容器佔用的 :8000）
uv run uvicorn app.main:app --port 8001
```

啟動後可用以下指令驗證（PowerShell）：

```powershell
curl http://localhost:8001/health

$headers = @{
  "X-Internal-Token" = "internal-dev-token"
  "X-Tenant-Id"      = "demo-a"
  "X-User-Id"        = "alice"
  "X-User-Role"      = "USER"
}

curl -Headers $headers http://localhost:8001/skills

curl -Method Post -Headers ($headers + @{"Content-Type"="application/json"}) `
  -Body '{"input":{"question":"退款政策是什麼？"}}' `
  http://localhost:8001/skills/rag_qa/invoke
```

> 文件的新增/列表/刪除已移到 backend 核心服務（經 platform 的 `/api/documents`）；
> 本服務不再直連資料庫，檢索節點是以 HTTP 呼叫 backend 的 `/api/retrieval/search`。
> 要先有文件可檢索，請走 platform（:8080）的 documents API 建立。

## 環境變數

| 變數 | 預設值 | 說明 |
| --- | --- | --- |
| `LLM_BASE_URL` | `http://localhost:4000` | LiteLLM 閘道位址 |
| `LLM_API_KEY` | `sk-1234` | LiteLLM 虛擬金鑰 |
| `LLM_MODEL` | `gpt-4o-mini` | 實際使用的模型名稱；本機測試可用 `mock-gpt`，無需真的 OpenAI 額度 |
| `LANGFUSE_ENABLED` | `false` | 是否開啟 Langfuse LangChain callback（圖執行過程的追蹤） |
| `LANGFUSE_PUBLIC_KEY` | 無 | 開啟追蹤時，由 langfuse SDK 直接讀取的公開金鑰 |
| `LANGFUSE_SECRET_KEY` | 無 | 開啟追蹤時，由 langfuse SDK 直接讀取的私密金鑰 |
| `LANGFUSE_HOST` | 無 | 開啟追蹤時，由 langfuse SDK 直接讀取的 Langfuse 伺服器位址 |
| `INTERNAL_API_TOKEN` | `internal-dev-token` | 服務間共享密鑰；入站呼叫必須帶相同值的 `X-Internal-Token`，出站呼叫 backend 時也用它 |
| `BACKEND_BASE_URL` | `http://localhost:8002` | backend 核心服務位址；檢索節點經它做向量搜尋（嵌入計算也在 backend） |
| `RETRIEVAL_TOP_K` | `4` | 檢索節點預設取回的片段數（工作流可自行覆蓋） |
| `WORKFLOW_TIMEOUT_SECONDS` | `120` | 工作流執行逾時秒數（可由個別工作流的 `timeout_seconds` 覆蓋） |

> `LANGFUSE_PUBLIC_KEY` / `LANGFUSE_SECRET_KEY` / `LANGFUSE_HOST` 並非由本專案的 `Settings` 讀取，
> 而是 langfuse SDK 依其慣例直接從環境變數取得，因此不會出現在 `app/settings.py` 裡。

## 如何新增一個有權限邊界的技能（YAML 方式）

內建與自訂技能均以 **YAML 定義**、由引擎編譯為 LangGraph 圖執行。編寫 YAML 技能定義時：

1. 在 `app/skills/` 底下新增一個 YAML 檔（例如 `app/skills/my_skill.yaml`）。
2. 定義技能的 YAML 結構，包括：
   - `name` — 技能識別符
   - `description` — 簡短說明
   - `required_role` — 所需角色（`USER` 或 `ADMIN`，預設 `USER`）
   - `input_schema` — 輸入 schema（Pydantic 格式或 JSON schema）
   - `output_schema` — 輸出 schema
   - `nodes` — 技能包含的節點清單（節點引用與邊的拓樸圖）

3. 若需檢索租戶文件，在 YAML 中引用內建節點 `retrieve`。技能編譯器會依據節點契約自動串接資料流。

4. 完成 YAML 定義後，透過 `POST /api/skills` 端點上傳（或直接放在 `app/skills/` 使引擎在啟動時載入）。

5. 驗證技能語法，使用 `POST /api/skills/validate` 端點檢查 YAML 與 schema 是否有效。

6. 重新啟動服務（或由後端重新載入），`GET /skills` 應該就能看到新項目（含 `required_role`），
   `POST /skills/{name}/invoke` 即可呼叫；權限不足會收到 403，input 不符 schema 會收到 422。

> 自訂技能由 `app/skills/custom.py` 負責從 backend 載入並合併到技能目錄，無需在此服務端手動 import。
