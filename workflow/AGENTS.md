# Workflow Service (Python 3.12+ LangGraph + FastAPI, host `:8001` → container `:8000`)

Area-specific guidance. Cross-service contracts (X-Internal-Token + identity headers, ApiError) live in the repo-root [AGENTS.md](../AGENTS.md).

## Layout

- `app/engine/` — Skill engine (Node-first P1–P4): `node_registry.py` (`@node` decorator, contracts), `skill.py` (YAML schema + static validation), `compiler.py` (Skill → LangGraph graph + cache + recursion guard), `expressions.py` (AST-based safe condition evaluator), `script_runner.py` (v1 in-process sandbox, ADMIN-only, upgrade path to subprocess), `tool_registry.py` (`@tool` decorators, HTTP/local). **Reads enforcement:** Harness enforces declared `reads` at invoke time — it filters the state passed to node functions to only the keys listed in `reads` (explicit reads required to view state keys; tool/script steps and anonymous nodes receive unfiltered state; agentic runner receives identity keys + reads + input_schema fields; effective_reads are computed per step type at compilation time and passed to the harness at invoke).
- `app/skills/` — Skill definitions: five built-in YAML skills (`kb-query.yaml`, `rag-qa.yaml`, `summarize.yaml`, `triage.yaml`, `analyze-report.yaml` — the last is ADMIN-only) plus five `template-*.yaml` skeletons (template-compare/template-stats use `nl_logic` nodes for business logic, with dedicated `top_k` scalar slots and instruction preambles), `deps.py` (`KbQueryDeps` + `_default_deps()`, DI assembly point), `custom.py` (load custom skills from backend and merge at startup).
- `app/tools.py` — four initial tools (retrieve, embed, chunk, rerank).
- `app/nodes/` — reusable graph nodes: `retrieve` (tenant-filtered vector search **via backend HTTP**, not a local vector store), `nl_extract`/`nl_logic` with `_llm_input.py` (shared user message assembly), and the four modules ported from the retired hand-written workflows — `rag_answer`, `summarize_text`, `triage` (classify/quick/deep), `analyze_report` (doc_insights/report_synthesize).
- `app/nodes/kbquery/` — the `kb-query` skill's `kb_query` node family (too big for one module): 10 single-responsibility nodes under `nodes/`, Pydantic contracts (`models.py`), Protocol ports (`ports.py`) with adapters/locators, deterministic `calculator.py`. Evidence-verification gate: no substantive answer unless verification PASSes; retries are bounded by `KB_QUERY_MAX_RETRIEVAL_ATTEMPTS`. Only the vector search source is wired to backend; keyword/metadata/table/structured searchers, external reranker, and the audit DB are behind ports, not yet connected. `app/skills/kb-query.yaml` is the sole `kb-query` graph (the hand-written LangGraph version and the standalone `/workflows` API it powered were retired once the YAML graph reached parity — Node-First migration Phase 3a).
- `app/backend_http.py` — shared low-level backend HTTP callables (retrieval + other ops); used by `retrieve` node and kbquery's `BackendVectorSearch` port adapter.
- `app/skills/deps.py` — `KbQueryDeps` dataclass + `_default_deps()`: the DI assembly point for kb_query-family nodes, shared by all built-in/custom/template skills that need `llm`/retrieval/audit ports. Engine core (`app/engine/`) never imports `app/nodes/**`; `app/skills/` is the layer allowed to wire node families to concrete adapters.
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
- `GET /tools` — safe Agent Builder tool catalog (`name`/`kind`/`description`/`risk`/`returns`; no endpoint, token, args schema, or callable).
- `GET /skills` — list all skills (built-in + custom) with `input_schema`/`output_schema` and fail-closed `bindable`; builtin is false, custom is true only with a positive persisted `current_revision`.
- `POST /skills/validate` — validate a Skill YAML definition (syntax + schema check).
- `POST /skills/{name}/invoke` — execute a skill, return `{skill, output}`.

Business Rule runtime endpoints (internal .NET integration contract; all require
`X-Internal-Token` plus identity headers):

- `GET /business-rules/catalog` — versioned fact/operator/action metadata,
  supported gates, and resource limits. The request gate is context and is not
  persisted in the RuleSet AST.
- `POST /business-rules/validate` — body `{gate, ruleSet}`; always returns
  `{valid, canonicalRuleSet, errors:[{path,code,message}]}` for semantic
  validation. A valid empty AST still returns
  `canonicalRuleSet: {version:1,rules:[]}`. Callers may add an optional
  `referenceCatalog: {skills?,tools?,roles?,facts?}`; omitted categories preserve
  backward compatibility, while present categories make action references
  fail closed against that allowlist.
- `POST /business-rules/simulate` — body `{gate, ruleSet, facts}`; validates and
  canonicalizes first, then invokes the same pure evaluator used at runtime.
  It never calls tools, network, files, or databases.

Business Rule POST bodies are capped at the catalog's `maxRequestBytes` and
`maxJsonDepth` before JSON decoding. Validation also bounds object fields,
reported errors, nodes, strings, and collections. The evaluator independently
rechecks fact availability at the requested gate; unavailable facts become
`unknown` and follow the rule's explicit fail-closed policy.

Workflow Designer / Harness Graph IR endpoints (D4; internal token plus identity
headers required):

- `GET /workflow-designer/catalog/nodes` returns the server-owned, versioned
  typed Node/port catalogue. It is distinct from the Skill engine's `/nodes`.
- `POST /workflow-designer/validate` validates and canonicalizes
  `{definition, ui_metadata}`. The semantic Graph IR uses camelCase fields;
  its semantic hash is deliberately independent from UI metadata.
- `POST /workflow-designer/simulate` returns only a deterministic, data-free
  stable-node-ID trace. It never invokes tools, adapters, networks, or
  LangGraph runtime execution.

## Gotchas

- **Skill engine + node registry** — nodes are first-class citizens via `@node` decorators; skills are declarative YAML compiled to LangGraph graphs at load/invoke time. Custom skills are fetched from backend and merged with built-ins. There is no separate named-workflow mechanism any more (the retired `app/workflows/` directory and the `/workflows` API contained hand-written graphs that have been superseded by declarative Skill YAML equivalents); every workflow is now a Skill.
- **State machine only** — this service no longer manages documents, chunking, or embeddings. Retrieval calls backend `/api/retrieval/search` over HTTP (httpx via the shared `AsyncClient`) with the internal token and identity headers.
- LLM goes through LiteLLM (`LLM_BASE_URL`/`LLM_API_KEY`/`LLM_MODEL` envs; `mock-gpt` for keyless testing). Langfuse LangChain callback is toggled by `LANGFUSE_ENABLED`.
- `langfuse.langchain.CallbackHandler` imports `langchain` internally — the full `langchain` package is required, `langchain-core` alone is not enough (already pinned in `pyproject.toml`).
- Host port is `:8001` (container `:8000`) because mem0 occupies host `:8000`.
- Test suite: 800 passed, 1 skipped (PostgreSQL runtime integration is separately covered by the D3 evidence verifier).
- **D3 direct-Agent runtime:** `app/runtime/` executes only Backend-issued immutable snapshots and requires both `AGENT_TEST_RUN_ENABLED=true` and durable PostgreSQL checkpoint configuration; there is no in-memory production fallback. A lease generation maps to a generation-specific LangGraph thread ID, checkpoint references are HMAC-signed, and resume input is accepted only for the exact durable interrupt identity. `waiting_input` releases the Backend lease; restart recovery, deadline, cancellation, output-contract validation, provider usage bounds, rule gates, tool grants, and scoped retrieval all fail closed. Missing provider usage is conservatively charged as serialized input plus the configured maximum output.
- **D4 Harness Graph IR:** `app/orchestration/` is a separate constrained
  compiler contract; do not route it through `app/engine/compiler.py` or reuse
  the Skill YAML schema. Workflow is the sole validator/canonicalizer. Graph
  IR permits only server-owned typed primitives, rejects embedded prompts,
  rules, Skill instructions, Agent bindings, and `latest`, validates topology,
  bounded loops/fan-out/governance/required stages, and keeps compiler-owned
  authority, budget, cleanup, and audit outside editable IR. The D1 default
  Agent-Runtime fixture's nested `bounded_agent_loop.children` is supported as
  a compatibility shape and must remain compiler-valid.
- **Backend HTTP client:** shared `httpx.AsyncClient` singleton (module-level `_client`, lifespan-managed) for all backend callables (`app/backend_http.py`) — avoids per-call TCP/TLS overhead. `get_client()` returns the singleton (lazy-creates if needed), and `aclose_client()` closes it at shutdown; tests using `TestClient` fall back to lazy creation.
- **invoke input filtering:** `_clean_skill_input()` strips ENGINE_KEYS (`fatal_error`, `trace`, `errors`) plus RESERVED_KEYS (identity/immutable/seed) and `__` prefixed keys from the invoke input before building the skill state — prevents callers from injecting forged error frames or bypassing fatal-error short-circuit. Invoke then seeds `tenant_id`/`user_id`/`role` into the state from the caller's real identity headers (the only trusted injection point — ToolContext reads them from state).
- **Script authoring gate (validate-time only):** `POST /skills/validate` passes the caller's role as `author_role`; a non-ADMIN author submitting a definition with script steps gets a `forbidden_script` validation error. `custom.load` and invoke call `validate_source` without `author_role` (gate off) — existing USER+script skills in the DB keep loading and running; `required_role` (who may *invoke*) is untouched.
- **LLM timeouts & retries:** `ChatOpenAI` initialized with explicit `timeout=settings.llm_timeout` (default 60.0s) and `max_retries=settings.llm_max_retries` (default 2) to prevent hung requests from stalling the invoke pipeline; settings are overridable via env.
- **Skill invoke validation (422):** `POST /skills/{name}/invoke` returns `422 {detail: {fieldErrors: {...}}}` on input validation failure; each field error message is human-readable and derived from pydantic validation errors (e.g., `missing` → "為必填", `string_too_short` → "至少需 N 個字"), not raw pydantic error names or documentation URLs. Platform's `MapInvokeErrorAsync` maps this to the camelCase `ApiError` contract.
- **InputField constraints on non-string types:** `int`/`float`/`bool` fields with `min_length` are rejected at skill validate time (invalid schema), preventing dead input fields that would fail every invoke.
- **Skill package import (package.py):** when importing a zip, the parser handles two layouts: (1) classic root-level `SKILL.md` (still supported); (2) new single top-level folder `{name}/SKILL.md` per the standard. If zip root has no `SKILL.md` and all entries share one top-level folder, the parser strips that folder prefix; the prefix name **must equal** the `name` from frontmatter, or import fails with error code `folder_name_mismatch`. Path validation (`.`, `/`, drive, empty, normalization duplicates, symlinks) and all `LIMITS` checks run **after** prefix stripping (prefix itself is validated the same way). **Behavior change:** files under `{name}/scripts/*.py` now enter AST scanning for unsafe patterns (old layout paths outside a top-level `scripts/` folder were treated as lazy resources and skipped). Nested `{name}/sub/SKILL.md` is a read-only resource; only root-level `SKILL.md` is the authoritative definition.
