---
name: contract-change
description: Cross-service contract change procedure — validate all contract sides, sync tests, prevent breaking changes
---

# Contract Change Procedure

When modifying any cross-service contract (API shapes, headers, flows, response formats), use this procedure to prevent silent breaks on the opposite side.

## Steps

1. **Locate the invariant** — find the contract in root [AGENTS.md](../../../AGENTS.md) under "Cross-Service Contracts" or "Agent platform (D1–D7)".

2. **Read the full contract** — follow the link to [docs/cross-service-contracts.md](../../../docs/cross-service-contracts.md) or [docs/agent-platform-contracts.md](../../../docs/agent-platform-contracts.md) and read the relevant section *in full* before touching any code.

3. **Identify all sides** — determine which services/repos this contract touches:
   - Platform (`:8080` gateway, `platform/` .NET)
   - Backend (`:8002` core service, `backend/` .NET)
   - Workflow (`:8001` Python engine, `workflow/`)
   - Frontend (React SPA, `frontend/`)
   - Infra/docs (if configuration or event format changes)

4. **Change both sides together** — do **not** deploy one side first. Make changes to all affected sides in the same PR.

5. **Update all tests** — both sides must have test coverage:
   - `.NET` side: xUnit test files in `*/tests/*.Tests/`
   - Python side: pytest files in `workflow/tests/`
   - Frontend: unit or E2E (Playwright via `e2e/` folder)
   - Backend integration: update `backend/tests/Backend.Api.Tests/RouteSnapshots/backend-api.txt` if routes change

6. **Pre-flight checklist** — verify contract invariants are honored:
   - **ApiError shape & correlationId:** Platform derives ID from `HttpContext.TraceIdentifier`, forwards via `X-Correlation-Id` header; all error responses include matching header + body field (camelCase).
   - **Two SSE formats:** `/api/chat/stream` is `data:<value>` (no space); `/api/copilot/agui` is `data: ` (with space). Both remain unchanged; **never "fix"** one format.
   - **Feature-gate 404s:** disabled flags return identical 404 as nonexistent routes — no "feature disabled" message leaks gate existence.
   - **Field naming duality:** snake_case (documents/workflows/analysis) vs camelCase (auth/config) is **intentional**. Do not unify.
   - **Backend trust boundary:** `X-Internal-Token` on everything except `/health`; identity headers (`X-Tenant-Id`, `X-User-Id`, `X-User-Role`) per-endpoint; handlers call `RequireTenant()`/`RequireUserId()` where needed.

7. **Run full-chain tests** — before declaring complete:
   - Affected `.NET` service: `dotnet test` (all green)
   - Workflow: `uv run pytest` (all green)
   - Frontend/E2E: `npm run build` + Playwright verification if UI touched
   - **e2e-verifier** runs docker compose full-chain smoke

## Real-world example

Changing JWT claim format or adding a new header: find the invariant in root AGENTS.md → read its doc section → update backend's JWT signer + platform's verifier + workflow's header forwarding + frontend's token builder + tests in all four → verify routes snapshot + smoke test auth chain.

## When uncertain

Refer to the appropriate section in [docs/cross-service-contracts.md](../../../docs/cross-service-contracts.md) or [docs/agent-platform-contracts.md](../../../docs/agent-platform-contracts.md); the full contracts are the source of truth.
