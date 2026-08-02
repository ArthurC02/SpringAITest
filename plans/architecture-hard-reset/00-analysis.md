# Architecture Hard Reset — Analysis

> Status: approved plan, not implemented. This document records the target architecture and the evidence that motivated it. No production destructive migration exists until the P3 atomic cutover bundle is implemented and reviewed.

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

The repository is still under development. Existing API and development data compatibility are deliberately not constraints. The cutover therefore changes the existing routes and schema in place, resets application data through an explicit migration, and updates every in-repo consumer in one integration tranche. It does not add `/v2`, dual-write, compatibility adapters, or a rollback window.

## 2. Confirmed findings

| Severity | Finding | Consequence | Planned owner |
| --- | --- | --- | --- |
| High | Workflow and Orchestrator editors can turn an ETag conflict into a successful toast. | The UI reports a write that did not happen. | P1 / Frontend |
| High | Compose does not forward every D4–D7/Context gate to Backend. | A partially enabled deployment returns unexpected 404s. | P1 / Infra |
| Medium | Standalone Workflow execution exposes raw exception text through HTTP 500. | Internal paths, URLs, provider details, or data fragments can cross the service boundary. | P1 / Workflow |
| Medium | Orchestrator JSON editors silently retain the last valid value after invalid input. | The screen and the persisted draft can differ. | P1 / Frontend |
| Medium | InMemory root cancellation/deadline can commit a terminal root before child cascade succeeds and can swallow caller cancellation. | Lite behavior diverges from the PostgreSQL transaction model and can hide consistency bugs. | P1 / Backend |
| Medium | `flow_harness` imports concrete kb-query registration and a private compiler helper. | Runtime and engine modules cannot evolve independently. | P4 / Workflow |
| Medium | Agent Skill and Business Workflow share `skill` tables, DTOs, repositories, and dispatch. | Every change preserves a discriminator-based dual concept and increases branch count. | P3 / Cross-service |
| Low | PowerShell mem0 bootstrap and Lite health checks do not consistently fail on native-process failure. | Automation can report a successful partial startup. | P1 / Infra |
| Low | Workflow tests mutate process-global private registries. | Parallel test execution is unsafe. | P5 / Workflow |
| Low | Large composition roots, repositories, runtime managers, editors, and a central frontend type file concentrate unrelated reasons to change. | Review and regression scope grows with every feature. | P5 / All areas |

The repository also lacks a checked-in CI pipeline that proves all four services, Compose configuration, shell syntax, and contract snapshots together.

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
