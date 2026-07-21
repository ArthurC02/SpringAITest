# 驗收測試 — start-lite(無容器啟動模式)

> 狀態: **規劃中(實作前)。** 依據: [01-plan.md](01-plan.md)、[02-spec.md](02-spec.md)(§5 驗收條件來源)、[03-design.md](03-design.md)(簽章與 DI 佈線)。
>
> 本案的核心風險與 copilot-shared-core 不同:那邊是「重構既有行為,怕改壞」;這邊是「**新增一條平行模式,怕污染預設模式**」。因此安全網的重心是 **A 組:三個環變全部未設時,現行為一個位元都不能變** —— 既有 platform 367 案、backend 173 案、workflow 459 案就是這張網,任何一案因本計畫變紅即 blocker。

---

## 0. 三組的分工與硬順序

| 組 | 意義 | 何時必須綠 |
| --- | --- | --- |
| **A — 預設模式零污染** | 環變未設 = 現行 DI、現行行為、既有測試全綠 | 每個實作 P 收尾都重跑;任一紅即 blocker |
| **B — 新元件單元/整合驗收** | InMemoryMem0Client、六個 InMemory repos、RingBufferActivityExporter、get_recent_traces、DI 開關 | 各 P(03-design §11.1)的 exit gate |
| **C — lite 真啟動 e2e** | `start-lite` 腳本真的把四服務+LiteLLM 拉起來,整條鏈路可用 | 腳本交付後;全案 release gate |

**硬順序**:P1(mem0)→ P2(repos)→ P3(OTel)→ P4(traces tool)各自 B 綠 + A 重跑綠 → 腳本 → C 全綠。P1–P4 之間無資料依賴,若並行實作,合流時 A 組必須重跑一次。

---

## 1. 驗收原則(本案專用)

1. **xUnit + 手寫 fake,不引入 mocking 套件**(全 repo 慣例)。platform 側在 `Platform.Service.Tests`/`Platform.Web.Tests`,backend 側在 `Backend.Api.Tests`。
2. **開關測試必須測三態**:`inmemory`(或 `console`)、**未設**、**其他值**(如 `postgres`、`banana`)。後兩者行為必須相同(= 現行);只測開的那一半,關不掉的 bug 抓不到。
3. **規格裡的數字要 on-point + off-point**:mem0 每 user 100 對 → 測 100(全留)與 101(最舊被擠掉);ring buffer 200 → 測 200 與 201。等價類內部值(用 5 測 100)抓不到打錯的數字。
4. **隔離必須驗內容,不可驗 nullity**(沿用 copilot-shared-core 的教訓):user-b 查到的結果**內容不含** user-a 寫入的字串,而不是「查不到 = null」。
5. **升格搬遷 = 既有測試一行斷言都不改**:Fakes.cs 的六個類別搬進 `Backend.Api.Data.InMemory` 後,`Backend.Api.Tests` 173 案必須在**不改任何斷言**的前提下全綠(允許只改 `using`/命名空間)。斷言跟著搬遷被改寫,就失去「升格未改變行為」的證明力。
6. **契約測試直接違反前置條件**:`IMem0Client` 的「不丟例外」不是靠實作自律,`InMemoryMem0Client` 要用 null/空白/超長輸入直接打,斷言永不 throw。
7. **一等價類一代表值**,同分支多輸入併 `[Theory]`;不為覆蓋率測 DTO/getter。

---

## 2. A 組 — 預設模式零污染(6 案)

> 這組是本案的安全網。「未設環變」是所有既有使用者(容器模式、CI、開發者本機)的路徑。

| ID | 前置條件 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `A-01` | 三個環變皆未設 | `platform/` 下 `dotnet test` | **367 案全綠、案數不減**(Service 218 + Web 149;若期間其他計畫增了案數,以動工前基線為準) |
| `A-02` | 同上 | `backend/` 下 `dotnet test` | **173 案全綠**,且 Fakes 搬遷後**未改任何斷言**(原則 5) |
| `A-03` | `WebApplicationFactory`,環變未設 | 解析 `IMem0Client` | 具體型別是 **`Mem0Client`**(HTTP 版),不是 InMemory |
| `A-04` | 同上,backend `WebApplicationFactory`(Testing 環境) | 解析六個 repository 介面 | 具體型別是 **Dapper 版**;`NpgsqlDataSource` 仍有註冊。Testing 環境跳過 DbBootstrap 的既有行為不變 |
| `A-05` | `OTEL_MODE` 未設,Testing 環境 | platform 啟動(`WebApplicationFactory`) | 不掛 Console exporter、不註冊 `RingBufferActivityExporter` singleton(`GetService` 回 null)、無 get_recent_traces tool。既有「Testing 不掛 OTLP」行為不變 |
| `A-06` | 環變設為**其他值**(`MEM0_MODE=banana`、`DB_PROVIDER=postgres`、`OTEL_MODE=otlp`)`[Theory]` | 同 A-03/04/05 的解析 | 與**未設**完全相同(現行路徑)。開關只認 `inmemory`/`console`(case-insensitive) |

---

## 3. B 組 — 新元件驗收

### 3.1 P1 — InMemoryMem0Client(8 案)

| ID | 前置條件 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `B-M-01` | `MEM0_MODE=inmemory` | 解析 `IMem0Client` ×2 | 型別是 `InMemoryMem0Client` 且**兩次是同一實例**(singleton —— 不是 singleton 記憶就跨呼叫消失,這是本開關最容易錯的一點) |
| `B-M-02` | 空 client | `RecallAsync("u1","任何")` | 回 `""`(未知 user);`RecallAsync("","q")`、`RecallAsync("u1","")` 也回 `""`,**皆不 throw** |
| `B-M-03` | 先 `RememberAsync("u1","我住台北","好的記住了")` | `RecallAsync("u1","台北")` | 回傳包含 `我住台北`;比對 **case-insensitive**(用英文詞驗大小寫);格式為 `- <userMsg>\n- <aiReply>` 逐行 |
| `B-M-04` | remember 兩筆都含關鍵字 | `RecallAsync` | **最新的一對排在最前**(latest-first 排序斷言,驗第一行內容) |
| `B-M-05` | 對 u1 remember **100** 對 / **101** 對 `[Theory]` | recall 第 1 對的獨特關鍵字 | 100:找得到;101:**找不到**(最舊被擠掉)。on/off-point |
| `B-M-06` | u1、u2 各 remember 不同密語 | `RecallAsync("u2", u1 的密語)` | 回 `""` —— **驗內容隔離**,u2 的任何 recall 結果不含 u1 寫入的字串(原則 4) |
| `B-M-07` | `RememberAsync` 以 null/空白 userId、雙空 message `[Theory]` | 呼叫 | 無操作、不 throw、字典不長出垃圾鍵 |
| `B-M-08` | 併發:16 執行緒同時對同一 user remember+recall 各 100 次 | `Task.WhenAll` | 不 throw、無 `InvalidOperationException`(集合被併發修改)、結束後對數 ≤ 100 |

### 3.2 P2 — InMemory repositories(9 案)

> 前置:六類搬進 `Backend.Api.Data.InMemory`,`A-02` 先綠(搬遷本身無破壞)再驗新增行為。

| ID | 對象 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `B-D-01` | DI 開關 | `DB_PROVIDER=inmemory` 下解析六介面 ×2 | 全部 InMemory 型別且 **singleton 同實例**(scoped 會導致每請求一份全新空資料 —— 登入後下一個請求就查無此人,本開關最危險的錯法) |
| `B-D-02` | DI 開關 | `DB_PROVIDER=inmemory` 下啟動 | **`NpgsqlDataSource` 未註冊**、`DbBootstrap` 未被呼叫;在**完全沒有 PostgreSQL** 的機器上啟動成功(這是 lite 的存在理由) |
| `B-D-03` | Auth 種子 | `FindUserByUsernameAsync("admin-a"/"user-a"/"user-b")` `[Theory]` | 齊全;role 分別 ADMIN/USER/USER;租戶分別 demo-a/demo-a/demo-b;`BCrypt.Verify("password123", hash)` 為 **true**(不比對 hash 字面值 —— BCrypt 每次生成不同,驗 Verify 才對) |
| `B-D-04` | Auth 種子 | `FindTenantByCodeAsync("demo-a")` | invite code `demo-a-invite`;`demo-b` 同理。與 DbBootstrap.cs:124-126 逐字一致 |
| `B-D-05` | Auth 註冊 | `AddUserAsync` 新 user 後重查 | 查得到;`UsernameExistsAsync` 由 false 轉 true |
| `B-D-06` | Conversation 隔離 | (demo-a,user-a) 與 (demo-b,user-b) 各 Add 一筆含獨特密語 | `ListDescAsync("demo-a","user-a")` 內容**不含** demo-b 的密語(原則 4);排序最新在前 |
| `B-D-07` | RAG cosine | 存入三個 chunk:與 query 向量**相同**、**正交**、**反向** | 相同者 score ≈ 1.0(±1e-5)、正交 ≈ 0、反向 ≈ -1;排序依 score 遞減;threshold 過濾掉低分者。**用手工小向量(如 3 維)驗數學,不用 1536 維** |
| `B-D-08` | RAG 隔離+狀態 | tenant-a 的 ready 文件、tenant-b 的 ready 文件、tenant-a 的 processing 文件 | `SearchAsync(tenant-a)` 只回 tenant-a 且 **status=ready** 的 chunk;processing 文件不出現 |
| `B-D-09` | ConfigurationSet | 同租戶 SetActive 第二份 | 至多一份 `is_active=true`(原本靠 DB 部分唯一索引兜底,InMemory 版必須在應用層等價保證 —— 這是 fake 升格時最容易漏抄的 DB 級約束) |

### 3.3 P3 — RingBufferActivityExporter(5 案)

| ID | 前置條件 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `B-O-01` | 直接 new,餵 **200** / **201** 個 Activity `[Theory]` | `GetRecentSpans()` | 200:全在;201:**最早那筆不在**、count == 200。on/off-point |
| `B-O-02` | 餵 5 筆有序 span | `GetRecentSpans(3)` | 恰 3 筆、**最新優先**、是最後 3 筆 |
| `B-O-03` | span 帶 tags:`skill_name`、`model`、及一個**白名單外**的 key | `GetRecentSpans()` | Attributes 含白名單鍵、**不含**白名單外鍵(快照瘦身是給 LLM 讀的前提) |
| `B-O-04` | `OTEL_MODE=console` 的 `WebApplicationFactory` | 解析 `RingBufferActivityExporter`;發一個 `chat.service` span | singleton 可解析;span 流進 buffer(**證明 OTel pipeline 與 DI 拿到的是同一顆** —— 03-design §2.3 修過的 lambda 外註冊,就是為了這條) |
| `B-O-05` | 併發 Export 與 GetRecentSpans 交錯 | 16 執行緒 | 不 throw;count 恆 ≤ 200 |

### 3.4 P4 — get_recent_traces(5 案)

| ID | 前置條件 | 動作 | 預期結果 |
| --- | --- | --- | --- |
| `B-T-01` | `OTEL_MODE=console` | 檢查 ChatClientAgent 的 `ChatOptions.Tools` | 含名為 `get_recent_traces` 的 `AIFunction` 恰一個 |
| `B-T-02` | `OTEL_MODE` **未設** | 同上 | **不含**(lite-only,生產絕不出現 —— 這條與 `A-05` 成對,是治理鎖) |
| `B-T-03` | buffer 有 5 筆 span | 直接呼叫 AIFunction(`limit=3`) | 回 3 筆,timestamp 遞減,含 name/duration_ms/attributes |
| `B-T-04` | 同上 | `filter="chat"` | 只回 name 或 attributes 含 `chat`(case-insensitive)者;filter 無符合 → 空 list,不 throw |
| `B-T-05` | buffer 為空 | 呼叫 | 空 list,不 throw |

---

## 4. C 組 — lite 真啟動 e2e(10 案)

> 依專案記憶 **fakes-hide-real-behavior**:B 組全綠只證明類別自身正確,證明不了四個進程真的能一起活。本組在**沒有 Docker daemon 運行**的前提下執行(這是本計畫的驗收主張);由 `e2e-verifier` 跑,瀏覽器部分用 Playwright MCP。

| ID | 動作 | 預期結果 |
| --- | --- | --- |
| `C-01` | 乾淨 shell 執行 `./scripts/start-lite.ps1`(Windows)與 `.sh`(如有 Linux 環境) | 前置檢查通過;五個進程(backend/workflow/platform/frontend/LiteLLM)全起;三個健康檢查(`:8002/health`、`:8001/health`、`:8080/actuator/health`)在逾時內綠;產生 `start-lite.pids`;**全程未觸碰 Docker** |
| `C-02` | `POST` backend 登入 `admin-a/password123` | 200 + JWT;解出 tenant=demo-a、role=ADMIN。錯密碼 → 401 ApiError |
| `C-03` | 帶 JWT 打 `POST /api/chat/stream`(SSE) | 幀為 `data:<value>`(**冒號後無空格**)逐塊到達;內容是 mock-gpt 固定文本;隨後 `GET /api/chat/history` 含本輪(InMemoryConversationRepository 真的接住了) |
| `C-04` | 同一 JWT 連續兩輪:先「記住我的密語是 XYZZY」,再「我的密語是什麼」 | 第二輪回覆可參考第一輪(InMemoryMem0Client remember→recall 真通)。⚠️ mock-gpt 回固定文本,**斷言面在 platform 日誌/console span 中 recall 非空**,不在回覆文字 |
| `C-05` | `POST /api/copilot/agui` (a) 無 JWT (b) 有效 JWT | (a) **401**;(b) 200 + `data: `(**有空格**)+ 事件序列 `RUN_STARTED`→`TEXT_MESSAGE_*`→`RUN_FINISHED` |
| `C-06` | 帶 JWT 上傳文件 | **502** ApiError 四鍵齊全(RabbitMQ 不在,既有契約);服務不崩、後續聊天照常 |
| `C-07` | Skill 鏈路:`GET /api/nodes`;建一個壞 YAML skill;invoke 內建 `rag_qa` | 目錄有料(workflow 活著);壞 YAML → 400/422(validate 真走 workflow HTTP);rag_qa invoke 不 5xx(空結果可接受) |
| `C-08` | 觀察 platform stdout/log;再於聊天中要求「顯示最近的 spans」 | log 含 Console exporter 的 span 輸出(`chat.service` 等 source);get_recent_traces 被呼叫且回傳含方才操作的 span name。⚠️ mock-gpt **不會**主動呼叫工具 —— 此條若無真模型,降級為:直接以 HTTP/單元方式呼叫該 AIFunction 驗證 buffer 有料,並在報告中註明降級 |
| `C-09` | 重啟 platform 與 backend 進程,重打 `C-03`/`C-04` 的查詢 | history 空、recall 空 —— **重啟即失憶是規格行為**(02-spec §6),必須驗到而不是繞過;登入種子帳號**仍在**(建構子種子,非外部狀態) |
| `C-10` | `./scripts/stop-lite.ps1` | 所有 PID 終止、五個 port 釋放、`start-lite.pids` 刪除;重複執行回「No running processes found」不報錯;`litellm.log` 全程**無 Langfuse callback 錯誤**(lite.yaml 生效) |

---

## 5. Exit gates

| Gate | 條件 |
| --- | --- |
| **P1(mem0)exit** | `B-M-01`~`08` 綠 + `A-01`/`A-03`/`A-06` 重跑綠 |
| **P2(repos)exit** | `B-D-01`~`09` 綠 + `A-02`/`A-04`/`A-06` 重跑綠。`B-D-01`(singleton)與 `B-D-02`(無 Postgres 可啟動)任一紅即 blocker |
| **P3(OTel)exit** | `B-O-01`~`05` 綠 + `A-05` 重跑綠 |
| **P4(traces tool)exit** | `B-T-01`~`05` 綠。`B-T-02`(預設不註冊)紅即 blocker —— 這是治理鎖,不是普通缺陷 |
| **全案 release gate** | A 組全綠(兩個 .NET 方案 `dotnet test` + workflow pytest 不退化)+ B 全綠 + `C-01`~`C-10` 綠。任何「未設環變時行為改變」皆為 blocker |

---

## 6. 證明「零污染 + lite 可用」的最小集合

只能跑一組時,跑這 6 條:

| # | 案例 | 單獨守住什麼 |
| --- | --- | --- |
| 1 | `A-01` + `A-02` | 預設模式零污染(既有 540 案 .NET 測試就是行為快照) |
| 2 | `A-06` | 開關只認暗語,亂設不會半開 |
| 3 | `B-D-01` | singleton —— 錯成 scoped 時 lite「能啟動但什麼都存不住」,最隱蔽的失敗型態 |
| 4 | `B-D-03` | 種子帳號 + BCrypt Verify,lite 的第一個使用者動作(登入)成敗在此 |
| 5 | `B-T-02` | get_recent_traces 絕不進生產 |
| 6 | `C-01` + `C-10` | 腳本真的起得來、收得掉,整個計畫的對外承諾 |

---

## 7. 明確非驗收範圍(YAGNI)

- RabbitMQ in-memory 替代(階段二;`C-06` 只驗 502 契約不變)。
- backend/workflow 的 OTel(階段二;lite 只有 platform 有遙測)。
- mem0 recall 品質(關鍵字比對是刻意的低配,不驗語意相關性)。
- ring buffer 的租戶隔離(進程級共用是明文設計,03-design §6)。
- 效能/壓力(lite 是開發機模式)。
- 20/21 滑動窗口邊界 —— 屬 copilot-shared-core 的 `A-19`/`B-P2-02`,不重複。
- 真模型下的工具呼叫體驗(`C-08` 已註明 mock-gpt 降級路徑;要真模型驗收需改 `CHAT_MODEL` + 金鑰,屬手動加測)。

---

## 附錄:案例數統計

| 組 | 案例數 | 編號 |
| --- | --- | --- |
| **A — 預設模式零污染** | 6 | `A-01`~`A-06` |
| **B — 新元件** | 27 | mem0 8、repos 9、OTel 5、traces tool 5 |
| **C — lite 真啟動 e2e** | 10 | `C-01`~`C-10` |
| **合計** | **43** | — |
