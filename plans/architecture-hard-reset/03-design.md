# Architecture Hard Reset — Design

> Status: approved target, not implemented. Current source and executable tests remain authoritative until the corresponding phase gate passes.

## 1. Migration architecture

### 1.1 Runner and ownership

`DbMigrationRunner` is the single SQL migration engine used by Backend startup and the dedicated migration command. SQL files are immutable embedded resources ordered by a numeric version.

Two modes are intentionally different:

- **Startup mode:** verifies checksums and applies registered safe pending migrations. It may initialize an empty database only after the P3 production bundle ships. If legacy application tables are present and the hard-reset marker is absent, startup fails with an operator-facing instruction; it never performs the destructive reset.
- **Dedicated destructive mode:** invoked only by `migrate-db.*` with the exact confirmation flag. It is the only mode allowed to execute `0001_architecture_hard_reset.sql` against a legacy database.

P2 ships only the runner, command framework, and test-only fixture migrations. The production `0001`–`0003` files and manifest ship atomically with P3 consumers. This prevents old code from starting against the target schema and prevents multiple Backend replicas from treating application startup as authorization to delete data.

### 1.2 Lock, checksum, and transaction

For the initial hard-reset bundle:

1. Open one Npgsql connection to the validated target database.
2. Begin a transaction, set a bounded lock timeout (default 60 seconds, configurable only from 5–300 seconds), and acquire a fixed numeric advisory transaction lock key declared in code and documentation.
3. Before durable mutation, inspect `public` and read existing `springaitest_meta` metadata when present. Classify the combined catalog/ledger state as empty, known legacy, known current, or unknown nonempty; reject unknown databases and any current ledger/schema mismatch. Only an allowed empty or confirmed legacy branch may create missing metadata objects; `springaitest_meta` remains outside the reset target.
4. Hash a manifest entry containing version, name, and canonical SQL bytes shipped in the assembly; normalize line endings once and forbid environment substitution.
5. Before any pending SQL, verify every applied row. Reject checksum/name drift, duplicate or non-contiguous versions, and a database version newer than the binary.
6. Execute `0001`, `0002`, and `0003`, followed by every bundle postcondition, in the same transaction.
7. Insert all three completed ledger rows in that transaction and commit once.

A lock timeout or cancellation returns a stable diagnostic and performs no mutation. The lock covers classification, applied-row verification, SQL, assertions, audit, and ledger commit. A crash rolls back the complete bundle and its ledger rows; connection close releases the lock. Later non-destructive migrations use the same algorithm with one file per transaction. Migration SQL must not contain non-transactional operations such as `CREATE INDEX CONCURRENTLY` or `VACUUM`. A future need for those requires a separately designed durable phase state machine, not a hidden exception.

### 1.3 Hard-reset SQL

`0001_architecture_hard_reset.sql` has two explicit branches. The empty-database branch may run in startup or dedicated mode after P3 ships. The destructive legacy branch runs only in dedicated mode:

1. Classify `empty`, `known legacy SpringAITest`, `known current target`, or `unknown nonempty`. Empty is allowed; known legacy requires confirmation; known current requires a valid contiguous ledger and all target postconditions and is a no-op when no migration is pending; unknown nonempty, ledger/schema mismatch, and protected system databases are rejected.
2. Require the exact allowlist of SpringAITest-owned tables, views, sequences, extensions, and schema markers. Any additional object aborts rather than being silently dropped.
3. Create a temporary inventory of allowlisted application table names and exact row counts.
4. Remove and recreate only the application-owned `public` schema objects, grants, and required extensions. `springaitest_meta` is retained.
5. Persist inventory counts and the execution UUID without row content. The runner—not SQL—writes completion rows after all bundle assertions pass.

`0002_target_schema.sql` creates all target tables. `0003_constraints_and_indexes.sql` adds and asserts every FK, CHECK, unique constraint, and operational index. The runner treats all three files as one transaction even though they remain separate reviewable resources. Fresh databases execute the same bundle, with `0001` taking its empty-database branch; therefore fresh and reset schema fingerprints converge.

The normalized schema fingerprint is computed from a sorted catalog projection of every migration-owned object: schema names; extensions including version and installation schema; tables and partitioning; columns (name, logical type, nullability, identity/generation, collation, and normalized default); constraints including FK actions; indexes; views/materialized views and normalized definitions; sequences and ownership/options; functions/procedures and signatures/definitions; triggers; custom types/domains/enums; and row-level-security state/policies. If a category is intentionally absent, an explicit postcondition asserts that absence. The projection excludes catalog OIDs, owners, ACL ordering, physical row/index order, statistics, and migration/audit timestamps. The fingerprint query and canonical serializer are versioned test assets shared by fresh-database and legacy-reset verification; equality never relies on textual `pg_dump` output.

The destructive command prints the resolved host, port, database, user, optional compose project, and expected legacy markers. It requires a literal confirmation token containing the database name. Empty variables and `postgres`/`template0`/`template1` are rejected. Local Compose cleanup additionally requires project `springaitest`; database migration itself relies on database markers, not a spoofable Compose name. A remote SpringAITest development database is allowed only with both `--allow-remote` and a second exact host/database confirmation.

## 2. Controlled cutover

The hard cutover is an operator procedure, not ordinary service startup:

```text
maintenance response / drain ingress
  -> disable chat, writes, dispatch, approvals
  -> cancel or explicitly abandon active runs; stop queue writers and consumers without purging
  -> stop all Frontend publication, Platform instances, Workflow workers/claim loops, Backend instances/background consumers, and evidence jobs
  -> take snapshot and complete a restore drill
  -> prove no active leases/commands/approvals/outbox delivery and run one dedicated migration job
  -> verify migration checksums and schema fingerprint
  -> purge named development queues and clean confirmed mem0/Lite development state
  -> run development seed
  -> start Backend
  -> start Workflow with runtime gates off and verify checkpoint/internal APIs
  -> start Platform with runtime gates off and verify auth/proxy
  -> publish Frontend and require storage/build-version handshake
  -> enable retained gates in dependency order
```

Because existing data is deliberately discarded, active runs, approvals, immutable pins, conversations, checkpoints, and outbox rows are deleted together. The recovery boundary is appdb only: its snapshot/restore drill proves database recovery, while mem0, RabbitMQ application queues, and Lite state are intentionally cleaned only after the database migration succeeds and are not rollback-restored. No old binary may be restarted after the reset; recovery is roll-forward or restore appdb followed by explicit external-state reseeding.

## 3. Backend modules

```text
Artifacts/
  AgentSkills/          controller, contracts, repository, revisions
  BusinessWorkflows/    controller, contracts, repository, revisions
HarnessWorkflows/       D4 Graph IR drafts and revisions
AgentRuns/              queries, commands, transaction coordinator
OrchestratorRuns/       queries, commands, cascade coordinator
Governance/             approvals, effects, evidence, eval, rollout
Data/Migrations/        runner and immutable SQL
```

Repositories own aggregate-local SQL. A coordinator owns any transaction spanning aggregates and passes the same connection/transaction explicitly. Controllers do not compose partial writes. InMemory implementations use one staged commit under the documented lock order so failure/cancellation cannot expose half a transition.

## 4. Platform modules

Composition registration is grouped into authentication, public API, Agent runtime, and observability extensions. Domain clients remain separate, while a shared internal transport applies internal token, identity headers, correlation, timeout, cancellation, safe 4xx passthrough, and 5xx normalization.

The transport helper must not know endpoint-specific DTOs or business rules. `BackendClient`, `WorkflowEngineClient`, `WorkflowAdminService`, `OrchestratorRunService`, and `Mem0Client` remain domain-facing facades rather than one generic client.

Chat selection has no fallback branch. Resolution yields an entitled executable Root Orchestrator or one of the stable errors in the specification. Explicit IDs pass the same server-owned catalog policy and never establish tenant/identity. All transports call the same runtime service.

## 5. Workflow modules

```text
engine/       public compiler, registry, node shell, script/tool contracts
artifacts/    Agent Skill and Business Workflow loaders
runtime/
  lifecycle/ claim, lease, cancellation, deadlines
  recovery/  checkpoints, interrupts, resume
  execution/ Agent and Root coordination
  flow/      Business Workflow runtime wrapper
```

The composition root registers system nodes. Runtime modules depend only on public engine contracts; they do not import a concrete kb-query node package or a `compiler._*` symbol. Registry instances are injectable/invocation-local in tests.

Unexpected exceptions are logged once with correlation and sanitized metadata. Public/internal HTTP responses use the stable error contract; governance records contain safe codes, not provider exception strings.

## 6. Frontend modules

Each feature owns its API functions, types, editor state, and presentation. A shared action primitive returns an explicit success, validation failure, conflict, or transport failure; toast behavior consumes that result rather than assuming a resolved Promise means success.

JSON fields are controlled components holding `{text, parsedValue, error}`. Parent forms aggregate validity and disable every server action while invalid. A build/storage schema identifier detects an obsolete browser bundle or local state and produces a controlled reload/clear operation, preventing old tabs from silently calling removed endpoints.

The API client sends `X-Client-Schema-Version`. Platform advertises the minimum supported version and returns a controlled 426 on changed endpoints for an old or missing version. This is a cutover fence, not a permanent multi-version compatibility layer.

## 7. External development-state cleanup

`reset-development-data.*` resolves the fixed `springaitest` Compose project and only named resources. Default cleanup covers appdb/checkpoint data, mem0 development state, RabbitMQ application queues, and `.lite` state. Langfuse evidence, uploaded source files outside these stores, and unrelated volumes require separate explicit flags.

Both shell variants use the same target list and confirmation phrase, stop on the first unexpected failure, report each removed resource, and leave enough information to run seed/start commands. Recursive filesystem targets are resolved and verified under the repository `.lite` directory before removal.

## 8. Observability and readiness

- Correlation ID flows browser → Platform → Backend/Workflow and into run/evidence logs.
- Readiness distinguishes process liveness from an executable Root Orchestrator configuration.
- The Orchestrator readiness probe resolves the binding and verifies published Root/Worker/Verifier/checkpoint prerequisites without causing an LLM call or side effect.
- Migration version/checksum, schema fingerprint, effective feature gates, and runtime readiness are visible to internal diagnostics without exposing secrets.

## 9. Cross references

- [Analysis](00-analysis.md)
- [Delivery plan](01-plan.md)
- [Specification](02-spec.md)
- [Acceptance tests](04-acceptance-tests.md)
- [Deletion and migration ledger](05-deletion-and-migration-ledger.md)
