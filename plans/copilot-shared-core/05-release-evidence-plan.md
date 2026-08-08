# Release Evidence Execution Record — Copilot Shared Core

> 狀態：**已有 RealModel PASS evidence，但 release sign-off 仍 blocked。** 現存 PASS bundle 只證明 `E-04`／`E-05`；不得以該 bundle、smoke 8/8 或局部 gate PASS 取代同一組最新 rerun、完整 regression、獨立 review 與 fresh-image e2e。

## 結論

release evidence harness 已可產生可重跑、去識別化 bundle。Workspace 中最後一次、也是唯一一次 RealModel PASS bundle 為 `20260725T111722672Z-90c9119f`，其中 `E-04`、`E-05` 均 PASS；較早的 22 次 RealModel 嘗試為 FAIL/BLOCKED。這份 PASS bundle 並未與最新 Deterministic lane、完整 regression、獨立 reviewer 與 fresh-image e2e 組成同一組可稽核 sign-off，因此 Copilot Shared Core 的 release 目標尚未完成。

| Gate | 結果 | 已執行的證據 | 阻擋原因／後續修復門檻 |
| --- | --- | --- | --- |
| `E-01 / B-P1-08` | PASS | 真 browser account switch 與 AG-UI 真實 401 logout；僅保留 token fingerprint、JUnit 與安全截圖。 | 保留於後續 full rerun。 |
| `E-02 / C-03` | PASS | 同 thread、跨 tenant 的 canonical model-input keyed-HMAC 投影與 trait tests。 | 保留於後續 full rerun。 |
| `E-03 / C-04` | PASS | full-array resend、assistant ID fallback、真 browser client-tool 流程；canonical model input 為一組 call／一個 logical wire result，且無 orphan result。 | 保留於後續 full rerun。 |
| `E-04 / C-05` | PASS | `20260725T111722672Z-90c9119f` 證明 authenticated AG-UI persistence 與 `{tenant}:{user}` scoped real mem0 recall。 | 必須納入同一組最新 full rerun 與 sign-off 證據；單獨 PASS 不解除 release block。 |
| `E-05 / C-07` | PASS | 同一 bundle 保存三次符合 fixture number 的 real-model routing captures。 | 必須納入同一組最新 full rerun 與 sign-off 證據；單獨 PASS 不解除 release block。 |
| `E-06 / C-08` | PASS | nginx、Vite、curl 與 browser `ReadableStream` 的受控 3-frame streaming 檢查。 | 保留於後續 full rerun。 |

## 已保存的 evidence

所有 bundle 均在 `.gitignore` 排除的 `artifacts/copilot-shared-core/<UTC-run-id>/`，只保存 keyed-HMAC 投影、JUnit、SSE timing 與安全截圖；不保存 raw marker、JWT、Authorization、cookie 或 prompt 全文。

- 原引用的兩個 bundle ── `20260721T083532847Z-f8054fd1`(Deterministic PASS)與 `20260721T073849789Z-5000aa13`(Real-model FAIL)── 在本 workspace **已不存在**,無法覆核。
- 本 workspace 現存 `artifacts/copilot-shared-core/` 下共 23 個 RealModel lane bundle,全部為 2026-07-25 執行。其中 22 個(`20260725T084356429Z-0e97d93f` 起至 `20260725T111119274Z-b2d78fe3` 止)為 FAIL/BLOCKED 嘗試;最後一次、也是唯一一次 PASS 的是 `20260725T111722672Z-90c9119f/manifest.json`:`result:"PASS"`、`E-04`(authenticated AG-UI persistence + scoped real mem0 recall)PASS、`E-05`(three real-model routing captures matched fixture number)PASS,`commitSha:"debeddb31f4a718f005b39f1939a76cda8780f6c"` 已確認是現 HEAD 的祖先 commit,`dirtyWorktree:true`。

找到這個 PASS bundle **不等於 release sign-off 完成**——見下方「Release sign-off 條件」,第 2、4 項本次仍未執行,仍待補。

## 可重跑命令

在 repo root 執行。`-StartEvidenceProfile` 會啟動 test-only evidence profile；首次執行或 evidence image／compose profile 有變動時加入 `-BuildEvidenceProfile`。兩個 lane 都以 non-zero exit code 表示任一 gate 非 PASS。

```powershell
# Deterministic：E-01、E-02、E-03、E-06
pwsh -File scripts\verify-copilot-shared-core-evidence.ps1 `
  -Lane Deterministic -StartEvidenceProfile -BuildEvidenceProfile

# Real model：E-04、E-05；固定 dated chat snapshot 與 embedding alias，不可 fallback 到 mock-gpt
pwsh -File scripts\verify-copilot-shared-core-evidence.ps1 `
  -Lane RealModel -StartEvidenceProfile -BuildEvidenceProfile
```

若 evidence images 已建好且 compose 設定未變，可移除 `-BuildEvidenceProfile`。可選擇明確輸出目錄，但必須維持在 ignored root 下：

```powershell
pwsh -File scripts\verify-copilot-shared-core-evidence.ps1 `
  -Lane Deterministic -StartEvidenceProfile `
  -EvidenceDir artifacts/copilot-shared-core/<UTC-run-id>
```

## Release sign-off 條件

下列條件必須在同一組可稽核的最新 rerun 中成立，才可把本文件狀態改為完成：

1. `E-01`～`E-06` 全部 PASS；零 FAIL、BLOCKED 或 SKIP。
2. platform xUnit 391（Service 241 + Web 150）通過，frontend lint/build 與既有 smoke 8/8 通過。
3. real-model lane 的固定模型／embedding 設定、provider 實際 model ID（及可用時 fingerprint）、image identity 與 bundle 路徑均記入 manifest；capture 與 bundle secret scan 均通過。
4. `code-reviewer` 與 fresh-image `e2e-verifier` 完成獨立檢查，確認沒有 response-only 假綠、重複 tool result、cross-tenant 漏洩或測試資料 cleanup 遺留。

在上述條件完成前，release 保持 **blocked**。

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
