# 實作規格 — 核心旅程 UX 修復（P0）

> 承 [01-plan.md](01-plan.md) 的階段切分（P0=WS4、P1=WS1-a/b、P2=WS1-c、P3=WS3、P4=WS2 決策）。
> 本文逐 workstream 記錄：現況（引實際檔案）、目標行為、不做什麼、舊碼盤點、風險與契約守則。
> 所有檔案路徑相對於 repo 根 `c:\Users\a8022\OneDrive\Desktop\SpringAITest`。

---

## 1. WS1 — 接通文件→提問旅程（C1）

### 1.1 現況

- `frontend/src/components/DocumentsView.tsx:222-249`：文件列表 `<table>` 欄位只有「標題／狀態／片段數／建立時間／操作」，操作欄（`:243-247`）目前只有一顆「刪除」按鈕。沒有任何導向聊天或副駕的動作。
- `frontend/src/components/ChatView.tsx` 與 `frontend/src/hooks/useChat.ts`：`send(text)` 只接受純文字（`useChat.ts:169`），沒有「帶入指定文件脈絡」的參數或機制。
- `frontend/src/components/AppShell.tsx`：
  - `useCopilotReadable`（`:158-169`）已把文件清單（`id`/`title`/`status`/`chunk_count`）餵給副駕。
  - `askKnowledgeBase` action（`:190-204`）已可呼叫 `rag-qa` skill，但只吃自由文字 `question`，不吃指定文件 id，也不會被文件列表的操作觸發。
  - `<CopilotSidebar defaultOpen={false}>`（`:337-359`）：目前沒有任何程式路徑會主動展開它。
- `frontend/src/hooks/useDocuments.ts`：`fetchList`（`:29-36`）與輪詢迴圈 `startPolling`（`:38-65`）只回傳合併後的文件陣列，**不追蹤「哪些文件剛從 `processing` 變成 `ready`」**。`DocumentsView.tsx:79` 目前唯一的 toast 只在「送出建立」當下觸發（`已送出,處理中`），文件真正轉為 `ready` 時沒有任何主動通知——如果使用者建立後就切到別的視圖，不會知道文件已就緒。
- `platform/src/Platform.Service/SkillRoutingAgent.cs`：命中 skill 路由時，摘要文字由「裸」LLM 串流產生（`RunCoreStreamingAsync:96-132`），逐 token 直接寫進既有 SSE `data:` frame（見 `ChatController.cs:60-69`），**沒有任何欄位或事件標示這次回覆的來源是哪個 skill**。
- `frontend/src/components/ChatBubble.tsx:5-31`：目前只有「純文字」與「Markdown」兩種渲染路徑，沒有來源徽章的概念。

### 1.2 目標行為

1. `DocumentsView` 表格每列新增「問這份文件」操作：狀態為 `ready` 才可點；`processing`/`failed` 停用並顯示原因（tooltip 或行內文字，不做彈窗）。
2. 點擊後開啟副駕側欄（若原本收合則展開）並帶入該文件的脈絡（文件標題/id），讓使用者接著提問時副駕已經知道「問的是哪份文件」。
3. 文件從 `processing` 轉為 `ready`（或 `failed`）時，**即使使用者當下不在文件視圖**，也要看到主動 toast 通知。
4. 聊天氣泡命中 skill 路由時，於氣泡上標示來源（例如「來源：知識庫檢索」徽章），使用者能分辨這則回覆是「一般聊天」還是「查了知識庫/執行了某個 skill」得出的結果。
5. 全程不新增/修改任何後端 API 契約（驗收點：`ChatController.cs`/`DocumentController.cs`/`DocumentsController.cs` 的路由簽章、DTO 形狀、SSE frame 格式逐位元組不變）。

### 1.3 不做什麼

- 不做「多文件勾選後一起問」的批次選取 UI（YAGNI，單列動作即可）。
- 不新增/改動 `askKnowledgeBase` 之外的 skill（沿用既有 `rag-qa`／skill 路由，不新建 skill）。
- 不改 SSE wire 格式（`data:<value>`，無空格；`event:error` 語意不變）。
- 不做 AG-UI 版本的來源標示（copilot-shared-core 邊界：AG-UI 走 `@ag-ui/client` 協定與 CopilotKit 既有事件語意，屬另一條鏈路，本計畫不改動其事件形狀）。
- 不把 `documentId` 加進 `/api/chat` 或 `/api/chat/stream` 的 request body（那是 wire 契約變更）；文件脈絡改用「把文件標題拼進使用者可見的訊息文字」或「副駕既有 readable 自然帶入」，不新增欄位。

### 1.4 舊碼盤點

- `DocumentsView.tsx:222-249` 的表格操作欄：新增「問這份文件」不是取代刪除鈕，是同一格新增第二顆按鈕（或新增一欄），佈局細節留給實作，本規格不鎖死像素。
- `useDocuments.ts:29-65`：`fetchList`/`startPolling` 目前只回傳 merged 陣列本身，沒有「上一輪 vs 這一輪狀態差異」的追蹤。新增就緒偵測等同在 hook 的回傳形狀上加一個訊號（例如新增一個 `onReady` callback 參數，或回傳一個「剛轉態」的清單），**這是新增，不是取代**，但 `AppShell.tsx` 與 `DocumentsView.tsx` 兩處呼叫端（現有 `documents = useDocuments()` 解構）都要跟著更新解構欄位。
- `AppShell.tsx:342-351` 的 CopilotSidebar `instructions` 文案：新增「問這份文件」入口後，操作手冊文案需同步補一句，否則副駕會對使用者描述一個不存在的操作方式（沿用 settings-skill-redesign P1 已示範過的「副駕文案要跟著入口異動」慣例）。
- `SkillRoutingAgent.cs` 的 `RunCoreStreamingAsync`（`:96-132`）：若採「串流結束後追加一個標記 chunk」的方案（見 1.5 風險節），需要在既有 `await foreach (var chunk in _bareLlm.StreamAsync(...))` 迴圈結束後、`AppendExchangeToSessionAsync` 呼叫前，多 `yield return` 一個標記用的 `AgentResponseUpdate`。這是新增一步，不取代摘要串流本身的邏輯。
- `ChatBubble.tsx:23-27` 目前直接把 `message.content` 整段丟給 `<Markdown>`；新增來源標記解析後，這條路徑要先剝離標記字串再渲染，**剝離失敗時必須原樣顯示全文**（fail open，不能因解析失誤讓使用者看不到回覆內容）。

### 1.5 風險與契約守則

- **SSE 格式不可變**：不得新增/修改 `/api/chat/stream` 的 frame 語意（根 AGENTS.md「兩種 SSE 格式,都是故意的」契約）。若要在氣泡標示來源，唯一不違反「不改契約」約束的做法是：**在既有的 token 內容通道裡追加一個低碰撞率的 sentinel**（例如 HTML 註解 `<!--skill:kb-query-->`，Markdown 渲染器本身就會忽略 HTML 註解，前端額外做一次正則剝離取出徽章文字），而不是新增 SSE 欄位、新增 HTTP header 或改變 `data:`/`event:` 的 frame 結構。這是內容層的約定，不是 wire 形狀變更——但仍需与 `code-reviewer` 確認不會被真實模型輸出或使用者原文意外撞出同構字串（sentinel 的選字要刻意避開自然語言/Markdown 常見片段）。
- 尾端 sentinel 必須驗證不干擾既有 `event:error` 錯誤路徑（`ChatController.cs:75-80`）：中途失敗時不應該誤送出標記；`SkillRoutingAgent.cs` 的 §9.3 鐵律（委派呼叫必須讓下游例外原樣冒泡，不可被路由邏輯的 try/catch 吞掉）不得被本次改動破壞。
- 不得繞過 `ChatController` 類級 `[Authorize]` 或改變 `X-Conversation-Id` 語意。
- 通知機制若做成「輪詢比對狀態差異」，要注意 `useDocuments` 目前刻意設計成 `AppShell` 與 `DocumentsView` 共用同一份 state（`AppShell.tsx:135-137` 註解明講「避免兩處各自實例化造成雙重輪詢」）——新通知邏輯必須掛在這個共用層本身（`useDocuments` 內部或 `AppShell`），**不可在 `DocumentsView` 重新引入第二份輪詢**。
- Toast 觸發的元件位置：`ToastProvider` 包在 `AppShell.tsx` 回傳的 JSX 樹裡（`:267-363`），`useToast()` 只能在其**子孫元件**內呼叫。要讓「文件轉 ready」的通知在使用者停留在任何視圖時都能觸發，負責監看 `documents.docs` 狀態變化並呼叫 `useToast()` 的元件，必須是 `ToastProvider` 的子孫、且不受目前 `view` 切換影響而卸載（例如放在 `<ToastProvider><ConfirmProvider>` 之下、`<div className="shell">` 同層的一個常駐小元件，而不是塞進只有 `view === 'documents'` 時才掛載的 `DocumentsView`）。這是本 workstream 唯一需要新增元件結構（而非單純改文案/加按鈕）的地方，實作時務必先確認這一點，避免做出「只有停留在文件頁才會跳通知」的錯誤版本。

---

## 2. WS2 — PDF/Word 支援（C7，開放決策，不拍板）

### 2.1 現況

- `DocumentsView.tsx:92-104`（`onFile`）：只用瀏覽器原生 `File.text()` 讀檔，`accept=".txt,.md"`（`:164`）。`:91` 的既有 ponytail 註解直接寫明現況取捨：「檔案匯入只做前端讀文字填表單,API 不變;PDF/Word 解析需要後端支援時再加」——這條註解本身就是本 workstream 的起點。
- `backend/src/Backend.Api/Files/DocumentsController.cs:18-55`（`AllocateIngestIntent`）與 `backend/src/Backend.Api/Files/DocumentDtos.cs:24-26`（`DocumentIngestIntentRequest{Title,Text}`）：全鏈路只認 UTF-8 純文字 `text`，長度上限 1,000,000 字元（`:30-33`），沒有任何二進位/PDF/Word 解析路徑。
- `backend/src/Backend.Api/Files/DocumentProcessor.cs:73-78`：`Chunking.SplitText(message.Text)` 直接對純文字切塊，同樣沒有格式偵測或轉換邏輯。
- `frontend/package.json`（依 settings-skill-redesign 稽核追加確認的現況）：目前只有 `highlight.js`/`rehype-highlight`，沒有任何 PDF/Word 解析函式庫（連 settings-skill-redesign 計畫的 CodeMirror 都還沒真的引入）。

### 2.2 目標行為：三個選項，本計畫不拍板

**(a) 前端瀏覽器內抽字**
`.pdf`/`.docx` 加進 `accept`；用 pdf.js（PDF）與 mammoth 或同類函式庫（`.docx`）在瀏覽器內把二進位轉純文字，抽出結果沿用現有 `create(title, text)` 路徑，backend **完全不變**。
- 受「frontend 不可新增依賴」規則限制，需要明確授權（比照 settings-skill-redesign O5 CodeMirror 6「使用者已明示要功能,視為已授權」的先例）。
- 優點：backend zero 改動，`title`+`text` ingest 契約完全不變。
- 缺點：大型 PDF 在瀏覽器解析可能卡主執行緒（可能需要 Web Worker，增加複雜度）；掃描版 PDF（純圖片,無文字層）抽不出任何內容,需要明確的失敗提示（見 §2.3）。

**(b) platform 端轉換**
`DocumentController.Create` 收到上傳檔案後，由 platform 呼叫 .NET 端 PDF/Word 解析套件（例如 PdfPig / DocumentFormat.OpenXml）轉純文字，再走既有 backend text ingest 契約——對 backend 完全透明，`DocumentIngestIntentRequest{Title,Text}` 不變。
- 需要 platform 新增依賴，同樣需要明確授權。
- 優點：前端毫無變化（使用者體驗與檔案挑選流程一致，仍是「選檔案→送出」），檔案格式判斷邏輯集中在一處。
- 缺點：platform 現況定位是薄 gateway（`platform/AGENTS.md`：`Web -> Service` 無資料層，只轉發不處理內容），這是它首次承擔內容轉換邏輯,是角色定位上的一次擴張,需要明確評估是否恰當。

**(c) 維持現狀 + 明確格式錯誤提示（最小方案）**
`accept` 維持 `.txt,.md`；若使用者仍選到其他類型檔案（手動改副檔名、拖曳等繞過 `accept` 限制的情境），`onFile` 偵測副檔名/內容不符時顯示清楚的 inline 錯誤（沿用既有 `textErr`/`FormField` 的 error 顯示機制），文案講清楚「目前只支援 .txt/.md,請先另存為純文字」,而不是讓使用者拿到一包亂碼二進位塞進 textarea 送出。
- 零新依賴，零後端改動。
- 缺點：完全不解決「使用者手上就是 PDF/Word」的根本需求，只是把現有的沉默失敗（讀出亂碼）變成明確失敗。

### 2.3 不做什麼（不論選哪個選項）

- 不改 backend 的 ingest 契約（`title`+`text`，`DocumentDtos.cs` 不動）。
- 不做 OCR（掃描版 PDF/圖片轉文字）。
- 不做伺服器端非同步轉檔佇列——若選 (b)，轉換必須在 platform 收到 multipart 檔案後**同步**轉完再呼叫既有 `create`，不得新增第二條非同步管線疊加在既有 202 flow 之上（避免出現「轉檔中」與「processing」兩層語意混淆）。

### 2.4 舊碼盤點

- 若選 (a) 或 (b)：`DocumentsView.tsx:91` 的既有 ponytail 註解與 `:164` 的 `accept=".txt,.md"` 是要被取代的現況標記——選 (a) 時，註解裡「PDF/Word 解析需要後端支援時再加」這句話本身就會過期失真（因為根本不需要後端支援），必須同步改寫或移除，不能留著誤導後續讀者。
- 若選 (c)：沒有程式碼被取代，只新增一段驗證錯誤路徑；`onFile`（`:92-104`）目前完全不檢查副檔名/內容，新增檢查是純新增，不算刪除死碼。

### 2.5 風險與契約守則

- 無論選哪個選項，backend `POST /api/documents/ingest-intents`（`DocumentsController.cs:18`）與 RabbitMQ `DocumentMessage` 契約（`documentId`/`tenantId`/`userId`/`title`/`text`，`DocumentDtos.cs:20-21`）**不得變動**——這是根 AGENTS.md「async document processing」契約的核心，platform 發佈到 `documents.process` 佇列、backend 消費者 chunk/embed/寫入的整條路徑都假設 `text` 是已就緒的純文字。
- 若選 (b)，platform 新增依賴前必須確認該依賴不會把二進位內容意外落地到 log/trace——各服務既有 telemetry 一律「只帶固定 outcome/status enum,不含內容」（見 backend AGENTS.md「Bounded built-in telemetry」節），新的轉檔邏輯若記錄失敗原因,務必比照此原則。
- 兩個新依賴選項 (a)/(b) 都碰到「frontend/platform 不可新增依賴 without explicit authorization」這條 AGENTS.md 硬規則——**本計畫的角色只到把選項攤開**,實際選哪個需要使用者/PM 在 P4 明確拍板,不由本計畫代為決定,也不得在沒有拍板前直接動工任一選項。

---

## 3. WS3 — 副駕升格主入口（C8）

### 3.1 現況

- `AppShell.tsx:337-359`：`<CopilotSidebar defaultOpen={false} ...>`，目前沒有任何程式路徑會把它動態展開——每次登入都是收合狀態起手。
- `labels` prop（`:353-358`）只設定面板內部的 `title`/`initial`（初始問候語）/`placeholder`，這些是**面板展開後**的內容，**不是**浮動開合按鈕本身的文字——CopilotKit 的浮動 launcher 按鈕目前用函式庫預設樣式，沒有客製文字標籤。這點會影響「按鈕加文字標籤」的實作方式（見 3.5 風險節）。
- `Session` 介面（`types.ts:16-23`）沒有任何「是否已引導過副駕」的欄位；`localStorage` 目前只有聊天狀態鍵（`useChat`/`chatPersistence` 提到的 `springai-chat:*` keys），沒有「是否首次登入」的既有旗標可重用。

### 3.2 目標行為

1. 首次登入時引導使用者發現副駕：例如登入後第一次進入 `AppShell` 時 `defaultOpen` 為 `true` 一次，之後尊重使用者自己的開關狀態（不能每次登入都硬彈開，否則對回頭客是騷擾而非引導）。
2. 副駕開合的浮動按鈕加上可讀文字標籤（例如「AI 助理」），確保非技術使用者一眼看出這是可互動的入口，不是裝飾圖示。
3. 維持既有安全邊界：copilot actions 仍只操作表單/呼叫既有 `apiFetch` 授權端點，不新增「代按發布」之類的 action；`session.token` 一律不得進 `useCopilotReadable`（既有鐵律，沿用不變）。

### 3.3 不做什麼

- 不改 `CopilotSidebar` 的底層邏輯或事件協定，只調整 props/文案/首次開合時機。
- 不新增 copilot action，不擴大副駕可代為執行的操作範圍。
- 不做 onboarding tour 套件（沿用既有 Toast/CSS 提示等現成基礎，不新增依賴）。

### 3.4 舊碼盤點

- `AppShell.tsx:338` 的 `defaultOpen={false}` 是要被取代的行為：改成依「是否首次登入」動態決定的值，原本的字面量常數退場。
- 新增「首次登入」判斷需要一個新的 `localStorage` 鍵（比照 `storageKeys.ts` 既有集中管理慣例）；這是純新增，沒有既有程式碼可重用。

### 3.5 風險與契約守則

- 不得把 `session.token` 放進任何 `useCopilotReadable`（`frontend/AGENTS.md` 既有鐵律）。
- 首次引導邏輯必須是純前端 UX（`localStorage` 判斷），不得新增後端 API 只為了記錄「是否已引導過」——這種偏好留在瀏覽器足夠，符合 YAGNI。
- **CopilotKit 版本 pin 精確**（`@copilotkit/react-ui` 1.62.3）：客製浮動按鈕的文字標籤若需要覆寫元件內部結構或找一個目前版本沒有暴露的 prop/slot，需要先對照該版本的實際 API 面（3.1 已確認 `labels` prop 管的是面板內容，不是按鈕本身）——**這是本 workstream 唯一標記為「待驗證」的實作細節**，若該版本確實沒有官方 slot 可用，退回方案是在 `CopilotSidebar` 外自製一顆帶文字的觸發按鈕、透過受控的展開狀態（`defaultOpen`/若函式庫支援受控 `open`）驅動，而不是硬改函式庫內部 DOM。

---

## 4. WS4 — USER 層外洩清除（C10）

### 4.1 現況（逐一引用實際字串/位置）

- `AuthPage.tsx:208-212`：
  ```
  <div className="auth__seed">
    種子帳號（密碼 password123）:<br />
    admin-a · user-a（租戶 demo-a，邀請碼 demo-a-invite）<br />
    user-b（租戶 demo-b，邀請碼 demo-b-invite）
  </div>
  ```
  無條件渲染，任何環境的登入頁都看得到。
- `AuthPage.tsx:34-36`：
  ```
  useEffect(() => {
    if (consumeSessionExpired()) setError('session 已過期，請重新登入。')
  }, [])
  ```
  「session」是開發者黑話，直接暴露給 USER。
- `MessageList.tsx:58-64`：
  ```
  {messages.length === 0 && (
    <div className="chat__empty">
      <p>開始對話吧 👋</p>
      <p className="chat__hint">
        送出後會呼叫後端 <code>POST /api/chat/stream</code>（經 Vite proxy 轉到 :8080）。
      </p>
    </div>
  )}
  ```
  聊天空狀態直接印 HTTP method + API 路徑 + 內部 proxy 細節。

### 4.2 目標行為

1. `AuthPage.tsx:208-212` 種子帳號區塊只在前端 Development 模式渲染。
2. `AuthPage.tsx:35` 文案改為「登入已逾時，請重新登入。」（逐字採用，不再出現「session」字樣）。
3. `MessageList.tsx:59-64` 空狀態文案改寫成不含 API 路徑/HTTP method 的人話（拿掉 `<code>POST /api/chat/stream</code>` 那整行提示，只保留「開始對話吧」等純引導文字）。
4. 盤點其餘 USER 層可見技術字串：讀碼過程中沒有再發現其他無條件渲染的技術黑話字串（`DocumentsView`/`ChatView` 的錯誤訊息均來自 `ApiError.message`，屬後端契約產出的字串，不在本輪清理範圍——見 4.3 不做什麼）。若後續發現遺漏，比照本節模式處理，不需要另立計畫。

### 4.3 不做什麼

- 不改任何「伺服器端」錯誤文案的來源——`ApiError.message`（包含 500 的固定通用訊息、fieldErrors 等）是後端契約產出的字串（根 AGENTS.md「Error shape & correlation」），本計畫只清「前端自己寫死」的技術字串，不動 `ApiError` 契約本身、不在前端攔截改寫後端訊息。
- 不做 i18n/多語言框架——維持現有純繁體中文字串，只是把「技術黑話」換成「人話」，不引入國際化依賴。
- ADMIN 可見的技術細節（`ConfigView`、Agent Builder 等 ADMIN-only 頁面）不在本計畫清理範圍——C10 明確界定為「USER 層」外洩，ADMIN 本來就該看到系統細節（路由/節點/YAML 等），這是身分對應的資訊量差異，不是缺陷。

### 4.4 舊碼盤點

- `AuthPage.tsx:208-212`：整個 `<div className="auth__seed">` 區塊**文字內容不變**，但要包一層條件渲染——取代的是「無條件渲染」這個既有行為，不是文字本身。
- `AuthPage.tsx:35`：字串常值取代（舊：「session 已過期，請重新登入。」→ 新：「登入已逾時，請重新登入。」）。
- `MessageList.tsx:61-63`：`<p className="chat__hint">` 這一段整段刪除或改寫，取代掉現有的除錯提示文字。

### 4.5 風險與契約守則

- **`import.meta.env.DEV` 的覆蓋範圍需要明確認知**：這是 Vite 內建旗標，只在 `npm run dev`（Vite dev server，`:5173`）為 `true`；`npm run build` 產出的 bundle——**包括 `start-full`/`start-lite` 這類容器化 Development 環境所使用的靜態建置產物**——一律為 `false`。也就是說，若採用這個機制，種子帳號提示只在「直接跑 `npm run dev` 打本機/遠端 platform」這種場景出現，**在容器化的 Development posture 下也會消失**。這與後端/platform 用 `ASPNETCORE_ENVIRONMENT=Development` 判斷「Development」的範圍不同（後者在容器內仍算 Development，見根 AGENTS.md「Security & Configuration Tips」節）。frontend 目前**沒有**任何機制把後端的環境旗標傳給建置產物（`vite.config.ts` 只有 `VITE_API_PROXY_TARGET` 這個 proxy target 設定，見已讀原始碼）。是否要讓種子提示在容器化 Development 也顯示，需要額外訊號（例如透過 `/api/features` 之類端點回傳環境旗標，或建置時注入新的 `VITE_APP_ENV`）——**這是本計畫刻意不解決的範圍外決策**（YAGNI：量到真的需要在容器場景也看到提示時再加）。02-spec 記錄此為已知取捨，實作時採用零依賴的 `import.meta.env.DEV`（ponytail 階梯：平台原生功能，不新增機制），不視為缺陷，但 PR 說明需要點出這個覆蓋範圍限制。
- 文案變更不得影響 `consumeSessionExpired()`/`ApiError`/`fieldErrors` 的既有旗標語意與呼叫時機（`AuthPage.tsx:34-36`），只換顯示字串，不動判斷邏輯。
- `MessageList.tsx` 空狀態改寫不得移除 `chat__empty`/`chat__hint` 既有的 class/樣式掛鉤，只換文字內容，元件結構與既有 CSS 選擇器保持相容。

---

## 5. 測試策略總表

| Workstream | 工具 | 重點 |
| ---------- | ---- | ---- |
| WS1（frontend 部分） | `npm run lint` + `npm run build`；`e2e-verifier`（Playwright） | 「問這份文件」動作依狀態啟用/停用；文件轉 ready 時非文件視圖仍收到 toast；不新增第二份輪詢 |
| WS1（platform 部分，來源標示） | xUnit（`SkillRoutingAgent` 既有測試擴充）+ frontend 單元測試（`ChatBubble` sentinel 剝離） | 命中路由的串流結尾正確追加標記；`event:error` 路徑不受影響；SSE frame 格式逐位元組不變 |
| WS2 | 無（決策階段，無程式碼變更） | 產出是選項比較本身 |
| WS3 | `npm run lint` + `npm run build`；`e2e-verifier` | 首次登入自動展開一次；按鈕文字標籤存在；`useCopilotReadable` 不含 token |
| WS4 | `npm run lint` + `npm run build`；`e2e-verifier`（截圖比對 dev vs build 產物） | 三處字串已換；種子帳號區塊在 build 產物中不渲染 |
| 跨鏈 | `e2e-verifier`（docker compose） | 聊天 SSE / 文件 202 / 既有 skill invoke 契約不破（沿用根 AGENTS.md 慣例） |
