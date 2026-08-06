# Architecture Improvements Continuation — Remaining Plan

## 1. Execution order

### C0 — Rebase the continuation baseline

The previous dirty-worktree handoff is obsolete: the work is now in clean `HEAD=44f9de4`. Record that baseline and reconcile every future diff against it.

Reassessment reopened one Wave4-B defect: retention candidate retrieval processed only the first 100 rows. The dedicated correction below repaired and independently reviewed it; it was not folded into Wave5-B or P3.

### C0.1 — Wave4-B correction: complete candidate paging — COMPLETE

Consume the Backend cursor contract until exhaustion, with strict bounds and duplicate/progress protection. Prove 101+ candidates are neither omitted nor duplicated. Keep operational delete disabled.

### C1 — Wave5-B1: tracing degradation signal — COMPLETE

Replace silent enabled-tracing initialization failure with one content-free warning per process plus a low-noise in-process failure counter. Disabled tracing remains completely silent and must not initialize the handler. This tranche is independent of resource calibration and is the first implementation item.

### C2a — Wave5-B0: resource measurement contract — COMPLETE

The checked-in contract now inventories all 22 services, four versioned steady-running lanes, service classes, candidate counterpart relationships, and the four CI Compose snapshots. The zero-dependency collector records exploratory Docker CPU, memory-usage, and PID peaks without container identity or content data. Static validation rejects inventory drift and any pre-calibration resource limit. It never approves ceilings.

### C2b — Wave5-B0: execute reviewed runtime calibration — BLOCKED

Run each approved workload manifest with a usable Docker daemon and a controlled host/workload window. Review the generated exploratory bundles before defining any ceiling-approving workload. The current host has no reachable Docker daemon, so no runtime bundle was produced and no default can be approved.

### C3 — Wave5-B2: bounded built-in telemetry — COMPLETE (2026-08-06)

Backend now exposes only two bounded built-in `Meter` counters: final `DocumentProcessor` outcomes and fresh database-required readiness probes. Each has one fixed enum tag and no identifier, content, or error-text dimensions; listener failures are isolated from application behavior. Workflow remains deliberately unchanged because its existing admission snapshots are authoritative and there is no approved sink, exporter, or public endpoint for a duplicate telemetry adapter. The independent review correction preserved owner-cancellation semantics and added timeout/internal-exception coverage; focused and full Backend verification passed.

### C4 — Wave5-B3: calibrated Compose ceilings — BLOCKED

Only after C2b has approved values:

1. Add environment-overridable, effective Compose `mem_limit`, `cpus`, and `pids_limit` values through shared anchors for calibrated service classes only.
2. Apply matching values only to genuine evidence counterparts. appdb and LiteLLM have no symmetric counterpart; evidence model/capture/nginx are distinct workloads and require their own calibration.
3. Extend CI to inspect expanded base/evidence Compose JSON, prove the effective ceilings, and prove application hardening still coexists with them.
4. Add focused Compose expansion checks, shell syntax checks, and Docker health/runtime smoke when Docker is available.

Wave5-B must inventory old comments/tests that describe tracing failure as "silent" and any duplicated resource-limit declarations made obsolete by shared anchors.

The next independent work is non-destructive P3-R1 baseline validation or P3-R2 evidence-contract design if authorized. Otherwise, resume C2b when a Docker daemon is reachable; do not invent or approve ceilings before reviewed runtime calibration.

### C5 — Architecture Hard Reset P3 readiness and deferred execution

P3-R0 reconciliation is complete. Follow the reconciled work packages and stop gates defined by:

- [Delivery plan](../architecture-hard-reset/01-plan.md)
- [Target specification](../architecture-hard-reset/02-spec.md)
- [Acceptance tests, P3 matrix](../architecture-hard-reset/04-acceptance-tests.md)
- [Deletion and migration ledger](../architecture-hard-reset/05-deletion-and-migration-ledger.md)
- [Historical P3 inventory](../architecture-hard-reset/07-p3-inventory.md)
- [P3 reconciliation and work packages](../architecture-hard-reset/08-p3-reconciliation-44f9de4.md)

P3-R1 must mechanically validate the 52-table baseline, optional checkpoint relations, `document_ingest`, `checkpoint_retention_ack`, and the conversation-history index without shipping production SQL. P3-R2 defines trustworthy C8 evidence for removing only Business Workflow compatibility operations from public `/api/skills*`; internal `/skills/validate` and unified invoke retain separate retirement gates. P3-R3/P3-X remain blocked until the reconciled post-P5/C8 sequence passes. Do not create a `/v2` surface, dual write, first-match typed-artifact policy, or partial database/consumer cutover.

### C6 — Wave4-C1/C2: retention restore contract and operational provider

Before or alongside P3, C1 may define a repeatable restore-drill contract/harness while `delete` remains fail closed. After the P3 schema is frozen, C2 may add distributed claim/version fencing and a real immutable archive provider. Operational enablement additionally requires an independently operated archive target, least-privilege credentials, encryption/KMS, retention policy, and a non-skipped restore drill against a disposable database. Without those external prerequisites, delete remains blocked.

### C7 — Independent review and simplification

After each non-trivial implementation tranche:

1. Run an independent code review with special attention to tenant isolation, transaction/lock ordering, cancellation, disposal, feature-gate order, and content-free telemetry.
2. Resolve every actionable finding and rerun its focused tests.
3. Run a behavior-preserving simplification pass on only the recently changed files.
4. Re-review the simplified diff and repeat relevant tests.

Sol is reserved for the high-risk review described in [02-subagent-runbook.md](02-subagent-runbook.md); Terra remains the implementation default.

### C8 — Documentation, end-to-end, and release evidence

After implementation and review are stable:

1. Synchronize root and area `AGENTS.md`, README/runbooks, environment examples, and affected plan status. Documentation must describe effective Compose output, not merely YAML intent.
2. Run all service suites, Compose expansion, shell syntax/LF checks, diff hygiene, and container hardening.
3. Run the cross-service Docker smoke and relevant failure-injection drills.
4. Produce release evidence using the existing [release evidence plan](../copilot-shared-core/05-release-evidence-plan.md); retain known blocked/failed evidence honestly.
5. Reconcile the final dead-code inventory and prove deleted symbols/routes/tables are absent with literal search plus build/test evidence.

## 2. No silent carry-over

Every tranche ends in exactly one state:

- `COMPLETE`: all acceptance evidence exists and no required work remains;
- `PENDING`: implementation has not started or a draft was reverted;
- `ACTIVE`: an owner is currently working and no final report exists;
- `BLOCKED`: a named external precondition or failed stop condition prevents safe continuation.

Drafts, interrupted tests, successful syntax expansion without assertions, and uncommitted files alone never qualify as `COMPLETE`.
