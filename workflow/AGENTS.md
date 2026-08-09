# Workflow Service (Python 3.12+ LangGraph + FastAPI, host `:8001` → container `:8000`)

Area-specific guidance. Cross-service contracts (X-Internal-Token + identity headers, ApiError) live as invariants in the repo-root [AGENTS.md](../AGENTS.md) with full detail in [docs/cross-service-contracts.md](../docs/cross-service-contracts.md).

## Layout

- `app/engine/` — **Skill Engine** (Node-first): compiles **Business Workflows** (declarative YAML) to LangGraph graphs and governs their nodes (`node_registry.py` `@node` contracts, `skill.py` YAML schema/static validation, `compiler.py` graph/cache/recursion compilation). `agent_skill_graph.py` is the separate graph factory used only to preserve the unified direct-invoke bridge for an **Agent Skill** package; `node_shell.py` is the per-node execution shell, not the Harness. **Reads enforcement:** Node Shell filters state for declared `reads` at invoke time (explicit reads are required to view state keys; tool/script/anonymous steps receive their defined compatibility view; agentic runner receives identity keys + reads + input schema fields).
- `app/runtime/flow_harness.py` — unified Business Workflow execution and governance module shared by the fixed Harness and standalone invoke; both paths emit `workflow_completed`, and there is no separate legacy-flow module or event (execution/governance details in the Gotchas below).
- `app/skills/` — Business Workflow definitions: built-in YAML workflows plus `template-*.yaml` skeletons; `custom.py` obtains persisted artifacts and explicitly dispatches by backend `kind`; `deps.py` (`KbQueryDeps` + `_default_deps()`) is the DI assembly point wiring node families to concrete `llm`/retrieval/audit adapters. **Layering:** engine core (`app/engine/`) never imports `app/nodes/**`; `app/skills/` is the only layer allowed to do that wiring. Agent Skills are `SKILL.md` packages and never become YAML merely to be classified.
- `app/nodes/` + `app/nodes/kbquery/` + `app/tools.py` — reusable graph nodes and built-in tools. `retrieve` and kbquery's vector-search port do tenant-filtered vector search **via backend HTTP** (shared callables in `app/backend_http.py`), not a local vector store. kbquery's evidence-verification gate: no substantive answer unless verification PASSes, retries bounded by `KB_QUERY_MAX_RETRIEVAL_ATTEMPTS`; keyword/metadata/table/structured searchers, external reranker, and the audit DB are behind ports, not yet connected. `app/skills/kb-query.yaml` is the sole `kb-query` graph (the hand-written LangGraph version and the standalone `/workflows` API it powered were retired once the YAML graph reached parity — Node-First migration Phase 3a).
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
- `GET /skills` — unified catalog of Agent Skills and Business Workflows with trusted `kind`, schemas, and fail-closed `bindable`; builtin is false, custom is true only with a positive persisted `current_revision`.
- `POST /business-workflows/validate` — validate a Business Workflow YAML definition (syntax + schema check). `POST /skills/validate` is the same-handler compatibility alias; P5/C8 do not remove it, and retirement requires a separate consumer inventory, usage-zero, and rollback gate.
- `POST /skills/{name}/invoke` — unified execution of either stored artifact kind; Business Workflows run through the lightweight flow governance wrapper, while Agent Skills keep their separate graph bridge. Returns `{skill, output}`.
- P3-R2 evidence counts direct validation only when it is not a marked dependency hop, counts unified invoke exactly once after trusted kind resolution, and emits the fixed-schema JSON evidence line; missing/invalid origin is `unknown_origin`, not silently treated as internal traffic. See the [runtime evidence contract](../plans/architecture-hard-reset/09-p3-r2-runtime-evidence-contract.md).

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

Eval Runner endpoint (Phase E2; internal token plus identity headers required,
gated by `RUN_EVAL_ENABLED` — same `FeatureGateMiddleware` fail-closed-404
pattern as `/agent-runs/` and `/orchestrator-runs`):

- `POST /evals/run` executes one versioned eval suite (Backend-supplied, not
  persisted here) against a `kind="skill"` candidate using deterministic
  fixtures (`app.evals.fixtures`); returns per-case PASS/FAIL/ERROR verdicts,
  `canonical_identity`, and metrics. The service is stateless for eval: no
  suite/release state is persisted and no backend HTTP is called during a run.

## Gotchas

- **Canonical JSON & UTF-16 ordering** (`app/canonical_json.py`): uses UTF-16 ordinal key sorting (matching .NET `StringComparer.Ordinal`). Always use this module, not `json.dumps(sort_keys=True)` (wrong ordering for non-BMP keys). `app/runtime/bounded_json.py` truncates JSON for hashing.
- **Skill Engine + node registry:** nodes are first-class via `@node`; Business Workflows are declarative YAML compiled at load/invoke; Agent Skills are `SKILL.md` packages (trusted `kind`, not YAML-sniffed). `app/runtime/graph.py` is full Harness; `flow_harness.py` supplies execution core + standalone governance; `node_shell.py` is node-level governance. D4 Graph IR must never route through Business Workflow compiler.
- **State machine only** — this service no longer manages documents, chunking, or embeddings. Retrieval calls backend `/api/retrieval/search` over HTTP (httpx via the shared `AsyncClient`) with the internal token and identity headers.
- LLM goes through LiteLLM (`LLM_BASE_URL`/`LLM_API_KEY`/`LLM_MODEL` envs; `mock-gpt` for keyless testing). Langfuse LangChain callback is toggled by `LANGFUSE_ENABLED`: when false, tracing is completely silent; when enabled, handler initialization failure safely falls back without tracing, emits at most one fixed content-free warning per process, and increments a process-local failure counter. That counter is not exported to Prometheus or OTel.
- `langfuse.langchain.CallbackHandler` imports `langchain` internally — the full `langchain` package is required, `langchain-core` alone is not enough (already pinned in `pyproject.toml`).
- Host port is `:8001` (container `:8000`) because mem0 occupies host `:8000`.
- PostgreSQL runtime behavior is also covered by the dedicated evidence verifiers.
- **PostgreSQL checkpoint tests are skipped by default:** `test_agent_runtime_postgres.py` and `test_root_orchestrator_supervisor.py::test_root_checkpoint_round_trips_through_real_postgres_when_configured` skip unless `CHECKPOINT_DATABASE_URL` is set. CI never sets it, so these tests are never executed in the pipeline. Before touching `app/runtime/checkpoints.py`, run by hand: `CHECKPOINT_DATABASE_URL=postgresql://postgres:postgres@127.0.0.1:5433/springaitest uv run pytest tests/test_agent_runtime_postgres.py tests/test_root_orchestrator_supervisor.py`.
- **D3 direct-Agent runtime:** requires `AGENT_TEST_RUN_ENABLED=true` + durable PostgreSQL checkpoint (no in-memory fallback). Lease generation maps to thread ID; checkpoint refs HMAC-signed; resume only for exact durable interrupt identity. Restart recovery, deadline, cancel, output-contract, usage bounds, rules, tools, retrieval all fail closed. Missing usage charged conservatively (serialized input + max output).
- **D5/D6 runtime boundary:** Workflow has no `AGENT_CHAT_ENABLED` and no public chat-dispatch. Executes only Backend-issued Root commands behind `MULTI_AGENT_DISPATCH_ENABLED`; Backend's D6 allocation + Platform's canary decide. Once accepted, same claim/lease/checkpoint/transition/recovery/redaction apply; failed Platform kick cannot disappear durable Backend command.
- **Context Enrichment (E1/E3):** `CONTEXT_ENRICHMENT_ENABLED` defaults false and cannot activate a second runtime path by itself; it is meaningful only with D5. Workflow treats Backend context revisions and task-local request metadata as authority: it can submit a content delta but must not originate tenant/user ownership, root/child/task lineage, or worker/verifier role. Use the role-specific projected `context_ref`, preserve source/evidence pins, and treat the request ETag (not the context revision number) as the delta concurrency token. See [context-enrichment-contracts.md](../docs/context-enrichment-contracts.md).
- **D7 approved writes:** only `runtime.write_evidence` writable (tool + tenant allowlists); write gate creates `waiting_approval` (generic resume never resolves it). Workflow claims execution, consumes one-time approval lease, atomically sends evidence through Backend boundary. No process-local effect ledger; no public approval/claim/recovery/outbox APIs. Crashed work recovered via Backend claims; duplicate/stale fail closed.
- **Error handling and correlation tracking:** unexpected exceptions return fixed safe message + `correlation_id` (snake_case); `app/correlation.py` accepts/validates inbound `X-Correlation-Id` or generates `uuid4`; exceptions logged once with ID. Agentic skill 200 responses filtered by `PUBLIC_DENY_KEYS` (no `fatal_error`/`errors`/`audit_trail`); `trace` public. Filtering only on success, not errors.
- **P1 prompt manifest:** `PROMPT_ARTIFACTS_ENABLED` (default false) lets Backend-issued pin replace constants SYSTEM GOVERNANCE section. While enabled + pinned, any resolution failure raises `PromptManifestUnavailable` → fail-closed. `PROMPT_ARTIFACTS_SHADOW` (observe-only) falls back to constants on failure (log warning, set `audit["prompt_manifest_resolved"]=False`); still sends constants to provider, only compares hashes. Cache keyed `(tenant, revision)` for isolation.
- **D4 Harness Graph IR:** `app/orchestration/` is separate constrained compiler (never route through `app/engine/compiler.py`). Workflow sole validator/canonicalizer. IR permits only server-owned typed primitives; rejects embedded prompts/rules/instructions/bindings/`latest`. Validates topology, loops, fan-out, governance, stages; keeps compiler authority outside editable IR. Default Agent-Runtime fixture's `bounded_agent_loop.children` must remain compiler-valid.
- **Backend HTTP client:** shared `httpx.AsyncClient` singleton (module-level `_client`, lifespan-managed) for all backend callables (`app/backend_http.py`) — avoids per-call TCP/TLS overhead. `get_client()` returns the singleton (lazy-creates if needed), and `aclose_client()` closes it at shutdown; tests using `TestClient` fall back to lazy creation.
- **invoke input filtering:** `_clean_skill_input()` strips ENGINE_KEYS + RESERVED_KEYS + `__`-prefixed keys before state build (prevents injection of forged errors/bypass). Invoke then seeds `tenant_id`/`user_id`/`role` from identity headers (only trusted injection point).
- **Skill script deployment:** `APP_ENVIRONMENT` defaults to `Production`. With `ISOLATED_SKILL_SCRIPTS_ENABLED=true`, scripts use `IsolatedSubprocessRunner`; off, only `Development` uses `RestrictedInProcessRunner`, Production uses `DisabledScriptRunner`. Child: `-I -X utf8`, credential-free env, OS limits, parent-brokered calls; fails closed on unavailable limits. No production in-process fallback.
- **Workflow request bounds:** Business Rules requests are capped at 1 MiB before evaluation, including chunked bodies. Agent Skill multipart validation streams the package with a raw-byte cap of 6 MiB before complete spooling, rejects duplicate/unexpected/incomplete parts, and cleans temporary files even on disconnect/cancellation; ZIP entry/count/path/expanded-size limits still apply after transport validation.
- **Credential startup gate:** outside Development, `INTERNAL_API_TOKEN` must be nonblank and differ from the committed development token. Enabling D3/D5/D7 runtime paths also requires a non-development `CHECKPOINT_HMAC_KEY`; invalid production configuration fails during settings initialization rather than weakening execution or checkpoint integrity.
- **Script authoring gate:** `POST /skills/validate` passes caller role as `author_role`; non-ADMIN + script steps → `forbidden_script` error. `custom.load` and invoke call `validate_source` without `author_role` (gate off) — existing USER+script skills keep running.
- **LLM timeouts & retries:** `ChatOpenAI` with explicit `timeout` (default 60.0s) + `max_retries` (default 2) to prevent stalling; both overridable via env.
- **Skill invoke validation (422):** `POST /skills/{name}/invoke` returns `422 {detail: {fieldErrors}}` on validation failure; field messages human-readable (derived from pydantic, not raw names). Platform maps to camelCase `ApiError`.
- **InputField constraints on non-strings:** `int`/`float`/`bool` + `min_length` rejected at validate (invalid schema).
- **Skill package import:** parser handles two layouts: (1) root-level `SKILL.md`; (2) `{name}/SKILL.md` (standard). Auto-strips single top-level folder (prefix name must equal frontmatter `name`, or fail with `folder_name_mismatch`). Path/LIMITS validation runs after prefix-strip. Files under `{name}/scripts/*.py` enter AST unsafe-pattern scanning. Nested `{name}/sub/SKILL.md` is read-only resource only.
- **E2 eval runner:** `POST /evals/run` reuses compiler+harness, compiles with `cache=False` (every case builds fresh `KbQueryDeps`). `EvalSuite.cases` capped at 200; each case bounded by `workflow_timeout_seconds` via `asyncio.wait_for` (timeout → ERROR verdict, not 504). `candidate.pins` normalized to `{}` for identity (omitted/`null`/`{}` same). Eval fixtures use `KbQueryDeps` defaults (not settings), no tenant config applied — eval PASS ≠ deployed PASS.
