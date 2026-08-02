# 實作規格 — 系統設定重構 × 雙門 Skill 編輯器 × Configuration Set

> 狀態: **主要能力已交付。** 本文保留原始規格；目前實作與待驗證行為以 [plans README](../README.md) 為準。
> 下文保留 [01-plan.md](01-plan.md) 的 D1–D8 / O1–O7 原始規格；與現行程式碼不一致時，以程式碼和測試為準。
> 引擎(`workflow/app/engine/`)資料模型不動;唯一動到引擎的是新增一顆 `nl_logic` 節點與「執行時套用 Configuration Set」。
>
> **稽核追加(已推翻的具體技術判斷,不逐行改寫下文步驟記錄):**
> - ConfigView 現況為**四分頁**(`businessWorkflows`/`agentSkills`/`nodeParams`/`general`,`frontend/src/components/ConfigView.tsx:15-22,174-177`),非本文描述的三分頁。Skill 概念已依 skill-concept-realignment 拆分為「業務流程」(本計畫的範本/簡易-進階雙門編輯落地於此)與「Agent Skills」(package 概念,不使用本計畫的範本/nl_logic 機制)。
> - compare/stats 的 Python `script` 槽與 O5 CodeMirror 6 **未交付**;五支範本商業邏輯槽已統一為 `nl_logic`(`frontend/src/skills/templates.ts` 型別已無 `slotKind` 欄;`workflow/app/skills/template-compare.yaml:12` 註解「舊 script 槽已廢」;`frontend/package.json` 無 codemirror 依賴,無 `PythonEditor.tsx`)。
> - O6「`app_config` 維持全域」的決策**已被推翻**:現為 tenant-scoped + ADMIN-only(`(tenant_id, key)` 複合主鍵,GET/PUT 都經 `RequireTenant()`,見根 AGENTS.md Backend 信任邊界節)。此變更非本計畫落地,但決策記錄已過期。
> - `SkillsTab.tsx` **已不存在**,進階編輯器現為獨立 `AdvancedSkillEditor.tsx`(被 `BusinessWorkflowHome.tsx` 使用);`WorkflowsView.tsx` **未刪除**,已被 agent-platform-redesign D4 的 Workflow Designer 重新利用,與本文描述的用途無關;`SkillHome.tsx` 已變成無 kind 決策的共享 presentation 元件,由 `AgentSkillHome.tsx`/`BusinessWorkflowHome.tsx` 各自帶 `kind` props 組裝,非單一頂層 tab 元件;簡單/進階編輯實際呼叫 `frontend/src/api/businessWorkflows.ts`,非 `api/skills.ts`。

---

## 0. 五維釘定(核心交付,先講結論)

計畫要求把 **Model / Skill / Tool / Hook / MCP** 五個維度各釘一個具體選擇並指出插點。彙整如下,細節見後續章節。

| 維度                        | 具體選擇                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            | 插點(file:line)                                                                                                                                                                      |
| --------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Model**                   | 執行期商業邏輯:`nl_logic` 節點呼叫 LLM,模型取 `settings.llm_model`(預設 `gpt-4o-mini`,經 LiteLLM),溫度沿用 `llm.py` 的 0.7(v1 也促升為可調)。**授權/試跑時不另用模型** —— 試跑就是走同一條 invoke,沒有第二個模型。Configuration Set 以 `llm.model` / `llm.temperature` 鍵**per-tenant 覆寫**。                                                                                                                                                                                                                                                                                                      | `workflow/app/llm.py:15-20`、`workflow/app/settings.py:10`;新節點 `workflow/app/nodes/nl_logic.py`                                                                                   |
| **Skill**                   | 資料模型**不變**:仍是單一 `definition`(YAML 原文),`skill`/`skill_revision` 表、`SkillUpsert{definition}`、revision/`current_revision`、`required_role`、`tenant_id`、builtin(repo `skills/*.yaml`)vs custom(DB)全部照舊。4 分頁(名稱/描述/工作流程/商業邏輯)只是**前端撰寫外殼**,存檔前於瀏覽器把「規則」patch 進一份既有骨架 YAML → 沿用既有 `POST/PUT /api/skills`。**範本拆兩半**:骨架(引用節點的 flow)= workflow 內建 skill `template_*`(與 `@node` 契約同源同 deploy);UI metadata(label/開放欄位/輸入元件)= 前端常數。compose **patch 既有內建骨架**,前端不生成 flow YAML、不寫死節點名/版本。 | `backend/.../Skills/SkillDtos.cs:22-38`、`SkillController.cs`;骨架 `workflow/app/skills/template_*.yaml`(§3.0);前端 metadata `frontend/src/skills/templates.ts` + patch `compose.ts` |
| **Tool**                    | 沿用既有 `@tool` 註冊表與 `@node` 契約;`template_*` 骨架用的節點(檢索/驗證/`nl_logic`/`script`)全是引擎已驗證的既有節點,由 workflow curate、pytest 就地驗。compose 只 patch 骨架裡那顆商業邏輯 slot 與白名單開放欄位,**不新增節點/tool**。**slot 型別依原型分兩路**:`retrieval`/`infer`/`inspire` 的 slot 是 `nl_logic`(patch `instruction`);`compare`/`stats` 因需精確算術(相對排序 / 聚合算術),slot 預設是既有 `script` 步驟型別 → `RestrictedInProcessRunner` 沙箱(patch Python body),tool 面由 `uses_tools` 白名單約束(現制)。v1 不新增 tool。                                                  | `workflow/app/engine/tool_registry.py`、`script_runner.py`、`compiler.py:309-337`;骨架 `workflow/app/skills/template_*.yaml`                                                         |
| **Hook**(治理/生命週期攔截) | 本專案無 middleware 式「hook」概念;扮演該角色的是四道既有關卡,全部沿用不繞過:①**寫入期驗證** backend→workflow `POST /api/skills/validate`(引擎為唯一事實來源,502 若不可達);②**角色/租戶守衛** `[SkillAdminOnly]` + `RequireTenant()`(backend)+ 側欄過濾(前端,非安全邊界);③**試跑** = `POST /api/skills/{name}/invoke`(與正式執行同一路徑,無特權);④**Configuration Set apply-at-execution** = workflow invoke 時取 active set 疊上全域預設再建 deps。                                                                                                                                                | backend `SkillController.cs:80,107,138,155`;workflow `main.py:134-212`                                                                                                               |
| **MCP**                     | **執行/授權/試跑路徑上完全沒有 MCP server**。repo 內 `codebase-memory` / `playwright` 等 MCP 僅供開發代理,不在任何 runtime 上。明確記錄:本功能不引入、不依賴任何 MCP。                                                                                                                                                                                                                                                                                                                                                                                                                              | 無                                                                                                                                                                                   |

---

## 1. 前端規格(P1 / P2b / P2c)

### 1.1 導覽移除工作流頂層項(P1,對應 §3.1)

`frontend/src/components/AppShell.tsx`:

- `:15` `type View` 與 `:17` `VIEWS` 移除 `'workflows'`;`:19-25` `NAV` 陣列移除 `{ id:'workflows', … 工作流與 Skill }` 那列。
- `:11` `import WorkflowsView`、`:203` `{view === 'workflows' && <WorkflowsView …/>}` 一併刪。
- `:107-121` `switchView` copilot action:允許值清單移除 `workflows`;`:219` copilot instructions 內「工作流與 Skill」段改寫為「系統設定 › Skill」(否則副駕會教使用者點一個不存在的選單)。
- `WorkflowsView.tsx` 的三個 Tab 各自去處(§3.1 表):
  - **RunTab**(執行 workflow/skill invoke + TraceView,`WorkflowsView.tsx:48` 起)→ 收進 Skill 功能樹的「執行/試跑」子功能(見 1.4)。純 code 工作流(summarize/triage/rag_qa/analyze_report)的獨立執行入口**不在本計畫範圍**(仍可經聊天/agent 觸發);故 RunTab 的 workflow 清單部分直接下架,只保留 skill invoke 能力移入試跑子功能。
  - **SkillsTab**(`SkillsTab.tsx`)→ 系統設定 › Skill 的**進階模式**(1.3),整個元件近乎原樣重用。
  - **NodeCatalog**(唯讀)→ 進階模式的「工作流程」欄左側(插入節點用,`SkillsTab.tsx:406` 已如此嵌入)。
- `WorkflowsView.tsx` 檔案在搬移完成後刪除;`api/workflows.ts` 的 `listWorkflows` 若僅剩 `ConfigView` 舊分頁使用,隨該分頁一併退場(見 1.2)。

> ponytail:不新增 router、不動 `useState` 切視圖模型 —— 只刪選單項與對應分支,是最短 diff。

### 1.2 系統設定改三分頁(P1,對應 §3.2)

`frontend/src/components/ConfigView.tsx`:

- `:15` `type Tab = 'general' | 'workflows'` → `'skill' | 'nodeParams' | 'general'`;`:17-20` `TABS` 改為:
  ```
  { id:'skill',      label:'Skill' }
  { id:'nodeParams', label:'工作流節點參數' }
  { id:'general',    label:'一般設定' }
  ```
- 移除 `WorkflowsConfigTab`(`:127-181`)與其 `listWorkflows` 匯入(`:3`)—— 工作流唯讀清單不再是系統設定的內容(§3.2 只留三分頁)。
- `GeneralConfigTab`(`:23-124`)**原樣保留**(既有 key/value 表,ADMIN 就地編輯)。
- 新增 `<SkillHome>`(1.3–1.5,Skill 功能樹)與 `<NodeParamsTab>`(1.6,Configuration Set 表單)兩個子元件,掛在容器 `:207-208`。
- 整頁維持 ADMIN-only:側欄 `adminOnly:true`(`AppShell.tsx:24`)不變,後端把關為準。

### 1.3 雙門 Skill 編輯器(對應 §4.2–4.3)

「Skill」分頁 = 一棵功能樹(§4/§D7):清單 → 選定 → {編輯 | 試跑 | 版本}。清單沿用 `SkillsTab.tsx:273-323` 的表格(名稱/描述/角色/rev/狀態/操作),不重寫。

**兩道門編輯同一份 `definition`,資料層不分岔(D4):**

- **進階模式** = 現有 `SkillsTab` 的三欄編輯器(節點目錄 / YAML / 驗證結果,`SkillsTab.tsx:403-445`)**原樣重用**。它已能表達引擎全部能力,是「原始 YAML 逃生口」。
- **簡單模式(預設,新元件 `SimpleSkillEditor.tsx`)** = 非技術使用者看到的全部,零術語/零流程圖/零 YAML/零 state 鍵。UI 僅四塊(對齊 4 分頁需求 §需求3):
  1. **名稱**(新建可填、既有唯讀 —— 對應 `SkillController.cs:113` 改名需刪重建)。
  2. **描述**。
  3. **工作流程** —— 非技術使用者**不編排**,由選定的**範本**決定(1.5);UI 只顯示「從範本開始:○檢索 ○比對 ○統計 ○推論 ○啟發」。
  4. **商業邏輯(「我的規則」)** —— 預設**自然語言**文字框(→ `nl_logic`);「進階:改用 Python」toggle 才顯示 CodeMirror(1.7)。
- 兩道門切換:簡單模式的「進階編輯」鈕 → 把當前組譯出的 YAML 灌進 `SkillsTab` 編輯器接手(單向即可;從進階退回簡單非必要,YAGNI)。

四條防棄用原則落地:①永不空白起手(必選範本);②零術語(文案不出現 node/state/flow/YAML);③當場能試(試跑內建於編輯頁,1.4);④漸進揭露(簡單→進階為選擇性)。

### 1.4 試跑子功能(D5/O1)

- 重用 `frontend/src/api/skills.ts::invokeSkill` + `TraceView.tsx`(既有,顯示 `output.trace`)。
- 簡單模式:「試一下」= 一個範例輸入框 + 執行鈕 → 呼叫 `invokeSkill(name, {query: …})` → 顯示答案(`answerOf` 取 `final_answer`/`answer`…,`WorkflowsView.tsx:30-36` 可直接搬)+ 可展開 TraceView。
- **未存檔的草稿如何試跑?** invoke 只能跑已存在的 skill。v1 採**存後試**:簡單模式「儲存」成功後才啟用「試一下」(草稿即時試跑需引擎支援匿名 invoke,YAGNI,延後)。標記 `ponytail: 存後試,匿名草稿 invoke 等有人要再說`。

### 1.5 範本(P2a,對應 §4.4)

範本 = **兩件事、兩個天然歸屬**,不混成一坨:
- **骨架**(引用節點的 flow)= 引擎契約的一部分 —— 節點名/版本/param 形狀就是 workflow 的 `@node` 契約。它**放引擎旁邊**(§3.0),不進瀏覽器。前端若把骨架寫死,引擎一 rename/bump 節點,前端就會靜默組出過不了驗證的 YAML,錯誤在**存檔當下**炸給非技術使用者看(「node retrieve@1 not found」)—— 正是非技術前提要擋掉的治理外洩。
- **UI metadata**(label、開放欄位白名單、要顯示哪些表單欄位、輸入元件)= 純授權期呈現,**放前端**。

**範本(前端)= 指向內建骨架的薄 metadata**,`frontend/src/skills/templates.ts`,**不再帶任何 flow YAML**:

```ts
interface SkillTemplate {
  id: 'retrieval' | 'compare' | 'stats' | 'infer' | 'inspire'  // 對齊五問題原型 = 聊天路由意圖類別
  basedOn: `template_${SkillTemplate['id']}`  // 指向 workflow 內建骨架的 skill 名(§3.0)
  label: string                     // 「知識問答」…
  openFields: ('name'|'description'|'rule'|'topK'|'sortBy'|'metric'|'period')[]  // 簡單模式可填白名單
  labels: Record<string, string>    // 每個開放欄位的中文標籤/提示
  inputWidgets: Record<string, 'text'|'textarea'|'number'|'select'>  // 該欄位用什麼輸入元件
}
```

前端只知道「用哪支骨架、開放哪些欄位、怎麼呈現」;骨架的實體(含節點名/版本、input_schema)單一事實來源留在 workflow(§3.0)。五支骨架同時是撰寫端與聊天路由端共用的分類錨(五問題原型 = 路由意圖類別),兩邊不各自發明分類。若日後要讓組織自訂骨架 → 內建骨架已是 skill,天然遷移縫是把某支 `template_*` 複製成租戶 custom skill 再開 CRUD(YAGNI,§7)。

**商業邏輯 slot 依原型分兩路(重要)**:`retrieval`/`infer`/`inspire` 的 slot 是 `nl_logic`(NL 指令,LLM 執行期解讀);`compare`/`stats` 因輸出是精確數字(排序 / 聚合算術),slot 預設走 `script`(Python)—— 由 LLM 對多列資料做算術不可靠。故 `stats` 的 metadata:`basedOn: 'template_stats'`、`openFields` 含 `metric`(聚合指標/口徑)、`period`(統計期間)、`topK`(通常較高,讓聚合看到全集),`rule` 欄映射到骨架的 `script` slot(Python body)而非 `nl_logic`。`stats` 與 `compare` 共用同一條「精確→Python」機理與 `script`-path 機器,不新增機制(§3.0)。

### 1.6 組譯 = patch 既有骨架(不生成 flow,對應 §4.3.1)

新增 `frontend/src/skills/compose.ts::compose(template, form, baseDefinition): string`。**它 patch,不 generate:**

1. **取骨架 `definition`(YAML 原文)**:`baseDefinition` 走既有 catalog 路徑 —— `listSkillCatalog()`(`api/skills.ts:12`)的內建項目帶回 `template.basedOn` 那支骨架的原文(§3.0 給 catalog 內建項加 `definition` 一欄,前端本來就在呼叫這支)。前端不自備骨架。
2. **注入規則**:把骨架裡那顆標記過的商業邏輯 slot 的 `__RULE_SLOT__` sentinel 換成 `form.rule` 原文(§3.0 的注入槽約定)。slot 型別由骨架決定,compose 照骨架填:`nl_logic` slot(`retrieval`/`infer`/`inspire`)填 `params.instruction`;`script` slot(`compare`/`stats`,預設精確算術)填 Python body。`nl_logic` 骨架的「進階:改用 Python」toggle 則把該步驟整顆換成 `script`(1.7)。
3. **覆寫白名單開放欄位**:`name`/`description`,以及 `template.openFields` 允許的少數 flow 參數(如 retrieve 的 `top_k`、排序依據);其餘骨架內容原樣保留。
4. **emit** 最終 YAML → 走既有 `/api/skills` 寫入+驗證路徑。

- 因為 patch 的是**已驗證的骨架**,前端從不從零 author 引擎 flow,也不寫死節點名/版本 —— 引擎跨服務契約單一事實來源留在 workflow。
- 組譯後**一律先** `validateSkill(def)`(既有 `api/skills.ts:79`,打 `POST /api/skills/validate`);簡單模式把引擎錯誤碼翻人話(重用 `SkillsTab.tsx:32-40` 的 `CODE_LABEL`,避開行號/術語),進階模式對應到 YAML 行(現制)。
- 存檔:新建 `createSkill(def)`、更新 `updateSkill(name, def)`(既有,body 只有 definition,§SkillDtos)。名稱由後端從 YAML 解析(重複 409),前端不解析第二份事實來源(現制 `api/skills.ts:41-46`)。

### 1.7 線上 Python 編輯器(O5)

- **現況**:前端無 CodeMirror/Monaco(只有 `highlight.js` + `rehype-highlight`,見 package.json;`YamlEditor` 是 textarea + highlight overlay)。
- **O5 定案 CodeMirror 6(視為已授權)**。新增依賴 `codemirror` + `@codemirror/lang-python`(+ 傳遞依賴 `@codemirror/state`/`view`)。**僅在**簡單模式「改用 Python」toggle 與進階模式的 script 卡片載入(動態 import,不進主 bundle 首屏)。
- YAML 那欄**不換** CodeMirror,`YamlEditor`(textarea)夠用,不擴大 diff。
- 標記 `ponytail: CodeMirror 只給 Python,YAML 沿用 textarea;Monaco 過重不採`。

### 1.8 版本控管子功能(D5/O1)

- 完全重用既有:`api/skills.ts::listSkillRevisions` + `SkillsTab.tsx:325-360` 的唯讀歷史區塊(依 revision 遞減、`definition_sha256` 前 12 碼、`<pre>` 對照)。
- 回溯 = 取某版 `definition` 重新 `updateSkill`(產生新版,不改寫歷史)—— 純前端組合現有 API,無後端改動。

### 1.9 前端測試

無專屬 test runner(AGENTS):`npm run lint`(oxlint)+ `npm run build`(tsc + vite)為門檻。「範本能組出引擎可驗 YAML」這個真正有意義的檢查**移到 workflow pytest 就地做**(§3.0/§3.4:五支 `template_*` 載入即編、`__RULE_SLOT__` patch 後仍 valid),因為骨架與節點都在那裡、可 in-process 驗,不在瀏覽器重做一遍。`compose.ts` 只留一支**輕量 patch 形狀自我檢查**(node 腳本 assert:餵一份假骨架 YAML,斷言 `__RULE_SLOT__` 被規則原文取代、白名單欄位有覆寫、非白名單原樣;純函式、不打網路、不呼叫 `validateSkill`)—— 符合 ponytail「非平凡邏輯留一個可跑檢查」。

---

## 2. 後端規格(P4:Configuration Set)

### 2.1 Skill 側:零改動

`SkillUpsert{definition}`(`SkillDtos.cs:36-38`)、`SkillController` CRUD、`skill`/`skill_revision` 表、驗證閘門(`SkillController.cs:155-183`)全部不動。4 分頁在前端組譯,後端只收 YAML —— 這是 D4 的整個重點。

### 2.2 Configuration Set 新資料表(D6/O4/O7)

新表 `configuration_set`(appdb,backend 持有,與 skill 同源同路徑;`DbBootstrap` 冪等建表):

```sql
CREATE TABLE configuration_set (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   text NOT NULL,                        -- 對齊 skill.tenant_id(租戶 code,text)
    name        text NOT NULL,                        -- 組織內具名:「預設」/「高召回」
    is_active   boolean NOT NULL DEFAULT false,       -- 該租戶啟用中的組(至多一)
    values      jsonb NOT NULL DEFAULT '{}',          -- 只存覆寫值,見 §5 鍵集合
    created_by  text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_confset_tenant_name UNIQUE (tenant_id, name)
);
CREATE UNIQUE INDEX uq_confset_active ON configuration_set(tenant_id) WHERE is_active;
```

`uq_confset_active` 部分唯一索引 = 「一租戶至多一組 active」的 DB 級護欄(不靠應用碼把關,對齊 backend「DB constraint over app code」慣例)。

### 2.3 DTO / Controller / Repository

新增 feature 資料夾 `backend/src/Backend.Api/Configuration/`(比照 `Skills/`):

- **DTO**(snake_case,對齊 documents/workflows 契約;`ConfigurationSetDtos.cs`):
  - `ConfigurationSetInfo(id, name, is_active, updated_at)` —— 清單不含 values。
  - `ConfigurationSet(id, name, is_active, values, created_by, created_at, updated_at)` —— 單筆含 values(`values` 為 `Dictionary<string,double>`,見 §5 型別)。
  - `ConfigurationSetUpsert(name, values)` —— 建立/更新 body。`is_active` 不由 upsert 帶(用專屬 activate 端點,避免兩處寫 active 狀態競態)。
- **Controller** `ConfigurationSetController.cs`(`[Route("api/configuration-sets")]`,ADMIN-only,tenant-scoped,比照 `SkillController`):
  - `GET /api/configuration-sets`(清單,USER 可讀? —— **ADMIN-only**,因這是設定管理;掛 `[SkillAdminOnly]` 或新 `[ConfigAdminOnly]`,見下)。
  - `GET /api/configuration-sets/{id}`、`POST`、`PUT /{id}`、`DELETE /{id}`、`POST /{id}/activate`。
  - 每條以 `Request.RequireTenant()` 過濾(現制,`SkillController.cs:44` 模式);跨租戶一律 404,不洩存在性。
  - **values 型別/範圍驗證**:寫入前逐鍵比照 DataAnnotations 檢查(§5 表的型別/範圍);越界 → 422 + ApiError.fieldErrors(現制錯誤形狀)。
- **授權屬性**:現有 `[SkillAdminOnly]`(`SkillController.cs:80` 使用)語意是「ADMIN 才可寫」。Configuration Set 連讀都要 ADMIN,故**讀寫都掛 ADMIN 守衛**。可直接重用 `[SkillAdminOnly]`(名字略偏,但行為正確)或抽為共用 `[AdminOnly]`。ponytail:先重用 `[SkillAdminOnly]`,名字之後嫌髒再抽。
- **Repository**:Dapper 直打(現制,無 ORM);activate = 單一 CTE「先把本租戶所有列 is_active=false,再把目標 =true」原子完成(對齊 skill 寫入的 atomic CTE 慣例)。

### 2.4 Platform 代理(對應 §6.3「代理鏈」)

`platform/src/Platform.Web/Controllers/` 新增 `ConfigurationSetController`(比照 `SkillController.cs` 的 CRUD 代理),`Platform.Service` 新增 `ConfigurationSetService`(比照 `SkillService`,透過 `BackendClient` 打 backend)。JWT 驗證 + `User.ToUserContext()` 轉發身分 header(現制)。角色把關在 backend(現制,platform 只映射錯誤成 ApiError)。

> 讀取路徑(invoke 時取 active set)**不經 platform**:由 workflow 直接向 backend 取(O4b,見 §3.3)。platform 只代理 CRUD。

### 2.5 後端測試

xUnit 手寫 fake(無 mock 庫,現制):
- `ConfigurationSetRepository` 的 CRUD + activate 唯一性(兩次 activate 後只剩一組 active)。
- values 型別/範圍驗證(越界 422、fieldErrors)。
- 租戶隔離(demo-a 看不到 demo-b 的組,跨租戶 GET/PUT → 404)。
- ADMIN 守衛(USER 打任一端點 → 403,且早於模型驗證 —— 比照 `SkillController.cs:9-11` 的 filter 階段順序)。

---

## 3. Workflow 規格

### 3.0 五支 `template_*` 內建骨架 + 注入槽(P2a workflow 側,對應 §4.4)

骨架住在引擎旁邊,不在瀏覽器。`workflow/app/skills/` 新增五支 curate 過的內建 skill,與 `kb_query.yaml` 同機制(啟動即解析→編譯,`skills/__init__.py:50-61`;內建不入 DB,這裡是唯一事實來源):

| 骨架檔                    | 對齊原型 | 骨架管線                                                          | 注入槽型別          |
| ------------------------- | -------- | ----------------------------------------------------------------- | ------------------- |
| `template_retrieval.yaml` | 檢索     | 複用 `kb_query.yaml` flow 形狀(檢索+證據驗證+附出處),尾端接注入槽 | `nl_logic`(NL 指令) |
| `template_compare.yaml`   | 比對     | 檢索多筆 → 注入槽(比較/排序規則)                                  | `script`(精確排序)  |
| `template_stats.yaml`     | 統計     | 檢索(較高 top_k,讓聚合看到全集)→ 注入槽(聚合算術)                 | `script`(精確聚合)  |
| `template_infer.yaml`     | 推論     | 檢索 → 注入槽(LLM 推理)                                           | `nl_logic`(NL 指令) |
| `template_inspire.yaml`   | 啟發     | 檢索 → 注入槽(LLM 綜合)                                           | `nl_logic`(NL 指令) |

- **注入槽約定**:每支骨架含**恰好一顆**商業邏輯步驟,slot 型別依原型分兩路(見上表末欄):
  - `retrieval`/`infer`/`inspire`:slot 是 `nl_logic@1.0` 步驟,其 `params.instruction` 為 sentinel `__RULE_SLOT__`(合法字串,骨架照樣載入/驗證);compose(§1.6)把 sentinel 換成使用者規則原文,或(Python 進階)把該步驟整顆換成 `script`。
  - `compare`/`stats`:因輸出是精確數字(相對排序 / 聚合算術),slot **預設**是既有 `script` 步驟型別(`compiler.py:309-337` → `RestrictedInProcessRunner` 沙箱),其 Python body 為 sentinel `__RULE_SLOT__`(合法 no-op,骨架照樣載入/驗證);compose 把 body 換成使用者的 Python 規則。由 LLM 對多列資料做算術不可靠,故這兩支不預設走 `nl_logic`。
  - 兩路都是:sentinel 以外的節點/參數骨架鎖定,簡單模式使用者碰不到。`stats` 與 `compare` **共用同一條 `script`-path 機器**(`add_script_step`),不新增機制 —— `stats` 只是第二支 `script`-leaning 骨架。
- **骨架 definition 要能被前端取到**:workflow 的 `/skills`(即 `/api/skills/catalog` 來源,`main.py:96-114`)給**內建項**多帶一欄 `definition`(原文;workflow 本來就在啟動時 parse 過骨架,原文現成),platform 原樣代理,前端 `listSkillCatalog()` 已在呼叫。custom 項不必帶(前端不 patch 自訂 skill)。`ponytail: catalog 內建項加一欄 definition,重用既有清單端點,不另開骨架 fetch`。
- **同源同驗**:五支與 `@node` 契約同 repo、同 deploy,pytest 就地驗(§3.4)。引擎日後 rename/bump 節點,骨架與契約一起改 —— 不會像「前端寫死骨架」那樣,等非技術使用者存檔時才炸出 `node retrieve@1 not found`。五支也是撰寫端 ↔ 聊天路由端共用的分類錨(見 [chat-skill-routing 計畫](../chat-skill-routing/01-plan.md))。
- **成本(誠實標註)**:多維護五支內建 skill + 一條 `__RULE_SLOT__` sentinel 約定(`nl_logic` slot 填 `instruction`、`script` slot 填 Python body,同一個 sentinel 名);換來骨架同源、in-process 可驗、撰寫↔路由共用同一套分類。`retrieval`/`infer`/`inspire` 的注入槽用 `nl_logic@1.0`,故此交付**依賴 P3**(節點不存在則骨架載入即炸);`compare`/`stats` 的 `script` slot 走既有步驟型別,不依賴 P3。

### 3.1 `nl_logic` 節點(P3,對應 §5,唯一動到引擎處)

新檔 `workflow/app/nodes/nl_logic.py`,`@node` 註冊(比照 `nodes/retrieve.py`):

```python
@node(
    name="nl_logic", version="1.0",
    description="以自然語言 instruction 當商業邏輯,執行期呼叫 LLM 解讀並寫回結果",
    reads=[],                       # 固定讀取鍵無;要讀的 state 鍵由 params.input_keys 指定
    dynamic_reads=["input_keys"],   # 比照 retrieve 的 query_key:params 指定要餵 LLM 的 state 鍵
    writes=["business_result"],     # 預設寫回鍵;output_key 若改,見下方說明
    deps=["llm"],                   # 重用 StructuredLLMPort;compiler 會把 llm 版本記進 trace
    requires_tools=[],
)
def make_nl_logic_node(llm, *, instruction: str, input_keys=(), output_key="business_result"):
    async def nl_logic(state): ...   # instruction 當 system,選定 state 鍵組 user,llm.structured(...)
    return nl_logic
```

- **params 傳遞路徑已驗證可行**:`compiler.add_node_step`(`compiler.py:274-291`)呼叫 `spec.build(self.deps, **params)`,`params = step.get("params")`(`compiler.py:361`)。故 `instruction`/`input_keys`/`output_key` 由 YAML `params:` 直接進 factory kwargs —— 與 `retrieve` 完全同機制,無需改編譯器。
- **`output_key` 可變 vs `writes` 靜態的張力**:`@node` 的 `writes` 是靜態宣告,而 `output_key` 是每步驟參數。Harness 執行後**剝除未宣告的 writes 鍵**(`node_registry.py:56-59` 一帶),若 `output_key` != `business_result` 會被剝掉。**v1 簡化:鎖死 `output_key='business_result'`,不開放改**(簡單模式組譯固定用它,§1.6)。要支援可變 output_key 需引擎支援「動態 writes」,YAGNI。標記 `ponytail: output_key 先鎖 business_result;動態 writes 之後再談`。
- **Model**:`llm` 依賴由 deps 注入(§3.3),模型 = `settings.llm_model`,溫度 = `llm.py` 現值 0.7(v1 促升為可調,§5)。因 `deps` 含 `llm`,`compiler.py:287` 會把 `llm_version` 寫進 trace(稽核可見用哪個模型)。
- **治理**:一律經 Harness(trace/逾時/I/O 契約/fatal 短路),與其他節點同規格;無密鑰進節點(現制)。
- **註冊掛載**:`main.py:16-17` 已 import `app.nodes.retrieve` 觸發 `@node`;加一行 `from app.nodes import nl_logic as _nl_logic  # noqa: F401` 讓 `GET /nodes` 與編譯器查得到。
- **測試**(pytest):`nl_logic` 單元(mock LLM,驗 instruction→system、input_keys→user、寫回 business_result);一個「NL skill 端到端 invoke」(組一份含 nl_logic 的 skill → invoke → 斷言 output 有 business_result + trace 有該節點 + component_version 非空)。

### 3.2 友善外殼如何組成 node-first YAML

無需 workflow 端任何改動 —— 組譯全在前端(§1.6),產出的是普通 skill YAML,走既有 `validate_source`(`skill.py:458`)/ `compile`(`compiler.py:538`)。`nl_logic` 一旦註冊,它對驗證器/編譯器就是「又一個節點」,`_check_node`(`skill.py:254`)的 `dynamic_reads` 檢查(`skill.py:277-290`)會自動涵蓋 `params.input_keys`。

### 3.3 Configuration Set 執行時套用(O4b,§6.3 關鍵串接)

**現況**:設定只在**啟動**讀 env(`settings.py`);`custom.deps()`(`custom.py:44-52`)是**模組單例**,`_default_deps()`(`kb_query.py:35-52`)一次性從 `settings` 組出 `KbQueryDeps`(內含 `default_top_k`/`max_retrieval_attempts`);`llm.py::get_llm` 是 `lru_cache(maxsize=1)` 單例(model/temperature 固定)。

**改為(O4b (i):workflow 於 invoke 向 backend 取 + 依租戶/版本快取):**

1. invoke 時(`main.py:134-212`),取當前租戶的 active Configuration Set:向 backend 加一支 `GET /api/skills/../configuration-sets/active`(或直接 `GET /api/configuration-sets?active=1`,單筆);比照 `custom._fetch`(`custom.py:65-77`)帶內部 token + 身分 header。取不到(無 active 組)→ 用空 values(全走全域預設)。
2. **有效設定** = 全域預設(`settings` / 節點內建值)← 疊上 active set 的 `values`(租戶覆寫)。這正是 D8 的兩層:全域預設(未來系統 Admin 管)← 租戶覆寫(組織 Admin 管)。
3. 用有效設定**建一個 per-config 的 deps 容器**(而非改模組單例):`KbQueryDeps` 的 `default_top_k`/`max_retrieval_attempts` 取覆寫值;`llm` 依賴用覆寫的 `llm.model`/`llm.temperature` 現建一個 `LangChainStructuredLLM`(繞開 `get_llm` 的 maxsize=1 單例)。
4. **快取對齊既有機制,免重編**:`compiler.compile` 的快取鍵含 `id(deps)`(`compiler.py:540`)。同一租戶+同一 config 版本 → 同一 deps 實例 → 命中既有圖快取;config 換版 → 新 deps 實例 → 重編一次。故只需**依 (tenant_id, config_version) 快取 deps 容器**(新增小 dict),圖快取自動跟上。config_version 取 Configuration Set 的 `updated_at` 或內容雜湊。
5. **注入點**:`main.py:187` 目前 `state = {"tenant_id":…, **input}`,deps 藏在 `loaded.deps`(builtin)或 `custom.load` 內(`custom.py:139` `container = deps()`)。改為:invoke 時解析有效設定 → 取/建 per-config deps → 傳給 `custom.load(name, ctx, deps=…)` 與 builtin 的 compile。

**已知縫(誠實標註,對應記憶「skill-engine-governance-invariants」):**
- `retrieve.py:49` 的 `settings.retrieval_top_k` 是**模組全域讀取**,不經 deps。要讓它 per-tenant,需(a)範本在 flow 對 `retrieve` 步驟明寫 `params:{top_k:…}`(組譯期把覆寫值填進去,零引擎改動),或(b)把 retrieve 改讀注入的有效設定(較大改動)。**v1 採 (a)**:kb_query 用的是 `kb_query_top_k`(走 KbQueryDeps),真正吃 `retrieval_top_k` 的通用 retrieve 由範本顯式帶 top_k。標記 `ponytail: retrieval_top_k 由範本顯式帶,通用 retrieve 讀全域先不動`。
- `intent_classification.py:91` 的信心門檻 `0.6`、`llm.py:19` 的 `0.7`:v1 促升為 settings/deps 欄位(§5)。intent 門檻經 `KbQueryDeps`(需在 `intent_classification` factory 加一個 threshold 參數 + deps 欄位);溫度經 per-config 建 LLM 時帶入。
- deps 快取無上限會漲:比照圖快取 `_CACHE_MAX=32` FIFO(`compiler.py:526`),per-config deps 快取也給一個小上限。`ponytail: FIFO 小上限,租戶數量級是幾十`。

### 3.4 Workflow 測試(pytest)

- 五支 `template_*` 骨架:啟動載入即編(不炸)+ 把 `__RULE_SLOT__` patch 成一句規則(`nl_logic` slot)/一段 Python(`compare`/`stats` 的 `script` slot)後 `validate_source` 仍 valid + invoke 得到 `business_result`(印證骨架 = 引擎可驗、注入槽可用)。這是「範本能組出引擎可驗 YAML」的就地家(§1.9 移到此)。
- `nl_logic` 單元 + NL skill 端到端(§3.1)。
- Configuration Set 套用:給定覆寫 values,斷言 per-config deps 反映覆寫(top_k/model/temperature/intent 門檻);同 config 版本第二次 invoke 命中圖快取(不重編,比照 AT2-28 計數點 `compiler.py:465`);換版重編。
- 無 active 組 → 全走全域預設(回歸:kb_query 行為與現況一致)。
- 租戶隔離:A 租戶的 config 不影響 B 租戶的 invoke。

---

## 4. Configuration Set 值集合與分層(對應 §6.1–6.3 / O3)

### 4.1 v1 開放鍵(O3:5 既有 settings + 促升 2 個寫死值)

| `values` 鍵                         | 全域預設(來源)                      | 型別/範圍                        | 執行套用點                              |
| ----------------------------------- | ----------------------------------- | -------------------------------- | --------------------------------------- |
| `retrieval.top_k`                   | 4(`settings.retrieval_top_k`)       | int 1–50                         | 範本顯式帶進 retrieve params(§3.3 縫 a) |
| `kb_query.top_k`                    | 8(`settings.kb_query_top_k`)        | int ≥1                           | `KbQueryDeps.default_top_k`             |
| `kb_query.max_retrieval_attempts`   | 2                                   | int ≥1                           | `KbQueryDeps.max_retrieval_attempts`    |
| `workflow.timeout_seconds`          | 120                                 | int ≥1                           | `main.py:188` timeout(改讀有效設定)     |
| `llm.model`                         | `gpt-4o-mini`(`settings.llm_model`) | str(白名單 = LiteLLM 已配置模型) | per-config 建 LLM                       |
| `intent.confidence_threshold`(促升) | 0.6(`intent_classification.py:91`)  | float 0–1                        | `KbQueryDeps` 新欄位 → factory          |
| `llm.temperature`(促升)             | 0.7(`llm.py:19`)                    | float 0–2                        | per-config 建 LLM                       |

- **只存覆寫值**;未覆寫鍵回落全域預設。`values` jsonb 存數值(model 除外為字串 —— DTO 的 `values` 型別需容納 string|number,以 `Dictionary<string, JsonElement>` 或分兩欄承載;ponytail:先 `Dictionary<string,object>` + 逐鍵型別驗證,一個 jsonb 欄不拆)。
- **明確不做(§6.2 v2 / §9)**:rerank 加權、變體數上限、容差、locator 權重(v2);詞彙/口徑/意圖對照/公式等「規則資料」的 UI 化(另立計畫)。

### 4.2 兩層覆寫(D8,留系統 Admin 位不實作)

```
全域預設(節點內建 / settings)   ← 未來系統 Admin 管(本計畫不建,僅留位)
        ↑ 疊
租戶 active Configuration Set    ← 組織 Admin(= 現行 ADMIN)管
```

- 執行時「租戶覆寫疊在全域預設上」(§3.3 步驟 2)。系統 Admin 那層 v1 就是「程式內建的全域預設」,未來要讓它可改再加一張「全域預設表」+ `SYSTEM_ADMIN` 角色 —— 現行程式**不寫任何跨租戶捷徑**(所有查詢帶 tenant_id),為未來留路不擋路。

---

## 5. 多租戶落實點(D8 貫穿)

| 面向                  | 隔離維度                          | 落實                                                                                                          |
| --------------------- | --------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| Skill                 | `skill.tenant_id`(現況 ✓)         | `RequireTenant()` 過濾,跨租戶 404(`SkillController.cs:44`)                                                    |
| Configuration Set     | `configuration_set.tenant_id`(新) | 同上;`uq_confset_active` per-tenant;controller 每條帶 tenant                                                  |
| 執行取值              | invoke 時 ctx.tenant_id           | workflow 向 backend 取 active set 帶 `X-Tenant-Id`(`custom._headers` 模式);per-config deps 快取鍵含 tenant_id |
| 一般設定 `app_config` | **全域(無 tenant 欄)**            | O6 定案**不動**;若日後要 per-tenant 另立小計畫加欄                                                            |
| Admin 分層            | 組織 Admin = ADMIN + tenant 隔離  | 系統 Admin 僅留位;不寫跨租戶捷徑                                                                              |

跨租戶零可見零可改:任何 config 讀寫都經 tenant 過濾;deps 快取以 tenant 分槽,杜絕「A 租戶設定污染 B 租戶執行」(比照 `custom.py:1-13` 對快取毒化的顧慮)。

---

## 6. 分階(映射 §8,標最小切片)

| 階段                     | 內容                                                                                                                                                                                                                                                                                                         | 服務                               | 依賴                                                                                                                      |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| **P1 前端 IA**           | AppShell 移除 workflows 選單/View/action/文案;ConfigView 三分頁;Skill 功能樹(清單+進階模式=重用 SkillsTab)+ 試跑(TraceView)+ 版本(revisions)接為子功能                                                                                                                                                       | frontend                           | 無                                                                                                                        |
| **P2a 範本**             | **workflow**:curate 5 支 `template_*` 內建骨架(含 `__RULE_SLOT__` 注入槽;`retrieval`/`infer`/`inspire` = `nl_logic` slot,`compare`/`stats` = `script` slot)+ catalog 內建項帶 `definition`;**frontend**:5 支薄 metadata(`templates.ts`:basedOn/openFields/labels/inputWidgets)+ compose(patch)(`compose.ts`) | workflow + frontend                | 引擎既有節點(檢索/驗證/`nl_logic`/`script`);`nl_logic` slot 那三支 → 依賴 P3,`compare`/`stats` 的 `script` slot 不依賴 P3 |
| **P2b 簡單模式**         | `SimpleSkillEditor` + 範本挑選 + 名稱/描述/我的規則(NL 預設)+ 組譯(`compose.ts` patch 骨架)+ validate 翻人話 + 存後試                                                                                                                                                                                        | frontend                           | P2a、P3(NL 路徑)                                                                                                          |
| **P2c 進階模式**         | 已由 P1 重用 SkillsTab 覆蓋;剩「簡單→進階」單向切換 + Python(CodeMirror,O5)                                                                                                                                                                                                                                  | frontend                           | P2b                                                                                                                       |
| **P3 nl_logic 節點**     | `nodes/nl_logic.py` + 註冊 + pytest;NL 路徑打通                                                                                                                                                                                                                                                              | workflow                           | 無                                                                                                                        |
| **P4 Configuration Set** | 新表 + backend CRUD/activate + platform 代理 + workflow invoke 取值/per-config deps + 促升 2 值 + `NodeParamsTab` 表單                                                                                                                                                                                       | backend/platform/workflow/frontend | 獨立線                                                                                                                    |

- **最小可用切片 = P1 + P2a + P2b + P3**:交付「非技術使用者靠範本建 Skill、寫中文規則、存後當場試跑」的完整價值。P2c(Python/進階切換)服務技術使用者,可延後;**P4(Configuration Set)是另一條較重的線,可獨立排期**(跨四服務、新表、tenant 隔離,§8 標高風險)。
- 每階段跑既有測試門檻 + e2e 回歸:聊天 SSE / 文件 202 / 既有 skill invoke 契約不得破(§9)。

---

## 7. 明確不做(YAGNI,承 §9)

- 不退回 flow/logic/script 三欄位舊模型(引擎不動)。
- 不做視覺化拖拉流程編排(進階模式 = 步驟清單 + YAML)。
- 簡單模式不讓非技術使用者碰流程/節點/YAML(骨架代勞);**組織自訂骨架延後** —— 但骨架 v1 已是 workflow 內建 skill,未來要開放時的遷移縫天然存在(把某支 `template_*` 複製成租戶 custom skill 再加骨架 CRUD),不需重寫。
- 不 UI 化詞彙/口徑/意圖對照/公式等規則資料(另立計畫)。
- 不改公開聊天 API / SSE / AG-UI 契約。
- 不建系統 Admin 層(只留設計位:全域預設 = 程式內建,未來加表 + `SYSTEM_ADMIN`)。
- `nl_logic` 的 `output_key` v1 鎖死 `business_result`(動態 writes 延後)。
- 草稿匿名試跑延後(v1 存後試)。
- 一般設定 `app_config` 不 per-tenant 化(O6)。

---

## 8. 觸點速查(file:line)

**frontend**
- `components/AppShell.tsx:11,15,17,19-25,107-121,203,219` — 移除 workflows(View/VIEWS/NAV/import/render/switchView/copilot 文案)
- `components/ConfigView.tsx:3,15-20,127-181,207-208` — 三分頁改造、移除 WorkflowsConfigTab
- `components/SkillsTab.tsx`(全)— 進階模式重用;`32-40` CODE_LABEL 翻人話重用;`325-360` 歷史重用;`403-445` 三欄重用
- `components/WorkflowsView.tsx:30-36,48` — 搬 answerOf / 試跑邏輯後刪檔
- `components/TraceView.tsx`、`api/skills.ts`(invokeSkill/validateSkill/createSkill/updateSkill/listSkillRevisions)、`components/NodeCatalog.tsx` — 重用
- 新:`skills/templates.ts`(薄 UI metadata:basedOn/openFields/labels/inputWidgets,**無 skeleton**)、`skills/compose.ts`(**patch 既有骨架**,不生成 flow)、`components/SimpleSkillEditor.tsx`、Python 用 CodeMirror(動態 import)
- `api/skills.ts:12` `listSkillCatalog` — 重用(compose 取骨架 `definition`);`types.ts:118-125` `SkillCatalogEntry` 加 `definition?`(內建項才有)

**backend**
- `Skills/*` — 不動(SkillUpsert 只有 definition,`SkillDtos.cs:36`)
- 新:`Configuration/ConfigurationSetDtos.cs`、`ConfigurationSetController.cs`(`[Route("api/configuration-sets")]`,重用 `[SkillAdminOnly]`)、`ConfigurationSetRepository.cs`;`DbBootstrap` 加建表
- 模式參照:`Skills/SkillController.cs:44,80,107,138,155`(RequireTenant/ADMIN 守衛/驗證閘門/atomic CTE)

**platform**
- 新:`Platform.Web/Controllers/ConfigurationSetController.cs`、`Platform.Service/ConfigurationSetService.cs`(比照 `SkillController.cs` / `SkillService`,只代理 CRUD)

**workflow**
- 新:`app/skills/template_{retrieval,compare,stats,infer,inspire}.yaml`(五支內建骨架,各含一顆 `__RULE_SLOT__` 注入槽:`retrieval`/`infer`/`inspire` 是 `nl_logic` slot、`compare`/`stats` 是 `script` slot;`skills/__init__.py:50-61` 啟動自動載入,無需改碼)
- `main.py:96-114` `/skills`(catalog 來源)內建項加 `definition` 一欄(原文,前端 patch 用)
- 新:`app/nodes/nl_logic.py`(`@node`,deps=["llm"],dynamic_reads=["input_keys"]);`main.py:16-17` 加 import
- `main.py:134-212` — invoke 加「取 active config → per-config deps → 注入」;`:187-188` state/timeout 改讀有效設定
- `skills/custom.py:44-52,116-151` — `deps()` 改為可帶 per-config 容器;`load(name, ctx, deps=…)`
- `workflows/kb_query.py:35-52` — `_default_deps` 參數化(接受覆寫值)
- `kbquery/nodes/intent_classification.py:80-93` — threshold 由 factory 參數/deps 帶入(促升 0.6)
- `llm.py:15-20` — per-config 建 LLM 時帶 model/temperature(繞 maxsize=1 單例)
- `settings.py:10,19,25-26` — 全域預設來源;`compiler.py:526,540` — deps/圖快取機制(不改,靠 id(deps) 分槽)

---

## 9. 測試策略總表

| 服務     | 工具                                                             | v1 重點                                                                                                                                                                                                                                              |
| -------- | ---------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| frontend | oxlint + tsc/vite build;`compose.ts` 一支輕量 patch 形狀自我檢查 | patch 假骨架後 `__RULE_SLOT__` 被規則取代、白名單欄位覆寫、非白名單原樣;lint+build 綠(「組出的 YAML 可驗」移到 workflow 就地驗)                                                                                                                      |
| backend  | xUnit 手寫 fake                                                  | configuration_set CRUD/activate 唯一性/租戶隔離/values 型別範圍/ADMIN 守衛早於模型驗證                                                                                                                                                               |
| workflow | pytest                                                           | 五支 `template_*` 載入即編 + `__RULE_SLOT__` patch 後仍 validate=valid(`nl_logic` slot 填 NL、`compare`/`stats` 的 `script` slot 填 Python);nl_logic 單元 + NL skill e2e;config 套用(覆寫反映於 deps)+ 快取命中/換版重編 + 無 active 回落 + 租戶隔離 |
| 跨鏈     | e2e-verifier(docker compose)                                     | 聊天 SSE / 文件 202 / 既有 kb_query·rag_qa invoke 契約不破                                                                                                                                                                                           |
