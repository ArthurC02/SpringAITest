# Architecture Improvements Continuation — Status

> Rebased 2026-08-06 against clean `HEAD=44f9de4`. This is a coordination record, not a release declaration. Source, executable tests, and the authoritative phase gates remain decisive.

## 1. Current state

| Work item | State | Current evidence and boundary |
| --- | --- | --- |
| R0 | COMPLETE | Whole-system inventory and risk triage completed; follow-up work was split into bounded waves. |
| P2.5 | COMPLETE | Migration-foundation follow-up completed within the pre-P3 boundary. It does not claim the P3 production bundle or hard cutover. |
| Wave2 | COMPLETE | Security and bounded-input hardening completed with focused service tests. |
| Wave3 | COMPLETE | Persistence/contract correctness work completed with focused repository and API verification. |
| Wave4-A | COMPLETE | Runtime resilience work completed with its focused tests. |
| Wave4-B | COMPLETE | Reassessment reopened and then closed the candidate-paging defect. Workflow now consumes the Backend cursor contract to exhaustion with bounded page/candidate limits and fail-closed envelope, progress, cursor, and duplicate validation. Operational delete remains blocked as designed. |
| Wave5-A | COMPLETE | Health/readiness hardening completed. Focused Platform health tests reported 9 passing; `Platform.sln` build reported 5 projects, 0 errors, 0 warnings. |
| Wave5-B | ACTIVE / BLOCKED | Wave5-B1 tracing, Wave5-B0a calibration-contract tooling, and Wave5-B2 bounded telemetry are COMPLETE after independent review. B0b runtime calibration remains BLOCKED on a reachable Docker daemon and reviewed workload execution; B3 calibrated ceilings cannot proceed without that evidence, and no ceiling is approved. |
| Wave6 / P3 | RECONCILED / BLOCKED_BY_C8_GATE | P3-R0 re-inventoried `44f9de4` and passed independent review. The old destructive sequence is superseded: non-destructive P3-R1 baseline validation and P3-R2 evidence-contract work may proceed, while P3-R3/P3-X remain blocked until post-P5/C8 and the applicable independent route-retirement gates. |
| Wave4-C | SPLIT / BLOCKED | C1 may define the restore-drill contract while delete stays off. A production archive provider and operational delete enablement require an external archive target, credentials/KMS/retention policy, distributed claim fencing, and should follow the P3 schema decision. |
| Final integration closure | BLOCKED | Blocked on the remaining Wave5-B tranches, P3 reconciliation/execution when authorized, Wave4-C2, final documentation sync, end-to-end verification, and release evidence. |

The clean `44f9de4` baseline supersedes the prior dirty-worktree snapshot. This continuation now has a scoped uncommitted diff for Wave4-B paging, Wave5-B1 tracing, Wave5-B0a calibration tooling/CI, Wave5-B2 telemetry, documentation, and plan reconciliation. P3-R0 reconciliation is complete; the next independent work is non-destructive P3-R1 baseline validation, P3-R2 evidence-contract design, or B0b runtime calibration when Docker becomes available. Compose ceilings remain blocked on reviewed runtime evidence.

## 2. Evidence retained now

- Wave5-A focused check: `dotnet test tests/Platform.Web.Tests/Platform.Web.Tests.csproj --filter FullyQualifiedName~HealthApiTests` — 9 passed.
- Wave5-A compile check: `dotnet build Platform.sln --no-restore` — 5 projects, 0 errors, 0 warnings.
- Wave4-B is coordinator-confirmed complete; before integration, copy its final report's exact focused/full commands into the PR or handoff record. Do not infer them from a green aggregate run. `report` remains usable, while `delete` fails closed at configuration, startup assembly, and service execution until a real evidence provider exists.
- Wave4-B paging correction: `uv run pytest tests/test_checkpoint_retention.py -q` — 13 passed with one existing Starlette/httpx deprecation warning; independent review PASS.
- Wave5-B1 tracing: `uv run pytest tests/test_infra_hardening.py -q` — 18 passed; independent delta review PASS. Enabled initialization failures now emit at most one fixed content-free warning per process and increment a process-local counter; disabled tracing remains silent.
- Wave5-B0a contract: `python -m unittest scripts.tests.test_resource_calibration -v` — 10 passed; checked-in inventory validation and all four real expanded Compose snapshot validations passed; independent delta review PASS.
- Wave5-B0b runtime evidence: BLOCKED on this host because the Docker daemon is unavailable. The collector returned its sanitized blocked path with inner exit code `3` and created no bundle. This is not calibration evidence and approves no ceiling.
- Wave5-B2 bounded telemetry: Backend uses only built-in `Meter` counters. `backend.document_processing.outcomes` emits exactly once for each final `DocumentProcessor` result (including idempotent ready/deleted success) with its sole `outcome` tag limited to `success`, `retryable_failure`, or `terminal_failure`. `backend.health.readiness.checks` uses only `status=up|down` for actual fresh database-required probes; cache hits, database-optional mode, cancelled owners/waiters, and singleflight joiners do not emit, while timeout/internal exception probes emit `down`. Listener failure cannot change core behavior, and neither metric carries IDs, content, or error text. Workflow deliberately remains unchanged: its existing admission snapshots are the source of truth, with no approved sink/exporter/public endpoint for a duplicate adapter. Focused tests: 31 passed; Backend full suite: 1205 passed. Initial independent review found and corrected non-cooperative owner-cancellation mutation plus missing timeout/internal-exception coverage; delta and post-simplification reviews PASS, with a clean scoped diff check.
- R0 through Wave4-A were completed earlier in the same architecture-improvement run. Their detailed command logs remain in the originating task reports; this document intentionally does not manufacture counts that were not re-run here.

## 3. Workspace and baseline warning

At rebase time the repository is clean and the previous architecture-improvement changes are present in `44f9de4`. This was an external state change relative to the 2026-08-05 handoff, so future reports must identify both the inspected HEAD and current worktree state.

- Do not use a whole-tree reset, checkout, formatter, or bulk rewrite.
- Before each next wave, record `git status --short`, assign file ownership, and diff only the files owned by that wave.
- A modified, untracked, or newly committed file is not proof that its wave is complete.
- Before merge, reconcile every completion report against the filesystem, especially claims about deleted files, dead code, lock safety, and workspace cleanliness.

## 4. Authoritative references

- P3/P4/P5 sequencing: [Architecture Hard Reset plan](../architecture-hard-reset/01-plan.md)
- P3 acceptance: [Architecture Hard Reset acceptance tests](../architecture-hard-reset/04-acceptance-tests.md)
- Deletion obligations: [Deletion and migration ledger](../architecture-hard-reset/05-deletion-and-migration-ledger.md)
- Historical P3 inventory: [P3 inventory](../architecture-hard-reset/07-p3-inventory.md)
- Executable P3 reconciliation: [P3-R0 through P3-X](../architecture-hard-reset/08-p3-reconciliation-44f9de4.md)
- Historical cleanup rationale: [Cleanup and consolidation](../agent-architecture-improvements/06-cleanup-and-consolidation.md)
- Release evidence posture: [Copilot shared-core release evidence plan](../copilot-shared-core/05-release-evidence-plan.md)
