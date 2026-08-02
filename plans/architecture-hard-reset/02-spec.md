# Architecture Hard Reset — Target Specification

> This is the target contract after P3/P4. Until those gates pass, current source and tests remain authoritative.

## 1. Service authority

| Service | Owns | Must not own |
| --- | --- | --- |
| Platform | Public authentication/authorization boundary, HTTP/SSE/AG-UI transport, request correlation, safe error projection | Durable business state, rule interpretation, artifact validation |
| Backend | Tenant-scoped durable records, revisions, policies, commands, leases, approvals, idempotency, outbox | LLM execution, graph compilation, browser session state |
| Workflow | Business Workflow compilation, Agent/Harness execution, checkpoint protocol, tool/script governance | Tenant ownership, mutable business records, public browser API |
| Frontend | User interaction, local form state, authenticated API consumption | Authority decisions or inferred server state |

## 2. Artifact model

### 2.1 Agent Skill

An Agent Skill is a `SKILL.md` package with optional `references/`, `assets/`, and governed scripts. It is never YAML flow content.

```text
agent_skill
  id uuid PK
  tenant_id text
  name text
  description text
  required_role text
  enabled boolean
  current_revision integer
  created_by text
  created_at / updated_at timestamptz

agent_skill_revision
  id uuid PK
  agent_skill_id uuid FK -> agent_skill ON DELETE CASCADE
  revision integer
  package bytea
  package_sha256 text
  instruction_sha256 text
  created_by text
  created_at timestamptz
```

Required invariants: tenant/name uniqueness, positive revisions, one revision number per artifact, canonical name validation, allowed role values, immutable revision payload/hash, and current revision ownership.

### 2.2 Business Workflow

A Business Workflow is declarative YAML compiled by the Workflow engine. It is not an Agent Skill or Harness Workflow.

```text
business_workflow
  id uuid PK
  tenant_id text
  name text
  description text
  required_role text
  enabled boolean
  current_revision integer
  created_by text
  created_at / updated_at timestamptz

business_workflow_revision
  id uuid PK
  business_workflow_id uuid FK -> business_workflow ON DELETE CASCADE
  revision integer
  definition text
  definition_sha256 text
  created_by text
  created_at timestamptz
```

Required invariants mirror Agent Skill revision ownership but contain no package or `kind` column.

### 2.3 Harness Workflow

Existing `workflow` and `workflow_revision` remain the D4 Graph IR authority for `agent-runtime` and `orchestrator` Harness declarations. They do not reference either artifact table merely to reuse a schema.

Agent bindings reference `agent_skill`; Business Workflow pins and flow-run artifacts reference `business_workflow`. Any snapshot that may contain both uses distinct `agentSkills` and `businessWorkflows` collections rather than a kind-tagged union.

## 3. HTTP contracts

### 3.1 Platform public API

```text
/api/skills*                         Agent Skill CRUD/import/export/revisions/invoke
/api/business-workflows*             Business Workflow CRUD/export/revisions/validate/invoke
/api/admin/workflows*                Harness Workflow administration
/api/chat                            authenticated blocking chat
/api/chat/stream                     authenticated SSE chat
/api/chat/history                    authenticated caller history
/api/copilot/agui                    authenticated AG-UI transport
```

`/api/skills/validate` is removed. Business Workflow validation is only `/api/business-workflows/validate`. `/api/skills/{name}/invoke` never accepts or resolves a Business Workflow.

### 3.2 Backend internal API

```text
/api/agent-skills*
/api/agent-skills/{name}/revisions/{revision}/execution-artifact
/api/business-workflows*
/api/business-workflows/{name}/revisions/{revision}/execution-artifact
```

All routes except health require `X-Internal-Token`. Tenant-scoped handlers still require `X-Tenant-Id`; identity-requiring operations explicitly require user/role headers. No route infers artifact type from package presence or content.

### 3.3 Workflow internal API

```text
/agent-skills/validate-package
/agent-skills/{name}/invoke
/business-workflows/validate
/business-workflows/{name}/invoke
```

`/agent-skills/validate-package` remains the Workflow-owned package parser/validator used by Backend import and restore. All routes require the internal token and their documented identity context. Validation failures use controlled field errors. Runtime failures never expose raw exception text.

## 4. Chat and identity

- Every chat/history/AG-UI endpoint requires a valid JWT with nonblank tenant and user claims.
- Chat request bodies contain `message`, required UUID `turnId`, optional UUID `conversationId`, and optional explicit UUID `orchestratorId`; `userId` is removed. Missing/blank `conversationId` creates a server UUID returned as `X-Conversation-Id` on blocking and streaming responses.
- Session keys are the SHA-256 of canonical length-prefixed tenant, user, orchestrator, and conversation fields, not delimiter concatenation.
- The resolved Orchestrator, Root Workflow revision, Worker pool, independent Verifier, policy, and budgets must all be published and tenant-compatible.
- An explicit `orchestratorId` must resolve through the caller's server-evaluated tenant/audience/capability catalog. Cross-tenant, nonexistent, and unauthorized IDs all return the same 404; wire IDs never establish ownership. Administration/test execution uses separate ADMIN endpoints.
- The first accepted turn binds a conversation to one Orchestrator revision lineage. A later attempt to switch it returns `409 conversation_runtime_conflict`.
- Missing or disabled configuration returns a stable fail-closed error. There is no shared-core/Skill-routing fallback and no anonymous short-term memory namespace.
- Chat and AG-UI are two transports over the same durable Root Orchestrator runtime. SSE formatting remains transport-specific.
- Durable command idempotency derives from authenticated identity, conversation ID, and `turnId` (AG-UI uses its canonical run/message ID). A client disconnect after durable allocation does not cancel execution; only an explicit authenticated cancel command does.
- History is caller-scoped and cursor-paginated: `limit` defaults to 50 and is capped at 100; pages use an opaque stable cursor and return items in chronological order. Anonymous history no longer exists.

`MULTI_AGENT_DISPATCH_ENABLED` remains the runtime kill switch. `AGENT_CHAT_ENABLED` and `AGENT_CHAT_TENANT_ALLOWLIST` are removed.

## 5. Error contract

All Platform public errors use camelCase:

```json
{
  "timestamp": "RFC3339 UTC",
  "status": 409,
  "code": "version_conflict",
  "message": "Safe human-readable message",
  "correlationId": "opaque request identifier",
  "fieldErrors": {}
}
```

- `code` is stable and machine-readable; `message` may be localized.
- 409 includes the latest ETag in the response header when the current resource is visible to the caller.
- Unexpected 500 responses use a fixed message and correlation ID only.
- Internal logs hold exception details under existing redaction rules.
- Validation and budget errors expose only enumerated safe codes/messages.

Chat/runtime minimum codes and statuses are fixed:

| Status | Code | Meaning |
| --- | --- | --- |
| 401 | `authentication_required` | JWT is absent or invalid |
| 404 | `orchestrator_not_found` | Explicit ID is absent, cross-tenant, or not entitled |
| 409 | `runtime_not_configured` | No active binding or required published pin exists |
| 409 | `conversation_runtime_conflict` | Conversation is already bound to a different runtime |
| 409 | `version_conflict` | Draft/resource ETag is stale |
| 503 | `runtime_disabled` | Multi-agent dispatch kill switch is off |
| 500 | `workflow_execution_failed` | Unexpected governed execution failure |
| 426 | `client_upgrade_required` | Browser bundle predates the hard-cutover contract |

P1 migrates the shared Platform error writer and all public endpoint contract tests to the complete error shape; later phases add their domain codes without changing the envelope.

After the P3/P4 hard cutover, Frontend sends `X-Client-Schema-Version` on API requests. Platform publishes the minimum supported value through its runtime/bootstrap response and returns `426 client_upgrade_required` for missing or older versions on changed chat and administration endpoints. The client clears only named obsolete storage keys and performs a hard reload.

## 6. Migration contract

Migration metadata lives in the dedicated `springaitest_meta` schema, which the reset never drops. `schema_migration` contains `version`, `name`, `checksum`, `applied_at`, and `duration_ms`; `migration_cleanup_audit` lives beside it. The runner alone writes completion rows. Applied checksums are immutable. One PostgreSQL advisory transaction lock serializes migration runners. The initial `0001`–`0003` hard-reset files are one atomic bundle: all schema changes, postconditions, cleanup audit, and ledger rows commit or roll back together. Later ordinary migrations run one file per transaction.

Database classification occurs under the migration lock before any mutation: an empty database may initialize automatically; a complete allowlisted legacy SpringAITest schema requires the dedicated command and exact confirmation; a known-current target requires a valid contiguous migration ledger plus target-schema postconditions and becomes a no-op when nothing is pending; an unknown nonempty schema is always rejected. A ledger/current-schema mismatch fails closed and is never repaired automatically. Backend startup never performs the destructive branch and ignores `ALLOW_DESTRUCTIVE_MIGRATION`. That flag, if retained as script plumbing, is accepted only inside the dedicated migration process together with the command-line confirmation.

`migration_cleanup_audit` stores an execution UUID, migration version, table name, deleted row count, and reset timestamp. It stores no row payload.

## 7. Seed and external state

- Schema migrations contain no development users, tenants, prompts, Agents, Orchestrators, or Workflows.
- The P3 development seed command is idempotent and creates the complete Root Workflow, Worker, independent Verifier, and runtime binding needed for P4; legacy chat may ignore those records until the cutover.
- The reset command targets only the exact `springaitest` development project and explicitly named appdb/checkpoint, mem0, RabbitMQ, and Lite state.
- Langfuse traces and uploaded source files are excluded unless a separate explicit option names them.
- Frontend clears named obsolete keys using a storage schema version; it never deletes arbitrary origin storage.

## 8. Feature flags retained

Security/operational gates such as Agent Builder, Agent test run, Workflow Designer, multi-agent dispatch, context enrichment, approved write tools, eval, prompt artifacts, and isolated scripts remain fail closed. Compatibility-only chat canary flags are deleted. Chat requires `MULTI_AGENT_DISPATCH_ENABLED` to be effective in Platform, Backend, and Workflow. `WORKFLOW_DESIGNER_ENABLED` remains an administration gate and is explicitly removed from runtime readiness dependencies. Context enrichment is optional but cannot become effective without dispatch; D7 is additionally required only when the pinned runtime grants a write tool. Compose must pass every retained flag to every service that enforces it.
