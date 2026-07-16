# 實作規格 — 聊天 → Skill 路由(Chat-to-Skill Routing)

> 狀態:**P0 探查完成,方案定案,細節設計就緒**。承接 [01-plan.md](01-plan.md)。
> 一句話結論:**方案 A(LLM function-calling)不只可用,已經整條接好並在跑** —— 現況是把「5 個固定 workflow」當工具掛給 LLM 自選;本計畫只是把工具來源從**寫死的 workflow 清單**換成**動態的 Skill 目錄**(內建 + 租戶自訂,租戶/角色過濾)。P1 是一個小 diff,不是新建管線。

---

## 0. P0 探查結論(程式即事實,附 file:line)

| 探查項目 | 事實 | 出處 |
|---|---|---|
| Agent Framework tool/function-calling 是否可用 | **已可用且已接線**。`ILlmAgent.CompleteAsync/StreamAsync` 收 `IReadOnlyList<LlmTool>?`;非空即啟用 function calling,工具迴圈由框架處理 | `platform/src/Platform.Service/Abstractions/ILlmAgent.cs:10-13` |
| 工具如何轉成 AIFunction | `LlmTool` → `AIFunctionFactory.Create((string question, ct) => …, name, description)`,掛進 run-level `ChatOptions.Tools`;`ChatClientAgent` 內建 function-calling 迴圈自動執行工具 | `platform/src/Platform.Web/Infrastructure/AgentFrameworkLlmAgent.cs:67-85` |
| 聊天現在路由到什麼 | **5 個寫死的 workflow**(`rag_qa`/`kb_query`/`summarize`/`triage`/`analyze_report`),經 `ChatToolSpecs` 陣列 → `_workflows.InvokeAsync`(打 `/workflows/{name}/invoke`) | `ChatService.cs:190-203, 234-242` |
| 已登入才掛工具 | `BuildTools(userCtx)`:`userCtx is null`(匿名)回 `null` 裸聊;登入才掛工具;`RequiredRole` 不符即跳過(analyze_report 僅 ADMIN) | `ChatService.cs:212-232` |
| 兩條 REST 路徑都已路由 | `/api/chat`(阻塞)與 `/api/chat/stream`(SSE)共用 `ChatService` + `BuildTools(MaybeUserContext())`;JWT 有效即取身分,無效即匿名 | `ChatController.cs:28, 47, 60-61` |
| AG-UI 是另一隻 agent | `/api/copilot/agui` 用 **獨立** 的 `copilotAgent = IChatClient.AsAIAgent(instructions,…)`,**不走** `ChatService`/`BuildTools`;工具是前端 client tools(操作 UI 用),與知識檢索無關 | `Program.cs:194-200` |
| Skill 目錄怎麼取 | 已有 `WorkflowService.GetSkillCatalogAsync(ctx)` → workflow `GET /skills`;platform `GET /api/skills/catalog` 也是同一條 | `WorkflowService.cs:82-83`、`SkillController.cs:39-41`、`workflow/app/main.py:96-114` |
| 目錄內容 | 內建(repo `skills/*.yaml`)+ 本租戶自訂(來自 backend),每筆 `{name, description, required_role, source, revision, input_schema}` | `workflow/app/schemas.py:35-43` |
| 租戶隔離 | 自訂 skill 每次向 backend 依 `X-Tenant-Id` 取,跨租戶 backend 回 404 → 不可見;內建為全租戶共用 | `workflow/app/skills/custom.py:65-113` |
| Skill invoke 的角色/schema 把關 | invoke 端點依序驗 存在(404)→ 角色(403)→ input_schema(422);identity 鍵由伺服器注入,呼叫端夾帶會被剝除 | `workflow/app/main.py:134-212, 40-54` |
| Skill invoke 代理 | 已有 `WorkflowService.InvokeSkillAsync(name, input, ctx)` → `/skills/{name}/invoke`,錯誤碼映射與 workflow invoke 一致 | `WorkflowService.cs:53-65` |
| intent_classification 是什麼層級 | 是 **kb_query skill 執行圖內部** 的一個節點(規則優先、UNKNOWN 才問 LLM 且 confidence ≥ 0.6),**不是** 聊天層的路由器 | `workflow/app/kbquery/nodes/intent_classification.py:65-103` |

**推論**:方案 B(先分類再派工)所需的「意圖分類器」在架構上是 **skill 內部** 的執行步驟,不是聊天層路由。聊天層的路由器就是 **LLM 自己**(方案 A)。把 intent_classification 拉到聊天層會多一次 LLM 呼叫、多一份要維護的分類表,而 Agent Framework 的 function-calling 已經把「選哪個工具」這件事免費做掉了。

---

## 1. 定案:路由方案 **A(LLM function-calling)**,以 D 的姿態收尾

- **選 A 不選 B**:tool-calling 已接線且已在生產路徑跑;B 要新增分類器模型 + 分類→skill 對照表 + 一次額外 LLM 呼叫,換來的「可控/可測」在 A 這邊由「工具清單本身就是白名單 + workflow invoke 的三道把關(404/403/422)」提供。B 是重造一個 A 已經給的東西。
- **收尾成 D**:保留 01-plan 的 D 精神 —— A 為主 + 「無合適 skill → LLM 不呼叫任何工具 → 純聊天」的回退(這是 function-calling 的內建行為,零成本);C(明確選單)**不做**(YAGNI,非技術/聊天優先原則反對,且無人要求)。
- **本計畫的唯一實質改動**:`ChatService.BuildTools` 的工具來源,從寫死的 `ChatToolSpecs` 換/併成 **動態 Skill 目錄**。其餘(agent、SSE、mem0、span、三路徑分流)全不動。

---

## 2. 五大維度(釘死,附插入點)

### 2.1 Model
- **路由決策模型 = 聊天模型本身**,`CHAT_MODEL`(預設 `gpt-4o-mini`),經 LiteLLM(`LLM_BASE_URL` :4000)。做 function-calling 的那顆 LLM **就是**路由器,沒有第二顆。插入點:`AgentFrameworkLlmAgent` 建構時 `GetChatClient(options.ChatModel)`(`AgentFrameworkLlmAgent.cs:36`;`LlmOptions.ChatModel` 於 `ServiceOptions.cs:11`)。
- **不新增** 專屬 router/classifier 模型(方案 B 才需要)—— 刻意的簡化。
- **Skill 執行內部的 LLM**(如 kb_query 圖裡 intent_classification / answer_composer 用的)= workflow `settings.llm_model`(預設 `gpt-4o-mini`,`workflow/app/settings.py:10`、`llm.py:18`)。這是**路由下游**、skill 圖內部的事,與路由決策無關,不要混為一談。
- 溫度沿用 0.7(`LlmOptions.Temperature`);路由不需要另調溫度。

### 2.2 Skill
- **候選來源**:`WorkflowService.GetSkillCatalogAsync(userCtx)` → workflow `GET /skills`,回內建 + 本租戶自訂。**每輪聊天以 `userCtx` 取一次**(僅登入者)。
- **租戶過濾**:目錄端已做 —— 自訂 skill 依 `X-Tenant-Id` 向 backend 取,跨租戶不可見;內建全租戶共用(`custom.py:65-113`)。platform 端不需再過濾租戶。
- **角色過濾**:在 `BuildTools` **預先剔除** `required_role == "ADMIN"` 而使用者非 ADMIN 的 skill(沿用現有 analyze_report 的 `RequiredRole` 模式,`ChatService.cs:222-225`)—— 不把使用者無權執行的工具端到 LLM 面前。workflow invoke 端另有 403 把關(深度防禦,`main.py:166-173`)。
- **builtin vs custom**:一律平等轉成工具;`source` 只影響前端徽章顯示,不影響路由。
- **input_schema 的用途**:P1 用來挑「要餵給工具的那一個字串輸入鍵名」(見 §2.3);P3 用來支援多參數與缺參補問。
- **去重**:與殘留的 `ChatToolSpecs`(尚未 skill 化的 workflow)以工具名去重,skill 目錄優先。

### 2.3 Tool
- **方案 A 的映射**:每個候選 skill → 一個 `LlmTool`(`ChatService.BuildTools`,`ChatService.cs:212`)→ 一個 `AIFunction`(`AgentFrameworkLlmAgent.ToRunOptions`,`AgentFrameworkLlmAgent.cs:67-85`)。
  - `LlmTool.Name` = skill 名(需為合法函式名;skill 名 pattern `^[a-z][a-z0-9_]{2,63}$`,`workflow/app/engine/skill.py:69`,天然合法)。
  - `LlmTool.Description` = skill `description`(+ 必要時附「輸入為 <鍵名>:<型別>」提示,幫 LLM 決定何時呼叫)。
  - `LlmTool.InvokeAsync` = `(arg, ct) => _workflows.InvokeSkillAsync(name, {inputKey: arg}, userCtx, ct)`(改打 skill invoke,`WorkflowService.cs:53`),輸出以 `ExtractAnswer`(`ChatService.cs:270-281`)取字串回給模型。
- **Schema 形狀(P1 刻意簡化)**:沿用現有「單一字串參數 `question`」的形狀(`LlmTool.cs` 的 ponytail 註解已標明升級路徑)。輸入鍵名取該 skill `input_schema` 中**唯一的必填字串欄位**;多必填/非字串 → P3 泛化成 JSON-args 的 `AIFunction`。
- **與 workflow `@tool` registry 的關係(勿混淆,兩層)**:workflow 的 `@tool`(`tool_registry.py`)是 skill 的 **YAML flow 內部** 呼叫的低階 HTTP/local callable(retrieve/embed/…)。本計畫講的「工具」是 **skill 當成聊天的 function**。兩者不同層:聊天工具(skill)→ workflow invoke → skill 圖 → 圖內部才用到 `@tool`。

### 2.4 Hook
Agent Framework 這條路徑**沒有通用 hook 框架**;扮演各 hook 角色的既有介入點如下(全部沿用,不新建 hook 機制):
- **pre-invoke 角色/租戶守衛**:`BuildTools` 依 `required_role` 剔除(端出前) + 目錄的租戶過濾 + workflow invoke 的 403/租戶 404(執行前)。三處,深度防禦。
- **回退純聊天**:function-calling 內建 —— LLM 判定無合適工具就不呼叫,直接作答(即純聊天)。**零程式碼**。另保留既有確定性回退:kb_query 證據不足棄答 → 自動改打 rag_qa 兜底並如實註明(`ChatService.cs:245-254`);工具呼叫失敗回錯誤字串給模型轉述而**不炸整輪**(`ChatService.cs:258-262`)。
- **結果融回 SSE**:**框架的 function-calling 迴圈**負責 —— 執行工具 → 把結果餵回模型 → 模型續寫,續寫內容照常以 SSE `data:` chunk 串出(`ChatController.cs:49-55`)。skill 輸出**不是**原封注入串流,而是被模型**融進**答案。故 **SSE `data:` 契約零改動**(關鍵發現,見 §5)。
- **mem0 recall/remember 相對路由的順序**:recall 在組 prompt **之前**(`ChatService.cs:172`,`BuildPromptAsync`);remember 在**完整回覆之後**(阻塞 `ChatService.cs:70`、串流 `:140`)。路由/工具呼叫發生在 `_agent.CompleteAsync/StreamAsync` **之內**,即夾在 recall 與 remember 之間。順序天然正確:mem0 記的是**融合後的最終回覆**,不需要任何調整。

### 2.5 MCP
- **執行期路由路徑上沒有任何 MCP server**。skill 呼叫的是 workflow 的 `@tool` callable(HTTP 打 backend / 本地函式),不是 MCP。
- codebase-memory-mcp / playwright 等 MCP 純屬**開發期 agent 工具**(給 code-reviewer/e2e-verifier 用),**永遠不在**使用者請求的資料流裡。
- 明確結論:**N/A —— 本功能不引入、不依賴任何 MCP。**

---

## 3. 01-plan §6 開放問題逐條拍板

1. **路由粒度(5 類 vs 具體 skill)**:**具體 skill**。LLM 直接從工具清單(= 整份 Skill 目錄)選具體 skill。五個問句原型 `retrieval`/`compare`/`infer`/`inspire`/`stats` 現在就是目錄裡五個具體的內建錨點 skill(`template_retrieval`/`template_compare`/`template_infer`/`template_inspire`/`template_stats`,與撰寫端共用同一套分類 —— 見 settings-skill-redesign),因此「選 5 類」與「選具體 skill」並非兩層:選中某一類即等於選中它的 `template_*` 錨點 skill,而自訂 skill 與這五個錨點平等地一起被端到 LLM 面前。(`stats` 是聚合統計運算,與 `compare` 的相對排序不同;但路由對所有錨點一視同仁地當目錄候選,類別語意不影響機制。)至於 kb_query 內部的 intent_classification,仍是 skill 執行圖內的細節,不上升到聊天路由層。
2. **A vs B**:**A**(見 §1)。
3. **SSE 呈現**:**單一融合答案**,由模型把 skill 輸出寫進正文;出處/trace **預設不顯示**(非技術優先原則),需要時經未來的展開 UI(前端,非本計畫)。`data:` 格式不變。
4. **多輪缺參數反問**:P1 靠 LLM 自然反問(工具 description 標明需要什麼,模型缺資訊自然會問);P3 才做結構化 slot-filling(泛化多參 `AIFunction` + 框架的 partial-args 迴圈)。見 §6。
5. **三路徑是否都支援**:`/api/chat` + `/api/chat/stream` **P1 同時支援**(共用一條 `ChatService` 程式路徑,無額外成本);`/api/copilot/agui` **不在本計畫**(獨立 agent、client-side 工具、定位是 UI 操作助理而非知識檢索)→ 列 P4/非目標。

---

## 4. API / DTO 變更

**對外契約:零變更。** 三個聊天端點的 request/response/SSE 格式全部不動(01-plan §2 非目標)。改動全在 platform 內部:

- `ChatService.cs`
  - 新增 `Task<IReadOnlyList<LlmTool>?> BuildToolsAsync(UserContext?, CancellationToken)`(改成 async,因要抓目錄);`ChatAsync`/`StreamChatAsync` 兩處呼叫點改 await。
  - 新增私有 `SkillCatalogToTools(catalogJson, userCtx)`:解析 `GetSkillCatalogAsync` 的 `JsonElement`,依 `required_role` 過濾,挑 input key,產 `LlmTool`。
  - `ChatToolSpecs` 保留為「尚未 skill 化的 workflow」;與目錄以工具名去重。settings-skill-redesign 把這些 flow 遷成 skill 後,此陣列縮到空即可整段刪除(留 `ponytail:` 註解標明)。
- 依賴:`ChatService` 已注入 `IWorkflowService _workflows`(`ChatService.cs:31`),`GetSkillCatalogAsync`/`InvokeSkillAsync` 都在其上,**不需新增建構子依賴**。
- **無新增 DTO、無新增端點、無 DB 變更、無 env 變更。**

---

## 5. SSE 結果融合格式(不得破 `data:` 契約)

- 不新增任何 SSE 事件型別、不改 `data:`(無空格)格式。skill 輸出經 function-calling 迴圈餵回模型,模型**續寫的自然語言**照舊逐 chunk 走 `ChatController.cs:49-55` 的 `data:<line>\n` + 空行 flush。
- 阻塞路徑同理:`_agent.CompleteAsync` 內部跑完工具迴圈才回最終字串。
- 出處/trace:P2 若要「附引用」,做法是讓 skill 的**輸出字串本身**含出處(由 skill 的 answer 節點產),模型融入正文即帶出;**不**在 SSE 另開 metadata 通道(那會破契約)。預設不強推 trace(§3.3)。

---

## 6. 多輪 / 缺參數處理

- **P1**:單字串輸入 skill 佔絕大多數(五個問句原型 `retrieval`/`compare`/`infer`/`inspire`/`stats` —— 即撰寫端與路由端共用的那一套分類,由 workflow 內建的 `template_retrieval`/`template_compare`/`template_infer`/`template_inspire`/`template_stats` 錨點 skill 承載 —— 都是「一段自然語言查詢」)。模型把使用者問句當該字串引數傳入即可,缺資訊時模型自然反問(function-calling 常態)。
- **P3**:把 `LlmTool` 從單字串泛化為多欄位(`input_schema` → `AIFunctionFactory` 的多參 delegate/JSON schema),交由框架的 function-calling 在參數不齊時讓模型回頭問。`LlmTool.cs` 的 ponytail 註解已預留此升級路徑。
- **不做** 自建 slot-filling 狀態機(YAGNI):短期記憶 sliding window(`ChatService.cs:169`)已提供多輪上下文,模型反問→使用者補答→再呼叫,天然多輪。

---

## 7. 分階(對映 01-plan §7 P1–P4)

| Phase | 內容 | 對映 |
|---|---|---|
| **P1 路由核心** | `BuildTools` 改抓 Skill 目錄(內建+自訂、角色過濾、單字串引數),同時生效於 `/api/chat` 與 `/api/chat/stream`;回退純聊天沿用內建行為 | plan P1(且一次覆蓋兩路徑,超出「單一路徑先做」) |
| **P2 結果融合** | skill 輸出附出處(由 skill answer 節點產,融入模型正文);trace 預設隱藏。多數已由 function-calling 內建,P2 主要是「讓 skill 產出可引用的字串」+ 前端可選展開 | plan P2 |
| **P3 多輪補參** | `LlmTool` 泛化多參 → `input_schema` 缺欄位時模型反問補齊 | plan P3 |
| **P4 擴 AG-UI(視需要)** | 若要讓 CopilotKit 側也能路由 skill,替 `copilotAgent` 掛同一批 server-side skill 工具。**預設不做**(定位不同、非目標) | plan P4 |

**P1 的最小 diff 界線**:只改 `ChatService`(工具來源)+ 對應 `ILlmAgent` 呼叫點的 await;不碰 controller、agent、SSE、mem0、Program.cs。

**效能天花板(ponytail)**:每輪聊天多一次 `GET /skills`,而該端點對每個自訂 skill 又逐筆 fetch 一次 `input_schema`(`custom.py:90-113` 的 N+1)。登入者才付這成本。若量測到痛:在 `ChatService` 加 per-(tenant,role) 短 TTL 快取(30–60s,`IMemoryCache`),或讓 backend 的清單端點直接帶 `input_schema` 消掉 N+1。**升級路徑留註解,P1 先不做快取**(先量再優化)。

---

## 8. 測試策略(xUnit,手寫 fake,無 mocking library)

- **`Platform.Service.Tests`**(核心,貼近改動層):
  - 既有 fake `ILlmAgent`:斷言 `BuildToolsAsync` 產出的 `LlmTool` 清單 —— (a) 登入 USER 只拿到 USER skill、拿不到 ADMIN skill;(b) 匿名拿到 `null`;(c) 目錄含內建+自訂時都成工具;(d) 與殘留 `ChatToolSpecs` 依名去重。
  - fake `IWorkflowService`:`GetSkillCatalogAsync` 回預造 `JsonElement` 目錄;`InvokeSkillAsync` 驗證被以正確 `name`+input key 呼叫;回傳經 `ExtractAnswer` 取字串。
  - 錯誤路徑:`InvokeSkillAsync` 拋例外 → 工具回錯誤字串、聊天不中斷(對映 `ChatService.cs:258-262`)。
  - 目錄抓取失敗(workflow 502)→ 退回純聊天或殘留靜態工具,聊天不炸(best-effort,類比 mem0 吞錯)。
- **`Platform.Web.Tests`**(`WebApplicationFactory`):`/api/chat` 與 `/api/chat/stream` 帶有效 JWT 時走含工具路徑、匿名時裸聊;SSE `data:`(無空格)格式與空行結尾不變。
- **不新增** mocking library;沿用手寫 fake(AGENTS 規範 + 記憶 fakes-hide-real-behavior:跨服務行為交給 `e2e-verifier` 打真 workflow/backend 驗一次)。

---

## 9. 跨服務契約影響

- **公開聊天 API / SSE `data:` / AG-UI**:零變更(§4/§5)。
- **backend 信任邊界**:不變 —— 目錄與 invoke 仍經 platform→workflow(帶 `X-Internal-Token` + 身分頭),workflow→backend 取自訂 skill 同理。路由不新增任何服務間呼叫型態,只是**在聊天流程裡多用了兩個既有代理方法**(`GetSkillCatalogAsync`/`InvokeSkillAsync`)。
- **auth**:沿用 —— 匿名裸聊、JWT 有效才掛工具;角色由 `required_role` 過濾 + workflow 403 雙重把關。
- **兩層記憶**:順序與語意不變(§2.4)。
- **Skill Engine 契約**:workflow `/skills`、`/skills/{name}/invoke` 的既有行為與錯誤碼(404/403/422/504/500)全部沿用,本計畫不改 workflow 端。

---

## 10. 刻意簡化總表(ponytail)

- 路由器 = 聊天 LLM 本身,**不建**分類器模型/服務(方案 B 的成本省掉)。
- 工具 schema P1 維持單字串引數,多參/缺參補問延到 P3(`LlmTool.cs` 已預留升級路徑)。
- 明確選單(方案 C)**不做**。
- AG-UI 路由**不做**(P4/非目標)。
- 目錄每輪 fetch **不先加快取**(先量再優化;N+1 天花板與升級路徑已記於 §7)。
- 殘留 `ChatToolSpecs` 不急著砍,待 settings-skill-redesign 把 workflow 遷成 skill 後自然歸零。

> `[改 BuildTools 工具來源:寫死 workflow → 動態 Skill 目錄] → 跳過:分類器/選單/AG-UI/快取/多參,當 [量測到痛 或 進入 P3] 再加。`
