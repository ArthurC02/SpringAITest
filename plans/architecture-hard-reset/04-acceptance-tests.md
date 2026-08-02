# Architecture Hard Reset — Acceptance Tests

> Status: approved target, not executed. These gates become authoritative only with their implementation phase.

> A phase is not complete because implementation exists. It is complete only when its listed evidence passes against the current integrated repository.

## P0 — plan integrity

| ID | Scenario | Expected evidence |
| --- | --- | --- |
| P0-01 | Plan files and index | All six documents link correctly; no broken relative links |
| P0-02 | Superseded rules | P5/C8/R6 historical files point to this plan without erasing their rationale |
| P0-03 | Inventory | Every old route, table, FK, flag, runtime branch, test, and external state has a ledger disposition |
| P0-04 | Baseline | Full Backend, Platform, Workflow, Frontend and config/script checks are recorded before implementation |

## P1 — correctness and deployment

| ID | Scenario | Expected result |
| --- | --- | --- |
| P1-01 | Workflow draft save gets 409 | Editor locks, exposes reload, and emits no success toast |
| P1-02 | Orchestrator draft save gets 409 | Same conflict behavior as Workflow editor |
| P1-03 | JSON field becomes invalid | Field error is visible and create/save/validate/publish are disabled |
| P1-04 | JSON is corrected | Parsed value updates and actions become available without stale payload |
| P1-05 | Workflow raises unexpected exception | HTTP uses fixed safe message/code and correlation ID; log contains the exception |
| P1-06 | Second child cancellation fails | InMemory root/event/idempotency expose no half-committed terminal state |
| P1-07 | Caller cancels deadline cascade | `OperationCanceledException` propagates and is not converted to durable timeout |
| P1-08 | Compose gates enabled | Every enforcing service receives the expected effective value |
| P1-09 | Native bootstrap/health command fails | PowerShell and shell entry points return nonzero with the failing command identified |
| P1-10 | Any public endpoint returns an error | Response matches the complete ApiError envelope and carries correlation ID |

## P2 — migration-runner safety with fixtures

P2 executes this matrix only with test-only manifests and synthetic allowlisted schemas in disposable databases. It proves runner mechanics, not the production target schema. P3 must replay the applicable matrix against the shipped canonical `0001`–`0003` bundle before cutover.

| ID | Scenario | Expected result |
| --- | --- | --- |
| P2-01 | Empty disposable database | Fixture migrations initialize successfully |
| P2-02 | Legacy database without confirmation | Migration/startup fails before any DDL or deletion |
| P2-03 | Synthetic known-legacy fixture with exact confirmation | Fixture reset, replacement schema, audit, and ledger commit atomically |
| P2-04 | Empty host/database argument or protected system database name | Command refuses to connect or mutate; an actual empty disposable application database remains valid for P2-01 |
| P2-05 | Remote database without second confirmation | Command refuses destructive execution |
| P2-06 | Any `0001`–`0003` bundle statement or postcondition fails | The single bundle transaction rolls back and legacy schema/data remain |
| P2-07 | Process is killed during migration | Transaction rolls back, lock releases, and rerun succeeds |
| P2-08 | Same version and checksum rerun | No-op; no new cleanup or deletion occurs |
| P2-09 | Same version with changed checksum | Fail closed before executing pending SQL |
| P2-10 | Two runners start concurrently | Advisory lock serializes them; migration applies exactly once |
| P2-10a | Migration lock exceeds its bounded timeout or caller cancels | Stable diagnostic, no mutation, and no indefinite startup hang |
| P2-11 | Migration postcondition fails | No completion row is recorded |
| P2-12 | Cleanup audit | Table counts match the pre-reset inventory and contain no row payload |
| P2-13 | Fresh versus reset fixture database | The versioned full catalog projection produces identical normalized fingerprints; the only excluded categories are catalog OIDs, owners, ACL ordering, physical row/index order, statistics, and migration/audit timestamps, exactly as listed in 03-design §1.3 |
| P2-14 | Snapshot/restore drill | `pg_dump --format=custom` of the disposable fixture database, restored via `pg_restore` into a second disposable database, passes the same fingerprint comparison as P2-13. This is a mechanics drill for the appdb recovery boundary in 03-design §2, not physical volume snapshotting. |
| P2-15 | Applied manifest has name/checksum/version drift, a gap/duplicate, or a future version | Fail before pending or destructive SQL |
| P2-16 | Unknown object exists in `public` | Hard reset refuses to drop the schema and reports the unexpected object. This case must be evaluated after the extension-ownership exclusion in 03-design §1.3; a database with pgvector correctly installed into `public` is not an unknown-object case. |
| P2-17 | P2 binary starts normally | Production hard-reset SQL is absent from its registered manifest; old schema remains authoritative |

Migration tests must use disposable databases with allowlisted generated names. They may not target the normal `springaitest` appdb.

## P3 — artifact hard cutover

| ID | Scenario | Expected result |
| --- | --- | --- |
| P3-00 | Replay applicable P2-01–P2-16 cases with canonical `0001`–`0003` | Production bundle passes empty/current/legacy/unknown classification, atomic rollback, audit, concurrency, rerun, drift, and full-schema-fingerprint gates before cutover |
| P3-01 | Agent Skill package CRUD/revision/export/invoke | Uses only Agent Skill tables and endpoints |
| P3-02 | Business Workflow CRUD/revision/export/validate/invoke | Uses only Business Workflow tables and endpoints |
| P3-03 | Agent Skill endpoint receives flow content | Controlled validation failure; no sniffed dispatch |
| P3-04 | Business Workflow endpoint receives package | Controlled validation failure |
| P3-05 | Removed aliases/routes | Return 404 and have no registered endpoint metadata |
| P3-06 | Database catalog inspection | No `skill`, `skill_revision`, mixed `kind`, or old FK remains |
| P3-07 | Snapshot/binding inspection | Agent Skills and Business Workflows use distinct typed references |
| P3-08 | Static consumer inventory | No production reference to removed DTOs, routes, repositories, or discriminators |
| P3-09 | Seed rerun | Idempotent and produces complete valid development artifacts |
| P3-10 | Harness pins after reset | Agent revision, Agent run, and Orchestrator revision FKs still target Harness `workflow_revision`, never Business Workflow revisions |
| P3-11 | Agent Skill package import/restore | Workflow `/agent-skills/validate-package` remains the sole package parser and validates the new route |

## P4 — Orchestrator-only chat

| ID | Scenario | Expected result |
| --- | --- | --- |
| P4-01 | Chat/history/stream/AG-UI without JWT | 401; no downstream call or memory write |
| P4-02 | JWT lacks tenant or user | Fail closed before runtime allocation |
| P4-03 | Tenant lacks active Root binding | Stable unavailable error; no legacy fallback |
| P4-04 | Worker/Verifier/Workflow pin is invalid | Fail closed before command execution |
| P4-05 | Valid blocking chat | Durable Root run completes and history is caller-scoped |
| P4-06 | Valid SSE chat | Uses Root runtime and preserves the documented SSE error frame |
| P4-07 | Valid AG-UI chat | Uses the same Root runtime/persistence with protocol-standard SSE formatting |
| P4-08 | Legacy selection search | No reachable legacy brain, Skill-routing fallback, canary flag, or legacy memory namespace |
| P4-09 | Old browser bundle/storage | Controlled reload/storage-version handling, not silent calls to deleted endpoints |
| P4-09a | Missing or old `X-Client-Schema-Version` reaches a changed endpoint | 426 `client_upgrade_required`; no downstream mutation |
| P4-10 | Explicit Orchestrator is cross-tenant, absent, or not entitled | Same 404 code and no ownership/existence disclosure |
| P4-11 | Conversation attempts to switch Orchestrator | `409 conversation_runtime_conflict`; original binding remains |
| P4-12 | Duplicate `turnId` is retried | One durable command/effect and the same logical result |
| P4-13 | Client disconnects after durable allocation | Run continues; no implicit cancellation or duplicate retry |
| P4-14 | Dispatch gate parity | Chat is ready only when Platform, Backend, and Workflow dispatch gates are effective; Designer gate is not a runtime prerequisite |
| P4-15 | History pagination | Caller-only chronological items, stable opaque cursor, default 50 and maximum 100 |

## P5 — modularity and operations

| ID | Scenario | Expected result |
| --- | --- | --- |
| P5-01 | Backend cross-aggregate write | Runs through one transaction coordinator and rolls back atomically |
| P5-02 | Engine dependency check | Runtime imports only public engine contracts, not concrete node families/private symbols |
| P5-03 | Workflow tests in parallel | Registry fixtures do not leak across invocations |
| P5-04 | Production configuration uses development defaults | Startup fails with an actionable diagnostic |
| P5-05 | Image manifest | Required deployable images resolve to reviewed digests |
| P5-06 | Cross-service trace | One correlation ID connects public request, internal calls, run/evidence, and sanitized errors |
| P5-07 | Dependency outage/restart | No duplicate effect, lost durable command, tenant crossover, or hanging client |
| P5-08 | Verification isolation | Evidence runners use per-run allowlisted databases, restore prior container state, and never mutate normal appdb |

## Final release gate

- Backend full tests, including disposable PostgreSQL migrations, pass.
- Platform Service and Web full tests pass.
- Workflow full tests and permitted parallel suite pass.
- Frontend lint, build, logic tests, UI tests, and evidence tests pass.
- Compose base/full/evidence configurations and shell/PowerShell syntax pass.
- Contract/schema snapshots are current and reviewed.
- Deletion ledger has no pending production-code item.
- Independent architecture/security review reports no unresolved High or Medium finding.
