# Workflow Service (Python 3.12+ LangGraph + FastAPI, host `:8001` → container `:8000`)

Area-specific guidance. Cross-service contracts (X-Internal-Token + identity headers, ApiError) live in the repo-root [AGENTS.md](../AGENTS.md).

## Layout

- `app/workflows/` — one module per named workflow + `registry.py` (decorator registration; adding a workflow = adding one module file).
- `app/nodes/` — reusable graph nodes (e.g. `retrieve`: tenant-filtered vector search **via backend HTTP**, not a local vector store).
- `app/main.py` (FastAPI entry, internal-token check + tenant context), `settings.py` (pydantic-settings), `llm.py` (ChatOpenAI → LiteLLM), `tracing.py` (Langfuse callback behind env switch), `security.py`, `schemas.py`.
- Dependency management is uv + `pyproject.toml` (`[tool.uv] package = false`); dev group has pytest.

## Commands (run from `workflow/`)

```bash
uv sync
uv run pytest
uv run uvicorn app.main:app --port 8000
```

## Gotchas

- **State machine only** — this service no longer manages documents, chunking, or embeddings. Retrieval calls backend `/api/retrieval/search` over HTTP (httpx) with the internal token and identity headers.
- LLM goes through LiteLLM (`LLM_BASE_URL`/`LLM_API_KEY`/`LLM_MODEL` envs; `mock-gpt` for keyless testing). Langfuse LangChain callback is toggled by `LANGFUSE_ENABLED`.
- `langfuse.langchain.CallbackHandler` imports `langchain` internally — the full `langchain` package is required, `langchain-core` alone is not enough (already pinned in `pyproject.toml`).
- Host port is `:8001` (container `:8000`) because mem0 occupies host `:8000`.
