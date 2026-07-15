# 驗收準則與測試案例 — Node-first 架構翻轉 × Skill 流程引擎

> 相關文件:[計劃書](01-plan.md)、[規格書](02-spec.md)、[設計文稿](03-design.md)。
> 本文件把 [01-plan.md 第 5 節](01-plan.md)的每條驗收標準拆成可執行的測試案例。欄位、錯誤碼、API 形狀一律以 [02-spec.md](02-spec.md) 為準,程式落點以 [03-design.md](03-design.md) 為準;本文件不發明任何未在前三份文件出現的欄位或行為。案例分 Phase 陳列,對應 [01-plan.md 第 5 節](01-plan.md)表格的四個 Phase。

## 1. 測試層級對照表

| 層級 | 範圍 | 位置(建議) | 執行方式 |
|---|---|---|---|
| workflow 服務 pytest — 元件單元測試 | `engine/node_registry.py`、`engine/harness.py`、`engine/skill.py`(靜態驗證)、`engine/compiler.py`、`engine/expressions.py`、`engine/script_runner.py`、`engine/tool_registry.py` | `workflow/tests/test_engine_*.py`(比照現有 `test_registry.py`/`test_retrieve.py` 命名慣例) | `uv run pytest` |
| workflow 服務 pytest — parity e2e | `skills/kb_query.yaml` 編譯圖 vs 手寫 `kbquery/graph.py` | `workflow/tests/test_skill_kbquery_parity_e2e.py`(對照現有 `test_kbquery_e2e.py` 7 案例逐一複製) | `uv run pytest` |
| workflow 服務 pytest — 沙箱逃逸 | `engine/script_runner.py` 攻擊樣本集 | `workflow/tests/test_script_runner_sandbox.py` | `uv run pytest`(納入 CI) |
| backend xUnit | `Features/Skills/`(CRUD、驗證轉發、revision、軟刪、租戶隔離、角色) | `backend/Backend.Tests/Features/Skills/SkillsControllerTests.cs` 等(比照現有 feature folder 測試慣例) | `dotnet test` |
| platform xUnit | `/api/skills*` 代理、identity headers 轉發、401 行為 | `platform/Platform.Tests/SkillsProxyTests.cs` | `dotnet test` |
| 前端 lint + build | Skills 視圖(Tab 1/2/3)、角色可見性、驗證 debounce | `frontend/` | `npm run lint && npm run build`(元件測試如有既有慣例則比照補充) |
| e2e curl | 跨服務契約(建立 → invoke → trace → 稽核) | 手動或 CI 腳本 | `curl`(見各案例) |

## 2. Phase 1 — Node Registry + Harness

前置(全 Phase 共用):`workflow/` 已加入 `engine/node_registry.py`、`engine/harness.py`,kb_query 10 節點 + `retrieve` 已補 `@node(...)` 宣告(見 [02-spec.md §2.2](02-spec.md))。

**AT1-01 `@node` 註冊與 NodeSpec 契約查詢**
- 前置:匯入 `app.nodes.kbquery.evidence_verification`。
- 操作:`node_registry.get("evidence_verification", version="1.0")`。
- 預期:回傳 `NodeSpec`,`reads`/`writes`/`requires_tools` 與 [02-spec.md §2.1](02-spec.md) 範例逐鍵相等;`name=="evidence_verification"`、`version=="1.0"`。

**AT1-02 重複 `name@version` 啟動 raise**
- 前置:在測試模組內對同一 `name="evidence_verification", version="1.0"` 二度呼叫 `@node(...)` 裝飾另一 factory。
- 操作:import 該測試模組(觸發裝飾器執行)。
- 預期:拋出 `ValueError`(對齊現有 `test_registry.py::test_duplicate_name_raises_value_error` 的斷言風格),訊息含 `name` 與 `version`。

**AT1-03 `GET /nodes` 回應形狀**
- 操作:`curl -H "X-Internal-Token: $TOKEN" -H "X-Tenant-Id: t1" -H "X-User-Id: u1" -H "X-User-Role: USER" http://127.0.0.1:8001/nodes`
- 預期:`200`,body 為陣列,每筆含 `name`、`version`、`description`、`reads`(list[str])、`writes`(list[str])、`requires_tools`(list[str]);至少含 11 筆(kb_query 10 節點 + `retrieve`)。

**AT1-04 Harness 剝除未宣告的 writes 鍵**
- 前置:建一個測試節點,`writes=["allowed_key"]`,但函式本體額外寫入 `state["sneaky_key"] = "x"`。
- 操作:經 Harness 執行該節點。
- 預期:回傳的 state 含 `allowed_key`,**不含** `sneaky_key`。

**AT1-05 保留鍵不可被覆寫**
- 前置:輸入 state 已有 `query_id`/`original_query`/`query_timestamp`。
- 操作:節點函式嘗試寫入這三鍵的新值,經 Harness 執行。
- 預期:三鍵維持 Harness 執行前的原值(對齊現有 `runtime.traced()` 的 `IMMUTABLE_KEYS` 行為,見 [03-design.md §2](03-design.md) 遷移對照)。

**AT1-06 trace/fatal 短路行為不變**
- 前置:節點拋出視為 fatal 的例外(比照現有 `test_kbquery_e2e.py::test_e2e_blank_query_fatal_short_circuit` 的觸發條件)。
- 操作:經 Harness 執行的圖跑到該節點。
- 預期:後續節點不執行,`trace` 含該節點的 `status="error"` entry,圖回傳的錯誤結構與現行 `traced()` 行為一致。

**AT1-07(回歸底線)現有 workflow 測試全數不改一行全綠**
- 操作:`uv run pytest`(不修改 `workflow/tests/` 任何既有檔案)。
- 預期:`test_api.py`、`test_kbquery_e2e.py`(7 案例)、`test_kbquery_nodes.py`、`test_registry.py`、`test_retrieve.py`、`test_triage.py` 全部 PASS,無需改動既有斷言或 import 路徑。此案例是 P1 唯一的「不可退讓」驗收項(對應 [01-plan.md 第 5 節](01-plan.md) P1 驗收欄)。

## 3. Phase 2 — Skill 引擎

### 3.1 靜態驗證錯誤碼(每碼至少一案,對照 [02-spec.md §3.4](02-spec.md))

**AT2-01 `unknown_node`**
- 操作:對 `flow: [{node: no_such_node}]` 呼叫 `POST /skills/validate`。
- 預期:`{"valid": false, "errors": [{"code": "unknown_node", ...}]}`。

**AT2-02 `unbounded_loop` — 缺 `max_iterations`**
- 操作:`flow: [{loop: {until: "state.x == 1", body: [{node: query_intake}]}}]`(無 `max_iterations`)驗證。
- 預期:`errors` 含 `code: "unbounded_loop"`。

**AT2-03 `unbounded_loop` — 超出 1~10**
- 操作:同上,`max_iterations: 11`;另補一案 `max_iterations: 0`。
- 預期:兩案皆回 `unbounded_loop`。

**AT2-04 `invalid_expression`**
- 操作:`until: "os.system('echo x')"` 驗證。
- 預期:`errors` 含 `code: "invalid_expression"`(函式呼叫與屬性鏈皆屬白名單外語法,見 [02-spec.md §3.3](02-spec.md))。

**AT2-05 `invalid_flow` — 空 flow**
- 操作:`flow: []` 驗證。
- 預期:`errors` 含 `code: "invalid_flow"`。

**AT2-06 `invalid_flow` — 未知步驟型別**
- 操作:`flow: [{parallel: [...]}]`(型別不在 6 種之內)驗證。
- 預期:`errors` 含 `code: "invalid_flow"`。

**AT2-07 `invalid_flow` — YAML 解析失敗**
- 操作:送出語法錯誤的 YAML 原文(如未閉合的引號)給 `POST /skills/validate`。
- 預期:`422` 或 `{valid:false}`,`errors` 含 `code: "invalid_flow"`,不拋未捕捉例外。

**AT2-08 `dataflow_error`(警告級)**
- 操作:`flow: [{node: answer_composer}]`(直接跑 `answer_composer`,其 `reads` 的鍵無前置 `writes` 供給,也不在 `input_schema`/保留鍵)驗證。
- 預期:`errors` 含 `code: "dataflow_error"`;同時 `valid` 仍可為 `true`(警告級不阻擋存檔,對照 [02-spec.md §3.4](02-spec.md)表格「警告級」註記)——測試需同時斷言「有這條 warning」與「不因這條而拒存」。

### 3.2 條件式求值器(`engine/expressions.py`)

**AT2-09 比較運算**:`state.confidence < 0.7` 在 `state={"confidence": 0.5}` 求值為 `True`;`state={"confidence": 0.9}` 求值為 `False`。

**AT2-10 `and`/`or`/`not`**:`state.a and not state.b` 在 `{"a": True, "b": False}` 為 `True`;`state.a or state.b` 在 `{"a": False, "b": False}` 為 `False`。

**AT2-11 `in`**:`state.verification_result in ["PASS", "RETRY"]` 在 `{"verification_result": "PASS"}` 為 `True`,在 `{"verification_result": "FAIL"}` 為 `False`。

**AT2-12 不存在鍵求值為 `None`**:`state.no_such_key == None` 在空 `state={}` 為 `True`(對照 [02-spec.md §3.3](02-spec.md)「不拋錯,可與 None 比較」)。

**AT2-13 禁止函式呼叫**:`len(state.x)` 求值時拋出驗證期例外(存檔時已被 AT2-04 擋下;此案額外驗證執行期若繞過存檔驗證也一樣拒絕求值,不得靜默通過)。

**AT2-14 禁止屬性鏈**:`state.x.y`(超過 `state.` 一層)求值拋出例外。

### 3.3 Compiler(`engine/compiler.py`)

**AT2-15 sequence 順序**:`flow: [{node: query_intake}, {node: query_rewrite}]` 編譯後執行,`trace` 中兩節點出現順序為 `query_intake` 先於 `query_rewrite`。

**AT2-16 branch then 分支**:`when` 求值為 `True` 時,只有 `then:` 清單內節點執行,`else:` 節點不出現在 `trace`。

**AT2-17 branch else 分支**:`when` 求值為 `False` 且有 `else:` 清單時,只有 `else:` 節點執行。

**AT2-18 loop 達上限強制離開**:`until` 恆為 `False`(如 `"state.x == 'never'"`)、`max_iterations: 2`,執行後 `body` 恰執行 2 輪即離開迴圈(不因 `until` 恆假而無限跑),對應 [02-spec.md §3.2](02-spec.md) loop 語意「達上限強制離開」。

**AT2-19 loop until 提前離開**:`until` 在第 1 輪後即為 `True`、`max_iterations: 5`,執行後 `body` 只跑 1 輪。

**AT2-20 `__loop` 計數鍵 Skill 不可讀寫**:`flow` 內任一步驟嘗試以 `state.__loop_0_count` 作為 `when`/`until` 條件式的一部分(或 script 嘗試寫入 `__loop_0_count`)存檔時被 AT2-04/P3 script 驗證擋下(`__` 前綴鍵非 Skill 可存取範圍,對照 [02-spec.md §4](02-spec.md))。

### 3.4 kb_query.yaml Parity(引擎回歸基準)

**AT2-21 ~ AT2-27 七案完全對照現有 `test_kbquery_e2e.py`**
- 前置:`skills/kb_query.yaml`(見 [03-design.md §2](03-design.md))已編譯出 `CompiledStateGraph`;測試 fixture 沿用現有 `kbquery_fakes.py` 的假依賴,僅將「手寫圖」換成「skill 編譯圖」。
- 逐案(命名對照,行為斷言與現有測試逐一複製,不得放寬):
  1. `test_e2e_single_value_lookup_success`(skill 版)
  2. `test_e2e_table_cell_lookup_success`(skill 版)
  3. `test_e2e_cross_document_comparison_success`(skill 版)
  4. `test_e2e_retry_then_success`(skill 版)
  5. `test_e2e_retry_exhausted_safe_abstain`(skill 版)
  6. `test_e2e_retry_cap_prevents_infinite_loop`(skill 版)
  7. `test_e2e_blank_query_fatal_short_circuit`(skill 版)
- 預期:同一組輸入與假依賴下,skill 編譯圖與手寫圖產出的**最終 state(含 `answer_mode`/`verification`/`attempts`/`trace` 結構)完全相同**(逐鍵比對或沿用現有斷言),此為 [01-plan.md 第 5 節](01-plan.md) P2 驗收欄的硬性要求。

**AT2-28 編譯快取(同 revision 只編譯一次)**
- 操作:對同一 skill revision 連續呼叫 `compiler.compile(skill_def)` 兩次,以 mock/spy 包住實際建圖函式(`add_node`/`add_edge` 呼叫序列)計數。
- 預期:第二次呼叫命中快取,底層建圖函式**只被呼叫一次**;更新 revision(內容或版本號變動)後再次呼叫則重新編譯。

## 4. Phase 3 — Script Runner + Tool Registry

### 4.1 沙箱逃逸測試集(≥10 個攻擊樣本,逐一列出,全部在存檔或執行期被擋)

比照 [02-spec.md §5.2](02-spec.md) 白名單規則,每個樣本各為一個 `test_script_runner_sandbox.py` 內的 parametrize case:

| # | 攻擊樣本(script 原始碼片段) | 擋下時機 |
|---|---|---|
| AT3-01 | `import os` | 存檔靜態掃描(`forbidden_script`) |
| AT3-02 | `from os import system` | 存檔靜態掃描(`forbidden_script`) |
| AT3-03 | `exec("state['x']=1")` | 存檔靜態掃描 |
| AT3-04 | `eval("1+1")` | 存檔靜態掃描 |
| AT3-05 | `compile("1+1", "<s>", "eval")` | 存檔靜態掃描 |
| AT3-06 | `open("/etc/passwd")` | 存檔靜態掃描 |
| AT3-07 | `__import__("os")` | 存檔靜態掃描 |
| AT3-08 | `state.__class__.__bases__` (雙底線屬性存取) | 存檔靜態掃描 |
| AT3-09 | `global x` / `nonlocal x` | 存檔靜態掃描 |
| AT3-10 | `while True: pass` | 存檔靜態掃描(僅允許 `for` 於有限 iterable) |
| AT3-11 | `for i in range(10001): pass`(超過 10000 次迭代) | 執行期計數中止(存檔時無法靜態算出動態上界的情形,如 `range(n)` 其中 `n` 來自 state,需執行期擋) |

- 前置(共用):`POST /skills/validate` 或直接呼叫 `engine/script_runner.py` 的 AST 掃描函式。
- 操作:對每個樣本各自呼叫存檔驗證(AT3-01~AT3-10 預期存檔階段即拒絕)與/或執行(AT3-11 需執行期中止,因迭代次數可能依賴執行期才知道的動態值)。
- 預期:AT3-01~AT3-10 存檔回 `{"valid": false, "errors": [{"code": "forbidden_script"}]}`;AT3-11 執行期拋出受控例外(非未捕捉 crash),該步驟視為節點錯誤。

**AT3-12 timeout_ms 逾時走 fatal 短路**
- 前置:script 為合法白名單語法但故意跑滿 CPU 迴圈(如 `for i in range(1000000): x = i * i`),`timeout_ms: 1`。
- 操作:執行該 script 步驟。
- 預期:逾時觸發,該步驟視為節點錯誤,經 Harness 走 fatal 短路(對照 AT1-06),`trace` 記錄該步驟 `status="error"`、錯誤類別含 timeout 標記。

**AT3-13 state 寫入超 256KB 拒絕**
- 前置:script 嘗試 `state["big"] = "x" * (300 * 1024)`。
- 操作:執行該 script 步驟。
- 預期:寫入被拒絕(該步驟視為錯誤,不寫入 state),對照 [02-spec.md §5.1](02-spec.md)「單次寫入 state 的值總大小上限 256KB」。

**AT3-14 script sha256 入 audit trail 且原始碼全文不落 trace**
- 操作:執行含 script 步驟的 skill,檢視回應的 `trace` 與底層 audit 記錄。
- 預期:audit trail 含該 script 的 SHA-256(等於 `skill_revision.definition_sha256` 可回查的雜湊,或該步驟獨立算出的碼片段 hash);`trace` entry **不含** script 原始碼全文,只含讀寫鍵名清單、耗時、狀態、錯誤類別(對照 [02-spec.md §5.3](02-spec.md))。

**AT3-15 tool 呼叫入 trace(tool 名/耗時/狀態,args 不落值)**
- 前置:skill 含一個 `tool` 步驟呼叫 `local.calculator`,`args: {expression: "1+1"}`。
- 操作:執行。
- 預期:`trace` 含一筆 entry:`tool="local.calculator"`、`duration_ms`(數值)、`status="ok"`;**不含** `expression` 的實際值 `"1+1"`(只允許 args 鍵名摘要,不落值,對照 [02-spec.md §6.1](02-spec.md))。

**AT3-16 `tools.call` 呼叫不在 `uses_tools` 清單的 tool 被拒**
- 前置:skill 定義的 `uses_tools: ["local.calculator"]`,但 script 內呼叫 `tools.call("backend.retrieval_search", ...)`。
- 操作:執行該 script 步驟。
- 預期:呼叫被拒絕(該步驟視為錯誤),不實際發出對應的 tool 呼叫(對照 [02-spec.md §5.1](02-spec.md)「呼叫的 tool 必須在 Skill 的 `uses_tools` 清單中」)。

**AT3-17 未註冊 tool → `unknown_tool`**
- 操作:skill 定義的 `tool` 步驟引用 `tool: no_such_tool`,呼叫 `POST /skills/validate`。
- 預期:`errors` 含 `code: "unknown_tool"`(對照 [02-spec.md §3.4](02-spec.md))。

## 5. Phase 4 — 對外化

### 5.1 backend CRUD(`Features/Skills/`)

**AT4-01 POST 建立時呼叫 workflow validate**
- 前置:mock/fake `IWorkflowClient`(或等價的 HTTP client 抽象,比照現有 xUnit fake 風格,不用 mocking library)。
- 操作:`POST /api/skills`,合法 YAML。
- 預期:fake workflow client 記錄到一次 `POST /skills/validate` 呼叫;通過後 DB 寫入 `skill` + `skill_revision`(`revision=1`)。

**AT4-02 驗證失敗 422 fieldErrors 帶引擎錯誤碼**
- 前置:fake workflow client 對 validate 回傳 `{"valid": false, "errors": [{"code": "unbounded_loop"}]}`。
- 操作:`POST /api/skills`,同一份 YAML。
- 預期:`422`,ApiError 形狀 `{timestamp, status, message, fieldErrors}`,`fieldErrors` 內含 `"unbounded_loop"`;DB 未寫入。

**AT4-03 PUT 產生新 revision**
- 操作:對已存在的 skill `PUT /api/skills/quarterly_qa`,新 YAML 內容驗證通過。
- 預期:`200 {revision: <前值+1>}`,`skill.current_revision` 更新,`skill_revision` 新增一筆(`revision` 遞增,`definition_sha256` 對應新內容)。

**AT4-04 DELETE 軟刪 `enabled=false` 且 revision 保留**
- 操作:`DELETE /api/skills/quarterly_qa`。
- 預期:`skill.enabled=false`;`GET /api/skills` 清單不再列出;直接查 `skill_revision` 表仍可撈到所有歷史 revision(未刪除任何列)。

**AT4-05 路由鍵用 `name`**
- 操作:`GET /api/skills/quarterly_qa`(用 `name` 而非 `id`)。
- 預期:`200`,回應為該 skill(對照 [02-spec.md §7.2](02-spec.md)「路由鍵統一用 `name`」)。

**AT4-06 snake_case**
- 操作:`GET /api/skills/quarterly_qa`。
- 預期:回應欄位為 `required_role`、`updated_at`、`created_at` 等 snake_case(對照 [02-spec.md §7.2](02-spec.md),與 documents/workflows 一致)。

**AT4-07 租戶隔離**
- 前置:租戶 A 建立 skill `foo`。
- 操作:以租戶 B 的 identity headers(`X-Tenant-Id` 不同)呼叫 `GET /api/skills/foo`。
- 預期:`404`(跨租戶不可見,比照現有 documents/workflows 的租戶隔離慣例)。

**AT4-08 403 非 ADMIN**
- 操作:以 `X-User-Role: USER` 呼叫 `POST /api/skills` / `PUT /api/skills/{name}` / `DELETE /api/skills/{name}`。
- 預期:三者皆 `403`(對照 [02-spec.md §7.2](02-spec.md)角色欄「ADMIN」)。

### 5.2 workflow 服務

**AT4-09 `GET /skills` 合併內建 + 自訂並帶 `source` 欄位**
- 前置:內建 `skills/kb_query.yaml` 已載入;backend fake 回傳一筆自訂 skill `quarterly_qa`。
- 操作:`curl http://127.0.0.1:8001/skills`(帶服務間標頭)。
- 預期:`200`,陣列含至少兩筆,`kb_query` 的 `source=="builtin"`,`quarterly_qa` 的 `source=="custom"`,兩者皆含 `revision` 欄位(對照 [02-spec.md §7.1](02-spec.md))。

**AT4-10 `POST /skills/{name}/invoke` 錯誤碼順序與現有 `/workflows` 一致**
- 逐案(比照現有 `/workflows/{name}/invoke` 的錯誤碼測試矩陣):
  - 不存在的 skill name → `404`
  - `required_role: ADMIN` 的 skill,`X-User-Role: USER` 呼叫 → `403`
  - `input_schema` 驗證失敗(如缺必填 `query`)→ `422`
  - 下游逾時(如 `timeout_seconds` 到期)→ `504`
  - 未預期例外 → `500`
- 預期:五案錯誤碼與現有 `/workflows/{name}/invoke` 對同一情境的回應碼**逐一相同**,ApiError 形狀一致。

**AT4-11 `POST /skills/validate` 無副作用**
- 操作:連續呼叫 `POST /skills/validate` 三次(同一份或不同份 YAML)。
- 預期:不寫入任何 DB、不變更 `GET /skills` 清單、不產生 `skill_revision` 記錄(對照 [02-spec.md §7.1](02-spec.md)「無副作用」)。

### 5.3 platform 代理

**AT4-12 `/api/skills*` 401 與轉發**
- 操作:不帶 `Authorization` header 呼叫 `curl http://127.0.0.1:8080/api/skills`。
- 預期:`401`。
- 操作:帶合法 JWT 呼叫 `GET /api/skills`。
- 預期:`200`,platform 已附加 identity headers(`X-Tenant-Id`/`X-User-Id`/`X-User-Role`)轉發至 backend,回應內容與直打 backend 相同。

### 5.4 前端(frontend/,lint + build 為主,輔以功能斷言)

**AT4-13 三 Tab 角色可見性**
- 操作:以 `role="USER"` 登入,檢視 Workflows & Skills 視圖。
- 預期:僅 Tab 1(執行)可見;Tab 2(Skill 管理)、Tab 3(節點目錄)不顯示或顯示為停用(對照 [03-design.md §5.3](03-design.md))。
- 操作:以 `role="ADMIN"` 登入。
- 預期:三個 Tab 皆可見。

**AT4-14 YAML 編輯器驗證結果 debounce**
- 操作:在 Skill 編輯器內連續輸入(模擬 keystroke),於 800ms 內多次觸發 onChange。
- 預期:`POST /skills/validate` 僅在停止輸入 800ms 後發出一次(debounce 生效,對照 [03-design.md §5.2](03-design.md))。

**AT4-15 trace 檢視**
- 操作:執行一次 skill invoke,展開任一 trace 列。
- 預期:顯示該節點的 input/output 鍵名摘要與 `failure_codes`(如有),資料直接來自 invoke 回應的 `output.trace`,不另外呼叫新 API(對照 [03-design.md §5.2](03-design.md))。

**AT4-16 revision 歷史唯讀**
- 操作:於 Skill 清單點擊「歷史」。
- 預期:顯示各 revision 的唯讀文字對照(diff),無編輯或儲存操作入口。

**AT4-17 lint + build 全綠**
- 操作:`npm run lint && npm run build`
- 預期:兩者皆成功結束(exit code 0),新增的 Skills 相關元件無 TypeScript 型別錯誤或 ESLint 違規。

## 6. 治理硬規則驗證清單(對應 [02-spec.md §6.3](02-spec.md))

逐條各一個測試案例,四條硬規則皆為 Skill/Script 不可關閉:

**AT-GOV-01 稽核節點強制附加於每條終止路徑**
- 前置:skill 定義的 `flow` 刻意不包含 `audit_feedback` 節點(對照 [02-spec.md §3.1](02-spec.md)範例註解「audit_feedback 不必寫:引擎強制附加」)。
- 操作:compiler 編譯該 skill,執行到任一分支的終止點(含 branch 的 then/else 各分支、loop 離開後的路徑)。
- 預期:每條終止路徑執行後,`trace` 都含一筆等價於 `audit_feedback` 的稽核 entry,即使 skill 作者完全未在 YAML 中宣告;此行為不因 skill 定義而移除。

**AT-GOV-02 loop 上限不可省**
- 操作:嘗試存檔一個省略 `max_iterations` 的 `loop` 步驟(重跑 AT2-02 的情境,從治理角度再次確認:即使透過不同入口——如直接呼叫 `compiler.compile()` 略過 `POST /skills/validate`——也必須被擋)。
- 預期:`compiler.compile()` 對缺少 `max_iterations` 的 loop 定義同樣拒絕編譯(不僅止於 API 層驗證,引擎內部亦有此護欄,對照 [02-spec.md §6.3](02-spec.md) 第 2 點「引擎另設全圖 recursion_limit 護欄」)。

**AT-GOV-03 保留鍵不可被 Script 覆寫**
- 前置:script 步驟嘗試 `state["query_id"] = "forged"` 或 `state["original_query"] = "forged"`。
- 操作:執行該 script 步驟。
- 預期:`query_id`/`original_query`/`query_timestamp` 維持引擎注入的原值,script 的寫入被忽略或拒絕(對照 AT1-05,但驗證對象改為 Script 而非 Node)。

**AT-GOV-04 trace 不落 LLM 私有推理與 script 全文**
- 操作:執行含 LLM 節點(如 `query_rewrite`)與 script 步驟的 skill,檢視完整回應與底層 trace/audit 記錄。
- 預期:trace 不含 LLM 的 chain-of-thought 或私有推理內容(僅結構化輸出摘要,對齊現行 kb_query 治理原則「無 CoT」,見 [02-spec.md 引言](02-spec.md));script 原始碼全文不出現在 trace(只有 SHA-256,對照 AT3-14)。

## 7. 回歸檢查清單

**AT-REG-01 既有 `/workflows` API 行為不變**
- 操作:對 `GET /workflows`、`POST /workflows/{name}/invoke` 執行現有測試矩陣(沿用 `test_api.py` 案例,不修改斷言)。
- 預期:全數行為與 P1~P4 施工前一致;`GET /workflows` 可選擇性標註哪些已有 skill 等價物,但既有欄位不刪除、不改型別(對照 [02-spec.md §7.1](02-spec.md)「保留;清單可標註」)。

**AT-REG-02 4 個舊 `@register` workflow 照常運作**
- 操作:`rag_qa`、`summarize`、`triage`、`analyze_report` 四個工作流分別執行既有的 invoke 測試(`test_triage.py` 等)。
- 預期:全數 PASS,不因 `engine/` 新增而受影響;`kb_query` 工作流入口(`workflows/kb_query.py` 經 `@register`)在 P1~P2 期間對外行為亦不變(內部允許改為經 registry 組圖或最終由引擎編譯,但 `/workflows/kb_query/invoke` 的輸入輸出契約不變)。

**AT-REG-03 聊天 SSE 不受影響**
- 操作:`curl -N -X POST http://127.0.0.1:8080/api/chat/stream -H "Authorization: Bearer $JWT" -H "Content-Type: application/json" -d '{"message":"hello"}'`
- 預期:回應仍為 `data:<value>`(無空格)格式的 SSE 串流,行為與本計畫施工前一致(對照根目錄 AGENTS.md「Cross-Service Contracts」:兩種 SSE 格式皆屬既有契約,`/api/skills*` 的新增不得影響 `/api/chat/stream` 的 `proxy_buffering off` 設定或串流格式)。

---

## 附錄:各 Phase 案例數統計

| Phase | 案例數 | 編號範圍 |
|---|---|---|
| P1 — Node Registry + Harness | 7 | AT1-01 ~ AT1-07 |
| P2 — Skill 引擎 | 28 | AT2-01 ~ AT2-28(含 kb_query parity 7 案:AT2-21~AT2-27) |
| P3 — Script Runner + Tool Registry | 17 | AT3-01 ~ AT3-17(含沙箱逃逸樣本 11 個:AT3-01~AT3-11) |
| P4 — 對外化 | 17 | AT4-01 ~ AT4-17 |
| 治理硬規則驗證清單 | 4 | AT-GOV-01 ~ AT-GOV-04 |
| 回歸檢查清單 | 3 | AT-REG-01 ~ AT-REG-03 |
| **合計** | **76** | — |
