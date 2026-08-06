# Architecture Hard Reset — Analysis

> Status: historical target-analysis, reconciled 2026-08-06. This document records the target architecture and the evidence that motivated it. The executable P3 authority is now [08-p3-reconciliation-44f9de4.md](08-p3-reconciliation-44f9de4.md); no production destructive migration exists until its post-P5/C8 readiness and applicable route gates pass.

> Reconciliation note: this dossier was written for an earlier hard-cutover assumption. Current root and area contracts retain `/api/skills*` Business Workflow compatibility, the unified internal `/skills/{name}/invoke`, and `/skills/validate` as a same-handler compatibility alias. Cleanup C8 may narrow public `/api/skills*` to Agent-Skill-only by removing only Business Workflow compatibility operations; Agent Skill endpoints remain. Alias and unified-invoke retirement each require their own later consumer/usage/rollback approval. Physical artifact separation remains a possible post-P5/C8 target and may preserve compatibility facades.

## 1. Decision summary

SpringAITest keeps its four deployable applications:

```text
Browser -> Platform -> Backend
                    -> Workflow -> Backend
```

- Platform owns the authenticated public HTTP/SSE boundary and transport orchestration.
- Backend owns durable data, tenant policy, business rules, revisions, runs, approvals, and idempotency.
- Workflow owns compilation and governed execution, but not durable business authority.
- Frontend remains a thin administrative and chat client.

The repository is still under development, but current cross-service contracts deliberately retain specified compatibility routes and a rollback window. Cleanup C8 can authorize only removal of Business Workflow compatibility operations from `/api/skills*` after flow-write usage `= 0`, consumer cutover, an observation/rollback window, and manual approval; Agent Skill `/api/skills*` endpoints remain. Alias and unified-invoke retirement remain separate future decisions; a physical split can preserve them behind facades.

## 2. Confirmed findings

| Severity | Finding | Consequence | Planned owner |
| --- | --- | --- | --- |
| High | Workflow and Orchestrator editors can turn an ETag conflict into a successful toast. | The UI reports a write that did not happen. | P1 / Frontend |
| High | Compose does not forward every D4–D7/Context gate to Backend: `WORKFLOW_DESIGNER_ENABLED`, `MULTI_AGENT_DISPATCH_ENABLED`, `AGENT_CHAT_ENABLED`, `AGENT_WRITE_TOOLS_ENABLED`, and `CONTEXT_ENRICHMENT_ENABLED` are all read by Backend but absent from its Compose env block; Workflow's Compose env block is also missing `CONTEXT_ENRICHMENT_ENABLED`. | A partially enabled deployment returns unexpected 404s. | P1 / Infra |
| Medium | Standalone Workflow execution exposes raw exception text through HTTP 500. | Internal paths, URLs, provider details, or data fragments can cross the service boundary. | P1 / Workflow |
| Medium | Orchestrator JSON editors silently retain the last valid value after invalid input. | The screen and the persisted draft can differ. | P1 / Frontend |
| Medium | InMemory `CancelAsync` commits a terminal root before the child cascade succeeds. The deadline path (`ExpireLockedAsync`) already matches the PostgreSQL all-or-nothing contract and propagates real caller cancellation correctly; only the caller-initiated cancel path diverges. | Lite behavior diverges from the PostgreSQL transaction model and can hide consistency bugs. | P1 / Backend |
| Medium | `flow_harness` imports concrete kb-query registration and a private compiler helper. | Runtime and engine modules cannot evolve independently. | P4 / Workflow |
| Medium | Agent Skill and Business Workflow share `skill` tables, DTOs, repositories, and dispatch. | Every change preserves a discriminator-based dual concept and increases branch count. | P3 / Cross-service |
| Low | Lite startup reports success even when a service never passes its health check: the timeout path only warns and the script still exits zero. Postgres readiness wait already fails fast, but `ensure-mem0-db.ps1` CREATE DATABASE step (L31) does not check exit code before reporting success; the `.sh` version is protected by `set -e` fail-fast. | Automation can report a successful partial startup. | P1 / Infra |
| Low | Workflow tests mutate process-global private registries. | Parallel test execution is unsafe. | P5 / Workflow |
| Low | Large composition roots, repositories, runtime managers, editors, and a central frontend type file concentrate unrelated reasons to change. | Review and regression scope grows with every feature. | P5 / All areas |

The repository also lacks a checked-in CI pipeline that proves all four services, Compose configuration, shell syntax, and contract snapshots together.

Verified absent: there is no `.github/` directory and no Azure Pipelines, Jenkins, or GitLab CI
definition. The building blocks already exist and only need wiring: `dotnet build` plus
`dotnet test` for Backend and Platform, `uv run pytest` for Workflow, `npm run lint`/`build`/
`test:unit` for Frontend, the `scripts/start-*.{ps1,sh}` compose entry points, and the six
`scripts/verify-*.ps1` cross-service chains. Two of the four claims in this sentence have no
existing basis at all and must be built from scratch in P1: shell syntax checking (nothing but
`.gitattributes` LF pinning exists today) and contract snapshot comparison.

## 3. Target concepts

| Concept | Durable authority | Runtime | API namespace |
| --- | --- | --- | --- |
| Agent Skill | `agent_skill` / `agent_skill_revision` | Agent Skill runner and progressive context disclosure | `/api/skills*` public; `/api/agent-skills*` internal |
| Business Workflow | `business_workflow` / `business_workflow_revision` | Business Workflow compiler and flow runtime wrapper | `/api/business-workflows*` |
| Harness Workflow | Existing `workflow` / `workflow_revision` | Fixed Agent/Root Harness | `/api/admin/workflows*` |

No content sniffing or shared `kind` discriminator determines which concept an artifact represents.

## 4. Deliberate destructive reset

The target migration does not preserve existing application data. It inventories row counts, then removes the application schema and rebuilds it. Invalid, orphaned, and unclassifiable rows are deleted with everything else; no quarantine or transform path exists.

This is safe only under all of these assumptions:

- the environment has not reached production;
- the operator explicitly authorizes the reset for an exact database target;
- the SQL runs transactionally under an advisory lock;
- fresh and reset databases converge to the same schema fingerprint;
- development seed and external-state cleanup are separate, explicit commands.

The migration audit stores counts and identifiers only, never row contents, prompts, tokens, packages, or secrets.

## 5. Rejected alternatives

- **Merge or re-split services:** rejected because the current authority boundaries are defensible; most complexity is internal coupling, not process count.
- **Versioned v2 coexistence:** rejected because all consumers are in this repository and there is no released compatibility obligation.
- **Keep the mixed artifact tables:** rejected because the data can be reset and the discriminator is the root of repeated branching.
- **Transform or quarantine old data:** rejected by product decision; the migration records deletion counts and recreates seeds.
- **Keep legacy chat fallback:** rejected. Authenticated Root Orchestrator execution becomes the only chat runtime; unconfigured callers fail closed.
- **Add frameworks for migration or frontend state:** rejected. Dapper/Npgsql, SQL, React state, and existing test tools are sufficient.

## 6. Related documents

- [Plan](01-plan.md)
- [Target specification](02-spec.md)
- [Design](03-design.md)
- [Acceptance tests](04-acceptance-tests.md)
- [Deletion and migration ledger](05-deletion-and-migration-ledger.md)
- [Todo list](06-todo.md)
