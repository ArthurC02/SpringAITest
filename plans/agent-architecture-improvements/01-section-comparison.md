# 00–21 逐章比較

> 判定不是元件名稱比對，而是比較本系統是否已有同等或更強的 production mechanism。狀態分為：**較強**、**部分**、**缺少**、**不適用**。

## 比較矩陣

| 章  | 主題                 | 判定        | SpringAITest 現況                                                                                             | 應採取行動                                                                         |
| --- | -------------------- | ----------- | ------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------- |
| 00  | Harness thesis       | 較強        | D3/D5/D7 已把 authority、execution、approval 與 durability 分離。                                             | 保留 server-owned harness；用本矩陣補完整性檢查。                                  |
| 01  | Agent loop           | 較強        | LangGraph preflight/policy/tool/input/approval/output/budget/finalization 是 bounded state machine。          | 不建立第二個 loop；改善 evidence 與 model policy。                                 |
| 02  | Tool runtime         | 較強        | Registry、snapshot grants、rule scope、risk、dependencies 與 D7 once-only effect 共同約束 dispatch。          | Connector/MCP 必須映射進同一 tool boundary。                                       |
| 03  | Permission sandbox   | 部分        | Native tools 強；custom Python script 只防意外、不防惡意。                                                    | P0 改為 process isolation；不把 AST whitelist 當 security boundary。               |
| 04  | Hooks                | 不適用      | 固定 middleware/provider boundary 可稽核；沒有任意 callbacks。                                                | 不開放 runtime hooks；需要 extension 時用 typed/versioned provider。               |
| 05  | Planning/todos       | 不適用      | D5 root/child tasks、snapshots、budgets 與 events 已是 durable plan。                                         | 不新增 session-local todo authority。                                              |
| 06  | Subagents            | 較強        | Child run pin revisions/provenance/budgets；verifier independence、cascade cancel、PASS-only aggregation。    | 增加 operator lineage view，不改 coordination authority。                          |
| 07  | Skills               | 較強        | Declarative revisioned YAML、Node-First compiler、reads enforcement、tool registry 與 package validation。    | 只補 eval、provenance 與 script isolation。                                        |
| 08  | Context              | 較強        | E1/E3 有 canonical revision、evidence authority、role views、readiness 與 task-local deltas。                 | 補 retention/revocation/quality metrics 與 safe manifest。                         |
| 09  | Memory               | 部分        | Authenticated mem0 與 isolated short-term memory 已有；E-04 仍失敗，且 retention/deletion/provenance 不完整。 | 先修 E-04，再導入 audit-only governance。                                          |
| 10  | System prompt        | 部分        | Agent prompt revisioned；legacy chat guard/router/summary/persona 多為 constants/middleware composition。     | 建立 canonical prompt manifest、pinning、eval 與 rollback。                        |
| 11  | Error recovery       | 部分偏強    | Claims、leases、generation fencing、checkpoints、deadline、quarantine 與 dead letter 已有。                   | 將 production checks 納入 release lane；補 recovery SLO 與 model fallback policy。 |
| 12  | Task system          | 較強        | Backend-owned root/child state、cursor events、cancel/restart recovery 超過 local task store。                | 補 tenant/owner-safe list/search API 與統一 UX。                                   |
| 13  | Background execution | 部分        | RabbitMQ document processing durable，但 unexpected exception 目前可能 ACK 後遺失。                           | 定義 bounded retry/DLQ/durable failure；納入 recovery operations。                 |
| 14  | Scheduling           | 缺少        | 無 product-level schedule/webhook trigger authority。                                                         | Backend-owned trigger definition + fire ledger；只建立正常 D5 root。               |
| 15  | Worktree isolation   | 不適用      | Runtime 不修改 source tree。                                                                                  | 不實作；未來若引入 code-changing agent 再重評。                                    |
| 16  | Coordination         | 較強        | Durable command claim、lease、budget、verifier 與 aggregation contracts 明確。                                | 只補 operator visibility 與 metrics。                                              |
| 17  | Protocols            | 較強        | Internal token/identity、ETag/hash、AG-UI/SSE、redacted events 均有明確 contract。                            | External connector protocol 必須 canonicalized/pinned/versioned。                  |
| 18  | Autonomy             | 較強        | Immutable snapshot、budgets、policy、approval、verifier 取代 open-ended model discretion。                    | UI 顯示 server-derived autonomy posture，不提供 client 可編輯標籤。                |
| 19  | MCP/plugins/channels | 缺少但非 P0 | 無 first-class MCP/connector/channel lifecycle。                                                              | 有產品需求才建 governed connector；先 read-only、allowlisted。                     |
| 20  | Observability/eval   | 部分        | OTel/Langfuse、redacted events、aggregate operations metrics 已有。                                           | 建 evidence envelope、per-run reconciliation、versioned eval runner。              |
| 21  | Loop engineering     | 部分        | Budgets、repair limits、verifier、release gate、rollback 已有；improvement loop 仍人工。                      | 接通 trace/evidence → eval → gate → canary/rollback。                              |

## 交叉結論

### 已被現況超越

Agent loop、subagents、tasks、coordination、protocols 與 autonomy 不需要重寫。把教學 repo 的 JSON store、local worker、callback hook 或 peer messaging 搬入本系統，反而會建立繞過 Backend authority 的旁路。

### 部分成熟

Context、memory、prompt、recovery、background execution、observability 與 loop engineering 已有核心機制，但缺 production evidence closure 或 operator productization。改善應落在既有 abstraction 的輸入/輸出與可驗證證據，不重做核心 runtime。

### 真正缺少

Scheduling 與 MCP/connectors 是 genuine capability gaps，但不是可靠性 P0。兩者都會擴大 unattended execution 與 external authority，必須等 evidence/eval、script isolation 與 operator controls 先到位。

## 外部章節與本計畫對照

| 外部章節 | 本地 disposition / authority                              | 後續                           |
| -------- | --------------------------------------------------------- | ------------------------------ |
| 00–01    | D3/D5 server-owned bounded runtime 已取代 miniature loop  | Evidence/model policy 見 02/03 |
| 02–03    | Native tool boundary 保留；script boundary 補強           | 05                             |
| 04       | 任意 hooks 不採用；保留 typed/versioned provider          | 00 §5、05                      |
| 05       | Session todo 不採用；D5 durable ledger 是 authority       | 00 §5                          |
| 06–07    | D5 child runs 與 Node-First Skills 保留                   | 02、04 補 eval/visibility      |
| 08–11    | E1/E3、memory、prompt、recovery 延伸既有 authority        | 02、03、04                     |
| 12–14    | Durable runs 已有；補 discovery/recovery/Backend triggers | 04                             |
| 15       | Worktree isolation 不適用                                 | 00 §5                          |
| 16–18    | D5 coordination/protocol/autonomy 原樣保留                | 04 補 operator view            |
| 19       | Connector/MCP 缺少但非 P0                                 | 05                             |
| 20–21    | 接通 evidence、eval、gate、rollout                        | 02                             |
