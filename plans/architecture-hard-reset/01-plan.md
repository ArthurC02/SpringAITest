# Architecture Hard Reset — Delivery Plan

> Status: reconciled planning index (2026-08-06, baseline `44f9de4`). Source code and executable tests remain authoritative. The prior P3 hard-cutover sequence is historical and is blocked by the post-P5/C8 decision sequence plus any applicable independent route gates; see [08-p3-reconciliation-44f9de4.md](08-p3-reconciliation-44f9de4.md).

## 1. Goal and completion rule

If post-P5/C8 gates authorize it, replace compatibility-era branching with four internally modular services, physically separate Agent Skill and Business Workflow storage, an authenticated Orchestrator-only chat path, and a guarded SQL migration system. A physical split may retain compatibility facades until their independent retirement gates pass.

The program is complete only when the deletion ledger is empty, a fresh database and a destructively migrated legacy database have the same schema fingerprint, every in-repo consumer uses the target contracts, and the full cross-service verification is green.

## 2. Phase order

| Phase | Deliverable | Depends on | Merge gate |
| --- | --- | --- | --- |
| P0 | Plan, inventories, current-state baseline | — | Documents agree with current source; no runtime changes |
| P1 | Correctness and deployment stabilization | P0 | All existing tests plus focused regressions pass |
| P2 | Migration runner and guarded command framework using test-only fixture migrations | P1 | Safety suite passes against disposable PostgreSQL; no production schema change is discoverable |
| P3-R0 | Authority reconciliation | P2 | Current contracts, baseline, and stop gates reviewed |
| P3-R1 | Mechanical current-baseline validation | P3-R0 | No destructive SQL; current schema/fingerprint inputs validated |
| P3-R2 | C8 public-narrowing evidence contract and collection | P3-R0 | Usage, consumers, observation/rollback, and approval evidence mature independently |
| P4 | Authenticated Orchestrator-only chat and Workflow public engine boundary | P3-R1 + current runtime prerequisites | Chat/AG-UI use one durable runtime; legacy references are absent |
| P5 / C8 | Internal modularization and public `/api/skills*` Agent-Skill-only narrowing decision | P4 | P5 gates pass; C8 may remove only Business Workflow compatibility operations while Agent Skill endpoints remain |
| P3-R3 | Post-C8 target decision freeze | P3-R1 + P3-R2 + P5/C8 | Typed target/facade decisions frozen; no SQL |
| P3-X | Optional production SQL bundle plus schema/artifact physical cutover | P3-R3 + applicable separate route gates | Fresh/reset schema equivalence and all consumers pass atomically; required facades remain until their own gates pass |

P3-X, if authorized, is one cross-service integration tranche. A branch may contain intermediate commits, but main must never contain a Backend schema without matching Platform, Workflow, Frontend, seed, tests, and documentation.

## 3. P0 — planning and baseline

- Complete the route, schema, FK, feature-flag, persistent-state, and consumer inventories in the ledger.
- Record baseline commands and current counts for Backend, Platform, Workflow, Frontend, Compose, and script checks.
- Preserve historical P5/C8/R6 rationale without treating it as current authority; the current root and area retained-route/C8 rules remain authoritative.
- Before implementation, preserve the existing dirty worktree as a reviewable baseline; architecture work must not silently absorb unrelated edits.

## 4. P1 — correctness and deployment stabilization

- Make ETag conflicts explicit failures in Workflow and Orchestrator editors; no success toast is allowed.
- Track raw JSON text and validity; invalid fields block create, save, validate, and publish.
- Log unexpected Workflow exceptions with a correlation ID and return a fixed safe error.
- Give InMemory `CancelAsync` the same all-or-nothing cascade semantics as `ExpireLockedAsync` and PostgreSQL `ExpireDeadlineAsync`: cascade to every child first, commit the root terminal state only when all succeed, leave a non-terminal retryable state otherwise, and propagate caller cancellation rather than recording it as a child failure. The deadline path needs no further change.
- Forward the complete feature-gate matrix through Compose and correct the D6 documentation.
- Make `start-lite.ps1` and `start-lite.sh` exit nonzero when any required service fails its health check, instead of warning and continuing. Postgres readiness wait is already fail-fast; `ensure-mem0-db.ps1` CREATE DATABASE step (L31) lacks exit-code check and remains to be fixed; the `.sh` version has `set -e` protection.
- Add a repository CI entry point covering all four applications, Compose expansion, shell syntax, contract snapshots, and diff hygiene.

## 5. P2 — migration foundation

- Add `DbMigrationRunner`, resource discovery, checksum/lock handling, and the guarded command framework without registering it on normal production startup. Keep `DbBootstrap` as the production schema authority through P2.
- Prove the runner with test-only fixture migrations. Production `0001`–`0003` resources must not be registered or shipped in P2, so old binaries cannot create the target schema early. P2 tests do not claim target-schema or destructive-reset acceptance.
- Add guarded PowerShell and shell migration/reset commands. They must resolve and print the exact host/database/project before asking for destructive confirmation.
- Test advisory locking, checksum drift, transaction rollback, re-entry, empty/legacy/unknown database classification, and invalid targets using disposable PostgreSQL databases only.
- Keep seed outside schema migration and make it independently idempotent.

## 6. P3 — reconciliation and deferred schema/artifact cutover

The following former P3 bullets are historical target inputs only. They are not executable while the current compatibility contract remains in force. `P3-R0` completes on review; `P3-R1` and `P3-R2` may proceed independently; P4 follows R1/current runtime prerequisites; P5/C8 follows P4 while R2 evidence matures; `P3-R3` freezes typed schema, facade, same-name, seed, package-hash, and canary decisions only after C8; `P3-X` is optional and blocked until R3 plus the route gates applicable to its chosen facade design.

- Ship and register `0001`–`0003`, switch startup and PostgreSQL fixtures to `DbMigrationRunner`, and delete `DbBootstrap` in the same tranche.
- Add and register the production `0001_architecture_hard_reset.sql`, `0002_target_schema.sql`, and `0003_constraints_and_indexes.sql` bundle while replacing `skill` storage with independent Agent Skill and Business Workflow aggregates.
- Update every FK, snapshot, binding, eval candidate, repository, DTO, controller/client, workflow endpoint, UI client, fake, fixture, and contract test in the same tranche.
- Do not remove discriminator handling, dual-track routes, aliases, unified invoke, or coexistence tests in P3-R0–R3. C8 may authorize only removal of Business Workflow compatibility operations from public `/api/skills*` after its flow-write, consumer-cutover, rollback-window, and manual gate; Agent Skill endpoints remain and need positive behavior acceptance. `/skills/validate` and unified `/skills/{name}/invoke` each need separate later consumer/usage/rollback approval and may remain as facades through P3-X.
- Run no guarded destructive migration or production SQL during P3-R0–R3. P3-X may do so only against a validated exact target after its complete bundle, consumers, seed, fixtures, restore drill, and full-chain gates are ready together.

The tranche is large — roughly 100 to 150 files across four services plus SQL, scripts, and documentation. To keep it reviewable and bisectable without weakening the main-branch rule in section 2, sequence the work inside the branch as: (1) runner, SQL bundle, advisory lock and checksum handling; (2) fixture switch and the one-time developer migration step; (3) Backend schema, repositories, controllers, and FK rebuild; (4) Workflow routes and loaders; (5) Platform services and controllers; (6) Frontend types, API clients, and components; (7) test cleanup and documentation sync. Every intermediate commit must at least build (`dotnet build`, `npm run build`); a commit that does not compile is not a valid checkpoint even inside the branch.

## 7. P4 — one chat runtime and public Workflow contracts

- Require JWT on chat, stream, history, and AG-UI endpoints; remove body `userId` and anonymous continuity.
- Before enabling the new chat path, provision and validate the Root Workflow, Worker, independent Verifier, and runtime binding through the current non-destructive seed authority and preflight. P3-X later recreates equivalent seed only after its reset.
- Route every chat turn through the durable Root Orchestrator; missing configuration fails closed.
- Remove chat canary selection, legacy brain/routing, legacy memory namespaces, and fallback-only tests.
- Expose public Engine bootstrap/registry/script-contract APIs and remove runtime imports of concrete node families or compiler-private members.

## 8. P5 — internal modularization and operational closure

- Backend: separate command, query, and transaction coordination inside Agent Runs, Orchestrator Runs, Governance, and artifact modules.
- Platform: split composition registration by authentication, public API, runtime, and observability; centralize transport concerns without centralizing domain behavior.
- Workflow: separate claim/lease, checkpoint/recovery, and execution coordination; make test registries invocation-local.
- Frontend: split feature types/API/state/presentation by Agents, Workflows, Orchestrators, and Operations without adding a state or UI framework.
- Pin deployable images by digest, reject development defaults in the production profile, and validate cross-service traces and outage metrics.

## 9. Global constraints

- Preserve tenant isolation, idempotency, lease fencing, approvals, once-only effects, checkpoint integrity, redaction, and fail-closed gates.
- Do not introduce a fifth service, service mesh, new message bus, EF Core, or speculative abstraction.
- Every plan must remove code made dead by its replacement in the same phase.
- Destructive commands must never infer a broad target from an empty variable, current directory, or default database.

## 10. Plan set

- [Analysis](00-analysis.md)
- [Specification](02-spec.md)
- [Design](03-design.md)
- [Acceptance tests](04-acceptance-tests.md)
- [Deletion and migration ledger](05-deletion-and-migration-ledger.md)
- [Todo list](06-todo.md)
- [Historical P3 inventory](07-p3-inventory.md)
- [P3 reconciliation and work packages](08-p3-reconciliation-44f9de4.md)
