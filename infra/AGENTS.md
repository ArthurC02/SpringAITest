# Infra (Docker Compose, project name `springaitest`)

Area-specific guidance for `infra/` (compose, LiteLLM, Langfuse, mem0) and the root `scripts/` startup helpers. Cross-service contracts live in the repo-root [AGENTS.md](../AGENTS.md).

## Run modes

- **Infra-only (default):** `.\scripts\start-infra.ps1` / `./scripts/start-infra.sh`. Starts everything **except** `frontend` and `platform` (they carry profile `full`): the infra tier (litellm, langfuse-web/worker, postgres, clickhouse, redis, minio, mem0) plus the always-on app tier (appdb, rabbitmq, backend, workflow). Host-run `dotnet run` platform + `npm run dev` frontend pair with this mode.
- **Full container mode:** `.\scripts\start-full.ps1` / `./scripts/start-full.sh` / `docker compose --profile full up -d --build`. Adds the frontend and platform containers; platform publishes `:8080` to the host so frontend nginx's `host.docker.internal:8080` reverse-proxy works unchanged in both modes (one nginx config serves both).
- Do **not** run host frontend/platform and `--profile full` simultaneously — both bind `:5173` and `:8080`. A stale host process can also shadow the container: check `Get-NetTCPConnection -LocalPort 8080` before going full mode. Full run-mode matrix in [README.md](../README.md).
- **Prefer the start scripts over raw `docker compose up -d`:** they bring `postgres` up first and run `scripts/ensure-mem0-db.*` (creates the `mem0_app` DB and refreshes collation) so mem0 doesn't crash-loop waiting for its DB.

## Service gotchas

- **LiteLLM (`:4000`):** all model/provider routing lives in `litellm-config.yaml`; virtual key `sk-1234`; `mock-gpt` is the keyless test model. mem0's embedder needs `text-embedding-3-small` registered here.
- **RabbitMQ (`:5672` loopback, UI `:15672`):** the `guest` account only allows loopback connections — inter-container traffic must use the `app` account (password env `RABBITMQ_PASSWORD`, default `app-dev-password`). Queue `documents.process` is durable — messages survive broker restarts. If `POST /api/documents` returns 502, check broker reachability and `RABBITMQ_URL` in both platform and backend.
- **Backend + Workflow cross-service:** backend's new `WORKFLOW_BASE_URL` env (e.g. `http://workflow:8000` in compose, `http://localhost:8001` in host mode) is used at skill-write time to validate YAML against the engine. If workflow is unreachable, skill creation/update returns 502. Platform's `WORKFLOW_BASE_URL` env is used for skill invoke/validate/catalog endpoints and node catalog.
- **mem0 (`:8000`) — the official `mem0-api-server` image ships broken**, wrapped by `mem0.Dockerfile`; known issues: (1) lacks libpq → Dockerfile adds `psycopg[binary,pool]`; (2) needs a `mem0_app` DB it won't self-create → start scripts create it; (3) latest image may die at startup on `ImportError: langchain_neo4j` — container shows Up but port 8000 never listens; chat is unaffected (mem0 is best-effort). The compose service uses the **prebuilt local image** `springaitest-mem0:latest` (`image:`, not `build:`) so `start-full`'s `--build` doesn't stall on it; rebuild manually with `docker build -f mem0.Dockerfile -t springaitest-mem0:latest .` (on amd64 hosts add `--platform=linux/arm64` if the upstream base lacks an amd64 manifest). Details in the mem0 section of [README.md](../README.md).
- **appdb (host `:5433`):** PostgreSQL with pgvector; all app data persists here across restarts. Volumes (8 total incl. Langfuse/postgres/appdb/rabbitmq data) must survive teardown — `docker compose down` **without** `-v`, never `volume rm`/`prune`.
- **Frontend 502 from `/api`:** check platform reachability at `http://localhost:8080/actuator/health`. Infra-only mode expects host Vite; full mode requires `springaitest-frontend-1` running under the `full` profile.
- **Langfuse (`:3000`):** receives traces via two paths — LiteLLM `success_callback` (token/cost) and .NET OTLP. Treat traces as sensitive outside dev.
