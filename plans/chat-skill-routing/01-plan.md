# 計畫書 — 聊天 → Skill 路由(Chat-to-Skill Routing)

> 狀態:**框架級起始計畫,尚未動碼,細節設計待探查現有 chat pipeline**。
> 關聯:[settings-skill-redesign](../settings-skill-redesign/01-plan.md)(撰寫端);本計畫是**執行端**——讓撰寫好的 Skill 真正被用到。
> 前提知識:使用者多為非技術人員、以自然語言在聊天中提問(專案記憶 non-technical-users-chat-first)。

## 1. 背景與問題

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

## 3. 待探查(動工前必做,P0)

本計畫的細節設計**依賴**先讀清楚現有聊天內部,尚未進行:
- `platform` `ChatService` 如何組 LLM 呼叫、Microsoft Agent Framework 的 **tool / function-calling** 機制是否已可用、在哪注入。
- `/api/chat`、`/api/chat/stream`、AG-UI(`/api/copilot/agui`)三條路徑各自的資料流與差異。
- 短期記憶(sliding window)/ mem0 recall 在路由前後的介入點。
- skill 目錄(`/api/skills/catalog`,含 builtin/custom + input_schema)如何供路由端取得。

## 4. 路由方案(候選,待 P0 後定案)

| 方案 | 作法 | 優點 | 代價 |
|---|---|---|---|
| **A. LLM tool/function-calling** | 把該租戶可見的 skill 目錄當成 LLM 的 tools,由 Agent Framework 讓 LLM 自己選並呼叫 | 與現有 Agent Framework 天然契合;最自然;支援多輪釐清 | 依賴 LLM 判斷正確率;需把 skill 目錄 → tool schema |
| **B. 意圖分類 → 派工** | 先把問句分類到 檢索/比對/推論/啟發(或具體 skill),再 invoke | 可控、可測、可稽核 | 多一次分類呼叫;分類粒度到「類」還是「具體 skill」需定 |
| **C. 明確選單** | 使用者在聊天 UI 手動選 skill/模式 | 零誤判 | 最不直覺,違反非技術/聊天優先原則(僅作退路) |
| **D. 混合** | A/B 為主 + C 作為覆寫/兜底 + 無適配時回退純聊天 | 兼顧直覺與可控 | 實作面較大 |

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

| Phase | 內容 |
|---|---|
| **P0 探查** | 讀 ChatService / Agent Framework tools / 三條聊天路徑 / skill catalog 取用;產出細節設計(02-spec) |
| **P1 路由核心** | 意圖/skill 選擇 + 回退純聊天(單一聊天路徑先做) |
| **P2 結果融合** | skill 輸出融回 SSE + 出處/trace 呈現 |
| **P3 多輪與補參數** | input_schema 缺參數時的反問 |
| **P4 擴至其餘路徑** | 視需要擴到 stream / AG-UI |

> 下一步:P0 探查(仍待「開始」指令)。本計畫刻意停在框架級,避免在未讀 chat 內部前臆測細節。
