---
name: verify-phase
description: Cross-service behavior verification — select and run the correct evidence verifier based on change scope
---

# Evidence Verifier Selection & Execution

After making cross-service runtime behavior changes, verify the change with the corresponding deterministic verifier script. This skill maps change scope → script → pre-conditions → post-cleanup.

## Change Scope → Verifier Mapping

| Change Scope | Verifier Script | Port | Flags/Setup |
|---|---|---|---|
| D3 (Direct Agent runtime, test runs) | `scripts/verify-agent-runtime-d3.ps1` | `:8180` evidence | `AGENT_TEST_RUN_ENABLED=true`, `CHECKPOINT_DATABASE_URL`, `CHECKPOINT_HMAC_KEY` |
| D5 (Root Orchestrator multi-agent dispatch) | `scripts/verify-multi-agent-d5.ps1` | see script | `MULTI_AGENT_DISPATCH_ENABLED=true`, dependencies for D5 |
| D6 (Agent Chat canary routing) | `scripts/verify-agent-chat-d6.ps1` | see script | `AGENT_CHAT_ENABLED=true`, `AGENT_CHAT_TENANT_ALLOWLIST` |
| D7 (Write tools & approvals) | `scripts/verify-agent-governance-d7.ps1` | see script | `AGENT_WRITE_TOOLS_ENABLED=true`, approval/evidence ledger |
| Container security (Dockerfile, Compose hardening) | `scripts/verify-container-hardening.sh` | loopback | no special flags; validates image/compose security layers |

## Pre-Execution Checklist

- [ ] Identify which D-phase or system component changed
- [ ] Select the corresponding verifier script from the table above
- [ ] Ensure all required flags are set in `.env` (or `docker-compose*.yml` environment)
- [ ] For D3–D7: verify Backend/Workflow services are reachable at loopback (`:8002`, `:8001` or evidence ports)
- [ ] For container: Compose is up and stable
- [ ] Evidence profile (`:8180` for D3, etc.) binds loopback only — no host exposure

## Execution Pattern

```powershell
# D3 direct-Agent runtime
pwsh -File scripts/verify-agent-runtime-d3.ps1 [-Build]

# D5 multi-agent orchestrator
pwsh -File scripts/verify-multi-agent-d5.ps1

# D6 chat canary
pwsh -File scripts/verify-agent-chat-d6.ps1

# D7 governance
pwsh -File scripts/verify-agent-governance-d7.ps1

# Container hardening (Bash)
bash scripts/verify-container-hardening.sh
```

Detailed script behavior and options: see [infra/AGENTS.md](../../../infra/AGENTS.md) under "Operational scripts" and each D-phase section (D3 verifier, D4 Workflow Designer, D6 Agent Chat canary, D7 approved write canary).

## Post-Execution

- **Do NOT remove volumes or containers** — evidence profile stays running for inspection
- Inspect `docker compose logs` if any step fails (broker, appdb healthcheck, flag half-open)
- For repeated runs: no cleanup needed; verifier idempotently restarts evidence services
- Volume retention: 8 total volumes (Langfuse, postgres, appdb, rabbitmq, mem0, etc.) survive `docker compose down` (without `-v`)

## Troubleshooting

- **Broker unreachable:** RabbitMQ may need `rabbitmq-diagnostics ping`
- **appdb not ready:** `pg_isready` check; D3 verifier waits for Backend readiness
- **Flag half-open:** check all three services use the same flag value (`true`/`false`, not `1`/`yes` mixed)
- **mem0 startup failure:** best-effort; chat/operations still work without it
- **Compose build stale:** verify container is running latest image ID (see [infra/AGENTS.md](../../../infra/AGENTS.md#run-modes) build cache note)

For full guidance on run modes, healthchecks, and per-service configuration: [infra/AGENTS.md](../../../infra/AGENTS.md).
