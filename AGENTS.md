# Repository Guidelines

Solution-wide guidance only. Each area has its own `AGENTS.md` (+ `CLAUDE.md` importing it) with layout, commands, and area-specific gotchas — **read the area file before working in that area**; Claude Code loads it on demand when files there are touched.

## Monorepo Map

| Area        | What it is                                                                                                                                                                | Details                                  |
| ----------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| `platform/` | ASP.NET Core 10 gateway (`:8080`): JWT verify, SSE chat, Microsoft Agent Framework + AG-UI endpoint, mem0, BackendClient proxy, Skill CRUD/invoke proxy, **Agent CRUD/publish/rules proxy**. xUnit. | [platform/AGENTS.md](platform/AGENTS.md) |
| `backend/`  | ASP.NET Core 10 core service (`:8002`): feature folders, Dapper + appdb (PostgreSQL/pgvector), RabbitMQ document consumer, issues JWTs, Skills CRUD, **Agent CRUD/publish/revisions/rules**. xUnit. | [backend/AGENTS.md](backend/AGENTS.md) |
| `frontend/` | React 19 + Vite + TypeScript SPA (`:5173`): login + core views + flag-gated admin workspaces, CopilotKit sidebar.                                             | [frontend/AGENTS.md](frontend/AGENTS.md) |
| `workflow/` | Python 3.12+ LangGraph + FastAPI (host `:8001`): Skill engine layer, deterministic Business Rules, node/tool registries, retrieval via backend HTTP. pytest. | [workflow/AGENTS.md](workflow/AGENTS.md) |
| `infra/`    | Docker Compose (LiteLLM, Langfuse, postgres, RabbitMQ, mem0, appdb…), `litellm-config.yaml`. `scripts/` holds startup helpers.                                            | [infra/AGENTS.md](infra/AGENTS.md)       |

Start services with `.\scripts\start-infra.ps1` / `./scripts/start-infra.sh` (infra-only, default), `start-full.*` (everything containerized), or `start-lite.*` (no containers, all localhost). Run modes, port conflicts, and container known-issues: [infra/AGENTS.md](infra/AGENTS.md); run-mode matrix: [README.md](README.md).

## Subagent Delegation

**Delegate by default — do not wait to be asked.** Route substantive work to the project subagents in `.claude/agents/` and run independent ones in parallel; the main agent orchestrates (write the spec, delegate, integrate results):

| Agent                  | Use for                                                                             |
| ---------------------- | ----------------------------------------------------------------------------------- |
| `dotnet-implementer`   | Any implementation in `platform/` or `backend/` (+ xUnit tests)                     |
| `frontend-implementer` | Any implementation in `frontend/`                                                   |
| `python-implementer`   | Any implementation in `workflow/` (nodes, engine, skill compiler, sandbox + pytest) |
| `code-reviewer`        | Review after every non-trivial change, before declaring done                        |
| `code-simplifier`      | Simplify/refine recently changed code (clarity, reuse, dead flexibility) — behavior-preserving, after review |
| `e2e-verifier`         | Full-chain verification via docker compose when cross-service behavior changed      |
| `docs-updater`         | Sync README/AGENTS/docs files after feature or architecture changes                 |

Only trivial work (typo-level edits, quick Q&A) stays in the main loop.

Three delegation paths by task shape: trivial → main loop; exploratory/ambiguous spec → per-phase delegation as above (the user steers between phases); **well-specified feature work → the saved `dev-cycle` workflow** in `.claude/workflows/`, which chains the same subagents deterministically (parallel implementers → bounded review/fix loop → simplify → e2e verify when cross-service → docs) so the review gate is enforced by script, not convention. It runs only when the user asks for it (Workflow opt-in rule); do not auto-select it.

**Large fan-outs (guards against past spend-limit kills and blind dispatch):** any fan-out beyond ~12 subagents must (a) go through the Workflow tool — never bare parallel Agent calls — for its concurrency cap, per-agent journal, and hard `budget` ceiling; and (b) be preceded by a dispatch plan the user approves: purpose, agent count, per-stage model/effort, scope boundaries, rough token estimate. Tier models by work class: scanning/mechanical → haiku/sonnet at low effort; review/architecture → opus. `dev-cycle` (fixed shape, 4–8 agents) is exempt from the plan requirement.

**Remediation campaigns are resumable by convention:** audits land findings in `plans/<topic>-<date>.md` as checkboxes (file:line + proposed fix each) before any fix agent runs; fix waves consume unchecked items and tick them as they land (the saved `remediate` workflow automates this: load unchecked → fix in waves of 8 with checkpoint ticks → review). A killed run resumes by re-invoking with the same plan file — unchecked items are exactly the remaining work.

**Every subagent definition must pin all five knobs in its frontmatter:** `model`, `skills` (preloaded Skill names; see below), `tools`, `hooks`, and MCP access (MCP tools are granted as `mcp__<server>__<tool>` entries inside `tools`). Where one is deliberately absent (e.g. no hooks for a docs-only agent, or no skills for code-simplifier), record that as a YAML comment in the frontmatter — omission must be a decision, never an oversight. `skills:` accepts plugin names like `ponytail:ponytail` and project Skill names (e.g., `contract-change`); context-preload is hard guaranteed at agent startup. New agents follow the same rule.

Ad-hoc built-in agents (general-purpose, Explore, Plan, claude) inherit the session model; project subagents pin theirs. When spawning a built-in agent, always pass an explicit `model` (default `sonnet` unless the task warrants more) — the session model must never silently propagate into subagent work.

**Spot-check subagent completion reports, especially three claim types**: (a) lock/concurrency structure ("this ordering is safe"), (b) dead code ("nothing calls this"), (c) artifact/workspace state ("temp files deleted", "workspace clean") — verify with grep/Read yourself, don't take the report at face value (real cases: a claimed-clean workspace still had two temp files on disk; two agents signed off a lock ordering that held an ABBA deadlock caught only by code review). Conversely, a spec's blanket hard rule may not fit the file at hand (an agent rightly refused "never call across repositories while holding a lock" after showing the file's existing methods all did so with no ABBA) — give rules the specific risk they guard against so the agent can judge applicability instead of following blindly.

## Cross-Service Contracts

These facts span two or more areas — changing one side silently breaks the other — so they live here as **invariants only**. Full contracts live in [docs/cross-service-contracts.md](docs/cross-service-contracts.md) — **read the relevant section there before changing any side of these**:

- **Public chat API** (P4-1): every `/api/chat*` route requires JWT; identity, mem0 grouping, and persistence derive from JWT `{tenant}:{user}`. See [docs/cross-service-contracts.md § Public chat API](docs/cross-service-contracts.md#public-chat-api-platform-p4-1-partial).
- **Two SSE formats:** `/api/chat/stream` writes `data:<value>` (no space); `/api/copilot/agui` writes `data: ` (with space). Proxy paths must have `proxy_buffering off`. See [docs/cross-service-contracts.md § Two SSE formats](docs/cross-service-contracts.md#two-sse-formats-both-deliberate).
- **AG-UI copilot:** browser connects directly to platform's `/api/copilot/agui`, requires authentication, fail-closed on missing/blank tenant/user claims; shares skill-routing/mem0/persistence core with `/api/chat*`. See [docs/cross-service-contracts.md § AG-UI copilot](docs/cross-service-contracts.md#ag-ui-copilot).
- **Error shape & correlation:** every API returns ApiError with camelCase `correlationId`; Platform derives ID from `HttpContext.TraceIdentifier` and forwards via `X-Correlation-Id` to backend/workflow. Response field naming is intentionally mixed (snake_case for documents/workflows, camelCase for auth/config). See [docs/cross-service-contracts.md § Error shape & correlation](docs/cross-service-contracts.md#error-shape--correlation).
- **Backend trust boundary:** backend requires `X-Internal-Token` on everything except `/health`; identity headers are per-endpoint (handlers call `RequireTenant()`/`RequireUserId()` where needed); `/api/config` is ADMIN-only and tenant-scoped; both backend and workflow bind `127.0.0.1` only. Full details: [docs/cross-service-contracts.md § Backend trust boundary](docs/cross-service-contracts.md#backend-trust-boundary).
- **Async document processing:** platform publishes to RabbitMQ `documents.process` and returns `202`; backend consumer marks status `ready`/`failed`; document is absent from `GET /api/documents` until ready (eventual consistency); broker unreachable → `502`. Full details: [docs/cross-service-contracts.md § Async document processing](docs/cross-service-contracts.md#async-document-processing).
- **Auth:** backend sole JWT signer (ES256, one P-256 private key by active kid); platform receives public ring only, binds exact `iss`/`aud`/`kid`; key rotation: deploy expanded ring, atomically switch backend's kid/private key, keep old public key >24 hours.
- **Two memory layers:** *short-term* = Microsoft Agent Framework session store (resets on restart); *long-term* = mem0 (grouped by JWT `{tenant}:{user}`, cross-session). Both grouped by `{tenant}:{user}`.
- **Skill concepts and engine boundary (P0–P5):** **Agent Skill** = portable `SKILL.md` package; **Business Workflow** = declarative YAML compiled by Skill Engine; both persist behind `kind` discriminator; **Harness** = fixed `runtime/graph.py` LangGraph. P5 may make `/api/skills*` Agent-Skill-only only after C8 proves Business Workflow cutover and rollback window ends.
- **P3-R2 compatibility evidence:** bounded dimensions only; Platform replaces origin with server-derived value; Workflow sole unified-invoke authority. Details: [P3-R2 runtime evidence contract](plans/architecture-hard-reset/09-p3-r2-runtime-evidence-contract.md).
- **Prompt manifest (P1):** Backend's internal-only `GET /api/prompt-manifests/{revision}/resolved` returns raw pinned composition; pinned runs embed `execution_snapshot.agent.prompt_manifest {revision, sha256}`.
- **Eval framework (E2):** Backend owns durable eval authority; platform proxies under `/api/admin/operations` with D7's `AGENT_WRITE_TOOLS_ENABLED` pre-auth gate; Workflow's `/evals/run` is stateless, gated by `RUN_EVAL_ENABLED`. The operations dashboard's `metrics`/`version-comparison` aggregate a bounded window only (`window_days`, default 90, 1–365, out-of-range rejected not clamped, echoed in the response); platform proxies it verbatim and Backend owns the bound. See [docs/cross-service-contracts.md § Eval framework](docs/cross-service-contracts.md#eval-framework-e2).
- **Feature-gate 404s:** disabled flags return the same 404 body as nonexistent routes — no "feature disabled" message to prevent probing. Full details: [docs/cross-service-contracts.md § Feature-gate 404s](docs/cross-service-contracts.md#feature-gate-404s-are-indistinguishable-from-route-not-found). Do not reintroduce a gate-specific message or code.
- **O5 one-shot durable triggers:** Backend authority for trigger definitions, one-shot fire ledger, and claim/lease mechanics; Platform proxies `/api/admin/triggers*` with `AGENT_TRIGGERS_ENABLED` 404 fail-closed gate + `workflow.manage` authorization; Frontend sidebar entry "排程觸發" gated by `flags.agentTriggersEnabled && canManageWorkflow`. Fire creates D5 root runs via immutable execution principal snapshot (principal granted at trigger creation time, not at fire time). Full details: [docs/cross-service-contracts.md § O5 one-shot durable triggers](docs/cross-service-contracts.md#o5-one-shot-durable-triggers).

### Agent platform (D1–D7) — invariants only

Full contracts live in [docs/agent-platform-contracts.md](docs/agent-platform-contracts.md) — **read the relevant section there before changing any side of these**:

- **Agent Registry (D1):** `/api/agents*` is ADMIN-only Builder API behind `AGENT_BUILDER_ENABLED` (default false, 404 fail-closed); never reuse as USER catalog.
- **Business Rules (D2):** Agent `business_rules` is canonical JSON AST (`version:1`), never code; Workflow owns catalog/validator; unknown facts resolve through explicit `onUnknown` (default deny).
- **Direct Agent runtime (D3):** ADMIN-only test runs behind fail-closed `AGENT_TEST_RUN_ENABLED`; Backend owns durable state; Workflow owns LangGraph execution. No streaming, approvals, or write tools.
- **Workflow Designer & Orchestrator Registry (D4):** gated by `WORKFLOW_DESIGNER_ENABLED` + exact `workflow.manage` (not ADMIN alone); Workflow compiles Graph IR; root context tools `read`/`low` only.
- **Root Orchestrator runtime (D5):** `MULTI_AGENT_DISPATCH_ENABLED` fail-closed; Backend owns snapshots/fencing/budgets; Workflow claims only Backend artifacts, enforces verifier independence, PASS-only aggregation.
- **Context Enrichment (E1/E3):** `CONTEXT_ENRICHMENT_ENABLED` fail-closed, effective only with D5; Backend owns revisions/policy/evidence; Workflow submits content only. See [docs/context-enrichment-contracts.md](docs/context-enrichment-contracts.md).
- **Agent Chat canary (D6):** `AGENT_CHAT_ENABLED` + `AGENT_CHAT_TENANT_ALLOWLIST` control selection; additionally requires D5; ineligible callers stay legacy; unavailable Orchestrator fails closed.
- **Write tools & approvals (D7):** `AGENT_WRITE_TOOLS_ENABLED` fail-closed; only `runtime.write_evidence` is writable; durable `waiting_approval` before effect; once-only identity makes replay safe.

## Coding Style

All development agents follow [docs/coding-standards.md](docs/coding-standards.md): Karpathy's four principles, reuse-first, Node-First for `workflow/`, Business Rules in Backend only, and **every plan must inventory old code that becomes dead or duplicated**. Codebase-memory MCP for semantic search; Grep for literal strings.

Two platform choices requiring authorization to change: .NET test suites use **hand-written fakes, no mocking**; frontend has **no router and no UI library**.

## Commit & Pull Request Guidelines

Short imperative commit subjects. PRs: summary, test commands run, screenshots for UI changes, and call out any `infra/` configuration edits. Repository CI is `.github/workflows/ci.yml` (backend/platform/workflow/frontend test suites + compose expansion/hardening/resource-calibration validation + container-hardening verification + full-chain-smoke; backend job requires pgvector service container).

## Security & Configuration Tips

Never commit secrets. Copy `infra/.env.example` to `infra/.env` (development defaults only). All services route through LiteLLM (`http://localhost:4000`, virtual key `sk-1234`), never directly to OpenAI. Outside Development, startup rejects blank credentials or committed dev values; production must provide separate JWT material, internal token, database/broker credentials, and runtime HMAC keys. Per-service env variables: see each area's AGENTS.md. Treat prompt/completion logging and traces as sensitive outside Development. Start scripts explicitly opt into Development; raw Compose defaults to Production.

Line endings: `*.sh` must stay LF (CRLF shebang fails in Linux build images); `.gitattributes` pins this.
