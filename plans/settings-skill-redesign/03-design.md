# 設計文件 — 系統設定重構 × 雙門 Skill 編輯器 × Configuration Set(HOW)

> 狀態:**設計,尚未動碼**(承 01-plan/02-spec「先規劃,不執行」)。
> 本文是 02-spec 的 **HOW**:給實作者可直接照抄的簽章/DDL/props/演算法與落地順序。**不重述 WHAT** —— 決策看 [01-plan.md](01-plan.md)、規格看 [02-spec.md](02-spec.md),本文以「02-spec §X」交叉引用,只補「怎麼寫」那一層。
> 範圍:完整設計 **P1 + P2a + P2b + P3**(最小可用切片),**P4** 較薄(獨立線)。
> 所有觸點皆已對回真實程式碼(file:line);讀碼時發現的縫記在 §10「實作前必讀的落地縫」。

---

## 0. 五維釘定(承 02-spec §0,只補 HOW 插點)

| 維度 | HOW(這次真正要寫/改的那一行) |
|---|---|
| **Model** | 新節點 `make_nl_logic_node` 內 `await llm.structured(system=instruction, user=…, schema=_NlLogicOutput)`;`llm` 由 `deps=["llm"]` 從 `KbQueryDeps.llm`(= `LangChainStructuredLLM`,`adapters.py:56-73`)取。溫度/模型 v1 促升為 per-config(§7)。 |
| **Skill** | `SkillUpsert{definition}`(`SkillDtos.cs:36-38`)**不改**。前端 `compose()` 把使用者規則 patch 進 workflow 取回的骨架原文 → 沿用 `createSkill/updateSkill`(`api/skills.ts:44,52`)。 |
| **Tool** | 不新增 tool/node(除 `nl_logic`)。`compare`/`stats` 用既有 `script` 步驟型別(`RestrictedInProcessRunner`);其餘用既有 `retrieve`/kb_query 節點族。 |
| **Hook** | 四道既有關卡沿用:寫入期 `POST /api/skills/validate`(`SkillController.cs:155`)、`[SkillAdminOnly]`+`RequireTenant()`、試跑=`invoke`、P4 的 apply-at-execution(§7)。 |
| **MCP** | runtime 無 MCP。不引入。 |

---

## 1. 前端 — ConfigView 重構(P1)

### 1.1 目標元件樹

```
ConfigView(isAdmin)                       ← frontend/src/components/ConfigView.tsx
  tab: 'skill' | 'nodeParams' | 'general' (useState,無 router,現制)
  ├─ SkillHome(isAdmin)        [tab==='skill']      ← 新檔(§2)
  ├─ NodeParamsTab(isAdmin)    [tab==='nodeParams'] ← 新檔(P4,§6.4)
  └─ GeneralConfigTab(isAdmin) [tab==='general']    ← 原樣保留(ConfigView.tsx:23-124)
```

### 1.2 ConfigView.tsx 精確 diff(對應 02-spec §1.2)

- `:15` `type Tab = 'general' | 'workflows'` → `type Tab = 'skill' | 'nodeParams' | 'general'`。
- `:17-20` `TABS` 三列(順序 = Skill 優先,一般設定墊底):
  ```ts
  const TABS: { id: Tab; label: string }[] = [
    { id: 'skill',      label: 'Skill' },
    { id: 'nodeParams', label: '工作流節點參數' },
    { id: 'general',    label: '一般設定' },
  ]
  ```
- `:2-4` 匯入:刪 `import { listWorkflows } from '../api/workflows'` 與 `WorkflowInfo`;加 `import SkillHome from './SkillHome'`(P4 再加 `NodeParamsTab`)。
- `:127-181` 整個 `WorkflowsConfigTab` 刪除(工作流唯讀清單不再屬於系統設定,02-spec §1.2)。
- `:185` 初始 tab 改 `useState<Tab>('skill')`。
- `:207-208` 渲染分支:
  ```tsx
  {tab === 'skill' && <SkillHome isAdmin={isAdmin} />}
  {tab === 'nodeParams' && <NodeParamsTab isAdmin={isAdmin} />}  // P4;P1 階段先放佔位或不渲染
  {tab === 'general' && <GeneralConfigTab isAdmin={isAdmin} />}
  ```
- ADMIN-only 不變:`AppShell.tsx:24` 的 `adminOnly:true` 已擋側欄,後端把關為準。

### 1.3 導覽移除工作流(對應 02-spec §1.1)— AppShell.tsx

- `:15` `type View`、`:17` `VIEWS`:移除 `'workflows'`。
- `:19-25` `NAV`:刪 `{ id:'workflows', icon:'⚙', label:'工作流與 Skill' }`。
- `:11` 刪 `import WorkflowsView`;`:203` 刪 `{view === 'workflows' && <WorkflowsView …/>}`。
- `:107-121` `switchView` copilot action:`description`(`:109`)與允許值移除 `workflows`;`VIEWS.includes` 自然收斂。
- `:213-226` copilot `instructions`:`:219` 那句「工作流與 Skill:…」改寫成「Skill 編輯在『系統設定 › Skill』(僅管理員)」,否則副駕教使用者點不存在的選單。
- `WorkflowsView.tsx`:搬走 `answerOf`(`:30-36`,→ 試跑子功能)後**刪檔**。`api/workflows.ts::listWorkflows` 若無其他呼叫端一併退場(RunTab 的 workflow 清單不在本計畫範圍,02-spec §1.1)。`invokeWorkflow` 仍被 `AppShell.tsx:97`(askKnowledgeBase)用,**保留**。

> ponytail:不新增 router、不動 useState 切視圖模型;只刪選單項+分支。最短 diff。

---

## 2. 前端 — Skill 功能樹(P1 骨架 + P2b 填肉)

### 2.1 `SkillHome`(新檔 `components/SkillHome.tsx`)—— 清單 + 功能樹編排

**職責**:列可編輯 Skill(D1:custom 全部 + 內建 `kb_query` 唯讀)、選定後進三子功能樹(編輯/試跑/版本)。**不重寫**清單表格與歷史區塊,重用 `SkillsTab` 的既有 JSX 與 API。

```ts
interface SkillHomeProps { isAdmin: boolean }

// 內部 state
type Selection =
  | { kind: 'list' }
  | { kind: 'tree'; name: string; source: 'custom' | 'builtin'; sub: 'edit' | 'run' | 'history' }
```

清單資料源:`listSkills()`(custom,`api/skills.ts:28`)+ 從 `listSkillCatalog()`(`:12`)濾出 `source==='builtin' && name==='kb_query'` 補一列唯讀。欄位沿用 `SkillsTab.tsx:273-323`(名稱/描述/角色/rev/狀態/操作),外加「來源徽章」(重用 `WorkflowsView.tsx:23-27` 的 `SOURCE_LABEL` / `badge--src-*`)。內建 `kb_query` 列只給「檢視/試跑/版本」,不給「編輯/停用」(其定義是 repo 檔,非 DB)。

**須從 SkillsTab 抽出的唯一結構性重構**:把 `<section className="skill-editor">`(`SkillsTab.tsx:362-447`,節點目錄 / YAML / 驗證三欄)抽成獨立 `components/AdvancedSkillEditor.tsx`,props:
```ts
interface AdvancedSkillEditorProps {
  definition: string
  onChange: (d: string) => void
  saved: Skill | null
  onSave: () => void | Promise<void>
  busy: boolean
  validation: SkillValidation | null
  onExport?: () => void
  onClose: () => void
}
```
理由:進階模式子頁與「簡單→進階單向交棒」(02-spec §1.3)都要掛同一個編輯器;不抽就得複製那段三欄 JSX。**歷史區塊**(`SkillsTab.tsx:325-360`)同理抽 `SkillHistory.tsx`(props: `revisions: SkillRevision[] | null`, `busy`),試跑與版本子頁共用。`CODE_LABEL`/`WARN_CODES`(`:32-43`)移到 `skills/validationLabels.ts` 供兩門共用。
> ponytail:這是本計畫**唯一**的既有元件拆分;拆完 `SkillsTab` 幾乎只剩清單殼,可保留或併入 SkillHome。若嫌拆分大,退路是「SkillHome 直接內嵌 SimpleSkillEditor,進階仍走原 SkillsTab 整頁」——但兩門就無法共用同一顆編輯器,交棒得靠 YAML 字串傳遞。建議照抽。

### 2.2 `SimpleSkillEditor`(新檔,P2b)—— 四塊撰寫外殼

對應 02-spec §1.3 的四塊(名稱/描述/工作流程=範本/商業邏輯=我的規則)+ 試跑 + 版本子功能。

```ts
interface SimpleSkillEditorProps {
  mode: { kind: 'create' } | { kind: 'edit'; name: string }
  onSaved: (name: string) => void          // 存檔成功 → 回列表 & 啟用試跑
  onAdvanced: (definition: string) => void  // 「進階編輯」單向交棒(灌 YAML 進 AdvancedSkillEditor)
}

// 內部 state
interface SimpleState {
  templateId: SkillTemplate['id'] | null    // 5 radios;null=未選(永不從空白起手,§防棄用①)
  form: SkillForm                           // { name, description, rule, topK?, sortBy?, metric?, period? }
  ruleLang: 'nl' | 'python'                 // 商業邏輯:自然語言(預設) / Python(進階 toggle,O2)
  baseDefinition: string | null             // 選範本後從 catalog 取回的骨架原文
  validation: SkillValidation | null
  busy: boolean
  error: string | null
}
```

UI(零術語,02-spec §1.3):
1. **從範本開始**:5 個 radio,來源 = `TEMPLATES`(§3.1)。選定 → `listSkillCatalog()` 找 `name===template.basedOn` 那筆的 `.definition` 存入 `baseDefinition`;取不到就擋存檔(骨架是唯一事實來源,不前端自備)。
2. **名稱**(`form.name`):新建可填;`mode.kind==='edit'` 唯讀(改名需刪重建,`SkillController.cs:113`)。
3. **描述**(`form.description`)。
4. **工作流程**:非技術使用者不編排,僅顯示所選範本 label + 開放欄位(依 `template.openFields` 決定顯示哪些:`topK`/`sortBy`/`metric`/`period`,用 `template.inputWidgets` 對映元件)。
5. **我的規則**(`form.rule`):預設 `<textarea>`(`ruleLang==='nl'`);「進階:改用 Python」toggle → `ruleLang='python'` → 動態掛載 CodeMirror(§4)。
6. **試一下**(§2.3)+ **儲存**/**進階編輯**。

存檔流程(§3.2 的 compose + 既有寫入路徑):
```
const def = compose(template, form, baseDefinition!)      // 純函式,無網路
const v = await validateSkill(def)                         // api/skills.ts:79
if (!v.valid) { setValidation(v); return }                 // 翻人話:重用 CODE_LABEL,避開行號
mode.kind === 'create' ? await createSkill(def) : await updateSkill(mode.name, def)
onSaved(nameOf(def))
```

### 2.3 試跑子功能(D5/O1,對應 02-spec §1.4)

重用 `invokeSkill`(`api/skills.ts:17`)+ `TraceView`(既有,吃 `output.trace`)+ `answerOf`(從 `WorkflowsView.tsx:30-36` 搬進 `skills/answerOf.ts`)。**存後試**:`SimpleSkillEditor` 只有在 `onSaved` 後才啟用「試一下」(invoke 只能跑已存在的 skill;草稿匿名 invoke YAGNI)。標記 `ponytail: 存後試,匿名草稿 invoke 等有人要再說`。

### 2.4 版本控管子功能(D5/O1,對應 02-spec §1.8)

`SkillHistory`(§2.1 抽出)+ `listSkillRevisions`(`api/skills.ts:37`)。回溯 = 取某版 `definition` 重新 `updateSkill`(產新版,不改寫歷史,純前端組合)。

---

## 3. 前端 — 範本 metadata + compose(P2a 前端側 / P2b)

### 3.1 `skills/templates.ts`(薄 metadata,**無 skeleton YAML**)

```ts
export interface SkillTemplate {
  id: 'retrieval' | 'compare' | 'stats' | 'infer' | 'inspire'
  basedOn: `template_${SkillTemplate['id']}`   // 指向 workflow 內建骨架名(§5)
  label: string
  slotKind: 'nl_logic' | 'script'              // 決定 rule 欄注入 instruction 還是 Python body
  openFields: Array<keyof SkillForm>           // 簡單模式可填白名單
  labels: Partial<Record<keyof SkillForm, string>>
  inputWidgets: Partial<Record<keyof SkillForm, 'text' | 'textarea' | 'number' | 'select'>>
}

export type SkillForm = Partial<
  Record<'name' | 'description' | 'rule' | 'topK' | 'sortBy' | 'metric' | 'period', string>
>

export const TEMPLATES: SkillTemplate[] = [
  { id:'retrieval', basedOn:'template_retrieval', label:'知識問答', slotKind:'nl_logic',
    openFields:['name','description','rule','topK'],
    labels:{ rule:'我的規則(用中文寫就好,可留空)', topK:'檢索筆數' },
    inputWidgets:{ rule:'textarea', topK:'number' } },
  { id:'compare', basedOn:'template_compare', label:'比對排序', slotKind:'script',
    openFields:['name','description','rule','sortBy'],
    labels:{ rule:'比較規則(Python)', sortBy:'排序依據' },
    inputWidgets:{ rule:'textarea', sortBy:'text' } },
  { id:'stats', basedOn:'template_stats', label:'統計聚合', slotKind:'script',
    openFields:['name','description','rule','metric','period','topK'],
    labels:{ rule:'統計規則(聚合,Python)', metric:'統計指標', period:'統計期間', topK:'檢索筆數(通常較高)' },
    inputWidgets:{ rule:'textarea', metric:'text', period:'text', topK:'number' } },
  { id:'infer', basedOn:'template_infer', label:'推論', slotKind:'nl_logic',
    openFields:['name','description','rule'],
    labels:{ rule:'推論規則' }, inputWidgets:{ rule:'textarea' } },
  { id:'inspire', basedOn:'template_inspire', label:'啟發', slotKind:'nl_logic',
    openFields:['name','description','rule'],
    labels:{ rule:'啟發角度' }, inputWidgets:{ rule:'textarea' } },
]
```

> `slotKind` 是前端唯一需要知道的「骨架型別」提示(決定 rule 欄的輸入元件與 Python toggle 是否有意義);節點名/版本/flow 一律不進前端。

### 3.2 `skills/compose.ts` —— patch,不 generate

**簽章**:
```ts
export function compose(
  template: SkillTemplate,
  form: SkillForm,
  baseDefinition: string,   // 從 catalog 取回的骨架原文(§5 catalog 帶回)
): string
```

**演算法(純字串 patch,無 YAML 解析庫 —— 見 §10 縫①)**。骨架用**兩種 sentinel 約定**,compose 只做定點字串替換:

1. **規則注入槽**(每支骨架恰好一個):骨架把該步驟寫成 YAML **block literal 區塊純量**,槽內容是單獨一行 `__RULE_SLOT__`(合法字串,骨架照樣啟動載入/驗證,§5):
   ```yaml
   # nl_logic slot(retrieval/infer/inspire)
     - node: nl_logic@1.0
       params:
         instruction: |
           __RULE_SLOT__
         output_key: business_result
   # script slot(compare/stats)
     - script: |
         __RULE_SLOT__
   ```
   compose 找含 `__RULE_SLOT__` 的行,取其**縮排**,把該行換成 `form.rule` 逐行套同一縮排(區塊純量對任意多行 NL/Python 內容天然安全:冒號、引號、關鍵字都字面保留)。`form.rule` 空(NL 選填)→ 換成安全 no-op(`nl_logic`:一句「照原樣回答」;`script`:`pass`)。
2. **白名單純量覆寫**(name/description/topK/sortBy/metric/period):骨架對應行帶尾註標記,值為**合法預設**(確保啟動即載入):
   ```yaml
   name: template_retrieval          # __SLOT_name__
   description: 檢索範本骨架            # __SLOT_description__
   #   … retrieve 步驟內:
         top_k: 8                    # __SLOT_topK__
   ```
   對 `form` 內每個有值的欄位,compose 找帶 `# __SLOT_<field>__` 的行,重寫該行的純量值(字串加雙引號並跳脫,數字原樣)。未出現在 `form` 的欄位 → 骨架原值不動。

3. **emit**:回傳 patch 後的完整 YAML 字串 → 走既有 `validateSkill`→`createSkill/updateSkill`。

> 為何不加 `yaml` npm 套件:前端無 YAML 庫(§10 縫①),O5 只授權 CodeMirror,新增 runtime 依賴需另行授權。區塊純量 + 尾註 sentinel 讓 compose 用純字串 op 就能安全 patch,且骨架仍是啟動可載入的合法 YAML。真正「組出的 YAML 引擎過不過得了驗證」由 workflow pytest 就地驗(§8),不在瀏覽器重做。

### 3.3 types.ts 增補

```ts
// SkillCatalogEntry(:118-125)加一欄:內建骨架項才帶,custom 不帶
export interface SkillCatalogEntry {
  …既有…
  definition?: string   // 內建項的 YAML 原文(compose patch 用);custom 為 undefined
}
```
`SkillTemplate`/`SkillForm` 放 `skills/templates.ts`(非 types.ts —— 它們是 UI 常數,不是 API 契約)。

### 3.4 用到的 apiFetch 呼叫(全既有,零新增端點)

| 用途 | 呼叫 | 檔案 |
|---|---|---|
| 取骨架原文 | `listSkillCatalog()` → `.find(e=>e.name===basedOn)?.definition` | `api/skills.ts:12` |
| 存檔前驗證 | `validateSkill(def)` | `:79` |
| 建立/更新 | `createSkill(def)` / `updateSkill(name, def)` | `:44,52` |
| 試跑 | `invokeSkill(name, input)` | `:17` |
| 版本 | `listSkillRevisions(name)` | `:37` |
| 清單 | `listSkills()` | `:28` |

---

## 4. 前端 — 線上 Python 編輯器(O5,對應 02-spec §1.7)

- 新依賴(已授權):`codemirror` + `@codemirror/lang-python`(傳遞帶入 `@codemirror/state`/`view`)。
- 掛載點:僅 `SimpleSkillEditor` 的 `ruleLang==='python'` 分支與 `AdvancedSkillEditor` 的 script 卡(若有)。**動態 import**,不進首屏 bundle:
  ```ts
  const CodeEditor = lazy(() => import('./PythonEditor'))   // PythonEditor.tsx 內才 import codemirror
  // 用 <Suspense fallback={<Skeleton rows={3}/>}> 包住
  ```
- YAML 欄**不換** CodeMirror,`YamlEditor`(textarea + highlight overlay)沿用。
- 標記 `ponytail: CodeMirror 只給 Python,YAML 沿用 textarea;Monaco 過重不採`。

---

## 5. Workflow — 五支 template_* 骨架 + catalog definition(P2a workflow 側)

### 5.1 五支內建骨架(對應 02-spec §3.0)

新增 `workflow/app/skills/template_{retrieval,compare,stats,infer,inspire}.yaml`。啟動由 `skills/__init__.py::_load_builtin`(`__init__.py:50-61`)`glob("*.yaml")` 自動載入編譯(fail-fast:YAML/節點錯字在啟動當下炸)。

flow 形狀(對回 `kb_query.yaml` 與 `nodes/retrieve.py`):

| 骨架 | flow 形狀(HOW) | 槽 |
|---|---|---|
| `template_retrieval` | 複用 `kb_query.yaml:14-40` 的 intake→rewrite→intent→resolver→(retrieval loop)→answer_composer,尾端加一顆 `nl_logic@1.0` slot | `nl_logic` |
| `template_compare` | `query_intake@1.0` → `retrieve@1.0`(`params:{query_key: normalized_query, top_k: …}`)→ `script`(排序) | `script` |
| `template_stats` | 同上,`top_k` 預設較高(讓聚合看到全集)→ `script`(聚合) | `script` |
| `template_infer` | 檢索族(可薄:intake→retrieve)→ `nl_logic@1.0`(推理) | `nl_logic` |
| `template_inspire` | 同 infer,`nl_logic@1.0`(綜合) | `nl_logic` |

- **注入槽約定**:每支恰一顆商業邏輯步驟,寫成 §3.2 的 block literal + `__RULE_SLOT__`;白名單欄位帶 `# __SLOT_<field>__` 尾註。骨架本身載入即 valid(sentinel 是合法字串/合法預設值)。
- **retrieve top_k per-tenant 的縫**(§7 / 02-spec §3.3 縫 a):`template_retrieval` 用 kb_query 族(吃 `kb_query_top_k`,走 deps);`template_compare/stats` 用通用 `retrieve@1.0`,骨架的 `# __SLOT_topK__` 帶「compose 期預設值」。**執行期覆寫**(縫⑦):`retrieve` 執行期優先讀 per-config state seed `retrieval_top_k`(Configuration Set 的 `retrieval.top_k`,由 `main.py` invoke 期 seed),精度 per-config state ＞ SLOT ＞ 模組全域 —— 故已存的 compare/stats skill 免重 compose 即可靠 activate 生效。

### 5.2 骨架的 deps 掛載(**§10 縫③ —— 不做會啟動崩潰**)

`_load_builtin` 用 `_DEPS_BUILDERS`(`__init__.py:32`,現只有 `{"kb_query": _kb_query_deps}`)決定各 skill 的 deps;查無 → `deps=None` → 若節點需要依賴(`nl_logic` 要 `llm`、kb_query 族要 KbQueryDeps 各埠)**編譯期就炸**。故必須擴充:
```python
from app.workflows.kb_query import _default_deps as _kb_query_deps
_TEMPLATE_NAMES = ("template_retrieval","template_compare","template_stats","template_infer","template_inspire")
_DEPS_BUILDERS = {"kb_query": _kb_query_deps, **{n: _kb_query_deps for n in _TEMPLATE_NAMES}}
```
`KbQueryDeps` 已含 `llm`(`kb_query.py:38`)→ 同時滿足 `nl_logic` 的 `deps=["llm"]`(compiler 以屬性名 `llm` 取 → `getattr(deps,"llm")`)。**此改與五支 yaml 必須同一次提交**,否則服務起不來。

### 5.3 catalog 暴露 definition(**§10 縫② —— 原文並非現成**)

前端 compose 要骨架原文;`GET /skills`(`main.py:96-114`)目前從 `loaded.skill`(已 parse 的 model)組 `SkillInfo`,**不含**原文,而 `LoadedSkill`(`__init__.py:35-44`)**沒保留** `path.read_text` 的原文。兩步改:
1. `LoadedSkill` 加 `definition: str = ""`;`_load_builtin` 讀檔時 `raw = path.read_text(...)`,`parse_source(raw)`,`LoadedSkill(..., definition=raw)`。
2. `SkillInfo`(schema)加 `definition: str | None = None`;`main.py:103-113` 內建項帶 `definition=loaded.definition`,custom 項不帶(前端不 patch 自訂 skill)。platform 原樣代理(`SkillCatalog` 走 BackendClient 透傳)。
> `ponytail: catalog 內建項加一欄 definition,重用既有清單端點,不另開骨架 fetch`。

### 5.4 template_* 不該混進使用者可執行/可編輯清單(§10 縫④)

五支是**骨架**不是給人跑的 skill,但 `_load_builtin` glob 後它們會出現在 `GET /skills`(→ catalog)。約定:**前端**在 SkillHome 清單與任何「可執行」清單過濾掉 `source==='builtin' && name.startsWith('template_')`(它們只在 compose 依 `basedOn` 精確取用)。可選加固:把 `template_*` 前綴加進 backend `SkillController.ReservedNames`(`SkillController.cs:26-30`),擋使用者建同名 custom skill。

---

## 6. Workflow — nl_logic 節點(P3,唯一動到引擎處)

### 6.1 `nodes/nl_logic.py`(對應 02-spec §3.1)

```python
from pydantic import BaseModel
from app.engine.node_registry import node

class _NlLogicOutput(BaseModel):
    result: str

@node(
    name="nl_logic", version="1.0",
    description="以自然語言 instruction 當商業邏輯,執行期呼叫 LLM 解讀並寫回 business_result",
    reads=[],                        # 固定讀取鍵:無
    dynamic_reads=["input_keys"],    # 比照 retrieve 的 query_key:params 指定要餵 LLM 的 state 鍵
    writes=["business_result"],      # 靜態宣告;output_key v1 鎖死(見下)
    deps=["llm"],
    requires_tools=[],
)
def make_nl_logic_node(llm, *, instruction: str, input_keys=(), output_key="business_result"):
    async def nl_logic(state: dict) -> dict:
        keys = tuple(input_keys)
        if keys:
            user = "\n".join(f"{k}: {state.get(k)!r}" for k in keys)
        else:
            user = str(state.get("normalized_query") or state.get("query") or "")
        out = await llm.structured(system=instruction, user=user, schema=_NlLogicOutput)
        return {"business_result": out.result if out is not None else ""}
    return nl_logic
```

- **params 傳遞**:`compiler.add_node_step`(`compiler.py:274-291`)呼叫 `spec.build(self.deps, **params)`,`instruction`/`input_keys`/`output_key` 由 YAML `params:` 直進 factory kwargs —— 與 `retrieve` 同機制,**不改編譯器**。
- **output_key v1 鎖死 `business_result`**:`@node.writes` 是靜態;Harness 剝除未宣告的 writes 鍵,`output_key != business_result` 會被剝。開放需引擎「動態 writes」→ YAGNI。`ponytail: output_key 先鎖 business_result;動態 writes 之後再談`。
- **trace 稽核**:`deps` 含 `llm` → `compiler.py:287` 自動把 `llm_version` 寫進 trace `component_version`。
- **註冊掛載**:`main.py:16-17` 旁加 `from app.nodes import nl_logic as _nl_logic  # noqa: F401`,讓 `GET /nodes` 與編譯器查得到。(`skills/__init__.py` 只 import kb_query 族與 tools;`nl_logic` 走 `app.nodes`,故 main.py 這行是必要的觸發點。)

### 6.2 驗證器/編譯器零改動(對應 02-spec §3.2)

`nl_logic` 註冊後對 `validate_source`/`compile` 就是「又一個節點」;`skill.py` 的 `dynamic_reads` 檢查自動涵蓋 `params.input_keys`。組譯全在前端,workflow 端不動。

---

## 7. Workflow + Backend + Platform — Configuration Set(P4,較薄)

> P4 是獨立重線(跨四服務、新表、tenant 隔離)。此節給落地骨架,細度低於 P1-P3。

### 7.1 Backend 資料表(對應 02-spec §2.2)— DbBootstrap.cs `Ddl`(`:15-76`)尾端追加

```sql
CREATE TABLE IF NOT EXISTS configuration_set (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   text NOT NULL,                 -- 對齊 skill.tenant_id(租戶 code,text)
  name        text NOT NULL,
  is_active   boolean NOT NULL DEFAULT false,
  values      jsonb NOT NULL DEFAULT '{}',
  created_by  text NOT NULL DEFAULT '',
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_confset_tenant_name UNIQUE (tenant_id, name));
CREATE UNIQUE INDEX IF NOT EXISTS uq_confset_active ON configuration_set(tenant_id) WHERE is_active;
```
`uq_confset_active` 部分唯一索引 = 「一租戶至多一 active」的 DB 級護欄(對齊 backend「DB constraint over app code」;比照 `skill` 表冪等建於 `Ddl`)。

### 7.2 Backend DTO/Controller/Repository(新資料夾 `Configuration/`,比照 `Skills/`)

```csharp
// ConfigurationSetDtos.cs(snake_case,比照 SkillDtos.cs:8-46)
public sealed record ConfigurationSetInfo(
  [property: JsonPropertyName("id")] Guid Id,
  [property: JsonPropertyName("name")] string Name,
  [property: JsonPropertyName("is_active")] bool IsActive,
  [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);          // 清單不含 values

public sealed record ConfigurationSet(
  … 上列 + [property: JsonPropertyName("values")] Dictionary<string, object> Values,
  … created_by/created_at);                                               // 單筆含 values

public sealed record ConfigurationSetUpsert(
  [NotBlank] string? Name,
  Dictionary<string, object>? Values);   // is_active 不由 upsert 帶,走專屬 activate 端點
```
- `values` 型別:v1 用 `Dictionary<string, object>` 一個 jsonb 欄承載(model 為 string、其餘為 number),寫前逐鍵型別/範圍驗證(§9 表)。`ponytail: 先 Dictionary<string,object>+逐鍵驗證,不拆多欄`。
- Controller `ConfigurationSetController.cs`(`[Route("api/configuration-sets")]`):`GET`(清單)/`GET {id}`/`POST`/`PUT {id}`/`DELETE {id}`/`POST {id}/activate`。**讀寫全掛 `[SkillAdminOnly]`**(`SkillAdminOnlyAttribute.cs:17`;連讀都要 ADMIN,設定管理性質);每條 `Request.RequireTenant()` 過濾,跨租戶 404。越界值 → 422 + `ApiException.FieldErrors`(比照 `SkillController.cs:179`)。`ponytail: 先重用 [SkillAdminOnly],名字之後嫌髒再抽 [AdminOnly]`。
- Repository(Dapper 直打,比照 `SkillRepository.cs`):`activate` 用單一 data-modifying CTE 原子完成「先本租戶全 `is_active=false`,再目標 `=true`」(對齊 `SkillRepository.cs:57-83` 的 atomic CTE 慣例;不靠應用碼保「至多一 active」,DB 索引兜底)。
- **`SkillUpsert{definition}` 不動**(確認:§2.1)。

### 7.3 Platform 代理(對應 02-spec §2.4)

`Platform.Web/Controllers/ConfigurationSetController.cs` + `Platform.Service/ConfigurationSetService.cs`,比照 `SkillController`/`SkillService`:JWT 驗證 + `User.ToUserContext()` 轉發身分 header,透 `BackendClient` 打 backend,**只代理 CRUD**。角色把關在 backend。讀取路徑(invoke 取 active)**不經 platform**(§7.4)。

### 7.4 Workflow apply-at-execution(O4b (i),對應 02-spec §3.3)

**現況**:`custom.deps()`(`custom.py:44-52`)、`skills/__init__.py` 的 builtin deps、`llm.get_llm`(`llm.py:8` `lru_cache(maxsize=1)`)全是**模組/啟動單例**;builtin 圖在**啟動時**就以全域 deps 編好(`__init__.py:57`)。

**改為**(invoke 時 per-config 重建 deps,靠 `id(deps)` 快取免重編):
1. **取 active set**:backend 加 `GET /api/configuration-sets/active`(單筆,無 → 404→空 values)。workflow 在 `main.py:134-212` invoke 內比照 `custom._fetch`(`custom.py:65-77`,帶內部 token + 身分 header)取回。
2. **有效設定** = 全域預設(`settings`/節點內建)← 疊 active `values`(租戶覆寫)。這即 D8 兩層。
3. **per-config deps 容器**:以有效設定新建 `KbQueryDeps`(`default_top_k`/`max_retrieval_attempts`/新增 `intent_confidence_threshold` 覆寫;`llm=LangChainStructuredLLM(model=…, temperature=…)`,繞開 `get_llm` 單例)。
4. **快取**:新增小 dict `_config_deps_cache: OrderedDict[(tenant_id, config_version), deps]`,FIFO 上限(比照 `compiler.py:526` `_CACHE_MAX=32`)。`config_version` = active set 的 `updated_at` 或內容雜湊。同租戶+同版 → 同 `id(deps)` → `compiler.compile` 命中既有圖快取(`compiler.py:540` 鍵含 `id(deps)`);換版 → 新 deps → 重編一次。
5. **注入點**:invoke 內解析有效設定 → 取/建 per-config deps →
   - custom:`custom.load(name, ctx, deps=per_config)`(把 `load` 的寫死 `deps()`(`custom.py:139`)改為可帶入參數,None 時回退單例)。
   - **builtin**:`main.py:146` 目前用 `skills.get(name)` 的**啟動預編圖**(全域 deps)。有覆寫時須改為 `compiler.compile(loaded.skill, per_config)` 重編(cache 依 `id(deps)` 去重)——即 builtin 不再直接用 `loaded.graph`,而是「用 `loaded.skill` + per-config deps 走 compile」。無 active set → 沿用啟動圖(零額外成本,回歸現況)。
   - `main.py:188` timeout 改讀有效設定的 `workflow.timeout_seconds`。
   - **`retrieval.top_k` 走 state seed 不走 deps**(縫⑦):通用 `retrieve@1.0` 讀模組全域 `settings.retrieval_top_k`,不在 `KbQueryDeps` 上。`config_apply.resolve` 於 invoke 期額外回傳租戶覆寫的 `retrieval.top_k`(僅在 active `values` 明確帶該鍵時,否則 None);`main.py` seed 進初始 state 的 `retrieval_top_k`,`retrieve` 執行期優先讀之(精度 per-config state ＞ 骨架 SLOT ＞ 全域)。`retrieval_top_k` 是 `harness.CONFIG_SEED_KEYS`／`skill.RESERVED_KEYS`,故是 state 頻道(seed 不被 schema 濾掉)、資料流視為可用、呼叫端不得經 input 夾帶、節點/Script 只讀不可寫。未覆寫則不 seed,零行為變更 —— 讓已存(不重 compose)的 compare/stats skill 靠 activate 即生效。

**促升的兩個寫死值**(O3):
- `intent_classification.py:91` 的 `0.6`:`make_intent_classification_node` factory 加 `confidence_threshold=0.6` 參數 + `KbQueryDeps` 加同名欄位,graph 建構時傳入(`kbquery/graph.py`)。判斷式 `out.confidence >= 0.6` 改讀該參數。
- `llm.py:19` 的 `0.7`:只在 per-config 建 `LangChainStructuredLLM` 時帶入 `temperature`(全域路徑仍走 `get_llm` 的 0.7,不動)。

### 7.5 前端 `NodeParamsTab`(P4,對應 02-spec §2.3 表單)

Configuration Set 的 CRUD 表單:清單(多組 + active 標記)→ 選定/新建 → 表單欄位 = §9 開放鍵(型別依表:number/select),`activate` 按鈕。走新 `api/configurationSets.ts`(list/get/create/update/delete/activate,全經 `apiFetch`,錯誤 ApiError)。ADMIN-only(側欄已擋 + 後端把關)。

---

## 8. 兩層覆寫與系統 Admin 留位(對應 02-spec §4.2,不實作)

全域預設(節點內建/settings)= 未來系統 Admin 那層,v1 就是「程式內建值」;租戶 active Configuration Set = 組織 Admin(現行 ADMIN)那層。執行時租戶覆寫疊全域預設(§7.4 步 2)。程式**不寫任何跨租戶捷徑**(所有查詢帶 tenant_id),為未來 `SYSTEM_ADMIN` + 全域預設表留路。

---

## 9. Configuration Set v1 開放鍵(O3,對應 02-spec §4.1)

| `values` 鍵 | 全域預設(來源) | 型別/範圍 | 執行套用點 |
|---|---|---|---|
| `retrieval.top_k` | 4(`settings.retrieval_top_k`,`settings.py:19`) | int 1–50 | 通用 `retrieve@1.0` 執行期讀 per-config state seed `retrieval_top_k`(§7.4／§10 縫⑦)。取值精度:per-config state seed ＞ compose 期 SLOT(`# __SLOT_topK__`,build 參數) ＞ 模組全域 |
| `kb_query.top_k` | 8(`settings.py:25`) | int ≥1 | `KbQueryDeps.default_top_k` |
| `kb_query.max_retrieval_attempts` | 2(`settings.py:26`) | int ≥1 | `KbQueryDeps.max_retrieval_attempts` |
| `workflow.timeout_seconds` | 120(`settings.py:22`) | int ≥1 | `main.py:188` |
| `llm.model` | gpt-4o-mini(`settings.py:10`) | str(白名單=LiteLLM 已配置模型) | per-config 建 LLM |
| `intent.confidence_threshold`(促升) | 0.6(`intent_classification.py:91`) | float 0–1 | `KbQueryDeps` 新欄 → factory |
| `llm.temperature`(促升) | 0.7(`llm.py:19`) | float 0–2 | per-config 建 LLM |

只存覆寫值,未覆寫回落全域預設。明確不做(v2/另計畫):rerank 加權、變體數上限、容差、locator 權重、詞彙/口徑/意圖對照/公式規則。

---

## 10. 實作前必讀的落地縫(讀碼發現,02-spec 未點名)

| # | 縫 | 影響 | 對策 |
|---|---|---|---|
| ① | **前端無 YAML 庫**(package.json 僅 highlight.js) | compose 不能 parse/emit YAML | §3.2 block-scalar + 尾註 sentinel 純字串 patch;不加依賴 |
| ② | **`LoadedSkill` 不保留骨架原文**(`__init__.py:52` parse 後丟原文) | catalog 無 `definition` 可暴露,compose 取不到骨架 | §5.3:`LoadedSkill.definition=raw` + `SkillInfo.definition` |
| ③ | **`_DEPS_BUILDERS` 只認 kb_query**(`__init__.py:32`) | 五支 template 啟動 glob 載入時 deps=None → nl_logic/kb 族編譯期崩,**整服務起不來** | §5.2:五名映射 `_kb_query_deps`,與 yaml 同次提交 |
| ④ | **template_* 會混進 `GET /skills` 目錄** | 骨架出現在使用者可執行/可編輯清單 | §5.4:前端濾 `template_` 前綴;可選加進 ReservedNames |
| ⑤ | **builtin 圖啟動即預編(全域 deps)**(`__init__.py:57`) | P4 per-config 覆寫對 builtin 無效(用的是啟動圖) | §7.4 步 5:有覆寫時 builtin 改走 `compile(skill, per_config)`,靠 `id(deps)` 快取 |
| ⑥ | **`nl_logic` 走 `app.nodes` 非 kb_query 族** | `skills/__init__.py` 的 import 觸發不到它的 `@node` | §6.1:`main.py` 加 `from app.nodes import nl_logic` |
| ⑦ | **`retrieve.py` 的 top_k 取值** | `retrieval.top_k` 覆寫要能對已存(不重 compose)的通用 retrieve skill 執行期生效 | retrieve 執行期讀 per-config state seed:精度 per-config state(`retrieval_top_k` seed)＞ compose 期 SLOT(build 參數)＞ 模組全域。`retrieval_top_k` 列為 `harness.CONFIG_SEED_KEYS` → 進 `skill.RESERVED_KEYS`(是 state 頻道、資料流視為可用、呼叫端不得夾帶、只讀不可寫);`config_apply.resolve` 於 invoke 期抽出租戶覆寫值,`main.py` seed 進初始 state;未覆寫則不 seed,retrieve 回落 SLOT/全域,零行為變更。範本仍保留 `# __SLOT_topK__`(compose 期預設值),SLOT 是「無 active 覆寫時的預設」而非唯一落點 |

---

## 11. 多租戶落實點(D8,承 02-spec §5,逐點清單)

1. Skill:`skill.tenant_id` + `RequireTenant()`,跨租戶 404(`SkillController.cs:44,49`)——現況 ✓,不動。
2. Configuration Set CRUD:`configuration_set.tenant_id` + controller 每條 `RequireTenant()`;`uq_confset_active` per-tenant(§7.1-7.2)。
3. 執行取值:workflow 取 active set 帶 `X-Tenant-Id`(`custom._headers` 模式,`custom.py:55-62`);per-config deps 快取鍵含 `tenant_id`(§7.4 步 4)——杜絕 A 租戶設定污染 B 租戶執行(比照 `custom.py:1-13` 快取毒化顧慮)。
4. 內建 skill 執行:`state={"tenant_id": ctx.tenant_id, …}`(`main.py:187`)+ retrieve 帶 `X-Tenant-Id`——現況 ✓。
5. 一般設定 `app_config`:全域無 tenant 欄,O6 不動(§8)。
6. Admin 分層:組織 Admin=ADMIN+tenant 隔離;系統 Admin 僅留位,不寫跨租戶捷徑。

---

## 12. 變更順序(什麼先落什麼)

```
① P3  nodes/nl_logic.py + main.py import + pytest            (無依賴,先落)
② P1  AppShell 移除 workflows + ConfigView 三分頁 + SkillHome 骨架
       + 抽 AdvancedSkillEditor/SkillHistory                  (無依賴,可與①並行)
────────────────────────────────────────────────────────────
③ P2a-workflow  5×template_*.yaml + _DEPS_BUILDERS 擴充(縫③)
                + LoadedSkill.definition raw + SkillInfo.definition(縫②)
                ⚠ retrieval/infer/inspire 三支的 nl_logic slot 依賴①已合入;
                  compare/stats(script slot)不依賴①
④ P2a-frontend  skills/templates.ts + types SkillCatalogEntry.definition?
────────────────────────────────────────────────────────────
⑤ P2b  compose.ts(需③的 catalog.definition 已上線)+ SimpleSkillEditor
       + 試跑(需①的 NL 路徑)+ validate 翻人話 + Python CodeMirror(O5)
────────────────────────────────────────────────────────────
⑥ P4(獨立線,不擋①-⑤):
   6a backend  configuration_set 表 + DTO + Controller + Repository + activate
   6b platform ConfigurationSetController/Service 代理(需 6a)
   6c workflow invoke apply + per-config deps + 促升 2 值(需 6a 的 /active 端點)
   6d frontend NodeParamsTab(需 6b)
```

**硬順序**:catalog 的 `definition` 暴露(③)**必須早於** compose(⑤)能 patch;`_DEPS_BUILDERS` 擴充(③)**必須與** template yaml 同次提交(否則啟動崩);nl_logic 節點(①)**必須早於** nl_logic-slot 的三支 template 合入(否則骨架載入即炸)。

---

## 13. 測試矩陣(承 repo 慣例:xUnit 手寫 fake / pytest / 前端 lint+build)

### 13.1 Backend(xUnit,手寫 fake,無 mock 庫)
- `ConfigurationSetRepository` CRUD + `activate` 唯一性:兩次 activate 後只剩一組 `is_active`(打真 DB fixture,驗 `uq_confset_active` 生效)。
- values 型別/範圍:越界(如 `retrieval.top_k=99`)→ 422 + `fieldErrors`;非白名單鍵拒收。
- 租戶隔離:demo-a 看不到 demo-b 的組;跨租戶 `GET {id}`/`PUT`/`activate` → 404。
- ADMIN 守衛早於模型驗證:USER 送不合法 body 打任一端點 → 403(非 400),比照 `SkillAdminOnlyAttribute` 的 filter 階段順序。
- `SkillUpsert` 契約不回歸(既有 skill 測試全綠)。

### 13.2 Workflow(pytest,對回 343 既有慣例)
- **五支 template_* 就地驗**(§8 從前端移來的真正檢查):啟動載入即編不炸;各支把 `__RULE_SLOT__` patch 成一句規則(nl_logic slot)/一段 Python(compare/stats 的 script slot)後 `validate_source` 仍 `valid`;invoke 得到含 `business_result` 的 output。
- `nl_logic` 單元:mock `llm`,驗 `instruction`→system、`input_keys`→user 組裝、寫回 `business_result`;`output_key` 非預設時被 Harness 剝除(印證 v1 鎖死決策)。
- NL skill 端到端:組一份含 `nl_logic` 的 skill → invoke → 斷言 output 有 `business_result`、trace 有該節點、`component_version` 非空。
- P4 config 套用:給定覆寫 values,斷 per-config deps 反映(top_k/model/temperature/intent 門檻);同版第二次 invoke 命中圖快取(不重編,比照計數點 `compiler.py:465`);換版重編;無 active → 全走全域預設(kb_query 行為與現況一致);租戶隔離(A 的 config 不影響 B)。

### 13.3 Frontend(oxlint + tsc/vite build;無 test runner)
- `compose.ts` 一支輕量 **patch 形狀自我檢查**(node 腳本 assert,純函式、不打網路、不呼叫 validateSkill):餵假骨架 YAML(含 block-scalar `__RULE_SLOT__` + `# __SLOT_name__`/`# __SLOT_topK__`),斷言:①`__RULE_SLOT__` 那行被規則逐行取代且縮排保留;②多行規則不破壞 YAML 結構(縮排一致);③白名單欄位值被覆寫;④非白名單行原樣。符合 ponytail「非平凡邏輯留一個可跑檢查」。
- `npm run lint` + `npm run build` 綠(含新 CodeMirror 動態 import 的型別)。

### 13.4 跨鏈(e2e-verifier,docker compose)
聊天 SSE / 文件 202→ready / 既有 `kb_query`·`rag_qa` invoke 契約不破。

---

## 14. 明確不做(YAGNI,承 02-spec §7)

不退回三欄位舊模型;不做拖拉流程編排;簡單模式不碰流程/節點/YAML;組織自訂骨架延後(骨架 v1 已是 workflow 內建 skill,遷移縫 = 複製成 custom skill);不 UI 化規則資料;不改聊天/SSE/AG-UI 契約;不建系統 Admin 層(僅留位);`nl_logic.output_key` v1 鎖死;草稿匿名試跑延後;`app_config` 不 per-tenant 化;前端不加 YAML 庫。
