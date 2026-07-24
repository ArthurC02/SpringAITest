# Agent Skills 標準對齊 — 遷移規格（M 系列）

> 狀態：**已交付。** Agent Skills 標準格式、既有資料遷移與 conformance 已落地；外部 scripts 仍保持不可執行。
> 本檔是這次遷移的**單一事實來源**：改名對照表、目標 frontmatter 形狀、安全護欄、階段順序。各 subagent 一律依此，不得自行解讀。

## 0. 官方標準（權威，逐字）

- 目錄：`SKILL.md`（必要，root）+ 選配 `scripts/` `references/` `assets/` + **任意其他檔案/資料夾**。
- frontmatter：
  - `name`（必填）：**1–64 字；小寫 `a-z`、數字 `0-9`、連字號 `-`;不可開頭/結尾為 `-`;不可連續 `--`;須等於資料夾名。**
  - `description`（必填）：1–1024 字，非空。
  - `license`（選填）：授權名或指向 bundled license 檔。
  - `compatibility`（選填）：≤500 字。
  - `metadata`（選填）：**string→string map**（放非標準自訂欄位）。
  - `allowed-tools`（選填）：**空白分隔字串**的預核可工具（實驗性）。
- body：自由 Markdown，建議 <500 行 / <5000 tokens。

## 1. 名稱規則（引擎全面改為標準）

引擎 name 驗證由 `^[a-z][a-z0-9_]{2,63}$`（底線、須字母開頭、min3）**改為標準**：

- 正規：`^[a-z0-9]([a-z0-9-]*[a-z0-9])?$`，長度 1–64，且**不得含 `--`**。
- 即：小寫英數 + 連字號;不可首尾連字號;不可連續連字號;可數字開頭;可 1 字。
- **底線 `_` 不再合法。**

前端 `NAME_PATTERN` 與 `slugifySkillName` 同步改為連字號版（`slugify`：小寫、非 `[a-z0-9]` 連續段→單一 `-`、去頭尾 `-`、擋 `--`、截 64、空字串 fallback `skill`）。前端友善訊息改為「名稱只能用小寫英文、數字、連字號（-），不可首尾或連續連字號（1–64 字）；中文請放在說明」。

## 2. 改名對照表（skill 名稱；**節點/工具識別碼不動**）

**只有 skill `name`（= invoke key = yaml 檔名身分）改。** `@node`/`@tool` 識別碼（`summarize_text@1.0`、`query_intake@1.0`、`retrieve@1.0`、`nl_extract`、`rag_answer`…）、input/output 鍵（`query`/`answer`/`text`）、Python 模組與套件目錄（`app/nodes/kbquery/`）**一律維持底線,不在本次範圍**。

| 舊 skill 名 | 新 skill 名 | 類型 |
| ---------- | ---------- | ---- |
| `kb_query` | `kb-query` | 內建 flow |
| `rag_qa` | `rag-qa` | 內建 flow |
| `analyze_report` | `analyze-report` | 內建 flow（ADMIN） |
| `summarize` | `summarize` | 不變 |
| `triage` | `triage` | 不變 |
| `template_compare` | `template-compare` | 範本 |
| `template_infer` | `template-infer` | 範本 |
| `template_inspire` | `template-inspire` | 範本 |
| `template_retrieval` | `template-retrieval` | 範本 |
| `template_stats` | `template-stats` | 範本 |
| 自訂（DB）如 `year_compare` | `year-compare`（`_`→`-`） | 自訂 flow，DB 遷移 |

範本 `kind` 值、node 引用、business_result 等**不受影響**。

## 3. 目標 frontmatter 形狀（agentic package 的 SKILL.md）

標準欄位在頂層;引擎專屬需求收進 `metadata`（string→string，故結構化者以 JSON 字串存）:

```yaml
---
name: sales-helper
description: 銷售資料查詢與比較。當使用者問銷售數字或年度比較時使用。
license: Proprietary
compatibility: Requires the platform skill engine
allowed-tools: retrieve embed          # ← 取代 uses_tools;空白分隔;無工具則省略或空字串
metadata:
  kind: agentic
  required_role: USER
  timeout_seconds: "30"                 # 字串
  input_schema: '{"question":{"type":"str","required":true}}'   # JSON 字串（string→string 合規）
---
（body：runner instruction）
```

- **`allowed-tools`**：引擎的 tool allowlist 來源;以空白切分;每個名稱須在 tool registry 註冊（原 `unknown_tool` 檢查沿用）。舊 `uses_tools` 頂層欄位不再接受（agentic）。
- **`metadata.kind`**：`agentic`。（flow package 走既有 `skill.yaml` 路徑,不套此形狀。）
- **`metadata.input_schema`**：JSON 字串;parser 解析後供 routing（SingleRequiredStringKey）與 invoke。
- **`metadata.required_role` / `timeout_seconds`**：字串。
- **`license` / `compatibility`**：選填,儲存與 round-trip 保留;引擎目前不據以行為。
- canonical definition（backend 儲存的投影）仍由 workflow 產生;確保 `skills-ref validate`-friendly（頂層只有標準欄位）。

### 3.1 flow package = 單一自足 SKILL.md（**頂層資料夾，取消獨立 skill.yaml**）

使用者要求:下載的 zip 不應有獨立 `.yaml`;flow 定義應內嵌進 SKILL.md。這更貼近標準(標準 skill 就是單一 SKILL.md + 選配資源,沒有第二個「權威檔」)。zip 匯出必須包含恰好一個頂層資料夾,名稱等於 skill `name`。

- **export（backend `SkillExporter`）**:flow 匯出在 zip 內產生**單一頂層資料夾** `{name}/`,內含 `SKILL.md`(+ 選配 `{name}/scripts/` 等)。
  - zip 結構: `<name>.zip` → `{name}/SKILL.md`（+ 選配資源）。解壓自動得到正確命名的資料夾。
  - frontmatter:標準 `name` + `description`(YAML-safe)。
  - body:一段 fenced code block ` ```yaml … ``` `,內容為 flow 定義原文(= `skill.Definition`,**逐 byte 保留於圍籬內**)。可搭配一兩句人話說明,但權威定義就是那個 yaml 區塊。
- **import 解析（workflow）**:若 zip root 無 `SKILL.md`,且所有 entry 共用單一頂層資料夾,則剝除該前綴;前綴名**須等於** frontmatter `name`,不符則以錯誤碼 `folder_name_mismatch` 拒絕。剝除後流程同下:從 SKILL.md body 的**第一個 ` ```yaml ` 圍籬區塊**萃取定義原文(圍籬內 byte 原樣),以既有 YAML validator 驗證;name 須等於 route/frontmatter name。**不再要求或讀取 `skill.yaml`**;若舊 zip 仍含 `skill.yaml` 可相容接受(擇一路徑,並以測試固定;優先讀 SKILL.md 內嵌區塊)。**舊的根層 `SKILL.md` 結構相容接受**,無需資料夾層級。
- markdown body 允許內嵌任意語言 code block(```python 等);引擎只認 flow 的 ` ```yaml ` 區塊為定義,其餘視為說明文字。
- round-trip:export→import→export 後,萃取出的定義原文須與原 `skill.Definition` 逐 byte 相同(圍籬內容穩定)。`{name}/` 層級與內容完整往返。
- **安全護欄順序**:路徑驗證(`.`, `/`, drive, 空段, 正規化重複, symlink-like)與 `LIMITS` 檢查在前綴剝除**之後**執行;前綴本身亦跑同樣驗證,不可穿越。
- **行為變化**: `{name}/scripts/*.py` 現進 AST 掃描不安全模式(舊格式下該路徑因不以 `scripts/` 開頭而被當惰性 resource)。巢狀 `{name}/sub/SKILL.md` 僅作唯讀 resource;root `SKILL.md` 是權威定義。

agentic package 不受本節影響(其 SKILL.md 本就是 prose,無獨立 yaml)。

## 4. 檔案允許清單放寬（吃「任意其他檔案」但保安全）

parser 由「只收 SKILL.md + scripts/references/assets/」改為**接受任意額外檔案/資料夾**,但**安全護欄一律維持**:

- 仍拒：路徑穿越 `..`、絕對路徑、drive path、空路徑、正規化重複、symlink-like entry、超過檔案數/單檔/總解壓/壓縮比上限（`LIMITS` 單一來源）。
- 單一頂層資料夾剝除後,`SKILL.md` 須存在於 root；舊的根層直接放 `SKILL.md` 佈局亦相容。
- `scripts/*.py` 及 `{name}/scripts/*.py` 均過 AST scan、仍**只存不執行**（P3 之前不變）。
- 其餘額外檔案:**當唯讀 resource 儲存,永不執行/解讀**;resource 讀取工具的 path-normalize 白名單維持（讀取仍限 package 內、擋 `..`/絕對/`scripts/`）。
- round-trip 必須保留所有 entry 的 bytes（含新允許的額外檔案和資料夾層級）。

## 5. 資料遷移（DB）

backend 對 `skill` / `skill_revision` 既有 custom row:凡 `name` 含底線 → 以對照規則（`_`→`-`,並驗證結果符合新 name 規則）改名;同一 transaction 更新 name 與相關索引;`skill_revision` 保留歷史（新增一筆遷移 revision 或就地改名,擇一並在測試固定,零資料遺失）。內建 skill 不在 DB（在 workflow 檔案）,由改檔處理。

## 6. 相容性破壞（明示,不加 alias）

- invoke key 改變（`/api/skills/kb_query/invoke` → `/api/skills/kb-query/invoke`）。這是自包含單一部署,唯一 API 消費者是本專案前端,一併更新;**不建 legacy alias**（POC,YAGNI）。
- 所有服務內對舊名的引用（routing catalog、測試、docs、e2e 腳本、前端硬編碼）必須同批改。

## 7. 階段與順序

- **M1（並行實作）**
  - workflow：name 規則、rename 8 個 yaml（檔名+`name:`）、frontmatter 標準 parser（metadata/allowed-tools/license/compatibility、input_schema JSON 字串）、檔案允許清單放寬、custom loader、全部 pytest。
  - backend：DB 遷移（custom 底線→連字號）、`SkillExporter` 改標準 SKILL.md、name 規則若有硬編碼、xUnit。
  - platform：routing/catalog 對舊名引用、SkillRoutingAgent、xUnit。
  - frontend：`NAME_PATTERN`+`slugifySkillName`（連字號）、友善訊息、agentic 編輯器 frontmatter 形狀（metadata/allowed-tools）、硬編碼舊名、lint/build。
- **M2（阻抗迴圈）**：每服務 code-reviewer → 修 → 直到無問題;再 rebuild image 跑 e2e-verifier 全鏈（含 `skills-ref validate` 對匯出的 agentic package,若可用）。

## 8. 不變式（回歸保護）

- `/skills/{name}/invoke` 仍回 `{skill, output}`;`answer` 仍為 agentic 固定 output key。
- 節點/工具識別碼、input/output 鍵、Harness/audit/allowlist 行為不變。
- flow 編譯/執行語意不變（只有 skill 名稱字面改）。
- 所有既有測試改為新名後仍全綠;新增:name 規則 on/off-point、frontmatter 標準往返、檔案放寬往返、DB 遷移。
