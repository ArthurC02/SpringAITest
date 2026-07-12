# workflow —— LangGraph 工作流服務

「AI 數據檢索和分析平台」的資料面：以 **FastAPI + LangGraph** 承載向量檢索（RAG）與分析類工作流，
並負責服務間認證、角色權限邊界、多租戶資料隔離；透過內建的註冊表機制可隨時擴充工作流，
對外開放 HTTP 端點，供 Spring 後端（`backend/`）同步觸發並取回結果。

LLM 呼叫一律經由既有的 LiteLLM 閘道，觀測性沿用 LiteLLM → Langfuse，額外支援以環境變數開關的
Langfuse LangChain callback，讓「圖的執行過程」本身也能在 Langfuse 上形成 trace。

## 核心能力

- **向量檢索（RAG）**：`/documents` 負責文件的切塊、嵌入、儲存；`app/nodes/retrieve.py` 提供共用的
  LangGraph 檢索節點，各工作流可直接掛用。
- **分析工作流**：`rag_qa`（檢索增強問答）、`analyze_report`（主題分析報告，管理員限定）。
- **服務間認證**：所有 `/workflows*`、`/documents*` 端點都要求 `X-Internal-Token` 與 Spring 端共享的密鑰吻合。
- **角色權限邊界**：每個工作流宣告 `required_role`（`USER` 或 `ADMIN`），由 `X-User-Role` 標頭核對。
- **多租戶隔離**：所有文件與檢索操作皆以 `X-Tenant-Id` 為第一層邊界，租戶之間資料互不可見。

## API

### 認證與租戶 context

除了 `GET /health`，所有端點都必須帶上以下標頭：

| Header | 說明 | 缺失／不符時的行為 |
| --- | --- | --- |
| `X-Internal-Token` | 與 Spring 端共享的內部密鑰（`INTERNAL_API_TOKEN`） | **401** `{"detail": {"error": "unauthorized", "message": "..."}}` |
| `X-Tenant-Id` | 呼叫者所屬的租戶代碼（如 `demo-a`） | 缺少 → **400** `{"detail": {"error": "missing_context", "message": "..."}}` |
| `X-User-Id` | 呼叫者的使用者名稱 | 可省略，僅供追蹤用途，不影響授權判斷 |
| `X-User-Role` | `USER` 或 `ADMIN` | 缺少 → **400**（同上） |

### 工作流

| 方法 | 路徑 | 說明 | 請求 | 回應 |
| --- | --- | --- | --- | --- |
| GET | `/health` | 健康檢查（無需任何標頭） | 無 | `{"status": "ok"}` |
| GET | `/workflows` | 列出所有已註冊工作流 | 無 | `[{"name", "description", "required_role"}, ...]`（依 name 排序） |
| POST | `/workflows/{name}/invoke` | 同步觸發指定工作流並取回結果 | `{"input": {...}}` | `{"workflow": "...", "output": {...最終 state...}}` |

`input` 裡的 `tenant_id`／`user_id`／`role` 為保留鍵，一律會被忽略並由伺服器依標頭覆寫，
避免呼叫端夾帶假的租戶／使用者資訊破壞隔離邊界。

`POST /workflows/{name}/invoke` 的驗證順序與對應錯誤：

1. 工作流是否存在 → 否則 **404** `{"error": "workflow_not_found", "message": "...", "workflows": [...]}`
2. 呼叫者角色是否足夠（`ADMIN` 可執行一切，`USER` 只能執行 `required_role=USER` 的工作流）
   → 否則 **403** `{"error": "workflow_forbidden", "message": "..."}`
3. `input` 是否符合該工作流宣告的 `input_model`（若有宣告）
   → 否則 **422** `{"error": "workflow_input_invalid", "message": "..."}`
4. 執行圖，並套用逾時保護（預設 `WORKFLOW_TIMEOUT_SECONDS`，工作流可自行覆蓋）
   → 逾時 **504** `{"error": "workflow_timeout", "message": "..."}`；
     其他例外 **500** `{"error": "workflow_execution_failed", "message": "..."}`

### 文件（RAG 資料來源）

| 方法 | 路徑 | 說明 | 請求 | 回應 |
| --- | --- | --- | --- | --- |
| POST | `/documents` | 新增文件：切塊 → 嵌入 → 存入向量庫 | `{"title", "text"}`（皆須非空字串） | **201** `{"id", "title", "chunk_count"}` |
| GET | `/documents` | 列出呼叫者所在租戶的所有文件 | 無 | **200** `[{"id", "title", "chunk_count", "created_at"}, ...]`（僅本租戶） |
| DELETE | `/documents/{id}` | 刪除指定文件 | 無 | **204**；非本租戶或不存在 → **404** `{"error": "document_not_found", "message": "..."}` |

### 已註冊工作流一覽

| 名稱 | 說明 | 權限 | input |
| --- | --- | --- | --- |
| `summarize` | 將輸入文字做三句以內的摘要（線性流程） | USER | `{"text": "..."}` |
| `triage` | 依問題複雜度分流回答（條件分支流程） | USER | `{"question": "..."}` |
| `rag_qa` | 檢索增強問答：以租戶內文件回答問題並附引用 | USER | `{"question": "..."}`（非空） |
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

curl -Headers $headers http://localhost:8001/workflows

curl -Method Post -Headers ($headers + @{"Content-Type"="application/json"}) `
  -Body '{"input":{"question":"退款政策是什麼？"}}' `
  http://localhost:8001/workflows/rag_qa/invoke
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

## 如何新增一個有權限邊界的工作流

1. 在 `app/workflows/` 底下新增一個模組檔（例如 `app/workflows/my_flow.py`）。
2. 定義輸入驗證用的 `pydantic.BaseModel`（想維持寬鬆驗證則可省略，`input_model` 留空即可）：

   ```python
   from pydantic import BaseModel, Field

   class MyFlowInput(BaseModel):
       """my_flow 工作流的輸入 schema。"""
       topic: str = Field(min_length=1)
   ```

3. 定義工作流的 `State`（`TypedDict`）、各節點函式；若需要檢索租戶文件，直接掛用共用節點：

   ```python
   from app.nodes.retrieve import make_retrieve_node

   g.add_node("retrieve", make_retrieve_node(query_key="topic"))
   ```

4. 用 `@register(...)` 裝飾負責建圖並回傳已編譯圖的函式（通常命名為 `build`），
   並依需求宣告 `required_role`（預設 `"USER"`）、`input_model`、`timeout_seconds`：

   ```python
   from langgraph.graph import END, START, StateGraph
   from app.workflows.registry import register

   @register(
       "my_flow",
       description="說明這個工作流做什麼",
       required_role="ADMIN",       # 不需要角色邊界時可省略，預設 USER
       input_model=MyFlowInput,     # 不需要輸入驗證時可省略
       timeout_seconds=60,          # 不需要覆蓋全域逾時秒數時可省略
   )
   def build():
       g = StateGraph(MyFlowState)
       g.add_node("step", step)
       g.add_edge(START, "step")
       g.add_edge("step", END)
       return g.compile()
   ```

5. 到 `app/workflows/__init__.py` 補上一行 import，讓模組頂層的 `@register` 裝飾器在應用程式啟動時被執行：

   ```python
   from app.workflows import analyze_report, my_flow, rag_qa, summarize, triage  # noqa: F401
   ```

6. 重新啟動服務，`GET /workflows` 應該就能看到新項目（含 `required_role`），
   `POST /workflows/my_flow/invoke` 即可呼叫；權限不足會收到 403，input 不符 schema 會收到 422。

> 未來若工作流數量變多，可考慮改用 `pkgutil.iter_modules` 掃描 `app/workflows/` 底下所有模組並自動 import，
> 取代目前手動列舉的 `__init__.py`。
