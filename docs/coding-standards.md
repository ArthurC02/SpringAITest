# Coding Standards(開發代理憲法)

適用對象:`dotnet-implementer`、`frontend-implementer`、`python-implementer`、`code-reviewer`、`code-simplifier`。開工先讀本檔;實作代理已透過 agent 定義的 `skills:` frontmatter 預載 Skill `ponytail:ponytail`,無需自行載入。

## Karpathy 四原則(嚴格遵守)

來源:[multica-ai/andrej-karpathy-skills](https://github.com/multica-ai/andrej-karpathy-skills)。

1. **Think Before Coding** — 不確定就明說假設,不悶頭猜;有歧義時列出多種解讀;有疑慮主動提出;真的困惑就要求澄清。
2. **Simplicity First** — 寫解決當前問題的最小程式碼;不寫投機功能、多餘可配置性、不可能場景的錯誤處理;發現更簡單的寫法(50 行能做掉 200 行)就重寫。
3. **Surgical Changes** — 只動需求要求的範圍;不順手重構無關程式碼;跟隨既有風格;只清理「因本次變更而變無用」的程式碼,不動先前就存在的死碼(那是清理盤點的事,見下)。
4. **Goal-Driven Execution** — 先定義可驗證的成功標準;用測試迴圈驗證完成;多步計畫要有查核點。

## Ponytail 階梯(解法選擇順序)

停在第一個站得住的階:**不需要存在?→ codebase 已有?→ stdlib?→ 平台原生?→ 既有依賴?→ 一行?→ 最小實作。** 刻意留下的天花板(全域鎖、O(n²)、naive heuristic)用 `ponytail:` 註解標記天花板與升級路徑。

## 本方案原則

- **Re-Use 是每次開發的大前提**:動手寫之前先用 codebase-memory MCP 搜既有 helper/型別/模式;適當抽象 — 為已出現的重複抽,不為想像中的未來抽(dead flexibility 是要被砍的對象)。
- **Node-First 是 workflow/ 的設計憲法**:一切能力先是帶明確 `reads`/`writes` 契約的 `@node`;**Business Workflow** 永遠是宣告式 YAML 組合，不寫繞過 node registry 的旁路；**Agent Skill** 是 `SKILL.md` 公規包，不是 YAML 流程。Harness 專指 `runtime/graph.py` 的固定執行骨架，Node Shell 才是包住每個節點的治理殼。
- **Harness / 商業邏輯分層鐵律**:Workflow 只保留 **Harness 邏輯** — 圖編譯、節點契約與 reads enforcement、LLM 編排(prompt 組裝、ReAct、LiteLLM 呼叫)、checkpoint/interrupt、沙箱,以及規則求值/白名單計算這類**內容來自資料的通用解譯器**。**領域邏輯**(業務概念、領域計算公式、業務流程)、**系統整合**(呼叫外部系統的 adapter — 含 kbquery 未來接線的 searcher/reranker/audit port)、**資料存取**(資料庫與本地檔案)一律在 Backend;Workflow 節點透過 Backend HTTP 取用,永不從 Python 直連資料庫、檔案或外部系統。唯一例外:LangGraph checkpoint 的 PostgreSQL 持久化是執行狀態,屬 Harness(D3 契約明定),不受此限。
- **Business Rules 只住在 Backend**:規則的持久化與權威唯一在 Backend;Platform/Frontend 永不承載規則邏輯(只代理/渲染 catalog);Workflow 依 D2 契約僅提供無副作用的驗證/求值引擎,不落地任何規則。
- **測試只留有價值的案例,不著墨數量**:一等價類一代表值、規格數字配 on/off-point 邊界、安全語義必須有測試背書;不為覆蓋率測 getter/DTO/框架行為。完工回報附當輪測試結果(通過/失敗)作為驗證證據;但測試總數不寫進任何持久化文件(AGENTS.md、README、docs/)。
- **每次計畫必含清理盤點**:本次新需求會讓哪些舊程式碼/設計成為 **dead code** 或 **duplicated code**?計畫階段就列出,並在同一輪移除或合併。判斷死碼不可只看圖譜 fan-in=0 — CALLS 邊對介面 DI、方法群組、`?.`、裝飾器、`Depends()`、前端 ESM import 有系統性盲點,結論要用 `trace_path` + 實讀程式碼複核。
- **重構/清理輪必須同時稽核正確性,不能只找可刪除的東西**(教訓來自一輪掃描規格誤把型別安全、非同步正確性、鎖語意都歸進「風格偏好」而全數排除,整輪只問「能刪什麼」,沒有一條問「這裡寫得對不對」):「風格偏好」僅指命名、排版、註解密度 —— **不包含**型別安全、非同步正確性、併發/鎖語意、錯誤處理、API 誤用,這些是品質問題,一律在稽核範圍內。**「match 既有 idiom」不適用於既有 idiom 本身是錯的時候,一致性永遠不高於正確性**——發現既有程式碼用了壞模式,結論是「把它們一起修」,不是「照著寫」。**行數不是品質指標**:正確的併發與錯誤處理通常比錯誤的寫法更長(實測:同一輪重構讓程式碼淨減約 240 行,隨後的品質修正淨增約 600 行,方向仍然是對的)。

## .NET 併發規約(適用 `backend/` 與 `platform/`,兩者皆 net10.0)

每一條都是實際踩到或修掉的教訓,不是理論:

1. **生產碼禁止 `GetAwaiter().GetResult()` / `.Result` / `Task.Wait()`。** 若臨界區內需要 `await`(C# 不允許在 `lock` 內 `await`),改用 `SemaphoreSlim(1,1)` + `await WaitAsync(ct)` / `try` / `finally { Release(); }`。例外:`SemaphoreSlim` 自己的同步 `Wait()`/`Release()` 用在無法改 async 的 `void` 介面方法或屬性 getter 上是合法的——它阻塞的是號誌而非 `Task`,與 `Monitor.Enter` 同類,沒有 thread-pool 飢餓或 `AggregateException` 包裝的風險。
2. **鎖物件一律宣告為 `System.Threading.Lock`(.NET 9+),不用 `object`。** `lock (x)` 的行為取決於 `x` 的**靜態型別**——欄位與任何對外曝露的存取子必須同時宣告為 `Lock`,只改一邊比不改更危險:宣告成 `object` 會讓 `lock` 退回舊的 Monitor 路徑,與 `EnterScope()` 的持有者靜默失去互斥。
3. **`Monitor.Enter/Exit/TryEnter` 作用在 `Lock` 實例上可編譯、不拋例外、但完全不提供互斥**(已實測驗證)。一律用 `Lock.Enter()`/`Exit()`/`TryEnter()`——這個陷阱會讓併發測試靜默變成永遠通過的假綠燈。
4. **不要 `lock` 在集合物件、`this` 或 `typeof(X)` 上**,一律用專用的 `private readonly Lock` 欄位。鎖在公開屬性回傳的集合上,等於把互斥邊界洩漏到類別外。
5. **`SemaphoreSlim` 不可重入,`lock` 可重入。** 從 `lock` 換成 `SemaphoreSlim` 前,必須先建出「誰在持鎖時呼叫了誰」的關係圖(含 private helper、顯式介面實作、回呼)——在自己的臨界區內呼叫同一個 class 中也會取鎖的公開方法會永久死鎖,沒有錯誤訊息,測試不一定走得到。標準修法:私有 `XxxLocked()` 核心 + 公開包裝。真實案例:`InMemoryOrchestratorRunRepository.ClaimRecoveryAsync` 持鎖時呼叫了公開的 `ClaimCommandAsync`,靠 `lock` 的可重入性長期沒被發現。
6. **跨物件鎖序**:確立單一方向並寫進註解,新增巢狀鎖前必須 grep 反向路徑。真實案例:`InMemoryAgentRunApprovalRepository.ConsumeAsync` 原本是 Approval gate → AgentRun gate 單一方向,新增了反向的 AgentRun gate → Approval gate 造成 ABBA;兩把都是 repository 全域鎖,不同 run 的併發請求也會互卡,且 `lock` 不吃 `CancellationToken`,永久掛住並耗盡 thread pool。

## Codebase-Memory MCP 取代盲 grep

語意層問題(誰呼叫誰、跨檔引用、架構關係、找可重用的既有實作)一律先用 `mcp__codebase-memory__search_code` / `search_graph` / `trace_path` / `get_architecture` / `query_graph` / `get_code_snippet`,不做大範圍盲 Grep。Grep 只留給字面字串:env 變數名、訊息文案、確切 identifier 的全倉掃描。
