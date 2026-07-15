# 規格書 — Skill 撰寫 × 系統設定

> 相關文件:[計劃書](01-plan.md)、[設計文稿](03-design.md)。
> 契約以現有服務實際回應為準,不憑空發明欄位(見 [repo AGENTS.md](../../AGENTS.md) 跨服務契約段)。

## 1. 名詞定義

| 名詞                      | 定義                                                                                                                                          |
| ------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| **Workflow(工作流)**      | 寫死在 Python code、用 decorator 註冊的 LangGraph 狀態機。按名字 invoke。現有五個:`summarize`/`triage`/`rag_qa`/`analyze_report`/`kb_query`。 |
| **Skill**                 | 使用者於 Workflows & Skills 管理頁撰寫的一份 YAML `definition`。name、description、role、input 與 flow 均由 node-first 引擎解析；存於 appdb。 |
| **Claude Skill 匯出格式** | 可攜檔案:`SKILL.md`(YAML frontmatter `name`+`description`,body 為執行說明)+ `skill.yaml`(權威 definition 原文)。                              |
| **執行(invoke)**          | Skill 經 node-first 引擎編譯後執行；其中的 `script` 步驟由該計畫的 sandbox 承載。                                                             |

Skill 是**一份 YAML definition、兩個消費端**:轉檔(可攜)與執行(runtime)。本文件不另定義第二套欄位或 runtime。

## 2. 功能需求(FR)

### FR-1 系統設定分頁
- 系統設定頁改為分頁:**一般設定**(現有 key/value 表原樣)、**工作流**(唯讀)。Skill 管理不在此頁重複呈現。
- 非 ADMIN 不可見系統設定(維持現狀;側欄過濾 + 後端角色把關為真正邊界)。

### FR-2 工作流唯讀區
- 列出所有 code 註冊工作流:`name`、`description`、`required_role`(角色徽章)。
- 純唯讀;資料來源沿用 `GET /api/workflows`。

### FR-3 Skill CRUD
- ADMIN 可在 Workflows & Skills 的 Skill 管理 Tab **建立 / 檢視 / 編輯 / 停用** YAML Skill。
- 請求 body 只有 `definition`;引擎從 YAML 解析 `name`、`description`、`required_role`、`input_schema` 與 `flow`。revision 與軟刪語意以 node-first 為準。
- `name` 需通過格式與租戶內唯一性驗證(見 FR-6);與 backend 保留路由衝突時拒絕。

### FR-4 匯出宣告式 Skill 格式
- 使用者可對任一 Skill 觸發匯出,產出資料夾結構(見第 5 節),下載為 zip。
- 轉檔為純字串組裝,不解析或執行 definition；`skill.yaml` 必須逐 byte 保留儲存的 YAML 原文。

### FR-5 Skill 執行(作廢)
本節已作廢:執行統一走 node-first-skill-engine 的 `POST /skills/{name}/invoke`,見 [該計畫規格](../node-first-skill-engine/02-spec.md) 第 7 節。

### FR-6 驗證規則
- definition 的語法、name、description、flow、script 與 Tool 規則均以 node-first `POST /skills/validate` 的結果為唯一事實來源。
- backend 不安裝第二個 YAML parser，也不另行實作 Script 黑名單。

## 3. 非功能需求(NFR)

### NFR-1 安全
- **沙箱、密鑰隔離與 Tool 存取**:以 node-first 規格第 5、6 節為準，本計畫不重複定義。
- **權限**:Skill CRUD 限 ADMIN(撰寫者可注入執行碼)。
- **稽核**:建立/修改/刪除 skill、每次 invoke 的 input/output 落 log。
- **配額**:每租戶執行頻率/資源上限,防 DoS。

### NFR-2 相容
- 不改動聊天 SSE、文件 202 流程、既有工作流行為。
- 錯誤一律 ApiError 形狀 `{timestamp, status, message, fieldErrors}`(camelCase)。

### NFR-3 前端品質
- 沿用設計 token 與共用元件(Toast/Skeleton/ErrorBoundary),不硬寫顏色;文字/背景對比 WCAG AA 4.5:1(明暗兩模式)。
- 每次 API 走 `apiFetch`;401 走全域登出。

## 4. API 合約

### 4.1 Skill CRUD(platform 代理 backend,ADMIN)

| 方法   | 路徑                        | Body          | 回應                   |
| ------ | --------------------------- | ------------- | ---------------------- |
| GET    | `/api/skills`               | —             | `SkillInfo[]`          |
| GET    | `/api/skills/{name}`        | —             | `Skill`                |
| POST   | `/api/skills`               | `SkillUpsert` | `Skill`(201)           |
| PUT    | `/api/skills/{name}`        | `SkillUpsert` | `Skill`                |
| DELETE | `/api/skills/{name}`        | —             | 204                    |
| GET    | `/api/skills/{name}/export` | —             | zip(`application/zip`) |

**欄位命名**:比照 documents/workflows,採 **snake_case**(`required_role`、`updated_at`)。

```
SkillInfo   { name, description, required_role, current_revision, enabled, updated_at }
Skill       { name, description, definition, required_role, current_revision, enabled, updated_at }
SkillUpsert { definition }
```

### 4.2 工作流 invoke(作廢)
(作廢)user skill 不再併入 `GET /api/workflows`;執行面向見 node-first-skill-engine 規格第 7 節,現有 `/workflows` 契約零改動。

### 4.3 錯誤
- 全部回 ApiError。CRUD 常見:400 驗證失敗(帶 `fieldErrors`)、403 非 ADMIN、404 name 不存在、409 name 衝突。

## 5. Claude Skill 格式規格

匯出資料夾:
```
<name>/
  SKILL.md
  skill.yaml       # = skill.definition 原文，逐 byte 相同
```

`SKILL.md`:
```markdown
---
name: <name>
description: <description>
---

## 定義
本 Skill 的權威宣告式流程位於 `skill.yaml`。

## 執行
以引擎 `POST /skills/<name>/invoke` 執行；輸入依 `input_schema`，輸出由 flow 決定。
```

- frontmatter 僅 `name`、`description`(Claude Skill 標準必要欄位)。
- body 不複製 YAML 內容；`skill.yaml` 是唯一的定義來源。
- 若 flow 包含 `script` 步驟，其原始碼只存在於 `skill.yaml`，不可被匯出器偽裝成獨立可執行 `main.py`。

## 6. 執行 I/O 合約(作廢)

本節已作廢,執行 I/O 契約由 node-first-skill-engine 的 script 步驟規格取代。

## 7. 驗收條件

- **P1**:系統設定呈現一般設定與工作流兩分頁;工作流分頁列出五個 code workflow 的 name/description/required_role。
- **P2**:ADMIN 在 Workflows & Skills 建立一個 YAML Skill 後,`GET /api/skills` 可見;`export` 下載出的 zip 內 `SKILL.md` frontmatter 與 body 正確、`skill.yaml` 等於原始 definition。
- **(作廢)** P3:一個回聲 Skill(stdin→stdout 原樣)經 `POST /api/workflows/{name}` invoke,回傳的 `output` 等於送入的 `input`;斷網 / 逾時 / 記憶體超限案例分別被沙箱正確攔截並回對應狀態碼。
