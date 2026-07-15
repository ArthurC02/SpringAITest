# 計劃書 — Skill 撰寫 × 系統設定

> 狀態:已與 [node-first-skill-engine](../node-first-skill-engine/01-plan.md) 對齊。本文只定義該引擎的管理 UI 與可攜匯出；Skill 資料模型、驗證與執行語意以 node-first 為唯一事實來源。
> 相關文件:[規格書](02-spec.md)、[設計文稿](03-design.md)。

## 1. 背景與問題

- **系統設定頁太空**:[ConfigView.tsx](../../frontend/src/components/ConfigView.tsx) 原本只有一張 key/value 表(Key / Value / 更新時間),需要補上工作流唯讀檢視。
- **工作流的 Tool Description 是設定性質的資產,卻藏在 code 裡**:五個工作流的描述字串寫死在 Python 模組(例 [rag_qa.py:73](../../workflow/app/workflows/rag_qa.py#L73)),經 [registry.py](../../workflow/app/workflows/registry.py) 的 decorator 註冊,前端只看得到 `name/description/required_role`,無法在設定裡檢視或管理。
- **缺少讓使用者自訂能力的機制**:所有工作流都要改 Python code、重新部署。目標是讓 ADMIN 在「工作流與 Skill」視圖撰寫宣告式 Skill,並匯出可攜定義。

## 2. 目標 / 非目標

### 目標
1. 系統設定頁改成分頁,補進「工作流(唯讀)」區,把空頁補實。
2. 讓 ADMIN 在「工作流與 Skill」視圖撰寫 YAML `definition`,由 node-first 引擎驗證與執行。
3. Skill 可匯出為可攜的宣告式套件(`SKILL.md` + `skill.yaml`)。

### 非目標
- 不改動現有五個 code 註冊工作流的行為或 SSE/聊天契約。
- 不另做第二套 Skill 資料模型、驗證器或管理入口。
- 不在本階段支援 Python 以外語言的 Script。

## 3. 決策紀錄(Decision Log)

| #   | 決策                                         | 選擇                                           | 影響                                                                                   |
| --- | -------------------------------------------- | ---------------------------------------------- | -------------------------------------------------------------------------------------- |
| D1  | Skill 的 Python Script 是否在本系統真的執行? | **是,但僅作為 YAML `script` 步驟**             | 沙箱與執行語意由 node-first 承載                                                       |
| D2  | Skill 的權威資料                             | **單一 YAML `definition`**                     | name/description/role/input/flow 均由引擎解析,backend 不重複解析                       |
| D3  | 管理 UI 位置                                 | **Workflows & Skills 的 ADMIN Skill 管理 Tab** | 不在 ConfigView 複製第二個編輯器                                                       |
| D4  | 匯出格式                                     | **`SKILL.md` + `skill.yaml`**                  | `skill.yaml` 逐 byte 保留權威 definition；不把流程型 Skill 偽裝成 standalone `main.py` |

> runtime、Node/Tool、沙箱、revision 與 invoke 的規格均由 node-first 定義；本文不重複定義。

## 4. 範圍與分階

| 階段                           | 交付                                                                                      | 沙箱? | 相對成本 | 風險  |
| ------------------------------ | ----------------------------------------------------------------------------------------- | ----- | -------- | ----- |
| **P1 設定分頁 + 工作流唯讀區** | ConfigView 改分頁;新增 code workflow 唯讀清單(name/description/required_role)             | 否    | 小       | 低    |
| **P2 Skill 管理 + 宣告式匯出** | Workflows & Skills 的 YAML 編輯器、node-first CRUD/revision、匯出 `SKILL.md`+`skill.yaml` | 否    | 中       | 低—中 |

分階原則:P1 只處理設定檢視；P2 消費 node-first 已定義的資料與 API,不另建 runtime 或沙箱。

## 5. 風險

| 風險                 | 等級 | 緩解                                                                          |
| -------------------- | ---- | ----------------------------------------------------------------------------- |
| 文件與引擎契約分歧   | 高   | YAML definition、管理位置與匯出格式全部以 D2~D4 為準；兩份 plans 必須同步修改 |
| Skill 撰寫權限被濫用 | 中   | CRUD 限 ADMIN；引擎驗證、revision 與 audit 沿用 node-first                    |
| 前端加編輯器依賴膨脹 | 低   | MVP 用既有 YAML textarea,不新增依賴                                           |

## 6. 動工前待決事項

1. Python 編輯器是否要由既有 YAML textarea 升級 CodeMirror/Monaco(需授權與依賴決策)。
2. 是否在 P4 後才新增「單一 script 步驟」的專用下載格式；它不能改變本計畫 D4 的通用流程匯出。

## 7. 成功標準

- P1:系統設定頁不再是單張空表;工作流描述在設定裡可見。
- P2:ADMIN 能在 Workflows & Skills 管理 YAML Skill,並下載 `SKILL.md` 與逐 byte 相同的 `skill.yaml`。
