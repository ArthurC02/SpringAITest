# 計劃書 — Node-first 架構翻轉 × Skill 流程引擎

> 狀態: **核心能力已交付；以下是設計與驗收記錄。** Registry、harness、compiler、script runner、tool registry、Skill 驗證與執行都在現行程式碼中。
> 現況與測試入口見 [plans README](../README.md)。設定頁與作者體驗的後續需求統一記錄在 [settings-skill-redesign](../settings-skill-redesign/01-plan.md)。
> 相關文件: [規格書](02-spec.md)、[設計文稿](03-design.md)、[驗收案例](04-acceptance-tests.md)。
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
