# Agent Skill 標準格式支援 — 計畫

> 狀態：P0–P2 已交付；P3 production script isolation 延後。本檔保留為歷史交付計畫，現況以程式碼與 AGENTS.md 為準。
> **frontmatter 具體欄位形狀已由 05-standard-conformance.md 遷移（`metadata` 子物件 + `allowed-tools`），本檔 L41-54 的範例僅供歷史對照，不代表現行契約。**

## 目標

以標準 Agent Skill package 作為交換格式：zip 內有 `SKILL.md`，並可包含 `scripts/`、`references/`、`assets/`。使用者可上傳、下載與編輯 package；執行仍留在既有 Workflow Harness 內。

支援兩種 skill：

| kind      | 作者介面                | 執行模型                                              |
| --------- | ----------------------- | ----------------------------------------------------- |
| `flow`    | 既有宣告式 YAML         | 既有 compiler 直接編譯；行為不得改變                  |
| `agentic` | `SKILL.md` prose 與附件 | 編譯為一個受 Harness 管理的 `agent_skill_runner` 節點 |

兩者共用 tenant 隔離、`/invoke` 回應 `{skill, output}`、工具白名單、身分注入、未宣告寫入剝除及終端 `audit_feedback`。`kind` 為 additive，未指定時一律為 `flow`。

## 已驗證的基礎

- Workflow 已有宣告式 Skill schema、compiler cache、強制附加 `audit_feedback`、`harnessed(...)`、`tool_registry.invoke(...)` allowlist，以及 `script_runner.scan(...)` 的 AST 靜態掃描。
- `langgraph>=1.0,<2.0`、`langchain` 與 LiteLLM 用的 `ChatOpenAI` 已在 workflow 相依中；不需要 deepagents 或直接連 Anthropic。
- Backend 已能把 flow skill 匯出為 zip：`SKILL.md` 加 byte-preserving 的 `skill.yaml`；Platform 已原位元組代理 export。
- 自訂 skill 的 Workflow 載入流程目前只讀 backend 的 `definition`。因此新增 `package bytea` 本身不足以支援 agentic execution：必須有受內部 token 與 tenant 限制的 package 讀取契約，並由 Workflow 在 invoke 時取得 package。

## 範圍與限制

- 不替換既有 compiler、node/tool registry 或 Harness；agentic loop 只能存在於單一節點中，不能成為頂層編排器。
- 不改既有 flow skill、既有 `/skills/{name}/invoke` 形狀或既有 deterministic `SkillRoutingAgent` 的選擇模型。
- 不引入 deepagents、E2B、Firecracker、Pyodide/WASM 或 DinD。
- `agentic` 不保證輸出或數值的可重現性；其界限、allowlist、稽核及輸出契約必須可測。
- 外部 package 一律視為不可信輸入。現有 in-process AST runner 只適用於現有 ADMIN 開發情境，不能作為 production 外部 script 的安全邊界。

## 資料與驗證契約

### Package 格式

`flow` export 維持現有兩個檔案：`SKILL.md` 與權威的 `skill.yaml`，不重新序列化 YAML。

`agentic` package 的 `SKILL.md` frontmatter 是 metadata 的唯一來源，至少包含：

```yaml
---
name: example_skill
description: Example skill
kind: agentic
required_role: USER
input_schema:
  question:
    type: str
    required: true
uses_tools: []
timeout_seconds: 30
---
```

body 是 runner instruction。附件只允許 `scripts/`、`references/`、`assets/`；拒絕絕對路徑、`..`、重複正規化路徑、壓縮炸彈及超過明訂總大小/檔案數限制的 archive。

### 唯一責任

1. **Workflow** 解析及驗證 `SKILL.md`，回傳既有 `SkillMeta` 加 additive `kind`。它也是 `uses_tools`、input schema、timeout 與 agentic package 結構的唯一語意權威。
2. **Backend** 保留已驗證的 canonical definition/metadata、原始 package 與 revision hash；不得自行用另一套 parser 重複解讀 frontmatter。
3. **Backend → Workflow** 驗證請求必須能傳遞 package bytes，而不只是目前的 YAML `definition` 字串。選定 multipart 或 JSON base64 其中一種並釘成內部契約；不可兩者並存。
4. **Workflow → Backend** 必須新增內部 package 讀取端點，依 `X-Internal-Token` 與 `X-Tenant-Id` 限制。Workflow invoke 以此取得 package；package 不放進公開 `GET /api/skills/{name}` JSON。

這四點先以 request/response 測試釘住，再實作 runner。它們解決目前「Backend 能存 package、Workflow 卻只能讀 definition」的斷點。

## 執行模型

`kind: agentic` 的 compiler 分支產生等價的單節點 flow：

```yaml
flow:
  - node: agent_skill_runner@1.0
```

實作以獨立 `workflow/app/engine/agent_skill_graph.py` 模組達成同等效果（`compiler.py:503-504` 分派），而非把 `flow` 塞一個 node 走標準 builder；行為結果一致。

compiler 仍在終點加上 `audit_feedback`。`agent_skill_runner` 以 `harnessed(...)` 註冊，固定寫入 `answer`；Platform 與 Frontend 的 answer-key 清單以 `answer` 為相同優先序。它：

1. 從受保護的 package 取得 instruction 與允許的資源。
2. 以既有 LiteLLM model 建立 bounded `create_react_agent` loop。
3. 將每個 LangChain tool adapter 導向 `tool_registry.invoke(name, ctx, allowed, args)`；adapter 不得繞過 registry 或自行持有服務 token。
4. 以 agent-specific step limit 和整體 timeout 中止 loop。現有 `compiler.recursion_limit(skill)` 是 YAML graph 的 bound，不能直接宣稱為 React agent 的 step limit；實作前須確認所用 LangGraph API 的遞迴/超時語意並以測試固定。
5. 只把最終文字寫入已宣告 output key。工具結果、附件內容及 model trace 不得直接變成 public output。

`references/` 與 `assets/` 的可讀取範圍需明確限制在該 package。若要讓 model 按需讀取，提供唯讀、path-normalizing resource tool；不要把整包內容塞進 prompt。

`scripts/` 在 P1 不執行。它們可先被結構檢查及 AST 掃描；只有完成 production isolation design 並有明確 host-to-sandbox protocol 後，才可公開執行。這避免把「可上傳」誤當成「可安全執行不可信 Python」。

## 工作項目與順序

### P0：格式與資料流

**Backend / Workflow**

- 定義 package validation 與內部 package-read contract；新增契約測試。
- Backend schema 加 package blob 與 package SHA-256 revision 欄位；既有 flow row 的 package 為 NULL。
- 新增 ADMIN-only import endpoint。它將 archive 交給 Workflow 驗證，成功後重用既有 create/update/revive 與 revision CTE 路徑寫入。
- Workflow 增加 `kind`、Agent Skill parser 與驗證分支；flow YAML 的 parser、驗證與輸出完全不變。
- 擴充 export：flow 仍輸出現有 byte-preserving `skill.yaml`；agentic 原封輸出已存 package。修正舊 manifest 對 description 未做 YAML escaping 的 POC 假設。

**驗收**

- flow export → import → export 保留 `skill.yaml` bytes。
- agentic package round-trip 保留 entries 與 bytes。
- 路徑穿越、缺少 `SKILL.md`、未知 tool、非法 frontmatter、超限 archive 被拒絕。
- 同 tenant 可讀 package；跨 tenant、少 internal token 與公開 JSON 均不可取得。

### P1：受治理的 agentic runner

**Workflow**

- 新增並註冊 `agent_skill_runner`；`kind: agentic` 走單節點編譯分支，仍使用既有 compile cache 與 audit 追加。
- 將 registry tool 包成 LangChain adapter，測試 allowlist 拒絕時 tool function 未被呼叫。
- 實作並測試 agent loop 的明確 step/timeout 邊界、Harness fatal/error 路徑、audit、不可變身分鍵與 `{skill, output}` 輸出。
- 只提供 package-scoped read-only references/assets 存取；scripts 保持不可執行。

**Platform**

- catalog 與 `SkillMeta` 透傳 additive `kind`。
- 新增 import proxy，沿用 export 的 bearer、401 與錯誤映射行為。
- router 的單一必填字串條件不在本期拓寬；符合條件的 agentic skill 才會被 chat 路由，其餘仍可顯式 invoke。

### P2：使用者介面

**Frontend**

- 在既有 Skills 畫面提供 Agent Skill 上傳、下載與簡單的 frontmatter/body/附件編輯。
- flow 繼續使用既有 YAML/simple editor 與 trial-run/revision history；不建立第二個 flow schema。
- 使用 P0 import API，不將 client-side parse 當成伺服器驗證。

### P3：production script isolation

這是「允許外部 package 的 scripts 在 production 執行」之前的硬前置，獨立於格式、runner 與 UI 上線。

- 先寫出 script execution protocol：輸入/輸出 serialisation、唯讀資源掛載、CPU/記憶體/時間限制、取消、審計與錯誤分類。
- 沿用 `ScriptRunnerPort` 注入點，但將隔離 runner 實作與其 Linux deployment requirement 一起交付；gVisor 是候選實作，不是尚未驗證前的既定事實。
- sandbox 環境不得取得 `INTERNAL_API_TOKEN` 或其他 service secret，預設不得出網；合法服務呼叫必須走 host 的 tool registry proxy。
- 在 Linux production runner 有隔離與拒絕出網的整合測試前，拒絕執行 bundle scripts；Windows/dev 保持現有 AST runner 的既有 flow 行為。

## 不變契約與驗證

- `/skills/{name}/invoke` 繼續回 `{skill, output}`。
- `/skills/validate` 與 catalog 只新增 `kind`；既有 consumers 可忽略。
- Backend 的 `definition` 仍是 flow 的權威原文；agentic package 的權威內容是已驗證的 `SKILL.md` 與附件，不混用兩套可變來源。
- 既有 flow pytest、backend/platform xUnit、frontend lint/build 必須維持綠燈。
- P1 完成後，以一個無 scripts 的 agentic package 做 end-to-end：import、catalog、explicit invoke、可路由的單字串 input、allowlist deny、audit 及 export round-trip。

## 文件收尾

- 修正 `workflow/app/security.py` 已過期的 `/workflows*`、`/documents*` 描述。
- 對齊 Platform 與 Frontend 的 answer-key 順序與成員，讓 agentic runner 的固定 output key 不漂移。
- 功能完成後再更新 root 與各 area `AGENTS.md` 的端點、儲存、執行限制與測試數；不要在實作前把推測寫成現況。
