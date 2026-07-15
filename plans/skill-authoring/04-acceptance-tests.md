# 驗收準則與測試案例 — Skill 撰寫 × 系統設定

> 相關文件:[計劃書](01-plan.md)、[規格書](02-spec.md)、[設計文稿](03-design.md)。
> 範圍:僅涵蓋 **P1(系統設定分頁 + 工作流唯讀區)** 與 **P2(Skill CRUD + Claude Skill 匯出)**。
> **P3(Skill 執行)已隨計劃書 D3 作廢,執行相關驗收已移至 [node-first-skill-engine](../node-first-skill-engine/01-plan.md)。本文件不含任何 P3 測試案例。**
> 契約以 [02-spec.md](02-spec.md) 與根目錄 [AGENTS.md](../../AGENTS.md) 跨服務契約段為準,不發明欄位或行為。

## 0. 2026-07 對齊驗收矩陣(本節優先於下方歷史案例)

下方 AT1-01~AT2-27 是五欄位 Script / ConfigView 三分頁草稿的歷史案例，與現行 [01-plan.md](01-plan.md)、[02-spec.md](02-spec.md) 及 node-first 契約衝突，**不得再作為完成標準**。現行驗收如下:

| ID  | 驗收            | 預期                                                                                                                             |
| --- | --------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| R1  | ConfigView 分頁 | ADMIN 只看一般設定與工作流；工作流清單唯讀且有 Skeleton / alert 狀態                                                             |
| R2  | 管理入口        | USER 在 Workflows & Skills 只看執行；ADMIN 另看 Skill 管理與節點目錄；ConfigView 沒有第二個 Skill Tab                            |
| R3  | YAML CRUD       | `POST`/`PUT` body 只有 `{definition}`；workflow validate 是唯一 parser；revision、軟刪、tenant isolation 與 role 符合 node-first |
| R4  | 動態表單        | workflow 的 `input_schema` 從 workflow → platform → frontend 原樣保留；全字串 schema 生成欄位，否則 JSON textarea                |
| R5  | 宣告式 export   | zip 恰含 `SKILL.md` + `skill.yaml`；frontmatter 僅 name/description，YAML bytes 等於 definition，不 parse 或執行                 |
| R6  | invoke 錯誤分流 | fake custom loader 正常查無名稱 → 404；`BackendUnavailable` → 500；兩案獨立測試                                                  |
| R7  | 回歸            | workflow pytest、backend/platform xUnit、frontend lint/build 全綠；SSE、文件 202、既有 workflow 與 Config API 不回歸             |

R1~R7 的落點與細節以 [node-first 驗收文件](../node-first-skill-engine/04-acceptance-tests.md) 的 P4 / AT4-10~AT4-17 和本計畫 [02-spec.md](02-spec.md) 為準。

## 1. 歷史測試層級對照表(已由第 0 節取代)

| 層級                   | 執行位置                                   | 工具/慣例                                                                         | 涵蓋範圍                                                                                      |
| ---------------------- | ------------------------------------------ | --------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| Backend 單元/整合測試  | `backend/` xUnit                           | 手寫 fake(無 mocking library),對齊既有 54 個測試的慣例                            | `SkillController`、`SkillRepository`、`SkillExporter` 的邏輯與驗證規則                        |
| Platform 單元/整合測試 | `platform/` xUnit                          | 手寫 fake(`FakeBackendClient` 等),對齊既有 93 個測試的慣例                        | `SkillController`(platform 代理)、JWT 驗證、錯誤與狀態碼轉發                                  |
| 前端檢查               | `frontend/`                                | `npm run lint`、`npm run build`(必過);互動行為以手動驗收清單覆核(無既有 e2e 框架) | `SkillsTab`、`WorkflowsConfigTab`、`GeneralConfigTab`、表單驗證、Toast/Skeleton/ErrorBoundary |
| 端到端(e2e)            | `docker compose --profile full` 起全套服務 | `curl`(比照 `e2e-verifier` agent 慣例)                                            | 跨服務鏈路:瀏覽器 → platform → backend,含 identity headers 與角色把關                         |

## 2. 歷史 P1 驗收案例(已由第 0 節取代)

### AT1-01 三分頁呈現
- **前置**:以 ADMIN 帳號登入,已取得有效 JWT。
- **操作**:進入系統設定頁(`ConfigView`)。
- **預期**:頁面頂部顯示三個分頁籤,依序為「一般設定」「工作流」「Skill」;預設選取「一般設定」。

### AT1-02 一般設定分頁內容原樣
- **前置**:同 AT1-01,backend `/api/config` 已有既有 key/value 資料。
- **操作**:切至「一般設定」分頁。
- **預期**:呈現 Key / Value / 更新時間三欄表格,資料與切分頁前的 `ConfigView` 行為一致;無回歸(既有編輯/更新流程不變)。

### AT1-03 工作流分頁列出五個工作流
- **前置**:workflow 服務正常註冊 `summarize`/`triage`/`rag_qa`/`analyze_report`/`kb_query` 五個工作流。
- **操作**:切至「工作流」分頁。
- **預期**:
  - 呼叫 `GET /api/workflows`,清單顯示全部 5 筆。
  - 每筆顯示 `name`、`description`、`required_role` 角色徽章(USER/ADMIN 樣式沿用既有工作流頁)。
  - 清單順序或內容與既有 `/api/workflows` 呼叫端(例:工作流頁面)一致,不因新分頁而改變後端回應。

### AT1-04 工作流分頁純唯讀
- **前置**:同 AT1-03。
- **操作**:檢視「工作流」分頁 DOM。
- **預期**:不存在編輯、刪除、新增等操作按鈕;僅呈現清單。

### AT1-05 非 ADMIN 看不到系統設定入口
- **前置**:以 USER 角色帳號登入。
- **操作**:檢視側欄導覽。
- **預期**:側欄不出現「系統設定」連結(維持現狀行為)。

### AT1-06 非 ADMIN 直接呼叫仍被後端擋下
- **前置**:USER 角色 JWT。
- **操作**:直接以 USER token 呼叫 `GET /api/config`(或系統設定相關端點)。
- **預期**:回應 403,ApiError 形狀 `{timestamp, status:403, message, fieldErrors}`;證明側欄隱藏只是 UX,後端角色把關才是真正邊界。

### AT1-07 首次載入 Skeleton
- **前置**:模擬 `GET /api/workflows` 回應延遲。
- **操作**:切至「工作流」分頁瞬間。
- **預期**:資料返回前顯示 Skeleton 佔位元件(重用既有 Skeleton 元件),不誤顯示「無資料」。

### AT1-08 錯誤 alert
- **前置**:模擬 `GET /api/workflows` 回 5xx 或網路失敗。
- **操作**:切至「工作流」分頁。
- **預期**:顯示 `role="alert"` 的錯誤區塊,內容為 ApiError 的 `message`;不 crash 整頁(ErrorBoundary 兜底)。

P1 案例共 **8** 個(AT1-01 ~ AT1-08)。

## 3. 歷史 P2 驗收案例(已由第 0 節取代)

### 3.1 Skill CRUD API(backend,xUnit + e2e curl)

#### AT2-01 建立 Skill 成功(201)
- **操作**:`POST /api/skills`,headers 含 `X-Internal-Token: <INTERNAL_API_TOKEN>`、`X-Tenant-Id`、`X-User-Id`、`X-User-Role: ADMIN`;body:
  ```json
  { "name": "echo_skill", "description": "回聲測試", "flow": "輸入即輸出", "logic": "無邏輯", "script": "print('hi')" }
  ```
- **預期**:201,回應 `Skill`(snake_case):`{ name, description, flow, logic, script, required_role: "USER", enabled: true, updated_at }`。

#### AT2-02 清單 GET /api/skills
- **前置**:已建立 AT2-01 的 skill。
- **操作**:`GET /api/skills`(帶必要 headers)。
- **預期**:200,回傳陣列含 `SkillInfo { name, description, required_role, enabled, updated_at }`(不含 flow/logic/script)。

#### AT2-03 單筆 GET /api/skills/{name}
- **操作**:`GET /api/skills/echo_skill`。
- **預期**:200,回傳完整 `Skill`(含 flow/logic/script)。

#### AT2-04 更新 Skill
- **操作**:`PUT /api/skills/echo_skill`,body 修改 `description`。
- **預期**:200,回傳更新後 `Skill`,`updated_at` 有變動。

#### AT2-05 刪除 Skill(204)
- **操作**:`DELETE /api/skills/echo_skill`(ADMIN)。
- **預期**:204 無內容;後續 `GET /api/skills/echo_skill` 回 404。

#### AT2-06 404 name 不存在
- **操作**:`GET /api/skills/not_exist`。
- **預期**:404,ApiError `{timestamp, status:404, message, fieldErrors:null 或 {}}`。

#### AT2-07 409 同名衝突
- **前置**:已存在 `echo_skill`。
- **操作**:`POST /api/skills` 再建一個 `name: "echo_skill"`。
- **預期**:409,ApiError message 說明名稱已存在。

#### AT2-08 403 非 ADMIN
- **操作**:以 `X-User-Role: USER` 呼叫 `POST /api/skills`。
- **預期**:403 ApiError;GET 系列端點若規格未限制角色則放行(依 FR-3,CRUD 限 ADMIN;讀取比照設計為同一控制器同角色要求,故 GET 亦應驗證,測試需同時涵蓋 GET/POST/PUT/DELETE 四動詞的 403 案例)。

#### AT2-09 400 驗證失敗 — name 正則
- **操作**:`POST /api/skills`,`name: "Echo-1"`(大寫+連字號,不符 `^[a-z][a-z0-9_]{2,63}$`)。
- **預期**:400,ApiError `fieldErrors: { "name": "<格式錯誤訊息>" }`。

#### AT2-10 400 驗證失敗 — description 空
- **操作**:`description: ""`。
- **預期**:400,`fieldErrors.description` 非空提示。

#### AT2-11 400 驗證失敗 — description 超長
- **操作**:`description` 長度 501 字元。
- **預期**:400,`fieldErrors.description` 提示上限 500。

#### AT2-12 400 驗證失敗 — script 空
- **操作**:`script: ""`。
- **預期**:400,`fieldErrors.script` 非空提示。

#### AT2-13 與 code 註冊工作流同名拒絕
- **操作**:`POST /api/skills`,`name: "rag_qa"`(既有工作流名)。
- **預期**:400 或 409(依實作,ApiError 需明確標註衝突原因),不得建立成功;不得覆蓋既有工作流路由。

#### AT2-14 租戶隔離
- **前置**:租戶 A 建立 `skill_a`;請求改用租戶 B 的 `X-Tenant-Id`。
- **操作**:租戶 B 呼叫 `GET /api/skills`。
- **預期**:清單不含 `skill_a`;`GET /api/skills/skill_a` 以租戶 B 身分呼叫回 404(不洩漏存在性)。

### 3.2 匯出(Claude Skill 格式)

#### AT2-15 匯出 zip 結構
- **前置**:已建立 skill,`flow="步驟一\n步驟二"`、`logic="規則A"`、`script="print(1)"`。
- **操作**:`GET /api/skills/{name}/export`。
- **預期**:200,`Content-Type: application/zip`,`Content-Disposition` 檔名為 `<name>.zip`;解壓後含 `SKILL.md` 與 `scripts/main.py` 兩個檔案,無多餘檔案。

#### AT2-16 SKILL.md frontmatter
- **操作**:解析匯出的 `SKILL.md` YAML frontmatter。
- **預期**:僅含 `name`、`description` 兩個鍵,值與 skill 資料一致;無 `flow`/`logic`/`script`/`required_role` 等額外欄位混入 frontmatter。

#### AT2-17 SKILL.md body 結構
- **操作**:檢視 frontmatter 之後的 body。
- **預期**:依序含 `## 流程` + `flow` 原文、`## 商業邏輯` + `logic` 原文、`## Script` 段落(固定說明文字:程式碼位於 `scripts/main.py`,以 `python scripts/main.py` 執行,自 stdin 讀 JSON input、向 stdout 寫 JSON output)。

#### AT2-18 main.py 逐 byte 相同
- **操作**:比對 `scripts/main.py` 內容與資料庫中 `script` 欄位原文。
- **預期**:完全相同(逐 byte),不做任何跳脫、格式化或包裝。

#### AT2-19 匯出不執行程式碼
- **操作**:以含惡意/無效 Python 語法的 `script`(例:語法錯誤字串)建立 skill 並匯出。
- **預期**:匯出仍成功回 200 zip(純字串組裝,不解析/不執行 script),`scripts/main.py` 原樣寫入語法錯誤內容。

### 3.3 platform 代理(:8080)

#### AT2-20 JWT 缺失 401
- **操作**:不帶 `Authorization` header 呼叫 platform `POST /api/skills`。
- **預期**:401,ApiError 形狀正確;請求不應到達 backend。

#### AT2-21 backend 錯誤原樣轉發
- **操作**:backend 對某請求回 409(如 AT2-07 情境),經 platform 代理呼叫。
- **預期**:platform 回應狀態碼與 ApiError body 與 backend 一致(僅代理,不改寫)。

#### AT2-22 platform 附帶正確 identity headers
- **前置**:platform 端 fake backend client(xUnit)。
- **操作**:platform 收到合法 JWT 後呼叫 backend `/api/skills`。
- **預期**:轉發請求含 `X-Internal-Token`、`X-Tenant-Id`、`X-User-Id`、`X-User-Role`,值取自已驗證的 JWT claims。

### 3.4 前端(手動驗收 + lint/build)

#### AT2-23 表單即時驗證阻擋儲存
- **操作**:在 `SkillEditor` 輸入不合法 `name`(如含大寫)或空 `description`。
- **預期**:儲存按鈕呈 disabled 狀態或點擊後即時顯示欄位錯誤,不送出 API 請求。

#### AT2-24 儲存成功 Toast
- **操作**:填妥合法欄位並儲存。
- **預期**:`POST`/`PUT` 成功後顯示 Toast「已儲存」(或等義文案),清單即時反映新/更新的 skill。

#### AT2-25 刪除二次確認
- **操作**:點擊刪除按鈕。
- **預期**:出現確認對話(重用既有確認樣式);取消則不呼叫 API;確認後呼叫 `DELETE`,成功顯示 Toast 且清單移除該筆。

#### AT2-26 匯出觸發下載
- **操作**:點擊「匯出」按鈕。
- **預期**:瀏覽器觸發檔案下載,檔名 `<name>.zip`(對應 `exportSkill` 以 blob 觸發下載的實作)。

#### AT2-27 401 全域登出
- **前置**:token 已過期或失效。
- **操作**:於 SkillsTab 觸發任一 API 呼叫(如載入清單)。
- **預期**:`apiFetch` 攔截 401 → 清除 localStorage 中 session 與聊天相關 key → 導回登入頁(既有全域行為,不因新增 Skill 端點而有例外)。

P2 案例共 **27** 個(CRUD 14 個:AT2-01 ~ AT2-14;匯出 5 個:AT2-15 ~ AT2-19;platform 代理 3 個:AT2-20 ~ AT2-22;前端 5 個:AT2-23 ~ AT2-27)。

## 4. 歷史回歸檢查清單(已由第 0 節取代)

新增 Skill 相關端點與分頁後,以下既有行為必須維持不變(逐項以 e2e curl 或既有 xUnit 套件覆核):

| 項目                         | 檢查方式                                                                      | 預期                                                                                  |
| ---------------------------- | ----------------------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| 聊天 SSE(`/api/chat/stream`) | curl 手動觸發 SSE,檢查 `data:<value>`(無空格)格式                             | 格式與逐字元行為不變                                                                  |
| 文件非同步流程               | `POST /api/documents` → 202 `{id,title,status:"processing"}` → 輪詢至 `ready` | 流程與狀態機不變                                                                      |
| 既有 `/api/workflows`        | `GET /api/workflows`                                                          | 僅回 5 個 code 註冊工作流,**不合併** user skills(P3 作廢,FR-5/4.2 已明訂不改動此端點) |
| `/api/config` 既有行為       | `GET`/`PUT /api/config`                                                       | Key/Value CRUD 與角色要求(PUT 需 ADMIN)不變                                           |
| ApiError 全域形狀            | 任一 4xx/5xx 回應                                                             | `{timestamp, status, message, fieldErrors}` camelCase,新端點與既有端點一致            |
| backend 信任邊界             | 缺 `X-Internal-Token` 呼叫 backend `/api/skills`                              | 與其餘 backend 端點一致地被拒絕(非 `/health`)                                         |

## 5. 歷史案例總計(已由第 0 節取代)

- **P1:8 個**(AT1-01 ~ AT1-08)
- **P2:27 個**(AT2-01 ~ AT2-27)
- **合計:35 個驗收案例**,均不涉及 P3(Skill 執行/沙箱),P3 驗收見 node-first-skill-engine 計畫。
