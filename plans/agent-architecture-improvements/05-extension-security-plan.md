# Extension Security 與 Connector/MCP 計畫

> 優先級：Script isolation 為 P0；Connector/MCP 為 P2 且需產品需求。  
> 目標：先修正已存在的 execution boundary，再用現有 tool authority 安全接入外部協定。

## 1. 決策

Custom Skill script 目前是 trusted ADMIN authoring convenience，不是 hostile-code sandbox。AST allowlist、iteration/write limits 與 timeout 仍有價值。已證實的隔離失效是 CPU 與 memory（worker thread 無 OS-level process boundary；resource bomb 可拖垮整個 Workflow 程序）；secrets 隔離仰賴 AST analyzer 無漏洞，一旦有洞才成立，是縱深防禦而非首層邊界。

MCP/connector 不得直接把 discovered tools 暴露給模型，也不得讓 Workflow 持有 arbitrary server credentials。正確方向是 Backend-governed immutable connector revision，經 canonical discovery 映射到既有 safe tool catalog，再走 grant/rule/risk/approval/effect boundary。

## 2. Phase S1：Script process isolation

### 2.1 保留

- 現有 AST default-deny analyzer、reserved state keys、static reads/writes/tools contract。
- Authoring 限 ADMIN、save-time + invoke-time validation。
- Trace 只存 source SHA 與 safe key summaries。

### 2.2 替換 execution boundary

以短生命週期 subprocess 或專用 isolated worker 執行，最小 IPC contract 只傳：script bytecode/source hash、allowlisted input state projection、allowlisted tool-call proxy descriptor、deadline/budgets。Child environment 不含 `INTERNAL_API_TOKEN`、DB URL、JWT secret、provider keys 或 parent environment。

OS boundary 必須限制：wall time、CPU、memory、process/file descriptor、filesystem、network/egress。Timeout 後強制終止整個 process group；output 經 size/schema/key validation 後才 merge state。

Windows/Linux 開發與 container production 可使用不同 isolation adapter，但 policy/contract tests 相同。若平台無法提供可信 OS limits，production 直接禁用 script step，不能回退到「看似 sandbox」。

### 2.3 Tool calls

Child 不拿 credentials；只透過 parent broker 發出 typed request。Parent 重新驗證 snapshot grants、tool name/schema、risk、timeout、call count 與 output size。Write tools 預設禁止；若未來允許，仍須走 D7 approval/effect identity，不能由 subprocess 直接執行。

**驗收**：首要為 CPU/memory bomb 被 OS 終止且 worker 可繼續服務（核心資源隔離）；次級為 child 看不到 secrets/env/network/filesystem（縱深防禦）；timeout 無 orphan process；malformed IPC/output fail closed；相同 valid scripts 與既有 behavior parity。

## 3. Phase S2：Governed connector catalog

只有在有明確 integration use case 後啟動。Backend model：

- connector type、server identity/endpoint allowlist、transport policy。
- immutable revision、canonical discovery/schema hash、capabilities/risk。
- tenant scope、credential **reference**、owner、health、last check、revocation。
- egress/DNS/redirect/TLS policy、timeouts、size/rate limits。

Credentials 存既有 secret facility 或部署環境，API/DB DTO 只保存 opaque reference；health check 不回顯 secret。

## 4. Phase S3：MCP adapter

1. Server-side discovery canonicalize 並 pin schema；unknown/schema drift 先 quarantine，不能自動擴權。
2. 每個 MCP tool 映射成既有 registry tool descriptor，標示 read/write、risk、input/output schema、connector revision。
3. Runtime effective authority = snapshot grant ∩ business rule ∩ connector health/revision ∩ tenant policy。
4. Arguments/result 做 schema、size、content-type、timeout、redirect 與 redaction validation。
5. 初始與本計畫可交付範圍嚴格限 read-only。現有 D7 once-only transaction 只適用 `runtime.write_evidence`，不能直接推廣到外部 MCP side effect。

Write-capable connector 必須另立前置 gate：connector-specific idempotency 或 prepare/commit/reconciliation contract、durable attempt ledger、unknown-outcome recovery 與 crash-window tests。外部系統若無可證明的 idempotency/reconciliation 能力，即使 approval 通過也 fail closed；D7 可重用 approval/policy identity，但不能被宣稱單獨提供 external exactly-once。

Workflow 只拿 Backend-issued connector invocation artifact，不拿管理 credentials，不自行接受 server URL 或 discovery result。

## 5. Channels 與 inbound messages

Channel adapter 是 connector 的受限類型，不是任意 plugin hook：

- Inbound identity mapping、signature/replay protection、tenant routing 與 payload schema 由 Backend policy 決定。
- Message 只建立正常 trigger occurrence/root，不直接呼叫 Workflow graph。
- Outbound delivery 是 durable derived record，可 retry，但不能改 execution truth。
- Browser 只見 connection health、scope、last check、credential-reference status，不見 secret。

## 6. 明確不做

- 不做 arbitrary Python/JavaScript plugin install。
- 不做 user-provided lifecycle callbacks。
- 不讓 MCP server 自報 `readOnly` 就取得 read authority。
- 不讓模型選 server URL、credential、tenant 或 tool risk。
- 不讓 connector failure fallback 到較寬的 native tool。
- 不把 connector catalog、native tool registry 與 Skill catalog 合併成模糊的單一 marketplace。

## 7. Flags、kill switch 與 rollback

| Flag/control                     | 策略                                                                                             |
| -------------------------------- | ------------------------------------------------------------------------------------------------ |
| `ISOLATED_SKILL_SCRIPTS_ENABLED` | shadow parity → tenant canary → replace in-process；production 無可信 adapter 時 script disabled |
| `MCP_CONNECTORS_ENABLED`         | false、tenant allowlist、read-only only                                                          |
| Per-connector kill switch        | 立即阻擋新 invocation；active call bounded timeout                                               |
| Revision revocation              | future calls fail closed；歷史 snapshots/evidence 保留                                           |

Native tools 不受 connector flag 影響。Rollback connector 不可讓已 revoked schema/credential 恢復，需明確新 revision。

## 8. 驗證

- **Script adversarial matrix**（首要檢驗 resource isolation）：CPU、memory 炸彈被 OS 終止且 worker 可繼續服務；次級檢驗 fork/process、filesystem、network、env、oversized output、IPC corruption、timeout cleanup。
- Behavior parity：現有 valid script corpus 在 isolated adapter 輸出相同 state/trace contract。
- Connector SSRF/DNS rebinding/redirect/TLS、credential exfiltration、schema drift、oversized/slow response tests。
- Authority matrix：tenant、grant、rule、risk、health、revision、approval 任一不符即 fail closed。
- Future write-connector gate：approval、durable attempts、remote idempotency/reconciliation、unknown outcome、各 crash window；在該 gate 建立前 write schema 一律拒絕。
- Secret scan：logs/events/operations/eval/screenshots 無 credential、raw args/result。

## 9. Cleanup inventory

- Isolated adapter 全量且 parity/evidence 通過後刪除 in-process `exec` path；AST analyzer 保留。
- 移除 child process 可繼承的 broad environment/bootstrap code。
- MCP adapter 上線後仍保留 native registry；只刪重複 discovery cache 或 permission calculation。
- Connector 被刪除時保留 revision identity、run/effect evidence 與 audit；刪除 credential material 依 retention policy執行。
