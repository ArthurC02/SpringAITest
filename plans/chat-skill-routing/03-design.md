# 詳細設計 — 聊天 → Skill 路由(Chat-to-Skill Routing)

> 狀態:**實作就緒(implementer-ready)**。承接 [01-plan.md](01-plan.md) 與 [02-spec.md](02-spec.md)。
> 本文件是 **HOW**:02-spec 已定案「方案 A、把工具來源從寫死 `ChatToolSpecs` 換成動態 Skill 目錄」;此處給出確切簽章、映射邏輯、呼叫鏈、await 傳染面、測試個案與落地順序。**不重述** 02-spec 的結論,只在需要時引用其節次(如「見 02-spec §2.3」)。
> **規劃任務,不動生產碼。** 唯一產出即本檔。

---

## 0. 行號校驗(02-spec 引用 vs 現行程式)

以 MCP 逐一核對,**02-spec 的 file:line 全部準確,無漂移**。落地時以下錨點為準:

| 錨點 | 現行位置(已核) | 用途 |
|---|---|---|
| `ILlmAgent.CompleteAsync/StreamAsync(…, IReadOnlyList<LlmTool>? tools, …)` | `ILlmAgent.cs:10,13` | function-calling 入口,tools 非空即啟用 |
| `LlmTool` record(`Name, Description, Func<string,CancellationToken,Task<string>> InvokeAsync`) | `LlmTool.cs:8-11` | Service 層薄工具定義;ponytail 註解已在 `:6` 標明單字串參數升級路徑 |
| `AgentFrameworkLlmAgent.ToRunOptions` → `AIFunctionFactory.Create((string question, ct)=>…, name, desc)` | `AgentFrameworkLlmAgent.cs:67-85`(param 名硬寫 `question` 於 `:79`) | LlmTool → AIFunction,掛 run-level `ChatOptions.Tools` |
| `ChatService.BuildTools(UserContext?)` | `ChatService.cs:212-232` | **本次唯一實質改動點** |
| 靜態工具表 `ChatToolSpecs` | `ChatService.cs:190-203` | 保留為「尚未 skill 化的 workflow」 |
| 角色過濾 `spec.RequiredRole is not null && userCtx.Role != spec.RequiredRole` | `ChatService.cs:222-225` | 沿用同一 predicate 語義 |
| `InvokeWorkflowToolAsync`(工具委派實作 + kb_query abstain 兜底 + catch 錯誤字串) | `ChatService.cs:234-263`(abstain `:245-254`、catch `:258-262`) | skill 版委派的參考範本 |
| `ExtractAnswer`(依 `OutputKeys={answer,final_answer,report,summary}` 取字串) | `ChatService.cs:270-281`(keys `:206`) | skill 輸出 → 給模型的字串 |
| `_workflows` 已注入 | `ChatService.cs:31,48` | 不需新增建構子依賴 |
| `ChatAsync` 呼叫 `_agent.CompleteAsync(messages, BuildTools(userCtx), ct)` | `ChatService.cs:63` | await 傳染點 ① |
| `StreamChatAsync` 呼叫 `_agent.StreamAsync(messages, BuildTools(userCtx), ct)` | `ChatService.cs:97` | await 傳染點 ② |
| mem0 recall(prompt 前)/ remember(回覆後) | recall `:172`;remember 阻塞 `:70`、串流 `:140` | 順序天然正確,見 §5 |
| 短期記憶 `GetRecent` | `ChatService.cs:169` | 多輪上下文來源 |
| `WorkflowService.GetSkillCatalogAsync(ctx) → Task<JsonElement>`(GET `/skills`) | `WorkflowService.cs:82-83` → `GetCatalogAsync:89-100` | 目錄來源(原樣穿透 JSON) |
| `WorkflowService.InvokeSkillAsync(name, Dictionary<string,JsonElement> input, ctx) → Task<JsonElement>`(POST `/skills/{name}/invoke`) | `WorkflowService.cs:53-65` | skill 執行,404/403/422/502 映射同 workflow invoke |
| SkillInfo 目錄項形狀 `{name, description, required_role, source, revision, input_schema}` | `workflow/app/schemas.py:35-43`;端點 `main.py:96-114` | 解析對象 |
| `InputField` `{type∈{str,int,float,bool,list,dict}, required, min_length, default}` | `workflow/app/engine/skill.py:57-63` | 挑輸入鍵的依據 |
| 自訂 skill 目錄 N+1(清單 1 打 + 逐筆 `input_schema` N 打) | `custom.py:87`(gather)+ `_entry:100` | 效能天花板,見 §7 |

---

## 1. `BuildTools` → `BuildToolsAsync`:確切設計

### 1.1 新簽章

```csharp
// 由 private IReadOnlyList<LlmTool>? BuildTools(UserContext?)
// 改為:
private async Task<IReadOnlyList<LlmTool>?> BuildToolsAsync(UserContext? userCtx, CancellationToken ct)
```

改 async 的**唯一理由**:要 `await _workflows.GetSkillCatalogAsync(userCtx, ct)`。其餘結構不變:匿名(`userCtx is null`)仍回 `null`(裸聊,見 `ChatService.cs:214-217`)。

### 1.2 取目錄(每輪一次,僅登入者)

```csharp
if (userCtx is null) return null;

JsonElement catalog;
try
{
    catalog = await _workflows.GetSkillCatalogAsync(userCtx, ct);   // WorkflowService.cs:82
}
catch (Exception ex)   // workflow 502 / 逾時 / 壞 JSON
{
    // best-effort:目錄抓不到不讓聊天炸;退回殘留靜態工具(或 null)。類比 mem0 吞錯。
    _logger.LogWarning(ex, "Skill 目錄取得失敗,退回靜態工具:{訊息}", ex.Message);
    return BuildStaticTools(userCtx);   // 見 §1.5,即現行 ChatToolSpecs 迴圈抽出
}
```

`GetSkillCatalogAsync` 回 `JsonElement`(原樣穿透的 JSON 陣列,**非** typed DTO——`WorkflowService` 刻意不套 DTO 以免靜默吃新欄位,見 `ReadJsonAsync:114-127`)。故解析在 `ChatService` 端手做(§1.3)。

### 1.3 目錄項 → `LlmTool` 映射(`SkillCatalogToTools`)

新增私有純函式(可獨立單元測試,不碰網路):

```csharp
private IReadOnlyList<LlmTool> SkillCatalogToTools(JsonElement catalog, UserContext userCtx)
{
    var tools = new List<LlmTool>();
    if (catalog.ValueKind != JsonValueKind.Array) return tools;

    foreach (var item in catalog.EnumerateArray())
    {
        var name = item.GetProperty("name").GetString();
        var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var requiredRole = item.TryGetProperty("required_role", out var r) ? r.GetString() : "USER";

        // (a) 角色過濾:沿用現行語義(ChatService.cs:222)——required_role=="USER" 視為無限制,
        //     否則要求 userCtx.Role 完全相符。ADMIN-only skill 對非 ADMIN 直接不端出。
        if (!IsRoleAllowed(requiredRole, userCtx.Role)) continue;

        // (b) 挑輸入鍵:input_schema 中唯一的必填 str 欄位。挑不出 → P1 跳過(見 §1.4)。
        var inputKey = SingleRequiredStringKey(item);
        if (inputKey is null) continue;

        // (c) description 補提示,幫 LLM 判斷何時呼叫(02-spec §2.3)。
        var desc = $"{description}（輸入 {inputKey}:一段自然語言）";

        // 閉包捕捉 name/inputKey/userCtx;委派見 §2.2。
        var skillName = name!;
        tools.Add(new LlmTool(skillName, desc,
            (arg, ct2) => InvokeSkillToolAsync(skillName, inputKey, arg, userCtx, ct2)));
    }
    return tools;
}

private static bool IsRoleAllowed(string? requiredRole, string userRole)
    => string.IsNullOrEmpty(requiredRole) || requiredRole == "USER" || requiredRole == userRole;
```

**`SingleRequiredStringKey`**(挑輸入鍵):`input_schema` 是 `{ key: {type, required, min_length, default} }` 或 `null`(自訂 skill 逐筆取 schema 失敗時退 null,見 `custom.py:98-104`)。規則:

- 掃 `input_schema`,取所有 `required==true && type=="str"` 的鍵。
- **恰好一個** → 用它。
- 零個、多個、或 `input_schema` 為 null → 回 `null` → **P1 跳過此 skill**(它需要 P3 的多參泛化才能正確呼叫;端一個填不對參數的工具反而有害)。

> ponytail:P1 只吃「單一必填字串」的 skill(五個問句原型 `template_{retrieval,compare,infer,inspire,stats}` 與絕大多數自訂 skill 都是這型,見 02-spec §6)。多參/非字串 skill 在 P1 靜默跳過,**升級路徑 = P3**(§8)。此為刻意天花板,非遺漏。

**與 `AIFunctionFactory` 的參數名對齊**:`AgentFrameworkLlmAgent.cs:79` 硬寫委派參數名 `question`,那是**模型看到、要填的**參數名(對所有工具都叫 `question`);`inputKey` 是把該字串轉交給 skill invoke 時的**內部鍵名**。兩者解耦,不需同名,`ToRunOptions` 一行都不用改。

### 1.4 去重(skill 目錄優先,ChatToolSpecs 兜底)

```csharp
var skillTools = SkillCatalogToTools(catalog, userCtx);
var names = new HashSet<string>(skillTools.Select(t => t.Name), StringComparer.Ordinal);

var result = new List<LlmTool>(skillTools);
foreach (var t in BuildStaticTools(userCtx))       // 殘留 ChatToolSpecs
    if (names.Add(t.Name)) result.Add(t);          // 名稱未被 skill 佔用才補
return result;
```

現行 `ChatToolSpecs` 的工具名(`search_knowledge_base` 等)與 skill 名(`template_*`、`kb_query`…)命名空間本就不同,實務上不會撞;去重是安全網。settings-skill-redesign 把這些 workflow 遷成 skill 後,`ChatToolSpecs` 縮到空即可整段刪(留 `ponytail:` 註解)。

### 1.5 before / after(工具來源交換)

```
── BEFORE(ChatService.cs:212-232)──────────────────────────
BuildTools(userCtx):
  userCtx null → null
  foreach spec in ChatToolSpecs (寫死 5 筆):
    RequiredRole 不符 → skip
    tools.Add(LlmTool(spec.ToolName, spec.Description,
                      (arg,ct)=>InvokeWorkflowToolAsync(spec,…)))  // 打 /workflows/{name}/invoke
  return tools

── AFTER ───────────────────────────────────────────────────
BuildToolsAsync(userCtx, ct):
  userCtx null → null
  catalog = await GetSkillCatalogAsync(userCtx, ct)   // ← 新增:動態目錄(內建+自訂,租戶已過濾)
        └ 失敗 → 退 BuildStaticTools(userCtx)          // best-effort
  skillTools = SkillCatalogToTools(catalog, userCtx)   // 角色過濾 + 挑鍵 + 產 LlmTool → 打 /skills/{name}/invoke
  + BuildStaticTools(userCtx) 依名去重補上
  return result
```

`BuildStaticTools(userCtx)` = 把現行 `BuildTools` 內 `ChatToolSpecs` 迴圈(`:219-229`)原封抽成同步私有方法,行為零變。

---

## 2. 呼叫鏈:被選中的工具如何路由到 `InvokeSkillAsync`

### 2.1 完整鏈路(trace)

```
模型決定呼叫工具 "template_retrieval"({question:"…"})
  └ Agent Framework function-calling 迴圈(ChatClientAgent 內建)
      └ AIFunction.Invoke  ← AgentFrameworkLlmAgent.cs:78 AIFunctionFactory.Create
          └ LlmTool.InvokeAsync(question, ct)          ← 閉包,§1.3 (c)
              └ ChatService.InvokeSkillToolAsync(name, inputKey, arg, userCtx, ct)   ← 新增,§2.2
                  └ WorkflowService.InvokeSkillAsync(name, {inputKey: arg}, userCtx, ct)  ← WorkflowService.cs:53
                      └ POST /skills/{name}/invoke(帶 X-Internal-Token + 身分頭)
                  └ ExtractAnswer(output) → string                 ← 回給模型
      └ 模型把工具結果「融進」續寫 → 逐 chunk SSE data:(不另開事件)
```

### 2.2 skill 版工具委派(仿 `InvokeWorkflowToolAsync`,改打 skill invoke)

```csharp
private async Task<string> InvokeSkillToolAsync(
    string name, string inputKey, string arg, UserContext userCtx, CancellationToken ct)
{
    try
    {
        var input = new Dictionary<string, JsonElement>
        {
            [inputKey] = JsonSerializer.SerializeToElement(arg),
        };
        JsonElement res = await _workflows.InvokeSkillAsync(name, input, userCtx, ct);   // WorkflowService.cs:53
        return ExtractSkillAnswer(res);   // 見下
    }
    catch (Exception ex)   // 對映現行 ChatService.cs:258-262:回錯誤字串給模型轉述,不炸整輪
    {
        _logger.LogWarning(ex, "聊天工具 {工具} 呼叫 skill 失敗:{訊息}", name, ex.Message);
        return $"Skill {name} 呼叫失敗:{ex.Message}";
    }
}
```

**輸出取字串**:現行 `ExtractAnswer(Dictionary<string,JsonElement>)`(`:270`)吃的是 workflow invoke 的 `res.Output`(已是字典)。skill invoke 回的是**整包 `JsonElement`**,形狀 `{skill, output:{…}}`(`schemas.py:56-60`,platform 端 `InvokeSkillAsync` 原樣穿透)。故 P1 需一支薄 `ExtractSkillAnswer(JsonElement)`:

```csharp
private static string ExtractSkillAnswer(JsonElement res)
{
    var output = res.TryGetProperty("output", out var o) ? o : res;
    foreach (var key in OutputKeys)   // 重用 ChatService.cs:206 的 {answer,final_answer,report,summary}
        if (output.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
    return output.GetRawText();   // 都沒有 → 整包序列化回給模型(對映 ExtractAnswer 的 fallback)
}
```

> ponytail:不改現有 `ExtractAnswer`(它服務 `InvokeWorkflowToolAsync` 的字典路徑,仍在用);新增的 `ExtractSkillAnswer` 只多處理「JsonElement + 外層 output 包裝」。兩者共用 `OutputKeys` 常數,不重造。

### 2.3 arg 形狀小結

- 模型 → AIFunction:單一 `string question`(框架硬寫參數名)。
- LlmTool → skill invoke:`{ <inputKey>: "<question 原文>" }`,`inputKey` 由 §1.3 從 `input_schema` 挑出(如 `template_retrieval` 的必填字串鍵)。
- skill 回覆:`{skill, output}` → `ExtractSkillAnswer` → 純字串 → 交回 function-calling 迴圈。

---

## 3. 兩條聊天路徑共用一條程式路徑

- `/api/chat`(阻塞)`ChatController.cs:28` → `ChatService.ChatAsync` → `_agent.CompleteAsync(…, tools, …)`(`:63`)。
- `/api/chat/stream`(SSE)`ChatController.cs:47` → `ChatService.StreamChatAsync` → `_agent.StreamAsync(…, tools, …)`(`:97`)。

兩者都經 `MaybeUserContext()`(`ChatController.cs:60-61`)取身分,再共用 `BuildTools`。**把來源換成 skill 目錄後,兩路徑一次同時生效,零額外成本**(超出 01-plan「單一路徑先做」)。SSE `data:`(無空格)格式與空行結尾**完全不動**(工具結果由框架迴圈融回、模型續寫照舊逐 chunk 走 `ChatController.cs:49-55`)。

**AG-UI 為獨立 agent,不在本計畫**:`/api/copilot/agui` 用 `Program.cs:194-200` 另建的 `copilotAgent`(`IChatClient.AsAIAgent`),**不走** `ChatService`/`BuildTools`,其工具是前端 client tools(操作 UI),與知識檢索無關。→ 列 **P4 / 非目標**(02-spec §3.5)。

---

## 4. 回退(Fallback)三層

1. **無合適工具 → 純聊天**:function-calling 內建。LLM 判定沒有該呼叫的工具就不呼叫、直接作答。**零程式碼**(這正是「保留 D 的純聊天兜底」)。
2. **kb_query 證據不足 → rag_qa 兜底**:**既有確定性回退**,在 `InvokeWorkflowToolAsync` 內(`ChatService.cs:245-254`,`IsAbstain` 判 `answer_mode=="ABSTAIN"` → 改打 `rag_qa` 並如實註明)。
   - **P1 界定**:此兜底綁在 **workflow 版** `kb_query`(走 `InvokeAsync`)。若 `kb_query` 仍以 `ChatToolSpecs` 保留(尚未 skill 化),此邏輯原封不動照跑。**skill 版 `InvokeSkillToolAsync` P1 不移植 abstain 兜底**(YAGNI:等 `kb_query` 真的遷成 skill、且量到需要時再說;屆時 skill 內部本就該自帶棄答語義)。留 `ponytail:` 註解標明。
3. **目錄抓取失敗 → 退靜態工具/裸聊**:§1.2 的 try/catch,best-effort(類比 mem0 吞錯),聊天不炸。
4. **單一工具呼叫失敗 → 錯誤字串給模型**:`InvokeSkillToolAsync` 的 catch(§2.2),對映 `ChatService.cs:258-262`,不炸整輪。

---

## 5. mem0 順序(recall-before / remember-after 仍正確括住工具呼叫)

不需任何調整,現況天然正確:

```
BuildPromptAsync:
  GetRecent(cid)                    ← 短期記憶  (ChatService.cs:169)
  RecallAsync(uid, message)         ← mem0 recall,組進 system 前言  (:172)  ── 工具呼叫「之前」
_agent.CompleteAsync/StreamAsync(messages, tools, ct)
  └ function-calling 迴圈:選 skill → invoke → 融回 → 續寫   ← 路由/工具全發生「在此之內」
RememberAsync(uid, message, reply)  ← mem0 remember  (阻塞 :70 / 串流 :140)  ── 工具呼叫「之後」
```

recall 在 prompt 前、remember 在**完整(含工具融合後)回覆**之後 → mem0 記的是最終答案,語義正確。工具呼叫夾在 `CompleteAsync/StreamAsync` 內部,天然落在 recall 與 remember 之間。(02-spec §2.4)

---

## 6. DTO / API 影響:零對外變更

**對外契約(三個聊天端點 request/response/SSE、AG-UI)完全不動。** 改動全在 platform 內部、且**收斂在 `ChatService.cs` 一檔**:

- `BuildTools` → `BuildToolsAsync(UserContext?, CancellationToken)`(async)。
- 新增私有:`SkillCatalogToTools`、`SingleRequiredStringKey`、`IsRoleAllowed`、`InvokeSkillToolAsync`、`ExtractSkillAnswer`、`BuildStaticTools`(抽出現行 `ChatToolSpecs` 迴圈)。
- **await 傳染面(僅兩處,均已在 async 方法內)**:
  - `ChatService.cs:63` `_agent.CompleteAsync(messages, BuildTools(userCtx), ct)` → 先 `var tools = await BuildToolsAsync(userCtx, ct);` 再傳 `tools`。
  - `ChatService.cs:97` `_agent.StreamAsync(messages, BuildTools(userCtx), ct)` → 同樣先 await 取 `tools`(插在 `:94` `BuildPromptAsync` await 之後、手動 enumerator 之前;不涉 `yield`,無 CS1626 限制)。
- **依賴零新增**:`_workflows`(`IWorkflowService`)已注入(`ChatService.cs:31`),`GetSkillCatalogAsync`/`InvokeSkillAsync` 都在其上。
- **無新 DTO、無新端點、無 DB、無 env、不碰** `ChatController`/`AgentFrameworkLlmAgent`/`Program.cs`/`ILlmAgent`/`LlmTool`。

---

## 7. 效能天花板(ponytail):每輪目錄 fetch 的 N+1

**成本點**:`BuildToolsAsync` 每輪(僅登入者)打一次 `GET /skills`;該端點對**每個自訂 skill** 又逐筆再 fetch 一次以取 `input_schema`(`custom.py:87` gather + `_entry:100`),即 **1 + N** 次 backend 往返/輪。內建 skill 無此問題(schema 在記憶體)。

**P1 先不做快取**(先量再優化)。升級路徑(擇一,量到痛再上):

- **platform 端**:`ChatService` 加 per-`(tenant, role)` 短 TTL 快取(30–60s,`IMemoryCache`)。目錄變動頻率遠低於聊天頻率,短 TTL 幾乎全中。
  `// ponytail: 每輪 GET /skills 的 N+1;若量到痛 → IMemoryCache per-(tenant,role) 30–60s TTL`
- **backend 端**(更治本):讓 `/api/skills` 清單直接帶 `definition`/`input_schema`,消掉 workflow 側 `_entry` 的逐筆 N 打。跨服務改動較大,列為次選。

**刻意標為延後**,不在 P1 落地。

---

## 8. 多輪 / 缺參數(P3 sketch,延後)

- **P1**:靠 LLM 自然反問。工具 description 已標「輸入 `<key>`:一段自然語言」(§1.3 c);模型缺資訊時自然回問,使用者補答→短期記憶 sliding window(`ChatService.cs:169`)提供上下文→模型再呼叫。**不自建 slot-filling 狀態機**(YAGNI)。P1 僅支援「單一必填字串」skill,多參 skill 靜默跳過(§1.3)。
- **P3(結構化 slot-filling)**:把 `LlmTool` 從「單字串」泛化為「多欄位」:
  - `LlmTool` 增一條多參變體(或以 `input_schema` JSON 直接建 `AIFunction`);`LlmTool.cs:6` 的 ponytail 註解已預留此升級路徑。
  - `AgentFrameworkLlmAgent.ToRunOptions` 對多參工具改用 `AIFunctionFactory.Create` 的 JSON-schema 多參 delegate(而非硬寫單一 `question`)。
  - 參數不齊時交由框架 function-calling 的 partial-args 迴圈讓模型回頭問——仍**不需**自寫狀態機。
  - `SingleRequiredStringKey` 的「跳過」條件放寬:多必填/非字串改走多參路徑而非跳過。

---

## 9. 測試矩陣(xUnit,手寫 fake,無 mocking library)

沿用既有 fake(`FakeWorkflowService`:`Platform.Service.Tests/Fakes.cs:139,148`;`FakeMem0Client`、fake `ILlmAgent`)。需**擴充** `FakeWorkflowService`:`GetSkillCatalogAsync` 現回空陣列(`Fakes.cs:148-149`),要能回**預造 `JsonElement` 目錄**;`InvokeSkillAsync` 現回 `{skill=name}`(`:139-142`),要能**記錄呼叫參數**(name + input 字典)並回可設定的 output,供斷言。

### `Platform.Service.Tests`(核心,貼近改動層)

| # | 個案 | 斷言 |
|---|---|---|
| T1 | 目錄含 1 USER skill(`template_retrieval`,`input_schema:{question:{type:str,required:true}}`)+ 1 ADMIN skill;`userCtx.Role=USER` | `BuildToolsAsync` 只含 USER skill 工具;ADMIN skill 被剔除 |
| T2 | 同 T1,`Role=ADMIN` | 兩個工具都在 |
| T3 | `userCtx=null`(匿名) | 回 `null`(裸聊),**未呼叫** `GetSkillCatalogAsync` |
| T4 | 目錄含 builtin + custom 各一(皆單必填字串) | 兩者平等成工具;`source` 不影響 |
| T5 | 目錄某 skill `input_schema=null` 或 多必填 或 非字串 | 該 skill **被跳過**(P1 天花板) |
| T6 | 目錄 skill 名與殘留 `ChatToolSpecs` 撞名 | skill 版留、靜態版被去重剔除;不撞名時兩者共存 |
| T7 | 模型(fake `ILlmAgent`)選呼叫某 skill 工具 | `InvokeSkillAsync` 被以正確 `name` + `{inputKey: arg}` 呼叫(從 `FakeWorkflowService.Invokes` 斷言) |
| T8 | skill invoke 回 `{skill,output:{answer:"…"}}` | `ExtractSkillAnswer` 取出 `answer` 字串回模型 |
| T9 | `InvokeSkillAsync` 拋例外 | 工具回「Skill … 呼叫失敗」字串,聊天**不中斷**(對映 `:258-262`) |
| T10 | `GetSkillCatalogAsync` 拋 `WorkflowInvocationException`(workflow 502) | 退回靜態工具或裸聊,聊天**不炸**(best-effort) |
| T11 | fake `ILlmAgent` 不呼叫任何工具 | 純聊天回覆照常(無工具回退,零程式碼路徑) |
| T12 | mem0 順序 | `RecallAsync` 在 `CompleteAsync` 前、`RememberAsync` 在後(`FakeMem0Client.Remembered` 記到融合後 reply) |

### `Platform.Web.Tests`(`WebApplicationFactory`)

| # | 個案 | 斷言 |
|---|---|---|
| W1 | `/api/chat` 帶有效 JWT | 走含工具路徑(fake 目錄有料);回 `ChatResponse` |
| W2 | `/api/chat` 匿名 | 裸聊,不掛工具 |
| W3 | `/api/chat/stream` 帶 JWT | SSE `data:`(**無空格**)格式、每 event 空行結尾不變;工具融合後內容照常逐 chunk 出 |

> `FakeWorkflowService` 已在 `Platform.Web.Tests/Fakes.cs:166` 有 `GetSkillCatalogAsync`,同樣需可注入目錄。跨服務真行為(打真 workflow/backend)交給 `e2e-verifier` 驗一次(記憶 fakes-hide-real-behavior:手寫 fake 會掩蓋真 JSON 形狀/序列化差異)。

---

## 10. 落地順序(change-sequence,對映 01-plan / 02-spec §7 的 P1–P4)

| 序 | 動作 | 檔案 | Phase |
|---|---|---|---|
| 1 | `FakeWorkflowService` 擴充:可注入目錄 `JsonElement` + 記錄 `InvokeSkillAsync` 參數 | `Platform.Service.Tests/Fakes.cs`、`Platform.Web.Tests/Fakes.cs` | P1(先鋪測試地基) |
| 2 | 抽 `BuildStaticTools`(現行 `ChatToolSpecs` 迴圈原封搬出) | `ChatService.cs` | P1 |
| 3 | 加 `SkillCatalogToTools` / `SingleRequiredStringKey` / `IsRoleAllowed` / `ExtractSkillAnswer` / `InvokeSkillToolAsync`(純邏輯,可先單測 T1–T9) | `ChatService.cs` | P1 |
| 4 | `BuildTools` → `BuildToolsAsync`(取目錄 + best-effort catch + 去重);改兩處呼叫點 await(`:63`、`:97`) | `ChatService.cs` | P1 |
| 5 | 跑 `dotnet build` + `dotnet test`,補 T10–T12 / W1–W3 | — | P1 |
| — | **P2** skill 輸出附出處:讓 skill 的 answer 節點產可引用字串,模型融入正文;trace 預設隱藏(前端可選展開)。多數已由 function-calling 內建 | workflow skill 定義 + 前端(非本檔) | P2 |
| — | **P3** 多參泛化 + 缺參補問(§8) | `LlmTool.cs` / `AgentFrameworkLlmAgent.cs` / `ChatService.cs` | P3 |
| — | **P4** AG-UI 側掛同批 server-side skill 工具(**預設不做**,定位不同) | `Program.cs` | P4 / 非目標 |

**P1 最小 diff 界線**:只改 `ChatService.cs`(+ 兩份測試 Fakes)。不碰 controller、agent、SSE、mem0、`Program.cs`、`ILlmAgent`、`LlmTool`。

---

## 11. 刻意簡化總表(ponytail,含天花板與升級路徑)

| 簡化 | 天花板 | 何時升級 |
|---|---|---|
| 路由器 = 聊天 LLM 本身,不建分類器(方案 B) | 依賴 LLM 選工具正確率 | 量到誤選率高才考慮(02-spec §1 已論證 B 是重造) |
| P1 只吃「單一必填字串」skill,其餘跳過 | 多參/非字串 skill 暫不可路由 | P3(§8) |
| 工具參數名硬寫 `question`,不動 `ToRunOptions` | 單參上限 | P3 |
| skill 版不移植 kb_query→rag_qa abstain 兜底 | skill 版棄答無自動兜底 | kb_query 真的遷成 skill 且量到需要時 |
| 目錄每輪 fetch,不先加快取 | N+1(1+N 次 backend/輪,僅登入者付) | 量到痛 → IMemoryCache 30–60s(§7) |
| `ExtractSkillAnswer` 只認 `output` 外層 + `OutputKeys` | 非標準輸出鍵回整包 JSON | 出現新標準鍵時擴 `OutputKeys` |
| 殘留 `ChatToolSpecs` 不急砍 | 與 skill 目錄並存、去重兜底 | settings-skill-redesign 遷完自然歸零 |
| AG-UI 路由不做 | copilot 側無 skill 路由 | P4(若真要) |

> `[改 BuildTools 工具來源:寫死 workflow → 動態 Skill 目錄,+ 一支 skill 版工具委派] → 跳過:分類器/多參/快取/abstain-兜底/AG-UI,當 [進入 P3 或量測到痛] 再加。`
