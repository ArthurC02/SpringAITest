# Context Enrichment — 驗收測試

> 狀態：**E1 + E3 已交付並通過驗收**（backend 748 / platform 677 / workflow 1205 全綠；A-CTX-40 的生產觸發機制除外，見 §3）。本檔是 A-CTX 全表的唯一權威；[01-plan.md](01-plan.md) §9 保留取捨原則（I4）、刪除紀錄與測試形狀指引，兩者不一致時以本檔為準。
> 編號缺口（A-CTX-03 / 16 / 17 / 20）是 I4 刪除的刻意留痕，刪除理由見 01-plan §9，不回填。
> E2（A-CTX-23..39）與 E4（A-CTX-60..79）為保留區段：在 01-plan §11 Q1–Q3 定案前不預先編列，避免寫出與資料現實脫節的驗收條件。

## 1. E1：Context 骨架（A-CTX-01..22）

取捨原則：只留「這段**新**邏輯壞掉時會失敗」的案例；原樣複用的既有機制（canonical SHA、ETag 428/409、flag 前置 404、cascade cancel、scoped retrieval、root timeout）不再複製測試。安全項不精簡。

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-CTX-01 | 旗標關閉 | 三服務各自 fail-closed（新 API 認證前 404）；`context/acquire` 行為與 E1 之前**位元相同**——Backend `user_input` 短路與 Workflow `ProductionRootPlanner` 本地 acquirer 原樣保留（三種關閉組合各有測試）。configuration-only rollback 成立 |
| A-CTX-02 | 同一 context 連續兩次 enrichment | 產生 revision 1、2；revision 1 內容位元不變 |
| **A-CTX-04** | Evidence 的 `source_id` 不在 snapshot authority 內 | Backend 拒絕持久化；Workflow 端既有檢查亦拒絕（雙重）。**這條同時補一個既有缺口**：`workflow/app/runtime/orchestrator_backend.py:291-301` 的 `"Root context provenance exceeds snapshot authority"` 分支目前全 repo 沒有任何測試觸發過。Context Enrichment 是第一個真的會送 provenance 的呼叫者 |
| A-CTX-05 | **跨 tenant evidence 注入** | Hard fail + 安全告警，不得降級為 gap。必須用**另一租戶的真實 run/context**，不是不存在的 id——既有 `OrchestratorRunApiTests.cs:110`（`RunReads_AreOwnerScoped_AndDoNotLeakExistence`）測的是後者（owner-scope），嚴格跨租戶在 Orchestrator 這層尚未被證明過。斷言沿用共用 helper，不再複製第 7 份 `CrossTenant_*` 樣板 |
| A-CTX-06 | **文件內含指令文字**（prompt injection） | 內容只出現在 `[UNTRUSTED_EVIDENCE]` 區塊，不影響 system instruction / tool allowlist |
| A-CTX-07 | 呼叫者無 `knowledge.read:<docId>` | 檢索回空，投影前再檢一次仍為空；不得靠模型自律 |
| A-CTX-08 | Mandatory requirement 未滿足 | 狀態不得為 `READY`；round 未用盡則 expand，用盡則 `INSUFFICIENT_DATA` |
| A-CTX-09 | Readiness 落在政策定義的 assumptions 區間（種子政策為 0.70–0.85；區間屬 `context_policy` 資料，不是程式常數） | `READY_WITH_ASSUMPTIONS`，且 assumptions 非空並出現在 planner view（Backend 授予前驗 assumptions 區段與 `assumptions_count` 一致；Workflow 由 `assess_coverage` 節點把 gap 具體化為 assumptions） |
| A-CTX-10 | 關鍵歧義 | root 進 `waiting_input`；使用者回答經 `current_context.user_input` 回流並產生新 revision |
| A-CTX-11 | 重試造成重複檢索結果 | `deduplicate_evidence` 節點依 canonical key 合併，筆數不重複；不同 snapshot 的同一資料保留兩筆並標記 |
| A-CTX-12 | Required source timeout / Optional source 失敗 | 前者建立 gap 並影響 readiness；後者不阻斷流程，只降低 completeness |
| A-CTX-13 | Envelope 超過大小上限 | 建立 gap 並降級投影，**不得靜默截斷**——Workflow `build_views` 以 64 KB 傳輸上限降級（超限 evidence 不進 view、記 `evidence-oversized-degraded` gap 含 `content_ref`，完整 evidence 仍進 store）；Backend 硬上限 400 為後擋 |
| A-CTX-14 | Worker child 取得的 payload | 只含自己的 view；斷言 envelope 全量欄位不出現在 child snapshot |
| A-CTX-15 | Deadline 逼近 | 停止 optional retrieval，保留 mandatory flow；既有 timeout 仍生效 |
| A-CTX-18 | lite 模式（`DB_PROVIDER=inmemory`） | 一個吃 `IContextRepository` 的參數化 Theory 同時跑 Dapper 與 in-memory（C19），斷言 revision 不可變、tenant 隔離、唯一 active policy。存在理由是 `plans/test-audit/ledger.md` 已記錄過同型分歧，不是為了覆蓋率 |
| A-CTX-19 | Enrichment 產出的內容 | 不寫入 mem0；mem0 recall 內容不被當成 evidence |
| A-CTX-21 | 同一 chunk 由兩次檢索取得 | 以 `document_id + chunk_id + content_hash` 為 canonical key 合併為一筆 evidence（依賴 `RetrievedChunk` 新增 chunk 識別欄位） |
| **A-CTX-22** | **I2 守門測試**：同一組 evidence 與量測值，只改 `context_policy` 的門檻，重跑一次 | status 必須跟著改變。這是四條不變量裡最容易被侵蝕的一條——只要有人在 Workflow 補一個「暫時的」門檻常數，這個測試就會失敗。它同時證明判定確實來自 Backend |

測試形狀（01-plan §9 的指引，此處只記結論）：節點單元比照 `test_kbquery_nodes.py`（每支只打一個節點，依賴用 fake 隔離）；整圖端到端比照 `test_skill_kbquery_parity_e2e.py` 收斂後的形狀——**1 個代表案例做 golden trace，其餘驗結構性觀測點**，不做 N 份全欄位 golden。

## 2. E2：語意解析與 Catalog（A-CTX-23..39，保留區段）

**阻擋中**：待 01-plan §11 Q1（結構化數據源）、Q2（Entity Master 來源）定案。預期覆蓋面（僅供編列時參考，不是驗收條件）：metric definition version pinning、entity resolution 候選由 Backend 出且 LLM 不創造 ID、fiscal calendar 期間解析、source precedence 衝突解決、多來源 conflict detection。

## 3. E3：Task-local 擴充（A-CTX-40..）

E3 的契約細節在 E1 交付後定稿（03-design §9），以下條目定的是行為邊界，實作前可增補、不可放寬：

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-CTX-40 | Worker task 執行中提出 `ContextRequest` | 產生新 revision；原 revision 位元不變，已分派 task 引用的 `context_ref` 不漂移。**備註**：Backend API 與 Workflow seam（`TaskLocalContextRuntime`）已交付並由契約測試覆蓋；worker 端的生產觸發機制待 worker-tool 整合契約定義後接線（03-design §9.3） |
| A-CTX-41 | 兩個並行 task 同時提交 `ContextDelta` | If-Match 並發控制，token 是 **request version**（非 context revision number）：缺 If-Match → `428`、無效 → `400`、過期 → `409`（含 `fieldErrors.If-Match`），不靜默覆蓋 |
| A-CTX-42 | Delta 內容與既有 evidence 衝突 | 建立 conflict 進 verifier view，不靜默取代 |
| A-CTX-43 | per-task view | 每個 task 只拿到自己的 view 投影；delta 產生的新資料不自動出現在其他 task 的既有 view |
| A-CTX-44 | `context.*` SSE 事件 | 走既有 `orchestrator_run_event`：cursor 單調、產生端遮蔽、旗標關閉時不產生 |
| A-CTX-45 | Delta 使 readiness 跌破門檻 | Backend 重新判定並記錄狀態轉換事件；不得停留在舊 READY 判定 |
| A-CTX-46 | 旗標關閉時呼叫 delta API | 認證前 `404` fail-closed |

## 4. E4：品質與觀測（A-CTX-60..79，保留區段）

**阻擋中**：依賴 E2 與 E3。預期覆蓋面：Context Utilization、Need-More-Context After Dispatch Rate、golden case 套組、per-industry 門檻（僅在 Q3 確認多產業時成立）。

## 5. 回歸命令

每一垂直切片至少執行：

```powershell
cd backend
dotnet build
dotnet test

cd ../platform
dotnet build
dotnet test

cd ../workflow
uv run pytest
```

E1 完成前必須再以 full-chain E2E 驗證（手寫 fake 只能驗局部契約，不能取代跨服務真實 JSON 與資料庫行為）：

- PostgreSQL 真實 revision 不可變性、UNIQUE 去重鍵、唯一 active policy（lite 模式另跑 `DB_PROVIDER=inmemory` 對照）。
- 旗標矩陣：`CONTEXT_ENRICHMENT_ENABLED` × `MULTI_AGENT_DISPATCH_ENABLED` 四種組合下 acquire 的行為。
- LiteLLM `mock-gpt` 走完 enrichment 迴圈：not-ready → expand → READY → decompose，child envelope 帶 `context_ref`。
- 澄清路徑：`NEEDS_CLARIFICATION` → root `waiting_input` → `user_input` 回流 → 新 revision。

Root 的 PostgreSQL checkpointer 測試維持既有手動指令（`CHECKPOINT_DATABASE_URL=... uv run pytest ...`，見 workflow/AGENTS.md）；enrichment 本身無 checkpointer（C9），不新增這類條目。
