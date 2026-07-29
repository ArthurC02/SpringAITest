# Release Evidence Execution Record — Copilot Shared Core

> 狀態：**已執行，但 release blocked/failed。** 現有 evidence 不得作為 release sign-off，也不得以 smoke 8/8 或局部 gate PASS 取代未通過的 gates。

## 結論

release evidence harness 已可產生可重跑、去識別化 bundle；最新 Deterministic lane 的 `E-01`、`E-02`、`E-03`、`E-06` 全部 PASS，但 RealModel lane 的 `E-04`、`E-05` 未達 PASS。因此 Copilot Shared Core 的 release 目標尚未完成。

| Gate | 結果 | 已執行的證據 | 阻擋原因／後續修復門檻 |
| --- | --- | --- | --- |
| `E-01 / B-P1-08` | PASS | 真 browser account switch 與 AG-UI 真實 401 logout；僅保留 token fingerprint、JUnit 與安全截圖。 | 保留於後續 full rerun。 |
| `E-02 / C-03` | PASS | 同 thread、跨 tenant 的 canonical model-input keyed-HMAC 投影與 trait tests。 | 保留於後續 full rerun。 |
| `E-03 / C-04` | PASS | full-array resend、assistant ID fallback、真 browser client-tool 流程；canonical model input 為一組 call／一個 logical wire result，且無 orphan result。 | 保留於後續 full rerun。 |
| `E-04 / C-05` | FAIL | 固定 snapshot 的真模型 preflight、authenticated AG-UI persistence、bounded mem0 search。 | mem0 未在時限內取回 authenticated fact；必須確認寫入、索引／抽取與 `{tenant}:{user}` scope 後重新通過。 |
| `E-05 / C-07` | FAIL | 第一輪 chat 最終回覆未包含 fixture 數字，runner 因而 fail-closed 停止。 | 現有 bundle 未保存可稽核的 routing capture；必須修正 fixture 到檢索／生成答案的路徑，完成三輪回答與兩鏈路 capture 後才可 PASS。 |
| `E-06 / C-08` | PASS | nginx、Vite、curl 與 browser `ReadableStream` 的受控 3-frame streaming 檢查。 | 保留於後續 full rerun。 |

## 已保存的 evidence

所有 bundle 均在 `.gitignore` 排除的 `artifacts/copilot-shared-core/<UTC-run-id>/`，只保存 keyed-HMAC 投影、JUnit、SSE timing 與安全截圖；不保存 raw marker、JWT、Authorization、cookie 或 prompt 全文。

- Deterministic PASS bundle：`artifacts/copilot-shared-core/20260721T083532847Z-f8054fd1/`（`E-01`、`E-02`、`E-03`、`E-06` PASS；E-06 只計完整 content SSE events）。
- Real-model failure bundle：`artifacts/copilot-shared-core/20260721T073849789Z-5000aa13/`（`E-04`、`E-05` FAIL）。

Deterministic bundle 是該 lane 的有效證據；Real-model bundle 仍是失敗診斷證據。完整 release 必須在修正 E-04/E-05 後建立新的 RealModel bundle，不能以 deterministic PASS 或舊的局部 PASS 取代。

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

在上述條件完成前，release 保持 **blocked/failed**。

## Reconciliation Status (2026-07-29)

### 現況

對帳記錄如下：

| 項目 | 發現 |
| --- | --- |
| D6 delivery row PASS bundle | `01-plan.md` D6 delivery row 末行引用 RealModel bundle `20260725T111722672Z-90c9119f`，宣稱 E-04/E-05 PASS。 |
| Workspace 驗證 | 當前 workspace 的 `artifacts/copilot-shared-core/` 目錄中**找不到**該 bundle（無 `20260725T111722672Z-*` 目錄）。 |
| Canonical record | 本文件（05-release-evidence-plan.md）仍為唯一 durable evidence record：E-04/E-05 FAIL；即使 Deterministic lane PASS，也不能取代 RealModel lane 未達 PASS 的事實。 |
| Artifact identity 缺失 | 未能提供該 PASS bundle 的 runner manifest、artifact identity、case results 與 secret scan 驗證證據。 |

### 決議

按 Gate E0 原則（4.1 節與 02-evaluation-observability-plan.md §3）fail-closed：

- Release sign-off 保持 **blocked**，直到以下條件全部滿足：
  1. 若 PASS bundle 實際存在，需驗證並記入本文件；
  2. 若 PASS bundle 無法驗證或已遺失，需修復 E-04 mem0 recall 與 E-05 routing/answer fidelity 缺陷，重跑完整 E-01–E-06 RealModel lane 並產生新 bundle；
  3. Bundle 路徑、runner manifest、artifact identity 與 case results 必須記入新增的「已保存的 evidence」小節；
  4. 供 code-reviewer 與 e2e-verifier 的獨立檢查清單完成。
- 發布宣稱不得選擇方便的一邊（Deterministic PASS ≠ RealModel lane skip）。
- 此狀態已在 [agent-platform-redesign/01-plan.md](../agent-platform-redesign/01-plan.md) D6 delivery row 末行加註。
