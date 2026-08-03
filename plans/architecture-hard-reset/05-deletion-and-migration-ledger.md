# Architecture Hard Reset — Deletion and Migration Ledger

> Status: planning inventory. Before implementation, line references must be refreshed against the baseline commit. A row is complete only when its replacement and acceptance evidence exist in the same phase. Line references refreshed against baseline commit `8652528` (2026-08-03).

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
| `operations_execution_metric.skill_name`/`.skill_revision` 與 `operations_run_evidence.skill_name`/`.skill_revision` | `DbBootstrap.cs:509,515,531`; `OperationsGovernanceRepository`/`InMemoryOperationsGovernanceRepository` | P3 | Split into typed Agent Skill / Business Workflow identity columns. Row 18 named only the governance repository, not these two tables. |
| `eval_run.candidate_kind`/`candidate_ref`/`candidate_pins`/`candidate_identity_sha256` | `DbBootstrap.cs:889-890`; `EvalController`/`EvalRepository`/`InMemoryEvalRepository`/`EvalDtos.cs:99-124` | P3 | Replace the `CHECK (candidate_kind IN ('skill','agent'))` discriminator union with an explicit candidate type plus typed ref. Mirrors the Workflow-side item in §3 which had no Backend counterpart. |
| `skill.simple_form` | `DbBootstrap`; `SkillRepository`/`InMemorySkillRepository`; `BusinessWorkflowController` | P3 | Target schema (02-spec §2.1/2.2) does not define this column. Decide its destination (move into `business_workflow`, into both, or drop) and record it in the spec. Decision must also cover the `simpleForm` wire DTO fields in `Platform.Service/Dtos/SkillDtos.cs:23,48,62`; dropping the column without removing DTO fields leaves orphan fields. |
| Exact SpringAITest-owned object allowlist | Not written down anywhere in this plan set | P2 | 03-design §1.3 step 2 and row 22 both depend on an allowlist that was never enumerated. See §1.1 below for the 50-table baseline. |

### 1.1 Object allowlist baseline (50 tables)

Enumerated from `DbBootstrap.cs` at the audit commit. This is the input for the `0001` classification logic and the extras-abort comparison; refresh against the baseline commit before implementation. Note: `skill.simple_form` is an ALTER TABLE post-column added at `DbBootstrap.cs:144`, not present in the CREATE TABLE body, and easily overlooked during allowlist comparison.

`tenants`, `users`, `user_group_membership`, `conversations`, `rag_documents`, `rag_chunks`, `app_config`, `skill`, `skill_revision`, `configuration_set`, `agent`, `agent_revision`, `agent_revision_skill`, `workflow`, `workflow_revision`, `orchestrator`, `orchestrator_revision`, `agent_run`, `agent_run_approval`, `agent_run_approval_decision`, `agent_run_write_effect`, `agent_run_approval_execute`, `agent_run_write_outbox`, `orchestrator_run`, `tenant_runtime_binding`, `operations_regression_result`, `operations_regression_override`, `operations_release_audit`, `operations_execution_metric`, `operations_run_evidence`, `orchestrator_run_event`, `orchestrator_run_command`, `orchestrator_run_child`, `agent_run_skill`, `agent_run_event`, `agent_run_command`, `context_policy`, `source_catalog`, `metric_definition`, `context_revision`, `context_evidence`, `context_view`, `context_request`, `context_delta`, `eval_suite`, `eval_suite_revision`, `eval_run`, `eval_case_result`, `prompt_component_revision`, `prompt_manifest_revision`

Row 21 covers checkpoints/conversations/runs/approvals/audit/outbox generically, but `rag_documents`, `rag_chunks`, `app_config`, `configuration_set`, `agent`, `agent_revision`, `orchestrator`, `orchestrator_revision`, and `tenant_runtime_binding` hold author-created content rather than run/audit residue. The reset destroys them too; that is intended, but it must be stated rather than implied.

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
| `SkillHash.cs` | `Skills/SkillHash.cs`; called by 24 files across 10 modules (Agents, AgentRuns, Contexts, OrchestratorRuns, Orchestrators, Workflows, PromptArtifacts, Eval (`EvalRepository.cs`), RAG (InMemory), Data/DbBootstrap) | P3 | Move to a shared namespace. It is a generic SHA-256 helper that only happens to live under `Skills/`. |
| `SkillNameRules.cs` | `Skills/SkillNameRules.cs`; used by `SkillController` (deleted), `BusinessWorkflowController` (kept), and `Data/DbBootstrap.cs:1069` (in `MigrateSkillRowAsync`); if DbBootstrap is deleted in the same tranche, no issue, but namespace moves must be concurrent. | P3 | Move to `BusinessWorkflows/` or a shared location; `ReservedBusinessWorkflowNames` becomes single-domain. |
| `SkillExporter.cs` | `Skills/SkillExporter.cs`; used by both `SkillController` (deleted) and `BusinessWorkflowController.cs:37` (kept) | P3 | Move to `BusinessWorkflows/`. Agent Skill export uses the stored package, not on-the-fly zip assembly. |
| `ISkillValidator.cs` (with `SkillMetadata`/`SkillValidationResult`/`SkillValidationError`) | `Skills/ISkillValidator.cs`; implemented by `BusinessWorkflows/WorkflowSkillValidator.cs` | P3 | Move to `BusinessWorkflows/`, rename off the `Skill` vocabulary, and drop `SkillMetadata.Kind`. Conflict: `SkillMetadata` is also used on the retained side by Agent Skill package validation (`Skills/ISkillPackageValidator.cs:12` field `SkillMetadata? Skill` and `WorkflowSkillPackageValidator.cs:63`). "Move to BusinessWorkflows/" will pull Agent Skill validation in the opposite direction — mark as P3 pre-spec decision pending. |
| `InMemoryOperationsGovernanceRepository` / `InMemoryEvalRepository` Skill-shaped fields | `OperationsGovernance/InMemoryOperationsGovernanceRepository.cs:123-125`, `InMemoryEvalRepository.cs:94,177-178` | P3 | Fold into row 35's InMemory parity replacement; they mirror the two §1 schema rows above. |

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
| `GET /skills` unified catalog (mixed Agent Skill / Business Workflow, mixed builtin/custom) | `workflow/app/main.py` (`list_skills`) | P3 | 02-spec §3.3 lists only four target routes and never says whether catalog splits or stays merged. Decide and record before implementation; see §4 for the three frontend call sites that break if it narrows. |
| The 12 builtin YAML artifacts | `workflow/app/skills/*.yaml`, `workflow/app/skills/__init__.py` | P3 | None declares `kind:`, so all 12 default to `flow` — every builtin is a Business Workflow and none is an Agent Skill. Plan the directory/module move (03-design §5 `artifacts/`) accordingly. |
| `compiler._build_graph` kind dispatch | `workflow/app/engine/compiler.py:503-505` | P3 | Split into `compile_business_workflow()` / `compile_agent_skill()`; callers dispatch by artifact type instead of passing a kind-tagged `Skill`. |
| `engine/package.py` top-level parse dispatch | `workflow/app/engine/package.py:962-968` (`_parse_agentic`/`_parse_flow`) | P3 | Already two independent pipelines behind one entry point; split into two public parsers. |
| `skills/custom.py` `load`/`_entry` kind dispatch | `workflow/app/skills/custom.py:79-189` | P3 | `load_business_workflow`/`load_agent_skill` already exist as clean seams; split into two modules and delete the three-way dispatch. |
| **Harness kind routing for Skill pins** | `workflow/app/runtime/graph.py:555-561,705-788,1132-1213`; `workflow/app/runtime/models.py:149-172,229,429-437` | P3 | **Largest omission in this ledger.** D3/D5 Harness routes execution on `pin.kind`/`artifact.kind`/`scope.kind` string comparison (`_load_skill`, `_enter_skill_scope`, `_proposed_action`, route_satisfied). 02-spec §2.3 requires distinct `agentSkills`/`businessWorkflows` collections instead of a kind-tagged union, so `PinnedSkillSummary`, `ActiveSkillScope`, and `DirectAgentExecutionSnapshot.skills` must be split. Decide first whether one execution snapshot may pin both artifact types. |
| `EvalCandidate.kind: Literal["skill","agent"]` | `workflow/app/evals/models.py:33-43`; `evals/api.py:27-80` | P3 | Orthogonal to `Skill.kind`; ambiguous after the split. Replace with explicit types and delete the runtime kind rejection at `api.py:69-79`. |
| Workflow exception exposure and correlation tracking | `app/main.py:168-175` (`_run_with_timeout`), `app/main.py:508-512` (invoke custom.load except), `app/evals/api.py:48-52` | P1-05 | Workflow has no correlation ID mechanism; three raw exception external leak points via `str(e)` expose internal paths, URLs, provider details, or data fragments. P1-05 builds the mechanism; these three points replace `str(e)` with fixed safe messages and log the full exception. |

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
| `ChatOrchestratorController.cs:23` bare `NotFound()` | `platform/src/Platform.Web/Controllers/ChatOrchestratorController.cs:23` | P1 | Seventh public error exit; returns empty 404 body outside ApiError envelope. P1 error-envelope migration must cover it; otherwise contract tests miss this path. |
| D6 canary chat runtime cluster | `Platform.Service/AgentChatRuntime.cs`, `Abstractions/IAgentChatRuntime.cs`, `Options/ServiceOptions.cs` (`AgentChatOptions`), `Platform.Web/Controllers/ChatOrchestratorController.cs` | P4 | Row 62 named only the thin `AgentChatRoutingAgent` shell. `AgentChatRuntime` holds the actual `IsCanaryTenant` / `mode == "legacy"` / return-null-to-fall-back logic; `IAgentChatRuntime` must fail with stable error codes instead of returning null. Decide whether `ChatOrchestratorController` is re-gated or deleted. |
| `ChatAssistant` session store `withIsolation:false` | `Platform.Web/Program.cs:247-287` | P4 | The inline comment documents `false` as deliberate **because anonymous continuity must be preserved**. P4 removes anonymous chat, so the justification expires and this should become `withIsolation:true` (Strict, fail-closed) like AG-UI. Note: `Program.cs:254-255` comments name test cases (e.g., `ChatServiceTests.Chat_ShortTermMemory_CarriesPriorExchange`) that assume anonymous continuity; changing to `withIsolation:true` and deleting the anonymous branch must be coordinated with those tests in the same tranche. |
| `ChatMemoryKeyDerivation` anonymous branch | `Platform.Service/Abstractions/ChatMemoryKeyDerivation.cs` | P4 | Delete the `userCtx is null` branch (`NormalizeUser`/`NormalizeConversation`) once chat requires JWT. |
| `ChatRequest.TurnId` and `X-Conversation-Id` response header | `Platform.Service/Dtos/ChatDtos.cs`, `Platform.Web/Controllers/ChatController.cs` | P4 | 02-spec §4 requires a mandatory UUID `turnId` and a server-generated conversationId returned as `X-Conversation-Id`. Neither exists today; both are additions, not deletions. |
| `WORKFLOW_DESIGNER_ENABLED` x `MULTI_AGENT_DISPATCH_ENABLED` coupling | `Platform.Web/Program.cs:67-68` | P1/P4 | Current code makes dispatch require the designer gate; 02-spec §8 explicitly removes `WORKFLOW_DESIGNER_ENABLED` from runtime readiness dependencies. Direct code/spec conflict. Additional: `contextEnrichmentEnabled` flows through `Program.cs:63→67→71` with three-layer dependency on `WORKFLOW_DESIGNER_ENABLED`; when decoupling dispatch, E1/E3 validity conditions and their interconnection must be revisited, and the refactor scope expands to include context enrichment feature completeness. |
| Shared union `Skill`/`SkillUpsert`/`BusinessWorkflowCreated` DTOs | `Platform.Service/Dtos/SkillDtos.cs` | P3 | One DTO with a `[JsonRequired]` `Kind`, asserted to different subsets by `SkillService.ReadSkillAsync` and `BusinessWorkflowService.EnsureFlowKind`. Split into two record families. |
| `agentChatEnabled` in `GET /api/features` | `Platform.Web/Program.cs:587`; frontend Features consumers | P4 | Wire-contract shape change when the flag is deleted. |

## 5. Frontend

| Item | Current location | Phase | Disposition |
| --- | --- | --- | --- |
| `SkillKind = flow | agentic` and shared artifact revisions (also `SkillSimpleForm`, `SkillInfo`, `Skill`, `SkillRevision`, `SkillInputField`, `SkillCatalogEntry`, `SkillValidationError`, `SkillValidation`, `SkillResult`) | `frontend/src/types.ts` | P3 | Replace with separate Agent Skill and Business Workflow types. |
| Unified `invokeSkill`/Skill API filtering | `frontend/src/api/skills.ts` and Skill views | P3 | Split API clients; Business Workflow never calls Skill endpoints. |
| Shared revision/invoke hooks | `SkillHistory.tsx`, `SkillRunPanel.tsx`, `useSkillSelection.ts`, `useSkillRows.ts` | P3 | Split or parameterize with explicit domain contracts; no runtime `kind` branch. |
| Compatibility Skill/Business Workflow UI branching | SkillHome, AgentSkillHome, BusinessWorkflowHome, run panels | P3/P5 | Delete kind-routing state; retain only genuinely shared visual components. |
| Chat `userId` and anonymous UUID/localStorage | chat API/hooks/session state | P4 | Delete; JWT identity and server-derived session keys are authoritative. |
| Legacy chat/canary UI flags | root navigation/runtime flags | P4 | Delete with Platform flags. |
| `AgentEditor.tsx` 409 false-success toast — **corrected: no bug** | `frontend/src/components/AgentEditor.tsx` | P1 | Implementation verification (2026-08-03) found the recon claim wrong: AgentEditor does not route saves through `runWithToast` (its own try/catch sets `setConflict` and the success toast sits after `await`), so 409 never produced a success toast. The common-layer fix (`runWithToast.onConflict`) covers Workflow/Orchestrator editors; AgentEditor's correct behavior is pinned by a new regression assertion in `agentBuilder.ui.spec.ts`. |
| Stale browser local state | named session/chat/draft keys | P4 | Clear through an explicit storage schema version; do not scan/delete unrelated keys. |
| Central `types.ts` and oversized editors | frontend feature code | P5 | Move domain types/API/state beside each feature; preserve shared primitives only. |
| `SkillHome.tsx` builtin-view catalog call | `frontend/src/components/SkillHome.tsx:2,100` (`openBuiltinView`) | P3 | Calls the Agent Skill catalog unconditionally, including for Business Workflow rows. Breaks if `/api/skills/catalog` narrows. |
| `useSkillSelection.ts` unconditional catalog call | `frontend/src/hooks/useSkillSelection.ts:3,100` (`onHistoryReverted`) | P3 | Same catalog-domain mismatch during revision restore; the `kind` branch above it does not cover this call. |
| `SimpleSkillEditor.tsx` template skeleton source | `frontend/src/components/SimpleSkillEditor.tsx:3,74` (`loadSkeleton`) | P3 | A Business-Workflow-only editor reads `template-*` skeletons from the Agent Skill catalog endpoint. |
| Storage/build schema version mechanism | Verified absent: no `X-Client-Schema-Version` header, no 426 handling, no storage version constant | P4 | Rows in §4/§5 reference this as an existing mechanism. It does not exist — this is a feature to add per 03-design §6 / 02-spec §5, not cleanup. |
| `springai-chat:conversationId` client-generated UUID | `frontend/src/api/chat.ts:28-35` (`getConversationId`) | P4 | Client pre-generates the id; 02-spec §4 makes the server authoritative via `X-Conversation-Id`. Reconcile which side generates on the first turn. |

## 6. Infrastructure and data cleanup

| Item | Current location/state | Phase | Disposition |
| --- | --- | --- | --- |
| Missing Backend gate propagation | `infra/docker-compose.yml` | P1 | Add every retained enforcing flag; remove deleted chat flags in P4. |
| PowerShell native failures ignored | `scripts/ensure-mem0-db.ps1` and callers | P1 | Check exit codes and fail with context. |
| `ensure-mem0-db.ps1` CREATE DATABASE exit code unchecked | `scripts/ensure-mem0-db.ps1:31-32` | P1 | CREATE DATABASE can fail but the script still prints success and exits 0; the `.sh` version is protected by `set -e` fail-fast. Add `$LASTEXITCODE` check. |
| Lite health failure returns success | `scripts/start-lite.*` | P1 | Aggregate required health failures and exit nonzero. |
| Mutable deployment image tags | Compose services | P5 | Pin reviewed deployment images by digest; local mem0 image gets an existence check. |
| Normal appdb pollution/container drift by evidence scripts | D3/D5/D6 and all other verifier service chains | P2/P5 | Use a generated allowlisted per-run DB actually wired into every evidence service; restore prior container state. D7 is the reference pattern. |
| mem0 development data | mem0 history/vector storage | P2 | Delete only through confirmed `reset-development-data.*`. |
| RabbitMQ development jobs | application queues | P2 | Purge named queues after writers stop; do not delete unrelated vhosts/volumes. |
| Lite local state | repository `.lite` directory | P2 | Resolve and verify the exact repository child path before removal. |
| Langfuse traces/evidence | Langfuse storage and `artifacts/` | — | Preserve by default; require a separate named option to remove. |
| Compose profile discovery mismatch | `docker-compose.yml` and `docker-compose.evidence.yml` | P1 | Current `infra/AGENTS.md` gate descriptions (D6/D7 backends/workflows) do not match actual Compose setup: there is a fourth profile layer (`evidence-real` with rabbitmq-evidence/backend-evidence/workflow-evidence services). After P1 composer fixes, docs must sync the gate descriptions to match actual Compose reality. |

## 7. Tests and documents

| Item | Phase | Disposition |
| --- | --- | --- |
| Dual-track equivalence and 410 compatibility tests | P3 | Delete; the relationship no longer exists. |
| Alias-preservation tests | P3 | Delete and replace with route-absence assertions. |
| Anonymous chat and legacy fallback tests | P4 | Delete; replace with 401/unavailable/no-downstream tests. |
| Skill package migration tests for old rows | P3 | Delete; destructive reset does not rewrite old packages. |
| InMemory/PostgreSQL behavior divergence tests | P1/P5 | Replace with one shared contract suite, not implementation-specific expectations. |
| Root/area AGENTS, API contracts, README, Compose comments | Each phase | Update in the same change as behavior; historical plans retain a superseded header. |
| D3 pin/artifact mixed-kind test fixture source | P3 | `workflow/tests/test_agent_runtime.py` (`snapshot()`/`flow_artifact()` helpers) is imported by `test_agent_runtime_api.py`, `test_agent_runtime_manager.py`, `test_d7_write_evidence.py`, `test_prompt_manifest_assembler.py`, and `test_agent_runtime_flow.py` (5 consumers); rewriting the models affects the source and all five consumers (6 files total). |
| `workflow/tests/test_skills_custom.py` (667 lines) | P3 | Mixed flow/agentic custom-artifact coverage in one file; split into two per-type test files. |
| Backend PostgreSQL fixture switch | P3 | `PostgresFixture` (`ConfigurationSetRepositoryTests.cs:16-46`) is shared by 14 test files via `[Collection("Postgres")]` (SkillRepositoryTests, RunEvidenceEnvelopePostgresApiTests, RagRepositoryTests, PromptArtifactsPostgresTests, OrchestratorRunRepositoryTests, OrchestratorRepositoryTests, OperationsGovernancePostgresApiTests, EvalGovernancePostgresApiTests, ContextRepositoryTests, ConfigurationSetRepositoryTests, ConfigRepositoryTests, AuthRepositoryTests, AgentRunRepositoryTests, AgentRepositoryTests), and 5 files call `DbBootstrap.RunAsync` directly at 32 sites (AgentRepositoryTests×3, ConfigRepositoryTests×4, ConfigurationSetRepositoryTests×2, OrchestratorRepositoryTests×9, SkillRepositoryTests×14) plus production Program.cs 1 site. All must move to the runner in the same tranche. |

## 8. Items explicitly retained

- D4 Harness `workflow` / `workflow_revision`, Graph IR validation, and their schema identity after reset; they must never be renamed to or replaced by `business_workflow`.
- Harness pins from Agent revisions, Agent runs, and Orchestrator revisions to `workflow_revision`; hard reset deletes their data but recreates the same Graph-IR authority and FK identity.
- `/api/chat`, `/api/chat/stream`, `/api/chat/history`, and `/api/copilot/agui` route names and their deliberate SSE formatting difference.
- `MULTI_AGENT_DISPATCH_ENABLED` and other security/operational feature gates.
- Immutable revision hashes, tenant scoping, ETags, idempotency keys, leases, approvals, once-only effects, outbox, checkpoint signing, and redaction.
- Business Workflow compiler and Node Shell; only legacy naming and private coupling are removed.

## 9. Completion query

Before closing P3/P4, run a literal and semantic inventory across production code, tests, scripts, Compose, README/AGENTS/contracts, and generated route/schema snapshots. Historical plan text may retain old names only when its header links here and labels the content historical.

## Related documents

- [Analysis](00-analysis.md)
- [Delivery plan](01-plan.md)
- [Target specification](02-spec.md)
- [Design](03-design.md)
- [Acceptance tests](04-acceptance-tests.md)
- [Implementation todo list](06-todo.md)
