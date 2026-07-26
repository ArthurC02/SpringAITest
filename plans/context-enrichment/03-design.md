# Context Enrichment — 技術設計

> 狀態：**E1 + E3 已交付**。承接 [01-plan.md](01-plan.md) 與 [02-spec.md](02-spec.md)，本檔定義 HOW。
> 架構決策（C1–C19）與重用盤點是 01-plan §5–§6 的責任，本檔不重複理由，只落到檔案、schema 與節點契約層級。範圍以 E1 為主；E3 只定邊界，E2/E4 阻擋中（01-plan §11）。

## 1. 服務責任

```text
Root Orchestrator（workflow/app/runtime/orchestrator.py，既有）
      ↓ ContextAcquirer seam（orchestrator.py:381，既有注入點）
Enrichment Graph（新：app/skills/context-enrichment.yaml + app/nodes/context_enrichment/）
      ↓ 既有 tool 通道（internal token + identity headers）
Backend :8002（新：Contexts/ feature folder）
  Context Store + Readiness 判定 + Policy + Catalog + acquire 行為升級
      ↕
appdb（PostgreSQL；lite 模式為 in-memory 雙路徑）
```

### Backend（`Contexts/` 新 feature folder）

- 六張表加入 `DbBootstrap.Ddl`（additive-only 冪等建表，見 §2）。
- `IContextRepository` Dapper 與 in-memory 雙實作；in-memory 仿 `InMemoryConfigurationSetRepository` 樣板（`lock (_store)`、手動重建 UNIQUE 與唯一 active）。
- `ContextCanonicalizer.cs`：Context 專屬欄位驗證；key-sort 遞迴直接呼叫 `AgentCanonicalizer.CanonicalizeDefinition`（C16，不發明 `ICanonicalizer`）。
- `ReadinessEvaluator.cs`：純函式判定器（見 §5），無 IO、可單測。
- Revision 寫入交易複製既有六步驟樣板（C17）：FOR UPDATE 鎖 → 驗版本 → reference lock（FOR SHARE）→ supersede → insert revision → 更新指標欄。
- `Common/` 新增 `RequireIfMatchVersion()` / `SetVersionETag(long)` 兩個 extension method，只給新 Context controller 用（C15，不回改既有三份）。
- 改寫 `OrchestratorRunRepository.AcquireContextAsync`（`OrchestratorRunRepository.cs:301`）：見 §4。
- `OrchestratorTaskEnvelope.cs:26` 欄位白名單加 `context_ref`（C8）。
- `Retrieval/RetrievalDtos.cs` 的 `RetrievedChunk` 增加 chunk 識別欄位（additive；chunk 粒度 `content_ref` 與去重的前提）。
- 種子資料：`source_catalog` 一筆 `backend_documents`（`source_id` 與 Orchestrator `knowledge_sources` 對齊——C7）；`context_policy` 每 tenant 一份 active 種子。

### Workflow（`app/nodes/context_enrichment/` + `app/skills/context-enrichment.yaml`）

- 節點族仿 `nodes/kbquery/` 結構：`models.py`（固定欄位 Pydantic 模型，無自由文字夾帶）、`ports.py`（Protocol + adapters）、`nodes/` 每檔一個 `@node`。
- 圖由 YAML 組成（I1）：分支走 `when`、迴圈走 `loop: {max_iterations, until}` + `app/engine/expressions.py` 既有安全求值，節點外不得有協調用 Python 流程控制。
- **禁止任何門檻 / 權重 / precedence 常數**（I2）；`loop.until` 直接寫 `context_status != 'NEED_MORE_CONTEXT'`，判定值來自 Backend。
- canonical hash 一律 `app/canonical_json.py`（C16a）；不需要自己的 checkpointer（enrichment 是同步一次性呼叫，暫停語意在 root——C9）。
- DI 走 `app/skills/deps.py` 既有裝配點，擴充兩個 port：`ContextPolicyPort`（GET policies，失敗即 fail closed）、`ContextStorePort`（submit / get revision）。

### Platform

- `CONTEXT_ENRICHMENT_ENABLED` 認證前 404 中介軟體（照 `Program.cs` 既有六旗標樣板）；`GET /api/features` 加對應布林。本期無新 controller。

### Frontend

- E1 無變更；Context 摘要以既有 orchestrator run trace 事件呈現。

## 2. 資料模型（實作細節）

表形狀的權威是 01-plan §7；本節只補實作層決定：

| 表 | 鍵與索引 | 說明 |
| --- | --- | --- |
| `context_revision` | `PK(context_id, revision)`；index `(tenant_id, root_run_id)` | canonical bytea 是權威，`definition` jsonb 只供查詢投影；`readiness` 與 `policy_id` 成對非空 |
| `context_evidence` | `PK(evidence_id)`；`UNIQUE(context_id, revision, snapshot_id, content_ref)` | UNIQUE 即去重 canonical key 的落地（同 snapshot 同 chunk 只此一筆；跨 snapshot 保留兩筆） |
| `context_view` | `PK(view_id)`；`UNIQUE(context_id, revision, view_type)` | 每 revision 每角色至多一份投影 |
| `context_policy` | `PK(id)`；`UNIQUE(tenant_id, name)`；partial unique index `(tenant_id) WHERE is_active` | 「每 tenant 唯一 active」比照 `configuration_set` 用 partial index 落地，in-memory 端手動重建 |
| `source_catalog` | `PK(source_id)`；`tenant_id` **nullable**（NULL = 系統層全租戶來源） | 對 01-plan §7 的一處細化：E1 種子 `backend_documents` 是全租戶共用，若 PK 含 tenant 反而要為每個 tenant 複製一筆；出現第一個租戶專屬來源時再收斂 |
| `metric_definition` | `PK(metric_id, definition_version)` | E1 只建表，零內容（E2 阻擋中） |

- 全部走 `DbBootstrap.Ddl` additive-only 冪等建表，不引入 migration framework。
- in-memory 雙路徑的正確性由參數化 Theory 保證（C19；A-CTX-18），不靠人工紀律。

## 3. Enrichment Graph 節點契約

§8（01-plan）流程圖的每一行就是一個 `@node`。契約表（`reads` 是 Harness 過濾的依據，必須顯式宣告）：

| 節點 | reads | writes | 性質 |
| --- | --- | --- | --- |
| `validate_job` | `job` | `validated_job`（不合法 → fatal，短路） | 確定性 |
| `resolve_security_scope` | identity keys（`tenant_id`/`user_id`/`role`）、`snapshot_authority` | `security_scope` | 確定性；不由 LLM 決定 |
| `normalize_request` | `validated_job` | `normalized_request` | 確定性（E1 實作零 LLM；未來需要 LLM 輔助時沿用 `LangChainStructuredLLM` 降級樣板） |
| `resolve_time` | `normalized_request`、runtime timestamp | `time_frame` | 確定性；相對期間語意由 policy 解析（Backend） |
| `build_requirements` | `normalized_request`、`context_policies` | `requirements` | 確定性；禁止產生 SQL/URL/table name |
| `discover_sources` | `requirements`、`security_scope` | `candidate_sources` | 確定性；回傳 `source_catalog ∩ snapshot.authority` **全集**（precedence 只作確定性排序）；**選擇與 pin 是 Backend 的權威**（`SelectSourceAsync`），Workflow 不做挑選——避免同一條 precedence 業務規則兩份實作 |
| `retrieve_documents` | `candidate_sources`、`requirements`、`normalized_request` | appends `evidence_raw` | tool：既有 `backend.retrieval_search`（scoped）；以標記 `unmet` 的 requirement 名稱擴充 query，讓 expansion 圈真的檢索到不同內容 |
| `normalize_evidence` | `evidence_raw` | `evidence_normalized` | 確定性；保留原始值與規則版本 |
| `deduplicate_evidence` | `evidence_normalized` | `evidence` | 確定性；**是節點不是 reducer**（編譯器 `appends` 只掛 `operator.add`，不為此改引擎） |
| `detect_conflicts` | `evidence` | `conflicts` | 確定性；衝突不靜默覆蓋，必進 verifier view |
| `build_facts` | `evidence`、`conflicts` | `facts` | 確定性（E1 零 LLM）；文件證據無數值計算 |
| `assess_coverage` | `requirements`、`evidence`、`retrieval_gaps`、`candidate_sources` | `coverage`、`assumptions` | 確定性；把 gap（optional source 失敗、optional requirement 未覆蓋）具體化為 assumptions——機制轉換，無業務門檻 |
| `build_views` | `facts`、`evidence`、`conflicts`、`assumptions`、`requirements` | `views` | 確定性投影 + 授權重檢；以 64 KB 傳輸上限做體積降級（超限 evidence 不進 view、記 `evidence-oversized-degraded` gap） |
| `submit_revision` | `views`、`evidence`、`requirements`、`coverage`、`assumptions` | `context_status`、`unmet_requirements`、`context_ref` | tool：`POST /api/contexts/{id}/revisions`；送真實 `assumptions_count` 與 `measurements.completeness`（覆蓋比） |
| `expand_requirements` | `unmet_requirements`、`requirements` | `requirements` | 迴圈體內；**就地標記 `unmet: true`**，`requirements` 形狀在迭代間恆為 `list[dict]`，不整批替換 |

YAML 骨架（示意；`required_role: ADMIN` 為既有頂層欄位）：

```yaml
name: context-enrichment
required_role: ADMIN
flow:
  - validate_job
  - resolve_security_scope
  - normalize_request
  - resolve_time
  - build_requirements
  - loop:
      max_iterations: 3          # 與 snapshot.max_context_rounds 的下界對齊，非另一套預算
      until: "context_status != 'NEED_MORE_CONTEXT' or context_attempt >= remaining_context_rounds"
      #      ^ 後半條只讀 Backend 發的 round 預算，無新常數
      body:
        - discover_sources
        - retrieve_documents
        - normalize_evidence
        - deduplicate_evidence
        - detect_conflicts
        - build_facts
        - assess_coverage
        - build_views
        - submit_revision
        - expand_requirements
```

E1 實作**全確定性、圖內零 LLM**（比原設計更安全）；`LangChainStructuredLLM` 的「結構化輸出失敗即降級」模式留作未來 LLM 節點的樣板。能複用的既有件直接複用：`local.rerank`、`search_chunks_scoped`、`MAX_TASK_CONTEXT_BYTES` 傳輸上限。

## 4. Acquire 資料流

```text
RootOrchestrator.execute(context_round=N)
  → 旗標關閉（任一端）→ 舊路徑位元不變：
      Workflow：ProductionRootPlanner 本地 acquirer（_assess_context LLM 澄清評估
                + search_chunks_scoped 檢索，可回 ready:true）
      Backend：current_context.user_input 短路（ready:true 回聲）
  → 旗標開啟 → ContextEnrichmentAcquirer：
      1. GET /api/context-policies        ← 不可用 → fail closed，回 not-ready
      2. invoke enrichment skill（同進程，走既有 Skill 引擎；不經 HTTP /skills invoke）
      3. skill 內 submit_revision → Backend 判定 + 持久化
      4. POST /api/orchestrator-runs/{run}/context/acquire（既有端點）
  → AcquireContextAsync（旗標開）：
      - user_input 不再短路 → missing:["clarification-revision-required"]，澄清走新 revision
      - 本 run 有 READY / READY_WITH_ASSUMPTIONS revision → ready:true，
        context 含 context_ref，provenance 由 Context Store 產生
      - 否則 → fail-closed not-ready
  → BLOCKED_BY_POLICY / INSUFFICIENT_DATA → ContextAcquisition.terminal=true → root failed（不重試）
```

單一真相：旗標開啟時 acquire 回傳的 `context` / `provenance` 一律從已持久化 revision 投影而來，Workflow 不自行拼裝回傳值。configuration-only rollback：旗標關閉即回到 E1 前的舊行為（含三種關閉組合的測試釘住）。

## 5. Readiness 判定器（Backend）

- 純函式：`(候選 envelope 量測值, policy.values) → {status, readiness, unmet_requirements[]}`。
- 判定順序：授權違規 / 禁用來源 → `BLOCKED_BY_POLICY`；mandatory requirement 缺口 → 不得 READY（Hard Gate）；關鍵歧義 → `NEEDS_CLARIFICATION`；加權分數 ≥ READY 門檻 → `READY`；落在 assumptions 區間 → `READY_WITH_ASSUMPTIONS`；其餘 → `NEED_MORE_CONTEXT`。
- 所有門檻 / 區間來自 `context_policy.values`，判定器本體零常數；同一交易內把結果與 `policy_id` 寫入 revision。`values` 有頂層鍵白名單（`readiness` / `bootstrap_requirements` / `source_requirements` / `source_precedence`），未知鍵 → 政策無效（422）。
- caller 的量測值不合法（round 越界、assumptions 為負等）→ `ArgumentException` → 400；`ContextPolicyInvalidException` 只留給政策本身解析失敗。
- `READY_WITH_ASSUMPTIONS` 授予前由 `ContextCanonicalizer.ValidateAssumptions` 驗 envelope assumptions 非空、與 `assumptions_count` 一致、出現在 ready view（E3 單一角色 view 時檢查 `context_ref` 指向的那份）。
- E1 無 metric 計算（文件證據沒有數值）；C11b 的「Backend 算數值」到 E2 才有實作對象。

## 6. API 與錯誤

- ApiError `{timestamp, status, message, fieldErrors}` 契約沿用；新資源回應欄位 **snake_case**（對齊 documents / workflows 群，不對齊 auth/config 的 camelCase）。
- `POST /api/contexts/{id}/revisions`：需 `RequireTenant()` + `RequireUserId()`（root run 以 tenant+user 雙重 scope，防同租戶跨使用者污染）。候選 envelope 或量測值不合法 → `400`；無 active policy → `503`；政策內容無效 → `422`（三個錯誤碼在 submit / `GET /api/context-policies` / E3 delta 三處一致）。revision 由交易內 `max(revision)+1` 產生——E1 只有 enrichment 迴圈單一寫入者；E3 delta 的並發 token 是 **request version**（非 revision number），缺 If-Match → 428、無效 → 400、過期 → 409。
- `GET` 端點（revisions 與 views）回 `ETag`（revision）；旗標關閉 → 認證前 `404`（Platform 中介軟體樣板）；跨 tenant → `404` 不洩漏存在性。child 建立時的 context 投影例外映射為 `400`（與 envelope 驗證同一組），不得逃逸成 500（workflow 會視為可重試）。
- Workflow 側錯誤映射：policy 不可用 / submit 失敗 → enrichment 終止，acquire 回 not-ready，`missing` 帶明確代碼（如 `context-policy-unavailable`），不偽造 provenance。

## 7. 旗標與設定

| 服務 | 設定 | 落點 |
| --- | --- | --- |
| Backend | `CONTEXT_ENRICHMENT_ENABLED`（預設 false） | 既有旗標讀取樣板；關閉時 Contexts API 全部 404 |
| Workflow | `context_enrichment_enabled`（預設 false） | 已落地 `settings.py`；C6 查出的 D6 旗標文件落差也已一併修正（`workflow/AGENTS.md` 與根 AGENTS.md 的 D6 敘述改為「Workflow 無 `AGENT_CHAT_ENABLED`」） |
| Platform | `CONTEXT_ENRICHMENT_ENABLED`（預設 false） | 認證前 404 中介軟體 + `GET /api/features` |

依賴關係：三端都要求 `MULTI_AGENT_DISPATCH_ENABLED` 同時為 true 才生效（沒有 Root Orchestrator 就沒有消費者）。

## 8. 建議變更位置

| Area | 新增／調整 |
| --- | --- |
| `backend/` | `Contexts/`（controller、repos、canonicalizer、readiness evaluator、policy）、`DbBootstrap` DDL、`Common/` ETag helpers、`OrchestratorRunRepository.AcquireContextAsync`、`OrchestratorTaskEnvelope` 白名單、`RetrievalDtos.RetrievedChunk` |
| `workflow/` | `app/nodes/context_enrichment/`、`app/skills/context-enrichment.yaml`、`app/skills/deps.py` 兩個新 port、`settings.py` 旗標、`ContextAcquirer` adapter 接線 |
| `platform/` | 旗標中介軟體、`GET /api/features` |
| `frontend/` | E1 無 |
| `infra/` | 無（不引入新容器——C4） |

## 9. 交付後餘留事項

1. ~~`RetrievedChunk` chunk 識別欄位~~ 已落地（`chunk_id` 投影，相容建構子已刪）。
2. ~~C6 旗標文件落差~~ 已修（settings 落地 + AGENTS.md D6 敘述修正）。
3. E3 契約已定稿於 [docs/context-enrichment-contracts.md](../../docs/context-enrichment-contracts.md)。**Workflow 端的 E3 生產呼叫端刻意未接線**：`TaskLocalContextRuntime` 是完成的 seam（契約測試覆蓋），但「worker 何時觸發 ContextRequest」的機制文件從未定義，接線前需先定義 worker-tool 整合契約——見 `app/runtime/task_local_context.py` 模組 docstring。
4. E2 全部阻擋於 01-plan §11 Q1–Q3。
