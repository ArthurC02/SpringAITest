# 聊天 → Skill 路由 — 已交付歷史紀錄

> 狀態: **已交付；以下是設計與驗收記錄，不是待辦清單。** 現行行為以 [plans README](../README.md) 與 `platform/src/Platform.Service/ChatService.cs` 為準。
> **歷史草稿警示:** 第 1–7 節描述交付前的候選方案，包含已否決的 function-calling 與固定意圖分類；不得當成現行契約或後續實作需求。
> **LlmTool / AIFunction / ChatToolSpecs / `/workflows` 描述都沒有成為現行路徑。** 現行為手動 Skill 名稱路由，見 [04-acceptance-test.md](04-acceptance-test.md) 標頭與 `platform/src/Platform.Service/SkillRoutingAgent.cs`。
> **Skill 概念重整註記（P0）：** 現行路由以 catalog 名稱與統一 invoke `{skill, output}` 契約運作，**不依 artifact `kind` 分流**；Agent Skill 與 Business Workflow 的分類及載入由 workflow 引擎處理。此計畫中的歷史「Skill」字樣不得被解讀為「Skill 必為 YAML flow」。
> 關聯:[settings-skill-redesign](../settings-skill-redesign/01-plan.md)(撰寫端);本計畫是**執行端**——讓撰寫好的 Skill 真正被用到。
> 前提知識:使用者多為非技術人員、以自然語言在聊天中提問(專案記憶 non-technical-users-chat-first)。

## 1. 歷史背景與問題

- 使用者在**聊天**用自然語言問四類問題:**檢索 / 比對 / 推論 / 啟發**(複雜度遞增)。
- 使用者**不會**、也不該去按 skill 名字手動 invoke —— 那太工程導向。
- 現況:聊天走 platform 的 `ChatService`(Microsoft Agent Framework LLM + mem0 + 短期記憶),**沒有接上任何 skill**;Skill 只能經 `POST /api/skills/{name}/invoke` 顯式呼叫。
- **缺口**:一句 NL 問句 → 該由哪個 skill(或純聊天)回答?沒有這一環,settings-skill-redesign 辛苦撰寫的 skill 對非技術使用者等於用不到。

## 2. 目標 / 非目標

### 目標
1. 聊天收到 NL 問句時,能**自動判斷**該用哪個 skill 回答(或回退純聊天)。
2. 分類沿用四類問題原型(檢索/比對/推論/啟發),**與撰寫端範本共用同一套意圖分類**,不各自發明。
3. Skill 結果**融回聊天串流**(SSE),含出處/trace 摘要,體驗與現有聊天一致(格式契約不破)。
4. 全程**多租戶 + 角色**把關:只路由到該租戶、該角色可見的 skill。

### 非目標
- 不改公開聊天 API / SSE `data:` 格式 / AG-UI 契約(對外行為相容)。
- 不做撰寫端(那是 settings-skill-redesign)。
- 不做跨租戶 skill 分享。

## 3. 已完成的歷史探查(P0)

本計畫的細節設計**依賴**先讀清楚現有聊天內部,尚未進行:
- `platform` `ChatService` 如何組 LLM 呼叫、Microsoft Agent Framework 的 **tool / function-calling** 機制是否已可用、在哪注入。
- `/api/chat`、`/api/chat/stream`、AG-UI(`/api/copilot/agui`)三條路徑各自的資料流與差異。
- 短期記憶(sliding window)/ mem0 recall 在路由前後的介入點。
- skill 目錄(`/api/skills/catalog`,含 builtin/custom + input_schema)如何供路由端取得。

## 4. 已否決的路由候選方案

| 方案                             | 作法                                                                              | 優點                                                | 代價                                                  |
| -------------------------------- | --------------------------------------------------------------------------------- | --------------------------------------------------- | ----------------------------------------------------- |
| **A. LLM tool/function-calling** | 把該租戶可見的 skill 目錄當成 LLM 的 tools,由 Agent Framework 讓 LLM 自己選並呼叫 | 與現有 Agent Framework 天然契合;最自然;支援多輪釐清 | 依賴 LLM 判斷正確率;需把 skill 目錄 → tool schema     |
| **B. 意圖分類 → 派工**           | 先把問句分類到 檢索/比對/推論/啟發(或具體 skill),再 invoke                        | 可控、可測、可稽核                                  | 多一次分類呼叫;分類粒度到「類」還是「具體 skill」需定 |
| **C. 明確選單**                  | 使用者在聊天 UI 手動選 skill/模式                                                 | 零誤判                                              | 最不直覺,違反非技術/聊天優先原則(僅作退路)            |
| **D. 混合**                      | A/B 為主 + C 作為覆寫/兜底 + 無適配時回退純聊天                                   | 兼顧直覺與可控                                      | 實作面較大                                            |

**初步傾向 D(以 A 或 B 為主)**:與 Agent Framework 契合,且保留「無合適 skill → 純聊天」的回退。A vs B 的取捨(讓 LLM 自選 vs 先分類再派)待 P0 讀完 Agent Framework 能力後定。

## 5. 跨切面(與 settings-skill-redesign 一致)

- **共用意圖分類**:檢索/比對/推論/啟發 同時是撰寫端範本類別與路由端意圖類別 —— 一套分類,兩端共用。
- **多租戶徹底落實**:路由候選僅限該租戶 active、該角色可見的 skill;跨租戶零可見(D8)。
- **非技術優先**:路由對使用者透明(他只管用自然語言問);skill 名稱/trace 預設不干擾,想看細節再展開。
- **相容**:SSE `data:` 格式、AG-UI、mem0 兩層記憶行為不變。

## 6. 開放問題(待 P0 後拍板)

1. 路由粒度:分到「問題類(4 類)」還是「具體 skill」?(類 → 再選 skill,或直接選 skill)
2. LLM 自選(A)vs 先分類(B):等 Agent Framework tool 能力盤點後定。
3. skill 結果如何在 SSE 串流中呈現(單一答案?含出處?trace 摘要要不要預設顯示?)。
4. 多輪:路由後若 input_schema 缺參數(如缺 query),聊天要不要反問補齊?
5. 三條聊天路徑(chat / chat/stream / AG-UI)是否都要支援路由,還是先做其一。

## 7. 分階(暫擬,待 P0)

| Phase               | 內容                                                                                             |
| ------------------- | ------------------------------------------------------------------------------------------------ |
| **P0 探查**         | 讀 ChatService / Agent Framework tools / 三條聊天路徑 / skill catalog 取用;產出細節設計(02-spec) |
| **P1 路由核心**     | 意圖/skill 選擇 + 回退純聊天(單一聊天路徑先做)                                                   |
| **P2 結果融合**     | skill 輸出融回 SSE + 出處/trace 呈現                                                             |
| **P3 多輪與補參數** | input_schema 缺參數時的反問                                                                      |
| **P4 擴至其餘路徑** | 視需要擴到 stream / AG-UI                                                                        |

> 下一步:P0 探查(仍待「開始」指令)。本計畫刻意停在框架級,避免在未讀 chat 內部前臆測細節。

---

## 附錄 §A — 選路方案定案推理與五維度摘要

(併自 02-spec.md,2026-08-09 整併)

### 選 A(LLM function-calling)不選 B(意圖分類 → 派工)

- tool-calling 已接線且已在生產路徑跑(`ILlmAgent.CompleteAsync/StreamAsync` 收 `IReadOnlyList<LlmTool>?`,非空即啟用,工具迴圈由框架處理)。
- 方案 B 所需的「意圖分類器」在架構上其實是 skill(kb_query)內部的執行步驟,不是聊天層路由;要在聊天層重建等同再造一次 A 已經免費提供的東西——多一次 LLM 呼叫、多一份要維護的分類表,換來的「可控/可測」其實已由 A 這邊「工具清單本身就是白名單 + workflow invoke 的三道把關(404/403/422)」提供。B 是重造一個 A 已經給的東西。
- 收尾成 D:A 為主 + 「無合適 skill → LLM 不呼叫任何工具 → 純聊天」的回退(這是 function-calling 的內建行為,零成本);C(明確選單)**不做**(YAGNI,非技術/聊天優先原則反對,且無人要求)。

### 五維度定案摘要(Model / Skill / Tool / Hook / MCP)

定案時逐一釘死插入點,其中兩條為核心原則,決定了後續所有實作邊界:

- **路由器 = 聊天 LLM 本身,不另建分類器**:做 function-calling 的那顆 LLM(`CHAT_MODEL`,經 LiteLLM)就是路由器,沒有第二顆專屬 router/classifier 模型(方案 B 才需要,刻意的簡化);skill 執行內部(如 kb_query 圖裡的 intent_classification / answer_composer)用的是 workflow 自己的 `settings.llm_model`,那是路由下游、skill 圖內部的事,與路由決策無關,不要混為一談。
- **SSE `data:` 契約零改動 / 結果融合不必開新 SSE 通道**:skill 輸出經 function-calling 迴圈餵回模型,由模型「融進」續寫的自然語言,照舊逐 chunk 走既有 SSE `data:`(無空格)格式;不新增任何 SSE 事件型別。出處/trace 如需呈現,做法是讓 skill 的輸出字串本身含出處、模型融入正文即帶出,而非在 SSE 另開 metadata 通道(那會破契約);出處/trace 預設不強推顯示(非技術優先原則)。
- 其餘維度(Skill 候選來源與角色/租戶過濾、Tool 單字串引數的 P1 簡化形狀、Hook 角色由既有介入點分擔而非新建框架、MCP 執行期路由路徑不涉入)屬於逐維度實作規格,已隨程式碼落地並隨後續重構漂移,不再逐條記錄於此;現行事實以 `SkillRoutingAgent.cs` 為準。

---

## 附錄 §B — 回退層次與刻意簡化天花板

(併自 03-design.md,2026-08-09 整併)

### 三層 Fallback

1. **無合適工具 → 純聊天**:function-calling 內建行為。LLM 判定沒有該呼叫的工具就不呼叫、直接作答,即純聊天。零程式碼。
2. **目錄取得失敗 → best-effort**:抓 Skill 目錄失敗(workflow 502/逾時/壞 JSON)不讓聊天炸,退回殘留靜態工具或裸聊,處理方式類比 mem0 吞錯。
3. **單一工具呼叫失敗 → 錯誤字串轉述**:skill invoke 失敗時,工具回一段錯誤字串交回模型轉述,不炸整輪聊天。

(原設計另記一條既有確定性回退——kb_query 證據不足時自動改打 rag_qa 兜底並如實註明,綁定於當時尚未 skill 化的 workflow 版 kb_query;skill 版工具委派當時刻意不移植此邏輯,列為 YAGNI 天花板,留待 kb_query 真的遷成 skill 且量到需要時再補。)

### 刻意簡化總表(天花板與升級路徑)

| 簡化                                              | 天花板                                     | 何時升級                                         |
| ------------------------------------------------- | ------------------------------------------- | -------------------------------------------------- |
| 路由器 = 聊天 LLM 本身,不建分類器(方案 B)         | 依賴 LLM 選工具正確率                      | 量到誤選率高才考慮(§A 已論證 B 是重造)             |
| 只吃「單一必填字串」skill,其餘靜默跳過             | 多參/非字串 skill 暫不可路由               | 多參泛化升級(原規劃 P3,結構化 slot-filling)       |
| 工具參數名硬寫 `question`,不動框架委派             | 單參上限                                   | 隨多參泛化一併升級                                 |
| skill 版不移植 kb_query→rag_qa 的 abstain 兜底     | skill 版棄答無自動兜底                     | kb_query 真的遷成 skill 且量到需要時               |
| Skill 目錄每輪 fetch,不先加快取                    | N+1(每個自訂 skill 逐筆取 input_schema,僅登入者付) | 量到痛 → 短 TTL 快取(per-tenant/role)或 backend 清單端點直接帶 schema |
| 殘留靜態工具表(當時的 `ChatToolSpecs`)不急砍       | 與 skill 目錄並存、依名去重兜底             | 撰寫端把對應 workflow 遷成 skill 後自然歸零        |
| AG-UI(`/api/copilot/agui`)不做路由                | copilot 側無 skill 路由,僅前端 client tools | 視需要另行擴充(獨立評估,非本計畫延伸)             |

> 原始 ponytail 註記:「改工具來源:寫死 workflow → 動態 Skill 目錄,+ 一支 skill 版工具委派」→ 跳過分類器/多參/快取/abstain 兜底/AG-UI,量測到痛或進入後續階段再加。
