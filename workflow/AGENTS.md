# Workflow Service (Python 3.12+ LangGraph + FastAPI, host `:8001` → container `:8000`)

Area-specific guidance. Cross-service contracts (X-Internal-Token + identity headers, ApiError) live in the repo-root [AGENTS.md](../AGENTS.md).

## Layout

- `app/engine/` — Skill engine (Node-first P1–P4): `node_registry.py` (`@node` decorator, contracts), `skill.py` (YAML schema + static validation), `compiler.py` (Skill → LangGraph graph + cache + recursion guard), `expressions.py` (AST-based safe condition evaluator), `script_runner.py` (v1 in-process sandbox, ADMIN-only, upgrade path to subprocess), `tool_registry.py` (`@tool` decorators, HTTP/local).
- `app/skills/` — Skill definitions: `kb_query.yaml` (first built-in skill, declarative equivalent of old hand-written graph), `custom.py` (load custom skills from backend and merge at startup).
- `app/tools.py` — four initial tools (retrieve, embed, chunk, rerank).
- `app/workflows/` — one module per named workflow + `registry.py` (decorator registration; adding a workflow = adding one module file).
- `app/nodes/` — reusable graph nodes (e.g. `retrieve`: tenant-filtered vector search **via backend HTTP**, not a local vector store).
- `app/kbquery/` — the `kb_query` workflow's package (too big for one module): 10 single-responsibility nodes under `nodes/`, typed state (`state.py`), Pydantic contracts (`models.py`), Protocol ports (`ports.py`) with adapters/locators, deterministic `calculator.py`, and a `runtime.traced()` wrapper that enforces audit tracing + `original_query` immutability. Evidence-verification gate: no substantive answer unless verification PASSes; retries are bounded by `KB_QUERY_MAX_RETRIEVAL_ATTEMPTS`. Only the vector search source is wired to backend; keyword/metadata/table/structured searchers, external reranker, and the audit DB are behind ports, not yet connected. Registry now locks and compiles versioned graph; kept as parity reference.
- `app/main.py` (FastAPI entry, internal-token check + tenant context), `settings.py` (pydantic-settings), `llm.py` (ChatOpenAI → LiteLLM), `tracing.py` (Langfuse callback behind env switch), `security.py`, `schemas.py`.
- Dependency management is uv + `pyproject.toml` (`[tool.uv] package = false`); dev group has pytest.

## Commands (run from `workflow/`)

```bash
uv sync
uv run pytest
uv run uvicorn app.main:app --port 8000
```

## APIs

New Skill Engine endpoints (all require `X-Internal-Token` and identity headers):

- `GET /nodes` — list all registered nodes with their I/O contracts.
- `GET /skills` — list all skills (built-in + custom) with `input_schema`/`output_schema`.
- `POST /skills/validate` — validate a Skill YAML definition (syntax + schema check).
- `POST /skills/{name}/invoke` — execute a skill, return `{status, output, trace}`.
- `GET /workflows` — list workflows with new `input_schema` field.

## Gotchas

- **Skill engine + node registry** — nodes are first-class citizens via `@node` decorators; skills are declarative YAML compiled to LangGraph graphs at load/invoke time. Custom skills are fetched from backend and merged with built-ins. Test suite expanded to 343 pytest tests (unit + integration coverage for engine, compiler, tool registry, and all nodes).
- **State machine only** — this service no longer manages documents, chunking, or embeddings. Retrieval calls backend `/api/retrieval/search` over HTTP (httpx) with the internal token and identity headers.
- LLM goes through LiteLLM (`LLM_BASE_URL`/`LLM_API_KEY`/`LLM_MODEL` envs; `mock-gpt` for keyless testing). Langfuse LangChain callback is toggled by `LANGFUSE_ENABLED`.
- `langfuse.langchain.CallbackHandler` imports `langchain` internally — the full `langchain` package is required, `langchain-core` alone is not enough (already pinned in `pyproject.toml`).
- Host port is `:8001` (container `:8000`) because mem0 occupies host `:8000`.
