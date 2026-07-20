# 驗收測試 — 聊天 → Skill 路由（Chat-to-Skill Routing）

> 狀態：**已交付功能的驗收基準。** 承接 [01-plan.md](01-plan.md)、[02-spec.md](02-spec.md)、[03-design.md](03-design.md)；實際測試入口見 [plans README](../README.md)。
> 範圍：驗證動態 Skill 目錄與手動 LLM 名稱選擇路由，且不破壞聊天、權限、記憶及 SSE 契約；P2–P4 僅記錄未來門檻，不視為 P1 失敗。

## 1. 驗收原則與層級

### 1.1 P1 通過條件

P1 必須同時滿足：

1. 所有標為 `P1 / Must` 的 deterministic 測試通過。
2. `platform` build、`Platform.Service.Tests`、`Platform.Web.Tests` 全部通過，且既有測試無回歸。
3. 至少一次真 platform → workflow → backend 的整合驗證通過，確認目錄與 invoke 的真 JSON 形狀、snake_case 欄位及身分標頭沒有被 fake 掩蓋。
4. 公開 `/api/chat/stream` 仍逐事件輸出精確的 `data:<value>\n\n`；`data:` 後不得多一個空格。
5. 不得以真 LLM 的單次選擇結果作為穩定 CI 斷言；路由品質依 §7 的獨立評估執行。

### 1.2 驗證層級

| 層級                   | 用途                                                                          | 自動化位置                                                      |
| ---------------------- | ----------------------------------------------------------------------------- | --------------------------------------------------------------- |
| Service unit           | 候選工具映射、角色、schema、去重、invoke、fallback、mem0 順序                 | `platform/tests/Platform.Service.Tests/`（沿用手寫 fake）       |
| Web integration        | JWT/匿名分流、阻塞回應、SSE bytes 與空行                                      | `platform/tests/Platform.Web.Tests/`（`WebApplicationFactory`） |
| Cross-service E2E      | 真 `/skills` 與 `/skills/{name}/invoke` JSON、租戶隔離、identity 注入與錯誤碼 | full compose；建議 `e2e-verifier` 腳本/測試集合                 |
| LLM quality evaluation | 自然語言到具體 Skill 的選擇品質與無工具率                                     | 非 blocking CI 的固定資料集評估，見 §7                          |

> 測試可依實際 solution 目錄調整檔名，但不得降低驗證層級。所有 .NET 測試使用既有手寫 fake，不引入 mocking library。

## 2. 共用測試資料與觀測點

### 2.1 身分與租戶

- `tenant-a-user`：tenant `A`、role `USER`。
- `tenant-a-admin`：tenant `A`、role `ADMIN`。
- `tenant-b-user`：tenant `B`、role `USER`。
- 匿名：無有效 JWT，`MaybeUserContext()` 回 `null`。

### 2.2 目錄樣本

> 注意：`template_*` 內建骨架是空殼、不可被路由當工具（`source=="builtin"` 且 `name` 以 `template_` 開頭者一律過濾，與前端同一條規則）。因此可路由的 builtin 範例改用非 template 名（如 `kb_query`）；template_ 的排除見 CSR-P1-031。

```json
[
  {
    "name": "kb_query",
    "description": "從知識庫檢索答案",
    "required_role": "USER",
    "source": "builtin",
    "revision": 1,
    "input_schema": {
      "query": { "type": "str", "required": true, "min_length": 1 }
    }
  },
  {
    "name": "tenant_a_private_search",
    "description": "租戶 A 的專用檢索",
    "required_role": "USER",
    "source": "custom",
    "revision": 3,
    "input_schema": {
      "question_text": { "type": "str", "required": true }
    }
  },
  {
    "name": "admin_report",
    "description": "管理報表",
    "required_role": "ADMIN",
    "source": "custom",
    "revision": 2,
    "input_schema": {
      "prompt": { "type": "str", "required": true }
    }
  }
]
```

### 2.3 必要 fake 觀測點

`FakeWorkflowService` 必須可設定及記錄：catalog 回值/例外、catalog 呼叫次數與 `UserContext`、skill invoke 回值/例外、invoke 的 skill name、input dictionary、`UserContext` 與順序。fake `ILlmAgent` 必須能記錄收到的 tools、選擇並執行指定 tool、或刻意不呼叫工具。mem0 fake 與 agent fake 共用事件序列，以驗證 recall/tool/remember 的先後。

## 3. P1 deterministic 驗收案例

每一案都必須能在不呼叫真 LLM 的情況下重現；fake agent 的「選工具」是測試安排，不是對模型智力的假設。

### 3.1 候選目錄、角色與租戶安全

#### CSR-P1-001 — USER 只取得可用 Skill

- 優先級／Phase：Must / P1
- 前置條件：以 `tenant-a-user` 呼叫；catalog 為 §2.2。
- Given：目錄同時含 USER 與 ADMIN Skill。
- When：建立本輪 tools。
- Then：tools 含 `kb_query`、`tenant_a_private_search`，不含 `admin_report`；catalog 恰呼叫一次並收到 tenant A/USER 身分。
- 驗證層級／位置：Service unit / `Platform.Service.Tests`。

#### CSR-P1-002 — ADMIN 可取得 USER 與 ADMIN Skill

- 優先級／Phase：Must / P1
- 前置條件：以 `tenant-a-admin` 呼叫；catalog 為 §2.2。
- Given：`required_role` 分別為 `USER`、`ADMIN`。
- When：建立本輪 tools。
- Then：三個 Skill 均在候選中；角色比較採既有精確語義，不自行正規化未知角色。
- 驗證層級／位置：Service unit / `Platform.Service.Tests`。

#### CSR-P1-003 — 匿名裸聊且不讀目錄

- 優先級／Phase：Must / P1
- 前置條件：`UserContext=null`。
- Given：catalog fake 若被呼叫即令測試失敗。
- When：執行阻塞或串流聊天。
- Then：agent 收到 `tools=null`；catalog 與 skill invoke 呼叫次數皆為 0；聊天仍正常回覆。
- 驗證層級／位置：Service unit + Web integration / 兩個 test projects。

#### CSR-P1-004 — builtin 與 custom 路由地位相同

- 優先級／Phase：Must / P1
- 前置條件：一筆 `source=builtin`、一筆 `source=custom`，其餘欄位皆合格。
- Given：兩筆皆只有一個必填字串欄位。
- When：建立 tools。
- Then：兩者都成為 `LlmTool`；`source` 不影響過濾、排序或 invoke 方式。
- 驗證層級／位置：Service unit / `Platform.Service.Tests`。

#### CSR-P1-005 — 跨租戶 custom Skill 零可見

- 優先級／Phase：Must / P1
- 前置條件：真 backend 中 `tenant_a_private_search` 僅屬 tenant A；建立 A、B 兩個 JWT。
- Given：兩租戶分別經 platform 讀取目錄。
- When：tenant A 與 tenant B 各發一輪已登入聊天，並記錄送給 agent 的 tool names。
- Then：A 可見該 Skill；B 的真 `/skills` 回應及 agent tools 均不含它；B 嘗試直接 invoke 同名 Skill 時仍得到既有不可見語義（404），不得洩漏描述、schema 或存在性。
- 驗證層級／位置：Cross-service E2E / full compose。

#### CSR-P1-006 — workflow 端角色深度防禦

- 優先級／Phase：Must / P1
- 前置條件：USER 身分；ADMIN-only Skill 存在。
- Given：繞過 platform 候選過濾，直接以 USER 身分頭經合法內部代理嘗試 invoke。
- When：呼叫真 `/skills/{name}/invoke`。
- Then：workflow 回 403 ApiError；Skill 未執行。platform 的預先過濾與 workflow 的執行前守衛兩者皆有效。
- 驗證層級／位置：Cross-service E2E。

#### CSR-P1-007 — 呼叫端不可覆寫伺服器 identity

- 優先級／Phase：Must / P1
- 前置條件：tenant A/USER；可回顯執行 context 的測試 Skill。
- Given：input 刻意夾帶其他 tenant/user/role 的 identity keys。
- When：經 platform → workflow 真實 invoke 鏈呼叫。
- Then：夾帶 identity 被剝除；Skill 觀測到的 tenant/user/role 只來自可信身分頭，且不得升權或跨租戶。
- 驗證層級／位置：Cross-service E2E。

### 3.2 Schema 天花板、映射與去重

#### CSR-P1-008 — 恰好一個必填字串欄位可路由（on-point）

- 優先級／Phase：Must / P1
- 前置條件：`input_schema={query:{type:"str",required:true}}`。
- Given：合格 Skill。
- When：建立 tool 並以 fake agent 傳入原文 `比較 Q1 與 Q2`。
- Then：tool 存在；模型側參數仍名為 `question`，內部 invoke body 精確為 `{"query":"比較 Q1 與 Q2"}`。
- 驗證層級／位置：Service unit / `Platform.Service.Tests`。

#### CSR-P1-009 — 零個必填字串欄位被跳過（off-point 0）

- 優先級／Phase：Must / P1
- 前置條件：分列測試 `input_schema=null`、空 object、只有 optional str、只有 required int。
- Given：每筆均無唯一可用的必填字串輸入。
- When：建立 tools。
- Then：該 Skill 不出現在 tools，且不被 invoke；聊天仍可使用其他工具或裸聊。
- 驗證層級／位置：Service unit / parameterized xUnit theory。

#### CSR-P1-010 — 兩個必填欄位被跳過（off-point 2）

- 優先級／Phase：Must / P1
- 前置條件：分列測試兩個 required str，以及一個 required str 加一個 required int。
- Given：Skill 需要多參數才能正確執行。
- When：建立 tools。
- Then：該 Skill 被靜默跳過；不得只挑其中一欄造成錯參呼叫。這是 P1 天花板，待 P3 泛化。
- 驗證層級／位置：Service unit / parameterized xUnit theory。

#### CSR-P1-011 — optional 欄位不破壞單參資格

- 優先級／Phase：Must / P1
- 前置條件：恰好一個 required str，另有零個或多個 optional 欄位。
- Given：只有一個必須由聊天提供的字串。
- When：建立 tools。
- Then：Skill 可路由，invoke body 只含選中的 required str，不捏造 optional 值。
- 驗證層級／位置：Service unit。

#### CSR-P1-012 — 非陣列 catalog 不產生動態工具

- 優先級／Phase：Must / P1
- 前置條件：catalog 分別為 object、string、null JSON。
- Given：傳輸成功但頂層形狀不是陣列。
- When：解析 catalog。
- Then：不拋例外、不產生動態 Skill；殘留靜態工具仍按設計可用。
- 驗證層級／位置：Service unit / xUnit theory。

#### CSR-P1-013 — Skill 優先的同名去重

- 優先級／Phase：Must / P1
- 前置條件：動態 Skill 與殘留 `ChatToolSpecs` 產生相同 tool name。
- Given：同名各一，另各有一個不撞名工具。
- When：合併候選。
- Then：同名只保留動態 Skill 版且總數精確為 3；比較使用 `StringComparer.Ordinal`。不撞名者皆保留。
- 驗證層級／位置：Service unit。

#### CSR-P1-014 — Description 帶正確輸入提示

- 優先級／Phase：Should / P1
- 前置條件：description 與唯一 input key `question_text`。
- Given：Skill 可路由。
- When：映射成 `LlmTool`。
- Then：保留原 description，並包含 `question_text` 與「一段自然語言」提示；不得洩漏其他租戶 metadata。
- 驗證層級／位置：Service unit。

#### CSR-P1-031 — builtin `template_*` 骨架不可被路由

- 優先級／Phase：Must / P1
- 前置條件：目錄含 `source=="builtin"` 且名稱以 `template_` 開頭的空殼骨架（如 `template_retrieval`、`template_stats`，撰寫端 settings-skill-redesign 併入同一 `GET /skills` 目錄），以及至少一個非 template 的可路由 Skill。
- Given：template_ 骨架縱使有合格的單一必填字串 `input_schema`，仍是不可路由的空殼。
- When：建立 tools。
- Then：`template_*`（builtin）一律不出現在 tools；非 template 的 Skill 正常成為工具。過濾條件是 `source=="builtin"` 與 `template_` 前綴的**合取**——`source=="custom"` 的 `template_` 前綴不被剝除（off-point：驗 source 維度）。
- 驗證層級／位置：Service unit / `Platform.Service.Tests`。

### 3.3 Invoke 與真 JSON 輸出

#### CSR-P1-015 — 呼叫正確 Skill、輸入鍵與身分

- 優先級／Phase：Must / P1
- 前置條件：fake agent 明確選 `tenant_a_private_search`，arg=`原始問句`。
- Given：tool 閉包已捕捉 name、input key、tenant A/USER context。
- When：fake agent 執行 tool。
- Then：`InvokeSkillAsync` 恰呼叫一次，name、字串內容與 `UserContext` 完全相符；不得改打舊 `/workflows/{name}/invoke`。
- 驗證層級／位置：Service unit。

#### CSR-P1-016 — 標準 output key 取值

- 優先級／Phase：Must / P1
- 前置條件：分列回傳 `{skill,output:{answer:"A"}}`、`final_answer`、`report`、`summary`。
- Given：每次只有一個標準字串欄位。
- When：工具結果轉成餵回模型的字串。
- Then：分別得到精確的 `A` 等字串，不含外層 JSON。
- 驗證層級／位置：Service unit / xUnit theory。

#### CSR-P1-017 — 非標準輸出回傳 raw JSON

- 優先級／Phase：Must / P1
- 前置條件：回傳 `{skill,output:{rows:[1,2],count:2}}`；另測沒有 `output` 外層的 object。
- Given：沒有可用的標準字串 key。
- When：擷取答案。
- Then：回傳被選定 output object（或根 object）的合法 raw JSON，資訊不遺失且不拋例外。
- 驗證層級／位置：Service unit。

#### CSR-P1-018 — 真跨服務 JSON 形狀不被 DTO 靜默改寫

- 優先級／Phase：Must / P1
- 前置條件：full compose；至少一個 builtin 與一個 tenant custom Skill。
- Given：workflow 真 `/skills` 回 `{name,description,required_role,source,revision,input_schema}` 陣列，invoke 真回 `{skill,output}`。
- When：platform 先取 catalog 再 invoke。
- Then：`required_role`、`input_schema` 保持 snake_case 且可解析；custom revision/schema 不丟失；invoke 的外層 `output` 被正確解包。此案不得用 fake HTTP response 代替。
- 驗證層級／位置：Cross-service E2E。

### 3.4 錯誤、fallback 與不中斷

#### CSR-P1-019 — 無合適工具時純聊天

- 優先級／Phase：Must / P1
- 前置條件：fake agent 收到 tools 但被安排不呼叫任何一個。
- Given：一般寒暄問句。
- When：執行阻塞與串流聊天。
- Then：回覆正常、skill invoke 次數為 0；不得強制分類或派工。
- 驗證層級／位置：Service unit + Web integration。

#### CSR-P1-020 — Catalog HTTP 502 回退靜態工具

- 優先級／Phase：Must / P1
- 前置條件：`GetSkillCatalogAsync` 拋對應 workflow 502 的例外。
- Given：殘留靜態工具表非空。
- When：已登入者聊天。
- Then：聊天不中斷；agent 收到角色允許的靜態工具；記錄 warning；不產生半套動態工具。
- 驗證層級／位置：Service unit。

#### CSR-P1-021 — Catalog 傳輸錯誤與逾時皆 best-effort

- 優先級／Phase：Must / P1
- 前置條件：分列注入 DNS/connection failure、`HttpRequestException`、逾時，以及壞 JSON 解析錯誤。
- Given：目錄尚未成功建立。
- When：已登入者聊天。
- Then：各錯誤均走與 502 相同的靜態工具 fallback，聊天不回 5xx；warning 不得記錄 token、完整 prompt 或敏感 catalog。
- 驗證層級／位置：Service unit；至少一種 connection failure 另做 Web integration。

#### CSR-P1-022 — Catalog 失敗且無靜態工具時裸聊

- 優先級／Phase：Must / P1
- 前置條件：catalog 失敗；測試配置的 `BuildStaticTools` 結果為空。
- Given：已登入使用者仍可使用一般聊天。
- When：發送訊息。
- Then：agent 在沒有可用工具的情況正常作答，API 不失敗。
- 驗證層級／位置：Service unit。

#### CSR-P1-023 — 單一 Skill invoke 的 HTTP/傳輸錯誤不炸整輪

- 優先級／Phase：Must / P1
- 前置條件：分列注入 404、403、422、504、500 映射例外與 connection reset/timeout。
- Given：fake agent 已選中該 Skill。
- When：執行 tool。
- Then：每種錯誤都轉成含 Skill name 的失敗字串交回 agent；聊天仍產生最終回覆並記 warning，不把未處理例外洩露成 API 5xx。
- 驗證層級／位置：Service unit / xUnit theory。

#### CSR-P1-024 — 既有 workflow `kb_query → rag_qa` fallback 不回歸

- 優先級／Phase：Must / P1
- 前置條件：`kb_query` 仍由殘留 `ChatToolSpecs` 提供；第一次結果 `answer_mode=ABSTAIN`。
- Given：動態目錄接入後靜態工具仍存在。
- When：fake agent 選中 workflow 版 `kb_query`。
- Then：依既有邏輯再 invoke `rag_qa`，並如實標示 fallback；不得把此行為誤加到 P1 的 skill 版委派。
- 驗證層級／位置：Service unit / 既有回歸測試。

### 3.5 阻塞、SSE 與 mem0 順序

#### CSR-P1-025 — 阻塞 API 融合 Skill 結果

- 優先級／Phase：Must / P1
- 前置條件：有效 JWT；fake agent 執行 tool 後回最終文字 `融合後答案`。
- Given：catalog 與 invoke 成功。
- When：`POST /api/chat`。
- Then：HTTP 200 與既有 `ChatResponse` 形狀不變；response 只呈現最終答案，不新增 tool/trace DTO 或中間事件。
- 驗證層級／位置：Web integration。

#### CSR-P1-026 — SSE 精確 wire contract

- 優先級／Phase：Must / P1
- 前置條件：有效 JWT；可控 fake stream 先產出 `第一段`，等測試閘門開啟後才允許產出 `第二段` 並結束。
- Given：tool 結果已由 agent 內部融合。
- When：以 `ResponseHeadersRead` 呼叫 `POST /api/chat/stream`，逐段讀 raw bytes；在第二段與 stream completion 仍被閘門阻擋時先讀第一個事件，再開閘讀完。
- Then：第一個 `data:第一段\n\n` 必須在開閘前可見，證明不是結束後整包吐出；完整 body 精確為 `data:第一段\n\ndata:第二段\n\n`。每事件有空行結尾；不得出現 `data: 第一段`、自訂 event type、原始 tool JSON 或 trace metadata。
- 驗證層級／位置：Web integration 以 gated fake stream + raw incremental read 斷言；另以 full compose 經實際 frontend nginx（及 Vite dev proxy 的獨立 smoke）確認第一事件在後續事件前到達且 SSE path 未 buffering，不先用寬鬆 SSE parser 正規化。

#### CSR-P1-027 — SSE 與阻塞路徑使用同一動態目錄規則

- 優先級／Phase：Must / P1
- 前置條件：相同 JWT、catalog 與 fake agent。
- Given：分別呼叫 blocking 與 streaming endpoint。
- When：兩輪建立 tools。
- Then：兩路收到相同 tool names、角色過濾、schema 跳過及去重結果；各輪 catalog 恰取一次。
- 驗證層級／位置：Service unit + Web integration。

#### CSR-P1-028 — 阻塞 mem0 順序與記憶內容

- 優先級／Phase：Must / P1
- 前置條件：共用事件記錄器；tool 結果被融合成 `最終答案`。
- Given：mem0 recall、agent/tool invoke、mem0 remember 均可觀測。
- When：執行 `ChatAsync`。
- Then：順序精確為 `Recall → Agent/Tool → Remember`；remember 儲存使用者原訊息與 `最終答案`，不是原始 Skill JSON 或中間輸出。
- 驗證層級／位置：Service unit。

#### CSR-P1-029 — 串流 mem0 在完整列舉後 remember

- 優先級／Phase：Must / P1
- 前置條件：stream 至少兩 chunk；共用事件記錄器。
- Given：consumer 完整列舉 stream。
- When：讀取所有 chunk。
- Then：recall 在第一個 agent/tool 動作前；remember 在最後一個 chunk 完成後且只呼叫一次，內容是完整串接後回覆。
- 驗證層級／位置：Service unit。

#### CSR-P1-030 — 目錄取得頻率符合無快取的 P1 邊界

- 優先級／Phase：Should / P1
- 前置條件：同一已登入使用者連續發兩輪聊天。
- Given：P1 刻意未加 catalog cache。
- When：完成兩輪。
- Then：`GetSkillCatalogAsync` 每輪一次、合計精確兩次；單輪不得重複讀目錄。此案記錄 N+1 現況，不要求 backend round-trip 數改善。
- 驗證層級／位置：Service unit；效能基線可在 E2E 記錄。

## 4. P2–P4 延後驗收門檻（不阻擋 P1）

#### CSR-P2-001 — 出處融入正文而非新增 SSE 通道

- 優先級／Phase：Future / P2
- 前置條件：Skill answer 節點可產生引用字串。
- Given：答案帶出處。
- When：阻塞與串流聊天。
- Then：引用出現在自然語言正文；trace 預設隱藏；SSE 仍符合 CSR-P1-026。
- 驗證層級／位置：workflow unit + Web integration + frontend E2E。

#### CSR-P3-001 — 多參 schema 與缺參補問

- 優先級／Phase：Future / P3
- 前置條件：`LlmTool`/AIFunction 已泛化 JSON args。
- Given：Skill 有兩個以上必填欄位或非字串欄位。
- When：使用者資訊不足後再於下一輪補充。
- Then：模型先詢問缺項，再以型別正確的完整 input invoke；短期記憶保存上下文，不自建重複 slot state。
- 驗證層級／位置：Service integration + LLM evaluation。

#### CSR-P4-001 — AG-UI 若納入時才掛 server-side Skill

- 優先級／Phase：Future / P4（預設非目標）
- 前置條件：產品決策明確要求 CopilotKit 路由知識 Skill。
- Given：AG-UI 仍使用獨立 agent。
- When：實作 server-side tools。
- Then：不混淆既有 client UI tools，且 AG-UI 的標準 `data: ` 契約不被 REST chat 的 `data:` 契約污染。
- 驗證層級／位置：Platform.Web integration + frontend E2E。

## 5. 明確非目標／反向驗收

以下條件在 P1 必須保持「沒有發生」：

- 不新增 router/classifier 模型或第二次分類 LLM 呼叫。
- 不新增明確 Skill 選單，不要求非技術使用者輸入 Skill name。
- 不修改 `/api/chat`、`/api/chat/stream` request/response DTO，不新增 SSE event/metadata channel。
- 不修改 AG-UI agent、`Program.cs` 或其 `data: `（有空格）協定。
- 不新增 DB migration、環境變數、MCP server 或執行期 MCP 依賴。
- 不為 catalog 加快取；30–60 秒 per-`(tenant,role)` cache 只在量測證明需要後另案處理。
- 不把 skill 版 invoke 自動接上 workflow 版 `kb_query → rag_qa` abstain fallback。
- 不將多參或非字串 Skill 偽裝成單字串工具；它們在 P1 應被跳過。

可用 source diff/架構檢查加一個 non-functional test job 驗證改動界線：P1 生產碼應收斂於 `ChatService.cs`；測試支援僅修改兩份 fake/相應測試。若實作確有必要超出此界線，須先更新設計與本文件，而不是靜默擴張。

## 6. 需求追溯矩陣

| 來源需求／決策                                      | 驗收案例                            | 覆蓋結果                                      |
| --------------------------------------------------- | ----------------------------------- | --------------------------------------------- |
| 01-plan 目標 1：自然語言自動選具體 Skill 或純聊天   | CSR-P1-004、008、015、019；§7       | 候選與 invoke deterministic；選擇品質另評估   |
| 01-plan 目標 2：五類與撰寫端共用錨點                | CSR-P1-004、008；CSR-EVAL-001       | `template_*` 與 custom 同池，不建第二分類器   |
| 01-plan 目標 3：結果融回 blocking/SSE               | CSR-P1-016–018、025–027；CSR-P2-001 | P1 最終答案與 wire contract；出處列 P2        |
| 01-plan 目標 4：多租戶與角色                        | CSR-P1-001、002、005–007、018       | 預過濾、真租戶隔離、403、identity 防竄改      |
| 01-plan 非目標：公開 API/SSE/AG-UI 不變             | CSR-P1-025、026；CSR-P4-001；§5     | blocking DTO、兩種 SSE 空格契約明確分離       |
| 02-spec §1：方案 A，以純聊天收尾                    | CSR-P1-019；§5；CSR-EVAL-001        | 無第二分類器，允許 no-tool                    |
| 02-spec §2.2：builtin/custom、角色及租戶候選        | CSR-P1-001–007                      | 完整覆蓋                                      |
| 02-spec §2.3 / design §1.3：單一必填 str 天花板     | CSR-P1-008–012、014                 | 0/1/2 邊界與 mixed-type on/off-point          |
| design §1.4：Skill 優先、Ordinal 去重               | CSR-P1-013                          | 精確數量及優先來源                            |
| design §2：`question` → inputKey → `{skill,output}` | CSR-P1-008、015–018                 | fake 與真 JSON 雙層覆蓋                       |
| design §4：fallback 四層                            | CSR-P1-019–024                      | no-tool、catalog、invoke、既有 abstain 全覆蓋 |
| design §5：mem0 recall-before / remember-after      | CSR-P1-028、029                     | blocking 與 stream 各一                       |
| design §3/§6：兩 REST 路徑與 API 零變更             | CSR-P1-025–027                      | Web 層覆蓋                                    |
| design §7：每輪 fetch、N+1/快取延後                 | CSR-P1-030；§5                      | 釘死 P1 頻率與非目標                          |
| design §8：多參/slot filling 延後                   | CSR-P1-009、010；CSR-P3-001         | P1 跳過，P3 升級門檻                          |
| design §11：skill 版無 abstain、AG-UI 不做          | CSR-P1-024；CSR-P4-001；§5          | 反向驗收                                      |
| backend trust / schema /錯誤碼真契約                | CSR-P1-005–007、018、023            | E2E 避免 fake 掩蓋序列化與安全問題            |

## 7. 真 LLM 路由品質評估（不得作為 deterministic CI）

#### CSR-EVAL-001 — 固定語料集的具體 Skill 選擇品質

- 優先級／Phase：Quality gate / P1 上線前與模型變更時；非每次 PR 的 blocking CI。
- 前置條件：固定、版本化且去識別化的語料集；至少涵蓋 retrieval/compare/infer/inspire/stats、tenant custom Skill、一般寒暄與模糊問題。每類至少 20 題，並記錄可接受 Skill 集合，而非強迫單一名稱。
- Given：固定 catalog snapshot、`CHAT_MODEL`、LiteLLM route、system prompt 與模型參數；每題至少重跑 3 次。
- When：讓真 LLM 自行 tool-call。
- Then：報告每類 precision/recall、整體適配題 top-1 acceptable-tool rate、無工具題的 no-tool rate、越權工具暴露/呼叫率；安全硬門檻為越權呼叫率 0%。首版建議品質門檻為適配題 ≥ 85%、無工具題 no-tool ≥ 90%，但應以基線校準後由產品確認。
- 驗證層級／位置：獨立 evaluation job；保存摘要、模型版本與 catalog hash，不保存敏感 prompt 全文。

品質評估失敗代表需要調整 Skill description、catalog 或模型選擇；它不應被改寫成「某一句話必須永遠呼叫某工具」的 xUnit，因溫度 0.7、模型版本與供應商路由都可能造成合理波動。相對地，候選白名單、角色剔除、invoke body、錯誤 fallback、mem0 順序及 SSE bytes 都是 deterministic，必須留在 blocking CI。

## 8. 建議執行順序與證據

1. `dotnet build` platform solution。
2. 執行 `Platform.Service.Tests`，確認 CSR-P1-001–024、027–030。
3. 執行 `Platform.Web.Tests`，確認 CSR-P1-003、019、021、025–027。
4. 啟動 full compose，以兩租戶與兩角色執行 CSR-P1-005–007、018；保留經遮罩的 request/response shape、狀態碼與服務 log 摘要。
5. 在上線前或 `CHAT_MODEL`/prompt/catalog 描述有變更時執行 CSR-EVAL-001。

驗收報告需列出：commit、環境、模型版本（僅 quality eval）、通過/失敗/跳過案例、跳過理由、raw SSE 片段、E2E 使用的 tenant/role（去識別化）及已知偏差。P2–P4 案例在 P1 報告中標示 `Not in scope`，不得誤報為失敗或已完成。
