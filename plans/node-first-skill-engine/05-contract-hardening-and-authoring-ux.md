# 契約強制化 × 作者/執行 UX — Node-first 第二階段

> 狀態: **已實作(2026-07-24)。** Track A(A1/A2)與 Track B(B1/B2/B3)七批全部落地並通過 code-reviewer 全差異審查(七檢點全過,判定可收);gate:workflow 655 / backend 266 / platform 437 / frontend lint+build 綠。遺留:`@codemirror/lang-python` 孤兒依賴待授權移除。 相關文件: [計劃書](01-plan.md)、[規格書](02-spec.md)、[設計文稿](03-design.md)、[驗收案例](04-acceptance-tests.md)。
> 本文是 01–04(已交付記錄)之後的下一階段:把 Node-First 尚未兌現的「reads 契約強制化」補完,並修正非技術使用者在 Skill 建立/執行/編輯三個畫面的實際斷點。
> 所有事實基礎均經逐檔查證(含一次「改了實測再還原」的爆炸半徑量測與一輪對抗性評估);證據以 file:line 註記。

## 1. 背景:探查結論

對 workflow 引擎層做 Node-First 落實度總體檢,結論一句話:**圖的組成完全 Node-First(全服務 `StateGraph`/`add_node` 只在 compiler.py);節點隔離只做了一半 —— writes 有牆(Harness 剝除),reads 沒有(節點拿整包 raw state);script 與 agentic 是兩塊明確例外地。**

已確認的具體問題:

| # | 問題 | 證據 |
| --- | --- | --- |
| ① | reads 只是宣告,無執行期強制;且已有三處契約不誠實:`agent_skill_runner` 讀身分三鍵未宣告、`nl_logic`/`nl_extract` 經 `_llm_input` 回落讀 `normalized_query`→`query` 未宣告 | `node_shell.py` 的 `harnessed` 傳整包 state;`agent_skill_runner.py:194-196`;`_llm_input.py` |
| ② | `_check_node` 對 `dynamic_reads` 的 list 值(`input_keys: [docs]`)100% 誤報警告級 `dataflow_error` —— 三支 NL 骨架在基準狀態就各帶一條假警告 | `skill.py` `_check_node`:`if not isinstance(key, str)` |
| ③ | `nl_logic` 的 `output_key` 參數是死彈性:收了照寫,Harness 依 `writes=["business_result"]` 剝除,非預設值永遠靜默丟棄 | `nl_logic.py` 的 ponytail 註解自承 |
| ④ | 執行表單 all-or-nothing:`input_schema` 任一欄位非 `str` → **整張表單**塌成裸 JSON textarea;旗艦 `kb-query`/`template-retrieval` 因 `session_context: dict` 中招,使用者手打非 JSON 看到 V8 英文 `Unexpected token …` | `SkillRunPanel.tsx` `strFields()` |
| ⑤ | invoke 422 = pydantic 原始英文 dump(含內部 model 名、`errors.pydantic.dev` 連結),platform 再整包字串拼進「Skill輸入不符合規範:」 | `main.py` `_validate_input` 的 `str(e)`;`WorkflowService.cs` `MapInvokeErrorAsync` |
| ⑥ | `int/float/bool` + `min_length` = 寫入期不報錯、執行期每次 invoke 必 422(合法值也炸)的死欄位 | `build_input_model` 把 `min_length` 掛上非 str 型別;實跑證實 |
| ⑦ | 簡單模式 create-only:非技術使用者建完 skill 只能進三欄 YAML 進階編輯器;DB 未存範本身分與表單值,無從重回 | `SkillHome.tsx` 只在 `creating` 掛載 `SimpleSkillEditor`;`skill` 表無對應欄位 |
| ⑧ | `dataflow_error` 警告在簡單模式被 `blockingErrors()` 過濾,使用者存出「永遠取不到值」的 skill 而無感;進階編輯器則已正確渲染(警告/錯誤 chip 區分) | `SimpleSkillEditor.tsx` `humanErrors()`;`AdvancedSkillEditor.tsx` 渲染分支 |
| ⑨ | compare/stats 兩支骨架的商業邏輯槽是 `script:`(要非技術使用者寫 Python);DB 查證:唯一啟用的 script skill 只是骨架+填一格,唯一真手寫的已軟刪,另一支已自行遷移到 `nl_logic` —— script 槽是被使用者放棄的能力 | `template-compare/stats.yaml`;appdb `skill_revision` 實查 |

已拍板的三個方向(使用者決定):

1. **型別化渲染用現有六型別逐欄做,不擴充 schema**(不加 date/enum;內建 18 欄位 16 str + 2 dict,擴充收益不足,留待真有自訂需求再評估)。
2. **建立表單的 period/metric/sortBy 廢除,改自然語言規則**(compare/stats 骨架 script→nl_logic)。
3. **編輯鎖死用「存表單狀態」解**(DB 加欄存 `{template_id, form}`,可重回簡單模式)。

## 2. 明確不做(與理由)

- **不動 agentic 的 trace 粒度**:ReAct 中間步驟塌縮成一筆 TraceEntry 是刻意邊界;tool 呼叫已逐次以子 entry 入 trace(`tool_registry.invoke` as_step=False)。只補文件。
- **不移除 `script:` 步驟型別、不動 `script_runner`、不移除任何 node/tool 註冊**:`custom.py` 每次載入重驗(`validate_source`),動了 → 既有 skill(`year-order`)下次執行受控 500。這是所有批次的共同紅線。
- **不把 `normalized_query`/`query` 加進 `RESERVED_KEYS`**:它同時是 `writable_key()` 黑名單來源,加入會使 `query_rewrite` 的 writes 非法、`_clean_skill_input` 剝掉呼叫端的 `query`,幾十支測試量級的破壞。
- **不做 dataflow 的 must-reach 精確分析**:現行寬鬆解讀是刻意取捨(假陽性代價高於假陰性),ponytail 註解已標升級路徑。
- **不改 backend 存檔 201 回應形狀、不修 agentic package 匯入的警告遺失**:UI 已有 `/validate` 獨立管道,非根因。

## 3. Track A — Node-First 契約強制化(workflow only)

### A1(一批,python-implementer)

| 項 | 內容 |
| --- | --- |
| P0-c | 修 `skill.py` `_check_node` 的 `dynamic_reads` list 誤報:值為 `list[str]` 時逐項做資料流檢查,`str` 維持現行,其他型別才報 `dataflow_error` |
| P0-a | 補三處 reads 宣告:`agent_skill_runner` → `["tenant_id","user_id","role"]`;`nl_logic`、`nl_extract` → `["normalized_query","query"]`。同批把 `test_nl_logic.py` / `test_nl_extract.py` 的 `assert spec["reads"] == []` 目錄 pin 改成釘新契約值 |
| P2 | 刪 `nl_logic` factory 的 `output_key` 參數,回傳固定 `{"business_result": result}` |
| P3 | `app/skills/__init__.py` docstring 補:「節點不碰全域 settings,唯一例外是 `retrieve` 的 top_k 最終回落(優先序 config seed > 建構參數 > 全域)」 |

已實測(改後跑過再還原):643 tests 恰 2 支轉紅(即上述兩支 pin);`template-infer`/`template-inspire` 各新增 1 條警告級 dataflow_error,不阻擋存檔、不影響冷啟動編譯;`agent_skill_runner` 三鍵均在 `RESERVED_KEYS`,零新增警告。

**驗收:`uv run pytest` 全綠(643 支,pin 斷言更新後)。**

### A2(依賴 A1,python-implementer)— reads 執行期強制

> 對抗性評估修正:原設計以「writes 非 None 就過濾」為閘門,**會打爆 agentic runner 與 tool 步驟**(兩者有 writes、無 reads;tool 被過濾後 `tool_context` 拿到空 tenant_id = 多租戶隔離破功)。修正後的閘門如下。

**閘門語意:`harnessed()` 新增獨立參數 `reads: Iterable[str] | None = None`;`None` → 不過濾(與 `writes=None` 的既有慣例對稱),顯式提供才過濾。過濾 = 傳 `{k: state[k] for k in effective_reads if k in state}` 給節點函式;缺鍵表現同「前置未寫入」,不 raise。**

各步驟型別的 effective_reads:

| 步驟型別 | effective_reads | 建置點 |
| --- | --- | --- |
| node 步驟 | `spec.reads ∪ dynamic_reads 經該步驟 params 解析`(值為 `str` 取單鍵,`list[str]` 取全部)`∪ (run_on_fatal 節點加 ENGINE_KEYS)` | `compiler.add_node_step` |
| agentic runner | `IDENTITY_KEYS ∪ spec.reads ∪ tuple(skill.input_schema)` | `compiler._build_agentic_graph`(它不經 `add_node_step`,必須顯式傳) |
| tool 步驟 | 不傳 reads(不過濾)。理由:它讀身分三鍵 + args 的 `$state` 引用,身分由引擎自建的 `run_tool` 閉包讀取,非作者碼;過濾無治理收益,漏算即事故 | `compiler.add_tool_step` |
| script 步驟 | 不傳 reads(維持現況;script_runner 自有 StateView 讀寫記錄與 FORBIDDEN_WRITE_KEYS) | `compiler.add_script_step` |
| 匿名節點(測試直接包 fn) | `reads=None` → 不過濾 | — |

**驗收(三支,拒絕假綠):**

1. `reads=["a"]` 的節點看不到 state 的 `b` 鍵。
2. agentic:fake model **回顯 user_message**,斷言其中含 input_schema 的 `query` 值(現有 fake 回固定字串,無論輸入被過濾與否都綠 —— 必須換成回顯式)。
3. tool 步驟:斷言 `ToolContext.tenant_id` 非空且 `$state` 引用的 args 正確解析。
4. `uv run pytest` 全綠。

## 4. Track B — Skill UX(跨服務)

### B1(第一個做;frontend + python + dotnet 三方)

**B1-fe(frontend-implementer)** — `SkillRunPanel.tsx` 廢除 `strFields()` all-or-nothing,改逐欄渲染:

- `str` → text input(維持現行必填/min_length 中文前置檢核);`int`/`float` → `<input type="number">`;`bool` → checkbox;`list`/`dict` → **該欄自己的** JSON textarea(不再拖垮整張表)。
- 沿用 `NodeParamsTab` + `nodeParams.ts` 已驗證的 pattern(metadata 表 + 依 kind 分支 + 中文逐欄錯誤),不新建表單框架、不加依賴。
- 送出時依型別組 payload(現行 `Record<string,string>` 全當字串送)。
- **留空規則(對抗性評估補)**:選填欄位值為空字串 → 省略該鍵(對齊現行 str 的 `v!==''` 語意);`list`/`dict` 空 textarea 不得進 `JSON.parse`;required + 空 → 中文「必填」錯誤。

**B1-py(python-implementer)** — 兩點:

- `main.py` `_validate_invoke` 的 422 不再 `str(e)`:改組 `{error, message(人話總結), fieldErrors: {欄位: 人話}}`,由 `e.errors()` 逐條翻譯(missing→「為必填」、string_too_short→「至少需 N 個字」、int_parsing→「必須是整數」…),不外洩 pydantic model 名與 URL。
- `build_input_model` 對 `int/float/bool` + `min_length` 在**寫入期**拒絕(validate 報錯,而非存進去變死欄位)。

**B1-net(dotnet-implementer;對抗性評估補,缺這步 B1-py 無效)** — `Platform.Service/WorkflowService.cs` 的 `MapInvokeErrorAsync`:解析 workflow 422 body 的 `detail`,把 `message` 與 `fieldErrors` 映進對外 ApiError(`{timestamp,status,message,fieldErrors}` camelCase 契約),不再整包字串拼接。注意 invoke 錯誤走這條、CRUD 走 `BackendErrorMapper`(後者已帶 fieldErrors,不動)。

**驗收:** 前端 lint+build 綠;workflow pytest 綠;platform xUnit 綠;手動:kb-query 試跑表單逐欄呈現、只有 `session_context` 是 JSON 框;int 欄填字母看到中文欄位錯誤。

### B2(骨架去 script;python + frontend)

**B2-py(python-implementer;依賴 A1 的 P0-c 僅因警告數,runtime 依賴 A2 的 list dynamic_reads 解析)**

- `template-compare.yaml`/`template-stats.yaml`:`script:` 步驟 → `nl_logic` 節點(對齊 `template-infer` 形狀:`query_intake → retrieve → nl_logic(input_keys:[docs])`),保留各自 `top_k` 純量槽(stats=50/compare=8)與 instruction 前導語。
- **編列 `test_skill_templates.py` 重寫**(對抗性評估點名,至少 4–6 支涉及:`SCRIPT_TEMPLATES` 分類、`test_script_rule_slot_is_in_script_block`、`test_patched_compare_invoke_sorts_docs_by_score`、`test_patched_stats_invoke_aggregates`、`test_topk_slot_*` 用 Python 規則者全部改為 NL 規則語意)。
- 驗收:`uv run pytest` 全綠。

**B2-fe(frontend-implementer;依賴 A1 的 P0-c —— 假警告修掉前不得把警告亮給簡單模式)**

- `templates.ts`:刪 `slotKind` 欄位(聯集塌成單值)、`sortBy`/`metric`/`period` 移出 openFields 與 `SkillForm`。
- `compose.ts`:刪 `emptyRule` 的 script 分支、正規式的 `=` 分隔符(topK 槽用 `:`,已確認不受影響)。
- `SimpleSkillEditor`:存檔後對 valid-but-warnings 顯示非阻擋提示,只用 `validationLabels` 的人話,不顯示引擎原文。

**相容性(已查證):** 已存 DB 的 skill 是完整 YAML,`custom.py` 照載照跑;簡單模式 create-only,無「重 compose 蓋掉既有值」路徑;`WHITELIST` 與 `openFields` 是獨立清單,拿掉欄位不產生壞 YAML。

### B3(編輯解鎖;dotnet + frontend;依賴 B2 定案表單欄位)

> **前置:先合入當前 branch 在途的 skill package 改動**(`SkillExporter.cs`/`SkillPackageMigration.cs`/`SkillController.cs` 正在變動,B3 的 schema 變更必須 rebase 在其上)。

- **backend(dotnet-implementer)**:`skill` 表加 `simple_form jsonb NULL`,存 `{templateId, form: {name, description, rule, topK}}`(B2 之後表單只剩這些)。**只有 Create/Update 兩條路徑帶入**(`SkillUpsert` 加欄位,platform proxy body 透傳);Import/Restore 不帶(package 匯入品無表單狀態);**export zip 明確不含 simple_form**(UI 便利欄,非可攜 skill 內容)。
- **frontend(frontend-implementer)**:`SimpleSkillEditor` 支援 edit 模式(讀回 simple_form 重填);`SkillHome` 清單對有 simple_form 的 skill 提供「簡單模式編輯」入口(無 simple_form 者維持進階編輯);簡單模式儲存 = 重跑 compose 更新 definition + 同步更新 simple_form。UI 需明示:曾在進階模式手改過的 skill,簡單模式重存會以表單重建定義(覆蓋手改)。

## 5. 依賴圖與執行順序(對抗性評估修正版)

```
B1(fe + py + net 並行)                 ← 第一個做:純收益、命中最痛破口
A1(P0-c + P0-a + P2 + P3)             ← 可與 B1 並行(檔案不重疊)
A2(reads 強制)                         ← 依賴 A1
B2-py ── runtime 依賴 A2;B2-fe ── 依賴 A1(P0-c)
B3    ── 依賴 B2(表單欄位定案)+ 在途 package 改動合入
每批 → code-reviewer;全部完成 → docs-updater
```

docs-updater 範圍:workflow/AGENTS.md(契約段落、reads 強制語意、**測試數更新**)、根 AGENTS.md(Skill Engine 段落、agentic 是單一不透明節點的邊界、各區測試數)、plans/README.md 指向本文件。

## 6. 全域紅線(每批驗收必含)

1. 不移除 `STEP_TYPES` 的 `"script"`;不動 `script_runner`;不移除任何既有 node/tool 註冊。
2. 身分鍵(`tenant_id/user_id/role`)的注入點與唯讀性不變;A2 的任何過濾不得使 `ToolContext` 拿到空租戶(驗收 #3 守著)。
3. `/skills/{name}/invoke` 回應形狀 `{skill, output}` 不變;ApiError 契約(camelCase 四欄)不變 —— B1-net 是往 `fieldErrors` **填內容**,不是改形狀。
4. 對外錯誤訊息不得含 pydantic model 名、`errors.pydantic.dev` URL、python 型別字樣。
