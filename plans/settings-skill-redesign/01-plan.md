# 計畫書 — 系統設定重構 × 4 分頁 Skill 編輯器 × 節點參數設定

> 狀態:**規劃中,尚未動碼**(使用者指示「先規劃,不要執行」)。
> 前置脈絡:現行後端已是 node-first Skill 引擎(Skill = 單一 YAML `definition`,引擎驗證/編譯/沙箱/revision,已 e2e 全綠)。本計畫**只重設計前端資訊架構與撰寫體驗,並最小幅度擴充引擎**,不退回舊 flow/logic/script 三欄位模型。

## 1. 使用者需求(逐條)

1. 功能選單(側欄)**移除「工作流」與「Skill」** 兩個頂層項。
2. Skill 的編輯移進**「系統設定」**。
3. Skill 編輯用**頁籤(Tab)分成四塊**:名稱 / 描述 / 工作流程 / 商業邏輯。
4. **商業邏輯** 可為**自然語言**,或選 **Python**;選 Python 需提供**前端線上 Python 編輯器**。
5. 「系統設定」的**兩大核心** = Skill + **部分工作流節點的參數調整設定**。

### 已拍板決策
- **D1**(可編輯範圍):系統設定的 Skill 清單只列**可編輯的 Skill**(自訂 Skill + 內建 kb_query);純 code 工作流不進此清單。
- **D2**(NL 商業邏輯語意):自然語言商業邏輯**接一個新的 LLM 節點在執行時解讀執行**(功能完整,需擴充引擎)。
- **D3**(節點參數推進方式):由本規劃**先讀節點契約、提可調參數清單**(見第 6 節)供選定。
- **D4**(架構取捨):4 分頁只是**撰寫外殼**,存檔時組合成現有 node-first YAML `definition`;引擎資料模型不動。
- **D5**(O1):**執行/試跑** 與 **版本控管** 是「Skill 編輯」功能樹底下的**子功能**,不是獨立頁。版本控管接現有 `skill_revision`(node-first 已具備)。
- **D6**(O4):節點參數以**具名的 Configuration Set** 管理,**tenant-scoped**;不同組織有各自的參數組(一租戶可多組、擇一啟用)。需專屬 schema,非命名空間化 app_config 可承載。
- **D7**(IA 概念):**Skill 是一棵完整功能樹**(編輯／執行試跑／版本…);功能選單的定位是**「相關功能樹的群組」**,不是把單一葉功能平鋪。
- **D8**(跨切面):**多租戶徹底落實**(見 §2.5);**Admin 分兩層** —— 組織 Admin(現行 ADMIN)與系統 Admin(未來才建,本計畫僅**留設計位**,不實作)。

## 2. 架構總判斷:4 分頁 = 友善外殼,底層仍是 node-first YAML

```
系統設定 › Skill 編輯（4 分頁）
  名稱 ─────────────┐
  描述 ─────────────┤  存檔時組譯成一份 YAML definition
  工作流程（flow）───┤  → POST/PUT /api/skills（既有端點、既有引擎驗證）
  商業邏輯（script / nl_logic 節點）┘
```

- 名稱 → YAML `name`;描述 → `description`;工作流程 → `flow:`(node/branch/loop/sequence 步驟);商業邏輯 → flow 內的 `script` 步驟(Python)或 `nl_logic` 節點(自然語言)。
- **保留「進階 / 原始 YAML」逃生口**:進階使用者仍可直接編輯整份 definition(重用現有 `YamlEditor`)。4 分頁負責常見情境,不取代 YAML 的表達力。
- 好處:`skills.ts` / 引擎 / validate / invoke / revision / 匯出**全部不動**,只換前端撰寫方式。

## 2.5 跨切面原則:多租戶與 Admin 分層(D8,貫穿全計畫)

**多租戶徹底落實**:所有可變資料以 `tenant_id` 為隔離維度,跨租戶零可見零可改。
- Skill:`skill`/`skill_revision` 已帶 `tenant_id`(現況 ✓)。
- 節點參數:以 Configuration Set 承載,tenant-scoped(§6.3 新 schema)。
- **既有 `app_config` 是全域(無 tenant 欄)**——「一般設定」目前所有租戶共用。若組織級一般設定也要 per-tenant,需 schema 變更;**標記為待評估**(§7 O6),本計畫不強制改動一般設定的租戶模型。

**Admin 分兩層**(現在只做第一層,第二層留位):
| 層級 | 現況 | 管轄 |
|---|---|---|
| **組織 Admin** | = 現行 `ADMIN` 角色(X-Tenant-Id 隔離) | 自己租戶的 Skill、Configuration Set、組織設定 |
| **系統 Admin** | **未規劃、不建置**(未來) | 跨租戶、全系統**預設值**、所有組織治理 |

設計留位(不實作,但不擋路):
- 值分兩層 —— **全域預設值**(未來系統 Admin 管)← **租戶覆寫值**(組織 Admin 管);執行時「租戶覆寫疊在全域預設上」。節點參數的內建預設即全域預設,Configuration Set 即租戶覆寫。
- 角色模型預留未來的 `SYSTEM_ADMIN`;現行程式一律以「組織 Admin = ADMIN + tenant 隔離」為邊界,不寫任何跨租戶捷徑。

## 3. 資訊架構變更(前端)

### 3.1 側欄
- 目前 `NAV`(AppShell.tsx:19):chat / documents / **workflows** / analysis / config。
- 變更:**移除 `workflows` 項**。`WorkflowsView` 目前承載三件事,各自去處:
  | 現有(workflows 視圖) | 去處 |
  |---|---|
  | Tab2 Skill 管理(SkillsTab) | → 系統設定 › Skill(改成 4 分頁) |
  | Tab3 節點目錄(NodeCatalog 唯讀) | → 併入 Skill 編輯的「工作流程」分頁左欄(插入節點用);唯讀瀏覽亦可留在系統設定 |
  | Tab1 執行工作流 + skill invoke + Trace | → **收進 Skill 功能樹的「執行/試跑」子功能**(D5,§4.0);重用 `TraceView` |

> D5 定案:試跑不再是獨立入口,而是「Skill 編輯」功能樹的子功能。純 code 工作流(summarize 等 4 個)的執行入口不在本計畫範圍(它們仍可經聊天/agent 觸發)。

### 3.2 系統設定(ConfigView)頂層改為三分頁
```
系統設定
├── Skill          ← 核心一:Skill 功能樹（清單 + 雙門編輯器 + 試跑 + 版本，見 §4）
├── 工作流節點參數  ← 核心二:節點可調參數表單（見 §6）
└── 一般設定        ← 既有 key/value 表，原樣保留
```
- 角色:整頁維持 ADMIN-only(側欄過濾 + 後端把關,現狀不變)。

## 4. Skill 功能樹(D5/D7)

「Skill 編輯」不是一個編輯器,而是一棵**功能樹**;功能選單把它當「相關功能樹的群組」呈現:

```
系統設定 › Skill(功能樹群組)
└── 選定一個 Skill
    ├── 編輯      ← 4 分頁:名稱 / 描述 / 工作流程 / 商業邏輯(§4.2)
    ├── 執行/試跑  ← 子功能(D5/O1):填 input → invoke → TraceView 顯示 output.trace
    └── 版本控管  ← 子功能(D5/O1):skill_revision 唯讀歷史 + 版本對照/回溯
```

- 三個子功能共享「當前選定的 Skill」;租戶隔離貫穿(D8)。
- 版本控管接**現有** `skill_revision`(每次 PUT 一筆、帶 `definition_sha256`),前端只需清單 + 唯讀 diff;回溯 = 取某版 definition 重新 PUT(產生新版,不改寫歷史)。

### 4.1 清單 → 選定
- 清單只列可編輯 Skill(D1):欄位 名稱 / 描述 / 角色 / 來源徽章(builtin·custom)/ rev / 狀態 / 操作。
- 點一列 → 進該 Skill 的功能樹(預設「編輯」子功能);內建 kb_query 以唯讀檢視呈現(其定義是 repo 檔案,非 DB,CRUD 不可改)。

### 4.2 設計前提:使用者多為非技術人員(決定 UX 走向)

撰寫者很多是**非技術人員**。若編輯體驗要求他們理解「流程/節點/state/YAML」,他們就不會用,功能樹形同廢棄。因此編輯採**雙門分層**:非技術使用者不編排流程,只**挑範本 + 填自己的規則 + 當場試跑**;技術使用者才進得到流程/YAML。兩道門編輯**同一份 Skill**,資料層不分岔。

### 4.3 雙門編輯

**簡單模式(預設,非技術使用者看到的全部)**
```
┌ 新增 / 編輯 Skill ────────────────────────────┐
│ 從範本開始:  ○ 知識問答  ○ 純計算/判斷  ○ 文件分析 │
│ 名稱   [__________]   說明 [__________]         │
│ 我的規則(用中文寫就好):                        │
│  [ 找不到明確數字時就說查無資料,不要猜。       │
│    回答一定要附上來源文件名稱。 ]               │
│  [進階:改用 Python 撰寫]  ← 技術者才點          │
│ 試一下: [範例輸入______] [執行] → 顯示答案(可展開 trace) │
│                          [儲存]  [進階編輯]     │
└───────────────────────────────────────────────┘
```
- **零術語、零流程圖、零 YAML、零 state 鍵**。UI 只出現「範本 / 名稱 / 說明 / 規則 / 試一下」。
- 「我的規則」預設走**自然語言**(→ `nl_logic` 節點);Python 藏在「進階」toggle(→ `script` 步驟,CodeMirror)。
- 範本提供管線(檢索/驗證等),使用者只填名稱/說明/規則 + 範本開放的少數參數(見 §4.4)。

**進階模式(技術使用者才進的另一道門)**
- 單一 flow 步驟編輯器(typed step 卡片:內建節點 / 分支 / 迴圈 / Python / 自然語言,可插任意位置)+ 原始 YAML 無損切換。能表達引擎**全部**能力。
- 由簡單模式「進階編輯」進入。非技術使用者永不需看到。

**四條防棄用原則**(對應非技術使用者痛點):①永不從空白開始(一律範本起手);②零術語;③當場能試(試跑內建於編輯頁);④漸進揭露(簡單→進階為選擇性深入,非必經)。

### 4.3.1 組譯(兩道門都輸出同一份 YAML)
```yaml
name: <名稱>            # 簡單模式:名稱欄;新建可填,既有唯讀(改名需刪重建)
description: <說明>
required_role: USER
input_schema: { ... }   # 由範本提供預設;進階模式可改
flow:
  <範本提供的管線步驟…>            # 簡單模式使用者看不到、不編輯
  - node: nl_logic@1.0            # 「我的規則」= 自然語言
    params: { instruction: "<規則原文>", output_key: business_result }
  # 或 「我的規則」= Python:
  # - script: "<CodeMirror 內容>"
```
- 簡單模式:範本決定管線與邏輯注入點,使用者的規則注入範本指定的 slot(見 §4.4);進階模式:使用者自行編排整個 flow。
- 存檔前一律走 `POST /api/skills/validate`(引擎為唯一事實來源);簡單模式把引擎錯誤翻成人話(避開行號/術語),進階模式對應到步驟卡/YAML 行。

### 4.4 範本(Template)—— 方案能否落地的關鍵資產

範本是「預先組好、留下填空」的 Skill 骨架。**由技術方先種**,之後所有非技術使用者靠它起手。

**範本對齊使用者在聊天中真正會問的五類問題原型**(複雜度遞增;見專案記憶 non-technical-users-chat-first):

| 範本 | 使用者問句樣態 | 管線(隱藏) | 開放給使用者填 | 對應形狀 |
|---|---|---|---|---|
| **檢索** | 「X 出現在哪份文件 / 查一下 X」 | 檢索 + 證據驗證 + 附出處回答(kb_query 形狀) | 名稱 / 說明 / (選)我的規則 / (選)檢索筆數 | 檢索管線 + 選用邏輯 slot(`nl_logic`) |
| **比對** | 「比較/排序 A、B、C(YoY、績效排名)」 | 檢索多筆 + 比較/排序(具名項的相對次序) | 名稱 / 說明 / **比較規則** / (選)排序依據 | 檢索 + `script`(比較排序需精確,預設 Python) |
| **統計** | 「全年總額 / 平均 / 某指標的計數・分佈」 | 檢索(較高 top_k,拉全集)+ script 聚合 | 名稱 / 說明 / **統計規則(聚合)** / (選)統計指標 / (選)統計期間 | 檢索 + `script`(聚合算術需精確,預設 Python) |
| **推論** | 「假設 X,後續會怎樣」 | 檢索 + LLM 推理 | 名稱 / 說明 / **推論規則** | 檢索 + `nl_logic`(推理) |
| **啟發** | 「這個場景,給我 insight」 | 檢索 + LLM 綜合 | 名稱 / 說明 / **啟發角度** | 檢索 + `nl_logic`(綜合) |

- **比對 vs 統計是兩件事**:比對 = 具名項之間的相對次序/排名(YoY、績效排名),輸出是「排序」;統計 = 對整個集合/期間做聚合(總額/平均/計數/分佈),輸出是「聚合數字」,通常不指名項目相互比較。
- 檢索最接近純管線(自訂邏輯選用);推論/啟發本質是 LLM 推理,重度依賴 `nl_logic`(印證 D2)。**比對與統計因輸出是精確數字,預設走 Python(`script`)** —— LLM 對多列資料做排序/算術不可靠;兩者共用同一條「精確→Python」機理,統計只是第二支 `script`-leaning 範本。
- 五個原型同時也是「聊天 → skill 路由」(見 [chat-skill-routing 計畫](../chat-skill-routing/01-plan.md))要分辨的意圖類別 —— 撰寫端與路由端共用同一套分類,不各自發明。

範本規格 = **兩個天然歸屬**(骨架 = 引擎契約,metadata = 授權期呈現):
- **骨架**(flow + input_schema + 一顆明確的「規則注入 slot」;`nl_logic` slot 或 `compare`/`stats` 的 `script` slot)= 節點名/版本就是 `@node` 契約 → **放 workflow**:`workflow/app/skills/template_*.yaml` 五支 curate 過的內建 skill(比照 `skills/kb_query.yaml`,與節點契約同源同 deploy、pytest 就地驗)。
- **UI metadata**(開放欄位白名單、標籤、輸入元件)= **放前端**常數,指向骨架名(`basedOn: template_*`)。
- 前端**不帶骨架 flow**:它 patch workflow 取回的既有骨架(換掉 slot 的規則、覆寫白名單欄位),不從零 author 引擎 flow —— 節點名/版本這個跨服務契約不被複製進瀏覽器,引擎 rename/bump 節點不會讓前端靜默組出過不了驗證的 YAML。
- 未來要讓組織自訂骨架再議(YAGNI):骨架已是 skill,遷移縫天然存在(複製成 custom skill + CRUD)。

> 範本讓「工作流程」對非技術使用者隱形,且直接對應他們會問的問題 —— 這是本方案相對原始 4 分頁最關鍵的差異。

## 5. 引擎擴充:`nl_logic` LLM 節點(對應 D2)

新增一個節點,讓自然語言商業邏輯可執行。**這是本計畫唯一動到 workflow 引擎的地方**。

- 位置:`workflow/app/nodes/nl_logic.py`(全域節點家),`@node` 註冊。
- 契約(參 `node_registry.py` 的 `@node`):
  - `params`:`instruction`(NL 原文,必填)、`input_keys`(要餵給 LLM 的 state 鍵清單,選填)、`output_key`(寫回鍵,預設 `business_result`)。
  - `reads`:`input_keys`(動態,參照 retrieve 的 `dynamic_reads` 模式);`writes`:`[output_key]`。
  - `deps`:`llm`(重用 `StructuredLLMPort` / `app/llm.py` 的 client,model 取 `settings.llm_model`)。
- 行為:以 `instruction` 為 system prompt、選定 state 鍵組成 user 內容,呼叫 LLM(比照 `query_rewrite`/`intent_classification` 的 `llm.structured(...)`),結果寫回 `output_key`。
- 治理:一律走 Harness(trace / 逾時 / I/O 契約 / fatal 短路),與其他節點同規格;無密鑰進節點。
- **待驗證的實作細節**:node 步驟的 `params` 如何在執行期傳進節點函式(retrieve 的 `query_key` 走 params + `dynamic_reads`,`nl_logic` 沿用同機制)—— 實作時先確認 `compiler.py` 對 node `params` 的傳遞路徑。
- 測試:pytest 加 `nl_logic` 的單元(mock LLM)+ 一個「NL skill 端到端 invoke」案例。

## 6. 工作流節點參數設定(系統設定核心二)

### 6.1 事實:現況可調 vs 寫死
讀 `settings.py` 與 kb_query 十節點後的結論:

**已是 env/settings 可調(開放成本低)**
| 參數 | settings 欄位 | 預設 | 影響 |
|---|---|---|---|
| 共用檢索取回數 | `retrieval_top_k` (`RETRIEVAL_TOP_K`, 1–50) | 4 | `retrieve` 節點未指定 top_k 時 |
| kb_query 檢索基準數 | `kb_query_top_k` (`KB_QUERY_TOP_K`) | 8 | retrieval_planner 基準 |
| kb_query 最大檢索次數 | `kb_query_max_retrieval_attempts` | 2 | RETRY 迴圈收斂點(input 亦可覆寫) |
| 工作流逾時 | `workflow_timeout_seconds` | 120 | 全域執行逾時 |
| LLM 模型 | `llm_model` (`LLM_MODEL`) | gpt-4o-mini | 所有 LLM 節點 |

**高價值但寫死在節點(開放成本中,需改碼 + 加 settings + 經 `KbQueryDeps` 注入)**
| 參數 | 位置 | 現值 |
|---|---|---|
| 意圖分類 LLM 採用信心門檻 | intent_classification.py:91 | 0.6 |
| LLM 溫度 | llm.py:19(單例寫死,全節點共用) | 0.7 |
| query_rewrite 變體數上限 | query_rewrite.py:74 | 5 |
| retrieval_planner top_k 加倍倍率 | retrieval_planner.py:55,93 | ×2 |
| ScoreReranker 加權(period/metric/excluded) | adapters.py:145-151 | +0.2/+0.2/−0.5 |
| evidence_verification 數值容差 | evidence_verification.py:8,10 | 1e-6 / 5e-5 |
| locator 分數權重 | locators.py:64-68,112-114,171 | 多個 |

**視為「設定資料」但目前寫死**:詞彙字典 `DEFAULT_GLOSSARY`、口徑詞 `VERSION_TERMS`/`OPPOSITE_TERMS`、意圖→方法對照 `_INTENT_METHODS`、公式規則 `_FORMULA_RULES`。開放這些是「規則管理」等級,範圍大,**本計畫不含**。

### 6.2 建議 v1 範圍(待你選定)
- **v1(推薦)**:只開放上表「已是 settings」的 5 個 + 促升 2 個高價值寫死值(**意圖信心門檻 0.6**、**LLM 溫度 0.7**)為 settings。理由:立即有感、成本可控,不動大範圍規則邏輯。
- **v2(選配)**:再促升 rerank 加權、變體數上限、容差等。
- **明確不做**:詞彙/口徑/意圖對照/公式等「規則資料」的 UI 化(另立計畫)。

### 6.3 Configuration Set — tenant-scoped 具名參數組(D6/O4,本功能最大工程成本)

節點參數不是全域 key/value,而是**具名的 Configuration Set**:每個組織(租戶)有自己的一或多組參數設定,擇一啟用。命名空間化 `app_config`(全域、無租戶)**承載不了**這個需求,需專屬 schema。

**資料模型(新增,appdb / backend 持有,與 skill 同源同路)**
```sql
CREATE TABLE configuration_set (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   text NOT NULL,              -- 對齊 skill.tenant_id 的 text（租戶 code）
    name        text NOT NULL,              -- 組織內具名，如 "預設" / "高召回"
    is_active   boolean NOT NULL DEFAULT false,  -- 該租戶啟用中的組（至多一組 active）
    values      jsonb NOT NULL DEFAULT '{}',     -- { "kb_query.top_k": 8, "llm.temperature": 0.7, ... }
    created_by  text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_confset_tenant_name UNIQUE (tenant_id, name)
);
CREATE UNIQUE INDEX uq_confset_active ON configuration_set(tenant_id) WHERE is_active;
```
- 只存**覆寫值**;未覆寫的鍵回落**全域預設**(= 節點內建 / settings 預設)。分層對齊 D8:全域預設(未來系統 Admin 管)← 租戶 Configuration Set(組織 Admin 管)。
- `values` 的鍵集合 = §6.2 選定開放的參數;每個鍵有型別/範圍(存前於 backend 驗證,比照現有 DataAnnotations)。

**執行時如何被讀到(關鍵串接)**
- workflow 目前只在**啟動**讀 env/settings。改為:invoke 時取當前租戶的 active Configuration Set,疊在全域預設上,注入 `KbQueryDeps` 等節點依賴。
- 取值路徑(擇一,P4 動工前定 O4b):
  - **(i)** workflow 在 invoke 時向 backend 取(比照它已會向 backend 取自訂 skill 定義)+ 依租戶+版本快取;或
  - **(ii)** platform 轉發 invoke 時,把 active set 的 values 一併帶下(少一次 workflow→backend 往返,但把設定編排放到 platform)。
  - 傾向 **(i)**:與「skill 定義也是 workflow 向 backend 取」一致,事實來源集中。
- 代理鏈:CRUD(`/api/configuration-sets*`)platform → backend(比照 skill CRUD);讀取在 invoke 路徑上。

**API(CRUD,組織 Admin,tenant-scoped)**
`GET/POST/PUT/DELETE /api/configuration-sets`、`POST /api/configuration-sets/{id}/activate`。錯誤沿用 ApiError;欄位 snake_case。

## 7. 決策定調(全數拍板)

O1(D5)、O4(D6)前已定案。本輪一次定調其餘全部:

| # | 決策 | **定調** | 備註 / 代價 |
|---|---|---|---|
| **O2** | 簡單模式「我的規則」預設格式 | **自然語言(→ `nl_logic`)為預設;Python 為進階選項** | 直觀優先。代價:每次執行多一次 LLM 呼叫、行為不完全確定;非技術情境划算 |
| **O2b** | v1 範本清單 | **對齊五類問題原型:檢索 / 比對 / 統計 / 推論 / 啟發**(§4.4) | 與聊天路由共用同一套意圖分類;比對與統計預設走 `script`;組織自訂範本延後 |
| **O3** | 節點參數 v1 範圍 | **5 個既有 settings + 促升 2 個寫死值(意圖信心門檻 0.6、LLM 溫度 0.7)為 settings** | 立即有感、成本可控;其餘寫死值 v2 再議,規則資料類不做 |
| **O4b** | Configuration Set 執行取值路徑 | **(i) workflow 於 invoke 向 backend 取 + 依租戶/版本快取** | 與「skill 定義也向 backend 取」一致,事實來源集中 |
| **O5** | 線上 Python 編輯器 | **CodeMirror 6(視為已授權)** | 使用者已明示要線上寫 Python;新增依賴,僅用於進階/Python 規則 |
| **O6** | 「一般設定」`app_config` per-tenant 化 | **本計畫不動(維持全域)** | 若日後組織級一般設定需隔離,另立小計畫加 tenant 欄 |
| **O7** | Configuration Set 每租戶組數 | **多組 + 一 active**(schema 已按此設計) | 支援「高召回/保守」等多套切換;租戶內 name 唯一、至多一 active |

> 定調後無阻塞項;下一步可將 P1–P4 各自展開為實作 spec(仍待「開始實作」指令)。

## 8. 交付分階(規劃,待核准後才動工)

| 階段 | 內容 | 服務 | 風險 |
|---|---|---|---|
| **P1 前端 IA** | 移除 workflows 選單;Skill 功能樹搬進系統設定;系統設定改三分頁;試跑(TraceView)+版本控管(revisions)接為子功能 | frontend | 低 |
| **P2a 範本** | workflow:curate 5 支 `template_*` 內建骨架(骨架 flow + input_schema + 注入槽;三支 `nl_logic` slot、比對/統計為 `script` slot)、catalog 內建項帶 definition;frontend:薄 UI metadata(basedOn/開放欄位/標籤/元件)+ compose(patch 骨架)(§4.4) | workflow + frontend | 低—中 |
| **P2b 簡單模式** | 範本挑選 + 名稱/說明/我的規則(NL 預設、Python 進階 CodeMirror)+ 內建試跑 + 組譯 YAML + validate 翻人話 | frontend | 中 |
| **P2c 進階模式** | 單一 flow 步驟編輯器 + YAML 無損切換(技術使用者;可延後) | frontend | 中 |
| **P3 nl_logic 節點** | 新增 LLM 節點 + 註冊 + pytest;「我的規則」NL 路徑打通(P2b 依賴此) | workflow | 中 |
| **P4 Configuration Set** | 新 schema + backend CRUD + platform 代理 + invoke 讀取串接(O4b)+ 系統設定表單;settings 促升(O3)| workflow/backend/platform/frontend | **高**(跨服務讀設定 + 新資料表 + tenant 隔離) |

- **最小可用切片**:P1 + P2a + P2b + P3 即可交付「非技術使用者靠範本建 Skill、寫中文規則、當場試跑」的完整價值。P2c(進階模式)服務技術使用者,可延後;P4(Configuration Set)是另一條較重的線,可獨立排期。
- **多租戶(D8)貫穿每階段**:Skill 已 tenant-scoped;Configuration Set 新表帶 tenant_id;系統 Admin 僅留位不建。
- 每階段沿用既有測試慣例(frontend lint+build;workflow pytest;.NET xUnit)並跑 e2e 回歸(聊天 SSE / 文件 202 / workflows 契約不得破)。

## 9. 明確不做(YAGNI 邊界)
- 不退回 flow/logic/script 三欄位舊模型(報廢引擎,禁止)。
- 不做視覺化拖拉流程編排(進階模式 v1 用步驟清單 + YAML;拖拉之後再加)。
- 簡單模式不讓非技術使用者碰流程/節點/YAML(範本代勞);組織自訂範本亦延後。
- 不 UI 化詞彙/口徑/意圖對照/公式等規則資料(§6.1 末,另立計畫)。
- 不改公開聊天 API / SSE / AG-UI 契約。
