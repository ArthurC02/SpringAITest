# Context Enrichment Contracts (E1/E3)

This document complements the D5 Root Orchestrator contracts in
[agent-platform-contracts.md](agent-platform-contracts.md). It is the shared
contract for server-owned context revisions (E1) and child task-local context
requests/deltas (E3).

## Rollout and authority

`CONTEXT_ENRICHMENT_ENABLED` defaults to `false`. Each service must fail closed:
the Backend and Platform effective gate also requires their effective D5
`MULTI_AGENT_DISPATCH_ENABLED` gate. When Backend enrichment is off,
`/api/contexts`, `/api/context-views`, `/api/context-policies`, and child
`context-requests` routes are undiscoverable (`404`).

Rollback is configuration-only: with either gate off, D5
`/api/orchestrator-runs/{runId}/context/acquire` behaves bit-identically to the
pre-E1 contract — Backend's `current_context.user_input` clarification
short-circuit (echoing `ready:true`) and Workflow's local
`ProductionRootPlanner` acquirer (clarification assessment plus scoped
retrieval) both remain in force. With both gates on, clarification goes
through a context revision instead: `user_input` no longer short-circuits and
acquire reports `missing:["clarification-revision-required"]` until a READY
revision exists.

Backend is the sole authority for a context's tenant ownership, root/run
association, canonical revision bytes and hash, readiness, policy, evidence,
source/adapter pins, and role views. Workflow can submit content but never
wire-supplied identity, ownership, root/child/task lineage, or role. Every
internal call requires the normal internal token and scoped identity headers.

## E1 revisions, policy, and evidence

The canonical `context_revision` record has one of these statuses:

- `NEED_MORE_CONTEXT`
- `NEEDS_CLARIFICATION`
- `BLOCKED_BY_POLICY`
- `INSUFFICIENT_DATA`
- `READY`
- `READY_WITH_ASSUMPTIONS`

Only `READY` and `READY_WITH_ASSUMPTIONS` are acquire-ready.
`BLOCKED_BY_POLICY` and `INSUFFICIENT_DATA` are terminal: the Workflow
acquirer reports them with `terminal:true` and distinct missing codes
(`context-blocked-by-policy` / `context-insufficient-data`), and the root run
fails instead of retrying or waiting for input. Readiness is computed from the
active Backend policy and objective measurements; callers do not choose it.
`READY_WITH_ASSUMPTIONS` is granted only when the envelope's assumptions
section is non-empty, matches `assumptions_count`, and appears in the ready
view. Policies and the source catalogue are tenant-scoped, server-owned
records; policy `values` enforce a top-level key allowlist (`readiness`,
`bootstrap_requirements`, `source_requirements`, `source_precedence`) and at
most one active policy per tenant. The selected source and adapter are pinned
on the revision, so a later catalogue change cannot reinterpret already
persisted context.

Evidence is provenance, not caller commentary. Document evidence must resolve
to a same-tenant, exact document chunk using
`document://<document-id>#chunk/<chunk-id>` and its matching content hash.
Unresolvable, cross-tenant, or hash-mismatched evidence fails closed. Context
views are derived server records, including worker and verifier projections;
do not substitute an arbitrary view or `context_ref` from the wire.

The internal E1 API is:

- `POST /api/contexts/{contextId}/revisions`
- `GET /api/contexts/{contextId}/revisions/{revision}`
- `GET /api/context-views/{viewId}`
- `GET /api/context-policies`

Revision responses use snake_case. The revision ETag is the revision number
(returned by both revision and view reads); it is not the E3 request version
described below. `POST .../revisions` requires both `X-Tenant-Id` and
`X-User-Id` — the root run is owner-scoped, so a same-tenant different user
cannot attach revisions to it. Error classes are uniform across the revision,
policy, and delta endpoints: an invalid candidate envelope or measurement is
`400`, a missing active policy is `503`, and an invalid policy is `422`.

## E3 child context requests and deltas

The Backend creates or returns one durable request for an authorized child:

- `POST /api/orchestrator-runs/{runId}/children/{childId}/context-requests`
- `GET /api/orchestrator-runs/{runId}/children/{childId}/context-requests/{requestId}`
- `POST /api/orchestrator-runs/{runId}/children/{childId}/context-requests/{requestId}/deltas`

A request response includes its ID, root run, child, immutable task ID, derived
role, task-local context ID, base/current `context_ref`, and `version`. The
role is derived only from the immutable child execution kind: an
`orchestrator-worker` gets the worker projection and an
`orchestrator-verifier` gets the verifier projection. A child delta therefore
updates only its task-local context lineage; it never becomes the root's latest
ready context.

The delta body accepts context content (`definition`, `evidence`, `views`,
measurements, and times) only. It deliberately has no identity, root, child,
task, role, context ID, or context-reference field. The Backend derives those
values and applies the same E1 canonicalization, policy, evidence validation,
source selection, and role-view construction as a direct revision.

`If-Match` is mandatory on a delta. A missing header is `428`, an invalid
header is `400`, and a stale request version is `409` with
`fieldErrors.If-Match`. On success, the response is the new context revision
and its ETag is the new *request* version. Do not use the context revision
number as a delta concurrency token. An unavailable active policy is `503` for
a delta; an invalid policy is `422`.

All request lookups and writes are owner-scoped through the root and child;
unknown or unauthorized resources return the normal `404` without revealing
their existence.

## Durability and observability

For a successful E3 append, Backend commits the canonical revision, request
version compare-and-swap, append-only `context_delta` record, current context
reference, and root event in one transaction. A failed transaction leaves no
orphan revision, delta, or event.

Context events use the existing `orchestrator_run_event` cursor and types
`context.requested` and `context.delta_applied`; there is no separate context
event table or SSE stream. Public event payloads are producer-redacted and
contain only lifecycle identifiers and safe status/readiness metadata, never
context definitions, evidence bodies, or source contents.
