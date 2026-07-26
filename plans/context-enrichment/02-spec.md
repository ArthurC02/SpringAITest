# Context Enrichment — 產品與系統規格

> 狀態：**E1 + E3 已交付**。承接 [01-plan.md](01-plan.md)，本檔定義 WHAT 與邊界。
> 四條設計不變量（I1–I4）、架構決策（C1–C19）與重用盤點定於 01-plan，本檔不重複理由，只固定對外可觀察的契約。
> **E2 相關契約（metric / entity / peer）在 01-plan §11 Q1–Q3 定案前刻意留空**（見 §10），不預先發明沒有資料來源的規格。

## 1. 名詞

- **Context Envelope**：一次 Root run 的結構化分析上下文 artifact，內含 requirements、facts、evidence 參照、gaps、conflicts、assumptions，以 canonical JSON 序列化。
- **Context Revision**：envelope 的不可變版本（`context_id` + `revision`）；任何 Task 固定引用一個 revision，修正一律產生新 revision。
- **Context Evidence**：單筆可回溯證據：來源、snapshot、`content_ref`、content hash、客觀量測（`observations`）、lineage。不落地任何導出分數。
- **Context View**：針對 `planner` / `worker` / `verifier` / `synthesizer` 角色的最小投影；child 只拿 view，永不拿完整 envelope。
- **Context Policy**：Backend 擁有的業務政策資料：readiness 權重與門檻、source precedence、requirement 樣板、時間解析政策、正規化規則版本。E1 為種子資料，無編輯 API。
- **Readiness**：Backend 依 policy 對候選 envelope 做出的權威判定：`status` + `readiness` 分數 + `unmet_requirements[]`。
- **Requirement**：完成某類分析所需的資料需求項；樣板由 policy 定義，分 mandatory / optional。
- **Grounded Fact**：有 evidence 支撐的結構化事實；任何數值一律由 Backend 計算，LLM 與組裝層只做敘述化。
- **Source Catalog**：server-owned 唯讀來源目錄；`source_id` 必須與 Orchestrator snapshot 的 authority 欄位對齊（C7）。
- **Enrichment Skill**：系統自有 skill `context-enrichment`，由既有 Skill 引擎編譯執行；非使用者資產，不可綁定、不可被一般 invoke 取用。

## 2. Context 生命週期與狀態

```text
Enrichment Graph 組裝候選 envelope + 客觀量測值
  → POST /api/contexts/{id}/revisions（Backend 套 policy，同一交易持久化）
  → 權威 status：
      NEED_MORE_CONTEXT        round 未用盡 → expansion；用盡 → INSUFFICIENT_DATA
      NEEDS_CLARIFICATION      root 進 waiting_input；使用者回答經 user_input 回流 → 新 revision
      BLOCKED_BY_POLICY        終止（terminal）→ root failed；不降級、不重試
      INSUFFICIENT_DATA        終止（terminal）→ root failed；明確回報，不靜默降級
      READY                    進入 decompose
      READY_WITH_ASSUMPTIONS   進入 decompose；assumptions 必須非空且出現在 planner view
```

終止語意的落地：`ContextAcquisition` 契約含 `terminal: bool = false`；acquirer 對兩個終止狀態回 `terminal:true` 並帶可辨識代碼（`context-blocked-by-policy` / `context-insufficient-data`），root 對 terminal not-ready 直接走 failed，不進 waiting_input。Backend 授予 `READY_WITH_ASSUMPTIONS` 前驗證：envelope 的 assumptions 區段非空、筆數與 `assumptions_count` 一致、且出現在 ready view（E3 delta 只有單一角色 view 時，檢查對象是 `context_ref` 指向的那份），不一致 → 400。

規則：

- 同一 `(context_id, revision)` 的 canonical bytes 永不改寫；沿用 canonical bytes + SHA-256 + jsonb 投影樣板（權威是 bytes，jsonb 只供查詢）。
- 每個 revision 記錄判定當時所用的 `policy_id`；分數與政策成對存（稽核事實——01-plan §7 對 C4a 的例外說明）。
- Readiness 的分數、權重、門檻、assumptions 區間都不是契約常數：它們是 policy 資料，只改 policy 就必須改變判定結果（A-CTX-22 守門）。

## 3. Requirement 與 Readiness（判定權在 Backend——C10/I2）

- Workflow 只提交候選 envelope 與客觀量測值：覆蓋了哪些 requirement、每筆 evidence 的量測、衝突清單。
- Backend 在 `POST /api/contexts/{id}/revisions` 套用 policy，**同一交易內**持久化並回傳權威 `status` / `readiness` / `unmet_requirements[]`。
- Hard Gate：mandatory requirement 未滿足時，status 不得為 `READY` 或 `READY_WITH_ASSUMPTIONS`。
- Workflow 端不得存在任何門檻、權重、precedence 常數；expansion 迴圈的 `until` 條件只讀 Backend 回傳的 `context_status`。

## 4. Evidence 與 Provenance

- 每一筆 context 值必有 provenance，形狀沿用 TaskEnvelope 既有契約：`{context_key, source_type, source_id, observed_at, content_sha256}`。
- `source_type` ∈ `caller | context-tool | knowledge-source`；E1 文件證據使用 `knowledge-source`。
- `source_id` 必須 ⊆ snapshot authority（`authority.context_tools` / `knowledge_sources`）；越權即 hard fail，Backend 持久化前與 Workflow 既有檢查（`orchestrator_backend.py:291-301`）雙重把關。
- `content_ref` 指向既有主鍵：`document://{documentId}#chunk/{chunkId}`；`content_hash` 為內容 SHA-256。
- Evidence 去重 canonical key = `document_id + chunk_id + content_hash`；不同 snapshot 的同一資料保留兩筆並標記。
- `observations` 只存**單筆** evidence 的客觀量測（retrieved_at、命中筆數、缺漏欄位、來源 authority_class）；freshness / authority / consistency 等分數由 Backend 依當時政策計算，不寫進 evidence 列（C4a）。
- **context 層**的客觀量測走 submit 時的 `measurements`（如 `completeness` = 已覆蓋 requirement 數 / 總數、`assumptions_count`、`context_round`）——覆蓋比是 context 層的量，不塞進單筆 evidence。門檻與判定仍在 Backend。
- 文件內容進入 Prompt 一律位於 `[UNTRUSTED_EVIDENCE]` 區塊，永不進 system instruction，也不得影響 tool allowlist。

## 5. View 投影

- child 的 TaskEnvelope 欄位白名單新增 `context_ref {context_id, revision, view_id}`（C8）；`context` 欄縮成 view 投影後的最小資料；`context_provenance` 由 Context Store 產生，不再由 caller 手寫。
- Worker view 不含其他 task 的資料、完整 envelope、對話歷史（既有 envelope 驗證已禁止 `messages` / `conversation` / `memory` 等鍵，原樣沿用）。
- Verifier view 含 claims + evidence + definitions + conflicts，不含 worker 推理過程。
- 投影前對每個 `content_ref` 重檢一次資料授權（`knowledge.read:<docId>` scoped retrieval 契約）；無權限 → 該筆不投影，不靠模型自律。
- **體積降級**：view 組裝以既有 64 KB 傳輸上限為界，超限的 evidence 內容不進 view、改記一筆明確 gap（`evidence-oversized-degraded`，含 `content_ref`）；完整 evidence 仍進 Context Store。不得靜默截斷；Backend 端的硬上限（400）是後擋不是常態路徑。
- Prompt 分區塊渲染：`[SYSTEM_POLICY]` `[TASK]` `[DOMAIN_DEFINITIONS]` `[STRUCTURED_FACTS]` `[UNTRUSTED_EVIDENCE]` `[CONFLICTS_AND_GAPS]` `[OUTPUT_SCHEMA]`；token 上限以既有 `orchestrator_token_cap` 為準。

## 6. Context Policy

- `values` jsonb 逐鍵白名單（形狀仿 `configuration_set`，獨立的表——C18），未知鍵一律拒絕。E1 實際鍵集合：
  - `readiness`：`ready_threshold`、`assumptions_min`（assumptions 區間下界）、`optional_failure_penalty`
  - `bootstrap_requirements`：啟動時的 requirement 清單（mandatory/optional）
  - `source_requirements`：來源層 requirement
  - `source_precedence`：衝突時的來源優先序
  - （`requirement_templates` / `time_resolution` / `normalization_rules_version` 原列於初版規格，E1 無讀者、已自種子與白名單移除；E2 需要時再連同讀者一起加回）
- E1 為種子資料：無編輯 API、無 revision 表（C18a）；每 tenant 至多一份 active（第二份 active 拒絕）。
- `GET /api/context-policies` 不可用時 Workflow **fail closed**：不得用內建預設值頂替（C11a）。

## 7. API 能力

路徑實作前可微調；全部是 Backend internal API（`X-Internal-Token` + `RequireTenant()` 邊界內），Platform 本期不新增公開 API。

```text
POST /api/orchestrator-runs/{run}/context/acquire    # 既有端點，行為升級（見下）
POST /api/contexts/{id}/revisions                    # 提交候選 envelope → 權威判定 + 同交易持久化
GET  /api/contexts/{id}/revisions/{rev}
GET  /api/context-views/{viewId}
GET  /api/context-policies                           # 唯讀；Workflow 規劃 requirement 前取得
```

- **acquire 契約不變**：request `{context_round, current_context, allowed_tools, allowed_knowledge_sources}`（後兩者必須與 snapshot authority 完全一致，否則維持既有拒絕）；response `{ready, context, provenance[], missing[]}`。行為依旗標分流：
  - **旗標關閉**（任一端）：行為與 E1 之前**位元相同**——Backend 的 `current_context.user_input` 短路（回 `ready:true` 回聲）與 Workflow `ProductionRootPlanner` 的本地 acquirer（LLM 澄清評估 + scoped 檢索）原樣保留。configuration-only rollback 成立。
  - **旗標開啟**：澄清一律走 revision——`user_input` 不再短路，acquire 回 `missing:["clarification-revision-required"]`，由 enrichment graph 把回答帶入新 revision；本 run 存在 `READY` / `READY_WITH_ASSUMPTIONS` revision 時回 `ready:true`，`context` 內含 `context_ref`。
- 錯誤碼：候選 envelope 或量測值不合法 → `400`；active policy 不存在 → `503`（submit、`GET /api/context-policies`、E3 delta 三處一致）；policy 內容無效 → `422`（三處一致）。`GET` 端點（revisions 與 views）皆回 ETag（revision）。
- SSE：只在既有 `orchestrator_run_event` 新增 `context.*` event_type；單調 cursor 與產生端遮蔽不變。

## 8. 角色與安全

- 新旗標 `CONTEXT_ENRICHMENT_ENABLED`：Backend / Workflow / Platform 各自獨立 fail-closed（預設 false），且依賴 `MULTI_AGENT_DISPATCH_ENABLED`。旗標關閉時新 API 全部 404、acquire 回到 E1 之前的舊路徑（§7）；**新路徑絕不在旗標關閉時放行**。
- 唯一進入點是 D5/D6 Root Orchestrator（`ContextAcquirer` seam）。Enrichment skill 是系統自有資產，三道既有防線（`required_role: ADMIN`、builtin `bindable:false`、builtin 名稱優先於 custom）已查證生效（C1a），不可被一般 skill invoke 繞過 Root 的授權與預算。
- 跨 tenant evidence、未授權 source、模型自創 ID 一律 hard fail，不降級為 gap。
- Enrichment 產出不寫入 mem0；mem0 recall 內容不作為 evidence（兩層記憶不互寫）。
- legacy chat（`/api/chat*` 與非 canary AG-UI）位元不變。

## 9. 非目標

同 01-plan §3，此處只列清單不重複理由：metric / peer / benchmark 檢索、Industry Pack 檔案樹、legacy chat 變更、Redis / MinIO / Graph DB、adaptive top-K 與跨 session 學習、取代 mem0、第二套預算 / 事件 / 鎖 / 旗標機制。

## 10. E2 保留節（阻擋中）

Metric definition version pinning、Entity resolution、fiscal calendar、多來源選擇與 conflict detection 的 WHAT，待 01-plan §11 Q1（結構化數據源）與 Q2（Entity Master 來源）定案後補入本節。在此之前 `metric_definition` 表只有 schema、沒有內容，任何以它為前提的契約都不成立。
