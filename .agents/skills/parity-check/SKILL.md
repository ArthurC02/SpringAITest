---
name: parity-check
description: Dapper ↔ InMemory repository parity verification — ensure both implementations honor the same semantics
---

# Dapper ↔ InMemory Parity Checklist

When modifying any Backend repository (Dapper SQL or in-memory), both implementations must behave identically. Drift is invisible — lite/test suites can be all-green while production behaves differently, or vice versa.

## InMemory Implementation Locations

InMemory repositories live in **three places** — do not assume `Data/InMemory/` is complete:

1. `src/Backend.Api/Data/InMemory/` — centralized in-memory base classes
2. Feature-folder-paired implementations (e.g., `src/Backend.Api/Agents/Data/InMemoryAgentRepository.cs`)
3. Colocated Dapper implementations (same folder as Dapper)

**Before modifying either side, grep all three locations** to find every InMemory implementation of the interface you're changing.

## Verification Steps

1. **Identify the interface** — find the repository interface (e.g., `IAgentRunRepository`).

2. **Locate all implementations** — grep for the interface name in `**/*InMemory*.cs` and `**/*Repository.cs` files.

3. **Compare state-set semantics:**
   - **Filter conditions** — both filter by the same criteria (tenant, status, role, date ranges)
   - **Ordering** — same sort order (by ID, creation time, status precedence)
   - **Null vs. empty** — both return empty collections, not null (or both null if designed that way)
   - **Tenant isolation** — both enforce tenant boundaries identically
   - **ETag/version increment** — both increment on write, both reject stale updates (409)

4. **Check atomicity & transactions:**
   - **Dapper:** multi-statement transactions are all-or-nothing (failure rolls back entire transaction)
   - **InMemory:** if partial success leaves a terminal state, later retries skip the record forever (asymmetry)
   - **Both must match:** Dapper's rollback semantics require InMemory to also revert on partial failure, not commit partial state

5. **Verify cascade integrity:**
   - **Parent delete/cancel:** does it cascade to children? (e.g., agent run cancel → child runs)
   - **Both sides must cascade identically** — real bugs: `CancelAsync` missed cascading `waiting_input` children; `Expire()` never cascaded to underlying agent runs

6. **Validate critical paths with real test cases:**
   - **Lock ordering:** if multiple locks are held, verify order is unidirectional (e.g., Approval → AgentRun, never reverse)
   - **Real bug:** `InMemoryOrchestratorRunRepository.ClaimRecoveryAsync` held lock while calling `ClaimCommandAsync` (lock re-entrance via Monitor), but no D5 tests caught the reverse-lock path in Dapper

7. **Regression test requirement:**
   - **Assert authoritative state**, not mirrored fields:
     - **Wrong:** `Assert.Equal(run.StatusCache, "completed")` — cache can be correct while underlying data is wrong
     - **Right:** Query the underlying persistent record, assert the real status
   - Real case: dual-ledger write in D7 approvals left a mirrored field updated but the primary ledger unchanged; tests asserting only the cache passed silently

## Common Drift Patterns (Watch For)

- **Soft-deleted vs. hard-deleted** — Dapper soft-deletes by status; InMemory removes from dict
- **Concurrent access** — Dapper uses `FOR UPDATE` row locks; InMemory uses global `lock` statement
- **Partial failures** — Dapper rolls back entire transaction; InMemory may leave partial state if no try/catch
- **No-match returns** — Dapper: `null` or empty list; InMemory: must match exactly

## Before Declaring Complete

- [ ] Both Dapper and InMemory implementations updated
- [ ] Test suite runs green for both (`dotnet test`)
- [ ] Regression tests assert **authoritative** state, not cached/mirrored fields
- [ ] Cascade integrity verified (especially parent deletes, status transitions)
- [ ] No asymmetric partial-failure semantics (all-or-nothing must match)

See [backend/AGENTS.md](../../../backend/AGENTS.md#gotchas) for the full context and real-world examples.
