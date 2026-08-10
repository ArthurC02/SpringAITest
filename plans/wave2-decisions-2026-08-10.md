# Wave 2 決策記錄（2026-08-10）

- **來源**：[系統重估補強計畫](system-reinforcement-2026-08-10.md) Wave 2 的八道待拍板決策
- **決策者**：架構決策，經使用者授權由 Claude 代為判斷後實作（2026-08-10）
- **性質**：決策記錄 + 實作範圍。每題記錄**選了什麼、為什麼、放棄了什麼、什麼條件下要重新檢視**
- **原則**：能用最小爆炸半徑解決「實際發生的傷害」的選項優先；需要合規或產品輸入才能定的參數不假裝有答案，改為修掉可量測的症狀並記下待確認的政策意圖

---

## W2-01 重量級跨服務驗證進 CI

**決定：選項 (d) + (c) 混合。**

- frontend 三支真瀏覽器 evidence spec（auth / session-tool / streaming）併入既有 `full-chain-smoke` job——該 job 已啟動四個容器，基礎設施已具備，`playwright.config.ts` 也已有 `evidenceSkipGuardReporter` 防假 PASS。
- 四支 D-phase 腳本（D3 / D5 / D6 / D7）進新的 nightly 排程 workflow，同時支援 `workflow_dispatch` 手動觸發。

**為什麼**：D3/D5/D7 是並發與持久層風險最高的區塊，現況「只靠人記得跑」等於沒有防線；但把它們塞進每次 PR 會讓回饋時間顯著變長，而它們偵測的是回歸而非新錯——晚一天發現遠優於不發現。frontend evidence 則因為容器已經在跑，邊際成本近乎零，沒有理由不進 PR 路徑。

**docker layer cache：不引入。** 拒絕 `type=gha` buildx cache 與新的 GitHub Action 依賴。理由不是成本，是正確性：本 repo 剛因為容器跑到舊 image 而在 e2e 程序裡加了「build 陷阱檢查」；在這個歷史下引入 layer 快取，是拿一個真實的 stale-artifact 風險去換推測性的 CI 分鐘節省。兩個 job 各自 build 四個 image 的浪費予以接受並記錄。

**重新檢視條件**：CI 排隊時間成為實際阻礙，或 nightly 連續兩週零發現（代表可以降頻）。

---

## W2-02 操作性資料生命週期

**決定：儀表板選 (e)；保留策略選 (d)。**

### 儀表板時間窗（實作）

`GetMetricsAsync` / `GetVersionComparisonAsync` 加**預設 90 天**時間窗，呼叫端可經查詢參數覆寫（有界 1–365），回應形狀擴充為帶出實際統計區間，**畫面上明示「統計區間：最近 N 天」**。

**為什麼**：全量 COUNT/AVG/SUM/GROUP BY 隨資料量單調變慢，這是可量測、可修的工程問題。選項 (g)（加窗但不顯示）被否決——數字含義改變卻不告訴使用者，是把效能問題轉嫁成正確性問題。

### 保留策略（本輪不實作，記錄政策意圖）

**建議政策（待合規確認後才實作）**：

| 表 | 建議保留 | 理由 |
|---|---|---|
| `agent_run_event`、`orchestrator_run_event`、`operations_execution_metric` | 90 天 | 逐 event / 逐 call 寫入的可觀測性資料，無稽核義務 |
| `operations_run_evidence`、`eval_case_result` | 365 天 | release evidence，被 sign-off 紀錄引用（見 [copilot-shared-core/05-release-evidence-plan.md](copilot-shared-core/05-release-evidence-plan.md)） |
| `context_revision`、`context_evidence`、`context_view` | **不刪** | 被 run 的 `context_ref` 釘住，刪除會使既有 run 的 context 無法解析 |

**為什麼不現在做**：保留期限是合規決策，不是工程決策；而 evidence 類表被具名 sign-off 引用，DELETE 不可逆。先修掉真正在痛的查詢效能，把不可逆的動作留給有權決定的人。

**重新檢視觸發條件**：任一表超過 1000 萬列，或收到明確的合規保留要求。

---

## W2-03 觸發器 `fire_at` 上限

**決定：選項 (a)，上限 90 天**，於 `TriggerDtos.FireAt()` 輸入驗證拒絕（400），Dapper 與 InMemory 兩側同步。

**為什麼 90**：one-shot 觸發器的改期路徑本來就是「取消後重建」（O5 刻意的 YAGNI 決策），因此 90 天對真實排程需求不構成限制；但它把「權限提升定時裝置」的引信長度從無限砍到有界。

**為什麼不選 (b)**：fire 時比對 principal 快照與現況，會直接推翻 O5「不可變快照」的既定不變式——那個設計是為了讓 fire 行為可預測、不隨外部狀態漂移。用 (a) 限制時間窗可以在**不動不變式**的前提下把風險收斂到可接受，是更小的爆炸半徑。

**附帶**：`backend/AGENTS.md` 記錄前置條件——上線任何 runtime 的 role/capability/群組授予或撤銷 API 之前，必須重新評估本項（屆時 90 天仍可能太長）。

---

## W2-04 JWT 保管位置

**決定：選項 (d)——維持 localStorage，以 W1-08 的 CSP 為主要防線，明文記錄為經評估的接受，並訂重新檢視觸發條件。**

**為什麼**：遷移到 httpOnly cookie 是跨 platform + frontend + e2e 的完整工程（簽發、CSRF、CORS、SSE/AG-UI 兩條傳輸的認證、全域 401 路徑），半套遷移比不遷移更糟。現況第一方程式碼無 XSS 破口（`react-markdown` 預設不渲染 raw HTML；唯一的 `dangerouslySetInnerHTML` 經 hljs 逸出），加上 W1-08 已上 CSP，殘餘風險可接受且有防線。

**重新檢視觸發條件（任一成立即應啟動 W3-04）**：
1. 對外開放自助註冊給非受信任使用者；
2. 引入任何第三方前端腳本或需要放寬 CSP 的整合；
3. 進行正式安全稽核；
4. 出現任何一次實際的 XSS 發現。

---

## W2-05 D5 root run deadline 逾時

**決定：選項 (c)——讀取路徑收斂。** `GetAsync` 在回傳前，對已過 deadline 且仍非終態的 run 呼叫既有的 `ExpireDeadlineAsync`。

**為什麼**：回報的實際傷害是「使用者看到永遠執行中而無錯誤提示」——那正是讀取路徑上發生的事。(c) 用最小的改動修掉那個傷害，且**不引入第二個會改變 run 終態的行為者**：誤判把活著的 run 標成 `timed_out` 比現況這個 bug 更糟，而 (c) 在結構上不可能誤判（有人查詢才收斂，且沿用既有的、已被四條路徑呼叫的同一個冪等函式）。

**已知殘留缺口（有意識接受）**：沒有人查詢的 run 仍會一直掛著非終態。若日後彙總報表或 O4 的 recovery 可觀測性顯示這些殭屍 run 造成實際影響，再評估選項 (a)（獨立 BackgroundService + 寬限期）。

---

## W2-06 舊版 `GET /api/chat/history`

**決定：選項 (a) 加上限 500，並在文件標記 deprecated、指向 keyset 分頁版。**

**為什麼 500**：足夠寬鬆到現實中的呼叫方不會撞到，同時把單次成本封頂。

**為什麼不發 `Deprecation`/`Sunset` header**：`Sunset` 要帶一個具體的移除日期，那是產品承諾，不該由工程單方面寫進 wire 契約。文件層標記 deprecated 已足以引導新呼叫方走分頁版。

**為什麼不直接移除**：無法確認 repo 之外的呼叫方，移除是不可逆的破壞性變更。

---

## W2-07 文件處理消費者並行度

**決定：選項 (a)——維持 `prefetch=1`，加 `ponytail:` 註解寫明這是有意識的天花板與升級路徑。**

**為什麼**：目前沒有任何實際的批次上傳延遲回饋，調參等於投機優化；而提高並行度要付出 embedding provider 速率限制、DB 寫入競爭與更難除錯的代價。註解的價值在於讓後人知道這是刻意的全域序列化，不是疏漏——真正的成本是誤判為 bug 而去「修」它。

**升級路徑（寫在註解裡）**：先量測 202→ready 的等待時間（bounded telemetry，不得帶 tenant/document id），確認實際延遲後再決定提高 prefetch 或開多 channel。

---

## W2-08 文件失敗後的重新處理

**決定：選項 (a)——不做重新處理入口。** W1-18 已補上失敗原因，使用者的下一步是修正檔案後重新上傳。

**為什麼**：W1-18 落地的失敗分類實際只有三類（處理逾時、產生向量問題、未預期問題），三者的使用者手上都還留有原始檔案，重新上傳就是一個動作；而提供重試入口需要同時處理冪等（DELETE-then-INSERT）、重試次數上限、防連點、權限層級、以及 terminal 分類要不要隱藏按鈕——工程量與收益明顯不成比例。

**重新檢視條件**：若日後支援大型檔案上傳（重新上傳成本高）或出現高頻的 transient 失敗，再評估選項 (b)（僅 transient 分類顯示重試）。
