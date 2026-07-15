# 規格書 — Node-first 架構翻轉 × Skill 流程引擎

> 相關文件:[計劃書](01-plan.md)、[設計文稿](03-design.md)。
> 契約以現有服務實際行為為準;引擎沿用 [kb_query 的治理原則](../../workflow/app/kbquery/__init__.py)(驗證閘門、重試上限、稽核、無 CoT)。

## 1. 名詞定義

| 名詞                  | 定義                                                                                                                                                                        |
| --------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Node**              | 一等公民:具名、版本化、宣告 I/O 契約(`reads/writes`)與工具依賴(`requires_tools`)的節點 factory。經 `@node` 註冊進 Node Registry。                                           |
| **Harness**           | 節點標準執行殼。所有節點一律經 Harness 包裝執行,提供 trace、逾時、I/O 契約驗證、fatal 短路、Tool 注入。[runtime.traced()](../../workflow/app/kbquery/runtime.py) 的泛化版。 |
| **Skill**             | 使用者定義的流程描述(YAML 權威格式):要跑哪些 Node、順序、條件分支、迴圈、內嵌 Script 與 Tool 呼叫。                                                                         |
| **Engine / Compiler** | 把 Skill 定義靜態驗證後編譯成 LangGraph `StateGraph` 的元件。                                                                                                               |
| **Script Runner**     | Skill 內嵌 Python 片段的沙箱執行處。                                                                                                                                        |
| **Tool**              | Node 或 Script 可呼叫的能力:`http`(backend API)或 `local`(容器內安裝的函式庫/程式)。經 `@tool` 註冊進 Tool Registry。                                                       |
| **Workflow(舊)**      | 現有 `@register` 手寫圖工作流,保留為相容層。                                                                                                                                |

## 2. Node Registry

### 2.1 註冊契約

```python
@node(
    name="evidence_verification",
    version="1.0",
    description="確定性驗證閘門",
    reads=["selected_evidence", "target_period", "canonical_metric", "excluded_terms",
           "metric_terms", "candidate_answer", "calculation_trace", "requires_calculation"],
    writes=["verification_result", "confidence", "failure_reason", "failure_codes",
            "verified_evidence"],
    requires_tools=[],                      # 需要的 Tool 名稱清單,由 Harness 注入
)
def make_evidence_verification_node(tools: ToolBag) -> NodeFn: ...
```

- `NodeFn = Callable[[dict], Awaitable[dict]]`,與現行節點函式簽名相同。
- `reads`:節點會讀取的 state 鍵。**用途**:Skill 存檔時的資料流靜態檢查、稽核時的輸入摘要。
- `writes`:節點會寫入的 state 鍵。Harness 於執行後**剝除未宣告的輸出鍵**(防止節點偷寫 state),保留鍵(`query_id`/`original_query`/`query_timestamp`)沿用現行不可覆寫規則。
- `version`:語意化版本字串。同名多版本可並存;Skill 以 `node@version` 指定,未指定解析到最新版。
- 重複註冊同 `name@version` → 啟動即 raise(對齊現有 registry 行為)。

### 2.2 遷入清單(P1)

kb_query 10 節點(query_intake、query_rewrite、intent_classification、context_resolver、retrieval_planner、source_retrieval_rerank、data_locator、evidence_verification、answer_composer、audit_feedback)+ 共用 `retrieve`。節點函式本體不改,只補契約宣告。

### 2.3 節點查詢 API

`GET /nodes`(服務間標頭同現有 `/workflows`):

```json
[{"name": "evidence_verification", "version": "1.0", "description": "...",
  "reads": ["..."], "writes": ["..."], "requires_tools": []}]
```

## 3. Skill 定義格式(YAML 權威格式)

### 3.1 頂層結構

```yaml
name: kb_query                 # ^[a-z][a-z0-9_]{2,63}$,租戶內唯一
description: 可驗證可稽核的知識查詢
required_role: USER            # USER | ADMIN
input_schema:                  # 對外 invoke 的輸入驗證(轉為動態 Pydantic model)
  query: {type: str, required: true, min_length: 1}
timeout_seconds: 120           # 選填,覆蓋全域預設
flow:                          # 步驟清單(sequence 為隱含容器)
  - node: query_intake
  - node: query_rewrite@1.0    # 可鎖版本
  - loop:
      max_iterations: 2        # 必填,缺少 → 存檔拒絕
      until: "state.verification_result != 'RETRY'"
      body:
        - node: retrieval_planner
        - node: source_retrieval_rerank
        - node: data_locator
        - node: evidence_verification
  - branch:
      when: "state.confidence < 0.7 and state.verification_result == 'PASS'"
      then:
        - script: |
            state["assumption_note"] = "信心值偏低,建議人工複核"
  - node: answer_composer
  # audit_feedback 不必寫:引擎強制附加(見 6.3)
```

### 3.2 步驟型別(共 6 種,P2 做前 4 種,P3 補 script/tool)

| 型別       | 欄位                                                                      | 語意                                         |
| ---------- | ------------------------------------------------------------------------- | -------------------------------------------- |
| `node`     | `node: <name>[@version]`、選填 `params:`(靜態參數,傳給 factory)           | 執行已註冊節點                               |
| `sequence` | 步驟清單                                                                  | 依序執行(flow 頂層即隱含 sequence)           |
| `branch`   | `when:`(條件式)、`then:`、選填 `else:`                                    | 條件分支;then/else 為步驟清單                |
| `loop`     | `max_iterations:`(**必填**,1~10)、`until:`(條件式)、`body:`               | 有界迴圈;先跑 body 再驗 until,達上限強制離開 |
| `script`   | `script: <python 原始碼>`、選填 `timeout_ms:`(預設 2000,上限 10000)       | 沙箱執行(見第 5 節)                          |
| `tool`     | `tool: <name>`、`args:`(值或 `$state.<key>` 引用)、`save_as: <state key>` | 直接呼叫 Tool,結果寫入 state                 |

### 3.3 條件式語言(`when` / `until`)

擴充 [calculator.py](../../workflow/app/kbquery/calculator.py) 的 AST 白名單求值器,**不是 `eval`**:

- 允許:`state.<key>` 讀取、字串/數字/布林/None 常數、比較(`== != < <= > >=`)、`and/or/not`、`in`、括號。
- 禁止:函式呼叫、屬性鏈(僅 `state.` 一層)、下標以外的任何節點型別 → 存檔時拒絕。
- `state.<不存在鍵>` 求值為 `None`(不拋錯,可與 None 比較)。

### 3.4 靜態驗證(存檔時全數執行,任一失敗 → 拒絕儲存並回 422)

| 規則                                                                            | 錯誤碼                          |
| ------------------------------------------------------------------------------- | ------------------------------- |
| 引用的 node/tool 不存在或版本不存在                                             | `unknown_node` / `unknown_tool` |
| loop 缺 `max_iterations` 或超出 1~10                                            | `unbounded_loop`                |
| 條件式含白名單外語法                                                            | `invalid_expression`            |
| script AST 掃描違規(見 5.2)                                                     | `forbidden_script`              |
| 資料流檢查:某節點 `reads` 的鍵,無前置步驟 `writes` 也不在 `input_schema`/保留鍵 | `dataflow_error`(警告級)        |
| `flow` 為空、步驟型別未知、YAML 解析失敗                                        | `invalid_flow`                  |

## 4. Engine / Compiler

- 輸入:通過靜態驗證的 Skill 定義。輸出:編譯完成的 `CompiledStateGraph`。
- 每個 `node` 步驟 → Harness 包裝的節點;`branch`/`loop` → `add_conditional_edges` + 引擎植入的迴圈計數 state 鍵(`__loop_<id>_count`,Skill 不可讀寫 `__` 前綴鍵)。
- **State 模型**:通用 dict state(等同現行 `KbQueryState` 的泛化),`trace`/`errors` 維持 reducer 累加;保留鍵由引擎注入且不可被 input 覆蓋(沿用 [main.py](../../workflow/app/main.py) 的 `_RESERVED_INPUT_KEYS` 機制)。
- 編譯結果快取:同一 skill revision 只編譯一次(以 revision id 為 key)。
- 執行入口與現有工作流一致:`POST /skills/{name}/invoke` `{input}` → `{output}`,錯誤格式沿用 `workflow_*` 錯誤碼系列。

## 5. Script Runner(沙箱)

### 5.0 威脅模型(v1 的誠實邊界)

v1 in-process 沙箱**防意外、不防惡意**:AST 白名單擋得住 import/dunder/exec,但 (a) `asyncio.timeout` 無法中斷同步 CPU-bound 碼,協作式檢查點僅覆蓋迴圈計數,白名單內的 `sorted(range(10**8))`、`[0] * 10**9` 等記憶體/CPU 炸彈攔不住(256KB 上限只管 state 寫入,不管中間配置);(b) 腳本與 `INTERNAL_API_TOKEN` 同行程,密鑰隔離完全繫於白名單不破功。緩解:authoring 限 ADMIN(信任邊界內)、`range()` 與序列乘法引數加常數上限(建議 10^6);需防惡意作者時升 v2 subprocess。

### 5.1 執行模型(v1,in-process 受限執行)

- Script 收到兩個名稱:`state`(dict,**僅含該步驟宣告可讀寫的鍵**;未宣告時預設全 state 唯讀 + 寫入白名單為 skill 自訂鍵)與 `tools`(僅 `tools.call(name, **args)` 一個方法,呼叫的 tool 必須在 Skill 的 `uses_tools` 清單中)。
- 逾時:`timeout_ms`(預設 2000)以 `asyncio.timeout` + 協作式檢查點強制;超時 → 該步驟視為節點錯誤,走 Harness 的 fatal 短路。
- 記憶體/輸出限制:單次寫入 state 的值總大小上限 256KB。

### 5.2 AST 白名單(存檔時靜態掃描 + 執行前再驗一次)

- **禁止**:`import`/`from`、`exec`/`eval`/`compile`/`open`/`__import__`、雙底線屬性存取(`__class__` 等)、`global`/`nonlocal`、`lambda` 以外的函式定義中使用 yield、`while`(僅允許 `for` 於有限 iterable)。
- **允許**:賦值、`if/elif/else`、`for`(上限 10000 次迭代,執行期計數)、算術/比較/布林運算、f-string、`str/int/float/list/dict/set/tuple/len/min/max/sum/sorted/round/abs/enumerate/range/zip` 等白名單 builtins。
- 非確定性來源(`random`/`time`/`datetime`)不在白名單;需要時間戳由引擎注入 `state["query_timestamp"]`。

### 5.3 稽核

- Script 原始碼的 SHA-256 進 audit trail(不落原始碼全文於 trace,revision 表已存原文)。
- Script 步驟的 trace entry 記:讀寫的鍵名清單、耗時、狀態、錯誤類別。

### 5.4 v2 升級路徑(非本計畫範圍,介面先留)

`ScriptRunnerPort`(Protocol):`async def run(source, state_view, tools, limits) -> dict`。v1 實作 in-process;v2 換 subprocess(獨立行程 + rlimits)或 sidecar container,呼叫端不變。

## 6. Tool Registry

### 6.1 註冊契約

```python
@tool(name="backend.retrieval_search", kind="http",
      description="租戶向量檢索",
      args_schema={"query": str, "top_k": int},
      returns="list[chunk]")
async def retrieval_search(ctx: ToolContext, query: str, top_k: int = 4) -> list[dict]: ...
```

- `kind`:`http`(呼叫 backend,自動帶 X-Internal-Token + 租戶標頭,ctx 供 tenant_id)或 `local`(容器內函式庫,如 calculator、openpyxl 讀表)。
- 現有 adapters(BackendVectorSearch、ScoreReranker、三類 Locator、LoggingAuditRepository)改為 Tool 實作或由 Tool 包裝;`ports.py` 的 Protocol 介面不變。
- **每次 tool 呼叫入 trace**:tool 名、args 鍵名摘要(不落值)、耗時、狀態。

### 6.2 初始 Tool 清單(P3)

`backend.retrieval_search`、`local.calculator`(即 calculator.evaluate)、`local.glossary`、`local.rerank`。後續按需擴充,新 Tool = 加一個 `@tool` 函式。

### 6.3 引擎強制行為(治理硬規則,Skill 不可關閉)

1. 所有節點/步驟經 Harness 執行(trace、fatal 短路不可停用)。
2. `loop` 必有上限;引擎另設全圖 recursion_limit 護欄。
3. 稽核節點(audit_feedback 或引擎內建等價物)**強制附加**於每條終止路徑。
4. 保留鍵不可被 Skill 或 Script 覆寫。
5. audit trail 不落 LLM 私有推理、不落 script 原始碼全文(hash 代替)。

## 7. API 契約

### 7.1 workflow 服務(:8001,服務間標頭同現行)

| 端點                         | 說明                                                                                                                                          |
| ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| `GET /nodes`                 | 節點目錄(名稱/版本/契約)                                                                                                                      |
| `GET /skills`                | 合併清單:內建 skill(repo 檔案)+ 租戶自訂 skill(來自 backend),欄位 `{name, description, required_role, source: "builtin"\|"custom", revision}` |
| `POST /skills/validate`      | 對一份 skill 定義跑全部靜態驗證,回 `{valid, errors[]}`(前端編輯器即時校驗用)                                                                  |
| `POST /skills/{name}/invoke` | 執行;驗證順序與錯誤碼對齊現有 `/workflows/{name}/invoke`(404/403/422/504/500)                                                                 |
| `GET /workflows`(現有)       | 保留;清單可標註哪些已有 skill 等價物                                                                                                          |

### 7.2 backend(:8002,新增,X-Internal-Token + identity headers)

| 端點                        | 角色  | 說明                                           |
| --------------------------- | ----- | ---------------------------------------------- |
| `GET /api/skills`           | USER  | 租戶內清單                                     |
| `GET /api/skills/{name}`    | USER  | 單筆(含定義原文)                               |
| `POST /api/skills`          | ADMIN | 建立(server 端跑一次 workflow 服務的 validate) |
| `PUT /api/skills/{name}`    | ADMIN | 更新 → 產生新 revision                         |
| `DELETE /api/skills/{name}` | ADMIN | 停用(軟刪,`enabled=false`;revision 保留供稽核) |

路由鍵統一用 `name`;回應欄位比照 documents/workflows 採 **snake_case**(`required_role`、`updated_at`)。

### 7.3 platform(:8080,代理)

`/api/skills*` → backend(JWT 驗證後轉發,ADMIN 檢查交 backend);`/api/skills/{name}/invoke` → workflow。錯誤統一 ApiError 形狀 `{timestamp, status, message, fieldErrors}`。

## 8. 非功能需求

- **相容性**:P1~P2 期間現有 workflow 測試全數不修改即通過;既有 `/workflows` API 行為不變。
- **效能**:skill 編譯結果按 revision 快取;單次 invoke 額外開銷(驗證+建圖快取命中)< 10ms。
- **安全**:沙箱規則見第 5 節;authoring 限 ADMIN;服務仍僅綁 127.0.0.1。
- **可測性**:引擎每個元件(registry/harness/compiler/expressions/script_runner)獨立單元測試;sandbox 逃逸測試集(≥10 個攻擊樣本)入 CI;kb_query YAML parity test 為引擎回歸基準。

## 9. 與規格的偏離項與正名(實作後回填)

> 本節在 P1~P4 實作完成、經雙向符合度稽核(逐 AT 讀測試斷言 + 對照實作)後回填,使規格與程式碼一致。76 個驗收案全數通過;以下是實作**刻意偏離本規格或 [03-design.md](03-design.md)** 之處,連同原因與裁決。裁決分三類:**合理補全**(規格留白處的補全)、**對齊現況**(規格與既有系統/現實不符,實作對齊後者)、**誠實縮減**(規格列出但實作刻意不做,已用文件+測試誠實標記)。

### 9.1 偏離本規格(§2~§7)

| #   | 規格出處                           | 規格原文要求                                                    | 實際實作                                                                                                                                                          | 原因                                                                                                                                                           | 裁決                                                                                                   |
| --- | ---------------------------------- | --------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| D1  | §2.1                               | 節點 factory 統一簽名 `make_x(tools: ToolBag)`                  | 保留各節點原簽名,`@node` 以 `deps=[...]` 欄位名注入依賴                                                                                                           | 統一簽名須改動全部 10 個既有節點 factory,違反 P1「既有節點測試一行不改」的不可退讓底線                                                                         | **合理補全**                                                                                           |
| D1a | §2.1                               | 節點宣告 `requires_tools` → Harness 注入 ToolBag 給節點         | **此語意未實作**:所有 kb_query 節點 `requires_tools=[]`,tool 一律經 tool/script 步驟取用,不注入節點。`requires_tools` 僅出現在 `GET /nodes` 目錄,目前為裝飾性欄位 | kb_query 節點無一需要在節點內呼叫 tool;節點級 tool 注入無使用者,YAGNI                                                                                          | **誠實縮減**(欄位保留供未來;需要節點級注入時再實作)                                                    |
| D2  | §2.2 / 03-design §2                | 節點搬至 `app/nodes/kbquery/`                                   | 節點就地留在 `app/kbquery/nodes/`,僅補 `@node` 宣告                                                                                                               | 搬檔會改動既有測試的 import 路徑,違反 AT1-07;§2.2 明文「節點函式本體不改」,搬檔非硬性要求                                                                      | **合理補全**(建議回填 03-design 的路徑描述)                                                            |
| D3  | §3.2                               | loop「達 max_iterations 強制離開」                              | `max_iterations` 為引擎天花板(1~10),業務收斂條件放進 `until` 讀 state(kb_query 用 `retrieval_attempt >= max_retrieval_attempts`)                                  | 業務重試上限是**動態值**(deps 注入,可為 3),編譯期常數 `max_iterations` 表達不了;兩層護欄並存,先到者先收斂                                                      | **完全合規**(AT2-18/26 雙向釘死)                                                                       |
| D4  | §7.1                               | `POST /skills/validate` 未定義請求/回應細節                     | 收 `{definition: YAML}`;valid 時回應含 `skill` 中繼資料(name/description/required_role/input_schema),invalid 時無此欄                                             | backend 不裝第二個 YAML parser,靠此回應寫 DB;`skill` 欄有無即「可否寫入」閘門,不可繞                                                                           | **合理補全**                                                                                           |
| D5  | §5.0                               | 將「`range()` 與序列乘法引數常數上限 10^6」列為 v1 緩解措施     | **移除**序列乘法/pow 的配置上限 guard,只保留 range 長度上限                                                                                                       | 黑名單式 guard 有等價繞法(擋 `*` 有 `xs=xs+xs`、擋單次 pow 有累乘),給假安全感;range 長度是唯一「無等價改寫可繞」的完整護欄。§5.0 本即自陳 v1「防意外不防惡意」 | **誠實縮減**(docstring 明載現狀 + `test_*_honest_gap` 釘住邊界;需防惡意作者請升 §5.4 的 v2 subprocess) |
| D6  | §5.2                               | 沙箱白名單(禁 while/dunder,允許 for 與所列 builtins)            | **比規格更嚴**:僅放行 `tools.call` 一個屬性、拒絕 comprehension/generator                                                                                         | 開放非 dunder 屬性等於交出整張方法表;comprehension 會繞過 `for` 的執行期迭代計數器,使 10000 上限失效                                                           | **更嚴,非漏擋**(無合法 skill 被誤殺)                                                                   |
| D7  | §7.1                               | `GET /workflows`、`GET /skills`(catalog)欄位未含 `input_schema` | 兩者新增 `input_schema` 欄位(既有欄位型別/名稱不動)                                                                                                               | 前端執行表單改為依 schema 動態渲染,舊 workflow 需同形狀資料;additive 不違 AT-REG-01                                                                            | **合理補全**                                                                                           |
| D8  | 01-plan §4 / skill-authoring 舊 §5 | 匯出 `SKILL.md` + `scripts/main.py`                             | 匯出 `SKILL.md` + `skill.yaml`；YAML 原文逐 byte 保存                                                                                                             | 含 node/tool/branch/loop 的流程無法忠實轉成 standalone Python；`skill.yaml` 才是可移植且可稽核的權威 definition                                                | **合理補全**(兩份計畫與驗收已同步)                                                                     |

### 9.2 偏離 03-design(DB Schema §3 / 端點)

| #   | 設計出處 | 設計原文                                                     | 實際實作                                                                                               | 原因                                                                                                                           | 裁決                                                 |
| --- | -------- | ------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------- |
| E1  | §3 DDL   | `skill.tenant_id uuid`                                       | `tenant_id text`                                                                                       | 對齊既有 `rag_documents.tenant_id text` 與 identity header `X-Tenant-Id`(傳的是租戶 code 非 uuid);用 uuid 反與現行 schema 不符 | **對齊現況**                                         |
| E2  | §4.1     | backend 不自驗、驗證交引擎                                   | `POST/PUT` body 僅 `{definition}`;name/desc/role 取自引擎 validate 回報;backend 全域無任何 YAML parser | 單一事實來源落實,backend 不裝第二個 parser                                                                                     | **合理補全**                                         |
| E3  | §7.2     | (未列 revision 查詢端點)                                     | 新增 `GET /api/skills/{name}/revisions`(USER 可讀、租戶隔離)                                           | AT4-16 的 revision 歷史唯讀對照需要                                                                                            | **合理補全**                                         |
| E4  | §7.2     | (未明列衝突/不符碼)                                          | POST 同名 → 409;PUT 的 YAML `name` 與路由 `{name}` 不符 → 422                                          | 避免「改 A 卻改到 B」;名稱即執行時路由鍵,衝突須明確                                                                            | **合理補全**                                         |
| E5  | §3       | 軟刪 + revision 永不刪                                       | 軟刪後同名 POST **復活**成 bump revision(非永久 409)                                                   | 名稱即路由鍵,永久燒毀=功能死路;復活時稽核鏈連續、`skill_revision` 不刪列                                                       | **合理補全**(補強 §3 的軟刪語意)                     |
| E6  | §3 DDL   | `skill.definition_json jsonb`                                | **未建**此欄                                                                                           | 無讀取端;要填充須在 backend 裝第二個 YAML parser,與 E2 衝突。查詢/驗證的權威格式是引擎解析的 `definition` 原文                 | **誠實縮減**(有註解、無殘留依賴;需 jsonb 查詢時再議) |
| E7  | §7.3     | `/api/skills*`→backend、`/api/skills/{name}/invoke`→workflow | 另增 `/api/skills/catalog`、`/api/skills/validate`、`/api/nodes` → workflow 三條代理                   | 前端 Tab1(執行清單)、Tab3(節點目錄)、編輯器即時校驗需要;字面段路由優先序 + backend 保留字雙重防護                              | **合理補全**                                         |

### 9.3 後續建議(消除文件與碼落差的收尾)

1. 本節即為 D1~D7 / E1~E7 的規格回填;若 requires_tools(D1a)日後要落實節點級 tool 注入,§2.1 該語意需一併補實作。
2. `SkillRepository` 的真 SQL(原子 CTE、軟刪復活、租戶過濾)目前僅 e2e 全鏈路(案例 6/8)+ 讀碼背書,單元測試背靠 fake;建議補一個打真 PostgreSQL(Testcontainers)的整合測試,把 fake 與真 SQL 的語意對齊釘死。
