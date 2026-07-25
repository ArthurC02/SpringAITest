#!/usr/bin/env pwsh
# D5 deterministic verifier.  It never sends prompts or credentials to an
# external provider: the evidence profile uses only its loopback evidence-model
# and the behavioural cases execute the repository's deterministic adapters.
[CmdletBinding()]
param(
    [switch]$Build,
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$infra = Join-Path $repo 'infra'
$runTag = "d5e2e-$([guid]::NewGuid().ToString('N'))"
$script:passed = 0

function Pass([string]$Name) { $script:passed++; Write-Host "PASS $Name" }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Wait-Http([string]$Uri, [string]$Name) {
    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try { $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 4; if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { return } } catch {}
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $until)
    throw "$Name did not become healthy before timeout"
}
function Expect-Status([scriptblock]$Request, [int]$Expected, [string]$Name) {
    try { & $Request | Out-Null; throw "$Name unexpectedly succeeded" }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -ne $Expected) { throw "$Name expected HTTP $Expected, got $status" }
    }
}
function Invoke-Test([string]$Name, [string]$File, [string[]]$Arguments) {
    Push-Location $File
    try { & dotnet @Arguments; if ($LASTEXITCODE -ne 0) { throw "$Name failed" } }
    finally { Pop-Location }
    Pass $Name
}

# The deterministic evidence model requires fresh process-local HMACs. They
# are not printed, persisted, or reused after this invocation.
$env:AGENT_BUILDER_ENABLED = 'true'
$env:WORKFLOW_DESIGNER_ENABLED = 'true'
$env:MULTI_AGENT_DISPATCH_ENABLED = 'true'
$env:EVIDENCE_AGENT_TEST_RUN_ENABLED = 'true'
$env:EVIDENCE_DB_NAME = "d5evidence_$([guid]::NewGuid().ToString('N'))"
$env:EVIDENCE_CHECKPOINT_HMAC_KEY = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
$env:EVIDENCE_HMAC_KEY = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))

Push-Location $infra
try {
    # Use a per-run database so immutable seed drift in a developer's normal
    # appdb can neither affect evidence nor be overwritten by it.
    docker compose -f docker-compose.yml up -d appdb | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'unable to start D5 evidence appdb' }
    $dbReadyUntil = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        docker compose -f docker-compose.yml exec -T appdb pg_isready -U postgres | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $dbReadyUntil)
    if ($LASTEXITCODE -ne 0) { throw 'D5 evidence appdb did not become ready' }
    docker compose -f docker-compose.yml exec -T appdb psql -U postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE `"$($env:EVIDENCE_DB_NAME)`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'unable to create isolated D5 evidence database' }

    $compose = @('-f','docker-compose.yml','-f','docker-compose.evidence.yml','--profile','evidence','up','-d')
    if ($Build) { $compose += '--build' }
    $compose += @('appdb','rabbitmq','backend','workflow','evidence-model','platform-evidence')
    docker compose @compose | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'unable to start D5 evidence services' }

    Wait-Http 'http://127.0.0.1:8180/actuator/health' 'D5 platform-evidence'
    Wait-Http 'http://127.0.0.1:8001/health' 'D5 workflow'
    Pass 'clean loopback evidence services'

    $features = Invoke-RestMethod -UseBasicParsing -Uri 'http://127.0.0.1:8180/api/features'
    Require ($features.multiAgentDispatchEnabled -eq $true) 'D5 feature flag is not enabled on platform-evidence'
    Pass 'feature flag'

    # The public management start route must be fail-closed before any fixture
    # exists. A random GUID prevents a pre-existing run from affecting the check.
    $missing = [guid]::NewGuid().ToString()
    Expect-Status { Invoke-WebRequest -UseBasicParsing -Method Post -Uri "http://127.0.0.1:8180/api/admin/orchestrators/$missing/runs" -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = "$runTag-anon" } -Body '{"message":"d5","conversationId":"d5"}' } 401 'anonymous root start'
    Pass 'root auth gate'

    $user = Invoke-RestMethod -UseBasicParsing -Method Post -Uri 'http://127.0.0.1:8180/api/auth/login' -ContentType 'application/json' -Body '{"username":"user-a","password":"password123"}'
    Expect-Status { Invoke-WebRequest -UseBasicParsing -Method Post -Uri "http://127.0.0.1:8180/api/admin/orchestrators/$missing/runs" -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($user.token)"; 'Idempotency-Key' = "$runTag-user" } -Body '{"message":"d5","conversationId":"d5"}' } 403 'non-manager root start'
    Pass 'root authority gate'

    # These suites use deterministic in-process runtime adapters. Together they
    # prove immediate allocation/202, bounded two-child dispatch, verifier +
    # repair + aggregate, exclusive cursor paging, cancellation cascade,
    # restart/lease recovery, and write/authority denial without network LLMs.
    Invoke-Test 'backend root allocation/cursor/cascade/authority' (Join-Path $repo 'backend') @('test','tests/Backend.Api.Tests/Backend.Api.Tests.csproj','--no-restore','--filter','FullyQualifiedName~Orchestrator')
    Invoke-Test 'platform root proxy/auth/202' (Join-Path $repo 'platform') @('test','tests/Platform.Web.Tests/Platform.Web.Tests.csproj','--no-restore','--filter','FullyQualifiedName~OrchestratorRun')

    Push-Location (Join-Path $repo 'workflow')
    try {
        & uv run pytest tests/test_root_orchestrator.py tests/test_root_orchestrator_api.py tests/test_root_orchestrator_backend.py tests/test_root_orchestrator_production.py tests/test_root_orchestrator_supervisor.py -q
        if ($LASTEXITCODE -ne 0) { throw 'workflow deterministic root-runtime suite failed' }
    } finally { Pop-Location }
    Pass 'workflow two-child/verifier-repair/aggregate/recovery/write-denial'

    # Restart the real worker once; the deterministic supervisor suite above
    # checks claimed-command recovery and generation fencing across that restart.
    docker compose -f docker-compose.yml -f docker-compose.evidence.yml restart workflow | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'workflow restart failed' }
    Wait-Http 'http://127.0.0.1:8001/health' 'restarted D5 workflow'
    Pass 'workflow restart health'

    Write-Host "PASS D5 deterministic verifier checks=$script:passed"
} finally {
    if ($env:EVIDENCE_DB_NAME -match '^d5evidence_[0-9a-f]{32}$') {
        docker compose -f docker-compose.yml exec -T appdb psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS `"$($env:EVIDENCE_DB_NAME)`" WITH (FORCE)" | Out-Null
    }
    Pop-Location
    Remove-Item Env:EVIDENCE_CHECKPOINT_HMAC_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:EVIDENCE_HMAC_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:EVIDENCE_AGENT_TEST_RUN_ENABLED -ErrorAction SilentlyContinue
    Remove-Item Env:EVIDENCE_DB_NAME -ErrorAction SilentlyContinue
}
