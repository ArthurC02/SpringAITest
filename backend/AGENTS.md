# Backend Core Service (ASP.NET Core 10, `:8002`)

Area-specific guidance. Cross-service contracts (X-Internal-Token + identity headers, 202 flow, auth/JWT, ApiError) live in the repo-root [AGENTS.md](../AGENTS.md) — read that first when changing anything platform or workflow consumes.

## Layout

Solution `Backend.sln`, single project `src/Backend.Api` organized by feature folders — Auth, Conversations, Files, Retrieval, Analysis, Config — with no layered dependencies. Data access is Dapper 2.x + Npgsql 9.x directly against appdb (PostgreSQL with pgvector), no ORM. Tests in `tests/Backend.Api.Tests` (xUnit, 57 tests, hand-written fakes, in-memory Dapper fixtures).

## Commands (run from `backend/`)

```bash
dotnet build
dotnet test
dotnet run --project src/Backend.Api
```

Requires appdb running (default `DB_CONNECTION_STRING`: `Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=springaitest`). DB tables and seeds are created idempotently on startup.

## Gotchas

- **appdb owns ALL persistent data** (users, tenants, conversations, documents, vectors, config) — restarts do **not** clear history. Only platform's in-memory short-term window resets.
- **Document consumer** (`DocumentProcessor` BackgroundService): consumes RabbitMQ queue `documents.process` with prefetch 1 + manual ack; redelivery is idempotent (ON CONFLICT insert + skip-if-ready); chunk → embed → pgvector, then flips document status to `ready`/`failed`. Startup logs a retrying `BrokerUnreachableException` until the broker is up — expected noise, backed-off retry by design.
- **Embeddings:** `EMBEDDINGS_PROVIDER` is `fake` (deterministic, default — tests and E2E rely on it) or `openai` (routed through LiteLLM, needs `text-embedding-3-small` in `litellm-config.yaml`).
- **Seed data** (from `DbBootstrap`): tenants `demo-a`/`demo-b` (invite codes `demo-a-invite`/`demo-b-invite`); users `admin-a` (ADMIN, demo-a), `user-a` (USER, demo-a), `user-b` (USER, demo-b); password `password123`.
- **Auth feature issues the JWTs** (HS256) that platform validates — `JWT_SECRET` must match platform's.
- **Never expose to LAN:** the service binds `127.0.0.1` only and *trusts* the `X-Tenant-Id`/`X-User-Id`/`X-User-Role` headers after `X-Internal-Token` passes. Its security model assumes only platform and workflow can reach it.
- **Env:** `DB_CONNECTION_STRING`, `INTERNAL_API_TOKEN` (default `internal-dev-token`), `JWT_SECRET`, `EMBEDDINGS_PROVIDER`, `RABBITMQ_URL`.

## Testing

xUnit with hand-written fakes (no mocking library). Coverage spans Auth (registration, JWT signing), Retrieval (vector search), Config (ADMIN perms), chunking, fake-embedding determinism, and internal-token validation.
