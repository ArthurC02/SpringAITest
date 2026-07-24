# Backend Core Service (ASP.NET Core 10, `:8002`)

Area-specific guidance. Cross-service contracts (X-Internal-Token + identity headers, 202 flow, auth/JWT, ApiError) live in the repo-root [AGENTS.md](../AGENTS.md) — read that first when changing anything platform or workflow consumes.

## Layout

Solution `Backend.sln`, single project `src/Backend.Api` organized by feature folders — Auth, Conversations, Files, Retrieval, Analysis, Config, Skills — with no layered dependencies. Data access is Dapper 2.x + Npgsql 9.x directly against appdb (PostgreSQL with pgvector), no ORM; in lite mode can switch to in-memory repositories (singleton per-request snapshots, no persistence across restarts). `Common/ApiErrors.cs` is the centralized 404 message factory for consistency across endpoints. Tests in `tests/Backend.Api.Tests` (xUnit, 255 tests, hand-written fakes, in-memory fixtures).

## Commands (run from `backend/`)

```bash
dotnet build
dotnet test
dotnet run --project src/Backend.Api
```

Requires appdb running (default `DB_CONNECTION_STRING`: `Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=springaitest`). DB tables and seeds are created idempotently on startup.

## Gotchas

- **appdb owns ALL persistent data** (users, tenants, conversations, documents, vectors, config, skills) — restarts do **not** clear history. Only platform's in-memory short-term window resets. **rag_chunks vector search:** `rag_chunks` table has an HNSW index on the embedding column (`rag_chunks_embedding_hnsw_idx` using `vector_cosine_ops`) for ANN; requires pgvector ≥ 0.5. **Chunk writes:** inserted as a single multi-line `INSERT ... VALUES (...), (...), ...` statement per batch (reprocessing is idempotent via DELETE-then-INSERT inside one transaction), avoiding N round-trips.
- **Skills storage:** `skill` table (tenant_id, name, definition YAML, current_revision, `enabled`=soft-delete flag) + `skill_revision` audit table (one row per write, `definition_sha256`, never deleted) with atomic data-modifying CTEs on write. `DELETE` sets `enabled=false` (revisions retained for audit); re-`POST`ing a soft-deleted name revives it with a bumped revision (not a 409). Custom skills are user-managed; built-ins live in workflow.
- **Skill validation at write time:** `PUT /api/skills/{name}` calls `POST /api/skills/validate` on workflow (`:8001`) as a request-time dependency — if workflow is unreachable, return `502`. This is deliberate: the engine is the only source of truth for syntax.
- **Skill export:** `GET /api/skills/{name}/export` returns a zip (`application/zip`, `filename="<name>.zip"`) in Claude Skill format. **flow** skills export a *single self-contained* `{name}/SKILL.md` — one top-level folder named exactly `name` (05 §0: `name` must equal the folder name, so unzipping yields a matching folder instead of a tool-chosen one), holding standard frontmatter `name`+`description` (description YAML-safe) + a body embedding the `definition` column verbatim inside one fenced ` ```yaml ` block (05 §3.1; byte-for-byte between the opening ` ```yaml ` line and closing ` ``` `; no `skill.yaml` entry). The workflow importer strips that single top-level folder and round-trips by extracting the first ` ```yaml ` block. **agentic** skills (and any flow skill imported with a stored package) export the stored package bytes verbatim (all entries, no re-assembly, no folder added) — the asymmetry is deliberate: byte-identical round-trip wins over re-layout. `SkillExporter` is pure string assembly + `System.IO.Compression`, no code execution; role is USER (same data as `GET /api/skills/{name}`, just zipped), tenant-filtered so cross-tenant is 404.
- **Document consumer** (`DocumentProcessor` BackgroundService): consumes RabbitMQ queue `documents.process` with prefetch 1 + manual ack; redelivery is idempotent (ON CONFLICT insert + skip-if-ready); chunk → embed → pgvector, then flips document status to `ready`/`failed`. Startup logs a retrying `BrokerUnreachableException` until the broker is up — expected noise, backed-off retry by design.
- **Embeddings:** `EMBEDDINGS_PROVIDER` is `fake` (deterministic, default — tests and E2E rely on it) or `openai` (routed through LiteLLM, needs `text-embedding-3-small` in `litellm-config.yaml`).
- **Seed data** (from `DbBootstrap`): tenants `demo-a`/`demo-b` (invite codes `demo-a-invite`/`demo-b-invite`); users `admin-a` (ADMIN, demo-a), `user-a` (USER, demo-a), `user-b` (USER, demo-b); password `password123`. In lite mode (`DB_PROVIDER=inmemory`), the same seed data is hardcoded into `InMemoryAuthRepository` with matching BCrypt hashes.
- **InMemory repositories (lite-only, DB_PROVIDER=inmemory):** six singleton in-memory repositories (`InMemoryAuthRepository`, `InMemoryConversationRepository`, `InMemoryRagRepository` with true cosine similarity, `InMemoryConfigRepository`, `InMemorySkillRepository`, `InMemoryConfigurationSetRepository`) live in `src/Backend.Api/Data/InMemory/` and are registered at startup; `DbBootstrap` is skipped entirely, and `AddNpgsqlDataSource` is not called. Tests reference the same fakes (now hosted in the main API project under a global using alias `Backend.Api.Data.InMemory`), ensuring behavior parity between test and lite mode.
- **Auth feature issues the JWTs** (HS256) that platform validates — `JWT_SECRET` must match platform's.
- **Never expose to LAN:** the service binds `127.0.0.1` only and *trusts* the `X-Tenant-Id`/`X-User-Id`/`X-User-Role` headers after `X-Internal-Token` passes. Its security model assumes only platform and workflow can reach it.
- **Env:** `DB_CONNECTION_STRING`, `INTERNAL_API_TOKEN` (default `internal-dev-token`), `JWT_SECRET`, `EMBEDDINGS_PROVIDER`, `RABBITMQ_URL`, `WORKFLOW_BASE_URL` (e.g. `http://localhost:8001`, used for skill validation at write time). **Lite-only:** `DB_PROVIDER` (unset or `dapper` = Npgsql + DbBootstrap, `inmemory` = six in-memory repositories with seed accounts, skips DbBootstrap and Npgsql connection pool).

## Testing

xUnit with hand-written fakes (no mocking library). Coverage spans Auth (registration, JWT signing), Retrieval (vector search), Config (ADMIN perms), chunking, fake-embedding determinism, and internal-token validation.
