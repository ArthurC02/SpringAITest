#!/usr/bin/env pwsh
# Copilot Shared Core repeatable black-box smoke companion.
# It exercises the real HTTP/compose dependencies for the observable parts of
# C-01~C-08, but it does not replace model-input/session trace inspection,
# browser timing checks, or real-model routing verification.
# This script never removes containers or volumes. The optional mem0 outage check
# stops only mem0 and always starts it again from a finally block.
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:8080',
    [string]$ProxyBaseUrl = 'http://localhost:5173',
    [switch]$Rebuild,
    [switch]$CiComposeSubset,
    [switch]$IncludeMem0Outage,
    [string]$EvidenceDir = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$infraDir = Join-Path $repoRoot 'infra'
$script:passed = 0
$script:failed = 0
$evidenceRun = $null
if ($CiComposeSubset -and -not $Rebuild) {
    throw '-CiComposeSubset requires -Rebuild'
}
if ($EvidenceDir) {
    Import-Module (Join-Path $PSScriptRoot 'EvidenceHarness.psm1') -Force
    $evidenceRun = New-EvidenceRun -RepoRoot $repoRoot -EvidenceDir $EvidenceDir -Lane BlackBoxSmoke -Gates @('black-box-smoke')
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Case([string]$Id, [scriptblock]$Body) {
    try {
        & $Body
        $script:passed++
        Write-Host "PASS $Id"
    }
    catch {
        $script:failed++
        Write-Host "FAIL $Id - $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Invoke-JsonRequest(
    [string]$Method,
    [string]$Uri,
    [object]$Body = $null,
    [string]$Token = ''
) {
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $args = @{
        Method = $Method
        Uri = $Uri
        Headers = $headers
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $args.ContentType = 'application/json; charset=utf-8'
        $args.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }
    Invoke-WebRequest @args
}

function Login([string]$Username) {
    $response = Invoke-JsonRequest POST "$BaseUrl/api/auth/login" @{
        username = $Username
        password = 'password123'
    }
    Assert-True ($response.StatusCode -eq 200) "login $Username returned $($response.StatusCode)"
    ($response.Content | ConvertFrom-Json).token
}

function Invoke-Agui([string]$Token, [string]$ThreadId, [object[]]$Messages, [string]$Url = $BaseUrl) {
    $body = @{
        threadId = $ThreadId
        runId = [guid]::NewGuid().ToString('N')
        state = @{}
        messages = $Messages
        tools = @()
        context = @()
        forwardedProps = @{}
    }
    Invoke-JsonRequest POST "$Url/api/copilot/agui" $body $Token
}

function New-WireMessage([string]$Role, [string]$Content, [string]$Id = '') {
    if (-not $Id) { $Id = [guid]::NewGuid().ToString('N') }
    @{ id = $Id; role = $Role; content = $Content }
}

function Read-AguiReply([string]$Raw) {
    $frames = $Raw -split "`n`n" |
        Where-Object { $_.Trim() } |
        ForEach-Object { ($_ -replace '^data: ', '') | ConvertFrom-Json }
    $start = $frames | Where-Object type -eq 'TEXT_MESSAGE_START' | Select-Object -First 1
    $text = ($frames | Where-Object type -eq 'TEXT_MESSAGE_CONTENT' | ForEach-Object delta) -join ''
    @{ Id = $start.messageId; Text = $text; Types = @($frames.type) }
}

if ($Rebuild) {
    $oldChatModel = $env:CHAT_MODEL
    try {
        $env:CHAT_MODEL = 'mock-gpt'
        if ($CiComposeSubset) {
            Push-Location $infraDir
            try {
                & docker compose --env-file .env.example build backend workflow platform frontend
                if ($LASTEXITCODE -ne 0) { throw "compose build failed with exit code $LASTEXITCODE" }
                # The deterministic CI lane starts only the services exercised by this smoke.
                # Compose still brings their declared dependencies, but it does not pull the
                # optional prebuilt mem0 image or run a real-model lane.
                & docker compose --env-file .env.example --profile full up -d --no-build backend workflow platform frontend
                if ($LASTEXITCODE -ne 0) { throw "compose up failed with exit code $LASTEXITCODE" }
            }
            finally {
                Pop-Location
            }
        }
        else {
            & (Join-Path $PSScriptRoot 'start-full.ps1')
            if ($LASTEXITCODE -ne 0) { throw "start-full failed with exit code $LASTEXITCODE" }
        }
    }
    finally {
        $env:CHAT_MODEL = $oldChatModel
    }
}

$deadline = [DateTime]::UtcNow.AddMinutes(2)
$health = $null
do {
    try {
        $health = Invoke-WebRequest "$BaseUrl/actuator/health" -SkipHttpErrorCheck
    }
    catch {
        $health = $null
    }
    if ($health.StatusCode -eq 200) { break }
    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)
Assert-True ($null -ne $health -and $health.StatusCode -eq 200) "platform did not become healthy"

$tokenA = Login 'user-a'
$tokenB = Login 'user-b'
$tokenAdmin = Login 'admin-a'
$marker = "shared-core-$([guid]::NewGuid().ToString('N'))"
$threadId = "shared-core-$([guid]::NewGuid().ToString('N'))"

Invoke-Case 'C-00 Agent Builder fail-closed gate matches live AGENT_BUILDER_ENABLED' {
    # /api/features is AllowAnonymous and is the authority for the flag's live
    # value; do not guess it from an environment variable the harness itself
    # may not share with the running platform container.
    $features = Invoke-JsonRequest GET "$BaseUrl/api/features"
    Assert-True ($features.StatusCode -eq 200) "GET /api/features returned $($features.StatusCode)"
    $agentBuilderEnabled = ($features.Content | ConvertFrom-Json).agentBuilderEnabled

    $anonymous = Invoke-JsonRequest GET "$BaseUrl/api/agents"
    if ($agentBuilderEnabled) {
        # Flag on: the 404 fail-closed gate no longer applies, so anonymous now
        # hits the controller's own [Authorize] and is rejected with 401 —
        # still fail-closed, just a different (correct) status for this state.
        Assert-True ($anonymous.StatusCode -eq 401) "anonymous /api/agents returned $($anonymous.StatusCode) with AGENT_BUILDER_ENABLED=true, expected 401"
    }
    else {
        # Flag off (default): the pre-auth gate returns the same 404 as a
        # nonexistent route, before any authentication check runs.
        Assert-True ($anonymous.StatusCode -eq 404) "anonymous /api/agents returned $($anonymous.StatusCode) with AGENT_BUILDER_ENABLED=false, expected fail-closed 404"

        # admin-a is the seeded ADMIN identity. This assertion only makes sense
        # while the flag is off: it proves even a legitimately privileged,
        # authenticated caller cannot learn the route exists before the gate is
        # opened. Once the flag is on, admin-a is expected to reach the
        # controller (not fail-closed), so there is nothing equivalent to assert here.
        $admin = Invoke-JsonRequest GET "$BaseUrl/api/agents" $null $tokenAdmin
        Assert-True ($admin.StatusCode -eq 404) "authorized admin /api/agents returned $($admin.StatusCode) with AGENT_BUILDER_ENABLED=false, expected fail-closed 404"
    }
    $error = $anonymous.Content | ConvertFrom-Json
    Assert-True ($error.status -eq $anonymous.StatusCode -and -not [string]::IsNullOrWhiteSpace($error.code)) 'anonymous response is not ApiError-shaped'
}

Invoke-Case 'C-01A authenticated blocking chat + appdb history' {
    $historyBefore = @(((Invoke-JsonRequest GET "$BaseUrl/api/chat/history" $null $tokenA).Content | ConvertFrom-Json)).Count
    $chat = Invoke-JsonRequest POST "$BaseUrl/api/chat" @{
        message = "blocking-$marker"
        conversationId = [guid]::NewGuid().ToString()
    } $tokenA
    Assert-True ($chat.StatusCode -eq 200) "blocking chat returned $($chat.StatusCode)"

    $body = $chat.Content | ConvertFrom-Json
    $responseId = 0L
    Assert-True ([long]::TryParse([string]$body.id, [ref]$responseId) -and $responseId -gt 0) 'blocking chat response id is missing or invalid'
    Assert-True (-not [string]::IsNullOrWhiteSpace($body.reply)) 'blocking chat response reply is empty'
    # ConvertFrom-Json auto-converts an ISO-8601 "Z" string into a [DateTime]
    # (Kind=Utc). Re-stringifying that value with [string] renders it in the
    # local culture and drops the UTC marker, so a plain TryParse on the
    # re-stringified value would misread it as local time. Branch on the
    # actual runtime type instead, and for the string fallback assume UTC
    # when no offset is present rather than guessing the local zone.
    $createdAtRaw = $body.createdAt
    $createdAt = [DateTimeOffset]::MinValue
    if ($createdAtRaw -is [DateTime]) {
        $createdAtValid = $true
        $createdAt = [DateTimeOffset]::new($createdAtRaw.ToUniversalTime())
    }
    else {
        $createdAtValid = [DateTimeOffset]::TryParse(
            [string]$createdAtRaw,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal,
            [ref]$createdAt)
    }
    Assert-True $createdAtValid 'blocking chat response createdAt is invalid'
    $createdAge = [DateTimeOffset]::UtcNow - $createdAt.ToUniversalTime()
    Assert-True ($createdAge.TotalMinutes -ge -1 -and $createdAge.TotalMinutes -le 10) 'blocking chat response createdAt is outside the expected time window'

    $history = Invoke-JsonRequest GET "$BaseUrl/api/chat/history" $null $tokenA
    Assert-True ($history.StatusCode -eq 200) "history returned $($history.StatusCode) after blocking chat"
    $historyItems = @(($history.Content | ConvertFrom-Json))
    Assert-True ($historyItems.Count -gt $historyBefore) 'chat history count did not increase after the blocking turn'
    $persisted = $historyItems | Where-Object id -eq $responseId | Select-Object -First 1
    Assert-True ($null -ne $persisted -and $persisted.reply -eq $body.reply) 'blocking chat response was not persisted with the same id and reply'
}

Invoke-Case 'C-01B chat SSE + appdb history' {
    $historyBefore = @(((Invoke-JsonRequest GET "$BaseUrl/api/chat/history" $null $tokenA).Content | ConvertFrom-Json)).Count
    $stream = Invoke-JsonRequest POST "$BaseUrl/api/chat/stream" @{
        message = $marker
        conversationId = [guid]::NewGuid().ToString()
    } $tokenA
    Assert-True ($stream.StatusCode -eq 200) "chat stream returned $($stream.StatusCode)"
    Assert-True ($stream.Content -match '(?m)^data:[^ ]') 'chat SSE must use data: without a space'
    $history = Invoke-JsonRequest GET "$BaseUrl/api/chat/history" $null $tokenA
    Assert-True ($history.StatusCode -eq 200) "history returned $($history.StatusCode)"
    $historyAfter = @(($history.Content | ConvertFrom-Json)).Count
    Assert-True ($historyAfter -gt $historyBefore) 'chat history count did not increase after the streamed turn'
}

Invoke-Case 'C-02 AG-UI authentication + wire format' {
    $unauthorized = Invoke-Agui '' $threadId @(New-WireMessage user 'anonymous')
    Assert-True ($unauthorized.StatusCode -eq 401) "anonymous AG-UI returned $($unauthorized.StatusCode), expected 401"
    $error = $unauthorized.Content | ConvertFrom-Json
    Assert-True ($null -ne $error.timestamp -and $error.status -eq 401 -and $null -ne $error.fieldErrors) '401 is not ApiError-shaped'

    $authorized = Invoke-Agui $tokenA $threadId @(New-WireMessage user 'authenticated AG-UI')
    Assert-True ($authorized.StatusCode -eq 200) "authenticated AG-UI returned $($authorized.StatusCode)"
    Assert-True ($authorized.Content -match '(?m)^data: ') 'AG-UI SSE must use data: with a space'
    $reply = Read-AguiReply $authorized.Content
    Assert-True ($reply.Types -contains 'RUN_STARTED' -and $reply.Types -contains 'RUN_FINISHED') 'AG-UI event sequence is incomplete'
}

Invoke-Case 'C-03 response-level tenant isolation (trace inspection still required)' {
    $secret = "tenant-a-secret-$([guid]::NewGuid().ToString('N'))"
    $first = Invoke-Agui $tokenA $threadId @(New-WireMessage user "Remember exactly: $secret")
    Assert-True ($first.StatusCode -eq 200) 'tenant A AG-UI request failed'
    $second = Invoke-Agui $tokenB $threadId @(New-WireMessage user 'Repeat the other tenant secret')
    Assert-True ($second.StatusCode -eq 200) 'tenant B AG-UI request failed'
    Assert-True ($second.Content -notlike "*$secret*") 'tenant B response exposed tenant A content'
}

Invoke-Case 'C-04 AG-UI full-array resend smoke (integration trace still required)' {
    $messages = [System.Collections.Generic.List[object]]::new()
    foreach ($round in 1..3) {
        $messages.Add((New-WireMessage user "round-$round"))
        $response = Invoke-Agui $tokenA "$threadId-c04" $messages.ToArray()
        Assert-True ($response.StatusCode -eq 200) "AG-UI round $round failed"
        $reply = Read-AguiReply $response.Content
        Assert-True (-not [string]::IsNullOrWhiteSpace($reply.Id)) "round $round returned no assistant message id"
        $messages.Add((New-WireMessage assistant $reply.Text $reply.Id))
    }
}

Invoke-Case 'C-05 AG-UI persistence' {
    $aguiMarker = "agui-history-$([guid]::NewGuid().ToString('N'))"
    $historyBefore = @(((Invoke-JsonRequest GET "$BaseUrl/api/chat/history" $null $tokenA).Content | ConvertFrom-Json)).Count
    $response = Invoke-Agui $tokenA "$threadId-c05" @(New-WireMessage user $aguiMarker)
    Assert-True ($response.StatusCode -eq 200) 'AG-UI persistence request failed'
    $reply = Read-AguiReply $response.Content
    Assert-True ($reply.Types -contains 'RUN_FINISHED') 'AG-UI persistence run did not finish'
    $history = Invoke-JsonRequest GET "$BaseUrl/api/chat/history" $null $tokenA
    $historyAfter = @(($history.Content | ConvertFrom-Json)).Count
    Assert-True ($historyAfter -gt $historyBefore) 'shared history count did not increase after the AG-UI turn'
}

if ($IncludeMem0Outage) {
    Invoke-Case 'C-06 mem0 outage is best-effort' {
        Push-Location $infraDir
        try {
            docker compose stop mem0
            if ($LASTEXITCODE -ne 0) { throw 'failed to stop mem0' }
            $chat = Invoke-JsonRequest POST "$BaseUrl/api/chat" @{ message = 'mem0 outage chat' } $tokenA
            Assert-True ($chat.StatusCode -eq 200) "chat returned $($chat.StatusCode) while mem0 was down"
            $agui = Invoke-Agui $tokenA "$threadId-c06" @(New-WireMessage user 'mem0 outage copilot')
            Assert-True ($agui.StatusCode -eq 200) "AG-UI returned $($agui.StatusCode) while mem0 was down"
            Assert-True ($agui.Content -notmatch 'RUN_ERROR') 'AG-UI emitted RUN_ERROR while mem0 was down'
        }
        finally {
            try {
                docker compose start mem0
                if ($LASTEXITCODE -ne 0) { throw 'failed to restart mem0' }
                $mem0Ready = $false
                foreach ($attempt in 1..30) {
                    try {
                        $mem0Health = Invoke-WebRequest 'http://127.0.0.1:8000/docs' -SkipHttpErrorCheck
                        if ($mem0Health.StatusCode -eq 200) { $mem0Ready = $true; break }
                    }
                    catch {
                        # The container can be running before uvicorn starts listening.
                    }
                    Start-Sleep -Seconds 2
                }
                Assert-True $mem0Ready 'mem0 did not become ready after restart'
            }
            finally {
                Pop-Location
            }
        }
    }
}
else {
    Write-Host 'SKIP C-06 (pass -IncludeMem0Outage to run the disruptive mem0 outage check)'
}

Invoke-Case 'C-07 workflow/backend shape smoke (real-model shared routing still required)' {
    $documentMarker = "retrieval-$([guid]::NewGuid().ToString('N'))"
    $upload = Invoke-JsonRequest POST "$BaseUrl/api/documents" @{
        title = "shared-core-$documentMarker"
        text = "The verification marker is $documentMarker."
    } $tokenA
    Assert-True ($upload.StatusCode -eq 202) "document upload returned $($upload.StatusCode)"
    $doc = $upload.Content | ConvertFrom-Json
    $ready = $false
    foreach ($attempt in 1..30) {
        Start-Sleep -Seconds 2
        $documents = (Invoke-JsonRequest GET "$BaseUrl/api/documents" $null $tokenA).Content | ConvertFrom-Json
        $current = $documents | Where-Object id -eq $doc.id | Select-Object -First 1
        if ($current.status -eq 'ready') { $ready = $true; break }
        if ($current.status -eq 'failed') { throw 'document processing failed' }
    }
    Assert-True $ready 'document did not become ready within 60 seconds'
    $skill = Invoke-JsonRequest POST "$BaseUrl/api/skills/rag-qa/invoke" @{
        input = @{ question = 'What is the verification marker?' }
    } $tokenA
    Assert-True ($skill.StatusCode -eq 200) "rag-qa returned $($skill.StatusCode)"
    Assert-True ($skill.Content -like "*$documentMarker*") 'rag-qa did not retrieve the uploaded marker'
}

Invoke-Case 'C-08 frontend proxy framing (browser timing still required)' {
    $chat = Invoke-JsonRequest POST "$ProxyBaseUrl/api/chat/stream" @{
        message = 'proxy chat stream'
        conversationId = [guid]::NewGuid().ToString()
    } $tokenA
    Assert-True ($chat.StatusCode -eq 200 -and $chat.Content -match '(?m)^data:[^ ]') 'proxy changed chat SSE framing'
    $agui = Invoke-Agui $tokenA "$threadId-proxy" @(New-WireMessage user 'proxy AG-UI') $ProxyBaseUrl
    Assert-True ($agui.StatusCode -eq 200 -and $agui.Content -match '(?m)^data: ') 'proxy changed AG-UI SSE framing'
}

Write-Host "Result: $script:passed passed, $script:failed failed"
Write-Host 'Remaining release evidence: inspect C-03/C-04 model/session traces, C-05 mem0 storage,'
Write-Host 'run C-07 with a real routing-capable model, and verify C-08 chunk timing in a browser/curl trace.'
if ($evidenceRun) {
    $smokeStatus = if ($script:failed -eq 0) { 'PASS' } else { 'FAIL' }
    Set-EvidenceGate -Run $evidenceRun -Gate 'black-box-smoke' -Status $smokeStatus -Detail "passed=$script:passed; failed=$script:failed"
    $evidenceResult = Complete-EvidenceRun -Run $evidenceRun
    Write-Host "Evidence bundle: $($evidenceRun.FullPath)"
    if ($evidenceResult -eq 'FAIL') { exit 1 }
}
if ($script:failed -ne 0) { exit 1 }
