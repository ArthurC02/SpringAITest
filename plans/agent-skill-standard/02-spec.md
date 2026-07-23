# 規格 — Agent Skill 標準格式支援

> 狀態：**規劃中。** 承接 [01-plan.md](01-plan.md)。本檔定義 WHAT：使用者可見行為、資料契約與不變式；實作結構見 [03-design.md](03-design.md)。

## 1. 範圍

本功能以標準 Agent Skill zip 作為交換格式，新增 agentic skill 的匯入、匯出、執行及編輯能力，同時保留既有 flow skill 的 YAML authoring 與執行行為。

| kind      | 作者來源                  | 執行方式                                  |
| --------- | ------------------------- | ----------------------------------------- |
| `flow`    | 現有 YAML definition      | 現有 compiler 直接編譯                    |
| `agentic` | `SKILL.md`、resources zip | 一個受 Harness 管理的 agentic runner 節點 |

不在本案範圍：deepagents、第二套 orchestration runtime、公開執行 bundle scripts、直接 LLM provider 呼叫、既有 flow 行為或聊天路由規則的改寫。

## 2. 需求

### R1 — package 匯入與匯出

- `POST /api/skills/{name}/import` 接受 ADMIN 的 zip upload；成功後以既有 Skill revision 語意建立、更新或復活 skill。匯入器依 package 內容分派：flow 為 exporter 格式的 `SKILL.md` 加 `skill.yaml`，agentic 為 `SKILL.md` 加允許 resources。
- `GET /api/skills/{name}/export` 對有權讀取該 tenant skill 的使用者回傳 zip。flow 維持 `SKILL.md` + byte-preserving `skill.yaml`；agentic 回傳已儲存的原始 package bytes。
- 匯入失敗不得建立 revision、更新 metadata 或替換既有 package。
- 同一個合法 agentic package 的 import → export 必須保留 zip entries 與每個 entry 的 bytes。zip container 的 timestamp、entry order 或 compression level 不屬於 byte-equality 契約。

### R2 — package 結構與安全驗證

agentic package 必須有根目錄 `SKILL.md`，且只允許根目錄 `SKILL.md` 及 `scripts/`、`references/`、`assets/` 下的檔案。

- 拒絕絕對路徑、Windows drive path、`..`、空路徑、正規化後重複的 path、symlink-like archive entry，以及超過設定檔案數、單檔大小、總解壓大小或壓縮比限制的 archive。
- agentic 的 `SKILL.md` 必須有 YAML frontmatter，`name`、`description`、`kind: agentic`、`input_schema`、`uses_tools`、`required_role`、`timeout_seconds` 均依 Workflow schema 驗證。
- `name` 必須等於 import route 的 `{name}`；不得藉由 package 改名。
- `uses_tools` 的每個名稱必須在既有 tool registry 註冊。
- `scripts/*.py` 在寫入時須通過既有 AST scan；這只代表靜態格式合格，**不代表可以執行**。
- `references/` 與 `assets/` 可為空；其內容不應被當成 frontmatter、definition 或 executable code 解讀。

具體大小與數量上限由設定常數定義，並以 on-point/off-point 測試固定；不得散落在 controller、parser 與 UI。

### R3 — 單一語意權威與資料保護

- Workflow 是 `SKILL.md` 的唯一 parser 與語意 validator。Backend 不得另做一套 frontmatter parser。
- Backend 儲存原始 package、package SHA-256、由 Workflow 產生的 canonical agentic definition 與 metadata。對 agentic skill，frontmatter/package 是作者輸入權威；canonical definition 是服務相容與編譯用投影，不能被獨立編輯後與 package 漂移。
- 既有 definition-only create/update API 僅處理 flow。它不得建立 `kind: agentic` skill，也不得更新既有 agentic skill；這兩種請求回固定 409 或 422，且 definition、metadata、package、revision 與兩個 hash 均不變。agentic 的所有作者內容變更只能走 import。
- package bytes 不得出現在 `GET /api/skills`、`GET /api/skills/{name}`、catalog 或 revision JSON。
- Workflow 僅可經 internal-token 且 tenant-scoped 的 Backend 端點讀取 package。缺少或錯誤 token 回 401；跨 tenant 與不存在的 package 一律回 404。

### R4 — additive API 與相容性

- `SkillMeta`、skill catalog entry 可新增 `kind`；現有 consumer 忽略未知欄位時仍可運作。
- `/skills/{name}/invoke` 的成功回應一律維持 `{skill, output}`。
- flow skill 的 CRUD body、validator 輸入、export 結構與 compiler 行為維持現狀。
- 既有 Platform import/export 授權、Bearer 401 行為與 Backend ApiError 形狀不變。

### R5 — agentic runner

- `kind: agentic` 必須經現有 compiler 產生一個邏輯上的 `agent_skill_runner` 節點，且 compiler 仍在終點附加 `audit_feedback`。
- runner 必須由 `harnessed(...)` 包裝。因此 fatal short-circuit、例外安全輸出、immutable identity、declared writes 與 trace 語意必須與其他 node 一致。
- 模型一律使用既有 LiteLLM `get_llm()` 路徑；agent loop 使用既有 LangGraph/LangChain 相依，不新增 deepagents。
- 每個可供模型呼叫的 tool adapter 必須經 `tool_registry.invoke(name, ctx, allowed, args)`。未列於 `uses_tools` 的 tool 不得被呼叫，也不得產生副作用。
- runner 只能寫入固定、已宣告的 public `answer` key。Platform 與 Frontend 的 answer-key 清單均以 `answer` 為相同優先序。
- agent loop 必須同時有可測的 step limit 與整體 timeout。達任一界限時產生受控失敗、保留 Harness/audit 行為，且不得無限重試。
- `references/`、`assets/` 僅能以 package-scoped、唯讀且 path-normalized 的機制讀取。不得將整包內容無條件塞入 model prompt。

### R6 — 路由與顯式執行

- Platform catalog/`SkillMeta` 透傳 `kind`。
- 現有 deterministic router 的角色、template 過濾、最多兩次路由及 `SingleRequiredStringKey` 條件不變。
- 因此只有恰有一個必填字串 input 的 agentic skill 可被聊天路由；其餘 agentic skill 必須仍可由 `/api/skills/{name}/invoke` 顯式執行。
- agentic skill 不得註冊成 Platform native function tool，避免改變既有 AG-UI tool-call event 契約。

### R7 — 前端 authoring

- ADMIN 可在既有 Skills 工作區上傳 agentic package、下載 package，並編輯 frontmatter、Markdown body 與允許的附件。
- flow 繼續使用既有 YAML/simple editor、trial run 與 revision history；不得建立第二個 flow schema。
- 前端預檢可改善 UX，但 server-side import validation 是唯一接受條件。
- USER 不得因新增 UI 而取得 import、修改或 package 管理權限。

### R8 — scripts 的分期安全界線

P0/P1/P2 可接受並保存通過 AST scan 的 `scripts/*.py`，但不得執行它們。任何外部 package script 的 production execution 都必須等 P3 isolation 完成後才開放。

P3 的必要條件：隔離 runner、無 service secret 的執行環境、預設拒絕出網、CPU/記憶體/時間限制、取消與審計、host tool proxy，以及 Linux integration evidence。完成前，執行 bundle script 必須受控拒絕。

## 3. 非功能需求

- **租戶隔離：** package、metadata、revision 與 invoke 均依 tenant filter；不得因 package cache 跨 tenant 洩漏。
- **可稽核性：** 每次成功 import/update 產生 revision；revision 同時記錄 definition SHA-256 與 package SHA-256。失敗驗證零副作用。
- **資源界限：** package 解壓、parser、resource read、agent step 與 agent timeout 都有固定上限。
- **回歸保護：** flow pytest、Backend/Platform xUnit、Frontend lint/build 必須維持綠燈。

## 4. 需求追溯

| 需求   | 設計   | 驗收                            |
| ------ | ------ | ------------------------------- |
| R1–R3  | D1–D3  | AST-P0-001～013                 |
| R4、R6 | D4、D6 | AST-P1-001～006、AST-X-001～002 |
| R5     | D5     | AST-P1-007～014                 |
| R7     | D7     | AST-P2-001～005                 |
| R8     | D8     | AST-P3-001～006                 |
