# Workflow Service (Python 3.12+ LangGraph + FastAPI, host `:8001` → container `:8000`)

Area-specific guidance. Cross-service contracts (X-Internal-Token + identity headers, ApiError) live in the repo-root [AGENTS.md](../AGENTS.md).

## Layout

- `app/engine/` — **Skill Engine** (Node-first): it compiles **Business Workflows** (declarative YAML) to LangGraph graphs and governs their nodes. `node_registry.py` provides `@node` contracts; `skill.py` owns YAML schema/static validation; `compiler.py` owns graph/cache/recursion compilation; `agent_skill_graph.py` is the separate graph factory used only to preserve the unified direct-invoke bridge for an **Agent Skill** package; `node_shell.py` is the per-node execution shell, not the Harness. `expressions.py`, `script_runner.py`, `script_isolation.py`/`script_child.py`, and `tool_registry.py` retain their existing responsibilities. **Reads enforcement:** Node Shell filters state for declared `reads` at invoke time (explicit reads are required to view state keys; tool/script/anonymous steps receive their defined compatibility view; agentic runner receives identity keys + reads + input schema fields).
- `app/runtime/flow_harness.py` — unified Business Workflow execution and governance module shared by the fixed Harness and standalone invoke. Harness-internal flows support registered nodes plus script/tool steps under Node Shell, per-step budget, isolated-script, and effective-tool controls. Standalone flow invoke uses its lightweight preflight/budget/finalize wrapper. Both paths emit `workflow_completed`; there is no separate legacy-flow module or event.
- `app/skills/` — Business Workflow definitions: five built-in YAML workflows (`kb-query.yaml`, `rag-qa.yaml`, `summarize.yaml`, `triage.yaml`, `analyze-report.yaml` — the last is ADMIN-only) plus five `template-*.yaml` skeletons; `deps.py` (`KbQueryDeps` + `_default_deps()`, DI assembly point); and `custom.py`, which obtains persisted artifacts and explicitly dispatches by backend `kind`. Agent Skills are `SKILL.md` packages and never become YAML merely to be classified.
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

- **Canonical JSON & UTF-16 ordering** (`app/canonical_json.py`): centralized canonical JSON serialization (UTF-16 ordinal sorting of keys, matching .NET `StringComparer.Ordinal`) used by `app/runtime/models.py` and `app/orchestration/canonical.py`. **Always use this module for canonical JSON calculations — do not use `json.dumps(sort_keys=True)`, which produces different ordering for non-BMP character keys.** `app/runtime/bounded_json.py` is a shared bounded JSON truncation tool used by `flow_harness` and `tool_boundary`.
- **Skill Engine + node registry** — nodes are first-class citizens via `@node`. Business Workflows are declarative YAML compiled to LangGraph at load/invoke time; Agent Skills are `SKILL.md` packages dispatched by trusted persisted `kind`, not YAML-sniffed. The fixed `app/runtime/graph.py` is the full Harness; `app/runtime/flow_harness.py` supplies the shared flow execution core and the lightweight standalone governance wrapper; `engine/node_shell.py` remains the node-level governance shell. Harness-internal flows may use any registered node and governed script/tool steps, with tool calls restricted to the effective tool set. The D4 Graph IR is a Harness Workflow and must not be routed through the Business Workflow compiler.
- **State machine only** — this service no longer manages documents, chunking, or embeddings. Retrieval calls backend `/api/retrieval/search` over HTTP (httpx via the shared `AsyncClient`) with the internal token and identity headers.
- LLM goes through LiteLLM (`LLM_BASE_URL`/`LLM_API_KEY`/`LLM_MODEL` envs; `mock-gpt` for keyless testing). Langfuse LangChain callback is toggled by `LANGFUSE_ENABLED`: when false, tracing is completely silent; when enabled, handler initialization failure safely falls back without tracing, emits at most one fixed content-free warning per process, and increments a process-local failure counter. That counter is not exported to Prometheus or OTel.
- `langfuse.langchain.CallbackHandler` imports `langchain` internally — the full `langchain` package is required, `langchain-core` alone is not enough (already pinned in `pyproject.toml`).
- Host port is `:8001` (container `:8000`) because mem0 occupies host `:8000`.
- PostgreSQL runtime behavior is also covered by the dedicated evidence verifiers.
- **The default skips hide the only production checkpointer.** `tests/test_agent_runtime_postgres.py` (2 tests) and `test_root_orchestrator_supervisor.py::test_root_checkpoint_round_trips_through_real_postgres_when_configured` call `pytest.skip` unless `CHECKPOINT_DATABASE_URL` is set. What a plain `uv run pytest` therefore never executes: `PostgresCheckpointStore.open()`/`setup()` and its pool arguments, `AsyncPostgresSaver` compatibility (interrupt → reopen → resume across two saver instances), and the HMAC signature + payload-digest verification in `put/get_root_context_checkpoint`. CI runs pytest but does not set `CHECKPOINT_DATABASE_URL`, so these PostgreSQL-dependent tests skip in the pipeline — run them by hand against a real PostgreSQL before touching `app/runtime/checkpoints.py`: `CHECKPOINT_DATABASE_URL=postgresql://postgres:postgres@127.0.0.1:5433/springaitest uv run pytest tests/test_agent_runtime_postgres.py tests/test_root_orchestrator_supervisor.py`.
- **D3 direct-Agent runtime:** `app/runtime/` executes only Backend-issued immutable snapshots and requires both `AGENT_TEST_RUN_ENABLED=true` and durable PostgreSQL checkpoint configuration; there is no in-memory production fallback. A lease generation maps to a generation-specific LangGraph thread ID, checkpoint references are HMAC-signed, and resume input is accepted only for the exact durable interrupt identity. `waiting_input` releases the Backend lease; restart recovery, deadline, cancellation, output-contract validation, provider usage bounds, rule gates, tool grants, and scoped retrieval all fail closed. Missing provider usage is conservatively charged as serialized input plus the configured maximum output.
- **D5/D6 runtime boundary:** Workflow has no `AGENT_CHAT_ENABLED` setting and exposes no public chat-dispatch endpoint. It executes only Backend-issued Root commands behind `MULTI_AGENT_DISPATCH_ENABLED`; Backend's D6 allocation and Platform's public canary decide whether a chat becomes such a command. Once accepted, the same claim, lease-generation fencing, checkpoint, transition, recovery, and redaction contracts apply; a failed Platform kick cannot make a durable Backend command disappear.
- **Context Enrichment (E1/E3):** `CONTEXT_ENRICHMENT_ENABLED` defaults false and cannot activate a second runtime path by itself; it is meaningful only with D5. Workflow treats Backend context revisions and task-local request metadata as authority: it can submit a content delta but must not originate tenant/user ownership, root/child/task lineage, or worker/verifier role. Use the role-specific projected `context_ref`, preserve source/evidence pins, and treat the request ETag (not the context revision number) as the delta concurrency token. See [context-enrichment-contracts.md](../docs/context-enrichment-contracts.md).
- **D7 approved writes:** `AGENT_WRITE_TOOLS_ENABLED` defaults false. The only shipped write path is `runtime.write_evidence`, and it remains blocked unless the tool is in the immutable effective allowlist and its tenant is server-allowlisted. A write gate creates `waiting_approval`; generic resume must never resolve it. After Backend records a decision, Workflow claims execution, reads the Backend-issued execution identity, atomically consumes the one-time approval/effect lease, then sends the evidence through Backend's transactional evidence/outbox boundary. Workflow has no process-local effect ledger and must not expose approval consume, execution claim, recovery, or outbox APIs publicly. Crashed approved work is recovered through Backend execution claims; duplicate/replayed approvals or stale leases fail closed.
- **Error handling and correlation tracking:** Unexpected exceptions at any invoke layer return a fixed safe message (never exception details to the caller) plus `correlation_id` (snake_case) and `X-Correlation-Id` response header. The `app/correlation.py` ASGI middleware accepts valid inbound `X-Correlation-Id` headers or generates `uuid4` if absent; original exceptions are logged exactly once with the correlation ID for debugging. Agentic skill invoke 200 responses are filtered by `PUBLIC_DENY_KEYS` (masking `fatal_error` text content, stripping `errors` and `audit_trail` arrays entirely); `trace` remains public as a contract. This filtering applies only to 200 successful invokes, not error frames.
- **P1 prompt manifest (`app/runtime/prompt_manifest.py`):** `PROMPT_ARTIFACTS_ENABLED` (default false) lets a Backend-issued `agent.prompt_manifest` pin replace the constants SYSTEM GOVERNANCE section that `app/runtime/model.py::_system_frame` assembles. While the flag is on and the snapshot carries a pin, any resolution failure (backend unreachable/404, revision/SHA/schema mismatch, missing governance component) raises `PromptManifestUnavailable`, which the model step in `app/runtime/graph.py` maps to `error_code=prompt_manifest_unavailable` and fails the run closed — never a silent fallback to constants. `PROMPT_ARTIFACTS_SHADOW` (default false, meaningful only with the enabled flag on) is the deliberate exception to that rule: it is pure observe-only, so a manifest that cannot be resolved degrades to the constants frame instead of failing the run, logging one warning (hashes/pin identifiers only, never prompt text) and setting `audit["prompt_manifest_resolved"] = False`; when the manifest *does* resolve, shadow still sends the constants version to the provider and only compares/logs the two composition hashes. Only `shadow=false` (the default posture once the enabled flag is on) keeps the strict fail-closed guarantee. The resolved-manifest cache is keyed `(tenant, revision)` — revisions are immutable so there is no invalidation, but tenant is part of the key for isolation, not performance.
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
- **Skill script deployment policy (Phase S1):** `APP_ENVIRONMENT` defaults to `Production`, never implicit local development. With `ISOLATED_SKILL_SCRIPTS_ENABLED=true`, scripts use the short-lived `IsolatedSubprocessRunner`; with isolation off, only explicit `Development` may use `RestrictedInProcessRunner`, while Production installs `DisabledScriptRunner` and rejects script steps. Compiler fallbacks, default dependency wiring, and eval fixtures all resolve through this same policy. The child starts with `-I -X utf8`, a rebuilt credential-free environment, OS resource/process limits, and parent-brokered tool calls; if trustworthy OS limits are unavailable it fails closed. Tool grants/risk/call and result budgets plus returned writes are revalidated in the parent. Keep the blocking worker-thread transport and child-reported PID semantics documented in `script_runner.py`; do not introduce a production in-process fallback.
- **Workflow request bounds:** Business Rules requests are capped at 1 MiB before evaluation, including chunked bodies. Agent Skill multipart validation streams the package with a raw-byte cap of 6 MiB before complete spooling, rejects duplicate/unexpected/incomplete parts, and cleans temporary files even on disconnect/cancellation; ZIP entry/count/path/expanded-size limits still apply after transport validation.
- **Credential startup gate:** outside Development, `INTERNAL_API_TOKEN` must be nonblank and differ from the committed development token. Enabling D3/D5/D7 runtime paths also requires a non-development `CHECKPOINT_HMAC_KEY`; invalid production configuration fails during settings initialization rather than weakening execution or checkpoint integrity.
- **Script authoring gate (validate-time only):** `POST /skills/validate` passes the caller's role as `author_role`; a non-ADMIN author submitting a definition with script steps gets a `forbidden_script` validation error. `custom.load` and invoke call `validate_source` without `author_role` (gate off) — existing USER+script skills in the DB keep loading and running; `required_role` (who may *invoke*) is untouched.
- **LLM timeouts & retries:** `ChatOpenAI` initialized with explicit `timeout=settings.llm_timeout` (default 60.0s) and `max_retries=settings.llm_max_retries` (default 2) to prevent hung requests from stalling the invoke pipeline; settings are overridable via env.
- **Skill invoke validation (422):** `POST /skills/{name}/invoke` returns `422 {detail: {fieldErrors: {...}}}` on input validation failure; each field error message is human-readable and derived from pydantic validation errors (e.g., `missing` → "為必填", `string_too_short` → "至少需 N 個字"), not raw pydantic error names or documentation URLs. Platform's `MapInvokeErrorAsync` maps this to the camelCase `ApiError` contract.
- **InputField constraints on non-string types:** `int`/`float`/`bool` fields with `min_length` are rejected at skill validate time (invalid schema), preventing dead input fields that would fail every invoke.
- **Skill package import (package.py):** when importing a zip, the parser handles two layouts: (1) classic root-level `SKILL.md` (still supported); (2) new single top-level folder `{name}/SKILL.md` per the standard. If zip root has no `SKILL.md` and all entries share one top-level folder, the parser strips that folder prefix; the prefix name **must equal** the `name` from frontmatter, or import fails with error code `folder_name_mismatch`. Path validation (`.`, `/`, drive, empty, normalization duplicates, symlinks) and all `LIMITS` checks run **after** prefix stripping (prefix itself is validated the same way). **Behavior change:** files under `{name}/scripts/*.py` now enter AST scanning for unsafe patterns (old layout paths outside a top-level `scripts/` folder were treated as lazy resources and skipped). Nested `{name}/sub/SKILL.md` is a read-only resource; only root-level `SKILL.md` is the authoritative definition.
- **E2 eval runner:** `POST /evals/run` reuses the same compiler+harness path as `/skills/{name}/invoke` but compiles with `compiler.compile(skill, deps, cache=False)` — every case builds its own `KbQueryDeps` via `app.evals.fixtures.build_fixture_deps`, so caching by `id(deps)` would only evict the production compile cache (32-slot FIFO). `EvalSuite.cases` is capped at 200; each case's `graph.ainvoke` is bounded by the same `settings.workflow_timeout_seconds` budget as invoke, applied via `asyncio.wait_for` rather than the `_run_with_timeout` HTTP helper (a per-case timeout must become an `ERROR` verdict and let the suite continue, not a 504). `candidate.pins` accepts `null` (Backend's System.Text.Json Web defaults do not omit null fields) and is normalized to `{}` before folding into `canonical_identity`, so omitted/`null`/`{}` all produce the same identity. Eval fixtures deliberately diverge from deployed defaults — `default_top_k`/`max_retrieval_attempts` use the `KbQueryDeps` dataclass defaults, not `settings.kb_query_top_k`/`settings.kb_query_max_retrieval_attempts`, and no tenant Configuration Set (`app.skills.config_apply.resolve`) is applied — so an eval PASS is not the same claim as a PASS under the tenant's deployed configuration.
