#!/usr/bin/env pwsh
# CI companion for the `full-chain-smoke` job (W2-01): runs the three real-browser evidence
# specs (E-01 auth, E-03 session-tool, E-06 streaming) against the loopback-only deterministic
# evidence profile (`infra/docker-compose.evidence.yml --profile evidence`).
#
# This is intentionally NOT scripts/verify-copilot-shared-core-evidence.ps1: that script is the
# full release-evidence sign-off harness (adds E-02 dotnet model-input capture checks, HMAC
# cross-tenant proofs, and a durable artifact bundle under artifacts/copilot-shared-core/). This
# companion only needs the three specs' own real assertions to run for real (not skip) so
# frontend/evidenceSkipGuardReporter.ts stays meaningful in CI, per the W2-01 decision.
#
# Reuses the `backend` / `workflow` containers the calling job already started
# (verify-copilot-shared-core.ps1 -CiComposeSubset) rather than recreating them: the deterministic
# evidence services this script starts (evidence-model, workflow-capture-proxy, platform-evidence,
# evidence-nginx, frontend-evidence) only read those two over the network, no depends_on edge.
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$infraDir = Join-Path $repoRoot 'infra'
$frontendDir = Join-Path $repoRoot 'frontend'
. (Join-Path $PSScriptRoot '_development-jwt.ps1')

function Wait-Http([string]$Url, [int]$Attempts = 60) {
    foreach ($attempt in 1..$Attempts) {
        try {
            if ((Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 5).StatusCode -eq 200) { return $true }
        }
        catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

# platform-evidence declares JWT_ISSUER/JWT_AUDIENCE/JWT_PUBLIC_KEY_RING_JSON as bare Compose
# passthrough (no `${...}` default), so it needs these set as real process env vars — unlike the
# base `platform` service, `--env-file` alone will not reach it. Reuse the same dev-only fallback
# resolution the release-evidence harness uses (process env > infra/.env > committed
# infra/.env.example), so this stays byte-identical to whatever the already-running `backend`
# container is signing with.
$env:JWT_ISSUER = Get-DevelopmentJwtSetting 'JWT_ISSUER'
$env:JWT_AUDIENCE = Get-DevelopmentJwtSetting 'JWT_AUDIENCE'
$env:JWT_PUBLIC_KEY_RING_JSON = Get-DevelopmentJwtSetting 'JWT_PUBLIC_KEY_RING_JSON'

# evidence-model refuses to start without a >=32 char HMAC key (infra/evidence/evidence_model.py).
# It is process-local and never returned by that service, so a fresh per-run value is enough.
$hmacBytes = [byte[]]::new(48)
[Security.Cryptography.RandomNumberGenerator]::Fill($hmacBytes)
$env:EVIDENCE_HMAC_KEY = [Convert]::ToBase64String($hmacBytes)
$env:EVIDENCE_CHECKPOINT_HMAC_KEY = $env:EVIDENCE_HMAC_KEY

Push-Location $infraDir
try {
    $composeArgs = @('--env-file', '.env.example', '-f', 'docker-compose.yml', '-f', 'docker-compose.evidence.yml', '--profile', 'evidence', 'up', '-d')
    if (-not $SkipBuild) { $composeArgs += '--build' }
    $composeArgs += @('evidence-model', 'workflow-capture-proxy', 'platform-evidence', 'evidence-nginx', 'frontend-evidence')
    docker compose @composeArgs
    if ($LASTEXITCODE -ne 0) { throw 'Unable to start the deterministic evidence profile.' }
}
finally {
    Pop-Location
    Remove-Item Env:EVIDENCE_HMAC_KEY, Env:EVIDENCE_CHECKPOINT_HMAC_KEY -ErrorAction SilentlyContinue
}

if (-not (Wait-Http 'http://127.0.0.1:4010/health')) { throw 'evidence-model did not become healthy.' }
if (-not (Wait-Http 'http://127.0.0.1:8180/actuator/health')) { throw 'platform-evidence did not become healthy.' }
if (-not (Wait-Http 'http://127.0.0.1:5180/')) { throw 'frontend-evidence did not become healthy.' }

Push-Location $frontendDir
try {
    $env:PW_BASE_URL = 'http://127.0.0.1:5180'
    $env:EVIDENCE_DIR = Join-Path ([IO.Path]::GetTempPath()) ('frontend-evidence-ci-' + [guid]::NewGuid().ToString('N'))
    # E-03: evidence-model (infra/evidence/evidence_model.py `_tool_call_message`) answers any
    # user message containing this literal marker, while tools are advertised and no matching
    # tool result exists yet, with a deterministic `switchView({"view":"documents"})` call.
    $env:EVIDENCE_TOOL_PROMPT = '__evidence_tool_call__ switch to the documents view'
    # E-06 legacy single-case form (streaming.evidence.spec.ts): a real authenticated
    # `/api/chat/stream` call. Deterministic streaming always yields exactly the three
    # "evidence" / " model" / " reply" content deltas (evidence_model.py `_stream`), each
    # separated by the evidence-profile's frame delay, before the endpoint's own `data:` (no
    # space) framing and completion.
    $env:EVIDENCE_STREAM_PATH = '/api/chat/stream'
    $env:EVIDENCE_STREAM_BODY_JSON = '{"message":"evidence stream check"}'
    $env:EVIDENCE_STREAM_USE_SESSION = '1'
    npm run test:evidence -- e2e/auth.evidence.spec.ts e2e/session-tool.evidence.spec.ts e2e/streaming.evidence.spec.ts
    if ($LASTEXITCODE -ne 0) { throw 'Frontend real-browser evidence specs (auth/session-tool/streaming) failed.' }
}
finally {
    Pop-Location
    Remove-Item Env:PW_BASE_URL, Env:EVIDENCE_DIR, Env:EVIDENCE_TOOL_PROMPT, Env:EVIDENCE_STREAM_PATH, Env:EVIDENCE_STREAM_BODY_JSON, Env:EVIDENCE_STREAM_USE_SESSION -ErrorAction SilentlyContinue
}

Write-Host 'Frontend real-browser evidence specs (E-01/E-03/E-06) passed.'
