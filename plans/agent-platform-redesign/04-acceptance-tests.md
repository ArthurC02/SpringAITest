# Agent 平台重整 — 驗收測試

> 狀態：規劃中。每期先建立跨服務契約測試，再完成實作。

## 1. P0：資料與契約

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-DATA-01 | 同 tenant 建立兩個不同 slug 的 Agent | 成功，revision 狀態正確 |
| A-DATA-02 | 同 tenant 重複 slug | 409；不同 tenant 可使用相同 slug |
| A-DATA-03 | 讀取其他 tenant Agent | 404，不洩漏存在性 |
| A-DATA-04 | 發布 Agent | 建立不可變 revision，固定 Skill revisions 與 definition hash |
| A-DATA-05 | Skill 更新 | 已發布 Agent snapshot 不變 |
| A-DATA-06 | rollback | 建立新 revision；歷史不被改寫 |
| A-DATA-07 | Dapper 與 in-memory repository | CRUD、revision、soft disable 與 binding 行為一致 |
| A-DATA-08 | 兩位 ADMIN 同時編輯同一 draft | stale ETag/expectedDraftVersion 回 409，不覆蓋新版本 |
| A-DATA-09 | validate 後 draft 被修改再 publish | publish 拒絕，必須重新驗證相同 draft version |
| A-DATA-10 | 讀取 pinned Skill artifact | 精確回指定 revision/hash；Skill current 更新不影響 |
| A-DATA-11 | allowedTools/knowledgeSources 省略、null 或空陣列 | 全部 canonicalize 為無權限，publish/run 不取得預設全開能力 |
| A-DATA-12 | Orchestrator/Workflow contract round-trip | draft/revision/node ports、root-child lineage 與 Verifier Report 可無損序列化 |
| A-DATA-13 | Orchestrator/Workflow published revision | immutable；restore 建立新 revision，active run 不漂移 |
| A-DATA-14 | `workflow.manage` claim/policy | capability principal 通過；單純 tenant ADMIN 與 USER 均拒絕 |
| A-DATA-15 | SYSTEM_ADMIN 標籤 | 只映射 capability policy，不形成繞過 tenant/policy 的隱含角色 |

## 2. P1：Agent Builder

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-UI-01 | ADMIN 建立 Agent | 可輸入名稱、說明、System Prompt，儲存 draft |
| A-UI-02 | USER 進入 Agent 設定 | 不可建立、修改或發布 |
| A-UI-03 | Skill picker | 只顯示同 tenant、enabled、驗證通過的 Skills |
| A-UI-04 | Tool picker | 顯示描述與風險分類，不顯示內部 token/endpoint |
| A-UI-05 | Agent draft 有失效 Skill | 顯示定位錯誤，禁止發布 |
| A-UI-06 | 發布預覽 | 顯示固定的 Agent revision、Skill revisions、工具與規則摘要 |
| A-UI-07 | Agent Builder | 一般 ADMIN 不出現 LangGraph node/edge/state key；Workflow Designer 是獨立 SYSTEM_ADMIN 功能 |
| A-UI-08 | Agent audience | catalog 與 run 都拒絕不在 allowed role/group 的使用者 |

## 3. P2：Business Rules

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-RULE-01 | number fact 搭配 string operator | server validation 拒絕並定位該 condition |
| A-RULE-02 | 巢狀超過上限或 AST 過大 | 拒絕，沒有 evaluator 資源放大 |
| A-RULE-03 | 高額退款規則 | `amount > 5000` 回 `require_approval: ADMIN` |
| A-RULE-04 | deny 與 route 同時命中 | deny 依固定 precedence 勝出 |
| A-RULE-05 | 缺少安全關鍵 fact | fail closed 並留下 decision trace |
| A-RULE-06 | UI round-trip | 編輯、儲存、重載後 canonical AST 語意相同 |
| A-RULE-07 | 自然語言摘要 | 與 AST 對應，摘要不作執行權威 |
| A-RULE-08 | simulation | 使用正式 evaluator；不呼叫真實寫入工具 |
| A-RULE-09 | prompt injection 要求忽略規則 | evaluator 結果不受模型文字影響 |

## 4. P3：Orchestrator／Workflow Registry 與 Designer

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-WF-01 | SYSTEM_ADMIN 建立 Workflow draft | 可從 Node Catalog 拖放、連線、設定、儲存與重載 |
| A-WF-02 | tenant ADMIN/USER 呼叫 Workflow 管理 API | 403；ADMIN 不自動取得 `workflow.manage` |
| A-WF-03 | React Flow UI round-trip | Semantic Graph IR 不因座標/viewport 改變；`ui_metadata` 可獨立更新 |
| A-WF-04 | typed ports 不相容 | client 即時阻擋且 server validate 再次拒絕 |
| A-WF-05 | 未知 node/version、dangling edge、unreachable/dead-end | server 回定位至 stable node/edge id 的錯誤 |
| A-WF-06 | 一般 cycle 或無界 loop | publish 拒絕；明示 bounded loop 才可通過 |
| A-WF-07 | 缺 Start/End/Verifier/Join/Audit 等必要階段 | publish 拒絕；governance shell 不可刪除 |
| A-WF-08 | fan-out 無 join/concurrency budget | publish 拒絕 |
| A-WF-09 | stale draft ETag | 409，不覆蓋他人修改 |
| A-WF-10 | publish | 建立 immutable Workflow revision，固定 node versions/semantic hash；P3 不開放 executable subflow |
| A-WF-11 | published rev1 後發布 rev2 | rev1 active runs 不漂移；新 run 依 rollout policy 使用 rev2 |
| A-WF-12 | restore/rollback | 建立新 revision，不原地修改歷史 Graph |
| A-WF-13 | simulate | 使用 server compiler/evaluator，不執行寫入型工具 |
| A-WF-14 | simulated trace overlay | 以 stable node IDs 呈現模擬狀態，敏感資料遮罩；真實 root/child trace 於 P4 接入 |
| A-WF-15 | Orchestrator/Agent revision 所引用的 Workflow/Agent/Verifier reference 跨 tenant 或未發布 | 該 Orchestrator/Agent 的 validate/publish 拒絕；Workflow Graph 本身不保存 Agent binding（見 A-WF-28） |
| A-WF-16 | SYSTEM_ADMIN 發布 Orchestrator | 固定 Root Workflow、Context allowlist、Agent pool、Verifier revision、budgets 與 policies |
| A-WF-17 | active root run 期間發布新版 Orchestrator | active run 保持原 revision；只有新 run 使用新版 |
| A-WF-18 | Root Context acquisition 嘗試載入 Skill 或寫入型工具 | 拒絕；Root 只可使用有效交集內的唯讀 Context node/tool |
| A-WF-19 | Orchestrator 設定面板 | 可管理 Root instructions/policy、Context allowlist、Worker pool、pinned Verifier 與 budgets |
| A-WF-20 | Graph IR 內嵌 System Prompt、Business Rule 或 Skill instruction | validate/publish 拒絕；只能使用 pinned typed reference |
| A-WF-21 | tenant-specific business Node Type | 不進入 system Node Catalog；商業動作必須由 Agent 經受治理 Tool/Rule 完成 |
| A-WF-22 | dependency preflight 失敗 | Agent 尚未執行即受控終止或降級，回可觀測錯誤且不遺留資源 |
| A-WF-23 | timeout/cancel/error terminal path | checkpoint、child/tool cancellation、cleanup 與 audit 依 Harness 契約完成 |
| A-WF-24 | 兩個契約相容的 Agent 使用同一 Agent-Runtime Workflow revision | Harness 可重用；各 run 仍固定自己的 Agent/Rule/Skill snapshot，不互相污染 |
| A-WF-25 | 一般 USER 執行含 `dispatch_agents` 的已發布 Root Harness | 不要求 `workflow.manage`；依 runtime effective authority 正常執行或拒絕 |
| A-WF-26 | Agent-Runtime 缺 preflight/output validation 等 visible-required stage | publish 拒絕；cleanup/audit 屬 compiler-owned wrapper，不可由 Graph 覆寫，所有 terminal path 必經（以 run-time 驗證，非 publish 驗證） |
| A-WF-27 | Verifier 使用 Worker Harness variant 或寫入工具 | publish/run 拒絕；必須使用 read-only Verifier variant 與固定 report contract |
| A-WF-28 | Workflow Graph 保存特定 Agent binding／`latest` selector | validate 拒絕；Agent reference 由 Orchestrator/Agent revision 與 run snapshot 注入 |

## 5. P4：Root Orchestrator 與 Agent Runtime

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-RUN-01 | Context 已足夠 | 不做多餘檢索，直接進入拆解 |
| A-RUN-02 | Context 不足但可查 Backend | 主動取得資料，不先詢問使用者 |
| A-RUN-03 | 只能由使用者提供關鍵資訊 | run 進入 `waiting_input`，只問最小澄清問題 |
| A-RUN-04 | resume | 使用相同 Root Workflow、Agent-Runtime、Agent/Skill revisions 恢復，不重跑已完成副作用 |
| A-RUN-05 | 重複查詢 | 被 dedupe 或預算拒絕，不形成無限循環 |
| A-RUN-06 | 超過 Context/tool/step/time budget | 受控終止、audit 完整、無背景工具殘留 |
| A-RUN-07 | Agent 綁定 Skill A/B，未綁定 C | catalog/load/invoke 均不可看見或呼叫 C |
| A-RUN-08 | Skill 要求未允許工具 | 工具未被呼叫，回 tool denied decision |
| A-RUN-09 | caller 無資料權限 | 即使 Agent/Skill allowlist 有工具仍拒絕 |
| A-RUN-10 | pinned Skill 被更新 | run 使用 snapshot 固定 revision |
| A-RUN-11 | package resource | 只可讀同 package 的受允許相對路徑與大小 |
| A-RUN-12 | external script | production isolation 未完成前不可執行 |
| A-RUN-13 | 規則要求 approval | 寫入型工具未執行，run 進入 `waiting_approval` |
| A-RUN-14 | result 驗證失敗 | 在 repair budget 內重派 child task；超限後透明說明不足 |
| A-RUN-15 | audit | 記錄 Root Workflow、parent-child、Agent/Skill revisions、rules、tools、Context、budget 與結果 |
| A-RUN-16 | 標準 Agent Skill 執行 | Skill instruction 載入同一 Worker child context，不建立額外 Agent child run |
| A-RUN-17 | legacy flow 綁定 pinned revision | adapter 執行指定 revision，不按名稱落到 current flow |
| A-RUN-18 | 同 tenant、不同 knowledge source | retrieve 只能讀 Agent 的 Effective Data Scope，越界來源拒絕 |
| A-RUN-19 | 模型在 tool args 偽造較大 scope | server 忽略／拒絕，不擴張 ToolContext scope |
| A-RUN-20 | approval 核准與拒絕 | 只有正確 tenant/role 且非禁止 self-approval 的核准者可決策 |
| A-RUN-21 | approval 過期、跨 tenant、重播或重複決策 | 一律拒絕，write tool 未執行 |
| A-RUN-22 | cancel waiting/running run | 進入 cancelled；in-flight tool 被取消且不可 resume |
| A-RUN-23 | Skill A scope 內呼叫未允許工具 | 即使 Agent direct allowlist 較寬仍拒絕 |
| A-RUN-24 | Skill A 完成後進入 direct-tool step | A 的 instruction frame 已移除，不影響後續決策或工具授權 |
| A-RUN-25 | Skill scope 中 pause/resume/cancel | 精確恢復相同 revision/scope 或在取消時清除，不殘留到其他 task |
| A-RUN-26 | 業務核准者沒有 `workflow.manage` | 若 tenant、`required_role`、separation-of-duties 與 run policy 均通過，仍可透過 runtime approval API 決策 |
| A-MA-01 | Context 不足 | Root 先主動取得足夠資料，再進入工作拆解 |
| A-MA-02 | 兩個獨立 tasks | 分派到兩個 pinned Worker Agent children，實際平行且不超過 concurrency cap |
| A-MA-03 | child Context | 只收到最小 task envelope，不含完整 conversation／其他 Agent memory |
| A-MA-04 | worker 嘗試委派另一 Agent | v1 拒絕；delegation depth 不得提升 |
| A-MA-05 | 一個 child timeout/fail | 依 published join policy fail-fast/partial/repair，不由模型臨時改策略 |
| A-MA-06 | parent cancel/timeout | 取消所有未完成 children，無背景 child/tool 殘留 |
| A-MA-07 | verifier selection | 使用獨立 pinned verifier revision，預設不可等於被驗 Worker 且無 write tools |
| A-MA-08 | verification report | 符合固定 verdict/evidence/repair schema；prompt prose 不可偽造 PASS |
| A-MA-09 | repair loop | 只在 maxRepairRounds 內重派；超限後停止 |
| A-MA-10 | aggregation | 只使用 PASS outputs，保留 worker/verifier/citation lineage |
| A-MA-11 | delegation authority | child effective authority 不超過 caller/root workflow/Agent/Skill/rule 交集 |
| A-MA-12 | parallel write tasks | v1 拒絕，或必須有已發布的衝突/approval/idempotency policy |

## 6. P5：聊天與 AG-UI

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-CHAT-01 | request 指定同 tenant published Orchestrator | 使用該 Root Workflow snapshot |
| A-CHAT-02 | Orchestrator 不存在、跨 tenant、disabled 或未發布 | 404/409，不能 fallback 到其他 tenant/default |
| A-CHAT-03 | 舊 client 未帶 orchestratorId | 已遷移 tenant 使用 Default Orchestrator；未完成安全設定者保持 legacy，既有聊天仍可用 |
| A-CHAT-04 | `/api/chat/stream` | `data:` 無空格格式、flush 與 mid-stream error frame 不變 |
| A-CHAT-05 | AG-UI | `data: ` 標準格式、JWT fail-closed 與 session isolation 不變 |
| A-CHAT-06 | 401 | 兩種 transport 都觸發同一 global logout |
| A-CHAT-07 | memory | recall 在 Agent run 前，remember/persistence 在完整 turn 後 |
| A-CHAT-08 | shared brain | Chat 與 AG-UI 對同 Orchestrator、輸入與 Context 使用相同 Root Workflow |
| A-CHAT-09 | clarification | 多輪補充後 resume 同一 run，不建立失去上下文的新計畫 |
| A-CHAT-10 | 同 conversation 有 waiting root run | 下一輪依 tenant/user/conversation/orchestrator 唯一鍵恢復；切換前先取消 |
| A-CHAT-11 | 同一使用者交錯使用 Orchestrator A/B | root session/checkpoint 不污染；Worker Agent memory 仍依 agentId 隔離 |
| A-CHAT-12 | conversation history | 訊息帶 orchestrator/workflow/rootRun；child lineage 帶 Agent/Workflow revisions |
| A-CHAT-13 | legacy mem0 migration | 未分類的 `{tenant}:{user}` 舊內容不自動注入新 Agent |

## 7. P6：發布與營運

| ID | 情境 | 驗收 |
| --- | --- | --- |
| A-OPS-01 | 發布前 regression set 有失敗 | 依 tenant policy 阻擋或要求明確 override |
| A-OPS-02 | rollback | 新 turn 使用 rollback revision，舊 run 保持原 snapshot |
| A-OPS-03 | 敏感 trace | UI/API 遮罩 secrets、token、未授權 Context |
| A-OPS-04 | 成本/延遲 | 可依 Agent、revision、Skill、tool 聚合 |
| A-OPS-05 | 人工核准 | approval 有核准人、時間、決策與 idempotency audit |
| A-OPS-06 | Workflow canary/rollback | 新 root runs依 rollout revision；active runs 保持原 snapshot |
| A-OPS-07 | 多 Agent 指標 | 可觀察 node latency、fan-out、child success、verifier reject/repair、aggregation |

## 8. 回歸命令

每一垂直切片至少執行：

```powershell
cd backend
dotnet build
dotnet test

cd ../platform
dotnet build
dotnet test

cd ../workflow
uv run pytest

cd ../frontend
npm run lint
npm run build
```

P4/P5 必須再由 full-chain E2E 驗證：

- PostgreSQL 真實 revision/binding/snapshot。
- Platform → Backend/Workflow 的 internal token 與 identity headers。
- LiteLLM mock model 的 decomposition/dispatch/verifier/aggregation 與 Worker Skill loop。
- chat streaming、AG-UI、pause/resume、approval 與跨租戶隔離。

手寫 fake 只能驗證局部契約，不能取代上述跨服務真實 JSON、stream 與資料庫行為。
