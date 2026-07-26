# Context Enrichment — 計畫書

> 狀態：**E1 + E3 已交付**（含符合性審查與修復輪）；E2/E4 阻擋於 §11 Q1–Q3。
>
> 來源設計文件：`context-enrichment-agentic-platform-design.md` v1.0（下稱「設計稿」），一份以 `MAF → LangGraph → Backend APIs` 為前提的通用架構規格。本計畫是它對照本 repo 現況（D1–D7 已交付）之後的落地版本：**保留設計稿附錄 A 的十條架構規則，刪掉本 repo 已經有的重複建設，並明確標示尚無資料來源、因此本期不做的部分。**
>
> 前提知識：[agent-platform-redesign/01-plan.md](../agent-platform-redesign/01-plan.md)（D1–D7 的領域模型與交付邊界）。本計畫是它的第八個交付階段，內文以 **E1–E4** 編號以免與既有 D1–D7 混淆。

---

## 1. 問題與現況判定

### 1.1 設計稿要解決的問題

模型在資訊不足時過早拆任務、多 Agent 重複檢索、KPI 定義版本漂移、Context 全量塞 Prompt、結論缺 Evidence 與可重現性、權限被模型擴張。解法是把 Context 從「聊天字串」升級成**版本化、可稽核、可投影的結構化 artifact**。

### 1.2 本 repo 的實際缺口只有一個

D5 Root Orchestrator 的執行骨架已經完整：它在拆任務之前會先呼叫 `POST /api/orchestrator-runs/{run}/context/acquire`，且該呼叫受 `max_context_rounds` 預算約束。但 Backend 端目前的實作是：

```csharp
// backend/src/Backend.Api/OrchestratorRuns/OrchestratorRunRepository.cs:309-312
// No implicit connector exists in Backend.  Do not echo caller/model context or invent
// provenance: an unavailable server-owned adapter is an explicit, safe insufficiency.
var missing = tools.Select(x => "context-tool:" + x).Concat(...).DefaultIfEmpty("context-adapter-unavailable").ToArray();
return new(false, EmptyObject(), Array.Empty<JsonElement>(), missing);
```

也就是說：**整條 Bootstrap Context Enrichment 的呼叫鏈、預算、授權檢查、provenance 契約與 fail-closed 行為都已經在了，唯獨沒有任何一個真正會回 `ready:true` 的來源。** Workflow 端的消費者與驗證也已存在：

| 既有元件 | 位置 | 現況 |
| --- | --- | --- |
| `ContextAcquirer` 可插拔 seam | `workflow/app/runtime/orchestrator.py:381-383` | 已是 `(snapshot, context, round) -> ContextAcquisition` 契約 |
| `ContextAcquisition` 契約 | `workflow/app/runtime/orchestrator.py:357-361` | 已有 `ready` / `context` / `provenance` / `missing` |
| Provenance 越權檢查 | `workflow/app/runtime/orchestrator_backend.py:291-301` | 已強制 `source_id` ⊆ snapshot authority |
| Round 預算 | `workflow/app/runtime/orchestrator.py:420-423` | 已有 `max_context_rounds` 上限與耗盡處理 |
| 澄清回流通道 | `backend/.../OrchestratorRunRepository.cs:308` | `current_context.user_input` 已可短路回 `ready:true` |

**結論：Context Enrichment ＝ 補上那個 server-owned read-only adapter，並讓它產出的 context 成為可版本化、可稽核、可重播的 artifact。不是新建一個平行的 Agent 架構。**

> **實作期修正（重要）**：本節「沒有任何來源會回 `ready:true`」的判定當初不完整——Workflow 的 `ProductionRootPlanner` 一直有一條**本地** acquirer（LLM 澄清評估 + scoped 檢索），能在 Workflow 內回 `ready:true`；Backend 端才是永遠 not-ready。E1 交付後的最終形狀：旗標開啟 → server-owned Context Store 路徑；旗標關閉 → 舊本地 acquirer 與 Backend `user_input` 短路**位元不變地保留**（configuration-only rollback 成立）。

### 1.3 阻擋性前提：本 repo 沒有 structured data plane

設計稿假設存在 `finance_dw`、Entity Master、KPI Catalog、Peer Benchmark DB。本 repo 目前**一個都沒有**。唯一真實存在的權威資料來源是：

- `documents` + `rag_chunks`（pgvector HNSW），經 `POST /api/retrieval/search`，已有 `scope_contract_version:1` 與 `knowledge_sources` 的 fail-closed 範圍限制。

因此設計稿 §14.4（Metric APIs）、§14.8（Peer / Benchmark APIs）、§10.12 的五路平行檢索、§13（Industry Pack）在本 repo **無法實作，只能定契約留空**。這不是可以靠寫程式解決的問題，是資料問題。本計畫把它列為 E2 的開工前提（見 §11 Q1）。

誠實的期望值設定：**若永遠沒有結構化數據源，Context Enrichment 的收益上限是「文件證據的版本化 + readiness gate + 最小 Context 投影」。** 這仍然有價值（它直接解決「過早拆任務」與「Claim 無 Evidence」兩個問題），但不是設計稿描繪的產業分析平台。

---

## 2. 設計不變量

以下四條是本計畫的硬約束，任何實作決策與後續文件都不得違反。它們排在目標之前，因為它們會否決掉設計稿裡好幾個看起來合理的做法。

| 不變量 | 在本計畫的具體意義 | 被它否決掉的做法 |
| --- | --- | --- |
| **I1 Node-First** | Enrichment 的每一個步驟都是註冊在既有 registry 的 `@node`，宣告 `reads`/`writes`/`deps`/`appends`；圖由宣告式 YAML 組成。節點函式之外不得有任何協調用 Python 迴圈或分支 | 自建 `StateGraph`、在單一大函式裡串流程、用 `if/else` 取代 `add_conditional_edges`、用未註冊的私有 helper 偷渡步驟 |
| **I2 商業邏輯只能在 Backend** | 「需要什麼、允不允許、正式語意是什麼、衝突怎麼解、夠不夠」全部由 Backend 決定並持久化。Workflow 只負責「怎麼取得與組裝」 | **Readiness 判定寫在 Workflow**（原草稿的 C10，已改）、把權重/門檻寫成 Python 常數、在節點裡硬寫 source precedence、由 LLM 決定資料夠不夠 |
| **I3 適當抽象化 + 重用既有程式碼** | 先找既有實作（§5 的盤點表），沒有才寫；重複到第 N 份時抽共用，但不為單一使用者發明抽象 | 新開一套 DI 容器、新開一套預算/事件/鎖機制、為單一實作定義介面、把已存在的 kb-query 節點重寫一遍 |
| **I4 只留有價值的測試** | 一個測試要能在「這段邏輯壞掉」時失敗，且不與既有測試重複。既有機制（canonical SHA、cascade cancel、flag 404）若原樣複用，就不為它再寫一份 | 為每張新表複製一份 SHA 驗證測試、為 getter/setter 寫測試、驗框架而非驗自己的規則 |

I2 是四條裡影響最大的。它把設計稿 §16「Context Quality Gate」整節的歸屬改掉了——見 C10。

## 3. 目標 / 非目標

### 目標

1. `POST /api/orchestrator-runs/{run}/context/acquire` 能在授權範圍內回 `ready:true`，且每一筆 context 值都帶可回溯的 provenance。
2. Context 成為不可變、版本化的持久化 artifact（`context_id` + `revision`），任何 Task 固定引用一個 revision。
3. Root Orchestrator 在 Readiness Gate 通過前不拆 Task；不通過時的三種出口（補資料 / 澄清 / 資料不足）都是明確狀態，不是靜默降級。
4. 每個 child 只拿到自己的 `ContextView`，不拿完整 Envelope。
5. 文件內容永遠以 untrusted evidence 區塊進入 Prompt，永遠不進 system instruction。
6. 跨 tenant evidence、未授權 source、模型自創 ID 一律 hard fail 而非降級。

### 非目標

- **不做 metric_series / peer / benchmark / dimension breakdown 檢索**：沒有來源（§1.3）。
- **不做 Industry Pack 檔案樹**（設計稿 §13.2）：單一產業時它是零收益的抽象；改為 `metric_definition.pack_id` 欄位，出現第二個產業時才長出 pack。
- **不動 legacy chat**：`/api/chat*` 與非 canary 的 AG-UI 路徑位元不變。Context Enrichment 只掛 D5/D6 Root Orchestrator 路徑。
- **不引入 Redis / MinIO / Graph DB**：既有 PostgreSQL + 文件儲存已足夠（見 C4）。
- **不做 adaptive top-K、memory promotion workflow、跨 session 學習**（設計稿 Phase 4）。
- **不取代 mem0**：mem0 是使用者層級的長期偏好；Context Store 是單次請求範圍的分析 artifact。兩者不互相寫入（設計稿 §18.6 同此結論）。
- **不新建第二套預算 / 事件 / 鎖 / 旗標機制**：全部沿用 D3–D7 既有實作。

---

## 4. 責任邊界：設計稿三平面 → 本 repo 四服務

| 設計稿平面 | 本 repo 服務 | 依據 |
| --- | --- | --- |
| Context Control Plane（MAF） | `platform/` 的 `AIContextProvider` + `AgentChatRoutingAgent`，以及 Workflow 內的 `RootOrchestrator` 決策層 | 本 repo 的「拆任務」責任在 D5 Root Orchestrator（Workflow 內），不在 platform。platform 只做身分、政策、旗標、傳輸 |
| Context Assembly Plane（LangGraph） | `workflow/app/nodes/context_enrichment/` + `app/skills/context-enrichment.yaml`（新增，走既有 Skill 引擎——見 C1） | 仿 `app/nodes/kbquery/` 的節點族結構 |
| Authoritative Data Plane（Backend APIs） | `backend/src/Backend.Api/`（新增 `Contexts/` feature folder） | 沿用 feature folder + Dapper + canonical bytes/SHA 慣例 |

> **設計稿 §2.4 的責任規則在本 repo 的對應（依 I2 加嚴）**：Root Orchestrator 決定何時需要 Context；Enrichment 節點決定**如何取得與組裝**；Backend 決定**需要什麼、是否允許取得、資料的正式語意、衝突怎麼解，以及夠不夠**。設計稿把最後一項（Quality Gate）放在 LangGraph 側，本計畫依 I2 移到 Backend。

逐項歸屬，避免實作時再爭論：

| 決策 | 歸屬 | 說明 |
| --- | --- | --- |
| 需要哪些 requirement | **Backend** | requirement 樣板是業務政策（C11） |
| 允不允許取得（tenant / ACL / source / tool） | **Backend** | 已完成，不動 |
| 資料的正式語意（metric 定義、單位、財務曆、時間政策） | **Backend** | C11 |
| 衝突怎麼解（source precedence） | **Backend** | C11 |
| Context 夠不夠（Hard Gate + Readiness） | **Backend** | C10——本計畫相對設計稿最大的一處改動 |
| 指標數值怎麼算 | **Backend** | C11b |
| 圖怎麼跑、檢索怎麼發、去重怎麼合、投影怎麼渲染 | Workflow | 機制，不是業務 |
| 意圖抽取、候選重排 | Workflow（LLM） | 兩者都不得產生 ID，也不得決定夠不夠 |
| 期間字串剖析 vs 期間語意解析 | **兩邊各一半** | Workflow 用既有 `kbquery/textutils.find_periods` 把文字剖析成候選期間 token（機制，可重用）；Backend 才依財務曆與時間政策把 token 解析成絕對起訖日（業務）。「最近一季」等於什麼，永遠是 Backend 說了算 |
| 身分、旗標、傳輸 | Platform | 已完成，不動 |

一個重要偏離要記錄：設計稿把 Planner 放在 MAF（platform）。本 repo 的 planner 是 Workflow 內的 `RootOrchestrator._decompose`。**本計畫遵循本 repo 現況，不把 planning 搬到 platform**——搬遷成本高、收益為零，且會破壞 D5 已交付的 snapshot 授權模型。

---

## 5. 重用盤點：設計稿的需求，本 repo 已經有的部分

這是本計畫最重要的一節。**下表左欄若已有實作，實作階段一律禁止另建平行機制。**

| 設計稿需求 | 本 repo 既有實作 | 判定 |
| --- | --- | --- |
| TaskEnvelope（§8.4） | `backend/src/Backend.Api/OrchestratorRuns/OrchestratorTaskEnvelope.cs:23-36`，含 canonical JSON、欄位白名單、`write_intent`/`delegation_depth` fail-closed | **擴充**，加一個 `context_ref` 欄位 |
| Evidence 的 source + hash（§8.8） | 同檔 `:33` 的 `context_provenance`：`{context_key, source_type, source_id, observed_at, content_sha256}` | **已是 EvidenceRef 形狀**，升級為持久化實體 |
| 不可變 revision + canonical bytes + SHA（§6.3、§15.3） | `agent` / `agent_revision` / `workflow_revision` / `skill_revision` 的既有樣板（backend/AGENTS.md「Agent Registry storage」） | **照抄樣板** |
| Optimistic concurrency（§10.20） | `If-Match` / ETag，缺 → 428、過期 → 409 | **直接沿用** |
| Bounded Expansion 預算（§6.5） | `orchestrator_token_cap`（hash-protected）、`max_context_rounds`、child budgets、result caps | **直接沿用**，不新建預算系統 |
| Deadline 向下傳遞 / Cancellation（§10.12） | `RootOrchestrator.execute(remaining_deadline_seconds=...)`、cascade cancel | **直接沿用** |
| Checkpoint / Retry / Interrupt（§24.5） | `app/runtime/checkpoints.py`（PostgresSaver + HMAC 簽名 checkpoint ref）、lease generation → thread id | **直接沿用** |
| SSE 事件 + cursor + 伺服器端遮蔽（§20.6） | `orchestrator_run_event` + 單調 cursor + 產生端遮蔽 | **直接沿用**，只加 `context.*` event_type |
| Resolve Security Scope（§10.2） | `InternalTokenMiddleware`、`RequireTenant()`、`X-User-Capabilities` 的 `knowledge.read:<docId>`、scoped retrieval fail-closed | **已完成**，Enrichment 只負責在投影前再檢一次 |
| Document / RAG API（§14.7） | `POST /api/retrieval/search` + `scope_contract_version:1` + `knowledge_sources` | **直接沿用**，唯一真實來源 |
| **整條 Enrichment 流程的同構實作** | `workflow/app/nodes/kbquery/` 已經是「解析 → 檢索規劃 → 正規化/重排 → 品質檢查 → 打包」的生產級節點族：`context_resolver`（`nodes/context_resolver.py:35-51`，確定性解析期間/口徑版本/canonical 指標，**資料不足只寫 `unresolved_context`，不猜測**）、`retrieval_planner`（依 `failure_codes` 調整策略）、`evidence_verification`（確定性 gate + `FailureCode` enum，不 PASS 不給實質答案）、`AuditTrail`/`Evidence` 固定欄位模型 | **直接擴充這個節點族**（見 C1），不新開引擎 |
| Deterministic First（§6.2） | 同上 + `calculator.py` 確定性計算、`GlossaryPort`/`StaticGlossary`（`ports.py:27-36`、`adapters.py:28-52`） | **照抄樣板**；metric/entity 字典直接掛既有 glossary port |
| 受限重試迴圈（§10.17 Expansion） | Skill YAML 的 `loop: {max_iterations, until}` + AST 安全求值（`app/engine/expressions.py`），編譯器強制附加稽核收尾（`compiler.py:481-495`） | **直接沿用**，不自刻 loop/join |
| 節點治理（reads 過濾、fatal 短路、trace、writes 剝除） | `@node` 契約（`app/engine/node_registry.py:67-123`）+ `harness.py:102-146` | **免費繼承** |
| 三值 unknown 紀律（§19） | `workflow/app/business_rules/` 的純評估器：unavailable fact → `unknown` → 顯式 `onUnknown`（預設 deny） | **沿用紀律**，不重寫引擎 |
| MAF Context Provider（§12） | `platform/src/Platform.Service/ChatContextProvider.cs:27,46`（已是 `AIContextProvider`，注入 `Instructions` 而非 `Messages`） | **擴充既有類別**，不新建 provider 抽象 |
| 旗標 fail-closed（§23） | 六個既有旗標，全部在認證前的中介軟體回 404（`platform/src/Platform.Web/Program.cs:60-72, 453-546`） | **照抄樣板** |
| HITL 澄清（§9.3 `NEEDS_CLARIFICATION`） | D3/D5/D6 的 `waiting_input` + 嚴格 checkpoint identity + `current_context.user_input` 回流 | **直接沿用**，不新建 interrupt |

**設計稿 27 節裡真正需要新寫的，只有五樣：Context Store（Backend）、Readiness 政策與判定（Backend，I2）、Catalog 資料（Backend）、Enrichment 節點族（Workflow，且是擴充 kb-query 而非新引擎）、View 投影。其餘都是既有機制的接線。**

---

## 6. 架構決策

| ID | 決策 | 理由 |
| --- | --- | --- |
| C1 | **Enrichment 用既有 Skill 引擎實作，不自建 `StateGraph`**：節點放 `workflow/app/nodes/context_enrichment/`（仿 `nodes/kbquery/` 子套件結構），以一份系統自有的 `app/skills/context-enrichment.yaml` 串成 flow + `loop`，DI 擴充既有 `KbQueryDeps` | kb-query 節點族已是同構問題的生產驗證實作（見 §5）。走既有引擎可免費繼承 reads 過濾、fatal 短路、trace、writes 剝除、bounded loop、安全條件求值與強制稽核收尾。**曾考慮並否決**：(a) 自建 `app/context_enrichment/` StateGraph——會複製一份既有治理，且 enrichment 是同步一次性呼叫、不需要自己的 checkpointer（澄清暫停由 root 的 `waiting_input` 負責，見 C9）；(b) 走 D4 Graph IR——它明文禁止內嵌 prompt/adapter，且那是使用者可視編輯的契約 |
| C1a | 該 skill 是**系統自有、非使用者資產**。經查證，三道防線**全部已經存在，本計畫零新增程式、零新增測試**：① `required_role: ADMIN`（既有頂層欄位，`app/skills/analyze-report.yaml:3` 已在用）；② builtin 的 `bindable` 恆為 false（`GET /skills` 既有 fail-closed 規則）；③ 同名覆寫不可能——invoke 先查 builtin，`skills.get(name)` 命中就不會呼叫 `custom.load`（`workflow/app/main.py:482-490`） | 採用 C1（走既有 Skill 引擎）原本的疑慮是「enrichment 可能被當成一般 skill 直接 invoke，繞過 Root 的授權與預算」。查證後這個疑慮不成立，C1 的代價實際為零。依 I4，既有機制不再補測試 |
| C2 | Context Store 由 Backend 擁有（Dapper + in-memory 雙路徑），沿用 `agent_revision` 的 canonical bytes + SHA256 + 定時比對驗證 | Backend 是資料與權限的唯一權威。Workflow 目前只寫 checkpoint，維持這條界線 |
| C3 | **3 張核心表 + 1 張政策表 + 2 張 catalog 表（共 6 張）**，不是設計稿 §15.2 的 25 張：`context_revision`、`context_evidence`、`context_view`、`context_policy`（C18）、`source_catalog`、`metric_definition` | requirements / facts / gaps / conflicts / assumptions 全部內嵌 envelope canonical JSON——它們沒有獨立查詢需求，拆表是投機性正規化。lineage 是 evidence 的欄位不是表（整條鏈在寫入時已知） |
| C4 | 不引入 Redis / MinIO。文件 evidence 存 `content_ref` 指向既有 `documents`/`rag_chunks` 主鍵；**單筆 evidence** 的結構化 payload 內嵌 JSONB 並沿用 65 536 bytes 上限（整份 envelope 的 256 KB 上限另見 §10） | 既有 PostgreSQL 已同時是文件庫、向量庫與 revision 庫。E1 不做快取；若之後量測顯示需要，cache key 必須含 entitlement fingerprint（設計稿 §15.5） |
| C4a | Evidence 只落地**客觀量測**（`observations`），不落地分數。`freshness_score` / `authority_score` / `consistency_score` 這類導出值由 Backend 依當時政策計算，不寫進 evidence 列 | I2：分數是政策的函數，政策會改版。把分數凍結在 evidence 上等於把業務規則複製到資料裡，之後改門檻就得回填歷史資料 |
| C5 | 唯一進入點是 D5/D6 Root Orchestrator。Enrichment 掛在既有 `ContextAcquirer` seam（`orchestrator.py:381`） | 一個注入點、零拓樸改動、既有預算與授權自動生效。legacy chat 不受影響 |
| C6 | 新旗標 `CONTEXT_ENRICHMENT_ENABLED`，Backend / Workflow / Platform 各自獨立 fail-closed，且依賴 `MULTI_AGENT_DISPATCH_ENABLED` | 沿用既有六旗標樣板。沒有 Root Orchestrator 就沒有 Enrichment 的消費者。**實作前先確認一件事**：`workflow/app/settings.py:41-54` 目前**沒有** `agent_chat_enabled` 欄位，D6 在 Workflow 端實際共用 `multi_agent_dispatch_enabled`；根 `AGENTS.md` 與 `workflow/AGENTS.md:81` 宣稱的「三服務各自獨立旗標」在 Workflow 端與程式碼有落差。新旗標要嘛真的在 `settings.py` 落地，要嘛比照現況並修正文件——不要再複製一個只存在於文件的旗標 |
| C7 | **Source ID 命名必須與 Orchestrator snapshot 的 `authority.context_tools` / `knowledge_sources` 對齊** | 硬約束不是建議：`orchestrator_backend.py:291-301` 已經會拒絕越權 provenance。`source_catalog.source_id` 若與授權欄位對不上，整條鏈直接失敗 |
| C8 | TaskEnvelope 擴充：`OrchestratorTaskEnvelope.cs:26` 的欄位白名單加入 `context_ref {context_id, revision, view_id}`；`context` 縮成 view 投影後的最小資料；`context_provenance` 由 Context Store 產生，不再由 caller 提供 | 避免 envelope 與 Context Store 兩份真相。View 是唯一投影來源 |
| C9 | `NEEDS_CLARIFICATION` → root `waiting_input`，使用者回答經既有 `current_context.user_input` 回流 | 不新建 interrupt 機制；D6 已有「回傳第一個伺服器界定的澄清問題」的行為 |
| **C10** | **Readiness 的判定權在 Backend，不在 Workflow（I2）。** Workflow 只提交「候選 envelope + 客觀量測值」（覆蓋了哪些 requirement、每筆 evidence 的 authority/freshness/completeness、衝突清單）；`POST /api/contexts/{id}/revisions` 由 Backend 套用政策後**回傳權威 `status` + `readiness` + `unmet_requirements[]`**，並在同一交易內持久化。Workflow 的 expansion 迴圈以這個回傳值當 `until` 條件 | 原草稿讓 Workflow 算分、Backend 只重驗——那是同一條業務規則的兩份實作，違反 I2 也違反 I3。改成單一實作後，Workflow 端只剩量測與組裝，沒有任何門檻常數。副作用是**少寫一份程式**，不是多寫 |
| C11 | 政策內容一律是 Backend 擁有的資料，不是任一服務的程式常數：Readiness 權重與門檻、source precedence（衝突怎麼解）、requirement 樣板（哪種分析能力需要哪些 requirement）、時間解析政策（「最近一季」＝最新已結束財務季度，設計稿 §10.7）、單位/幣別正規化規則版本 | I2。這些每一條都是「業務怎麼認定」，不是「程式怎麼跑」。放 Workflow 就等於把業務規則藏進 Python |
| C11a | Workflow 透過一支唯讀的 `GET /api/context-policies` 取得上述政策後才規劃 requirement；政策不可用時 fail closed（不得用內建預設值頂替） | 內建預設值 = 悄悄把業務規則搬回 Workflow。fail closed 才守得住 I2 |
| C11b | 指標數值由 Backend 計算（它同時擁有 formula 與資料）；Workflow 不做業務算術，只做組裝與敘述化 | I2 + 設計稿 §6.2「數值計算不由 LLM 也不由組裝層決定」。C13 的「程式計算」因此落在 Backend |
| C12 | Entity / Industry / Peer resolution E1 **不做** | 設計稿 §10.4 自己禁止 LLM 創造 entity_id；repo 沒有 entity master，E1 做出來只會是無來源的猜測。E2 有種子資料才開 |
| C13 | 數值型 Grounded Fact 一律由程式計算、且計算方是 Backend（C11b）；Workflow 的 LLM 只做候選重排與**已由 Backend 算好**的數值的敘述化。`workflow/app/nodes/kbquery/calculator.py` 的確定性風格仍是節點層的樣板，但不得在其中新增業務算式 | 設計稿 §6.2、§10.18 + I2 |
| C14 | Prompt 分區塊渲染：`[SYSTEM_POLICY]` `[TASK]` `[DOMAIN_DEFINITIONS]` `[STRUCTURED_FACTS]` `[UNTRUSTED_EVIDENCE]` `[CONFLICTS_AND_GAPS]` `[OUTPUT_SCHEMA]`；文件內容永遠在 `UNTRUSTED_EVIDENCE`。token 上限以既有 `orchestrator_token_cap` 為準 | 設計稿 §17.4、§18.1。不新建第二套 token 預算 |
| **C15** | **ETag / If-Match：抽共用**。既有三份（`AgentController.cs:282-306`、`WorkflowController.cs:37,39`、`OrchestratorController.cs:22-24`，其中 Orchestrator 還把同一判斷內聯在單行重複）語意等價但寫法分歧、錯誤訊息中英夾雜。新增第 4 份前，在 `Common/` 抽兩個 extension method（`RequireIfMatchVersion()` / `SetVersionETag(long)`），**只給新 Context controller 用，不回頭改既有三份** | I3 的「適當」在此有明確界線：Rule of Three 已觸發，抽 10 行無分支邏輯成本極低；但既有測試可能斷言了確切錯誤字串，回頭統一是為了整潔而承擔契約破壞風險——不做 |
| **C16** | **Canonicalizer：複用不抽象**。key-sort 遞迴直接呼叫 `AgentCanonicalizer.CanonicalizeDefinition`（`Orchestrators/OrchestratorDtos.cs:28` 已經是這個做法，本 repo 自己建立的慣例）；Context 專屬的欄位驗證獨立寫在 `Contexts/ContextCanonicalizer.cs`。**不要發明 `ICanonicalizer` 或共用基底類別** | `AgentCanonicalizer` 587 行、`WorkflowCanonicalizer` 50 行，真正重複的只有約 12 行 key-sort，其餘 90% 是各 aggregate 的語意驗證。為 12 行發明介面是 I3 明文禁止的「為單一問題發明抽象」 |
| **C16a** | **Python 端算 canonical hash 一律用 `workflow/app/canonical_json.py`（UTF-16 ordinal 排序，對齊 .NET `StringComparer.Ordinal`），不要自己寫 `json.dumps(sort_keys=True)`** | 稽核原本查出 repo 有**兩份排序語意不同**的跨服務 canonical JSON 實作，對非 BMP 字元 key 已實測分岔。**此問題已於本計畫開工前修掉**：兩份統一到新的共用模組 `app/canonical_json.py`，並留下 `workflow/tests/test_canonical_json.py` 的 parity 測試釘住。Context Store 的 canonical bytes 必須與 Backend 的 `AgentCanonicalizer` 位元一致，所以新程式一律走這個唯一事實來源。**注意**：.NET 端只排 object key、**保留 array 順序**（`AgentCanonicalizer.cs:552`），所以陣列的排序鍵只需在 Workflow 內部確定性，不需對齊 .NET |
| **C17** | **Revision publish 交易：複製樣板，不抽跨 aggregate 框架**。複製「FOR UPDATE 鎖 → 驗版本 → reference lock（FOR SHARE）→ supersede → insert revision → 更新指標欄」六步驟；若 Context 也有 Publish/Restore 共同邏輯，比照 `OrchestratorRepository.WriteRevision` 在**同一檔案內**抽私有方法 | 三個既有 revision 表的欄位差異很大（skill binding / ui_metadata / verifier 參照），鎖定順序本身也是各自的業務規則。抽共用等於要把 INSERT 欄位清單參數化成 mini-ORM，成本遠大於維持三份百行內的原生 SQL |
| **C18** | **Readiness 政策的存放：仿 `configuration_set` 形狀新建一張 `context_policy` 表，不塞進既有表**。`app_config` 無 tenant 概念（`DbBootstrap.cs:74-76`）不適合；`configuration_set` 的形狀（tenant 隔離 + jsonb + 逐鍵白名單驗證，`Configuration/ConfigurationValues.cs:20-70`）正合用，但 key 白名單、業務語意、生命週期都不同，**共用同一張表會讓兩套白名單互相污染** | I3：複用「形狀」而非複用「表」 |
| C18a | **E1 的 `context_policy` 是種子資料，沒有編輯 API、沒有 revision 表**。要能編輯政策時（E4）才加 draft+revision 雙表 | 現有兩張組態表都沒有變更歷史。業務政策長期需要稽核軌跡，但 E1 沒有人能改它——先不蓋。這是刻意的延後，不是遺漏 |
| C19 | **in-memory 雙路徑：實作複製既有樣板（`Data/InMemory/InMemoryConfigurationSetRepository.cs`，127 行、`lock (_store)` 手動重建 UNIQUE 與唯一 active）；但測試寫成一個吃 `IContextRepository` 的參數化 xUnit Theory，同時跑兩份實作**（Dapper 半邊沿用 `PostgresFixture` + `SkippableFact`，DB 不可達即 skip）。**範圍僅限 Context 這一組 repository，不做全 repo 的契約測試框架** | 初稿寫「複製樣板、各測各的」，被證據推翻：`plans/test-audit/ledger.md` 記錄了 `InMemoryAgentRunApprovalRepository.CompleteExecuteAsync(deadLetter:true)` 與 Dapper 版行為分歧（run 卡在 `queued` 而非 `failed`），**是人工稽核抓到的，沒有任何測試擋下**。這個 bug class 是真的。但修法不是發明全 repo 框架（那比新增整個 Context repository 還大，I3 禁止），而是一個測試類別吃兩個實例——這不是框架，成本近乎零，且正好符合 I4「這段邏輯壞掉時會失敗」 |

---

## 7. 資料模型

沿用 Backend 既有 `DbBootstrap` 冪等建表慣例（不引入 migration framework），並補 in-memory repository 契約測試（lite 模式行為必須一致）。

```sql
-- 核心三表
context_revision(
  context_id uuid, revision int, tenant_id text, root_run_id uuid null,
  status text,                    -- 完整狀態集見 §8：NEED_MORE_CONTEXT / NEEDS_CLARIFICATION /
                                  -- BLOCKED_BY_POLICY / INSUFFICIENT_DATA / READY / READY_WITH_ASSUMPTIONS
  canonical bytea, sha256 text,   -- 權威 UTF-8 canonical bytes，與 agent_revision 同樣板
  definition jsonb,               -- 查詢投影用，非權威
  as_of timestamptz,
  readiness numeric, policy_id uuid,  -- 判定結果 + 當時所用的政策。兩者必須成對存：
                                      -- 沒有 policy_id 的分數在政策改版後無法解釋（C4a 的例外——
                                      -- 這裡存的是「當時做了什麼決定」，屬稽核事實，不是導出值）
  created_at timestamptz, expires_at timestamptz,
  PRIMARY KEY(context_id, revision))

context_evidence(
  evidence_id uuid PK, context_id uuid, revision int, tenant_id text,
  evidence_type text, source_id text, snapshot_id text,
  content_ref text,               -- 例：document://{documentId}#chunk/{chunkId}
  content_hash text,              -- sha256，直接對應 task envelope 的 content_sha256
  scope jsonb,
  observations jsonb,             -- 只存客觀量測：retrieved_at、命中筆數、缺漏欄位、
                                  -- 來源 authority_class。分數由 Backend 依政策導出，不落地成 evidence 欄位（I2）
  acl_decision_id text, observed_at timestamptz,
  lineage jsonb)                  -- 整條 source→snapshot→extraction→normalization，寫入時已知

context_view(
  view_id uuid PK, context_id uuid, revision int, tenant_id text,
  view_type text,                 -- planner / worker / verifier / synthesizer
  canonical bytea, sha256 text, definition jsonb)

-- 政策一表（I2：業務邏輯的落腳處。E1 為種子資料，無編輯 API、無 revision 表——C18a）
context_policy(                   -- 形狀仿 configuration_set，但是獨立的表（C18）
  id uuid PK, tenant_id text, name text, is_active bool,
  values jsonb,                   -- readiness 權重/門檻、source precedence、requirement 樣板、
                                  -- 時間解析政策、正規化規則版本；逐鍵白名單驗證
  created_by text, created_at timestamptz, updated_at timestamptz,
  UNIQUE(tenant_id, name))

-- Catalog 二表（E1 只種一筆來源；metric_definition 到 E2 才有內容）
source_catalog(source_id text PK, tenant_id text, source_type text, authority_class text,
  date_coverage jsonb, freshness_sla jsonb, adapter_id text, acl_policy_id text, enabled bool)
metric_definition(metric_id text, definition_version text, pack_id text, tenant_id text,
  formula jsonb, unit text, aggregation text, grain text, dimensions jsonb,
  PRIMARY KEY(metric_id, definition_version))
```

設計稿 §15.2 另外 20 張表的處置：`context_item` / `context_requirement` / `context_view_item` / `grounded_fact` / `fact_evidence_link` / `context_gap` / `context_conflict` / `context_assumption` → **內嵌 envelope canonical JSON**；`evidence_content_ref` / `evidence_lineage` / `evidence_acl_decision` → **evidence 的欄位**；`industry_pack*` / `entity_*` / `peer_group*` → **E2 之後且需先有資料來源**；`retrieval_job` / `retrieval_step` / `retrieval_trace` → **既有 `orchestrator_run_event` 已涵蓋**；`memory_item*` → **mem0 已涵蓋，不重建**。

---

## 8. 目標執行流程

```text
使用者訊息（D6 canary 租戶）或 SYSTEM_ADMIN 測試啟動（D5）
  → Backend durable 配置 Root run，Workflow claim（既有）
  → RootOrchestrator.execute(context_round=1)
      → ContextAcquirer  ←── 本計畫接在這裡
          → Enrichment Graph（app/skills/context-enrichment.yaml，節點在 app/nodes/context_enrichment/）
              validate_job          Deterministic，不合法直接終止，不進任何 LLM node
              resolve_security_scope 從既有 identity headers + capabilities 取得，不由 LLM 決定
              normalize_request      規則優先 + LLM 輔助意圖抽取
              resolve_time           絕對時間基準來自 runtime timestamp，非模型推理
              build_requirements     語意需求 → 資料需求；此節點禁止產生 SQL/URL/table name
              discover_sources       查 source_catalog ∩ snapshot.authority
              retrieve_documents     既有 /api/retrieval/search（scope_contract_version:1）
              normalize_evidence     正規化，保留原始值與正規化規則版本
              deduplicate_evidence   canonical key 去重（是節點不是 reducer——見 §9 Workflow 工作項）
              detect_conflicts       不靜默覆蓋，衝突必須進 verifier view
              build_facts            數值由 Backend 算好後才敘述化（C11b）
              build_views            planner / worker / verifier 投影
              submit_revision        POST /api/contexts/{id}/revisions
                                     ↑ 只送候選 envelope + 客觀量測值
                                     ↓ Backend 套政策後回傳權威 status（C10）
                 ├ NEED_MORE_CONTEXT 且 round 未用盡 → expand_requirements → 回 discover_sources
                 ├ NEEDS_CLARIFICATION → 交還 root（waiting_input）
                 ├ BLOCKED_BY_POLICY / INSUFFICIENT_DATA → root 終止（terminal，failed），不重試
                 └ READY / READY_WITH_ASSUMPTIONS → 回傳 context_ref
          → 回傳 {ready, context:{context_ref, view_id, ...}, provenance[], missing[]}
      → ready=false 且 round 用盡 → 既有 INSUFFICIENT_DATA 路徑
      → NEEDS_CLARIFICATION      → 既有 waiting_input（C9）
      → ready=true               → 既有 decompose → 每個 TaskEnvelope 帶 context_ref（C8）
  → Worker children 只收到自己的 ContextView
  → Verifier 收到 claims + evidence + definitions + conflicts（不含 worker 推理過程）
  → PASS-only 彙總（既有）
```

---

## 9. 交付階段

| 里程碑 | 完成後可用狀態 | 前提 | 驗收 | 規模 |
| --- | --- | --- | --- | --- |
| **E1 Context 骨架** | `context/acquire` 對文件來源回 `ready:true`；Context 版本化持久化；Backend 擁有 Readiness 判定；child 只拿 view | 無（今天就能做） | 18 項（[04-acceptance-tests](04-acceptance-tests.md) §1；初稿 21 項依 I4 刪 4 增 1） | XL |
| **E2 語意解析與 Catalog** | Metric definition version pinning、Entity resolution（候選由 Backend 出）、fiscal calendar、多來源選擇、conflict detection | **需先回答 §11 Q1/Q2**：是否有結構化數據源與 entity 種子 | A-CTX-23..39 | XL |
| **E3 Task-local 擴充** | `ContextRequest` / `ContextDelta` / 新 revision + optimistic concurrency；per-task view；`context.*` SSE | E1 | A-CTX-40..59 | L |
| **E4 品質與觀測** | Context Utilization、Need-More-Context After Dispatch Rate、golden case 套組、per-industry 門檻 | E2、E3 | A-CTX-60..79 | M |

依賴：E1 → E3 → E4；E1 → E2 → E4。E2 可與 E3 並行。

### E1 工作項（各服務）

**Backend**（`Contexts/` 新 feature folder）
- 三表 + `context_policy` + 兩 catalog 表，加進 `DbBootstrap.Ddl` 冪等建表（additive-only，沿用既有慣例）。
- Dapper 與 in-memory 雙實作；測試依 C19 寫成一個吃 `IContextRepository` 的參數化 Theory 同時跑兩份實作——不各寫各的，也不擴張成全 repo 框架。
- `Common/` 抽 ETag/If-Match 兩個 extension method 供新 controller 用（C15）；canonical key-sort 複用 `AgentCanonicalizer`（C16）；publish 交易複製六步驟樣板（C17）。
- **Readiness 政策與判定**（I2 的核心工作，原本錯放在 Workflow）：Hard Gate 條件、權重、門檻、source precedence、requirement 樣板、時間解析政策，全部是 Backend 的資料 + 一支純函式判定器。
- `POST /api/contexts/{id}/revisions`：套用政策 → 回傳權威 `status` / `readiness` / `unmet_requirements[]` → 同一交易內不可變寫入 + canonical SHA。
- `GET /api/context-policies`：Workflow 規劃 requirement 前唯讀取得政策；不可用時 fail closed（C11a）。
- `GET /api/contexts/{id}/revisions/{rev}`、`GET /api/context-views/{viewId}`。
- 改寫 `AcquireContextAsync`（`OrchestratorRunRepository.cs:301`）：從「永遠 not ready」改為讀取本 run 已持久化的最新 revision，產出 `ready` + `context` + `provenance`；沒有 revision 時維持既有 fail-closed 行為。
- `OrchestratorTaskEnvelope.cs:26` 欄位白名單加 `context_ref`（C8）。
- `source_catalog` 種子一筆 `backend_documents`，`source_id` 與 Orchestrator `knowledge_sources` 對齊（C7）。
- **`RetrievedChunk` 需增加 chunk 識別**：`Retrieval/RetrievalDtos.cs:25-29` 目前只回 `{document_id, title, content, score}`，`rag_chunks.id` 沒有投影出來。Evidence 要做到 chunk 粒度的 `content_ref` 與去重 canonical key，就必須把它加上（additive 欄位，既有呼叫端不受影響）。

**Workflow**（`app/nodes/context_enrichment/` + `app/skills/context-enrichment.yaml`，見 C1）
- 節點族仿 `nodes/kbquery/` 結構：`models.py`（固定欄位、無自由文字夾帶）、`ports.py`、`nodes/`。**§8 流程圖的每一行就是一個 `@node`**（I1）：節點外不得有協調用的 Python 流程控制，分支一律走 YAML 的 `when`/`loop.until` + `app/engine/expressions.py` 的安全求值。
- **Workflow 端不得出現任何門檻、權重、precedence 常數**（I2）。`submit_revision` 節點 `writes=["context_status", "unmet_requirements", "context_ref"]`，YAML 的 `loop.until` 直接寫成 `context_status != 'NEED_MORE_CONTEXT'`——既有 AST 安全求值器（`app/engine/expressions.py`）已支援這種比較，不需要擴充。判定值來自 Backend，Workflow 不重算。實作審查時這條要逐檔檢查。
- §8 的節點；能複用的直接複用而非重寫：`context_resolver` 的確定性解析與 `unresolved_context` 語意、`retrieval_planner` 的失敗碼驅動重規劃、`evidence_verification` 的 `FailureCode` gate、`local.rerank` 與 `backend.retrieval_search` 既有 tool、`search_chunks_scoped`（`app/backend_http.py:75-96`）。
- LLM 只出現在意圖抽取與候選重排，且沿用 `LangChainStructuredLLM`（`kbquery/adapters.py:55-91`）既有的「結構化輸出失敗即降級」模式。
- Expansion 迴圈用 YAML `loop: {max_iterations, until}`，不自刻。
- **去重是一個節點，不是 reducer**：既有 Skill 編譯器對 `appends` 只掛 `operator.add`（`app/engine/compiler.py:219-220`），不支援自訂合併函式。設計稿 §11.3 的 `merge_evidence` reducer 因此落成 `deduplicate_evidence` 節點；**不要為了它去改編譯器**（I3：一個使用者不值得動共用引擎）。
- 接上 `RootOrchestrator` 的 `acquire_context` 注入點（`orchestrator.py:398`）。
- **不需要**自己的 checkpointer：enrichment 是同步一次性呼叫，暫停語意在 root（C9）。

**Platform**
- `CONTEXT_ENRICHMENT_ENABLED` 旗標與認證前 404 中介軟體（照 `Program.cs:453-546` 樣板）。
- `GET /api/features` 增加對應布林。
- 本期 platform 不需要新 controller：Enrichment 不對外暴露公開 API，只透過既有 orchestrator run 事件被觀察。

**Frontend**
- E1 不做新 view。Context 摘要（readiness、evidence 數、gaps、assumptions）以既有 orchestrator run trace 的事件呈現即可。獨立 Context view 留到 E3 有 delta / per-task view 之後再評估。

### E1 驗收重點

**取捨原則（I4）**：只留「這段**新**邏輯壞掉時會失敗」的案例。既有機制若是原樣複用（canonical SHA 驗證、ETag 428/409、flag 前置 404、cascade cancel、scoped retrieval、root timeout），它們的行為已由既有測試釘住，本計畫**不再複製一份**。安全項不適用精簡——跨 tenant 與 prompt injection 一律保留。

**節點族的測試形狀**：比照 kb-query 現況——`test_kbquery_nodes.py` 做節點單元（每支只打一個節點，依賴用 fake 隔離），`test_skill_kbquery_parity_e2e.py` 做整圖端到端。後者有一段值得抄的歷史：它原本對 7 個案例各做全欄位 golden trace 比對，任一個 state 鍵改名就要重產 7 份快照、失敗訊息只會是「兩個超長 dict 不相等」；已經收斂成 **1 個代表案例做 golden + 其餘改驗結構性觀測點**。Enrichment 的整圖測試直接採用收斂後的形狀，**不要重蹈 N 份 golden trace**。

依此原則從初稿刪掉的案例，連同刪除理由記錄如下（每一條都已用既有測試位置確認過），避免日後被當成遺漏補回來。刪除留下的編號缺口（A-CTX-03 / 16 / 17 / 20）刻意不回填，讓「刪過什麼」在編號上留痕；E2 起的編號從 23 開始：

| 原案例 | 刪除理由 |
| --- | --- |
| canonical bytes/SHA 不符即 fail closed | C16 直接複用 `AgentCanonicalizer.ReadAuthoritativeDefinition`。既有 `AgentRunSnapshotContractTests.cs:236` 已把七種竄改形式（null bytes、hash 不符、BOM、非 canonical JSON、非嚴格 UTF-8…）全測過。複用而不改寫，沒有新風險 |
| root cancel 後 child 停止 | D5 cascade cancel 未被本計畫修改；`workflow/tests/test_root_orchestrator.py:584` 已覆蓋 |
| `If-Match` 缺失 428 / 過期 409 | C15 抽出的 helper 只是搬移既有邏輯，行為不變；`AgentsApiTests.cs:232/212` 與 `WorkflowAdminApiTests.cs:21/24` 已在兩個 aggregate 上覆蓋 |
| Enrichment skill 被直接 invoke | 三道防線都是既有機制且已查證生效（C1a） |

E1 驗收全表（A-CTX-01..22）移至 [04-acceptance-tests.md](04-acceptance-tests.md) §1，該檔是 A-CTX 編號與條目內容的唯一權威；本節只保留取捨原則、刪除紀錄與測試形狀指引作為歷史依據。

---

## 10. 風險與降級

| 風險 | 影響 | 處置 |
| --- | --- | --- |
| **沒有結構化數據源**（§1.3） | E2 之後的價值上限受限 | 開工前先回答 Q1。若答案是「沒有且短期不會有」，本計畫只交付 E1 + E3，並在 `plans/README.md` 誠實標示能力邊界 |
| Bootstrap 增加一輪往返 | Root run 延遲上升 | Enrichment 全程在既有 root timeout 內；optional retrieval 先被砍。目標：bootstrap p95 ≤ root deadline 的 1/3 |
| Envelope 與 TaskEnvelope 兩份 provenance | 雙真相漂移 | C8：view 是唯一投影來源，task envelope 的 provenance 由 view 產生、不手寫；加一個一致性測試 |
| Envelope 體積 vs 既有上限 | 大型分析爆量 | 既有硬上限：`MAX_TASK_CONTEXT_BYTES` 65 536、`MAX_ROOT_RESULT_BYTES` 983 040（`workflow/app/runtime/orchestrator.py:22-26`），以及 Backend `context`/`task_envelope` 各 65 536。Envelope 另設 256 KB 上限，超過即建 gap（A-CTX-13）；view 另有 `max_input_tokens`。**不得為了塞下而放寬既有常數** |
| ~~Enrichment skill 被當成一般 skill 直接 invoke~~ | — | **已查證不成立**：三道既有防線都已生效（C1a）。此風險撤除 |
| 旗標文件與程式碼落差（C6） | 以為有三層 fail-closed，實際只有兩層 | 實作 E1 時一併核實並修正 `workflow/AGENTS.md:80` 與根 AGENTS.md 的 D6 敘述 |
| in-memory 雙路徑成本 | 每條業務規則都要手動同步兩份實作。全 repo 目前只靠人工紀律，且**已經真的漏過一次**（`plans/test-audit/ledger.md` 的 `CompleteExecuteAsync(deadLetter:true)` 分歧） | C19：Context 這一組用參數化 Theory 同時跑兩份實作，把這個 bug class 擋在門外；但不擴張成全 repo 框架 |
| Enrichment 失敗使 Root 完全不可用 | 可用性倒退 | 旗標關閉即完整回退到既有 not-ready 行為（A-CTX-01）；Enrichment 不是 root 啟動的前置依賴 |
| PostgreSQL checkpointer 測試預設 skip | 新圖的 checkpoint 缺乏自動覆蓋 | 沿用 workflow/AGENTS.md 已記載的手動指令，並把 enrichment 的 checkpoint 測試放進同一組 |

---

## 11. 待決策（需人回答）

| # | 問題 | 影響 |
| --- | --- | --- |
| Q1 | 是否有（或計畫接入）真實的結構化指標來源（`finance_dw` 等）？ | 決定 E2 是否開工，以及 metric_series / peer / benchmark 契約是否只留空殼 |
| Q2 | Entity Master 的來源：人工匯入、從既有文件抽取、還是外部主資料系統？ | 決定 entity resolution 能否成立（C12）。沒有來源就不做 |
| Q3 | 目前服務幾個產業？ | 決定 Industry Pack 是否存在（非目標之一）。一個產業 → 只留 `metric_definition.pack_id` 欄位 |
| Q4 | Context revision 的保留與 TTL 政策？設計稿 §26.2 建議 planner 24h / session decision 30d / revision 依稽核政策 | 決定 `expires_at` 與清理工作 |
| Q5 | 確認「Enrichment 只掛 D5/D6 路徑、legacy chat 位元不變」是否符合預期？ | 若要覆蓋 legacy chat，範圍與風險大幅上升 |

Q1–Q3 阻擋 E2（E4 依賴 E2，連帶被擋）；未回答之前 E1 與 E3 仍可獨立進行。Q4 未定前，`expires_at` 先落地為 nullable、不排清理工作。Q5 是範圍確認：未獲相反指示前，預設維持「只掛 D5/D6、legacy chat 位元不變」。

---

## 12. 完成定義

E1 完成的定義是：在 `CONTEXT_ENRICHMENT_ENABLED=true` 且 canary 租戶下，一次 Root run 能夠——

1. 取得帶 provenance 的文件 evidence，
2. 產生不可變 `ContextEnvelope` revision，
3. 通過 Readiness Gate（或明確進入澄清 / 資料不足），
4. 讓每個 child 只收到自己的 view，
5. 全程可從 `orchestrator_run_event` 重建，
6. 且旗標關閉後行為與今天位元相同。

後續文件已撰寫：[02-spec.md](02-spec.md)（契約與狀態機，WHAT）、[03-design.md](03-design.md)（服務責任、schema 與節點契約，HOW）、[04-acceptance-tests.md](04-acceptance-tests.md)（A-CTX 全表的唯一權威）。與資料現實綁定的部分（E2 契約、E4 指標）在三份文件中都是**保留區段**，待 §11 Q1–Q3 定案後才補入——不預先發明沒有資料來源的規格。
