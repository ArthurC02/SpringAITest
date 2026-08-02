# Architecture Hard Reset — Deletion and Migration Ledger

> Status: planning inventory. Before implementation, line references must be refreshed against the baseline commit. A row is complete only when its replacement and acceptance evidence exist in the same phase.

## 1. Database and migration

| Item | Current authority/consumer | Phase | Disposition and evidence |
| --- | --- | --- | --- |
| `DbBootstrap` monolithic DDL and inline migrations | `backend/src/Backend.Api/Data/DbBootstrap.cs`; Backend startup and PostgreSQL fixtures | P2/P3 | P2 adds and tests `DbMigrationRunner` with fixtures while `DbBootstrap` remains the production authority. P3 ships the production bundle, switches startup/fixtures, and deletes `DbBootstrap` in the same tranche. |
| Production migration manifest | Not present | P2/P3 | P2 contains runner plus fixture-only migrations; P3 adds/registers `0001`–`0003` with the matching consumers so an old binary cannot create the target schema. |
| Migration metadata placement | Not present | P2 | Create `springaitest_meta.schema_migration` and `migration_cleanup_audit`; hard reset never drops this schema and only the runner writes completion rows. |
| `skill` | `DbBootstrap`; Skill repository/controllers; Agent/Orchestrator snapshots | P3 | Destructively drop. Create `agent_skill` and `business_workflow`; schema catalog asserts old table absent. |
| `skill_revision` | Same plus `agent_revision_skill` and `agent_run_skill` FKs | P3 | Drop only with every dependent FK/table in the same reset transaction. Create two revision tables. |
| `skill.kind` and mixed package/definition columns | `DbBootstrap`, `SkillRepository`, DTOs and dispatch | P3 | Delete rather than backfill. Static inventory confirms no discriminator dispatch remains. |
| `agent_revision_skill` | Agent publication/binding repository | P3 | Rebuild as an Agent-Skill-only pin/link with FK to `agent_skill_revision`. |
| `agent_run_skill` | Direct-Agent snapshot and execution | P3 | Replace mixed union with a typed Agent Skill snapshot. Business Workflow run pins use their own relation/artifact. |
| Harness Workflow pins | `agent_revision.runtime_workflow_*`, `agent_run.workflow_*`, `orchestrator_revision.workflow_*` | P3 | Recreate FKs to `workflow_revision(workflow_id, revision)`. Never redirect these fields to `business_workflow_revision`. |
| Operations/eval `skill_name`/`skill_revision` identity | Governance repository and eval contracts | P3 | Replace with explicit Agent Skill or Business Workflow identity; do not create a new generic discriminator union. |
| Package/name rewrite migrations | `SkillPackageMigration`, `DbBootstrap.MigrateSkillPackagesAndNamesAsync`, related tests | P3 | Delete; old packages are not migrated. New package validation applies only to new Agent Skill data. |
| Development seed embedded in bootstrap | Current `DbBootstrap` | P3 | Move to a separate idempotent seed command when production schema authority switches. Schema migration contains no environment-specific principals/artifacts. |
| Legacy checkpoints, conversations, runs, approvals, audit and outbox rows | appdb application schema | P3 | Count in temporary inventory, then delete with the schema. No payload archive/quarantine. |
| Unknown objects in `public` | Possible operator/custom state | P2 | Do not delete. Compare against the exact SpringAITest-owned object allowlist and abort on extras. |

## 2. Backend API and domain

| Item | Current location | Phase | Disposition |
| --- | --- | --- | --- |
| Shared `ISkillRepository` / `SkillRepository` / `InMemorySkillRepository` | `backend/src/Backend.Api/Skills/` and `Data/InMemory/` | P3 | Replace with independent Agent Skill and Business Workflow repositories/fakes. |
| `/api/skills` flow CRUD/list/get/export | `Skills/SkillController.cs` | P3 | Delete flow behavior. Skill surface becomes Agent-Skill-only. |
| Business Workflow actions backed by shared Skill repository | `BusinessWorkflows/BusinessWorkflowController.cs` | P3 | Rewire to the Business Workflow aggregate and revision table. |
| `/api/skills/validate` proxy/handler | Backend and Platform Skill controllers | P3 | Delete; canonical route is `/api/business-workflows/validate`. No 410 adapter. |
| Agent Skill package validation client | `ISkillPackageValidator`, `WorkflowSkillPackageValidator`, import/restore callers | P3 | Retarget to Workflow `/agent-skills/validate-package`; package parsing remains Workflow authority. |
| Unified artifact execution route | Backend execution-artifact and package readers | P3 | Split Agent Skill and Business Workflow routes and DTOs. |
| Child/snapshot mixed `Skill` rows | AgentRun/OrchestratorRun repositories and snapshot builders | P3 | Replace with explicit typed collections and queries. |
| Mixed Skill pins in InMemory consumers | `InMemoryAgentRepository`, `InMemoryAgentRunRepository`, `InMemoryOrchestratorRunRepository` | P3 | Replace with explicit Agent Skill/Business Workflow models and keep behavior aligned with PostgreSQL. |
| Oversized repositories | AgentRun, InMemoryAgentRun, OrchestratorRun, governance repositories | P5 | Split query/command/coordinator after P3 schema stabilizes; preserve transaction boundaries. |

## 3. Workflow

| Item | Current location | Phase | Disposition |
| --- | --- | --- | --- |
| `/skills/validate` compatibility alias | `workflow/app/main.py` | P3 | Delete route and alias tests. Keep only `/business-workflows/validate`. |
| `/skills/validate-package` | `workflow/app/main.py`, `engine/package.py`, package tests | P3 | Rename to `/agent-skills/validate-package`; preserve validation/security behavior. |
| `/skills/{name}/invoke` kind dispatch | `workflow/app/main.py`, `app/skills/custom.py` | P3 | Replace with separate Agent Skill and Business Workflow invoke routes/loaders. |
| Unified Backend artifact readers | `runtime/artifacts.py`, `skills/custom.py`, `skills/package_reader.py`, `engine/agent_skill_graph.py` | P3 | Split by endpoint and artifact type; no reader calls `/api/skills*` for Business Workflow data. |
| Eval unified Skill invoke assumptions | `workflow/app/evals/api.py`, `fixtures.py`, `models.py`, `runner.py` | P3 | Make candidate type explicit and dispatch to the matching target route/model. |
| Definition/package kind sniffing | Skill catalog/load/compile path | P3 | Delete; endpoint and model type are authoritative. |
| Runtime import of kb-query registration | `workflow/app/runtime/flow_harness.py` | P4 | Move registration to composition root; depend on public registry API. |
| Runtime use of `compiler._script_contract` | `flow_harness.py` | P4 | Publish a supported engine contract resolver, then delete the private dependency. |
| Process-global private test registries | `workflow/tests/conftest.py` and engine tests | P5 | Replace with invocation-local fixtures; prove permitted parallel execution. |
| Legacy chat-related execution/fallback helpers | Runtime/chat integration consumers | P4 | Delete after every chat transport uses Root Orchestrator and failure tests prove no fallback. |

## 4. Platform and public API

| Item | Current location | Phase | Disposition |
| --- | --- | --- | --- |
| Skill service/client mixed DTO and invoke | `Platform.Service/SkillService.cs`, `WorkflowEngineClient.cs` | P3 | Agent-Skill-only service plus explicit Business Workflow methods. |
| Missing Business Workflow revision/invoke proxy | Platform `BusinessWorkflowController`, service, `IWorkflowEngineClient`, DTO/tests | P3 | Add list/restore/invoke using Business Workflow contracts; remove union `ValidateSkillAsync`/`InvokeSkillAsync` semantics. |
| Platform `/api/skills/validate` | `Platform.Web/Controllers/SkillController.cs` | P3 | Delete endpoint and tests. |
| `AGENT_CHAT_ENABLED` | Platform/Backend configuration, Compose/scripts/docs | P4 | Delete compatibility canary flag. |
| `AGENT_CHAT_TENANT_ALLOWLIST` | Platform configuration and routing | P4 | Delete; no tenant fallback/canary path remains. |
| `AgentChatRoutingAgent` legacy resolution | `Platform.Service/AgentChatRoutingAgent.cs` | P4 | Replace with required Root Orchestrator resolution; unavailable is controlled failure. |
| Skill-routing compile-time API consumer | `Platform.Service/SkillRoutingAgent.cs` | P3/P4 | In P3 retarget the still-live legacy path to Agent-Skill-only invoke so the hard cutover compiles; delete the routing brain in P4. |
| Legacy `ChatAssistant`/Skill-routing brain | Platform chat composition and tests | P4 | Delete after Chat and AG-UI share Root runtime. Keep transport/session adapters only where still used. |
| Anonymous chat/history contract | Chat controller, DTO, tests | P4 | Require JWT; remove body `userId` and anonymous continuity/history behavior. |
| Giant composition root | `Platform.Web/Program.cs` | P5 | Split registration by responsibility without moving domain rules into extensions. |
| Partial public error envelope | Platform `ApiErrorWriter`, controllers, API clients/tests | P1 | Add stable code/correlation ID everywhere and migrate all endpoint contract tests before later phases add domain-specific codes. |

## 5. Frontend

| Item | Current location | Phase | Disposition |
| --- | --- | --- | --- |
| `SkillKind = flow | agentic` and shared artifact revisions | `frontend/src/types.ts` | P3 | Replace with separate Agent Skill and Business Workflow types. |
| Unified `invokeSkill`/Skill API filtering | `frontend/src/api/skills.ts` and Skill views | P3 | Split API clients; Business Workflow never calls Skill endpoints. |
| Shared revision/invoke hooks | `SkillHistory.tsx`, `SkillRunPanel.tsx`, `useSkillSelection.ts`, `useSkillRows.ts` | P3 | Split or parameterize with explicit domain contracts; no runtime `kind` branch. |
| Compatibility Skill/Business Workflow UI branching | SkillHome, AgentSkillHome, BusinessWorkflowHome, run panels | P3/P5 | Delete kind-routing state; retain only genuinely shared visual components. |
| Chat `userId` and anonymous UUID/localStorage | chat API/hooks/session state | P4 | Delete; JWT identity and server-derived session keys are authoritative. |
| Legacy chat/canary UI flags | root navigation/runtime flags | P4 | Delete with Platform flags. |
| Stale browser local state | named session/chat/draft keys | P4 | Clear through an explicit storage schema version; do not scan/delete unrelated keys. |
| Central `types.ts` and oversized editors | frontend feature code | P5 | Move domain types/API/state beside each feature; preserve shared primitives only. |

## 6. Infrastructure and data cleanup

| Item | Current location/state | Phase | Disposition |
| --- | --- | --- | --- |
| Missing Backend gate propagation | `infra/docker-compose.yml` | P1 | Add every retained enforcing flag; remove deleted chat flags in P4. |
| PowerShell native failures ignored | `scripts/ensure-mem0-db.ps1` and callers | P1 | Check exit codes and fail with context. |
| Lite health failure returns success | `scripts/start-lite.*` | P1 | Aggregate required health failures and exit nonzero. |
| Mutable deployment image tags | Compose services | P5 | Pin reviewed deployment images by digest; local mem0 image gets an existence check. |
| Normal appdb pollution/container drift by evidence scripts | D3/D5/D6 and all other verifier service chains | P2/P5 | Use a generated allowlisted per-run DB actually wired into every evidence service; restore prior container state. D7 is the reference pattern. |
| mem0 development data | mem0 history/vector storage | P2 | Delete only through confirmed `reset-development-data.*`. |
| RabbitMQ development jobs | application queues | P2 | Purge named queues after writers stop; do not delete unrelated vhosts/volumes. |
| Lite local state | repository `.lite` directory | P2 | Resolve and verify the exact repository child path before removal. |
| Langfuse traces/evidence | Langfuse storage and `artifacts/` | — | Preserve by default; require a separate named option to remove. |

## 7. Tests and documents

| Item | Phase | Disposition |
| --- | --- | --- |
| Dual-track equivalence and 410 compatibility tests | P3 | Delete; the relationship no longer exists. |
| Alias-preservation tests | P3 | Delete and replace with route-absence assertions. |
| Anonymous chat and legacy fallback tests | P4 | Delete; replace with 401/unavailable/no-downstream tests. |
| Skill package migration tests for old rows | P3 | Delete; destructive reset does not rewrite old packages. |
| InMemory/PostgreSQL behavior divergence tests | P1/P5 | Replace with one shared contract suite, not implementation-specific expectations. |
| Root/area AGENTS, API contracts, README, Compose comments | Each phase | Update in the same change as behavior; historical plans retain a superseded header. |

## 8. Items explicitly retained

- D4 Harness `workflow` / `workflow_revision`, Graph IR validation, and their schema identity after reset; they must never be renamed to or replaced by `business_workflow`.
- Harness pins from Agent revisions, Agent runs, and Orchestrator revisions to `workflow_revision`; hard reset deletes their data but recreates the same Graph-IR authority and FK identity.
- `/api/chat`, `/api/chat/stream`, `/api/chat/history`, and `/api/copilot/agui` route names and their deliberate SSE formatting difference.
- `MULTI_AGENT_DISPATCH_ENABLED` and other security/operational feature gates.
- Immutable revision hashes, tenant scoping, ETags, idempotency keys, leases, approvals, once-only effects, outbox, checkpoint signing, and redaction.
- Business Workflow compiler and Node Shell; only legacy naming and private coupling are removed.

## 9. Completion query

Before closing P3/P4, run a literal and semantic inventory across production code, tests, scripts, Compose, README/AGENTS/contracts, and generated route/schema snapshots. Historical plan text may retain old names only when its header links here and labels the content historical.
