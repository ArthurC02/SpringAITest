# Prompt、Model 與 Runtime Policy 計畫

> 優先級：P0/P1。  
> 目標：讓每次 execution 的 prompt composition 與 model behavior 可 pin、可重建、可評估、可 rollback，同時保持 raw content 不進 operations surface。

## 1. 問題

Published Agent `system_prompt` 已是 immutable revision 的一部分，但 legacy chat 與 shared core 還由多個 constants/providers 組合 guard、routing、summary、persona、memory 與 tool instructions。Workflow 側更在 `_system_frame()`（`workflow/app/runtime/model.py:282-310`）串接三段 composition 之一：SYSTEM GOVERNANCE + PINNED SKILL SUMMARIES + AGENT INSTRUCTION，而該組裝也無 Backend pinning。D3 model runtime 也缺 Backend-pinned 的明確 retry/fallback policy。結果是 run 可以 pin Agent revision，卻未必能完整回答「當時用了哪套 composition 與 model policy」。

## 2. 不建立 prompt CMS

本計畫只建立 immutable composition artifact identity，不做自由組合 marketplace，也不建立 Agent/Orchestrator prompt 之外的第二個 content authority。最小模型是 canonical manifest，引用既有 prompt 來源與政策的 explicit revision；禁止 `latest`：

- persona/system prompt revision。
- immutable guard policy revision。
- routing/summary/memory policy revisions。
- enabled tool catalog hash。
- context role-view policy revision。
- model policy revision 與 generation settings。
- composition schema version 與 canonical SHA。
- workflow runtime governance frame revision（`_system_frame()` 的 SYSTEM GOVERNANCE 段，Backend 發布的明確 revision，同樣禁止 `latest`）。
- pinned skill catalog hash（snapshot 當下的 PINNED SKILL SUMMARIES 內容雜湊，含截斷旗標）。

Raw prompt text 留在既有 protected artifact store；operations/browser 預設只見 revision/hash/server-authored summary。

## 3. Phase P1：Canonical prompt manifest

1. Backend 定義 manifest schema、canonical serialization、immutable revision 與 tenant scope；manifest 本身不獨立發布 prompt content。Canonical serialization 必須複用既有實作：Backend 的 `AgentCanonicalizer`（`backend/src/Backend.Api/Agents/AgentCanonicalizer.cs:58-76`，固定 key 順序）與 Workflow 的 `workflow/app/canonical_json.py`（UTF-16 ordinal 排序，已與 .NET `StringComparer.Ordinal` 對齊），不得新增第三套。
2. Platform/Workflow 各自有 deterministic assembler，輸入同一 manifest 後產生其 transport/runtime 所需 messages。Workflow assembler 既有實作起點為 `_system_frame()`（`:282-310`），是改造對象而非新建。
3. Agent/Orchestrator publish 或 root allocation 時，以同一 publish capability/ETag boundary 驗證、lock 並 pin 所有 component revisions 與 manifest SHA；active runs 不追 latest。
4. Off path 使用目前 constants byte-for-byte，先做 shadow hash comparison。

**驗收**：

- 相同 inputs 產生相同 canonical bytes/SHA。
- Manifest 引用不存在、未發布、跨 tenant 或 schema drift 時 fail closed。
- Agent/Orchestrator publish 需要 validation 與指定 eval result；rollback 是引用舊 component revisions 建立新的 owning artifact revision，不修改歷史。
- Chat 與 AG-UI 對共享 core 使用同一 guard/router/memory policy identity；persona/transport 差異明示在 manifest。

## 4. Phase P2：Model execution policy

建立 Backend-published policy revision：

- logical model role、LiteLLM alias、allowed resolved providers/models。
- timeout、attempt count、backoff、retryable error classes。
- fallback chain 與每一跳的 capability/data-region/cost ceiling。
- token/usage/cost budget 與 streaming requirement。
- response schema/tool-call capability 要求。

Workflow/Platform 只執行 snapshot pin 的 policy。Provider 回傳 resolved model ID/fingerprint 時寫入 evidence envelope；fallback 原因與 attempt metadata redacted 記錄。

**禁止**：client 指定任意 provider、runtime 靜默換 model、因 fallback 放寬 tool grant、把 retry 次數藏在 SDK defaults。

## 5. Phase P3：Prompt 與 context provenance UX

### Authoring view

- Agent system prompt / Orchestrator instruction revision diff。
- Manifest component/revision diff、validation、linked eval status。
- Model policy diff、cost/capability/fallback impact summary。

### Runtime safe projection

- Prompt manifest revision/SHA、model policy revision、resolved model identity。
- Context status/revision/source IDs/role-view type/assumptions/redaction reasons。
- Memory used boolean、policy revision 與結果 count，不顯示 memory content。
- Tool catalog hash 與 effective authority summary，不顯示 hidden arguments。

完整 composed prompt、unauthorized context、secrets、tool output 與 chain-of-thought 不進 browser。

## 6. Phase P4：Context 與 memory governance

在 E1/E3 與 mem0 外圍補 lifecycle，不重寫 retrieval：

1. Audit-only 記錄 retention class、expiry、supersession/revocation、deletion actor/reason。
2. 量測 stale-context、unsupported-memory、recall latency、source coverage 與 deletion lag。
3. Enforcement rollout：expired/revoked context 無法 acquire；tenant/user deletion fail closed。
4. 先修 shared-core E-04，再把 memory quality threshold 納入 eval；不能以 governance UI 掩蓋 recall 實際失敗。

## 7. Flags 與 rollback

| Flag                         | Rollout                                                            |
| ---------------------------- | ------------------------------------------------------------------ |
| `PROMPT_ARTIFACTS_ENABLED`   | shadow composition → tenant canary → default；off 回現有 constants |
| `MODEL_POLICY_ENABLED`       | observe resolved model → pin retry policy → allowlisted fallback   |
| `CONTEXT_GOVERNANCE_ENABLED` | audit-only → expiry/revocation enforcement                         |
| `MEMORY_GOVERNANCE_ENABLED`  | audit-only；E-04 修復後才 enforcement                              |

所有切換只影響 future roots/new chat turns。已接受 run 不換 manifest/model policy；audit history 不因 flag off 刪除。

## 8. 驗證

- Canonicalization golden tests 與 cross-language fixtures（必須複用 `AgentCanonicalizer` 與 `canonical_json.py`；禁止新增第三套 canonical 實作）。
- Platform/Workflow assembler parity：guard order、tool catalog、memory policy、transport-specific persona。
- Model policy matrix：timeout/retry/fallback/no-capability/cost ceiling/streaming；所有 fallback 保持原 authority。
- Tenant isolation、cross-tenant component reference、concurrent publish/ETag、active-run immutability、rollback revision tests。
- Secret scan：operations DTO、events、screenshots、eval bundles 不含 raw prompt/context/token。
- E2E：candidate manifest eval fail 阻擋 publish/rollout；舊 manifest rollback 後只套用新 roots。

## 9. Cleanup inventory

- Manifest cutover且 fallback usage 為零後，移除重複 hard-coded copilot/guard/routing/summary defaults。
- 移除 frontend-only prompt defaults；browser 不應成為 execution prompt source。
- Model policy authoritative 後，刪除散落的 retry/fallback constants；保留 SDK-level transport retry only if policy 明確允許。
- 不刪歷史 prompt/model revisions、snapshot pins 或 evidence identities。
