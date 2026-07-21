# Release Evidence Closure Plan — Copilot Shared Core

> 狀態：**待執行**。本文件只規劃如何補齊 `04-acceptance-test.md` 已列出的 release evidence 缺口；既有 `verify-copilot-shared-core.ps1` 8/8 是必要的 black-box smoke，但不是本計畫的完成條件。

## 1. 目標與完成定義

本計畫要把下列五類證據變成可重跑、可稽核、可明確判定 PASS/FAIL 的 release gates：

1. `B-P1-08`：瀏覽器中的登入身分切換、AG-UI `401` 全域登出與 localStorage 清理。
2. `C-03` / `C-04`：真正送進模型的訊息、session 去重、視窗裁切與 tool-call/result 配對。
3. `C-05`：AG-UI turn 同時落 appdb 與正確使用者的真 mem0。
4. `C-07`：使用非 `mock-gpt` 模型，兩條聊天鏈路實際走相同 skill routing / workflow。
5. `C-08`：經 nginx 與 Vite proxy 時，首 chunk 在完整回覆前抵達且 SSE framing 不變。

全部 evidence 必須產生機器可讀結果；`SKIP` 或環境缺失一律記為 `BLOCKED`，不能算 release PASS。

## 2. 證據架構

### 2.1 每次執行的 evidence bundle

驗證工具接受 `-EvidenceDir`，預設輸出到已由 `.gitignore` 排除的 `artifacts/copilot-shared-core/<UTC-run-id>/`：

- `manifest.json`：commit SHA、dirty flag、image digest、模型名稱/版本、測試起訖時間、各 gate 狀態。
- `requests/`：去識別化的 HTTP/SSE timing 與狀態；不得保存 JWT、密碼或真實使用者內容。
- `traces/`：只保存本次隨機 marker 相關的模型輸入投影、workflow invoke 投影與 mem0 查詢結果。
- `screenshots/`：僅保存登入頁、Copilot 成功狀態與 401 後登入頁；截圖前清除 token 顯示面。
- `junit/`：Playwright 與其他自動化結果，供 CI/release job 彙整。

每個案例使用無業務意義的隨機 `runId` / marker 做相關性，不把 tenant、JWT 或 prompt 全文寫進一般 application log。任何工具寫入第一個 artifact 前，必須先確認目標位於被忽略的目錄；輸出完成後做 fail-closed secret scan，若命中 `Authorization`、JWT 形狀、password、cookie 或未遮罩 prompt，該 lane 直接 `FAIL` 並刪除 bundle，不能只記 warning。

### 2.2 兩種模型 lane 不得混用

- **Deterministic evidence lane**：在僅供測試、只綁 loopback 的 compose profile 加入可錄製且可腳本化的 OpenAI-compatible evidence model。它用固定延遲串流、固定 tool call 與 request capture，驗 `C-03`、`C-04`、`C-08` 的真 platform/session/AG-UI/proxy 序列化；不得拿它證明模型會正確選 skill。
- **Real-model lane**：只負責 `C-05` 的 mem0 抽取/embedding 與 `C-07` 真 routing。執行者必須分別指定 platform chat、workflow、mem0 LLM 與 embedding 的固定 provider deployment/snapshot，不接受單獨設定 `CHAT_MODEL` 或可漂移 alias 作為可重現證據。manifest 保存四者的設定識別、provider 回傳的實際 model ID／可用時的 system fingerprint；設定或實際回傳不符即 `BLOCKED`。沒有有效 provider key 時同樣 `BLOCKED`，不得退回 `mock-gpt` 後宣稱通過。

## 3. Evidence matrix

| Gate | 自動化方法 | PASS 判準 | 主要假陽性防護 |
| --- | --- | --- | --- |
| `E-01 / B-P1-08` Browser auth | 在 `frontend/` 加 Playwright。真 full-compose 登入 A，確認 Copilot 可完成一輪；登出後登入 B，執行時以 token 的不可逆 SHA-256 fingerprint 確認後續 request 已換成 B（artifact 只存 fingerprint），並以 B scoped history 的新增資料交叉證明身分。另一案登入後把 session token 換成無效 token、reload、由 Copilot 發出真正會得到 `401` 的請求 | 回到登入頁；session key、chat messages、conversation/user keys 全清；舊帳號不再顯示；console 無 uncaught error；token 未出現在 Copilot readable state；artifact 無原始 JWT | 不只 route-fulfill 假 401 或前端記憶體狀態；至少一案必須由真 platform JWT middleware 回 `401` |
| `E-02 / C-03` Tenant isolation | 真 backend JWT、兩租戶、相同 `threadId`；A 寫入隨機 secret，B 用另一 marker 詢問。由 evidence model capture B 該輪實際 model input | B model input、B response、B session 投影均不含 A secret；isolation key 投影同時含 tenant 與 user 維度 | 不以 response-only 或 session `null` 判定；直接檢查實際模型輸入內容 |
| `E-03 / C-04` Session/dedup/tool pair | Node/Playwright AG-UI driver 重送三輪完整 message array，第二輪改 assistant ID，並由 scripted evidence model 發出一個真 client-tool call；driver 必須實際執行受控 handler、記錄 invocation，再用同一 call-id 回送 result；擷取第三輪 model input 與 AG-UI events | 每個邏輯 user/assistant 訊息各一次；數量線性成長；第三輪仍含第一輪；handler 恰執行一次；同一 call-id 恰有一組 call/result，無孤兒；另跑 20/21 視窗與 compaction 配對測試 | 不只計可偽造的 wire events；以 handler invocation、model-input 投影與 call-id 配對三者為準 |
| `E-04 / C-05` appdb + mem0 | Real-model lane。用「我的驗收偏好代碼是 `<marker>`」跑 authenticated AG-UI turn；確認 history 增加，再 bounded retry 呼叫 mem0 `/search`，`user_id` 必須是 `{tenant}:{user}` | history 有本輪 assistant reply；mem0 search 在時限內回傳與 marker 語意相符的 memory；另一使用者 search 不得取得該 memory | 不要求 mem0 逐字保存整段對話；以可抽取的單一事實與 user scope 驗證。只看 container 200 不算 PASS |
| `E-05 / C-07` Real shared routing | 建立隔離測試資料：上傳含唯一數字 marker 的小文件並等狀態 ready；以同一身分與同一問題分別打 `/api/chat` 與 AG-UI。platform→workflow 的真 HTTP 路徑在 evidence profile 經 loopback capture proxy 原樣轉送；proxy 只保存 skill name、input keys 與 keyed HMAC，不保存 input 原文。另收集 Langfuse/LiteLLM generation 與 workflow trace ID | 兩鏈路 invoke 同一 skill；input key 與 keyed HMAC 一致；workflow 成功解析真 JSON；最終回覆含 fixture 數字；AG-UI 不出現該 server skill 的 `TOOL_CALL_*`。固定四組模型 deployment/snapshot 與 temperature，最多跑 3 次且 3/3 必須通過 | capture 位於實際 HTTP 送出路徑，避免 response-only 假綠；不把業務 input 原文加入一般 trace。`mock-gpt` 或直接 invoke skill 均不算 routing evidence |
| `E-06 / C-08` Proxy streaming | evidence model 至少輸出 3 frames、frame 間固定延遲 250 ms。對 chat SSE 與 AG-UI，分別經 nginx full image 與 Vite dev proxy 執行 `curl --no-buffer --trace-time`；Playwright 頁面另以 `fetch(...).body.getReader()` 在 browser context 記錄每次 read 的時間與 bytes | curl 與 browser reader 各自至少看到 3 frames；第一與最後 frame 到達差至少 400 ms；browser 首次 read 早於 response completion；chat 維持 `data:`，AG-UI 維持 `data: ` | 使用受控延遲，避免 loopback + 短回覆讓 buffering 問題假綠；Playwright 只看 headers/response 完成或 curl 最終 body均不算 PASS |

## 4. 實作批次與硬順序

### Batch 0 — Evidence harness（先做）

1. 確認 `.gitignore` 已排除 `artifacts/copilot-shared-core/`，定義輸出目錄權限、`manifest.json` schema、PASS/FAIL/BLOCKED 狀態、秘密遮罩與 fail-closed secret-scan 測試；未通過前禁止產生其他 artifacts。
2. 為既有 PowerShell verifier 加 `-EvidenceDir`，保留目前預設行為。
3. 建立 optional `evidence` compose profile、deterministic evidence model，以及 real lane 的 platform→workflow loopback capture proxy；兩者都只綁 `127.0.0.1`，不進 production profile。capture proxy 使用每次 run 的暫時 HMAC key，只輸出 skill name、input keys 與 keyed hash，並將原 request 原樣送到真 workflow。
4. 對 Langfuse API/export 做短 spike；若版本 API 無法穩定依 marker 匯出 generation input，evidence model capture 是 `C-03/C-04` 的 canonical source，Langfuse 只作交叉佐證。

Exit：能產生不含 secrets 的 manifest、SSE timestamps 與 model-input 投影。

### Batch 1 — Browser gate

1. 在 `frontend/` 加 Playwright config、browser install/CI command 與穩定 selector；不以中文文案作唯一 selector。
2. 實作登入 A→Copilot→登出→登入 B，以及真 platform invalid-token→AG-UI 401 兩條測試。
3. 收集 JUnit、trace-on-first-retry 與必要截圖；trace/screenshot 不得保存可重用 token。

Exit：`E-01` 在 full compose 可重跑通過；沒有 browser binary 時明確回 `BLOCKED`。

### Batch 2 — Deterministic session/proxy gates

1. 實作 `C-03` 相同 thread 的跨租戶/跨使用者 model-input 檢查。
2. 實作 `C-04` full-array resend、assistant ID collision、真 client tool 與 20/21 compaction sequence。
3. 實作 nginx + Vite 的 curl frame timestamps，以及 browser `ReadableStream.getReader()` 到達時間與相對時間斷言。

Exit：`E-02`、`E-03`、`E-06` 全綠，且 artifacts 能由 reviewer 單獨重算斷言。

### Batch 3 — Real-model side effects/routing

1. preflight 檢查 provider key、backend/workflow/mem0/Langfuse readiness，並逐一驗證 `CHAT_MODEL`、`WORKFLOW_LLM_MODEL`、mem0 LLM、mem0 embedder 的固定 provider deployment/snapshot；manifest 記錄設定值、實際 model ID 與可用時的 system fingerprint。任一缺失或不符立即 `BLOCKED`。
2. 執行 mem0 scoped fact 寫入/查詢；bounded retry 建議上限 60 秒，超時即 FAIL。
3. 建立唯一 run namespace 下的 tenant/user、conversation、mem0 fact、document 與必要 skill fixture；等待 document ready，執行兩鏈路 routing 三次並匯出 workflow/model trace。
4. 所有 lane 都以 `try/finally` 清理 document、mem0 memory、conversation/history、暫時 skill、測試 user/tenant 與 capture state；cleanup 失敗寫入 manifest 並使整體 gate `BLOCKED`，不能靜默保留污染資料。

Exit：`E-04`、`E-05` 全綠；manifest 記錄 model/version、fixture ID 與 trace IDs。

### Batch 4 — Independent release review

由 `code-reviewer` 檢查測試是否只斷言可觀察行為、是否可能 response-only 假綠、artifacts 是否洩密；再由 `e2e-verifier` 在 fresh images 上執行完整命令。release sign-off 必須同時包含：

- 367 個 platform tests、frontend lint/build、既有 smoke 8/8。
- `E-01`～`E-06` 全部 PASS，零 `BLOCKED` / `SKIP`。
- image digest、真模型版本、trace IDs 與 evidence bundle 路徑。

## 5. 系統性風險與控制

| 風險 | 控制 |
| --- | --- |
| 真模型非確定性造成 flaky gate | 隔離 deterministic session/proxy lane；routing 固定模型、temperature、fixture，要求 3/3，而非把重試一次成功當 PASS |
| Langfuse API 或 schema 升級使驗證失效 | canonical request capture 放在 optional evidence model；Langfuse 只承擔 real-model routing 的 trace ID/交叉佐證 |
| trace、Playwright artifact 洩漏 JWT 或使用者內容 | 全部使用隨機無意義 marker；header/token 先 redact；輸出路徑已被 Git ignore；每次輸出後做 fail-closed secret scan 並設定保存期限 |
| mem0 摘要不保留逐字 marker | 測單一明確「偏好代碼」事實，做語意 search 與錯誤使用者 negative check，不斷言全文相等 |
| timing 門檻受 CI 負載影響 | 使用 250 ms 受控 frame delay與相對間隔判斷，不以固定 TTFT 上限作功能 gate；效能 SLA 另立基線 |
| 測試 profile 被誤用到正式環境 | evidence service 只在 optional profile、loopback binding、無 production compose dependency；啟動時印出 non-production banner |

## 6. 預估工作量與責任切分

| 工作 | 建議負責者 | 預估 |
| --- | --- | --- |
| Evidence manifest、PowerShell 整合、compose profile | infra / main agent | 0.5–1 天 |
| Evidence model capture + scripted stream/tool call | platform + infra | 1–1.5 天 |
| Playwright browser gates | frontend implementer | 1 天 |
| C-03/C-04 session assertions | platform implementer | 1 天 |
| C-05/C-07 real-model fixture與 trace 匯出 | e2e verifier + workflow/platform | 1–1.5 天 |
| Review、fresh-image rerun、文件同步 | reviewer / e2e / docs | 0.5 天 |

整體約 **5–6.5 engineer-days**；Batch 1 與 Batch 2 可在 Batch 0 完成後平行，Batch 3 必須等真模型 key 與基礎設施可用。

## 7. 執行命令介面（目標形狀）

```powershell
# 快速、無額度 smoke；維持既有用途
pwsh -File scripts/verify-copilot-shared-core.ps1 -IncludeMem0Outage

# deterministic browser/session/proxy evidence
pwsh -File scripts/verify-copilot-shared-core-evidence.ps1 `
  -Lane Deterministic -EvidenceDir artifacts/copilot-shared-core/<run-id>

# 真模型 mem0/routing evidence；不可自動 fallback 到 mock-gpt
pwsh -File scripts/verify-copilot-shared-core-evidence.ps1 `
  -Lane RealModel `
  -PlatformModel <fixed-deployment> -WorkflowModel <fixed-deployment> `
  -Mem0Model <fixed-deployment> -EmbeddingModel <fixed-deployment> `
  -EvidenceDir artifacts/copilot-shared-core/<run-id>
```

最終 wrapper 只有在 smoke 8/8、`E-01`～`E-06` 全 PASS 且必要 artifacts 齊全時回 exit code `0`。
