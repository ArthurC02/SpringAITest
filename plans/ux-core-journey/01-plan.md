# 計畫書 — 核心旅程 UX 修復（P0：推廣前必修）

> **狀態：P0 四個 workstream（WS1 a/b/c、WS2、WS3、WS4）已於 2026-08-09 全數實作交付，通過 code review 與測試（platform xUnit 973 綠；frontend build/lint/logic 143、UI 122 綠）。WS2（PDF/Word）採使用者拍板選項 (a) 前端瀏覽器內抽字，授權新增依賴 `pdfjs-dist@6.2.108`、`mammoth@1.12.0`（exact pin）；懶載入獨立 chunk、掃描版 PDF 明確錯誤、`.txt`/`.md` 路徑不變，並經第三輪 e2e 驗證。e2e 額外揪出並修復 `frontend/nginx.conf` 缺 `.mjs` MIME 映射導致容器模式 pdf.js worker 拒載的 bug（dev server 不經 nginx 故單元/UI 測試抓不到）。**
>
> 實作時記錄的刻意取捨：sentinel 標記會隨內容進 ConversationStore 與 mem0（前端渲染層剝離，已測試釘住）。
>
> **前置脈絡（WHY）：**
> 2026-08-08 PM 審查記錄於 [plans/product-review-2026-08/00-findings.md](../product-review-2026-08/00-findings.md)。四個問題編號沿用該文件：**C1**（核心旅程「上傳文件→針對文件提問」入口斷裂，對應 WS1）、**C7**（文件鏈只吃 `.txt`/`.md` 純文字，對應 WS2）、**C8**（最佳介面 AI 副駕預設收合無標籤，對應 WS3）、**C10**（USER 層技術細節外洩，對應 WS4）。
>
> **細節規格：** [02-spec.md](02-spec.md)（逐 workstream 的現況／目標行為／不做什麼／舊碼盤點／風險；WS2 的三選項比較亦在此，本檔僅記錄最終拍板結果）。

## 與既有計畫的邊界關係

- **[settings-skill-redesign](../settings-skill-redesign/01-plan.md)** 是體驗前例（把技術概念包裝成非技術使用者看得懂的介面的方法論），但動的是 ADMIN 的 Skill 撰寫體驗；本計畫動的是 USER 的日常使用體驗，兩者不重疊。
- **copilot-shared-core** 是副駕邊界 authority（AG-UI 與 `/api/chat*` 共用 skill-routing/mem0/persistence 核心、`session.token` 絕不進 `useCopilotReadable` 等安全邊界，見根 AGENTS.md「AG-UI copilot」節）；本計畫的 WS3（副駕升格主入口）只調整呈現層（`defaultOpen`、按鈕文案、首次登入引導時機），不重新定義、不新增 action、不繞過既有安全模型。
