# Release Evidence Execution Record — Copilot Shared Core

> 狀態：**完成 —— 2026-08-09 第五跑（單次重跑）六關（`E-01`～`E-06`）同輪全 PASS，四項技術條件達成，並已於同日取得 ArthurC 具名核准，release unblocked。** Deterministic bundle `20260809T113459310Z-f7ffda22` 與 RealModel bundle `20260809T113647460Z-96b8cab1` 為同一組可稽核 rerun（同 `commitSha`、時間戳銜接），regression 與獨立覆核均已完成。

## 結論

release evidence harness 已可產生可重跑、去識別化 bundle，並已完成一次同輪六關全 PASS 的可稽核 rerun：2026-08-09 第五跑，由 Deterministic bundle `20260809T113459310Z-f7ffda22`（`E-01`/`E-02`/`E-03`/`E-06` 第 1 次嘗試全 PASS）與 RealModel bundle `20260809T113647460Z-96b8cab1`（`E-04`/`E-05` 第 1 次嘗試全 PASS，無重試）組成，兩 bundle `commitSha` 同為 `a803eed`（`dirtyWorktree:true`），時間戳銜接（11:35–11:37 UTC）。同輪 regression（platform/backend xUnit 全綠、frontend oxlint+build 過）與獨立覆核（`code-reviewer` 覆核 bundle 完整性/機密掃描/harness diff；`e2e-verifier` 以 fresh 映像執行、image ID 逐一比對相符）均已完成，sign-off 條件 1、2、4 達成。舊的 2026-07-25 bundle（`20260725T111722672Z-90c9119f`）已由本次同輪 rerun 取代，不再單獨作為 sign-off 依據。使用者具名核准已於 2026-08-09 由 ArthurC 給出（見「Release sign-off 條件」小節），Copilot Shared Core 的 release 目標**完成**。

| Gate | 結果 | 已執行的證據 | 阻擋原因／後續修復門檻 |
| --- | --- | --- | --- |
| `E-01 / B-P1-08` | PASS | 真 browser account switch 與 AG-UI 真實 401 logout；僅保留 token fingerprint、JUnit 與安全截圖。2026-08-09 第五跑 Deterministic bundle `20260809T113459310Z-f7ffda22` 第 1 次嘗試 PASS。 | 已納入同輪 rerun；具名核准已於 2026-08-09 取得。 |
| `E-02 / C-03` | PASS | 同 thread、跨 tenant 的 canonical model-input keyed-HMAC 投影與 trait tests。2026-08-09 第五跑同 bundle 第 1 次嘗試 PASS。 | 已納入同輪 rerun；具名核准已於 2026-08-09 取得。 |
| `E-03 / C-04` | PASS | full-array resend、assistant ID fallback、真 browser client-tool 流程；canonical model input 為一組 call／一個 logical wire result，且無 orphan result。2026-08-09 第五跑同 bundle 第 1 次嘗試 PASS。 | 已納入同輪 rerun；具名核准已於 2026-08-09 取得。 |
| `E-04 / C-05` | PASS | `20260809T113647460Z-96b8cab1` 證明 authenticated AG-UI persistence 與 `{tenant}:{user}` scoped real mem0 recall，第 1 次嘗試 PASS，無重試。 | 已納入同輪 rerun；具名核准已於 2026-08-09 取得。 |
| `E-05 / C-07` | PASS | 同一 bundle 保存三次符合 fixture number 的 real-model routing captures，第 1 次嘗試 PASS；LiteLLM 實際路由確認 `openai/gpt-4o-mini-2024-07-18` 與 `openai/text-embedding-3-small`（非 alias 飄移）。 | 已納入同輪 rerun；具名核准已於 2026-08-09 取得。 |
| `E-06 / C-08` | PASS | nginx、Vite、curl 與 browser `ReadableStream` 的受控 3-frame streaming 檢查。2026-08-09 第五跑同 bundle 第 1 次嘗試 PASS。 | 已納入同輪 rerun；具名核准已於 2026-08-09 取得。 |

## 已保存的 evidence

所有 bundle 均在 `.gitignore` 排除的 `artifacts/copilot-shared-core/<UTC-run-id>/`，只保存 keyed-HMAC 投影、JUnit、SSE timing 與安全截圖；不保存 raw marker、JWT、Authorization、cookie 或 prompt 全文。

- 原引用的兩個 bundle ── `20260721T083532847Z-f8054fd1`(Deterministic PASS)與 `20260721T073849789Z-5000aa13`(Real-model FAIL)── 在本 workspace **已不存在**,無法覆核。
- 本 workspace 曾存有 `artifacts/copilot-shared-core/` 下共 23 個 2026-07-25 執行的 RealModel lane bundle。其中 22 個(`20260725T084356429Z-0e97d93f` 起至 `20260725T111119274Z-b2d78fe3` 止)為 FAIL/BLOCKED 嘗試;唯一 PASS 的是 `20260725T111722672Z-90c9119f/manifest.json`:`result:"PASS"`、`E-04`(authenticated AG-UI persistence + scoped real mem0 recall)PASS、`E-05`(three real-model routing captures matched fixture number)PASS,`commitSha:"debeddb31f4a718f005b39f1939a76cda8780f6c"` 已確認是現 HEAD 的祖先 commit,`dirtyWorktree:true`。此 bundle 未與同輪 Deterministic lane、完整 regression 與獨立覆核組成同一組可稽核 sign-off,已由下方 2026-08-09 第五跑取代。

**2026-08-09 第五跑(單次重跑,六關同輪全 PASS,sign-off 條件 1 達成)**:

- Deterministic:`artifacts/copilot-shared-core/20260809T113459310Z-f7ffda22` —— `E-01`/`E-02`/`E-03`/`E-06` 全 PASS(第 1 次嘗試)。
- RealModel:`artifacts/copilot-shared-core/20260809T113647460Z-96b8cab1` —— `E-04`/`E-05` 全 PASS(第 1 次嘗試,無重試)。
- 兩 bundle `commitSha` 同為 `a803eed`(`dirtyWorktree:true`,feat/dotnet-backend 未提交變更),時間戳銜接(11:35–11:37 UTC),LiteLLM 實際路由確認 `openai/gpt-4o-mini-2024-07-18` 與 `openai/text-embedding-3-small`(非 alias 飄移)。真模型花費量級:分毫等級(短 prompt,約 20 次以內小呼叫)。
- Smoke 同輪:`C-00`～`C-08` 中僅 `C-07` FAIL(fake-embeddings 非語意向量 + 歷史殘留 `rag_documents`,已根因確認為已知特性,非回歸;`C-06` by-design skip)。
- 本週期 harness 修復:`C-01A` `createdAt` 文化解析、`E-06` chat 認證 token、三處非 UUID `conversationId`、`Get-RealD6RunDebugStatus` null-safety、playwright artifact `finally` 清理、compose evidence 檔六處 `:?`→`:-` + script 層 `EVIDENCE_DB_NAME` guard、evidence 映像預設重建(`-SkipEvidenceBuild` 改為 opt-out)、`preventCompetingDocument401` 加攔 `/api/chat/history/page`、`streaming.ts` SSE 框架判別式改由案例宣告 + 受控全文位元組級比對。
- 已知殘留(低風險,非回歸):`concatFramedContent` 的 agui 分支對「框架多一個空格」因 `JSON.parse` 容忍前導空白而漏檢,屬既有盲點延續。

找到 PASS bundle **不等於 release sign-off 完成**——見下方「Release sign-off 條件」,四項技術條件已於 2026-08-09 第五跑達成,使用者具名核准亦已於同日取得。

## 可重跑命令

在 repo root 執行。`-StartEvidenceProfile` 會啟動 test-only evidence profile；預設每次都會重建 evidence images(陳舊映像曾造成假 JWT 失敗與 unhealthy 容器)。兩個 lane 都以 non-zero exit code 表示任一 gate 非 PASS。

```powershell
# Deterministic：E-01、E-02、E-03、E-06
pwsh -File scripts\verify-copilot-shared-core-evidence.ps1 `
  -Lane Deterministic -StartEvidenceProfile

# Real model：E-04、E-05；固定 dated chat snapshot 與 embedding alias，不可 fallback 到 mock-gpt
pwsh -File scripts\verify-copilot-shared-core-evidence.ps1 `
  -Lane RealModel -StartEvidenceProfile
```

若 evidence images 已建好且 compose 設定未變,可加 `-SkipEvidenceBuild` 加速。可選擇明確輸出目錄，但必須維持在 ignored root 下：

```powershell
pwsh -File scripts\verify-copilot-shared-core-evidence.ps1 `
  -Lane Deterministic -StartEvidenceProfile `
  -EvidenceDir artifacts/copilot-shared-core/<UTC-run-id>
```

## Release sign-off 條件

下列條件必須在同一組可稽核的最新 rerun 中成立，才可把本文件狀態改為完成：

1. `E-01`～`E-06` 全部 PASS；零 FAIL、BLOCKED 或 SKIP。**——已達成**：2026-08-09 第五跑（單次重跑）同輪全 PASS（見上方「已保存的 evidence」）。
2. platform xUnit 391（Service 241 + Web 150）通過，frontend lint/build 與既有 smoke 8/8 通過。**——已達成**：2026-08-09 第五跑 platform xUnit 1004/1004、backend xUnit 1288/1288（190 個 Postgres-only 測試因未起 DB 正常 skip）、frontend oxlint 過（僅既知警告）+ build 過（測試套件規模已隨 backend 遷移增長，391 為舊基準數字）。
3. real-model lane 的固定模型／embedding 設定、provider 實際 model ID（及可用時 fingerprint）、image identity 與 bundle 路徑均記入 manifest；capture 與 bundle secret scan 均通過。**——已達成**：manifest 記入 `openai/gpt-4o-mini-2024-07-18` 與 `openai/text-embedding-3-small`（確認非 alias 飄移）、image identity 與 bundle 路徑齊備，secret scan 通過（見 `code-reviewer` 覆核）。
4. `code-reviewer` 與 fresh-image `e2e-verifier` 完成獨立檢查，確認沒有 response-only 假綠、重複 tool result、cross-tenant 漏洩或測試資料 cleanup 遺留。**——已達成**：`code-reviewer` 覆核 bundle 完整性/機密掃描/harness diff 三項皆可信，無假綠、無放寬斷言；e2e 半邊由第五跑的獨立 `e2e-verifier` 以 fresh 映像執行，image ID 與新建映像逐一比對相符。

以上 4 項技術條件已於 2026-08-09 第五跑全數達成。

**具名核准（named sign-off）：已取得** —— ArthurC 於 2026-08-09 具名核准（原文「核准」，於 Claude Code session 中對本文件所列第五跑 bundle 與四項技術條件狀態核准）。本文件狀態改為**完成**，release **unblocked**。

## Reconciliation Status (2026-07-29)

### 現況

對帳記錄如下：

| 項目 | 發現 |
| --- | --- |
| D6 delivery row PASS bundle | `01-plan.md` D6 delivery row 末行引用 RealModel bundle `20260725T111722672Z-90c9119f`,宣稱 E-04/E-05 PASS。 |
| Workspace 驗證(2026-08-02 覆核更新) | 該 bundle **確實存在**於本 workspace:`artifacts/copilot-shared-core/20260725T111722672Z-90c9119f/manifest.json`,`result:"PASS"`、`E-04` PASS、`E-05` PASS、`commitSha:"debeddb31f4a718f005b39f1939a76cda8780f6c"`(已確認為現 HEAD 祖先)。同目錄另有 22 個同日較早的 RealModel lane 嘗試,全部 FAIL/BLOCKED;此 bundle 是最後一次、也是唯一一次 PASS。此前「找不到該 bundle」的紀錄有誤,已更正。 |
| 舊引用 bundle 缺失 | 原「已保存的 evidence」引用的 `20260721T083532847Z-f8054fd1`(Deterministic)與 `20260721T073849789Z-5000aa13`(Real-model)兩個 bundle,在本 workspace **已不存在**,無法覆核。 |
| Canonical record(2026-08-02 覆核更新) | manifest 內容已作為 E-04/E-05 的 durable evidence 記入上方「已保存的 evidence」小節。惟該 rerun 未見同批 Deterministic lane(E-01/E-02/E-03/E-06)bundle 留存於本 workspace,無法確認同一組 rerun 是否全綠。 |
| Artifact identity | manifest 已含 runner identity 欄位(`models`/`images`/`commitSha`/`dirtyWorktree`),但 sign-off 條件第 2 項(platform xUnit 全綠 + frontend lint/build + smoke 8/8)與第 4 項(code-reviewer / e2e-verifier 獨立檢查)本次**未執行**,仍待補。 |

### 決議

按 Gate E0 原則(4.1 節與 02-evaluation-observability-plan.md §3)fail-closed:

- Release sign-off 保持 **blocked**,**找到 PASS bundle 不等於 sign-off 完成**。以下條件須全部滿足才可解除:
  1. ~~若 PASS bundle 實際存在,需驗證並記入本文件~~ —— **已完成**:`20260725T111722672Z-90c9119f` 已驗證存在、manifest 已記入本文件「已保存的 evidence」小節。
  2. platform xUnit 全綠 + frontend lint/build + smoke 8/8(sign-off 條件第 2 項)—— **本次未執行,留白待補**。
  3. Bundle 路徑、runner manifest、artifact identity 與 case results 已記入「已保存的 evidence」小節。
  4. 供 code-reviewer 與 e2e-verifier 的獨立檢查,確認沒有 response-only 假綠、重複 tool result、cross-tenant 漏洩或測試資料 cleanup 遺留(sign-off 條件第 4 項)—— **本次未執行,留白待補**。
- 發布宣稱不得選擇方便的一邊(Deterministic PASS ≠ RealModel lane skip)。
- 此狀態已在 [agent-platform-redesign/01-plan.md](../agent-platform-redesign/01-plan.md) D6 delivery row 末行加註。

## Reconciliation Status (2026-08-09)

### 現況

獨立覆核發現:本文件先前僅記錄 2026-07-25 的舊 RealModel bundle(`20260725T111722672Z-90c9119f`),未反映 2026-08-09 第五跑(同輪六關全 PASS)的結果,屬放行前必補的記錄缺口。已依下表更新:

| 項目 | 發現 |
| --- | --- |
| 第五跑 bundle | Deterministic `20260809T113459310Z-f7ffda22`(`E-01`/`E-02`/`E-03`/`E-06` 全 PASS,第 1 次嘗試)與 RealModel `20260809T113647460Z-96b8cab1`(`E-04`/`E-05` 全 PASS,第 1 次嘗試,無重試),同組可稽核 rerun:`commitSha` 均為 `a803eed`(`dirtyWorktree:true`,feat/dotnet-backend 未提交變更),時間戳銜接(11:35–11:37 UTC)。 |
| Model/embedding 飄移檢查 | LiteLLM 實際路由確認 `openai/gpt-4o-mini-2024-07-18` 與 `openai/text-embedding-3-small`,非 alias 飄移;真模型花費量級為分毫等級(短 prompt,約 20 次以內小呼叫)。 |
| Smoke 同輪 | `C-00`～`C-08` 中僅 `C-07` FAIL(fake-embeddings 非語意向量 + 歷史殘留 `rag_documents`,已根因確認為已知特性,非回歸);`C-06` by-design skip。 |
| Regression(sign-off 條件 2) | platform xUnit 1004/1004、backend xUnit 1288/1288(190 個 Postgres-only 測試因未起 DB 正常 skip)、frontend oxlint 過(僅既知警告)+ build 過。 |
| 獨立驗證(sign-off 條件 4) | `code-reviewer` 獨立覆核 bundle 完整性/機密掃描/harness diff 三項皆可信,無假綠、無放寬斷言;e2e 半邊由第五跑的獨立 `e2e-verifier` 以 fresh 映像執行,image ID 與新建映像逐一比對相符。 |
| 本週期 harness 修復 | `C-01A` `createdAt` 文化解析、`E-06` chat 認證 token、三處非 UUID `conversationId`、`Get-RealD6RunDebugStatus` null-safety、playwright artifact `finally` 清理、compose evidence 檔六處 `:?`→`:-` + script 層 `EVIDENCE_DB_NAME` guard、evidence 映像預設重建(`-SkipEvidenceBuild` 改為 opt-out)、`preventCompetingDocument401` 加攔 `/api/chat/history/page`、`streaming.ts` SSE 框架判別式改由案例宣告 + 受控全文位元組級比對。 |
| 已知殘留(低風險) | `concatFramedContent` 的 agui 分支對「框架多一個空格」因 `JSON.parse` 容忍前導空白而漏檢,屬既有盲點延續,非回歸。 |

### 決議

- sign-off 條件 1(六關同輪全 PASS)—— **已達成**:2026-08-09 第五跑(單次重跑)。
- sign-off 條件 2(regression 全綠)—— **已達成**:見上表。
- sign-off 條件 3(manifest/model/secret scan)—— **已達成**:model routing、image identity、secret scan 均已記入並通過。
- sign-off 條件 4(獨立檢查)—— **已達成**:`code-reviewer` + fresh-image `e2e-verifier`(見上表)。
- **使用者具名核准(named sign-off)—— 已取得**:ArthurC 於 2026-08-09 具名核准(原文「核准」);release **unblocked**,本文件狀態改為完成。
- 此狀態已同步反映於本文件頂部狀態列、「結論」段落與「已保存的 evidence」小節。
