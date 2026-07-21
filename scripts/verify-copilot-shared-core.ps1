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
    [switch]$IncludeMem0Outage
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$infraDir = Join-Path $repoRoot 'infra'
$script:passed = 0
$script:failed = 0

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
        & (Join-Path $PSScriptRoot 'start-full.ps1')
        if ($LASTEXITCODE -ne 0) { throw "start-full failed with exit code $LASTEXITCODE" }
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
$marker = "shared-core-$([guid]::NewGuid().ToString('N'))"
$threadId = "shared-core-$([guid]::NewGuid().ToString('N'))"

Invoke-Case 'C-01 chat SSE + appdb history' {
    $historyBefore = @(((Invoke-JsonRequest GET "$BaseUrl/api/chat/history" $null $tokenA).Content | ConvertFrom-Json)).Count
    $stream = Invoke-JsonRequest POST "$BaseUrl/api/chat/stream" @{
        message = $marker
        conversationId = $threadId
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
    $skill = Invoke-JsonRequest POST "$BaseUrl/api/skills/rag_qa/invoke" @{
        input = @{ question = 'What is the verification marker?' }
    } $tokenA
    Assert-True ($skill.StatusCode -eq 200) "rag_qa returned $($skill.StatusCode)"
    Assert-True ($skill.Content -like "*$documentMarker*") 'rag_qa did not retrieve the uploaded marker'
}

Invoke-Case 'C-08 frontend proxy framing (browser timing still required)' {
    $chat = Invoke-JsonRequest POST "$ProxyBaseUrl/api/chat/stream" @{
        message = 'proxy chat stream'
        conversationId = "$threadId-proxy"
    } $tokenA
    Assert-True ($chat.StatusCode -eq 200 -and $chat.Content -match '(?m)^data:[^ ]') 'proxy changed chat SSE framing'
    $agui = Invoke-Agui $tokenA "$threadId-proxy" @(New-WireMessage user 'proxy AG-UI') $ProxyBaseUrl
    Assert-True ($agui.StatusCode -eq 200 -and $agui.Content -match '(?m)^data: ') 'proxy changed AG-UI SSE framing'
}

Write-Host "Result: $script:passed passed, $script:failed failed"
Write-Host 'Remaining release evidence: inspect C-03/C-04 model/session traces, C-05 mem0 storage,'
Write-Host 'run C-07 with a real routing-capable model, and verify C-08 chunk timing in a browser/curl trace.'
if ($script:failed -ne 0) { exit 1 }
