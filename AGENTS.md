# Repository Guidelines

Solution-wide guidance only. Each area has its own `AGENTS.md` (+ `CLAUDE.md` importing it) with layout, commands, and area-specific gotchas — **read the area file before working in that area**; Claude Code loads it on demand when files there are touched.

## Monorepo Map

| Area        | What it is                                                                                                                                                                | Details                                  |
| ----------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| `platform/` | ASP.NET Core 10 gateway (`:8080`): JWT verify, SSE chat, Microsoft Agent Framework + AG-UI endpoint, mem0, BackendClient proxy, Skill CRUD/invoke proxy. xUnit 175 tests. | [platform/AGENTS.md](platform/AGENTS.md) |
| `backend/`  | ASP.NET Core 10 core service (`:8002`): feature folders, Dapper + appdb (PostgreSQL/pgvector), RabbitMQ document consumer, issues JWTs, Skills CRUD. xUnit 100 tests.     | [backend/AGENTS.md](backend/AGENTS.md)   |
| `frontend/` | React 19 + Vite + TypeScript SPA (`:5173`): login + five views (Chat, Documents, Workflows & Skills, Analysis, Config), CopilotKit sidebar.                               | [frontend/AGENTS.md](frontend/AGENTS.md) |
| `workflow/` | Python 3.12+ LangGraph + FastAPI (host `:8001`): Skill engine layer, named workflows, node registry, retrieval via backend HTTP. pytest 343 tests.                        | [workflow/AGENTS.md](workflow/AGENTS.md) |
| `infra/`    | Docker Compose (LiteLLM, Langfuse, postgres, RabbitMQ, mem0, appdb…), `litellm-config.yaml`. `scripts/` holds startup helpers.                                            | [infra/AGENTS.md](infra/AGENTS.md)       |

Start services with `.\scripts\start-infra.ps1` / `./scripts/start-infra.sh` (infra-only, default) or `start-full.*` (everything containerized). Run modes, port conflicts, and container known-issues: [infra/AGENTS.md](infra/AGENTS.md); run-mode matrix: [README.md](README.md).

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
| `docs-updater`         | Sync README/AGENTS files after feature or architecture changes                      |

Only work with no matching agent (small edits in `workflow/`/`infra/`, quick Q&A) stays in the main loop.

**Every subagent definition must pin all four knobs in its frontmatter:** `model`, `tools`, `hooks`, and MCP access (MCP tools are granted as `mcp__<server>__<tool>` entries inside `tools`). Where one is deliberately absent (e.g. no hooks for a docs-only agent), record that as a YAML comment in the frontmatter — omission must be a decision, never an oversight. New agents follow the same rule.

## Cross-Service Contracts

These facts span two or more areas — changing one side silently breaks the other, so they live here, not in the area files:

- **Public chat API** (platform): `POST /api/chat` (blocking), `POST /api/chat/stream` (SSE), `GET /api/chat/history`. POST bodies take `message` plus optional `userId` (mem0 long-term grouping; blank → `default`) and `conversationId` (short-term memory grouping; blank → falls back to `userId`). `GET /api/chat/history` is filtered by the caller's login identity (tenant + user, from the JWT) — anonymous requests get an empty array, not a 401 (endpoint stays `AllowAnonymous`); anonymous chat is never persisted either.
- **Two SSE formats, both deliberate:** `/api/chat/stream` writes hand-rolled `data:<value>` (no space); the AG-UI endpoint `/api/copilot/agui` writes protocol-standard `data: ` (with space). The frontend parses chat SSE with `fetch` + `ReadableStream` (`EventSource` cannot POST a body). Every proxy hop (Vite dev proxy, frontend nginx) must keep `proxy_buffering off` on SSE paths.
- **AG-UI copilot:** the browser connects **directly** to platform's `/api/copilot/agui` (AllowAnonymous, same posture as `/api/chat`) via `@ag-ui/client` HttpAgent + CopilotKit's `agents__unsafe_dev_only` — POC posture, no Node bridge. Frontend side: [frontend/AGENTS.md](frontend/AGENTS.md); platform side: [platform/AGENTS.md](platform/AGENTS.md).
- **Error shape:** every API returns ApiError `{timestamp, status, message, fieldErrors}` (camelCase). Response field naming is mixed **by design**: documents/workflows/analysis are snake_case (`chunk_count`, `required_role`, `created_at`); auth/config are camelCase (`updatedAt`). Do not "fix" either side.
- **Backend trust boundary:** backend (`:8002`) requires `X-Internal-Token: <INTERNAL_API_TOKEN>` plus identity headers (`X-Tenant-Id`, `X-User-Id`, `X-User-Role`) on everything except `/health`; Config `PUT` additionally requires role ADMIN. Only platform and workflow call it; workflow (`:8001`) shares the same trust model. Both bind `127.0.0.1` and must never face LAN or public internet.
- **Async document processing:** platform publishes to RabbitMQ queue `documents.process` and returns `202 {id, title, status:"processing"}`; backend's consumer chunks/embeds/stores and flips status to `ready`/`failed`. The document is absent from `GET /api/documents` until consumed — eventual consistency by design (frontend bridges the gap with optimistic insert + polling). Broker unreachable at publish time → `502`.
- **Auth:** backend issues JWTs (HS256, shared `JWT_SECRET`), platform validates them. Frontend stores `{token, username, role, tenantCode}` in localStorage and sends `Authorization: Bearer` on all API calls via the unified `apiFetch`; any `401` while logged in clears the session (and chat localStorage keys) and returns to login.
- **Two memory layers, don't confuse them:** *short-term* = in-memory sliding window in platform (last 20 messages per `conversationId`, resets on restart); *long-term* = mem0 (cross-session facts, best-effort — errors swallowed so chat never breaks). Everything else persists in appdb across restarts.
- **Skill Engine (P1–P4 nodes, backend/workflow split):** Skills are declarative YAML-defined workflows compiled by workflow's engine (`app/engine/`). Backend stores skill metadata in `skill`/`skill_revision` tables and issues JWTs; platform proxies CRUD (`/api/skills*` → backend) and invocation/validation (`/api/skills/{name}/invoke`, `/api/skills/validate`, `/api/skills/catalog`, `/api/nodes` → workflow). Workflow's engine is the sole source of truth for compilation/execution; it validates syntax at write time (backend calls `POST /api/skills/validate` as a request-time dependency — 502 if workflow is unreachable). Node contracts (`@node` decorators) declare `reads`/`writes`/`deps`/`appends`/`dynamic_reads`/`requires_tools`; tool registry (`@tool` decorators) wires HTTP and local callables. Custom skills merge with built-ins at load time.

## Coding Style & Naming Conventions

C# namespaces under `Platform.*` (gateway) and `Backend.*` (core service); classes/properties/methods `PascalCase`, private fields `_camelCase`, test classes end with `Tests`; tests are xUnit with hand-written fakes (no mocking library) in both .NET solutions. Frontend uses TypeScript React function components: components `PascalCase.tsx`, hooks `useX.ts`, shared types in `src/types.ts`.

## Commit & Pull Request Guidelines

Recent commits use short, imperative summaries such as `Restructure monorepo and add SSE streaming chat frontend (#1)`. Keep subjects concise and user-visible.

Pull requests should include a summary, test commands run, linked issues when applicable, and screenshots or recordings for UI changes. Note configuration edits under `infra/`.

## Security & Configuration Tips

Do not commit secrets. Copy `infra/.env.example` to `infra/.env`; the real `OPENAI_API_KEY` lives only there, behind LiteLLM. All services talk to OpenAI-compatible models **through LiteLLM** (`http://localhost:4000`, env `LLM_BASE_URL`, virtual key `sk-1234`), never directly; provider/model routing belongs in `infra/litellm-config.yaml`. The chat model comes from env `CHAT_MODEL` (default `gpt-4o-mini`; override to `mock-gpt` to test without quota). Per-service env variables are listed in each area's AGENTS.md. Treat prompt/completion logging and Langfuse traces as sensitive in non-dev environments. `JWT_SECRET` and `INTERNAL_API_TOKEN` have public dev defaults committed in the repo, identical across services (zero-config startup); before any non-loopback / non-dev deployment they must all be overridden — leaving the defaults is equivalent to running with no authentication.

Line endings: all `*.sh` must stay LF — a CRLF shebang makes `./script.sh` fail with `not found` inside Linux build images; `.gitattributes` pins this.

`litellm-venv/` in the repo root is a stray local virtualenv (ignored) — not part of the project.
