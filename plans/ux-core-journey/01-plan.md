# 計畫書 — 核心旅程 UX 修復（P0：推廣前必修）

> **狀態：WS1（a/b/c）、WS3、WS4 已於 2026-08-09 實作交付，通過 code review 與測試（platform xUnit 973 綠；frontend build/lint/logic 143、UI 122 綠）。WS2（PDF/Word）已於 2026-08-09 由使用者拍板選項 (a) 前端瀏覽器內抽字，並授權新增依賴 `pdfjs-dist@6.2.108`、`mammoth@1.12.0`（exact pin）；已實作交付（懶載入獨立 chunk、掃描版 PDF 明確錯誤、`.txt`/`.md` 路徑不變），並經第三輪 e2e 驗證。e2e 揪出並已修復 `frontend/nginx.conf` 缺 `.mjs` MIME 映射導致容器模式 pdf.js worker 拒載的 bug（dev server 不經 nginx 故單元/UI 測試抓不到）。至此 P0 四個 workstream 全數交付。**
> 本計畫是 P0 tranche（推廣前必修），範圍窄且刻意保守：只接通/修文既有旅程與外洩字串，
> 不新增後端 API、不新增聊天/文件的資料模型。
>
> 實作時記錄的刻意取捨：sentinel 標記會隨內容進 ConversationStore 與 mem0（前端渲染層剝離，已測試釘住）。
>
> docker compose 全鏈路 e2e-verifier 驗證尚未執行，列為合併前待辦。
>
> **前置脈絡（WHY）：**
> 2026-08-08 PM 審查記錄於 [plans/product-review-2026-08/00-findings.md](../product-review-2026-08/00-findings.md)
> （另一代理同時撰寫）。四個問題編號直接沿用該文件：**C1**（核心旅程「上傳文件→針對文件提問」入口斷裂）、
> **C7**（文件鏈只吃 `.txt`/`.md` 純文字）、**C8**（最佳介面 AI 副駕預設收合無標籤）、
> **C10**（USER 層技術細節外洩：登入頁種子帳密、聊天空狀態印 API 端點、「session 已過期」）。
>
> **實裝位置：**
> • 文件旅程：`frontend/src/components/DocumentsView.tsx`、`frontend/src/hooks/useDocuments.ts`（WS1）
> • PDF/Word：`frontend/src/components/DocumentsView.tsx`（WS2，僅決策記錄，見 02-spec）
> • 副駕入口：`frontend/src/components/AppShell.tsx`（WS3）
> • 外洩清除：`frontend/src/components/AuthPage.tsx`、`frontend/src/components/MessageList.tsx`（WS4）
> • 聊天來源標示（WS1 的一部分）另涉 `platform/src/Platform.Service/SkillRoutingAgent.cs`
>
> **細節規格：** [02-spec.md](02-spec.md)（逐 workstream 的現況／目標行為／不做什麼／舊碼盤點／風險）。

## 1. 動機摘要

00-findings 記錄的四個問題，用產品語言重述：

- **C1（旅程斷裂）**：使用者上傳文件後，沒有任何畫面元素引導他「現在可以問這份文件了」。文件列表與聊天視圖是兩個互不知道對方存在的視圖——`DocumentsView` 不曉得聊天，`ChatView` 不曉得文件。
- **C7（格式受限）**：知識庫只吃純文字。真實使用者的文件多半是 PDF/Word；目前的解法是讓使用者自己另存新檔成 `.txt`，這對非技術使用者是一道隱形門檻。
- **C8（副駕埋沒）**：全站唯一橫跨五個視圖、對非技術使用者最友善的入口（AI 副駕側欄）預設收合、按鈕無文字標籤，使用者不知道它存在。
- **C10（技術外洩）**：登入頁印種子密碼、聊天空狀態印 `POST /api/chat/stream`、逾時訊息用「session」這種開發者黑話——這些字串出現在 USER 會看到的畫面上，不是 ADMIN 專屬頁面。

四項共同點：**都不需要新後端能力**，是既有拼圖沒接起來、或該隱藏的內部細節冒出來。這正是「推廣前必修」的定位——先把已經做出來的東西接通、把不該外洩的東西藏好，而不是先做新功能。

## 2. 範圍：四個 Workstream

| # | Workstream | 對應問題 | 服務 | 決策狀態 |
| - | ---------- | -------- | ---- | -------- |
| WS1 | 接通文件→提問旅程 | C1 | frontend（+ platform 的來源標示） | 已定案，可直接實作 |
| WS2 | PDF/Word 支援 | C7 | frontend 或 platform（待決） | **開放決策，本計畫不拍板**（見 02-spec §2） |
| WS3 | 副駕升格主入口 | C8 | frontend | 已定案，可直接實作 |
| WS4 | USER 層外洩清除 | C10 | frontend | 已定案，可直接實作 |

四項共守一條硬約束：**不得修改任何既有後端 API 契約**（路由、DTO 形狀、SSE wire 格式、202 非同步流程、ApiError 形狀一概不動——見根 [AGENTS.md](../../AGENTS.md) Cross-Service Contracts 與 [docs/cross-service-contracts.md](../../docs/cross-service-contracts.md)）。WS1 的聊天來源標示是本計畫唯一碰到 platform 程式碼的地方，且刻意設計成不新增 SSE 欄位（見 02-spec §1 風險節）。

## 3. 階段切分與依賴

```
P0（無依賴，建議最先落地）─┬─ WS4 全部四項（純前端文案 + 條件渲染）
                          │
P1（無跨階段依賴）────────┼─ WS1-a「問這份文件」動作（DocumentsView + AppShell，純前端）
                          ├─ WS1-b 文件就緒主動通知（useDocuments + AppShell，純前端）
                          │
P2（依賴 P1 已有「問這份文件」入口，驗收時合看完整旅程）──── WS1-c 聊天氣泡標示 skill 來源
                          │                                （platform SkillRoutingAgent.cs + frontend ChatBubble.tsx）
                          │
P3（與 P0–P2 無耦合，可平行推進）── WS3 副駕升格主入口
                          │
P4（決策先行，不含實作）── WS2 PDF/Word：產出選項比較 → 待 PM/使用者三選一拍板
                             （拍板後才進入實作，不在本計畫驗收範圍內）
```

**排序理由：**
- P0（WS4）優先：改動最小、風險最低、跨檔案零耦合，四個字串/條件渲染獨立可個別合併。
- P1 先於 P2：P2 的「氣泡標來源」若脫離「先能問這份文件」的入口單獨驗收，看不出完整旅程效果；P1 完成後 P2 才有真實使用情境可測。
- P3 獨立於 P1/P2：副駕入口調整不碰文件/聊天資料流，可與前兩階段平行由不同人推進。
- P4 放最後且不含實作：WS2 是本計畫刻意不拍板的開放決策（前端新依賴 / platform 新依賴 / 維持現狀三選一，皆需使用者明確授權——見 AGENTS.md「前端無 UI 庫由選擇；不新增依賴需明確授權」硬規則），本計畫的產出是把選項攤開，不是替使用者決定。

## 4. 驗收關卡

| 階段 | 驗收方式 | 通過標準 |
| ---- | -------- | -------- |
| P0（WS4） | frontend lint + build；`e2e-verifier` 開瀏覽器截圖 | 三處字串已換（種子帳密條件渲染／「登入已逾時」／聊天空狀態無 API 路徑字樣）；`npm run build` 產物中種子帳號區塊不渲染 |
| P1（WS1-a/b） | frontend lint + build；`e2e-verifier`（Playwright） | 文件列表可見「問這份文件」操作且依狀態啟用/停用；文件轉 ready 時，即使當下停留在聊天視圖也能看到 toast |
| P2（WS1-c） | platform xUnit（`SkillRoutingAgent` 既有測試擴充）+ frontend 單元測試（`ChatBubble` 剝離邏輯）+ `e2e-verifier` | 命中 skill 路由的氣泡顯示來源徽章；`event:error` 中途失敗路徑不受影響（既有測試不能變紅）；SSE frame 格式（`data:<value>`，無空格）逐位元組不變 |
| P3（WS3） | frontend lint + build；`e2e-verifier` | 首次登入自動展開副駕一次；按鈕有文字標籤；後續登入尊重使用者自己的開關狀態；`useCopilotReadable` 仍不含 token |
| P4（WS2） | 無程式碼驗收關卡 | 產出是決策記錄本身；拍板後需求另開實作追蹤（本計畫或後續計畫） |

跨階段共同關卡：每階段完成後跑 `code-reviewer`（正確性 + 跨服務契約）與必要時 `code-simplifier`；涉及 platform/frontend 跨服務行為的 P2、P3 收尾前跑 `e2e-verifier` 全鏈路（聊天 SSE / 文件 202 契約不得破，比照根 AGENTS.md 慣例）。

## 5. 與既有計畫的關係

- **[settings-skill-redesign](../settings-skill-redesign/01-plan.md) 是體驗前例**：該計畫示範了「把技術概念（YAML/node/flow）包裝成非技術使用者看得懂的介面」的方法論——範本代勞、零術語、當場可試、漸進揭露。本計畫的 WS1（問這份文件）與 WS3（副駕主入口）延續同一種 UX 哲學，但範圍窄得多：不涉及 Skill 編輯或引擎擴充，只是把既有聊天/文件旅程的入口接通、把既有副駕入口的可見度調高。兩個計畫不重疊：settings-skill-redesign 動的是 ADMIN 的 Skill 撰寫體驗，本計畫動的是 USER 的日常使用體驗。
- **[copilot-shared-core](../copilot-shared-core/01-plan.md) 是副駕邊界 authority**：該計畫定義了 AG-UI 與 `/api/chat*` 兩條鏈路共用同一套 skill-routing/mem0/persistence 核心、CopilotKit action 只能操作表單不能代按發布、`session.token` 絕不進 `useCopilotReadable` 等安全邊界（見根 AGENTS.md「AG-UI copilot」節）。本計畫的 WS3（副駕升格主入口）與 WS1-c（聊天氣泡標來源，若涉及 AG-UI 路徑）一律視 copilot-shared-core 定的邊界為既有權威，不重新定義、不新增 action、不繞過既有安全模型。WS3 只調整**呈現層**（`defaultOpen`、按鈕文案、首次登入引導時機），不碰 copilot-shared-core 定義的任何 pipeline/wrapper 順序。

## 6. 明確不做（YAGNI 邊界）

- 不改動任何既有後端 API 契約（路由、DTO、SSE wire 格式、202 flow、ApiError 形狀）——四個 workstream 全數以此為硬約束，不得以「體驗需要」為由破例。
- WS2 不在本輪拍板實作選項，只列出比較（見 02-spec §2）；三個選項都需要新依賴或行為變更的明確授權，不由本計畫代為決定。
- 不做 i18n / 多語言框架（WS4 只是把技術黑話換成中文人話，不引入國際化機制）。
- 不做 onboarding tour 套件或引導框架（WS3 的首次引導純用既有 localStorage + CSS/Toast 基礎機制）。
- 不做文件多選批次操作（WS1 的「問這份文件」是單列動作，不做勾選多份文件一起問的批次 UI）。
- 不建置伺服器端「使用者是否已引導過副駕」旗標（留在瀏覽器 localStorage 足夠，見 02-spec §3 風險節）。
- 不擴大 CopilotKit 可代為執行的 action 清單（WS3 純粹是可見度/呈現調整，不新增 action、不放寬既有安全邊界）。

## 7. 舊碼盤點（總覽，逐項細節見 02-spec 各 workstream 末節）

| Workstream | 被取代/需同步的既有程式碼 | 02-spec 章節 |
| ---------- | -------------------------- | ------------- |
| WS1 | `DocumentsView.tsx` 操作欄佈局；`useDocuments.ts` 的 `fetchList`/`startPolling` 回傳形狀（新增就緒偵測）；`AppShell.tsx` copilot 操作手冊文案；`SkillRoutingAgent.cs` 串流結尾邏輯；`ChatBubble.tsx` 純文字/Markdown 渲染路徑 | §1 舊碼盤點 |
| WS2 | `DocumentsView.tsx:91` 的 ponytail 註解與 `:164` 的 `accept` 屬性（若選 (a)/(b)，該註解會過期失真） | §2 舊碼盤點 |
| WS3 | `AppShell.tsx:338` 的 `defaultOpen={false}` 字面常數 | §3 舊碼盤點 |
| WS4 | `AuthPage.tsx:208-212` 種子帳號區塊的無條件渲染；`AuthPage.tsx:35` 過期文案；`MessageList.tsx:59-64` 空狀態技術提示 | §4 舊碼盤點 |

沒有任何 workstream 產生「整個檔案退場」等級的死碼——四項都是既有畫面的局部調整，不是替換整條技術路徑，因此盤點顆粒度落在「行/區塊」而非「檔案」。
