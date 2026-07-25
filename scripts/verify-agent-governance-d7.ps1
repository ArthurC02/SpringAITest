#!/usr/bin/env pwsh
# D7 deterministic governance verifier.
#
# This is deliberately a hybrid proof: the public Platform/Backend routes are
# driven through their real ASP.NET HTTP pipelines, while the transaction and
# outbox cases run against a newly-created PostgreSQL database.  The Workflow
# approval transition is deterministic (it must never call a write tool before
# a durable Backend approval exists), so it is proved with its production graph
# and a poison write sink.  No prompt, token, approval reason, effect payload,
# or idempotency key is printed by this script.
[CmdletBinding()]
param(
    [switch]$KeepEvidenceDb,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$infra = Join-Path $repo 'infra'
$dbName = "d7evidence_$([guid]::NewGuid().ToString('N'))"
$script:checks = 0
$createdDb = $false
$appdbStartedByScript = $false
$appdbCreatedByScript = $false
$priorConnection = [Environment]::GetEnvironmentVariable('DB_CONNECTION_STRING', 'Process')

function Pass([string]$Name) { $script:checks++; Write-Host "PASS $Name" }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-DotnetTest([string]$Name, [string]$Project, [string]$Filter) {
    Push-Location (Split-Path $Project -Parent)
    try {
        & dotnet test $Project --no-restore --filter $Filter
        if ($LASTEXITCODE -ne 0) { throw "$Name failed" }
    }
    finally { Pop-Location }
    Pass $Name
}
function Invoke-Pytest([string]$Name, [string[]]$Arguments) {
    Push-Location (Join-Path $repo 'workflow')
    try {
        & uv run pytest @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Name failed" }
    }
    finally { Pop-Location }
    Pass $Name
}

try {
    Push-Location $infra
    try {
        # Reuse appdb without recreating it. If this verifier has to start (or
        # create) the container, it records that fact and stops only that
        # container in finally. The normal springaitest database is never used.
        [string]$appdbContainer = (& docker compose -f docker-compose.yml ps --all -q appdb)
        $appdbContainer = $appdbContainer.Trim()
        $appdbWasRunning = $false
        if ($appdbContainer) {
            [string]$running = (& docker inspect -f '{{.State.Running}}' $appdbContainer)
            if ($LASTEXITCODE -ne 0) { throw 'unable to inspect appdb state' }
            $appdbWasRunning = $running.Trim() -eq 'true'
        }
        if (-not $appdbWasRunning) {
            if ($appdbContainer) {
                & docker compose -f docker-compose.yml start appdb | Out-Null
            }
            else {
                & docker compose -f docker-compose.yml up -d --no-recreate appdb | Out-Null
                $appdbCreatedByScript = $true
            }
            if ($LASTEXITCODE -ne 0) { throw 'unable to start isolated-evidence dependency appdb' }
            $appdbStartedByScript = $true
        }
        $until = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            & docker compose -f docker-compose.yml exec -T appdb pg_isready -U postgres | Out-Null
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $until)
        if ($LASTEXITCODE -ne 0) { throw 'appdb was not ready before timeout' }
        Require ($dbName -match '^d7evidence_[0-9a-f]{32}$') 'unsafe evidence database name'
        & docker compose -f docker-compose.yml exec -T appdb psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE `"$dbName`"" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'unable to create isolated D7 evidence database' }
        $createdDb = $true
    }
    finally { Pop-Location }

    # PostgresFixture reads this only for this process.  It bootstraps the
    # temporary database, including D7 effect/outbox tables, before each real
    # repository acceptance test.
    $env:DB_CONNECTION_STRING = "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=$dbName"

    # A-RUN-13 and the three independently fail-closed flags.  Platform and
    # Backend assertions use their actual HTTP middleware/controllers; Workflow
    # has no public route and proves the equivalent runtime denial directly.
    Invoke-DotnetTest 'Platform HTTP flag-off approval/operations routes return 404' (Join-Path $repo 'platform/tests/Platform.Web.Tests/Platform.Web.Tests.csproj') 'FullyQualifiedName~RunApprovalApiTests|FullyQualifiedName~OperationsGovernanceApiTests'
    Invoke-DotnetTest 'Backend TestServer flag-off public/internal approval/effect/execute/telemetry routes return 404 before token/body' (Join-Path $repo 'backend/tests/Backend.Api.Tests/Backend.Api.Tests.csproj') 'FullyQualifiedName~D7FeatureGateTests'
    Invoke-Pytest 'Workflow write flag denies invocation; enabled path pauses before zero writes' @('-q','tests/test_agent_runtime.py','-k','registered_write_tool')

    # A-RUN-20/21/26: real HTTP request handling covers ordinary business
    # approvers (no workflow.manage), SoD, tenant fence, generic-resume denial,
    # and replay.  The same filter has an isolated PostgreSQL case below for
    # transaction-level, rather than in-memory-only, effect proof.
    Invoke-DotnetTest 'Approval HTTP role/SoD/cross-tenant/replay acceptance' (Join-Path $repo 'backend/tests/Backend.Api.Tests/Backend.Api.Tests.csproj') 'FullyQualifiedName~AgentRunApprovalApiTests|FullyQualifiedName~AgentRunApprovalInMemoryTests'
    Invoke-DotnetTest 'PostgreSQL approval effect/outbox exactly-once and cancellation fences' (Join-Path $repo 'backend/tests/Backend.Api.Tests/Backend.Api.Tests.csproj') 'FullyQualifiedName~D7_Approval_EffectOutboxAndExecuteFence_RoundTripThroughDapper'
    Invoke-Pytest 'Workflow durable effect identity and redacted approved-write telemetry' @('-q','tests/test_d7_write_evidence.py')

    # A-OPS-01..07: regression block/explicit idempotent override, rollout
    # new-root pin/rollback projection, redacted aggregation and legacy
    # inventory are covered by the real Backend HTTP API and Platform proxy.
    Invoke-DotnetTest 'PostgreSQL HTTP governance gate/override/future-root rollout/rollback/tenant metrics/version/inventory redaction' (Join-Path $repo 'backend/tests/Backend.Api.Tests/Backend.Api.Tests.csproj') 'FullyQualifiedName~OperationsGovernancePostgresApiTests'
    Invoke-DotnetTest 'Platform operations proxy preserves capability and logical attempt only' (Join-Path $repo 'platform/tests/Platform.Web.Tests/Platform.Web.Tests.csproj') 'FullyQualifiedName~OperationsGovernanceApiTests'

    # The source/test contracts deliberately distinguish the portions that
    # cannot be exercised as a single provider-backed model run here.  These
    # are assertions about the proof composition, not a claim of a full-model
    # e2e run.
    Pass 'hybrid evidence boundary: HTTP authorization plus real PostgreSQL effect/outbox; no model/provider write invoked'
    Write-Host "PASS D7 deterministic governance verifier checks=$script:checks"
}
finally {
    $cleanupFailures = @()
    if ($createdDb -and -not $KeepEvidenceDb -and $dbName -match '^d7evidence_[0-9a-f]{32}$') {
        Push-Location $infra
        try {
            & docker compose -f docker-compose.yml exec -T appdb psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS `"$dbName`" WITH (FORCE)" | Out-Null
            if ($LASTEXITCODE -ne 0) { $cleanupFailures += 'isolated evidence database cleanup failed' }
        }
        finally { Pop-Location }
    }
    elseif ($createdDb -and $KeepEvidenceDb) { Write-Warning 'Retained isolated D7 evidence database for diagnosis.' }
    if ($appdbCreatedByScript) {
        Push-Location $infra
        try {
            & docker compose -f docker-compose.yml rm -s -f appdb | Out-Null
            if ($LASTEXITCODE -ne 0) { $cleanupFailures += 'created appdb container cleanup failed' }
        }
        finally { Pop-Location }
    }
    elseif ($appdbStartedByScript) {
        Push-Location $infra
        try {
            & docker compose -f docker-compose.yml stop appdb | Out-Null
            if ($LASTEXITCODE -ne 0) { $cleanupFailures += 'appdb state restore failed' }
        }
        finally { Pop-Location }
    }
    [Environment]::SetEnvironmentVariable('DB_CONNECTION_STRING', $priorConnection, 'Process')
    if ($cleanupFailures.Count -gt 0) { throw ($cleanupFailures -join '; ') }
}
