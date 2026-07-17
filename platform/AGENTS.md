# Platform Gateway (ASP.NET Core 10, `:8080`)

Area-specific guidance. Cross-service contracts (SSE formats, ApiError, backend trust boundary, 202 flow, auth) live in the repo-root [AGENTS.md](../AGENTS.md) — read that first when changing anything another service consumes.

## Layout

Solution `Platform.sln`, dependencies one-way `Web -> Service` (no data layer):

- `src/Platform.Service` — business logic: `ChatService` (LLM via Microsoft Agent Framework), `Mem0Client`, short-term memory (in-memory sliding window, last 20 messages per `conversationId`), `BackendClient` proxy to backend `:8002`, `SkillService` (proxies CRUD to backend, invoke/validate/catalog to workflow), DTOs.
- `src/Platform.Web` — controllers (`ChatController`, `SkillController` for CRUD, `NodeController` for catalog/invoke), JWT validation (tokens are *issued* by backend, same `JWT_SECRET`), SSE streaming, AG-UI endpoint wiring, `GlobalExceptionHandler`, executable app.
- `tests/Platform.Service.Tests` + `tests/Platform.Web.Tests` — xUnit, ~182 tests total, hand-written fakes (no mocking library).

## Commands (run from `platform/`)

```bash
dotnet build
dotnet test
dotnet test tests/Platform.Service.Tests
dotnet run --project src/Platform.Web
```

After changing `Platform.Service`, rebuild the solution before running only `Platform.Web`; the .NET runtime handles transitive dependencies automatically (no stale-artifact issue like Maven's SNAPSHOT).

## Gotchas

- **Streaming vs blocking** (`ChatController`): `POST /api/chat` is synchronous and completes before returning; `POST /api/chat/stream` returns an `IAsyncEnumerable<string>`, writing chunks as SSE `data:` lines and flushing after each write. Stream payloads persist to the database in a completion handler and drive their `ActivitySource` span manually.
- **Agent Framework API traps:** the extension is `GetChatClient(model).AsAIAgent(...)` — the *string* overload carries instructions (`ChatClientAgentOptions` has NO `Instructions` property). `Microsoft.Extensions.AI.ChatMessage` collides with `OpenAI.Chat` — alias it.
- **AG-UI endpoint:** `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` (preview, restores against stable `Microsoft.Agents.AI`) + `AddAGUI()` + `MapAGUI("/api/copilot/agui", agent).AllowAnonymous()` in `Program.cs`, mapped after `UseAuthentication`/`UseAuthorization`. Note MapAGUI emits standard `data: ` (with space) — different from ChatController's hand-rolled `data:` on purpose. Upgrade path if hardening: `RequireAuthorization()` + forward the `Authorization` header from the client.
- **mem0 (`Mem0Client`):** `ChatService` calls `recall` before the LLM (memories injected as a `system` preamble) and `remember` after, on both `chat()` and `streamChat()`. Best-effort by design — every mem0 error is swallowed (`recall` → empty, `remember` → no-op) so chat never breaks.
- **Observability:** both blocking and streaming endpoints emit a business `Activity` span; .NET → OpenTelemetry → OTLP → Langfuse (configured in `appsettings.json` under `OpenTelemetry`). LiteLLM's `success_callback` covers token/cost as the second path.
- **Skill proxy routing:** `SkillController` routes CRUD (`POST`/`PUT`/`GET`/`DELETE /api/skills*`) and export (`GET /api/skills/{name}/export`, streams the backend zip through verbatim as `application/zip`) to backend; `SkillController` also routes invocation (`POST /api/skills/{name}/invoke`), validation (`POST /api/skills/validate`), and skill catalog (`GET /api/skills/catalog`) to workflow; `NodeController` routes node catalog (`GET /api/nodes`) to workflow. Both paths require `Authorization: Bearer` (JWT from user login). Workflow errors (unparseable YAML, missing nodes) surface as `400`/`422` to the client; if workflow is unreachable at invoke time → `502`.
- **Env:** `LLM_BASE_URL` (default `http://localhost:4000`), `CHAT_MODEL` (default `gpt-4o-mini`; `mock-gpt` for keyless testing), `WORKFLOW_BASE_URL` (e.g. `http://localhost:8001`, used for skill invoke/validate/catalog and node catalog), `BACKEND_BASE_URL`, `RABBITMQ_URL`, `INTERNAL_API_TOKEN`, `JWT_SECRET`.

## Testing

Prefer focused tests near the changed layer: service units in `Service.Tests` (fake `HttpMessageHandler` for BackendClient), controller/application tests in `Web.Tests` (`WebApplicationFactory`). Hand-written fakes only — no mocking library.
