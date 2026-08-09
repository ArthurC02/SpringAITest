# 計劃書 — Node-first 架構翻轉 × Skill 流程引擎

> 狀態: **核心能力已交付；以下是設計與驗收記錄。** Registry、harness、compiler、script runner、tool registry、Skill 驗證與執行都在現行程式碼中。
> 現況與測試入口見 [plans README](../README.md)。設定頁與作者體驗的後續需求統一記錄在 [settings-skill-redesign](../settings-skill-redesign/01-plan.md)。
> 規格書、設計文稿、驗收案例已併入本檔附錄:[§A 規格與程式碼偏離回填表](#附錄a-規格與程式碼偏離回填表併自-02-specmd2026-08-09-整併)、[§B DB Schema 與設計要點](#附錄b-db-schema-與設計要點併自-03-designmd2026-08-09-整併)、[§C 驗收案例統計與治理硬規則](#附錄c-驗收案例統計與治理硬規則併自-04-acceptance-testsmd2026-08-09-整併)。
> 第二階段(reads 契約強制化 × 作者/執行 UX)規劃於 [05-contract-hardening-and-authoring-ux.md](05-contract-hardening-and-authoring-ux.md)。

## 1. 背景與問題

- **當時現況是 Workflow 為主體**:每個工作流自己手寫 LangGraph 圖、自己持有節點；新流程等同寫 Python 後重新部署。
- **當時 kb_query 已把地基打好**:節點、執行殼與外部服務介面已存在，翻轉成本低。
- **目標型態**:Node 是一等公民(具名、帶 I/O 契約、可獨立測試),Workflow 只是組合結果;由使用者的 **Skill** 決定流程,Skill 可內嵌小段 Python Script,Script 有安全的執行處;Node 與 Script 可呼叫 Tool(backend API 或工作流容器內安裝的工具)。

## 2. 目標 / 非目標

### 目標
1. **Node Registry**:節點以 `@node` 註冊,宣告 `reads/writes/requires_tools` 契約;現有 kb_query 10 節點與 `retrieve` 遷入。
2. **Harness**:每個節點一律包在標準執行殼中執行(trace、逾時、重試上限、I/O 契約驗證、稽核、fatal 短路、Tool 注入)。
3. **Skill 流程引擎**:宣告式 Skill 定義(YAML/JSON)→ 靜態驗證 → 編譯成 LangGraph 圖執行;支援 sequence / branch / bounded-loop / node / script / tool 步驟。
4. **Script Runner**:Skill 內嵌 Python 的沙箱執行處(v1 in-process 受限執行,v2 subprocess 隔離升級路徑)。
5. **Tool Registry**:backend HTTP API 與容器內本地工具走同一介面,節點宣告依賴、Script 經 `tools.call()` 受控使用,呼叫全程入 trace。
6. **對外化**:Skill CRUD(ADMIN)存 appdb、前端 Skills 管理視圖、platform 代理端點。

### 非目標
- 不改動公開聊天 API、SSE 契約、AG-UI 端點。
- 不自造 workflow runtime — LangGraph 保留為編譯目標與執行引擎。
- 不做 Skill 市集、跨租戶分享、多語言 Script(僅 Python)。
- 不在本計畫內重做 skill-authoring 的匯出 UI；匯出資料契約採 `SKILL.md` + `skill.yaml`，由該計畫的 backend export endpoint 承載。

## 3. 決策紀錄(Decision Log)

| #   | 決策                        | 選擇                                                                                          | 理由 / 影響                                               |
| --- | --------------------------- | --------------------------------------------------------------------------------------------- | --------------------------------------------------------- |
| D1  | Skill 定義格式              | **YAML 為權威格式,JSON 為 API 傳輸格式**                                                      | 人可讀、易 diff、易稽核;DB 存原文 + 正規化 JSON           |
| D2  | Script 沙箱起步等級         | **v1 in-process 受限執行(AST 白名單 + 受限 builtins + timeout);v2 subprocess 隔離為升級路徑** | 先滿足流程膠水邏輯;重運算(pandas 級)需求出現才升 v2       |
| D3  | 自訂 Skill 儲存             | **backend appdb(經 BackendClient 同源同路);內建 Skill 留 repo 檔案**                          | 多租戶、權限、審計現成;與 skill-authoring 的 D 系決策一致 |
| D4  | 既有 4 個舊 workflow        | **保留 `@register` 相容層不動;Phase 2 以 kb_query 驗證引擎,舊 workflow 遷移為選配**           | 降低一次改動面;相容層與 Skill 清單在 API 層合併呈現<br>**後續現況**:`@register` 相容層已隨 4 個舊 workflow 全面遷移為 skill YAML(`skills/rag-qa.yaml`/`summarize.yaml`/`triage.yaml`/`analyze-report.yaml`)而移除,`workflow/app/workflows/` 目錄已不存在,此決策已被取代。       |
| D5  | 治理硬規則不可被 Skill 關閉 | **loop 無上限拒存、audit 節點引擎強制附加、trace/fatal 短路不可停用**                         | 金融稽核紅線,引擎層寫死                                   |
| D6  | Node 契約版本化             | **Skill 引用 `node@version`,預設解析到最新相容版**                                            | 節點升級不悄悄改變已上線 Skill 行為                       |

## 4. 與 skill-authoring 計畫的關係(歷史)

(歷史)本計畫取代了已封存並刪除的 skill-authoring 計畫:`flow/logic/script` 三欄表被 `definition` 單一權威欄位取代。

## 5. 分階段實施與驗收標準

| Phase                   | 內容                                                                                                                                                              | 驗收                                                                              |
| ----------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| **P1 — Node 一等公民**  | `engine/node_registry.py` + `engine/node_shell.py`(P1 期間叫 harness,後由 skill-concept-realignment 正名為 Node Shell,因 `Harness` 一詞已被 D4 固定骨架收回);kb_query 10 節點 + retrieve 遷入註冊;kb_query 圖改由「程式碼組合已註冊節點」建出 | 現有 workflow 測試全數不改一行、全綠;`GET /nodes` 列出節點與契約                  |
| **P2 — Skill 引擎**     | Skill schema + 靜態驗證(資料流檢查、loop 上限、未知節點拒絕)+ compiler(sequence/branch/bounded-loop)+ 條件式求值器(calculator 擴充布林/比較)                      | `skills/kb_query.yaml` 編譯出的圖通過與手寫圖**完全相同**的 7 個 e2e(parity test) |
| **P3 — Script 與 Tool** | v1 沙箱 Script Runner + Tool Registry(backend HTTP + 容器內工具)+ script/tool 步驟型別 + 稽核擴充(script 原始碼 hash、tool 呼叫紀錄入 trace)                      | 沙箱逃逸測試集(import/open/網路/無限迴圈)全數被擋;tool 呼叫出現在 audit trail     |
| **P4 — 對外化**         | backend skill CRUD API + appdb 資料表 + platform 代理 `/api/skills` + 前端 Skills 視圖(清單/編輯器/執行/trace 檢視)                                               | 端到端:ADMIN 於前端建立 Skill → USER invoke → trace 可視 → 稽核落地               |

每個 Phase 獨立可驗收、可停損;P1/P2 純 workflow 服務內部,P3 起才動 backend 與 frontend。

## 6. 風險與對策

| 風險                                  | 等級 | 對策                                                                                                                                        |
| ------------------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------------------------------- |
| Script 沙箱逃逸(使用者碼 = 攻擊面)    | 高   | authoring 限 ADMIN;AST 靜態掃描於存檔時拒絕;受限 builtins 無 import/open/網路;CPU/時間上限;v2 subprocess 隔離升級路徑;沙箱逃逸測試集納入 CI |
| 動態建圖失去型別檢查保護              | 中   | Skill 存檔時靜態驗證資料流(`reads` 必須有前置 `writes` 供給);parity test 鎖行為                                                             |
| 引擎過度設計(YAGNI)                   | 中   | 步驟型別只做 6 種(node/script/tool/branch/loop/sequence);不做平行分支、子流程呼叫,需求出現再加                                              |
| 舊 workflow 與 Skill 雙軌並存造成混亂 | 低   | API 層合併清單並標示來源(`source: code                                                                                                      | skill`);文件明示遷移路徑 |
| LangGraph 版本升級破壞 compiler       | 低   | compiler 只用 add_node/add_edge/add_conditional_edges 最小 API 面                                                                           |

## 7. 里程碑與工作量粗估

P1:0.5~1 天(多為搬移與泛化,測試現成)。P2:1~2 天(compiler + 驗證器 + parity test)。P3:1~2 天(沙箱與逃逸測試集是重心)。P4:2~3 天(跨三個服務 + UI)。合計約 5~8 個工作天,可分四次 PR 交付。

---

## 附錄A 規格與程式碼偏離回填表(併自 02-spec.md,2026-08-09 整併)

> 本節在 P1~P4 實作完成、經雙向符合度稽核(逐 AT 讀測試斷言 + 對照實作)後回填,使規格與程式碼一致。76 個驗收案全數通過;以下是實作**刻意偏離原規格書或原設計文稿**之處,連同原因與裁決。裁決分三類:**合理補全**(規格留白處的補全)、**對齊現況**(規格與既有系統/現實不符,實作對齊後者)、**誠實縮減**(規格列出但實作刻意不做,已用文件+測試誠實標記)。

### A.1 偏離規格書(原 §2~§7:Node Registry / Skill 定義格式 / Engine-Compiler / Script Runner / Tool Registry / API 契約)

| #   | 規格出處                           | 規格原文要求                                                    | 實際實作                                                                                                                                                          | 原因                                                                                                                                                           | 裁決                                                                                                   |
| --- | ----------------------------------- | --------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| D1  | Node Registry 註冊契約              | 節點 factory 統一簽名 `make_x(tools: ToolBag)`                  | 保留各節點原簽名,`@node` 以 `deps=[...]` 欄位名注入依賴                                                                                                           | 統一簽名須改動全部 10 個既有節點 factory,違反 P1「既有節點測試一行不改」的不可退讓底線                                                                         | **合理補全**                                                                                           |
| D1a | Node Registry 註冊契約              | 節點宣告 `requires_tools` → Harness 注入 ToolBag 給節點         | **此語意未實作**:所有 kb_query 節點 `requires_tools=[]`,tool 一律經 tool/script 步驟取用,不注入節點。`requires_tools` 僅出現在 `GET /nodes` 目錄,目前為裝飾性欄位 | kb_query 節點無一需要在節點內呼叫 tool;節點級 tool 注入無使用者,YAGNI                                                                                          | **誠實縮減**(欄位保留供未來;需要節點級注入時再實作)                                                    |
| D2  | Node 遷入清單 / 設計文稿程式結構    | 節點搬至 `app/nodes/kbquery/`                                   | 節點就地留在 `app/kbquery/nodes/`,僅補 `@node` 宣告                                                                                                               | 搬檔會改動既有測試的 import 路徑,違反 AT1-07;規格明文「節點函式本體不改」,搬檔非硬性要求                                                                      | **合理補全**(當時建議回填設計文稿的路徑描述)<br>**二次更新(現況)**:此後又完成了原規格所要求的搬遷,節點與領域契約已全數移至 `workflow/app/nodes/kbquery/`,`app/kbquery/` 已刪除;D2 的『合理補全』裁決已被現況覆蓋。 |
| D3  | Skill 定義格式(步驟型別表)         | loop「達 max_iterations 強制離開」                              | `max_iterations` 為引擎天花板(1~10),業務收斂條件放進 `until` 讀 state(kb_query 用 `retrieval_attempt >= max_retrieval_attempts`)                                  | 業務重試上限是**動態值**(deps 注入,可為 3),編譯期常數 `max_iterations` 表達不了;兩層護欄並存,先到者先收斂                                                      | **完全合規**(AT2-18/26 雙向釘死)                                                                       |
| D4  | API 契約(workflow 服務)            | `POST /skills/validate` 未定義請求/回應細節                     | 收 `{definition: YAML}`;valid 時回應含 `skill` 中繼資料(name/description/required_role/input_schema),invalid 時無此欄                                             | backend 不裝第二個 YAML parser,靠此回應寫 DB;`skill` 欄有無即「可否寫入」閘門,不可繞                                                                           | **合理補全**                                                                                           |
| D5  | Script Runner 威脅模型             | 將「`range()` 與序列乘法引數常數上限 10^6」列為 v1 緩解措施     | **移除**序列乘法/pow 的配置上限 guard,只保留 range 長度上限                                                                                                       | 黑名單式 guard 有等價繞法(擋 `*` 有 `xs=xs+xs`、擋單次 pow 有累乘),給假安全感;range 長度是唯一「無等價改寫可繞」的完整護欄。規格自陳 v1「防意外不防惡意」 | **誠實縮減**(docstring 明載現狀 + `test_*_honest_gap` 釘住邊界;需防惡意作者請升 v2 subprocess) |
| D6  | Script Runner AST 白名單           | 沙箱白名單(禁 while/dunder,允許 for 與所列 builtins)            | **比規格更嚴**:僅放行 `tools.call` 一個屬性、拒絕 comprehension/generator                                                                                         | 開放非 dunder 屬性等於交出整張方法表;comprehension 會繞過 `for` 的執行期迭代計數器,使 10000 上限失效                                                           | **更嚴,非漏擋**(無合法 skill 被誤殺)                                                                   |
| D7  | API 契約(`GET /workflows`/`GET /skills`) | 兩者(catalog)欄位未含 `input_schema`                        | 兩者新增 `input_schema` 欄位(既有欄位型別/名稱不動)                                                                                                               | 前端執行表單改為依 schema 動態渲染,舊 workflow 需同形狀資料;additive 不違 AT-REG-01                                                                            | **合理補全**                                                                                           |
| D8  | 本計畫書 §4(歷史,原亦見已封存刪除的 skill-authoring 計畫舊 §5) | 匯出 `SKILL.md` + `scripts/main.py`                             | 匯出 `SKILL.md` + `skill.yaml`；YAML 原文逐 byte 保存                                                                                                             | 含 node/tool/branch/loop 的流程無法忠實轉成 standalone Python;`skill.yaml` 才是可移植且可稽核的權威 definition                                                | **合理補全**(兩份計畫與驗收已同步)                                                                     |

### A.2 偏離設計文稿(DB Schema / 端點)

| #   | 設計出處 | 設計原文                                                     | 實際實作                                                                                               | 原因                                                                                                                           | 裁決                                                 |
| --- | -------- | ------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------- |
| E1  | DB DDL   | `skill.tenant_id uuid`                                       | `tenant_id text`                                                                                       | 對齊既有 `rag_documents.tenant_id text` 與 identity header `X-Tenant-Id`(傳的是租戶 code 非 uuid);用 uuid 反與現行 schema 不符 | **對齊現況**                                         |
| E2  | 後端整合 | backend 不自驗、驗證交引擎                                   | `POST/PUT` body 僅 `{definition}`;name/desc/role 取自引擎 validate 回報;backend 全域無任何 YAML parser | 單一事實來源落實,backend 不裝第二個 parser                                                                                     | **合理補全**                                         |
| E3  | 後端整合 | (未列 revision 查詢端點)                                     | 新增 `GET /api/skills/{name}/revisions`(USER 可讀、租戶隔離)                                           | AT4-16 的 revision 歷史唯讀對照需要                                                                                            | **合理補全**                                         |
| E4  | 後端整合 | (未明列衝突/不符碼)                                          | POST 同名 → 409;PUT 的 YAML `name` 與路由 `{name}` 不符 → 422                                          | 避免「改 A 卻改到 B」;名稱即執行時路由鍵,衝突須明確                                                                            | **合理補全**                                         |
| E5  | DB Schema | 軟刪 + revision 永不刪                                       | 軟刪後同名 POST **復活**成 bump revision(非永久 409)                                                   | 名稱即路由鍵,永久燒毀=功能死路;復活時稽核鏈連續、`skill_revision` 不刪列                                                       | **合理補全**(補強軟刪語意)                     |
| E6  | DB DDL   | `skill.definition_json jsonb`                                | **未建**此欄                                                                                            | 無讀取端;要填充須在 backend 裝第二個 YAML parser,與 E2 衝突。查詢/驗證的權威格式是引擎解析的 `definition` 原文                 | **誠實縮減**(有註解、無殘留依賴;需 jsonb 查詢時再議) |
| E7  | 後端整合 | `/api/skills*`→backend、`/api/skills/{name}/invoke`→workflow | 另增 `/api/skills/catalog`、`/api/skills/validate`、`/api/nodes` → workflow 三條代理                   | 前端 Tab1(執行清單)、Tab3(節點目錄)、編輯器即時校驗需要;字面段路由優先序 + backend 保留字雙重防護                              | **合理補全**                                         |

**後續建議(消除文件與碼落差的收尾)**:若 requires_tools(D1a)日後要落實節點級 tool 注入,Node Registry 該語意需一併補實作;`SkillRepository` 的真 SQL(原子 CTE、軟刪復活、租戶過濾)目前僅 e2e 全鏈路 + 讀碼背書,單元測試背靠 fake,建議補一個打真 PostgreSQL(Testcontainers)的整合測試,把 fake 與真 SQL 的語意對齊釘死。

## 附錄B DB Schema 與設計要點(併自 03-design.md,2026-08-09 整併)

appdb(PostgreSQL,Dapper)。演進(歷史):早期 skill-authoring 計畫的 `flow/logic/script` 三欄表(該計畫已封存刪除)被收斂為結構化 `definition`,並新增 revision 稽核表。

```sql
CREATE TABLE skill (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      text NOT NULL,                -- 原設計文稿為 uuid,實作依 A.2 §E1 對齊現況改為 text(見 DbBootstrap.cs)
    name           text NOT NULL,                -- ^[a-z][a-z0-9_]{2,63}$
    description    text NOT NULL DEFAULT '',
    required_role  text NOT NULL DEFAULT 'USER', -- USER | ADMIN
    definition     text NOT NULL,                -- Skill YAML 原文(權威格式)
    definition_json jsonb NOT NULL,              -- 正規化 JSON(查詢/驗證用,存檔時由服務轉出)
    current_revision int  NOT NULL DEFAULT 1,
    enabled        boolean NOT NULL DEFAULT true,
    created_by     text NOT NULL,                -- X-User-Id
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_skill_tenant_name UNIQUE (tenant_id, name)
);

-- 稽核與回溯:每次 PUT 產生一筆;invoke 記錄引用的 revision
CREATE TABLE skill_revision (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    skill_id       uuid NOT NULL REFERENCES skill(id),
    revision       int  NOT NULL,
    definition     text NOT NULL,                -- 該版 YAML 原文(script 原文含在內)
    definition_sha256 text NOT NULL,             -- 對應 audit trail 中的 hash
    created_by     text NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_skill_revision UNIQUE (skill_id, revision)
);

CREATE INDEX ix_skill_tenant ON skill(tenant_id) WHERE enabled;
CREATE INDEX ix_skill_revision_skill ON skill_revision(skill_id);
```

設計要點:

- **軟刪**:`DELETE` = `enabled=false`;revision 永不刪(金融稽核)。實作後續依 A.2 §E5 補強為「同名 POST 可復活成 bump revision」。
- **hash 鏈**:audit trail 記 `skill_name + revision + definition_sha256`,可回查當次執行用的確切定義與 script 原文。
- 內建 skill(repo 檔案)不入 DB;清單 API 由 workflow 服務合併兩來源。
- 節點目錄不入 DB(程式即事實來源,`GET /nodes` 即時反映)。
- 本 schema 同時是 skill-authoring P2(CRUD + 匯出)的資料表;該計畫不另建 `flow/logic/script` 三欄位表。

**更正記錄**:`app/kbquery/` 頂層已完全移除,含領域契約在內全數併入 `app/nodes/kbquery/`(對應 A.1 §D2 的二次更新)。

## 附錄C 驗收案例統計與治理硬規則(併自 04-acceptance-tests.md,2026-08-09 整併)

驗收已通過,現行驗證入口為 `workflow/tests/`、`backend/tests/`、`platform/tests/`、前端檢查(`npm run lint && npm run build`)。

各 Phase 案例數統計:

| Phase                               | 案例數 | 編號範圍                                                |
| ------------------------------------ | ------ | -------------------------------------------------------- |
| P1 — Node Registry + Harness         | 7      | AT1-01 ~ AT1-07                                           |
| P2 — Skill 引擎                      | 28     | AT2-01 ~ AT2-28(含 kb_query parity 7 案:AT2-21~AT2-27)   |
| P3 — Script Runner + Tool Registry   | 17     | AT3-01 ~ AT3-17(含沙箱逃逸樣本 11 個:AT3-01~AT3-11)      |
| P4 — 對外化                          | 17     | AT4-01 ~ AT4-17                                           |
| 治理硬規則驗證清單                   | 4      | AT-GOV-01 ~ AT-GOV-04                                     |
| 回歸檢查清單                         | 3      | AT-REG-01 ~ AT-REG-03                                     |
| **合計**                             | **76** | —                                                          |

治理硬規則四條(AT-GOV-01~04,對應 §6.3 引擎強制行為,Skill/Script 不可關閉):

1. **AT-GOV-01 稽核節點強制附加**:即使 skill 作者未在 YAML 宣告 `audit_feedback`,每條終止路徑(含 branch 各分支、loop 離開後)執行完都必須在 trace 留下等價稽核 entry。
2. **AT-GOV-02 loop 上限不可省**:缺 `max_iterations` 的 loop 不只 API 層擋,`compiler.compile()` 內部亦拒絕編譯,不可繞過驗證入口。
3. **AT-GOV-03 保留鍵不可被 Script 覆寫**:`query_id`/`original_query`/`query_timestamp` 在 script 步驟嘗試寫入後仍維持引擎注入原值。
4. **AT-GOV-04 trace 不落 LLM 私有推理與 script 全文**:trace 只含結構化輸出摘要(無 CoT)與 script SHA-256,不落原始碼全文。
