# Architecture Improvements Continuation — Subagent Runbook

## 1. Model and slot policy

- Terra is the default implementation, test, documentation, and ordinary review model.
- Sol is used only for high-risk independent review: destructive P3 SQL/migration logic, cross-repository transaction or lock ordering, authentication/tenant boundaries, checkpoint/lease fencing, and once-only approval effects.
- Luna selector is unavailable in this environment. Do not plan work that depends on it and do not silently substitute an unrecorded selector.
- There are four total concurrency slots, including the root coordinator. Use at most three subagents at once.
- Follow the repository's existing `.claude/agents/` role routing. Every new or edited agent definition must explicitly pin model, tools, hooks, and MCP access as required by root `AGENTS.md`.

## 2. Four-slot orchestration

| Slot | Default role | Mutation rule |
| --- | --- | --- |
| 1 | Root coordinator | Owns specification, task boundaries, integration, status, and spot checks; avoids competing edits. |
| 2 | Terra implementer A | Owns one service or one disjoint file set. |
| 3 | Terra implementer B | Owns an independent service/file set; never edits Slot 2 files concurrently. |
| 4 | Terra verifier/reviewer, or Sol high-risk reviewer | Review is read-only unless the root explicitly returns a bounded fix task with ownership. |

Parallelism is for independent work, not for multiple agents editing the same shared contract. Cross-service contracts are specified by the root before dispatch, then implemented in disjoint areas.

## 3. Ownership by tranche

| Tranche | Owner A | Owner B | Owner C / reviewer | Files that must not overlap |
| --- | --- | --- | --- | --- |
| Wave4-B paging correction | Workflow Terra | Backend read-only verifier | Terra reviewer | Workflow owns retention paging and tests; Backend cursor contract remains frozen unless a verified defect requires a separate task. |
| Wave5-B1 tracing | Workflow Terra | — | Terra reviewer | Only `workflow/app/tracing.py` and its focused tests. |
| Wave5-B0 calibration | Infra Terra | E2E Terra | Root verifier | Evidence/measurement scripts and plans only; no guessed Compose ceilings. |
| Wave5-B2 telemetry — COMPLETE | Backend Terra | Workflow scope verifier | Terra reviewer | Backend owns the two built-in bounded counters and tests; Workflow confirms existing admission snapshots remain authoritative and adds no adapter without an approved sink/exporter/public endpoint. |
| Wave5-B3 resources | Infra Terra | CI Terra | E2E verifier | Infra owns `infra/docker-compose*.yml` and resource anchors; CI owns expansion assertions; no application edits. |
| Wave6/P3 foundation | Backend Terra | Workflow Terra | Sol reviewer | Backend owns SQL/migration/schema/repositories; Workflow owns split runtime contracts; no Platform/Frontend edits yet. |
| Wave6/P3 consumers | Platform Terra | Frontend Terra | Backend/Workflow verifier | Platform and Frontend consume frozen contracts; schema/runtime owners are read-only during this step. |
| Final closure | Docs Terra | E2E Terra | Sol only for residual high-risk review | Docs owner edits docs only; E2E owner edits no product files unless given a separate repair task. |

If a file is already modified by another wave, the root either assigns that whole file to one owner or pauses one task. Agents must not resolve overlap with checkout/reset or by accepting whichever version was written last.

## 4. Dispatch packet

Every delegated task includes:

1. Exact objective and non-goals.
2. Allowed directories and explicit forbidden files.
3. Current dirty-worktree warning.
4. Contract references and acceptance IDs.
5. Required focused/full commands and failure injection.
6. Dead/duplicated-code inventory obligation.
7. `no commit` unless the user explicitly requests a commit.

## 5. Completion-report audit

The root spot-checks, rather than trusting, claims that:

- locks or transactions are safe;
- code is dead or has no callers;
- temporary files are removed or the worktree is clean;
- tests actually exercised PostgreSQL/Docker rather than skipping;
- Compose values are effective after interpolation/merge;
- warnings and metrics contain no prompts, payloads, identity, IDs, tokens, or exception text.

Use literal search for exact symbols and routes. Use the repository's semantic-search mechanism for behavioral callers. A reviewer finding reopens the owning tranche; it is not appended as undocumented cleanup.
