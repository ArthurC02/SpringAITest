# Architecture Improvements Continuation — Remaining Plan

## 1. Execution order

### C0 — Reconcile completed-wave evidence

Before new implementation, capture the Wave4-B final report and verify its claimed files and commands against the dirty worktree. Wave4-B is complete; this is evidence bookkeeping, not a new implementation wave.

If later reconciliation shows an acceptance failure, reopen only the owning wave. Do not silently fold the repair into Wave5-B or P3.

### C0.1 — Wave4-C: retention evidence provider and restore drill

Operational checkpoint deletion remains BLOCKED. Implement a real backup/restore evidence provider, wire it explicitly into Workflow startup, and complete a restore drill that proves retained evidence can reconstruct the targeted checkpoint/root context before enabling `CHECKPOINT_RETENTION_MODE=delete`. Only then may the current configuration and startup fail-closed gates be relaxed. Report mode must remain independent and available throughout.

### C1 — Wave5-B: resource ceilings and observability closure

Wave5-B is PENDING and must be implemented as a bounded operational-hardening change.

1. Add environment-overridable, effective Compose `mem_limit`, `cpus`, and `pids_limit` values through shared anchors for the measured starting set: Frontend, Platform, Backend, Workflow, LiteLLM, RabbitMQ, and appdb.
2. Apply symmetric limits to isolated evidence counterparts. Services without measurements remain explicitly inventoried as `calibration_pending`; do not guess ceilings.
3. Extend CI to inspect expanded base/evidence Compose JSON, prove the effective ceilings, and prove application hardening still coexists with them.
4. Change Workflow tracing callback initialization failure from silent degradation to a content-free warning emitted once plus a low-noise failure counter. Disabled tracing must remain silent.
5. Use only existing Backend logging, `Meter`, and `Activity` capabilities. Add low-cardinality signals only at natural health/admission/document/checkpoint hooks, and do not duplicate durable operations ledgers.
6. Add focused tests, Compose expansion checks, shell syntax checks, and Docker health/runtime smoke when Docker is available.

Wave5-B must inventory old comments/tests that describe tracing failure as "silent" and any duplicated resource-limit declarations made obsolete by shared anchors.

### C2 — Wave6: Architecture Hard Reset P3

Do not restate or partially reinterpret P3 here. Execute it only as the single cross-service integration tranche defined by:

- [Delivery plan §6](../architecture-hard-reset/01-plan.md#6-p3--schema-and-artifact-hard-cutover)
- [Target specification](../architecture-hard-reset/02-spec.md)
- [Acceptance tests, P3 matrix](../architecture-hard-reset/04-acceptance-tests.md)
- [Deletion and migration ledger](../architecture-hard-reset/05-deletion-and-migration-ledger.md)
- [Refreshed P3 inventory](../architecture-hard-reset/07-p3-inventory.md)

The continuation rule is simple: refresh line references and consumers, then execute the existing P3 ledger. Do not create a parallel compatibility plan, `/v2` surface, dual write, or partial main-branch cutover.

### C3 — Independent review and simplification

After each non-trivial implementation tranche:

1. Run an independent code review with special attention to tenant isolation, transaction/lock ordering, cancellation, disposal, feature-gate order, and content-free telemetry.
2. Resolve every actionable finding and rerun its focused tests.
3. Run a behavior-preserving simplification pass on only the recently changed files.
4. Re-review the simplified diff and repeat relevant tests.

Sol is reserved for the high-risk review described in [02-subagent-runbook.md](02-subagent-runbook.md); Terra remains the implementation default.

### C4 — Documentation, end-to-end, and release evidence

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
