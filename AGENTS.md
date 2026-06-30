# Repository Guidelines

## Project Structure & Module Organization

This monorepo has three main areas. `backend/` is a Maven multi-module Spring Boot app: `springaitest-domain` holds entities/repositories, `springaitest-service` holds business logic and Spring AI calls, and `springaitest-web` holds controllers and the executable app. Backend tests live under each module's `src/test/java`.

`frontend/` is a React 19 + Vite + TypeScript SPA. UI code is in `frontend/src`, with API helpers in `src/api`, hooks in `src/hooks`, and components in `src/components`.

`infra/` contains Docker Compose, LiteLLM, and Langfuse configuration. `scripts/` contains cross-platform startup helpers.

## Build, Test, and Development Commands

Backend commands run from `backend/`:

```bash
./mvnw clean install
./mvnw test
./mvnw -pl springaitest-service test
./mvnw -pl springaitest-web spring-boot:run
```

Use `.\mvnw.cmd` on Windows. The web module is the only executable backend module and runs on `:8080`. After changing `springaitest-domain` or `springaitest-service`, rebuild/install the reactor before running only `springaitest-web`, for example `./mvnw -pl springaitest-web -am install -DskipTests`; otherwise a stale local SNAPSHOT jar can cause runtime `NoSuchMethodError`.

Frontend commands run from `frontend/`:

```bash
npm install
npm run dev
npm run build
npm run lint
```

`npm run dev` serves Vite on `:5173` and proxies `/api` to `:8080`; `build` type-checks and bundles; `lint` runs oxlint.

Start infrastructure with `.\scripts\start-infra.ps1` or `./scripts/start-infra.sh`. Default compose mode is infra-only — 7 services: LiteLLM, Langfuse (web + worker), postgres, clickhouse, redis, and minio. Full container mode uses `.\scripts\start-full.ps1`, `./scripts/start-full.sh`, or `docker compose --profile full up -d --build` from `infra/`; this also starts the frontend and backend containers. In full mode the backend container publishes `:8080` to the host, so the frontend nginx's `host.docker.internal:8080` reverse-proxy works unchanged in both modes (one nginx config serves both). Do not run host frontend/backend and `--profile full` at the same time because both modes bind `:5173` and `:8080`. See [README.md](README.md) for the full run-mode matrix.

If the containerized frontend returns `502` from `/api`, check whether the backend is reachable at `http://localhost:8080/actuator/health`. In default infra-only mode the frontend is expected to be host Vite (`npm run dev`); in full mode `springaitest-frontend-1` must be running under the `full` profile.

## Architecture & Gotchas

Non-obvious things that otherwise require reading several files:

- **API endpoints** — all in `ChatController` under `/api/chat`: `POST /api/chat` (blocking), `POST /api/chat/stream` (SSE), `GET /api/chat/history`.
- **Streaming vs blocking** (`ChatServiceImpl`): `chat()` is `@Transactional`; `streamChat()` returns `Flux<String>` and is deliberately **not** `@Transactional` — the Flux executes at subscription time, outside the method's transaction. It persists in `doOnComplete` and drives its Micrometer `Observation` manually (`start()` / `doFinally(stop)`). Do not "fix" it by adding `@Transactional`.
- **SSE contract:** the stream endpoint returns `text/event-stream`, each chunk written as `data:<value>`. The frontend (`frontend/src/api/chat.ts`) parses it with `fetch` + `ReadableStream`, not `EventSource` (which cannot POST a body); `frontend/nginx.conf` sets `proxy_buffering off` so chunks aren't buffered.
- **Observability → Langfuse via two paths:** LiteLLM's `success_callback` (token/cost) and Spring AI → Micrometer Tracing → OTLP (`management.otlp.tracing` in `application.yml`). Both `chat()` and `streamChat()` emit a business span.
- **H2 is in-memory** (`ddl-auto: update`): the DB resets on every backend restart, so empty history after a restart is expected, not a bug. Console at `/h2-console`.
- **Line endings break the Docker build:** `mvnw` and `scripts/*.sh` must be LF — a CRLF shebang makes `./mvnw` fail with `not found` inside the Linux build image. `.gitattributes` pins them to `eol=lf` and `backend/Dockerfile` strips CR defensively.

## Coding Style & Naming Conventions

Use Java packages under `com.example.springaitest.*` and keep backend dependencies one-way: `web -> service -> domain`. Java classes use `PascalCase`; methods and fields use `camelCase`; tests end with `Test` or `Tests`.

Frontend uses TypeScript, React function components, and Vite conventions. Component files use `PascalCase.tsx`; hooks use `useX.ts`; shared types belong in `src/types.ts` or near their feature.

## Testing Guidelines

Backend tests use JUnit, Spring Boot test slices, H2, and Mockito. Run all tests with `./mvnw test`, or target one module with `-pl`. Prefer focused tests near the changed layer: repositories in `domain`, service units in `service`, and MVC/application tests in `web`.

Frontend has lint and build checks but no dedicated test runner. Run `npm run lint` and `npm run build` before PRs that change frontend code.

## Commit & Pull Request Guidelines

Recent commits use short, imperative summaries such as `Restructure monorepo and add SSE streaming chat frontend (#1)`. Keep subjects concise and user-visible.

Pull requests should include a summary, test commands run, linked issues when applicable, and screenshots or recordings for UI changes. Note configuration edits under `infra/`.

## Security & Configuration Tips

Do not commit secrets. Copy `infra/.env.example` to `infra/.env`; the real OpenAI key stays behind LiteLLM. Treat prompt/completion logging and Langfuse traces as sensitive in non-dev environments.

The app must talk to OpenAI-compatible models through LiteLLM, not directly. Provider/model routing belongs in `infra/litellm-config.yaml`; the Spring AI model name must match the LiteLLM `model_name`. The Spring app reaches LiteLLM at `base-url` `http://localhost:4000` (env `LLM_BASE_URL`) using the virtual key `sk-1234`; the real `OPENAI_API_KEY` lives only in `infra/.env`.

`litellm-venv/` in the repo root is a stray local virtualenv (ignored) — not part of the project.
