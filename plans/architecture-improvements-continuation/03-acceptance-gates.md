# Architecture Improvements Continuation — Acceptance Gates

## 1. Universal gate

Every wave must provide:

- focused tests for each changed branch and failure mode;
- the owning service's build/full suite in proportion to risk;
- failure injection for downstream unavailability, timeout/cancellation, malformed input, and replay where applicable;
- canary/feature-gate behavior and a configuration-only rollback path where the current contract promises one;
- explicit stop conditions;
- a dead/duplicated-code inventory with replacement evidence;
- diff hygiene and a scoped dirty-worktree report.

Skipped tests are evidence only when the skip is itself expected and separately recorded. A green suite that skipped the only production integration path does not pass the gate.

## 2. Wave4-B reopened closure gate

Wave4-B paging correction passed this gate on 2026-08-06:

- 101+ candidates are traversed across all pages without omission or duplication;
- malformed, repeated, or non-progressing cursors fail closed;
- attach its final focused/full test commands and results;
- inspect the claimed owned files and confirm no temp artifacts or accidental cross-wave edits;
- rerun any focused test affected by later shared-file changes;
- stop and reopen Wave4-B if its report cannot be matched to the current worktree.

Its dead-code inventory must identify any superseded helper, fallback, compatibility test, or duplicated lifecycle path removed by Wave4-B. "No dead code" requires search evidence.

## 3. Wave5-B gate

Wave5-B is split. Tracing may complete independently, but the overall wave remains incomplete until calibration, telemetry, and calibrated ceilings pass.

Wave5-B0a contract tooling passed independent review on 2026-08-06. Its versioned steady-running manifests are explicitly exploratory and `ceiling_approving=false`; they provide comparable baseline collection only. Runtime calibration remains blocked until reviewed workloads execute with a reachable Docker daemon.

### Compose and CI

- A reviewed measurement bundle records workload, engine/host metadata, sample window, and peak Docker memory-usage/CPU/PID evidence before any default is introduced. Docker `MemUsage` must not be represented as process RSS.
- Expanded base and evidence Compose JSON contains the approved default memory, CPU, and PID ceilings for every calibrated target.
- Environment overrides change the effective values without editing YAML.
- Evidence counterparts match their normal-service class unless an evidence-specific measured override is supplied.
- Read-only root filesystem, non-root user, `no-new-privileges`, dropped capabilities, bounded `/tmp`, and healthchecks remain present for application containers.
- Langfuse, ClickHouse, Redis, MinIO, mem0, and every other unmeasured service appear in an executable `calibration_pending` inventory. Partial guessed limits fail CI.

Failure injection: before calibration, prove every unmeasured service remains `calibration_pending`. After calibration, set one override per resource dimension, expand Compose, and assert the effective JSON value. Supply malformed/blank values and require Compose/CI to fail rather than silently discard the ceiling.

Rollback: remove the override to return to the reviewed default. If a default causes OOM, PID exhaustion, sustained throttling, or healthcheck instability under the deterministic smoke workload, stop rollout and restore the prior Compose revision; do not raise ceilings without evidence.

### Workflow tracing

- Enabled tracing plus handler initialization failure returns an empty runnable config and leaves execution available.
- Repeated failures increment a content-free low-noise counter but emit only one warning per process.
- The warning includes no exception text, prompt, payload, key, host credential, tenant/user/run ID, or model content.
- Disabled tracing does not import/initialize the handler, warn, or increment the counter.

Failure injection: make the handler raise two distinguishable secret-bearing exceptions and prove neither secret reaches captured logs.

Rollback: disabling the existing tracing flag returns to the no-handler path without requiring service redeploy logic beyond configuration.

### Backend built-in telemetry — Wave5-B2 COMPLETE (2026-08-06)

- Only built-in `Meter` facilities are used; no observability package was added.
- `backend.document_processing.outcomes` has exactly one `outcome` tag whose only values are `success`, `retryable_failure`, and `terminal_failure`. It emits exactly once for every final `DocumentProcessor` result, including an idempotent ready/deleted success.
- `backend.health.readiness.checks` has exactly one `status` tag whose only values are `up` and `down`. It emits only for an actual fresh database-required probe: cache hits, database-optional mode, cancelled owners/waiters, and singleflight joiners emit nothing; bounded timeout and internal exception probes emit `down`.
- Instrument-listener failure cannot change document-processing or readiness behavior. Neither metric carries tenant, user, document/run/checkpoint ID, free-form error text, prompt/content, route, tool, or model dimensions.
- Built-in listener tests assert the exact instruments and low-cardinality dimensions. The focused set passed 31 tests; the full Backend suite passed 1205. The independent initial review correction fixed non-cooperative owner-cancellation mutation and supplied timeout/internal-exception coverage; delta and post-simplification reviews passed.

Workflow intentionally adds no telemetry adapter: its existing admission snapshots remain the source of truth, and no approved sink, exporter, or public endpoint exists for a duplicate adapter.

Stop any future metric proposal that needs an identifier, free-form error text, content, or an unbounded route/tool/model tag.

Dead-code inventory: update stale "silent tracing" comments/tests, consolidate duplicated Compose resource maps into reviewed anchors, and list any superseded one-off metric helper. Preserve durable ledgers and release evidence.

## 4. Wave6 / P3 gate

P3 acceptance is exclusively the matrix in [Architecture Hard Reset acceptance tests](../architecture-hard-reset/04-acceptance-tests.md), supported by the [deletion and migration ledger](../architecture-hard-reset/05-deletion-and-migration-ledger.md) and [P3 inventory](../architecture-hard-reset/07-p3-inventory.md). This document adds no substitute checklist.

Mandatory execution properties:

- replay all applicable P2 migration-safety cases against the canonical production bundle;
- prove fresh/reset schema fingerprint equivalence;
- update all four services and every in-repo consumer in the same integration tranche;
- complete the deletion ledger in the same phase as each authoritative replacement;
- exercise destructive migration only against a disposable target or an explicitly confirmed development database;
- prove the documented roll-forward/appdb-restore recovery boundary.

Stop immediately on unknown database classification, checksum drift, lock ambiguity, partial consumer cutover, schema fingerprint mismatch, tenant-isolation regression, or any requirement to restart an old binary after reset.

**Superseded (2026-08-06 P3-R0 reconciliation; corrected 2026-08-09):** this file originally claimed P3 has no compatibility canary or rollback window. Per the current P3 authority [../architecture-hard-reset/08-p3-reconciliation-44f9de4.md](../architecture-hard-reset/08-p3-reconciliation-44f9de4.md) §1, compatibility routes deliberately coexist during P2–P5, C8 retirement requires usage `= 0`, consumer cutover, a completed rollback window, and human approval, and the destructive physical split is blocked until post-P5/C8 P3-R3. P3 recovery and rollout rules are those defined by Architecture Hard Reset.

## 5. Final release gate

Release requires:

1. Independent review findings resolved and re-tested.
2. Behavior-preserving simplification reviewed and re-tested.
3. Documentation synchronized after code stabilizes.
4. Backend, Platform, Workflow, and Frontend full suites green with required PostgreSQL tests actually executed.
5. Compose expansion, shell syntax/LF, diff hygiene, container hardening, health/runtime smoke, and relevant outage drills green.
6. Cross-service end-to-end evidence and the existing [release evidence plan](../copilot-shared-core/05-release-evidence-plan.md) completed or honestly marked blocked.
7. Final dead-code search and deletion-ledger reconciliation complete.
8. Dirty worktree partitioned into reviewable intended changes; no unexplained files and no false "workspace clean" claim.

Any failed real-model release gate, unauditable routing/tracing capture, skipped production checkpoint/database test, or unresolved high-risk review finding blocks release even when deterministic unit suites are green.
