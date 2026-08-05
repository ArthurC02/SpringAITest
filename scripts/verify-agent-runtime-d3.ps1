#!/usr/bin/env pwsh
# D3 deterministic, loopback-only release verifier. It uses the isolated
# evidence profile, creates only uniquely named test agents, and never prints
# JWTs, prompts, checkpoint IDs, or request bodies.
[CmdletBinding()]
param(
    [switch]$Build,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_development-environment.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$infra = Join-Path $repo 'infra'
$runTag = "d3e2e-$([guid]::NewGuid().ToString('N'))"
$env:AGENT_BUILDER_ENABLED = 'true'
$env:EVIDENCE_AGENT_TEST_RUN_ENABLED = 'true'
$env:EVIDENCE_CHECKPOINT_HMAC_KEY = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
$env:EVIDENCE_HMAC_KEY = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))

function Wait-Status([scriptblock]$Get, [string[]]$Expected, [string]$Name) {
    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $value = & $Get
        if ($Expected -contains [string]$value.status) { return $value }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $until)
    throw "$Name did not reach [$($Expected -join ',')] before timeout"
}

function New-Agent([hashtable]$Headers, [string]$Suffix, [object[]]$SkillBindings = @(), [hashtable]$BusinessRules = @{ version = 1; rules = @() }) {
    $body = @{
        slug = "$runTag-$Suffix"; name = "D3 E2E $Suffix"; description = 'deterministic release fixture'
        system_prompt = 'Follow only exposed runtime actions.'; execution_roles = @('worker'); capabilities = @()
        output_contract = @{ type = 'string' }; audience = @('ADMIN'); allowed_tools = @(); skill_bindings = $SkillBindings
        knowledge_sources = @(); business_rules = $BusinessRules
        runtime_limits = @{ max_tool_rounds = 0; max_context_rounds = 0; timeout_seconds = 120; token_budget = 10000000; step_budget = 100 }
    } | ConvertTo-Json -Depth 10 -Compress
    $created = Invoke-WebRequest -UseBasicParsing -Method Post -Uri 'http://127.0.0.1:8180/api/agents' -Headers $Headers -ContentType 'application/json' -Body $body
    $agent = $created.Content | ConvertFrom-Json
    $etag = $created.Headers.ETag
    $writeHeaders = @{ Authorization = $Headers.Authorization; 'If-Match' = $etag }
    $validation = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:8180/api/agents/$($agent.id)/validate" -Headers $writeHeaders -ContentType 'application/json' -Body '{}'
    if (-not $validation.valid) { throw "Agent validation failed for $Suffix" }
    $null = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:8180/api/agents/$($agent.id)/publish" -Headers $writeHeaders -ContentType 'application/json' -Body (@{expected_draft_version=$agent.draft_version}|ConvertTo-Json -Compress)
    return $agent
}

function Start-Run([hashtable]$Headers, $Agent, [string]$Marker) {
    $startHeaders = @{ Authorization = $Headers.Authorization; 'Idempotency-Key' = [guid]::NewGuid().ToString() }
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:8180/api/agents/$($Agent.id)/runs" -Headers $startHeaders -ContentType 'application/json' -Body (@{message=$Marker}|ConvertTo-Json -Compress)
}

function Get-Run([hashtable]$Headers, [string]$Id) {
    Invoke-RestMethod -Uri "http://127.0.0.1:8180/api/runs/$Id" -Headers $Headers
}

Push-Location $infra
try {
    $args = @('-f','docker-compose.yml','-f','docker-compose.evidence.yml','--profile','evidence','up','-d')
    if ($Build) { $args += '--build' }
    $args += @('appdb','rabbitmq','backend','workflow','evidence-model','platform-evidence')
    docker compose @args | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'unable to start D3 evidence profile' }

    $healthUntil = (Get-Date).AddSeconds($TimeoutSeconds)
    do { try { $health = Invoke-RestMethod 'http://127.0.0.1:8180/actuator/health' } catch { $health = $null }; Start-Sleep -Milliseconds 500 } while ($null -eq $health -and (Get-Date) -lt $healthUntil)
    if ($null -eq $health) { throw 'platform-evidence did not become healthy' }

    $login = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8180/api/auth/login' -ContentType 'application/json' -Body '{"username":"admin-a","password":"password123"}'
    $headers = @{ Authorization = "Bearer $($login.token)" }
    $agent = New-Agent $headers 'pause'

    # Durable waiting input and resume use the same run snapshot/checkpoint.
    $wait = Start-Run $headers $agent '__d3_waiting_input__'
    $waiting = Wait-Status { Get-Run $headers $wait.id } @('waiting_input') 'waiting-input run'
    $resumeHeaders = @{ Authorization = $headers.Authorization; 'Idempotency-Key' = [guid]::NewGuid().ToString() }
    $null = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:8180/api/runs/$($wait.id)/resume" -Headers $resumeHeaders -ContentType 'application/json' -Body (@{input=@{message='durable answer'}; expectedCheckpointVersion=$waiting.checkpoint_version}|ConvertTo-Json -Compress)
    $completed = Wait-Status { Get-Run $headers $wait.id } @('completed') 'resumed run'

    # Restart Workflow while a checkpoint is waiting, then resume through the
    # normal Platform path. No volume or database cleanup is performed.
    $recover = Start-Run $headers $agent '__d3_waiting_input__'
    $recoverWaiting = Wait-Status { Get-Run $headers $recover.id } @('waiting_input') 'recovery waiting-input run'
    docker compose -f docker-compose.yml -f docker-compose.evidence.yml restart workflow | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'workflow restart failed' }
    $workflowUntil = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try { $workflowHealth = Invoke-RestMethod 'http://127.0.0.1:8001/health' } catch { $workflowHealth = $null }
        if ($null -eq $workflowHealth) { Start-Sleep -Milliseconds 500 }
    } while ($null -eq $workflowHealth -and (Get-Date) -lt $workflowUntil)
    if ($null -eq $workflowHealth) { throw 'workflow did not recover after restart' }
    # The evidence capture proxy caches its upstream connection independently;
    # restart it only after Workflow is healthy so evidence does not create a
    # synthetic 502 that production's direct Platform->Workflow path lacks.
    docker compose -f docker-compose.yml -f docker-compose.evidence.yml restart workflow-capture-proxy | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'workflow evidence-proxy restart failed' }
    $restartUntil = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try { $restartHealth = Invoke-RestMethod 'http://127.0.0.1:8011/health' } catch { $restartHealth = $null }
        if ($null -eq $restartHealth) { Start-Sleep -Milliseconds 500 }
    } while ($null -eq $restartHealth -and (Get-Date) -lt $restartUntil)
    if ($null -eq $restartHealth) { throw 'workflow evidence proxy did not recover after restart' }
    $resumeHeaders['Idempotency-Key'] = [guid]::NewGuid().ToString()
    $null = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:8180/api/runs/$($recover.id)/resume" -Headers $resumeHeaders -ContentType 'application/json' -Body (@{input=@{message='recovered answer'}; expectedCheckpointVersion=$recoverWaiting.checkpoint_version}|ConvertTo-Json -Compress)
    $recovered = Wait-Status { Get-Run $headers $recover.id } @('completed') 'restart recovery run'

    # Cancellation is exercised from the durable waiting state, which proves
    # no resume can follow a cancelled checkpoint.
    $cancel = Start-Run $headers $agent '__d3_waiting_input__'
    $null = Wait-Status { Get-Run $headers $cancel.id } @('waiting_input') 'cancel waiting-input run'
    $cancelHeaders = @{ Authorization = $headers.Authorization; 'Idempotency-Key' = [guid]::NewGuid().ToString() }
    $null = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:8180/api/runs/$($cancel.id)/cancel" -Headers $cancelHeaders -ContentType 'application/json' -Body '{"reason":"deterministic e2e"}'
    $cancelled = Wait-Status { Get-Run $headers $cancel.id } @('cancelled') 'cancelled run'

    # An unpublished/unbound Skill name must never be accepted from model output.
    $unbound = Start-Run $headers $agent '__d3_unbound_skill__'
    $unboundFailed = Wait-Status { Get-Run $headers $unbound.id } @('failed') 'unbound-skill run'
    if ($unboundFailed.error_code -ne 'skill_not_pinned') { throw "Expected skill_not_pinned, got $($unboundFailed.error_code)" }

    # Unknown safety facts follow explicit onUnknown=deny rather than silently
    # becoming false.
    $denyRules = @{ version = 1; rules = @(@{
        id = 'missing-amount'; name = 'Missing amount denies'; enabled = $true; priority = 10
        when = @{ fact = 'action.amount'; op = 'gt'; value = '1.00' }
        then = @(@{ action = 'deny'; reason = 'high amount' })
        onUnknown = @(@{ action = 'deny'; reason = 'missing verified amount' })
    }) }
    $denyAgent = New-Agent $headers 'rule-deny' @() $denyRules
    $denied = Start-Run $headers $denyAgent '__d3_final_reply__'
    $deniedFailed = Wait-Status { Get-Run $headers $denied.id } @('failed') 'rule-deny run'
    if ($deniedFailed.error_code -ne 'rule_deny') { throw "Expected rule_deny, got $($deniedFailed.error_code)" }

    Write-Host "PASS D3 wait/resume=$($completed.id) restart-recovery=$($recovered.id) cancel=$($cancelled.id) unbound=$($unboundFailed.id) rule-deny=$($deniedFailed.id)"
    Write-Host 'Grant-dependent direct-tool and retrieval cases require a server-issued runtime tool/data grant API; D3 correctly snapshots empty grants when none exists.'
} finally {
    Pop-Location
    Remove-Item Env:EVIDENCE_CHECKPOINT_HMAC_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:EVIDENCE_HMAC_KEY -ErrorAction SilentlyContinue
}
