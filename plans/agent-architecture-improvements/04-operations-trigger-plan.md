# Operations、Recovery 與 Durable Trigger 計畫

> 優先級：P0–P2。  
> 交付狀態：Phase O1（2026-07-30）、O2（2026-08-09）、O3（2026-08-09，由 admin-experience-downshift 計畫 W1 交付）、O5（2026-08-09，one-shot schedule only；recurring/webhook 未做）已實作；Phase O4 未實作。  
> 目標：把已存在的 durable runtime 變成可操作產品，再以同一 command path 增加有限 trigger 能力。

## 1. 問題

Agent/Orchestrator consoles 能建立與輪詢單一 run，Backend 也已有 metrics、regression、override 與 rollout APIs；但 operator 必須知道 ID、approval 只能按 run 查、operations view 主要顯示 raw JSON。Recovery 很強但缺 SLO 與操作投影。Scheduling/webhook 則完全沒有 product authority。

順序必須是先讓現有 work 可發現、可稽核、可恢復，再新增 unattended triggers。

## 2. Phase O1：Operations/release cockpit（UI-first）

重用既有 metrics/version comparison/legacy inventory/regression/override/rollout APIs：

- Summary metrics 與 Agent/Skill/tool/node tables。
- Revision comparison、active-run immutability 與 future-roots-only 說明。
- Regression evidence status、override confirm、rollout/rollback controls。
- Unknown/estimated usage 清楚標示，不以 0 假裝精確。

Server authorization、`workflow.manage`、idempotency、audit 與 feature flags 不變；UI 不自行推導 release eligibility。

## 3. Phase O2：Unified Runs and Tasks center

Backend 增加 tenant/owner/capability-safe list/search projection，涵蓋 direct Agent 與 Root/child runs：

- filters：status、kind、owner、Agent/Orchestrator、created range、approval/recovery state。
- safe summary：pinned revisions、elapsed、budget summary、last event、child progress、error class。
- actions：依現有 contracts cancel/resume/trace drill-down；不提供任意 state edit。

UI 重用既有 polling、cursor events 與 trace rendering，提供全域 list/detail；authoring consoles 保留「從當前 draft/published artifact 建立 test run」的專屬入口。

**驗收**：沒有 ID 也能找到被授權的 active/recent runs；跨 tenant 或無 ownership 的查詢與 missing indistinguishable；reload 保留 ordered events、partial output、lineage 與 cancellation state。

## 4. Phase O3：Discoverable approval queue

新增「visible/actionable to me」paged API，分開定義兩種 predicate。Visible 可包含 owner 自己的 run；Actionable 必須逐句重用既有 decision authorization：same tenant、required role、未過期、exact waiting state，且 separation-of-duties 要求時不得是 requester。Owner 身分不自動取得 approve 權限。DTO 只含：run/Agent identity、created/expiry、required role、server-authored safe action summary、status。

不暴露 raw arguments、fingerprints、checkpoint、effect identity 或 lease。Decision 仍走既有 endpoint 與 once-only semantics。

**驗收**：expired/replayed/self-approval/cross-tenant decision server-side fail；queue 與 direct-decision 共用 policy 並有 parity tests；合法 non-owner business approver 可看見 actionable item；decision race 僅一個成功且 audit 完整。

## 5. Phase O4：Recovery operations

不改 claim/fencing 演算法，先補觀測與有限 operator actions：

document consumer bounded retry + terminal DLQ 已實作（見 [02-evaluation-observability-plan.md](02-evaluation-observability-plan.md) Phase E0 item 4）。O4 尚未完成的是：

- heartbeat、oldest recoverable age、claim lag、lease churn、quarantine/DLQ counts、reason codes。
- redacted retry/abandon actions，必須 idempotent、audited、capability-gated。

任何 operator retry 都不能繞過 snapshot/hash、lease generation、deadline、approval 或 effect identity。

## 6. Phase O5：Durable triggers

**交付狀態（2026-08-09）**：one-shot schedule 已透過 dev-cycle 全管線交付（實作 → 三輪審查 → 簡化 → e2e 22/22 → 文件）。範圍為 §6.2 one-shot schedule only；recurring cron、signed webhook、§7 通知收件匣仍未做。Backend `Triggers/` feature folder 落地 occurrence ledger、claim/lease、immutable principal grant snapshot；fire 走既有 D5 root creation/command path。Platform 代理 + `AGENT_TRIGGERS_ENABLED` 404 fail-closed gate + features flag；Frontend「排程觸發」視圖（gate = 旗標 + 精確 `workflow.manage`）；compose 佈線齊。名稱唯一性 scoped 到 `status='scheduled'`（取消後同名重建可行）。

### 6.1 Authority

Backend 持有 trigger definition/revision/ETag、fire occurrence ledger、claim/lease、next due 與 delivery status。Trigger revision 必須 pin Backend-issued execution principal、owner 與 authority policy；明確選擇 immutable grant snapshot 或每次 fire 重新授權，不能偷讀 creator 的「目前權限」造成不透明漂移。Trigger fire 只呼叫既有 D5 root creation/command path，沿用其 tenant/user/role/groups/capability intersection 建立正常 immutable snapshot；Workflow 不擁有 cron，也不直接排程執行。

### 6.2 最小來源

先選一種，不同時做滿：

- **One-shot/recurring schedule**：timezone、DST、misfire、jitter、overlap policy。
- **Authenticated webhook**：signature/key reference、timestamp/replay window、payload size/schema、source allowlist。

建議若沒有已確認 webhook 客戶，先做 one-shot schedule，再以相同 fire ledger 擴 recurring；若已有 integration demand，先做 signed webhook 可避免先解完整 cron UX。

### 6.3 Invariants

- 每個 occurrence 有 stable identity，at-most-once 建立 root；重送回相同結果。
- Trigger revision pin target published Orchestrator/Agent 與 sanitized input mapping。
- Principal disabled、grant revoked、cross-tenant target、target revision change 或 self-approval 衝突時 fail closed；system principal 也不豁免 D7 separation-of-duties。
- Disabled/unpublished target、policy failure、tenant flag off 不建立 run。
- Rollback 停止 future claims；已接受 roots 照 snapshot 完成。
- Overlap 預設 deny/skip；不允許 client 自訂無界 concurrency。

## 7. Notifications

在已有 run/task projection 上增加 in-app completion/escalation inbox。Notification 是 derived delivery record，不是 execution authority；晚到或重送不得改 run status。Email/Teams 等外部 channel 需等 connector governance，不先把 credentials 放進 trigger model。

## 8. Flags 與 rollout

| Flag                          | 預設/策略                                                |
| ----------------------------- | -------------------------------------------------------- |
| `OPERATIONS_COCKPIT_ENABLED`  | 延續既有 admin/operations gate，先 UI canary             |
| `RUN_DISCOVERY_ENABLED`       | false，tenant + exact capability                         |
| `RECOVERY_OPERATIONS_ENABLED` | false；off 不影響 automatic recovery                     |
| `AGENT_TRIGGERS_ENABLED`      | false，tenant/source allowlist，disabled 404 fail-closed |

## 9. 驗證

- Backend：list pagination/filter/tenant/owner tests；approval visibility/race；recovery action idempotency。
- Trigger：concurrent due claims、restart、duplicate occurrence、principal disabled、grant revoked、cross-tenant target、self-approval、target revision change、DST/misfire/overlap 或 webhook replay/signature matrix。
- Frontend：empty/loading/error/large-list、filter persistence、confirm/audit feedback、no raw JSON fallback。
- E2E：find run without ID、approve authorized item、reload trace、kill/restart worker、trigger creates exactly one normal root。
- Security：cross-tenant enumeration、IDOR、hidden fields、feature flag before auth、capability exact match。

## 10. Cleanup inventory

- Unified projection 穩定後，抽出 Agent/Orchestrator consoles 的重複 polling/trace formatting；保留 authoring actions。
- Structured cockpit 上線後移除 raw JSON production view；debug-only data 留 developer tooling，不暴露租戶。
- Recovery metrics authoritative 後，刪除無 owner 的 ad hoc log-only alerts。
- 不以 scheduler library 取代 D3/D5 workers；trigger 只負責發行正常 command。
- Trigger/channel 分離；未來 connector delivery 不複製 trigger fire ledger。
