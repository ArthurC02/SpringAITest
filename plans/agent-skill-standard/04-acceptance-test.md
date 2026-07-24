# 驗收測試 — Agent Skill 標準格式支援

> 狀態：**P0–P2 已交付；P3 延後。** 本檔保留為歷史驗收規格；現況以測試與程式碼為準。

## 1. 原則與環境

- Backend/Platform 使用 xUnit 與既有手寫 fake；Workflow 使用 pytest。新增測試不得引入 mocking library。
- archive 結構、tenant isolation、revision 與 full-stack proxy 必須有真 HTTP/DB 測試；純 parser fake 不足以驗證內部 token 邊界。
- 測試至少使用 `tenant-a` 與 `tenant-b`，各有 ADMIN/USER。所有跨 tenant 測試驗內容或 HTTP status，不只驗 object nullity。
- package fixture 至少包含：合法無 scripts agentic skill、合法 resource skill、unknown tool、unsafe script、path traversal、過大 archive、flow export fixture。
- P0–P2 不執行 bundle scripts。任何測試若觀測到 script 被執行，即為 blocker，不是預期能力。

## 2. P0 — package、儲存與內部契約

| ID           | Given                                                                  | When                                                          | Then                                                                                                        | 層級 / 建議位置                              |
| ------------ | ---------------------------------------------------------------------- | ------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- | -------------------------------------------- |
| `AST-P0-001` | tenant-a ADMIN 與合法 agentic zip                                      | import `/api/skills/sales_helper/import`                      | 2xx；metadata、`kind=agentic`、canonical definition 與 package SHA 寫入同一 revision                        | Backend API + PostgreSQL xUnit               |
| `AST-P0-002` | 合法 agentic zip 已 import                                             | export 同一 name                                              | entries 與每個 entry bytes 相同；zip metadata 不要求相同                                                    | Backend exporter xUnit                       |
| `AST-P0-003` | flow skill export fixture                                              | export → import → export                                      | `skill.yaml` bytes 完全相同；既有 flow definition 行為不變                                                  | Backend xUnit                                |
| `AST-P0-004` | 含 `../x`、絕對路徑、drive path 或正規化重複 entry 的 zip              | import                                                        | 422/validation error；skill/revision/package 均無改變                                                       | Workflow parser pytest + Backend integration |
| `AST-P0-005` | 缺 `SKILL.md`、無 frontmatter、name 不符、非 `agentic` kind 的 fixture | import                                                        | 拒絕，錯誤可定位到 package/frontmatter；零副作用                                                            | Workflow pytest + Backend xUnit              |
| `AST-P0-006` | frontmatter 引用不存在 tool                                            | import                                                        | `unknown_tool`；無資料寫入                                                                                  | Workflow pytest                              |
| `AST-P0-007` | package 含 AST 不允許的 `scripts/x.py`                                 | import                                                        | `forbidden_script`；不得儲存或執行 script                                                                   | Workflow pytest + Backend xUnit              |
| `AST-P0-008` | 檔案數、單檔大小、解壓大小及壓縮比在上限與剛超限的 fixture             | import                                                        | on-point 接受；off-point 拒絕；限制常數由 parser 單一來源取得                                               | Workflow parameterized pytest                |
| `AST-P0-009` | 已儲存 tenant-a package                                                | internal package GET：正確 token/tenant-a、錯 token、tenant-b | 依序為 zip、401、404；公開 GET skill JSON 不含 package/content                                              | Backend HTTP integration                     |
| `AST-P0-010` | tenant-a USER 與 ADMIN，且 request body 不合法                         | USER import                                                   | 403 先於 package/body validation；ADMIN 才進 validator                                                      | Backend controller xUnit                     |
| `AST-P0-011` | Backend 收到 import                                                    | 觀察 internal Workflow validate request                       | multipart 且含 expected name、internal token、tenant/user/role headers；不得同時存在 base64 validation path | Backend→Workflow contract test               |
| `AST-P0-012` | Workflow validate 回 invalid、timeout 或 5xx                           | import                                                        | invalid 回受控 validation error；下游不可達回 502；兩者都不寫 revision                                      | Backend xUnit                                |
| `AST-P0-013` | 已匯入 agentic skill，另有 flow definition payload                     | 對既有 `PUT /api/skills/{name}` 送 definition-only update     | 固定 409/422；definition、metadata、package、revision、definition/package hash 全部不變                     | Backend API + PostgreSQL xUnit               |

## 3. P1 — agentic compile 與 runner

| ID           | Given                                                                     | When                                    | Then                                                                                       | 層級 / 建議位置                            |
| ------------ | ------------------------------------------------------------------------- | --------------------------------------- | ------------------------------------------------------------------------------------------ | ------------------------------------------ |
| `AST-P1-001` | 同名 flow 與 agentic fixtures                                             | compile                                 | flow 走既有 graph；agentic graph 含 runner 且終端必有 audit                                | Workflow compiler pytest                   |
| `AST-P1-002` | 合法 agentic package、deterministic LiteLLM fake                          | explicit invoke                         | 回 `{skill, output}`，output 含固定 `answer` key 與既有 public fields                      | Workflow invoke pytest + Platform contract |
| `AST-P1-003` | runner 回傳 forged tenant/user/role、未宣告鍵與 `answer`                  | invoke                                  | immutable identity 不變、未宣告鍵被剝除、`answer` 保留                                     | Workflow Harness integration               |
| `AST-P1-004` | runner model 嘗試呼叫未列在 `uses_tools` 的 tool                          | invoke                                  | tool function 的 call counter 為 0；回受控拒絕/失敗；audit 仍可見                          | Workflow pytest                            |
| `AST-P1-005` | allowed tool fixture                                                      | invoke                                  | adapter 呼叫 registry 一次，收到 server-injected tenant/user/role；不得直持 internal token | Workflow pytest                            |
| `AST-P1-006` | resource package 含 `references/a.md`、`assets/a.txt` 與非法 path request | model/read-resource tool 讀取           | 只可唯讀取得允許 entry；`../`、絕對與 `scripts/` path 被拒絕；不洩漏 host 檔案             | Workflow pytest                            |
| `AST-P1-007` | 模型連續 tool calls 剛在 step limit 與多一次                              | invoke                                  | on-point 完成；off-point 受控終止，不無限迴圈，audit 仍執行                                | Workflow integration                       |
| `AST-P1-008` | model 或 tool 故意延遲                                                    | invoke 至 timeout                       | 受控 timeout，沒有 background tool 繼續執行，trace/audit 記錄失敗                          | Workflow async integration                 |
| `AST-P1-009` | package reader 對 tenant-a/b 各回不同 instruction                         | 交錯 invoke 同 name                     | 每次用自己的 tenant package；不得共用/污染內容                                             | Workflow tenant/cache pytest               |
| `AST-P1-010` | 含可通過 scan 的 `scripts/x.py` package                                   | invoke                                  | script call counter 為 0，明確回 disabled 或忽略；其他 agentic 行為正常                    | Workflow pytest                            |
| `AST-P1-011` | catalog 有可路由與多參 agentic entries                                    | chat 路由與 explicit invoke             | 單必填字串 entry 可沿用既有路由；多參 entry 不被路由但 explicit invoke 成功                | Platform service/web xUnit                 |
| `AST-P1-012` | agentic skill 經聊天路由                                                  | 觀察 AG-UI/chat events                  | 不產生 server skill 的 `TOOL_CALL_*`；最終內容由 `answer` 正確萃取                         | Platform Web xUnit                         |
| `AST-P1-013` | catalog/validate response 含 kind                                         | 舊 consumer deserialise、Platform proxy | kind 被透傳；既有欄位和 `/invoke` shape 不變                                               | Platform contract xUnit                    |
| `AST-P1-014` | 全部既有 flow fixtures                                                    | 跑既有 workflow suite                   | flow compiler、script steps、tools 與 audit 行為無回歸                                     | Existing workflow pytest                   |

## 4. P2 — 前端 package authoring

| ID           | Given                            | When                                   | Then                                                                                | 層級 / 建議位置                 |
| ------------ | -------------------------------- | -------------------------------------- | ----------------------------------------------------------------------------------- | ------------------------------- |
| `AST-P2-001` | ADMIN 已登入                     | 開啟 Skills                            | 可上傳/下載 agentic package；flow 仍進既有 editor                                   | Frontend browser e2e            |
| `AST-P2-002` | USER 已登入                      | 檢視 UI、直接呼叫 import               | UI 不提供管理動作；API 回 403                                                       | Browser + Backend authorization |
| `AST-P2-003` | 合法 package                     | 編輯 description/body/resource 後儲存  | client 重打 zip 後走 import；server 回傳 validation error 時保留草稿並顯示 ApiError | Browser/network e2e             |
| `AST-P2-004` | flow skill 與 agentic skill 各一 | 切換 editor、試跑、開 revision history | flow 走既有 create/update/trial/revision；agentic save 走 import；兩者不混用 schema | Browser e2e                     |
| `AST-P2-005` | export endpoint 回 401           | 點下載                                 | 與既有 `apiFetchBlob` 一致地清 session 並回登入；不得留下無效下載連結               | Frontend integration/e2e        |

## 5. P3 — production script isolation

| ID           | Given                                                    | When                       | Then                                                             | 層級 / 建議位置       |
| ------------ | -------------------------------------------------------- | -------------------------- | ---------------------------------------------------------------- | --------------------- |
| `AST-P3-001` | Linux isolation runner 與 script 要求讀環境變數          | 執行 bundle script         | 取不到 internal token、JWT、DB password 與 host secrets          | Linux integration     |
| `AST-P3-002` | script 嘗試 DNS/HTTP socket                              | 執行                       | 預設拒絕出網；合法資料需求必須改走受 allowlist 約束的 host tool  | Linux integration     |
| `AST-P3-003` | CPU loop、記憶體配置、超大 stdout、wall timeout fixtures | 執行                       | 各自被限制、可取消、host 保持可服務且留下受控 audit error        | Linux integration     |
| `AST-P3-004` | script 嘗試讀 package 外路徑或寫入資源                   | 執行                       | 只見唯讀允許資源；host filesystem 未被讀寫                       | Linux integration     |
| `AST-P3-005` | tenant-a/b script packages 同名                          | 交錯執行                   | input/resources/output/audit 不跨 tenant 混用                    | Linux integration     |
| `AST-P3-006` | Windows/dev 環境                                         | 嘗試啟用外部 bundle script | 明確拒絕或維持 disabled；不得宣稱提供 Linux production isolation | Environment gate test |

## 6. 跨服務與 release gate

| ID          | Given                                                  | When                                            | Then                                                                                  |
| ----------- | ------------------------------------------------------ | ----------------------------------------------- | ------------------------------------------------------------------------------------- |
| `AST-X-001` | full stack、tenant-a ADMIN、無 scripts agentic package | upload → catalog → explicit invoke → export     | import、kind 透傳、LiteLLM runner、Harness audit、output shape 與 round-trip 全鏈成功 |
| `AST-X-002` | full stack、可路由單字串 agentic skill                 | ChatView 與 AG-UI 各執行一次                    | 同樣的 skill/輸入被執行；既有 SSE 格式不變：chat `data:`、AG-UI `data: `              |
| `AST-X-003` | tenant-a/b 各有同名 agentic skill                      | A upload/invoke 後 B read/invoke                | B 不可讀 A package/資源、不得得到 A instruction 或 answer                             |
| `AST-X-004` | 既有 flow/custom fixtures                              | 跑 workflow、backend、platform、frontend checks | 全部既有 suites 綠；既有 flow API/export/invoke 行為未變                              |

### Phase exit gate

- **P0 exit：** `AST-P0-001`～`013` 綠；任何 archive bypass、跨 tenant package read、agentic definition 漂移或失敗寫 revision 都是 blocker。
- **P1 exit：** `AST-P1-001`～`014` 加 `AST-X-001`～`004` 綠；任何 allowlist bypass、無 audit、無界 loop、script execution 或公開 package 洩漏都是 blocker。
- **P2 exit：** `AST-P2-001`～`005`、frontend lint/build 與 P0/P1 regression 綠。
- **P3 exit：** `AST-P3-001`～`006` 在 Linux integration 綠後，才可允許 production bundle script execution。
