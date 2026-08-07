# P3 Reconciliation — Baseline `44f9de4`

> Status: P3-R0 planning reconciliation COMPLETE after independent review, 2026-08-06. This is not implementation, migration, or release evidence. Existing uncommitted Wave4/5 changes are outside P3 and must be preserved.

## 1. Authority and decision

The original hard-reset dossier assumed that P3 could physically split artifacts while deleting compatibility routes, aliases, unified invocation, and the shared discriminator model. Current root and area contracts supersede that assumption:

- During P2–P5, `/api/skills*` and `/api/business-workflows*` deliberately coexist for Business Workflow artifacts.
- Internal `POST /skills/{name}/invoke` remains unified across both artifact kinds.
- `/skills/validate` remains the same-handler compatibility alias; it has its own later consumer/usage/rollback retirement gate.
- Public `/api/skills*` may narrow to Agent-Skill-only only after cleanup C8 proves legacy flow-write usage `= 0`, consumer traffic has cut over, the rollback window has completed, and a human approves retirement. C8 removes only Business Workflow compatibility operations; Agent Skill endpoints remain and need positive existence/behavior acceptance. C8 does not retire the internal alias or unified invoke.
- Unified `POST /skills/{name}/invoke` likewise remains until its own separately approved consumer/usage/rollback gate passes.

Therefore the original destructive physical split is blocked until post-P5/C8 P3-R3, while route retirement is split into independent gates: C8 for public `/api/skills*`, and separate approvals for alias and unified invoke. P3-X may preserve either internal surface through a compatibility facade. The current `/api/admin/operations/legacy-inventory` is compile-time constants only; it is not trustworthy runtime-usage evidence.

## 2. Preserved P2 foundation and verified baseline facts

Keep the migration-runner, advisory-lock, checksum, database classification, fingerprint, and disposable restore-drill design. Current facts are:

- Production manifest remains `bundleThroughVersion=0`; `DbBootstrap` remains startup and seed authority.
- `ProductionManifestAbsenceTests` correctly assert that no production SQL is currently shipped. This plan does not claim P3 SQL is ready.
- Current Backend application schema baseline is 52 tables: the earlier 50 plus `document_ingest` and `checkpoint_retention_ack`. Five Workflow-owned checkpoint tables (`checkpoints`, `checkpoint_blobs`, `checkpoint_writes`, `checkpoint_migrations`, `workflow_root_context_checkpoint`) may exist and remain classification inputs.
- `conversations_history_page_idx` must be added to target fingerprint/postconditions.
- P3-R1 added the verified `checkpoint_retention_ack` entry to `MigrationManifest` and pinned the complete current inventory with executable tests; future drift is an execution blocker.
- Prompt manifests, evals, Context E1/E3, D3–D7, document-ingest idempotency, conversation history, and retention ACK are retained design inputs.

The read-only inventory commands used for this reconciliation inspected `git status`, the baseline plan set, current continuation plans, and literal plan/source references. They establish planning inputs only; they are not product test PASS evidence.

## 3. Current retention and conditional cleanup

No relevant production source is dead today. Retain until its relevant replacement proof and retirement gate:

- Backend/Platform: `SkillController`, `SkillService`, `WorkflowEngineClient`, and `SkillRoutingAgent`.
- Frontend: unified skills API, types, history, and run UI.
- Workflow: compatibility handler, custom loader, unified invoke, `agent_skill_graph`, `flow_harness`, runtime graph/checkpoints, and eval wire `kind`.
- Associated tests, including compatibility and alias coverage.

Only after the relevant replacement evidence and gate may a retirement package evaluate deletion of shared skill storage/repositories/kind DTOs, mixed pin tables/collections/kind branches, and flow-package parser/tests. C8 is sufficient only for public narrowing; alias and unified-invoke removal need their own approvals. The physical split must first define cross-type same-name semantics for typed snapshots; no first-match policy is authorized.

## 4. Stop gates

Stop immediately on any of the following:

1. No destructive SQL or schema split before P3-R1, P5/C8, and post-C8 P3-R3.
2. C8 cannot remove alias or unified invoke. No removal of either, flow compatibility, or coexistence tests before its separate retirement approval.
3. No action with an unknown schema, allowlist, or fingerprint mismatch.
4. No partial database/consumer deployment; P3-X is atomic across schema, services, seed, fixtures, restore, and full-chain gates.
5. No implementation while typed artifact routing or cross-type name collision remains unresolved.

## 5. Executable work packages

| Package | State | Scope and exit evidence |
| --- | --- | --- |
| P3-R0 — authority reconciliation | COMPLETE | This document and synchronized historical plan notices passed independent planning review; no source change. |
| P3-R1 — mechanical current-baseline inventory/fingerprint validation | COMPLETE (2026-08-07) | Executable tests pin the 52-table baseline, five optional checkpoint tables, `conversations_history_page_idx`, and `plpgsql`/`vector`; production manifest remains bundle 0 with no SQL. Migration suite: 66 passed, 1 Docker dump/restore skip. |
| P3-R2 — C8 public-narrowing evidence contract | DESIGN COMPLETE, EVIDENCE PENDING | [09-p3-r2-runtime-evidence-contract.md](09-p3-r2-runtime-evidence-contract.md) fixes the contract; bounded counters, fixed-schema JSON events, `scripts/export-artifact-compatibility-usage-v1.py`, and tests are implemented. Production log retention/extraction, deployment observation, external consumer attestation, rollback proof, and C8 approval remain incomplete. |
| P3-R3 — post-C8 target decision sheet | BLOCKED until P5/C8 | Freeze typed tables/FKs/snapshots/eval/operations identity, same-name semantics, seed authority, package hashes, canary retention, and required facades. No SQL before all decisions are frozen. |
| P3-X — destructive execution | BLOCKED until P3-R3 + applicable route gates | Atomically add production `0001`–`0003`, activate runner and fixtures, update every consumer/seed, and prove fresh/reset fingerprint, restore, and full-chain acceptance. Alias/unified-invoke facades remain unless their independent gates passed. |

## 6. Acceptance transition

P3-R acceptance replaces the old route-absence claim with reconciliation and readiness gates. Original P3-01 through P3-11 remain deferred P3-X tests where compatible with the retained contract. After C8, absence applies only to removed Business Workflow compatibility operations under `/api/skills*`; Agent Skill endpoints require positive existence/behavior acceptance. Alias and unified-invoke absence are invalid until their independent later approvals. P2 migration-safety test design remains intact and must be replayed against a future canonical production bundle only when P3-X is authorized.

## 7. Required future decision record

Before P3-X, P3-R3 must answer each item explicitly: typed table and FK shape; snapshot and eval/operations identity; cross-type same-name semantics; package/instruction hashes; seed authority and dependencies; canary retention; and which compatibility facades remain. C8 decides only public narrowing; alias and unified-invoke retirement require separate later consumer/usage/rollback approvals. A missing answer is a stop condition, not permission to select a first-match fallback.
