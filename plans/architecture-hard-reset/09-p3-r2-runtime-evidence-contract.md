# P3-R2 Runtime Usage Evidence Contract

> Status: DESIGN COMPLETE, EVIDENCE PENDING — revalidated 2026-08-08 against committed `HEAD=e24fe2b` plus the audited dirty worktree. This implementation checkpoint is an audit observation, not release evidence, and does not authorize C8, route removal, schema changes, or production SQL.

> Implementation checkpoint (2026-08-08): bounded counters, fixed-schema JSON events, Compose deployment-version wiring, `scripts/export-artifact-compatibility-usage-v1.py`, and unit coverage are implemented. Production instance-complete log extraction/retention proof, deployment observation, final approval-bundle assembly, external consumer attestation, rollback proof, and approval remain pending.

## 1. Decision boundary

P3-R2 supplies the evidence contract for one possible cleanup only: removing Business Workflow compatibility operations from the public `/api/skills*` surface while retaining Agent Skill behavior. It does not authorize removing either Workflow-internal compatibility surface:

- `POST /skills/validate` remains a same-handler alias of `POST /business-workflows/validate` until its own consumer/usage/rollback gate passes.
- `POST /skills/{name}/invoke` remains unified across Agent Skills and Business Workflows until its own gate passes.

No route may be narrowed merely because repository search finds no caller. The existing `/api/admin/operations/legacy-inventory` is a compile-time inventory and is not runtime evidence.

## 2. Current consumer baseline

The repository still contains active Business Workflow consumers of the public compatibility surface:

- Frontend shared history, restore, run, built-in view, and Copilot `rag-qa` paths use `/api/skills*`.
- Platform `SkillController`, `SkillService`, `WorkflowEngineClient`, and `SkillRoutingAgent` still expose or consume the shared catalog/invoke path.
- Workflow custom-artifact and immutable-revision readers still fetch Backend `/api/skills*` resources.
- `scripts/verify-copilot-shared-core.ps1` invokes a Business Workflow through the public compatibility route.
- Compatibility, dual-track, alias, and unified-invoke tests intentionally pin the current contract.

The detailed file inventory remains in [07-p3-inventory.md](07-p3-inventory.md). External consumers cannot be inferred from this repository; their owners must attest separately.

## 3. Evidence event

Instrumentation records exactly one logical event at the authoritative boundary for each completed or rejected operation:

```text
artifact_compatibility_usage_total{
  service,
  surface,
  operation,
  resolved_artifact_type,
  outcome
}
```

Allowed dimensions are bounded:

| Dimension | Allowed values |
| --- | --- |
| `service` | `backend`, `platform`, `workflow` |
| `surface` | `public_skills`, `public_business_workflows`, `workflow_validate_alias`, `workflow_business_workflows_validate`, `workflow_unified_invoke`, `unknown_origin` |
| `operation` | `list`, `read`, `create`, `update`, `delete`, `import`, `export`, `package`, `revision_read`, `revision_restore`, `execution_artifact`, `validate`, `invoke` |
| `resolved_artifact_type` | `agent_skill`, `business_workflow`, `unknown` |
| `outcome` | `success`, `rejected`, `not_found`, `error` |

Artifact names, tenant IDs, user IDs, correlation IDs, revisions, request paths, and client identifiers must not be metric labels. They belong only in sampled/redacted structured evidence when needed for investigation. The resolved type comes from authoritative stored metadata or the validated package result, never from route name, request source, or YAML/package sniffing.

## 4. Counting authority and deduplication

One user operation produces one count. Internal proxy and validation hops do not count again. A mixed list is the sole set-valued exception: it emits one row for each represented bounded artifact type, so Business Workflow visibility is not collapsed into `unknown`.

| Operation | Counting authority | Rule |
| --- | --- | --- |
| Public create/update/import/restore/delete | Backend | Resolve type first. Count success only after commit; count rejected, not-found, or error once the final result is known, using the resolved type or `unknown`. For delete, resolve type before deletion. |
| Public list/read/export/package/revision/execution-artifact | Backend | Count once after stored metadata resolves the returned or rejected artifact type. A mixed list emits one row per represented bounded artifact type. |
| Public validation | Outermost public service handling that request | Count once and preserve whether the caller used canonical Business Workflow validation or the Skill compatibility surface; downstream validation HTTP calls are dependencies. |
| Public invoke rejected before Workflow | Platform | Count only pre-controller 4xx/5xx that never reach Workflow, with `unknown` type. Once the Platform action is reached, Workflow remains the sole invoke authority. |
| Unified invoke | Workflow | Count once after the loader resolves `agent_skill` versus `business_workflow`. Platform forwards a server-derived, bounded origin (`public_skills` for its public compatibility controller; `workflow_unified_invoke` for chat/internal execution) over the trusted internal hop; it must ignore/replace any caller-supplied origin. Workflow uses that origin as `surface`. Platform and Backend lookup/proxy hops do not count. |
| Chat routing | Workflow invoke boundary | Each actually executed artifact is one invoke. A `kb-query` then `rag-qa` fallback is two invocations, correlated by tracing rather than collapsed. |

Retries count once per logical request where the existing idempotency/correlation contract identifies it. If no logical-attempt identity exists, the report must disclose possible retry inflation rather than inventing deduplication state.

The trusted origin is routing metadata, not identity. It has only the two input values above, is never accepted from the browser/public request, and maps into the existing `surface` dimension rather than adding a metric dimension. Missing or invalid origin is recorded as `unknown_origin` and blocks C8; the public controller must always send `public_skills`. This keeps public compatibility invoke evidence separate without double-counting at Platform or hiding incomplete propagation.

## 5. Evidence bundle

The release owner stores a redacted bundle outside source code and records its immutable digest in the C8 approval record. The bundle contains:

```json
{
  "schemaVersion": 1,
  "deployment": { "environment": "production", "version": "..." },
  "window": { "startUtc": "...", "endUtc": "..." },
  "metricsQuery": "versioned query or dashboard export identifier",
  "counts": [
    {
      "service": "backend",
      "surface": "public_skills",
      "operation": "update",
      "resolvedArtifactType": "business_workflow",
      "outcome": "success",
      "count": 0
    }
  ],
  "repositoryConsumers": [{ "owner": "...", "status": "migrated|retained_without_removed_dependency|blocked", "evidence": "..." }],
  "externalConsumers": [{ "owner": "...", "status": "migrated|none|unknown", "attestedAtUtc": "...", "evidence": "..." }],
  "rollback": { "owner": "...", "deadlineUtc": "...", "procedure": "..." },
  "approval": { "decision": "approved|rejected|pending", "owner": "...", "decidedAtUtc": "...", "evidence": "..." }
}
```

The export must include an explicit zero row for every applicable authoritative `service`/`surface`/`operation`/`resolvedArtifactType`/`outcome` combination. A missing series is not evidence of zero. Any row emitted by a non-authoritative service fails the bundle's deduplication check.

Do not store request or response bodies, package bytes, definitions, prompts, raw JWTs, or tenant/user identifiers in the bundle.

## 6. Observation and approval gates

C8 remains blocked until all conditions hold:

1. Instrumentation has been deployed for the whole declared observation window; partial-window or mixed-version data is rejected.
2. Every Business Workflow write operation under public `/api/skills*` reports zero attempts for every outcome during the approved window. Rejected, not-found, and error attempts still identify an uncut caller and block C8.
3. Read/invoke counts are reported, not silently ignored. Any nonzero count has an identified consumer and migration/retention decision.
4. Every repository consumer above is migrated or explicitly retained only because it does not depend on an operation being removed; automated verification scripts and tests are included.
5. Every known external consumer owner attests; `unknown` external-consumer status or any `unknown_origin` event blocks approval.
6. The rollback owner, procedure, and deployable compatibility build are verified, and the declared rollback window has ended at its recorded deadline.
7. A named human owner records `approved`. Silence, elapsed time, or a zero counter is not approval.

The minimum observation duration is a release decision based on actual caller cadence; this plan does not invent a universal number. The approval record must justify that duration and include at least one complete expected usage cycle. Any instrumentation outage resets the affected portion of the window.

## 7. Implementation posture

Each authoritative counter also emits one redacted, fixed-schema JSON line named `artifact_compatibility_usage_total`. Normal Compose injects one `DEPLOYMENT_VERSION` into the three emitting services; placeholder `development` and `unknown` values cannot be exported. The repository provides no production collector. `scripts/export-artifact-compatibility-usage-v1.py` validates an externally produced source manifest, complete per-instance coverage, deployment-version consistency, bounded dimensions, counting authority, and absence of `unknown_origin`, then emits every applicable zero row. It intentionally uses only the Python standard library.

Current-container snapshots or start/end comparisons are not sufficient evidence: a replica may scale out and disappear again inside the window. Production extraction must therefore come from an orchestrator or centralized-log source that retains the complete instance/lifecycle history and every event line. The release owner must prove that inventory, retention capacity, and absence of a log gap for the full window. Any gap resets the affected observation window.

Do not add a compatibility-usage database table merely to satisfy P3-R2. `operations_release_audit` and `operations_execution_metric` have different schemas and governance meaning and must not be overloaded. If production operations later require durable per-event querying beyond telemetry retention, design a separate bounded ledger as a new decision.

## 8. Exit states

- **DESIGN COMPLETE, EVIDENCE PENDING:** the contract, bounded counting implementation, structured event, and versioned exporter are reviewed, but production extraction/deployment/observation, external attestation, rollback proof, or approval is incomplete.
- **EVIDENCE COMPLETE, APPROVAL PENDING:** a valid bundle exists and all consumer entries are resolved, but no human decision is recorded.
- **C8 APPROVED:** the evidence bundle digest and named approval are recorded after the rollback window ends. This authorizes only the public narrowing described in section 1.
- **REJECTED/BLOCKED:** any unknown consumer, nonzero unowned use, telemetry gap, or missing rollback proof blocks narrowing without changing current routes.
