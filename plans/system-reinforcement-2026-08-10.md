# 系統重估補強計畫（九維度合併）

- **日期**：2026-08-10
- **來源**：九個維度的獨立評估發現（使用者旅程／管理面表達／安全邊界／測試與 CI 覆蓋／可靠性與可觀測性／效能與規模／資料層與遷移／前端結構／建置與文件）
- **性質**：補強計畫，非重寫。每一項都經過本輪實讀程式碼複核，附 file:line 證據
- **與既有計畫的邊界**：本計畫**不取代**任何既有計畫。`architecture-hard-reset`、`agent-architecture-improvements`（E0/E2/E4/O4/S2–S3/P2–P4/C2/C8）、`agent-platform-redesign`（D1–D7）、`context-enrichment`、`admin-experience-downshift`、`ux-core-journey`、`north-star-and-activation` 仍是各自範圍的 authority。凡與既有計畫重疊者，本計畫只引用其路徑並標記，**不重寫規格**
- **續跑方式**：本檔為 `remediate` workflow 的輸入。未打勾的 checkbox 就是剩下的工作；被中斷後以同一個檔案路徑重新叫用即可續跑

---

## 方法與立場

**做了什麼**：先讀根 `AGENTS.md`、各區域 `AGENTS.md`、`plans/README.md`（交付狀態索引）、`docs/coding-standards.md`，再逐項回到程式碼求證。九維度共提出 47 條發現，本輪逐條核對後：合併同根因、剔除證據薄弱或投機性的項目，剩下 34 條分三波。

**合併掉的根因叢集**（不同維度撞同一根）：

| 叢集 | 原始發現 | 合併結果 |
|---|---|---|
| 管理面中文化覆蓋有洞 | 側欄兩個英文入口、OrchestratorsView 全英文、編輯器裸英文標籤、系統設定術語外洩 | 併為 **W1-03** 一項（同一 stack、同一 PR、同一詞彙表） |
| 啟動遷移不收斂 | Skill 遷移全表重寫、`ALTER SET NOT NULL` 每次重下 | 併為 **W1-09**（同檔、同修法「先查再動」） |
| CI 從未跑過正式 checkpointer | 維度 4「E0 item 3 未落地」＋維度 9「workflow CI 綠燈假象」 | 併為 **W1-16** |
| 追蹤編號鏈路斷頭 | 前端從不顯示 correlationId ＋ RabbitMQ 路徑不傳 correlationId | 拆為 **W1-02**（前端）與 **W1-13**（後端／平台）：同一承諾的兩端，但不同 stack、不同 implementer |
| 操作性資料無界成長 | append-only 表無保留策略 ＋ Operations 儀表板無時間窗 | 併為 **W2-02** 一道決策（保留期限是合規題，儀表板換窗是語意變更題，一起拍板才一致） |
| 重量級驗證不在 CI | D3–D7 腳本、frontend evidence spec、CI 重複 build | 併為 **W2-01** 一道決策（同一個問題：CI 要投多少時間與錢） |

**分波規則**：

- **Wave 1** — 本輪可執行：不需使用者裁決、不引新依賴、S/M effort、爆炸半徑小。依「最小爆炸半徑優先」排序：P0 → 使用者可見缺陷 → 資料層／併發 → CI／文件／建置
- **Wave 2** — 需要使用者拍板：產品方向、旗標啟用序、合規期限、新依賴授權。每項附具體問題與選項，工程不代決
- **Wave 3** — 大型工程：引用既有計畫，或明確標記「需新計畫」。不在此重寫規格

**刻意決策紅線逐項檢查**（本計畫無任何一項違反）：

| 刻意決策 | 檢查結果 |
|---|---|
| hand-written fakes（不用 mocking 框架） | W1-10／W1-13 的測試規格皆明寫「手寫 fake」 |
| 前端無 router、無 UI library | W1-03／W1-04／W1-07 全部只改既有元件與既有 CSS class，零新依賴 |
| 404 偽裝 feature gate | W1-02 明列紅線：4xx 不附加追蹤編號，不得在 404 路徑引入額外資訊 |
| fail-closed 預設 | W1-07 的目錄反查 fail-open 只影響**裝飾性名稱**，不涉任何授權判定；W1-11 是把 fail-closed 補齊，不是放寬 |
| 雙 SSE 格式 | W1-08 明列紅線：不得動 `location /api/` 的 `proxy_buffering off` |
| backend／workflow 綁 127.0.0.1 | 無任何一項改動繫結位址 |
| LLM 一律走 LiteLLM | 無任何一項改動 LLM 呼叫路徑 |
| 回應欄位命名刻意混用 | W1-18 明寫「documents 域是 snake_case，跟隨該 DTO 既有慣例」；W1-02 明寫 correlationId 是 camelCase |
| 已否決：共用 csproj 抽象 | W1-21 明列紅線：純各自 Dockerfile 內的 layer 順序調整 |
| 已否決：Dapper 樣板抽象化 | W1-09／W1-12 只改條件與時鐘來源，不抽任何樣板 |
| 已否決：全 repo 測試框架化 | W1-17 明列紅線：兩側快照測試維持刻意的近似複本，不抽共用 |

---

## Wave 1 — 本輪可執行（21 項）

### P0

- [x] **W1-01** Run 核准/駁回加二次確認（`frontend/src/components/ApprovalInbox.tsx:222-225`）：`decide()` 內用既有 `useConfirm()`，文案帶入動作摘要與到期時間，`keyFor` 移到確認之後以免取消也消耗 logical attempt。D7 核准是 once-only、不可重放的最終寫入授權，卻是站內唯一沒有二次確認的高風險操作，而這個畫面設計上正是給非技術的業務核准者用的。

### P1 — 使用者可見缺陷

- [x] **W1-02** 5xx 錯誤訊息接上追蹤編號（`frontend/src/api/http.ts:62-65`）：`parseErrorMessage` 在 `status >= 500` 時把 `data.correlationId` 或 `X-Correlation-Id` header 附成「（追蹤編號：…）」。單改一個函式即同時覆蓋 `apiFetch` 與 SSE 兩條路徑。workflow 的固定訊息字面上就是「請提供追蹤編號給管理員」，但前端從未讀取它。
- [x] **W1-18** 文件失敗原因全鏈路（`backend/src/Backend.Api/Files/RagRepository.cs:162-171` → DTO → `frontend/src/components/DocumentsView.tsx`）：`MarkFailedAsync` 加 nullable `failureReason`，`rag_documents` 加欄位，DocumentProcessor 從**封閉集合**取已消毒分類文字，前端在失敗 chip 旁顯示。**先跑 `contract-change` skill**；不含重試（見 W2-08）。
- [x] **W1-03** 管理面中文化補完（`AppShell.tsx:55-56`、`OrchestratorsView.tsx:95-148,274-294,314,328,348,358,773,780,836,856-863,882,898`、`ConfigView.tsx:20,89-90`）：詞彙一律沿用 `AgentTestConsole.tsx` 既有對照，不新建。同輪 grep `frontend/tests` + `frontend/e2e` 更新受影響的文字選擇器。
- [x] **W1-07** 執行總覽解析 Agent/Orchestrator 名稱（`frontend/src/components/RunsView.tsx`、`runDiscoveryDisplay.ts:47-53`）：比照 `TriggersView.tsx:230-234` 的 client-side 反查。兩支目錄 API 各有獨立 flag/權限，必須**完全 fail-open**（404/403 靜默，退回短 GUID）。
- [x] **W1-04** 文件旅程兩處誠實回饋（`DocumentsView.tsx:253-263`、`AppShell.tsx:239-246,275-282`）：(a) 載入失敗時顯示「重新載入」而非「尚無文件」邀請文案（比照 `AnalysisView.tsx:27-35`）；(b) 副駕加「目前聚焦：《…》×」指示與清除鈕。
- [x] **W1-06** TriggerDetails 補世代守衛（`TriggersView.tsx:169-182`）：照抄 `ApprovalInbox.tsx:92-111` / `RunsView.tsx:95-117` 的 `generationRef`，並補 `:209` 重新載入鈕的 `disabled`。**不抽共用 hook**（只有 3 個呼叫點）。
- [x] **W1-05** 聊天來源徽章不外洩 slug（`frontend/src/skillSentinel.ts:24-26`）：白名單外一律回「來源：自訂技能」，不印 kebab-case 技術識別碼。
- [x] **W1-08** SPA 容器基線安全標頭（`frontend/nginx.conf`）：必做三個零風險標頭（nosniff / Referrer-Policy / X-Frame-Options），盡力加 CSP。**若 CSP 擋住 pdf.js worker 或 CopilotKit，拿掉 CSP 並回報，絕不得加 `unsafe-eval`。** 不得動 `location /api/` 的 `proxy_buffering off` 與 `.mjs` MIME 映射。

### P1 — 資料層／併發／可靠性

- [x] **W1-09** DbBootstrap 兩個遷移收斂（`DbBootstrap.cs:1093-1097,1247-1261,1264-1269`）：(a) 用已算好的 `nameChanged/definitionNeedsRewrite/packageNeedsRewrite` 包住兩個 UPDATE；(b) 用 `information_schema.columns` guard 包住 `ALTER … SET NOT NULL`（比照同檔 `:798-819` 的 DO 區塊）。外層 `SELECT … FOR UPDATE` 維持不變，加 `ponytail:` 註解記錄剩餘天花板。
- [x] **W1-12** O5 due/misfire 改用 DB 時鐘（`TriggerDispatcher.cs:31-33,63,145`、`TriggerRepository.cs:174-213`、`Data/InMemory/InMemoryTriggerRepository.cs`）：`ClaimDueAsync` 去掉傳入的 `now`，SQL 改用 `now()`（同述句內穩定，且與既有 `updated_at=now()` 同值）並回傳權威時間；misfire 用回傳值判斷。**先跑 `parity-check` skill。** 一次性觸發永不重試，時鐘飄移＝使用者排定的 run 靜默不發生。
- [x] **W1-10** InMemoryCheckpointRetentionRepository 實作 + 測試（`CheckpointRetentionRepository.cs:115-142`）：目前 `ListAsync` 永遠回空陣列，且該類別在全 repo 唯一出現處是 DI 註冊本身。改由兩個 InMemory run repository 各自在**自己的鎖內**回傳候選快照，retention repo 在鎖外合併。**紅線：絕不得持有自己的 `_gate` 時呼叫另外兩個 repository**（CLAUDE.md 記載的 ABBA 實例）。
- [x] **W1-11** `X-User-Capabilities` 有界驗證（`Common/IdentityHeaders.cs:29-35`）：比照同檔 `UserGroups()`（:56-83）做值數量／wire byte／entry 長度／控制字元檢查，單一壞值拒絕整個 header。**紅線：不得加名稱文法正則**（`RequireCapability` 已是 Ordinal 精確比對，未知字串本來就無法命中；加文法只會製造未來破壞面）。
- [x] **W1-13** 文件處理鏈路傳遞 correlationId（`Files/DocumentDtos.cs:20-21`、`platform/src/Platform.Service/RabbitDocumentQueue.cs:38`、`Files/DocumentConsumerService.cs`）：body 欄位 + AMQP `BasicProperties.CorrelationId` 雙寫，consumer 驗證後開 logging scope。**紅線：缺欄位的舊訊息必須照常處理完成**（滾動部署相容）。
- [x] **W1-14** Backend readiness 揭露 consumer 連線狀態（`Common/BackendHealth.cs:101-107`、`Files/DocumentConsumerService.cs`）：加 `document_consumer` 元件，**`Required: false`**（看得見但不翻轉 200/503，避免 broker 抖動造成重啟迴圈）；consumer 未啟動的部署不得出現該元件。
- [x] **W1-15** AgentChatRuntime 輪詢 100ms → 500ms（`platform/src/Platform.Service/AgentChatRuntime.cs:32`）：一個常數，backend 請求量降一個數量級，使用者無感（LLM 本身是秒級）。**不加退避狀態機**（YAGNI），加 `ponytail:` 註解記錄升級路徑。

### P1 — CI 覆蓋

- [x] **W1-16** workflow CI job 加 postgres service container（`.github/workflows/ci.yml:75-88`）：鏡射 backend job（:29-41）的 pgvector service，設 `CHECKPOINT_DATABASE_URL` + HMAC key（**實作前先讀 `workflow/app/settings.py` 確認變數名**），加「must be reachable」防呆步驟，改寫 :76-77 的過期註解。對應 `plans/agent-architecture-improvements/02-evaluation-observability-plan.md` §3（E0 item 3）。
- [x] **W1-17** Route snapshot 納入授權 metadata（`backend/tests/…/ApiRouteSnapshotTests.cs:17-28`、`platform/tests/…/ApiRouteSnapshotTests.cs:17-28` + 兩份 `RouteSnapshots/*.txt`）：每行附 `[authz:policy=…]` / `[authz:anonymous]` / `[authz:authenticated]` / `[authz:none]`。**紅線：不得改任何路由或授權屬性**；重新產生的快照 diff 必須完整呈現在 PR 中，讓 reviewer 一次性簽核所有路由的授權狀態。

### P2 — 文件與建置

- [ ] **W1-19** 三份旗標清單／健康檢查／腳本 OS 慣例同步（`README.md:36,75-83`、`infra/.env.example`、`platform/AGENTS.md`）：README 補 6 個缺漏旗標、.env.example 補 `RUN_DISCOVERY_ENABLED`/`AGENT_TRIGGERS_ENABLED`、platform/AGENTS.md 補 `/actuator/health/ready` 及其六項依賴檢查、README 補一句 verify-* 腳本僅 pwsh 版。**以程式碼與 compose 檔為準逐一核對，不照抄本計畫的清單。**
- [x] **W1-21** backend/platform Dockerfile restore 層分離（`backend/Dockerfile:3-7`、`platform/Dockerfile:3-7`）：`COPY *.csproj` + `dotnet restore` 獨立成層，再 `COPY . .` + `publish --no-restore`。與 `workflow/Dockerfile:9-14` 已有的做法一致。**紅線：不得抽共用 csproj**（已否決提案）。
- [x] **W1-20** start-infra 提示需手動 build（`scripts/start-infra.ps1:9`、`scripts/start-infra.sh:11`）：加 `-Build`/`--build` opt-in 開關；未帶時印提示。不預設 build（多數模式 A 使用者只改主機上的 platform/frontend）。`*.sh` 維持 LF。

---

## Wave 2 — 需使用者拍板（8 項）

每項都附具體問題與選項，工程不代決。

- [ ] **W2-01** 重量級跨服務驗證要不要進 CI、什麼頻率、要不要付 docker layer cache 的代價（D3/D5/D6/D7 四支 verify 腳本 + frontend 三支 evidence spec + 兩個 job 重複 build 四個 image）
- [ ] **W2-02** 操作性資料生命週期：保留期限、封存去處，以及 Operations 儀表板要不要加時間窗（8 張 append-only 表零 DELETE；`OperationsGovernanceRepository.cs:165-191` 全量聚合無時間窗）
- [ ] **W2-03** 一次性觸發器 `fire_at` 要不要設最大排程期限、N 是多少（`TriggerDtos.cs:185-188` 無上限；fire 時只驗帳號存在，不驗權限時效性）
- [ ] **W2-04** JWT 保管位置：維持 localStorage 還是遷移 httpOnly cookie（`frontend/src/api/auth.ts:18-20`；W1-08 的 CSP 是前置縱深防禦，不是替代品）
- [ ] **W2-05** Backend 要不要自主 sweep 過期的 D5 root run deadline（`OrchestratorRunRepository.cs:45,435-449` 完全依賴 Workflow 存活；誤判比現況更糟，故不由工程單方面決定）
- [ ] **W2-06** 舊版 `GET /api/chat/history` 無 LIMIT 端點的處置（`ConversationRepository.cs:23-33`；加上限是既有契約的語意變更）
- [ ] **W2-07** 文件處理消費者並行度：`prefetch=1` 全域序列化要不要提高（`DocumentConsumerService.cs:76`；noisy-neighbor 的定價只有你能做）
- [ ] **W2-08** 文件失敗後的「重新處理」動作：要不要做、語意為何、誰可以按（W1-18 只補原因，不含重試）

---

## Wave 3 — 大型工程 / 已有計畫（5 項）

不重寫規格；引用既有計畫或標記需新計畫。

- [ ] **W3-01** 移除 regressions 端點的 caller-supplied `passed` 自我背書路徑 → `plans/agent-architecture-improvements/06-cleanup-and-consolidation.md`（C2）。本輪以新鮮證據確認 `OperationsGovernanceController.cs:28` 該路徑**仍然可達**：任何 workflow.manage 持有者今天都能自我背書一個通過的品質關卡並解鎖 rollout。需確認 C2 的前置條件（E2 migration 完成）是否被獨立追蹤。
- [ ] **W3-02** ChatAssistant session store 逐出機制 → `docs/cross-service-contracts.md` § Two memory layers（P4-3 範圍）。P4-3 若採「每使用者一把 key」，本風險同時解除；若保留每對話一把 key，必須另包 TTL/LRU。只需把這句補進 P4-3 驗收條件 + platform/AGENTS.md 補上「無逐出」的現況事實。
- [ ] **W3-03** D5 root run deadline 自動逾時機制（取決於 W2-05）→ 需在 `plans/agent-architecture-improvements/04-operations-trigger-plan.md` §5（O4）新增設計章節。O4 目前只規劃 recovery 可觀測性與人工 retry/abandon，**不含**自動逾時。
- [ ] **W3-04** JWT 遷移 httpOnly cookie（取決於 W2-04）→ 需新計畫。形狀完全取決於 W2-04 選完整 httpOnly 還是記憶體 token + refresh cookie，兩者的 CSRF 面與 SSE 認證做法差很多。
- [ ] **W3-05** D3/D5/D7 與 frontend evidence 的 CI job 建置（取決於 W2-01）→ 需新計畫（小型）。四支 verify 腳本已存在且可執行，要新增的只是 CI 呼叫與環境組態，**不得重寫腳本規格**。

---

## 不採納的發現（10 項，附理由）

| 原始發現 | 不採納理由 |
|---|---|
| RunDiscovery 待審批／需要復原篩選缺索引 | 提案的兩個索引其中一個已存在且形狀可用（`DbBootstrap.cs:434` `ix_agent_run_approval_pending`）；另一個要挑什麼形狀取決於實際 EXPLAIN ANALYZE。目前無延遲症狀，現在下索引是投機優化。改列「待量測再議」 |
| AgentTestConsole 抽 `useAgentTestRun` hook | 發現本人明言「目前功能正確，不是壞掉」。純重構、L 級迴歸面，違反「補強≠重寫」 |
| AgentEditor.tsx 1908 行拆 14 個子元件檔 | 同上。「不修會發生什麼」的答案是「review diff 比較吵」 |
| 抽 `useCursorList` 共用 hook | 只有 3 個呼叫點，現在抽是 dead flexibility。真正的缺陷（缺世代守衛）已列 W1-06 |
| >500 行元件追蹤清單 + 抽檔規則 | 團隊流程提案，無程式碼變更，無法用 checkbox 驗收 |
| AppShell 視圖組態陣列化 | L 級架構調整，發現本人標「非急迫、不建議現在單獨立項」；作者已用 `agentPlatformTabs()` 緩解 |
| types.ts 1031 行按域拆檔 | 發現本人明言「不建議現在優先做」；會讓進行中的多條功能分支全部產生 import 衝突 |
| Playwright 選擇器翻新 | 提案本身就是「不要一次翻新，訂規則」。可執行的部分已折進 W1-03 規格 |
| verify-*.ps1 補 .sh 對照本 | pwsh 7 跨平台，無實際阻斷回報。只保留零成本的一半（README 一句話，已併入 W1-19） |
| DbBootstrap 1000 行 DDL 常數 | 發現本人已標「已有計畫在途（architecture-hard-reset P2/P3），僅記錄已檢視，無新提案」 |

---

## 執行建議

1. **Wave 1 先跑 W1-01**（P0，最小 diff，最大風險降低），再跑其餘 P1。
2. **W1-18 是本輪唯一的跨服務契約變更**，開工前務必先跑 `contract-change` skill；W1-10／W1-12 涉及 Dapper ↔ InMemory，開工前先跑 `parity-check` skill。
3. **Wave 2 的 8 道決策可以一次性回覆**，其中 W2-01、W2-02、W2-04 決定了 Wave 3 的三項是否成案。
4. 每一波結束跑 `code-reviewer`；W1-13／W1-18 跨服務，收尾跑 `e2e-verifier`；W1-19 交 `docs-updater`。
