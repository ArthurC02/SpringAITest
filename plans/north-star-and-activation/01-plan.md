# P2 — 戰略決斷與能力啟用

> 狀態：**decision plan — 待產品決策；不含實作**。本文件不新增架構、不修改任何既有計畫的 authority；它是決策彙整與排程文件，引用既有計畫段落作為證據來源，不重寫其內容。若本文件與被引用來源衝突，以來源計畫為準。
>
> 背景（WHY）：[product-review-2026-08/00-findings.md](../product-review-2026-08/00-findings.md) 的 C3 發現——方案全部差異化 Agentic 能力（D1–D7、E1/E3、Eval）flag 預設關閉、D6 real-model 證據 blocked；工程重心（數位員工平台）與可用體驗（文件分析助手）分裂；O5 觸發器與通知未建，「數位員工」無法自動工作。本檔是對這個分裂做出「先決定哪條故事，再決定啟用順序」的排序計畫。（該檔案由另一代理同時撰寫，若連結尚未存在，待其落地後即生效——本文件的論證不依賴其逐字內容，只依賴 README 目前狀態表已可驗證的事實。）

---

## 1. 北極星決策框架

兩條推廣故事目前都部分可用、都不完整。這節不選邊，只把「選哪邊要付出什麼」攤開。

### 1.1 故事 A：「數位員工平台」（Digital Employee Platform）

依 [plans/README.md](../README.md) Current Delivery Map：

- **已具備能力**：D1–D7 的 Agent Registry、Business Rules、Direct Agent test-run、Workflow Designer、Root Orchestrator 多 Agent dispatch、write tools + approvals 全部**程式碼與契約已交付**，但都在 fail-closed flag 之後（[agent-platform-redesign/01-plan.md](../agent-platform-redesign/01-plan.md)）；E1+E3 Context Enrichment 已交付於 `CONTEXT_ENRICHMENT_ENABLED`（[context-enrichment/01-plan.md](../context-enrichment/01-plan.md)）；O1 Operations cockpit 已交付（[agent-architecture-improvements/04-operations-trigger-plan.md](../agent-architecture-improvements/04-operations-trigger-plan.md) §2）。
- **缺口**：
  - D6 real-model release evidence sign-off **已完成**（2026-08-09，ArthurC 具名核准；見第 2 節，權威記錄 [copilot-shared-core/05-release-evidence-plan.md](../copilot-shared-core/05-release-evidence-plan.md)）。
  - O4（Recovery 操作）**未實作**——沒有它，operator 對既有 recovery 演算法缺乏 SLO 可視性與有限操作動作；O2（Runs 中心）、O3（審批佇列）、O5（觸發器，one-shot schedule only）已交付（[agent-architecture-improvements/04-operations-trigger-plan.md](../agent-architecture-improvements/04-operations-trigger-plan.md) §1、§3、§6）。
  - E2（語意解析與 Catalog）blocked 於結構化數據源未定（[context-enrichment/01-plan.md](../context-enrichment/01-plan.md) §11 Q1–Q3）——若「數位員工」的工作內容需要指標/實體分析而非只有文件證據，這個缺口直接封頂其能力。
  - R6 legacy 退場未執行，兩套 chat brain 長期並存（[agent-platform-redesign/05-migration-and-rollout.md](../agent-platform-redesign/05-migration-and-rollout.md) §10）。
- **已完成的決策後動作**：D6 證據鏈（第 2 節 checklist）已於 2026-08-09 完成 sign-off；O2→O3→O5 觸發器建置（第 3 節）已交付（O5 為 one-shot schedule only）。剩餘缺口為 O4（Recovery 操作）與依第 4 節排序啟用對應 flag；Agent 平台對外仍是 flag-off、canary-only 的存在，直至各 flag 依 §4 gate 開啟。

### 1.2 故事 B：「文件分析助手」（Document Analysis Assistant）

- **已具備能力**：chat-to-skill routing 已交付且是預設路徑（[chat-skill-routing/01-plan.md](../chat-skill-routing/01-plan.md)）；node-first Skill 引擎與 `kbquery` 檢索節點族已交付（[node-first-skill-engine/01-plan.md](../node-first-skill-engine/01-plan.md)）；async 文件處理管線（RabbitMQ 202→ready）已交付並記入跨服務契約（root `AGENTS.md`）；Copilot shared core（AG-UI + SSE + mem0 長期記憶）**程式碼/契約已交付，release sign-off 已於 2026-08-09 完成**（與 D6 共用同一組 real-model 證據，ArthurC 具名核准；[copilot-shared-core/05-release-evidence-plan.md](../copilot-shared-core/05-release-evidence-plan.md) 為權威記錄）。
- **缺口**：本 repo**沒有結構化數據源**（`finance_dw`、Entity Master、Peer/Benchmark DB 一個都沒有）——這不是工程缺口，是資料缺口（[context-enrichment/01-plan.md](../context-enrichment/01-plan.md) §1.3）。若「文件分析助手」的產品承諾止於「文件證據的版本化 + readiness gate」，此缺口不擋路；若承諾包含指標/同業比較分析，此缺口直接擋住能力上限。
- **需要的決策**：先回答 [context-enrichment/01-plan.md](../context-enrichment/01-plan.md) §11 的 Q1（是否有/計畫接入結構化指標來源）、Q2（Entity Master 來源）、Q3（服務幾個產業）。沒有這三題的產品答案，E2 無法開工，「文件分析助手」的能力上限就永久停在「文件證據」，這件事需要被**明確承諾**而不是預設懸置。

### 1.3 決策依賴（互斥的下一步）

- **選故事 A（數位員工平台）** → D6 證據鏈（第 2 節）已於 2026-08-09 完成；剩餘優先項為 O4（Recovery 操作）與依第 4 節排序啟用對應 flag。E2 的 Q1–Q3 可以繼續懸置（O5/O2/D7 不依賴它）。
- **選故事 B（文件分析助手）** → E2 的 Q1–Q3（結構化資料源）必須先有產品答案，才能決定要不要繼續投資 Context Enrichment 之外的「分析」敘事；D6 證據（含 copilot-shared-core release sign-off）已於 2026-08-09 完成，不再是此路徑的前置阻塞。
- 兩條故事都不能靠「flag 保持關閉」迴避決策——fail-closed 是安全姿態，不是產品答案（呼應第 4 節開頭原則）。

### 1.4 決策時限與決策者

| 項目 | 內容 |
| --- | --- |
| 決策內容 | 選擇故事 A 或故事 B 作為 P2 的主推廣敘事（可並行維護、但工程優先序必須有主從） |
| 決策者 | 使用者（專案擁有者） |
| 決策時限/日期 | 2026-08-09 已決 |
| 決策結果 | **並行不分主從**（兩條敘事同時推進） |
| 決策後動作 | 依 1.3 節觸發對應的第 2 節或 §11 Q1–Q3 產品作答 |

依 §1.3,並行意味著 D6 證據補齊與 E2 的 Q1–Q3 產品作答皆成為待辦;E2 三問仍待使用者回答,D6 證據輪次已於 2026-08-09 完成(sign-off,見第 2 節)。

---

## 2. D6 證據解封路徑

本節只彙整 [copilot-shared-core/05-release-evidence-plan.md](../copilot-shared-core/05-release-evidence-plan.md) 與 [agent-platform-redesign/05-migration-and-rollout.md](../agent-platform-redesign/05-migration-and-rollout.md) 已定義的完成條件為單一 checklist，並標註目前狀態。**本計畫不修改那兩份文件的 authority；沖突時以原文件為準。**兩份文件描述的是同一個底層事實（D6 canary chat 與 copilot-shared-core AG-UI 共用同一組 real-model 證據缺口），因此合併成一張表。

**狀態更新（2026-08-09）**：release sign-off 已完成，ArthurC 具名核准；權威記錄為 [copilot-shared-core/05-release-evidence-plan.md](../copilot-shared-core/05-release-evidence-plan.md)（本文件不重寫其內容）。以下 2.1–2.3 為 sign-off 完成前的排程與判定記錄，保留供稽核，不代表目前仍 blocked。

### 2.1 Release sign-off checklist（來源：copilot-shared-core/05 §「Release sign-off 條件」）

| # | 條件 | 目前狀態 |
| --- | --- | --- |
| 1 | `E-01`～`E-06` 全部 PASS；零 FAIL/BLOCKED/SKIP | 個別項目歷史上都曾 PASS（E-01/E-02/E-03/E-06 為既有 rerun 累積 PASS；E-04/E-05 見 bundle `20260725T111722672Z-90c9119f`），但**不是同一組最新 rerun**——不滿足 |
| 2 | platform xUnit 391（Service 241 + Web 150）+ frontend lint/build + smoke 8/8 全綠 | **本次未執行，留白待補** |
| 3 | real-model lane 固定模型/embedding 設定、provider model ID、image identity、bundle 路徑記入 manifest；capture 與 secret scan 通過 | E-04/E-05 的單次 bundle 已滿足；**未與條件 1 的同一組 rerun 合併** |
| 4 | `code-reviewer` 與 fresh-image `e2e-verifier` 獨立檢查（無 response-only 假綠、無重複 tool result、無 cross-tenant 漏洩、無測試資料殘留） | **本次未執行，留白待補** |

**結論（歷史記錄，sign-off 已完成）**：上表為 sign-off 完成前的排程判定，保留供稽核。截至 2026-08-09，四項條件已於同一組可稽核 rerun 全數滿足，release sign-off 完成（ArthurC 具名核准；權威記錄見 [copilot-shared-core/05-release-evidence-plan.md](../copilot-shared-core/05-release-evidence-plan.md)）。

### 2.2 D6 canary rollout 前置（來源：agent-platform-redesign/05 §5 R3/R4）

D6 對應的 rollout 階段是 R3（Tenant canary chat）與 R4（Default Orchestrator）：

- R3：少數 tenant/user 顯式使用 Orchestrator，legacy 為預設，同步比較成功率/成本/延遲。
- R4：canary 通過後，才將 Default Orchestrator 設為未傳 `orchestratorId` 的解析目標；此步驟要求 chat 與 AG-UI **同時**通過 shared-brain gate（不得只遷其中一條就宣稱完成）。

「canary 通過」的判定材料就是 §2.1 的四項條件——本文件把兩份原本分開的文件（release-evidence 的證據 checklist、migration-and-rollout 的 rollout gate）**接成同一條時間軸**：R3 canary 的「通過」＝ §2.1 四項全綠；R4 才能開始。

### 2.3 排程建議

1. 先補齊 §2.1 條件 2（xUnit + lint/build + smoke）——這是最低成本、無需 real-model 額度的項目，且是條件 4 審查的前提（reviewer/e2e-verifier 不應在已知回歸的基準上做獨立檢查）。
2. 條件 2 綠燈後，執行一次同批 Deterministic + RealModel 兩個 lane 的 rerun（`scripts\verify-copilot-shared-core-evidence.ps1`，兩個 `-Lane` 都跑），產出同一 UTC-run-id 前綴下的完整 bundle 組，滿足條件 1 與條件 3。
3. 同批 bundle 產出後，派工 `code-reviewer` 與 fresh-image `e2e-verifier` 做獨立檢查（條件 4）。
4. 四項全綠後，[copilot-shared-core/05-release-evidence-plan.md](../copilot-shared-core/05-release-evidence-plan.md) 狀態改為完成，同時 R3 canary 視為通過，可評估 R4。

---

## 3. 自動工作故事（O5 + 通知）

依 [agent-architecture-improvements/04-operations-trigger-plan.md](../agent-architecture-improvements/04-operations-trigger-plan.md)：O1、O2、O3、O5 已實作（O2 於 2026-08-09 交付；O3 於 2026-08-09 由 admin-experience-downshift 計畫 W1 交付；O5 於 2026-08-09 以 dev-cycle 全管線交付：實作→三輪審查→簡化→e2e 22/22→文件，範圍為 one-shot schedule only，recurring/webhook 未做），O4 未實作。「數位員工能自動工作」不是單一 flag，是一條**依賴鏈**——依賴鏈只剩下方 §3.2 通知收件匣未做。

### 3.1 啟用順序與依賴

**O2 Runs 中心 → O3 佇列 → O5 觸發器**：

- **O2（Unified Runs and Tasks center，§3）**：先讓已存在的 direct Agent / Root/child run 可被跨 ID 發現（filter by status/kind/owner/Agent/Orchestrator、safe summary、cancel/resume/trace）。這是後續一切「自動」的可觀測地基——沒有它，觸發器建立的 run 只能靠日誌肉眼確認。
- **O3（Discoverable approval queue，§4）**：在 O2 的 run 投影上加「visible/actionable to me」分頁 API，重用既有 decision authorization（same tenant、required role、未過期、exact waiting state、separation-of-duties）。觸發器建立的 run 若進入 `waiting_approval`，操作者必須能在佇列中找到它——沒有 O3，觸發式建立的 run 一旦需要人工確認就等於卡死不可見。
- **O5（Durable triggers，§6）**：Backend 持有 trigger definition/revision/ETag、fire occurrence ledger、claim/lease；fire 只呼叫既有 D5 root creation/command path，不新建排程權威。§6.2 建議先做 one-shot/recurring schedule 或 signed webhook 兩者擇一，不同時做滿。

O4（Recovery operations）不在這條鏈的必要路徑上——它補的是 operator 對既有 recovery 演算法的可視性與有限動作，觸發器上線不強制依賴它，但正式擴大 O5 rollout 前建議一併補齊（O4 的 heartbeat/claim lag/quarantine 指標，能在觸發器造成的 run 量增加後更快定位卡住的 run）。

### 3.2 §7 通知收件匣

在 O2/O3 的 run/task 投影上加 in-app completion/escalation inbox。硬限制（沿用 §7 原文，不重寫）：

- Notification 是 **derived delivery record**，不是 execution authority；晚到或重送不得改 run status。
- Email/Teams 等外部 channel 需等 connector governance 完成才能做；不先把 credentials 放進 trigger model。

通知收件匣依賴 O2（要有 run/task 可投影）；不依賴 O5（沒有觸發器，run 完成/escalation 一樣需要通知）。因此通知可以與 O3 並行、甚至先於 O5 上線——它不是「觸發器的附屬品」，是「Runs 中心的自然延伸」。

### 3.3 為何排在 P2 而非 P0/P1

O2–O5 之所以不在更早的 tranche 就做，是因為它有一個尚未完成的前置 workstream：**`admin-experience-downshift` 計畫的 O3 workstream**（該計畫另有獨立文件記錄其範圍，本檔不重複其內容）。O2 的「Runs 中心」與 O3 的「審批佇列」在概念上與 admin-experience-downshift 的既有 O3 範圍重疊——必須先確認兩份計畫對 O3 的定義是同一件事還是需要合併，才能決定 04-operations-trigger-plan 的 O2/O3 是新建還是擴充既有 workstream。這正是「決斷」而非「架構」問題，所以排入 P2：先決斷「O3 到底是哪一份計畫的 O3」，再排工程序。

在此決斷完成前，O2–O5 的實作優先序維持 P0/P1 已完成項目（E0–E3、O1、S1、P1 assemblers）之後、且晚於第 2 節 D6 證據補齊——因為沒有 D6 canary 通過，Root Orchestrator 產生的 run 對「數位員工平台」故事而言連手動觸發的可信度都還沒建立，遑論自動觸發。

### 3.4 Flags（來源：04-operations-trigger-plan §8）

| Flag | 預設/策略 |
| --- | --- |
| `OPERATIONS_COCKPIT_ENABLED` | 延續既有 admin/operations gate，先 UI canary（O1，已實作） |
| `RUN_DISCOVERY_ENABLED` | false，tenant + exact capability（O2） |
| `RECOVERY_OPERATIONS_ENABLED` | false；off 不影響 automatic recovery（O4） |
| `AGENT_TRIGGERS_ENABLED` | false，tenant/source allowlist，disabled 404 fail-closed（O5） |

---

## 4. Flag 啟用序

安全原則不變：**fail-closed 是刻意設計**。本節排的是「證據齊備後何時有資格打開」，不是「現在就把預設改開」——任何一列的啟用都要在對應計畫的驗收/rollout gate 通過後才執行，本表不取代那些 gate。

10 個目前預設關閉、與 C3 發現（D1–D7、E1/E3、Eval、O2/O5）直接相關的 flag，建議啟用順序如下：

| 順序 | Flag | 對應能力 | 前置證據 / 依賴 |
| --- | --- | --- | --- |
| 1 | `AGENT_BUILDER_ENABLED` | D1 Agent Registry CRUD | 無額外依賴；R0 shadow validation（production-like fixtures 驗證 tenant/revision/Graph/binding/policy） |
| 2 | `AGENT_TEST_RUN_ENABLED` | D3 Direct Agent test-run | 依賴 D1 已有可發布的 Agent snapshot；R1 test console 指標（node validation、token、latency、clarification） |
| 3 | `WORKFLOW_DESIGNER_ENABLED` | D4 Workflow Designer + `workflow.manage` | 依賴 D1；exact `workflow.manage` capability（非 ADMIN 泛權限）；R1 唯讀 Designer 驗證通過 |
| 4 | `MULTI_AGENT_DISPATCH_ENABLED` | D5 Root Orchestrator 多 Agent dispatch | 依賴 D3（已驗證 Worker/Verifier Agent）+ D4（已發布 Workflow）；R2 shadow/canary run 的 task decomposition/verifier/aggregation 對照通過 |
| 5 | `AGENT_CHAT_ENABLED` | D6 Tenant canary chat | **需要 `MULTI_AGENT_DISPATCH_ENABLED`（D5）已開**；且需第 2 節 D6 證據 checklist 四項全綠（R3 canary 通過）——這是本表唯一同時卡「flag 依賴」與「證據依賴」兩層的項目 |
| 6 | `CONTEXT_ENRICHMENT_ENABLED` | E1/E3 Context Enrichment | **需要 `MULTI_AGENT_DISPATCH_ENABLED`（D5）已開**（Backend/Workflow/Platform 三端各自 fail-closed，但都以 D5 為前提）；不依賴 `AGENT_CHAT_ENABLED`，可與第 5 項並行推進。若選第 1 節故事 B，此項之後的深化（E2）另需 §11 Q1–Q3 產品答案，本 flag 本身不受阻 |
| 7 | `AGENT_WRITE_TOOLS_ENABLED` | D7 Write tools + approvals | **雙白名單非空**：tool capability allowlist（唯一 write tool `runtime.write_evidence`）與 tenant allowlist 都必須先有內容，不可用空白名單「技術性開啟」；另需人工確認、idempotency、resume、取消、稽核鏈路驗證完成（R5） |
| 8 | `RUN_EVAL_ENABLED` | Eval 框架（Backend/Workflow 端） | Backend 已具備 suite/revision/run/result 權威與 regression 重算能力；platform 端 eval 代理路由與 `AGENT_WRITE_TOOLS_ENABLED` 共用同一 pre-auth 404 gate，故建議晚於第 7 項開啟以避免路由層 gate 狀態不一致 |
| 9 | `RUN_DISCOVERY_ENABLED` | O2 Runs 中心 | 依賴已有可觀察的 run 母體（第 4 項 D5 開啟後才有多 Agent run 可被發現）；另需釐清與 admin-experience-downshift O3 workstream 的範圍重疊（見 3.3 節） |
| 10 | `AGENT_TRIGGERS_ENABLED` | O5 觸發器（實作已於 2026-08-09 交付：one-shot schedule；recurring/webhook 未做） | 依賴 `RUN_DISCOVERY_ENABLED`（O2，第 9 項，run 必須可被發現）+ O3 審批佇列語意到位（觸發建立的 `waiting_approval` run 必須可見可操作）；先選 one-shot schedule 或 signed webhook 其中一種，不同時做滿（§6.2） |

---

## 5. 本文件不做的事

- 不修改 `plans/copilot-shared-core/05-release-evidence-plan.md`、`plans/agent-platform-redesign/05-migration-and-rollout.md`、`plans/agent-architecture-improvements/04-operations-trigger-plan.md`、`plans/context-enrichment/01-plan.md` 的任何內容或 authority。
- 不新增任何架構、API、schema 或 flag——第 4 節的 10 個 flag 全部是既有計畫已定義的 flag。
- 不預先假設第 1 節的決策結果；第 2–4 節對兩條故事都適用（D6 證據與 flag 啟用序是共用地基，不因選 A 或 B 而改變內容，只改變優先序）。
