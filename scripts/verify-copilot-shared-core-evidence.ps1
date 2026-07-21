#!/usr/bin/env pwsh
# Release-evidence runner. A gate is PASS only after its executable assertions,
# safe artifact projection, and cleanup all succeed. Missing prerequisites are
# BLOCKED; an assertion failure is FAIL.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Deterministic', 'RealModel')][string]$Lane,
    [string]$EvidenceDir = '',
    # RealModel evidence is deliberately pinned.  Do not accept a floating
    # provider alias here: the LiteLLM alias below resolves to a dated snapshot.
    [string]$PlatformModel = 'evidence-gpt-4o-mini-2024-07-18',
    [string]$WorkflowModel = 'evidence-gpt-4o-mini-2024-07-18',
    [string]$Mem0Model = 'evidence-gpt-4o-mini-2024-07-18',
    [string]$EmbeddingModel = 'text-embedding-3-small',
    [string]$JwtSecret = 'dev-jwt-secret-change-me-0123456789abcdef',
    [switch]$StartEvidenceProfile,
    [switch]$BuildEvidenceProfile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$infraDir = Join-Path $repoRoot 'infra'
Import-Module (Join-Path $PSScriptRoot 'EvidenceHarness.psm1') -Force
if (-not $EvidenceDir) { $EvidenceDir = Join-Path 'artifacts/copilot-shared-core' (Get-EvidenceUtcRunId) }
$gates = if ($Lane -eq 'Deterministic') { @('E-01', 'E-02', 'E-03', 'E-06') } else { @('E-04', 'E-05') }
$run = New-EvidenceRun -RepoRoot $repoRoot -EvidenceDir $EvidenceDir -Lane $Lane -Gates $gates
$script:evidenceViteProcess = $null

function ConvertTo-Base64Url([byte[]]$Bytes) { ([Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')) }
function New-EvidenceJwt([string]$User, [string]$Tenant) {
    $header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"HS256","typ":"JWT"}'))
    $payload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes((@{ sub=$User; role='USER'; tenantCode=$Tenant; exp=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()+600 } | ConvertTo-Json -Compress)))
    $unsigned = "$header.$payload"
    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($JwtSecret))
    "$unsigned.$(ConvertTo-Base64Url ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($unsigned))))"
}
function Invoke-Json([string]$Method, [string]$Url, [object]$Body = $null, [string]$Token = '') {
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $params = @{ Method=$Method; Uri=$Url; Headers=$headers; SkipHttpErrorCheck=$true }
    if ($null -ne $Body) { $params.ContentType='application/json'; $params.Body=$Body | ConvertTo-Json -Depth 30 -Compress }
    Invoke-WebRequest @params
}
function Wait-Http([string]$Url, [int]$Attempts = 45) {
    foreach ($attempt in 1..$Attempts) {
        try { if ((Invoke-WebRequest -Uri $Url -SkipHttpErrorCheck).StatusCode -eq 200) { return $true } } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}
function ConvertTo-SafePlaywrightJUnit([string]$SourcePath, [string]$RelativePath, [string]$SuiteName) {
    if (-not (Test-Path -LiteralPath $SourcePath)) { throw "Playwright did not produce JUnit evidence for $SuiteName" }
    [xml]$xml = Get-Content -LiteralPath $SourcePath -Raw
    $suite = $xml.SelectSingleNode('//*[local-name()="testsuite"]')
    if (-not $suite) { throw "Playwright JUnit was invalid for $SuiteName" }
    $summary = [pscustomobject]@{ Tests=[int]$suite.tests; Failures=[int]$suite.failures; Skipped=[int]$suite.skipped }
    Remove-Item -LiteralPath $SourcePath -Force
    Write-EvidenceJUnit -Run $run -RelativePath $RelativePath -Suite $SuiteName -Tests $summary.Tests -Failures $summary.Failures -Skipped $summary.Skipped
    return $summary
}
function Reset-Captures([string]$Base) { (Invoke-Json DELETE "$Base/captures").StatusCode -eq 200 -or (throw 'unable to reset evidence captures') }
function Get-Captures([string]$Base) { @(((Invoke-Json GET "$Base/captures").Content | ConvertFrom-Json).captures) }
function New-UserMsg([string]$Content) { @{ id=[guid]::NewGuid().ToString('N'); role='user'; content=$Content } }
function New-AssistantMsg([string]$Id, [string]$Content) { @{ id=$Id; role='assistant'; content=$Content } }
function Invoke-Agui([string]$Base, [string]$Token, [string]$Thread, [object[]]$Messages, [object[]]$Tools = @()) {
    Invoke-Json POST "$Base/api/copilot/agui" @{ threadId=$Thread; runId=[guid]::NewGuid().ToString('N'); state=@{}; messages=$Messages; tools=$Tools; context=@(); forwardedProps=@{} } $Token
}
function Get-AguiFrames([string]$Raw) {
    @($Raw -split "`n`n" | Where-Object { $_.Trim() } | ForEach-Object { ($_ -replace '^data: ', '') | ConvertFrom-Json })
}
function Get-AssistantReply([string]$Raw) {
    $frames = Get-AguiFrames $Raw
    $start = $frames | Where-Object type -eq 'TEXT_MESSAGE_START' | Select-Object -First 1
    if (-not $start) { throw 'AG-UI response lacked TEXT_MESSAGE_START' }
    @{ Id=$start.messageId; Text=(($frames | Where-Object type -eq 'TEXT_MESSAGE_CONTENT' | ForEach-Object delta) -join '') }
}
function Invoke-EvidenceTests([string]$Gate) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("copilot-evidence-$Gate-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    try {
        $tests = 0; $failures = 0
        foreach ($project in @('platform/tests/Platform.Service.Tests/Platform.Service.Tests.csproj', 'platform/tests/Platform.Web.Tests/Platform.Web.Tests.csproj')) {
            $name = (Split-Path $project -LeafBase) + '.trx'
            dotnet test $project --no-restore --filter "EvidenceGate=$Gate" --logger "trx;LogFileName=$name" --results-directory $temp --nologo | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "dotnet test failed for $project ($Gate)" }
        }
        foreach ($trx in Get-ChildItem -LiteralPath $temp -Filter '*.trx') {
            [xml]$xml = Get-Content -LiteralPath $trx.FullName -Raw
            $results = @($xml.SelectNodes("//*[local-name()='UnitTestResult']"))
            $tests += $results.Count
            $failures += @($results | Where-Object { $_.outcome -ne 'Passed' }).Count
        }
        if ($tests -eq 0) { throw "No $Gate tests were discovered." }
        Write-EvidenceJUnit -Run $run -RelativePath "junit/$Gate.xml" -Suite "EvidenceGate.$Gate" -Tests $tests -Failures $failures
        Write-EvidenceArtifact -Run $run -RelativePath "traces/$Gate-unit.json" -Value @{ tests=$tests; failures=$failures; source='dotnet trait filter' }
        if ($failures -ne 0) { throw "$Gate had $failures failed tests." }
        return $true
    } finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
function Start-DeterministicProfile {
    $bytes = [byte[]]::new(48); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $script:evidenceHmacKey = [Convert]::ToBase64String($bytes)
    $env:EVIDENCE_HMAC_KEY = $script:evidenceHmacKey
    # E-06 needs a known source cadence. Supply it explicitly to Compose and then verify the
    # running model reports the same value before writing it into any release artifact.
    $env:EVIDENCE_FRAME_DELAY_SECONDS = '0.5'
    Push-Location $infraDir
    try {
        $composeArgs = @('-f', 'docker-compose.yml', '-f', 'docker-compose.evidence.yml', '--profile', 'evidence', 'up', '-d')
        if ($BuildEvidenceProfile) { $composeArgs += '--build' }
        $composeArgs += @('evidence-model', 'workflow-capture-proxy', 'platform-evidence', 'evidence-nginx', 'frontend-evidence')
        docker compose @composeArgs
        if ($LASTEXITCODE -ne 0) { throw 'Unable to start optional evidence profile.' }
    } finally {
        Pop-Location
        Remove-Item Env:EVIDENCE_HMAC_KEY, Env:EVIDENCE_FRAME_DELAY_SECONDS -ErrorAction SilentlyContinue
    }
    if (-not (Wait-Http 'http://127.0.0.1:4010/health') -or -not (Wait-Http 'http://127.0.0.1:8180/actuator/health')) { throw 'Evidence profile did not become ready.' }
    $modelHealth = (Invoke-Json GET 'http://127.0.0.1:4010/health').Content | ConvertFrom-Json
    if ($null -eq $modelHealth.frameDelaySeconds -or [math]::Abs(([double]$modelHealth.frameDelaySeconds) - 0.5) -gt 0.001) {
        throw 'Evidence model did not confirm the required 0.5 second frame delay.'
    }
    $script:controlledFrameDelayMs = [int][math]::Round(([double]$modelHealth.frameDelaySeconds) * 1000)
    # Local builds commonly have no RepoDigest. Docker's immutable image ID is still a
    # sha256 digest and is enough to identify the exact evidence image used by this run.
    Push-Location $infraDir
    try {
        foreach ($service in @('platform-evidence', 'frontend-evidence', 'evidence-model', 'evidence-nginx')) {
            # Query Docker labels directly: evaluating the evidence compose file
            # after startup would require re-exposing the per-run HMAC key.
            $containerId = @(
                docker ps -q --filter 'label=com.docker.compose.project=springaitest' --filter "label=com.docker.compose.service=$service"
            ) | Select-Object -First 1
            if (-not $containerId) { throw "Evidence image container is missing: $service" }
            $imageId = [string](docker inspect --format '{{.Image}}' $containerId)
            $imageId = $imageId.Trim()
            if ($imageId -notmatch '^sha256:[0-9a-f]{64}$') { throw "Evidence image ID is invalid: $service" }
            Set-EvidenceMetadata -Run $run -Section images -Name $service -Value $imageId
        }
    } finally { Pop-Location }
}
function Get-MarkerHmac([string]$Marker) {
    # Must match evidence_model.py keyed_hmac() for a JSON string value.
    $canonical = '"' + $Marker.Replace('\\', '\\\\').Replace('"', '\\"') + '"'
    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($script:evidenceHmacKey))
    'hmac:' + ([Convert]::ToHexString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant())
}
function Get-CanonicalObjectHmac([string]$Json) {
    # Evidence model hashes parsed JSON with sorted keys and compact separators.
    # Gate fixtures are intentionally flat objects, so stable key ordering here
    # avoids treating harmless wire whitespace as a different tool argument.
    $object = $Json | ConvertFrom-Json -AsHashtable
    $ordered = [ordered]@{}
    foreach ($key in ($object.Keys | Sort-Object)) { $ordered[$key] = $object[$key] }
    $canonical = $ordered | ConvertTo-Json -Compress -Depth 20
    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($script:evidenceHmacKey))
    'hmac:' + ([Convert]::ToHexString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant())
}
function ConvertTo-SqlLiteral([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }
function Register-DeterministicBrowserUser([string]$Base, [pscustomobject]$Credential, [string]$InviteCode) {
    $response = Invoke-Json POST "$Base/api/auth/register" @{
        username=$Credential.Username; password=$Credential.Password; tenantCode=$Credential.TenantCode; inviteCode=$InviteCode
    }
    if ($response.StatusCode -ne 201) { throw 'unable to register the run-scoped browser evidence user' }
}
function Set-ProcessEnvironment([hashtable]$Values) {
    $previous = @{}
    foreach ($name in $Values.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        Set-Item "Env:$name" ([string]$Values[$name])
    }
    return $previous
}
function Restore-ProcessEnvironment([hashtable]$Previous) {
    foreach ($name in $Previous.Keys) {
        if ($null -eq $Previous[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" ([string]$Previous[$name]) }
    }
}
function Cleanup-DeterministicRun([string[]]$ConversationUsers, [string[]]$RegisteredUsers) {
    # Every identity is generated for this invocation.  Do not use a timestamp or
    # watermark: either can catch another evidence run's or a developer's row.
    $conversationValues = (@($ConversationUsers | Where-Object { $_ } | ForEach-Object { ConvertTo-SqlLiteral $_ }) -join ',')
    $userValues = (@($RegisteredUsers | Where-Object { $_ } | ForEach-Object { ConvertTo-SqlLiteral $_ }) -join ',')
    if (-not $conversationValues -or -not $userValues) { throw 'deterministic cleanup identities were incomplete' }
    $sql = "DELETE FROM conversations WHERE user_id IN ($conversationValues); DELETE FROM users WHERE username IN ($userValues);"
    Push-Location $infraDir
    try { docker compose exec -T appdb psql -v ON_ERROR_STOP=1 -U postgres -d springaitest -c $sql | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'deterministic cleanup failed' } }
    finally { Pop-Location }
}
function Get-CompletedSseEventsFromCurlTrace([string]$TracePath) {
    # curl's ASCII trace annotates each received block with its receipt timestamp. Accumulate
    # data until a complete SSE event (`\n\n` or `\r\n\r\n`) arrives, so a proxy's arbitrary
    # TCP chunk boundaries cannot turn into spurious application frames.
    $pending = ''
    $currentTime = $null
    $receivingData = $false
    $events = [Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $TracePath) {
        if ($line -match '^(?<stamp>\d{2}:\d{2}:\d{2}\.\d{6}) <= Recv data') {
            $currentTime = [DateTime]::ParseExact($matches.stamp, 'HH:mm:ss.ffffff', [Globalization.CultureInfo]::InvariantCulture).TimeOfDay.TotalMilliseconds
            $receivingData = $true
            continue
        }
        if ($receivingData -and $line -match '^[0-9A-Fa-f]{4}:\s+(?<hex>(?:[0-9A-Fa-f]{2}(?:\s|$)){1,16})') {
            # Binary curl traces preserve CR/LF as bytes. --trace-ascii renders
            # them as dots, which is ambiguous with literal JSON punctuation.
            $hexBytes = @($matches.hex.Trim() -split '\s+' | Where-Object { $_ })
            $pending += -join @($hexBytes | ForEach-Object { [char][Convert]::ToByte($_, 16) })
            while ($true) {
                $separator = [regex]::Match($pending, "\r?\n\r?\n")
                if (-not $separator.Success) { break }
                $rawEvent = $pending.Substring(0, $separator.Index)
                $pending = $pending.Substring($separator.Index + $separator.Length)
                $eventName = $null
                $data = [Collections.Generic.List[string]]::new()
                foreach ($eventLine in ($rawEvent -split "\r?\n")) {
                    if ($eventLine.StartsWith('data:')) { $data.Add($eventLine.Substring(5).TrimStart(' ')) }
                    elseif ($eventLine.StartsWith('event:')) { $eventName = $eventLine.Substring(6).TrimStart(' ') }
                }
                if ($data.Count -gt 0) {
                    $events.Add([pscustomobject]@{ OffsetMs=[double]$currentTime; Event=$eventName; Data=($data -join "`n") })
                }
            }
            continue
        }
        if ($line -match '^\d{2}:\d{2}:\d{2}\.\d{6} [=>]') { $receivingData = $false }
    }
    return @($events)
}
function Get-ControlledCurlContentFrames([object[]]$Events, [ValidateSet('Chat', 'Agui')][string]$Kind, [string[]]$ControlledContent) {
    $frames = [Collections.Generic.List[object]]::new()
    foreach ($frame in $Events) {
        if ($Kind -eq 'Chat') {
            $content = ([string]$frame.Data).Trim()
            if ($frame.Event -ne 'error' -and $content -ne '[DONE]' -and $ControlledContent -contains $content) {
                $frames.Add([pscustomobject]@{ Content=$content; OffsetMs=[double]$frame.OffsetMs })
            }
            continue
        }
        try { $agui = ([string]$frame.Data | ConvertFrom-Json -ErrorAction Stop) } catch { continue }
        if ($agui.type -ne 'TEXT_MESSAGE_CONTENT' -or $agui.delta -isnot [string]) { continue }
        $content = $agui.delta.Trim()
        if ($ControlledContent -contains $content) {
            $frames.Add([pscustomobject]@{ Content=$content; OffsetMs=[double]$frame.OffsetMs })
        }
    }
    return @($frames)
}
function Invoke-CurlStreamEvidence([string]$Name, [string]$Url, [string]$Body, [string]$FramePattern, [ValidateSet('Chat', 'Agui')][string]$Kind, [string[]]$ControlledContent, [string]$Token = '') {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("copilot-evidence-e06-$Name-" + [guid]::NewGuid().ToString('N'))
    $tracePath = "$temp.trace"; $bodyPath = "$temp.body"
    try {
        $arguments = @('--no-buffer', '--silent', '--show-error', '--trace-time', '--trace', $tracePath, '--output', $bodyPath, '-X', 'POST', '-H', 'Accept: text/event-stream', '-H', 'Content-Type: application/json')
        if ($Token) { $arguments += @('-H', "Authorization: Bearer $Token") }
        $arguments += @('--data', $Body, $Url)
        & curl.exe @arguments
        if ($LASTEXITCODE -ne 0) { throw "curl failed for $Name" }

        $body = Get-Content -LiteralPath $bodyPath -Raw
        if ($body -notmatch $FramePattern) { throw "curl changed $Name SSE framing" }
        $frames = Get-ControlledCurlContentFrames (Get-CompletedSseEventsFromCurlTrace $tracePath) $Kind $ControlledContent
        if ($frames.Count -lt 3) { throw "curl observed fewer than three controlled $Name content frames" }
        $actualContent = @($frames | ForEach-Object Content)
        if (@(Compare-Object -ReferenceObject $ControlledContent -DifferenceObject $actualContent -SyncWindow 0).Count -ne 0) {
            throw "curl $Name controlled content frames did not match the model fixture"
        }
        $firstToLastMs = $frames[$frames.Count - 1].OffsetMs - $frames[0].OffsetMs
        if ($firstToLastMs -lt 400) { throw "curl $Name SSE frames were buffered ($firstToLastMs ms)" }
        return @{ controlledContentFrames=$frames.Count; firstToLastMs=[math]::Round($firstToLastMs, 3) }
    } finally {
        Remove-Item -LiteralPath $tracePath, $bodyPath -Force -ErrorAction SilentlyContinue
    }
}
function Invoke-BrowserStreamEvidence([string]$ProxyName, [string]$BaseUrl, [object[]]$Cases) {
    if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) { throw 'NODE_UNAVAILABLE' }
    $saved = @{}
    foreach ($name in @('EVIDENCE_DIR', 'PW_BASE_URL', 'EVIDENCE_STREAM_CASES_JSON', 'EVIDENCE_STREAM_PATH', 'EVIDENCE_STREAM_PREFIX', 'EVIDENCE_STREAM_METHOD', 'EVIDENCE_STREAM_BODY_JSON', 'EVIDENCE_STREAM_USE_SESSION')) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    $playwrightArtifactDir = Join-Path $run.FullPath 'playwright'
    try {
        $env:EVIDENCE_DIR = $run.FullPath; $env:PW_BASE_URL = $BaseUrl
        $env:EVIDENCE_STREAM_CASES_JSON = $Cases | ConvertTo-Json -Depth 12 -Compress
        Remove-Item Env:EVIDENCE_STREAM_PATH, Env:EVIDENCE_STREAM_PREFIX, Env:EVIDENCE_STREAM_METHOD, Env:EVIDENCE_STREAM_BODY_JSON, Env:EVIDENCE_STREAM_USE_SESSION -ErrorAction SilentlyContinue
        Push-Location (Join-Path $repoRoot 'frontend')
        try { $playwrightOutput = (npm run test:evidence -- e2e/streaming.evidence.spec.ts 2>&1 | Out-String); $playwrightOutput | Out-Host; $code = $LASTEXITCODE }
        finally { Pop-Location }
        if ($code -ne 0 -and $playwrightOutput -match 'Executable doesn.t exist|browserType.launch') { throw 'BROWSER_UNAVAILABLE' }
        $junitPath = Join-Path $run.FullPath 'junit/playwright.xml'
        if (-not (Test-Path $junitPath)) { throw "Playwright did not produce JUnit evidence for $ProxyName" }
        $safeJUnit = ConvertTo-SafePlaywrightJUnit $junitPath "junit/e06-$ProxyName.xml" "EvidenceGate.E-06.$ProxyName"
        $browserTests = $safeJUnit.Tests; $browserSkipped = $safeJUnit.Skipped
        if ($code -ne 0) { throw "Playwright browser streaming assertion failed for $ProxyName" }
        if ($browserTests -ne 1 -or $browserSkipped -ne 0) { throw 'BROWSER_SKIPPED' }
        return @{ tests=$browserTests; cases=$Cases.Count }
    } finally {
        foreach ($name in $saved.Keys) {
            if ($null -eq $saved[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
            else { Set-Item "Env:$name" $saved[$name] }
        }
        if ((Test-Path -LiteralPath $playwrightArtifactDir) -and $playwrightArtifactDir.StartsWith($run.FullPath, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $playwrightArtifactDir -Recurse -Force }
    }
}
function Start-EvidenceViteProxy {
    if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) { throw 'NODE_UNAVAILABLE' }
    $script:evidenceViteProcess = Start-Process -FilePath 'npm.cmd' -ArgumentList @('run', 'dev', '--', '--host', '127.0.0.1', '--port', '5181', '--strictPort') -WorkingDirectory (Join-Path $repoRoot 'frontend') -PassThru -WindowStyle Hidden
    if (-not (Wait-Http 'http://127.0.0.1:5181/')) { throw 'Temporary Vite evidence proxy did not become ready.' }
}
function Stop-EvidenceViteProxy {
    if ($script:evidenceViteProcess) {
        & taskkill.exe /T /F /PID $script:evidenceViteProcess.Id | Out-Null
        Wait-Process -Id $script:evidenceViteProcess.Id -ErrorAction SilentlyContinue
        $script:evidenceViteProcess = $null
    }
}
function Invoke-Deterministic {
    $unit02 = Invoke-EvidenceTests 'E-02'
    $unit03 = Invoke-EvidenceTests 'E-03'
    if (-not $StartEvidenceProfile) {
        Set-EvidenceGate -Run $run -Gate E-01 -Status BLOCKED -Detail 'E-01 browser evidence requires -StartEvidenceProfile.'
        Set-EvidenceGate -Run $run -Gate E-02 -Status BLOCKED -Detail 'E-02 unit tests passed, but -StartEvidenceProfile was not supplied for canonical model-input capture.'
        Set-EvidenceGate -Run $run -Gate E-03 -Status BLOCKED -Detail 'E-03 unit tests passed, but -StartEvidenceProfile was not supplied for canonical model-input capture.'
        Set-EvidenceGate -Run $run -Gate E-06 -Status BLOCKED -Detail 'Evidence nginx/browser profile was not started.'
        return
    }
    Start-DeterministicProfile
    $base='http://127.0.0.1:8180'; $capture='http://127.0.0.1:4010'; $users=@("e02a-$([guid]::NewGuid().ToString('N'))", "e02b-$([guid]::NewGuid().ToString('N'))")
    $browserUsers = @{
        a = [pscustomobject]@{ Username="e01a-$([guid]::NewGuid().ToString('N'))"; Password='password123'; TenantCode='demo-a' }
        b = [pscustomobject]@{ Username="e01b-$([guid]::NewGuid().ToString('N'))"; Password='password123'; TenantCode='demo-b' }
    }
    $allConversationUsers = @($users + @($browserUsers.a.Username, $browserUsers.b.Username))
    # E-01/E-03/E-06 run under accounts created exclusively for this run.  Their
    # environment overrides are restored after each Playwright invocation.
    try {
        Register-DeterministicBrowserUser $base $browserUsers.a 'demo-a-invite'
        Register-DeterministicBrowserUser $base $browserUsers.b 'demo-b-invite'
    # E-01 is a real browser flow against frontend-evidence/platform-evidence.
    # Retain only its safe JUnit and screenshots; Playwright traces stay disabled.
    $e01Environment = Set-ProcessEnvironment @{ EVIDENCE_DIR=$run.FullPath; PW_BASE_URL='http://127.0.0.1:5180'; EVIDENCE_USER_A=$browserUsers.a.Username; EVIDENCE_PASSWORD_A=$browserUsers.a.Password; EVIDENCE_TENANT_A=$browserUsers.a.TenantCode; EVIDENCE_USER_B=$browserUsers.b.Username; EVIDENCE_PASSWORD_B=$browserUsers.b.Password; EVIDENCE_TENANT_B=$browserUsers.b.TenantCode }
    try {
        $playwrightArtifactDir=Join-Path $run.FullPath 'playwright'
        Push-Location (Join-Path $repoRoot 'frontend')
        try { $playwrightOutput=(npm run test:evidence -- e2e/auth.evidence.spec.ts 2>&1 | Out-String); $playwrightOutput | Out-Host; $code=$LASTEXITCODE }
        finally { Pop-Location }
        $junitPath=Join-Path $run.FullPath 'junit/playwright.xml'
        $safeJUnit=ConvertTo-SafePlaywrightJUnit $junitPath 'junit/e01-browser.xml' 'EvidenceGate.E-01.browser'; $browserTests=$safeJUnit.Tests; $browserSkipped=$safeJUnit.Skipped
        if($code -ne 0){if($playwrightOutput -match 'Executable doesn.t exist|browserType.launch'){throw 'BROWSER_UNAVAILABLE'};throw 'Playwright browser authentication assertion failed'}; if($browserTests -ne 2 -or $browserSkipped -ne 0){throw 'BROWSER_SKIPPED'}
        $index=0; if(Test-Path -LiteralPath $playwrightArtifactDir){Get-ChildItem -LiteralPath $playwrightArtifactDir -Recurse -Filter '*.png' | ForEach-Object { $index++; Move-Item -LiteralPath $_.FullName -Destination (Join-Path $run.FullPath "screenshots/e01-$index.png") -Force }; Remove-Item -LiteralPath $playwrightArtifactDir -Recurse -Force}
        Write-EvidenceArtifact -Run $run -RelativePath 'requests/e01-browser-summary.json' -Value @{ tests=$browserTests; skipped=$browserSkipped; screenshots=$index; target='frontend-evidence to platform-evidence' }
        Set-EvidenceGate -Run $run -Gate E-01 -Status PASS -Detail 'Two real browser account-switch and AG-UI 401 logout flows passed against the evidence frontend/platform.'
    } catch { if($_.Exception.Message -match '^BROWSER_(UNAVAILABLE|SKIPPED)'){Set-EvidenceGate -Run $run -Gate E-01 -Status BLOCKED -Detail $_.Exception.Message}else{Set-EvidenceGate -Run $run -Gate E-01 -Status FAIL -Detail $_.Exception.Message} }
    finally { Restore-ProcessEnvironment $e01Environment }
    try {
        Reset-Captures $capture
        $tokenA=New-EvidenceJwt $users[0] 'demo-a'; $tokenB=New-EvidenceJwt $users[1] 'demo-b'; $thread="e02-$([guid]::NewGuid().ToString('N'))"; $markerA="e02a_$([guid]::NewGuid().ToString('N'))"; $markerB="e02b_$([guid]::NewGuid().ToString('N'))"
        if ((Invoke-Agui $base $tokenA $thread @((New-UserMsg $markerA))).StatusCode -ne 200) { throw 'tenant A AG-UI failed' }
        $aCaptures=Get-Captures $capture; $aToken=Get-MarkerHmac $markerA
        if (-not @($aCaptures | Where-Object { $_.messages.tokenHmacs -contains $aToken })) { throw 'tenant A marker was absent from canonical model capture' }
        $aCaptureCount=$aCaptures.Count
        if ((Invoke-Agui $base $tokenB $thread @((New-UserMsg $markerB))).StatusCode -ne 200) { throw 'tenant B AG-UI failed' }
        $bToken=Get-MarkerHmac $markerB; $bCaptures=@(Get-Captures $capture | Select-Object -Skip $aCaptureCount)
        if (-not @($bCaptures | Where-Object { $_.messages.tokenHmacs -contains $bToken })) { throw 'tenant B marker was absent from canonical model capture' }
        if (@($bCaptures | Where-Object { $_.messages.tokenHmacs -contains $aToken })) { throw 'tenant B canonical model input contained tenant A marker' }
        Write-EvidenceArtifact -Run $run -RelativePath 'traces/e02-model-input.json' -Value @{ aMarkerHmac=$aToken; bMarkerHmac=$bToken; bCaptureCount=$bCaptures.Count; tenantAMarkerMatchesInB=0 }
        Set-EvidenceGate -Run $run -Gate E-02 -Status PASS -Detail 'Trait tests plus same-thread cross-tenant canonical model-input token-HMAC assertion passed.'

        Reset-Captures $capture; $thread="e03-$([guid]::NewGuid().ToString('N'))"; $wire=[Collections.Generic.List[object]]::new(); $markers=@(); $counts=@()
        foreach ($round in 1..3) {
            $marker="e03_${round}_$([guid]::NewGuid().ToString('N'))"; $markers += $marker; $wire.Add((New-UserMsg $marker))
            $response=Invoke-Agui $base $tokenA $thread $wire.ToArray(); if($response.StatusCode -ne 200){throw "E-03 round $round failed"}
            $reply=Get-AssistantReply $response.Content; $wire.Add((New-AssistantMsg $reply.Id $reply.Text))
            $markerHmacs=@($markers | ForEach-Object { Get-MarkerHmac $_ })
            $roundCapture=Get-Captures $capture | Where-Object { $tokens=@($_.messages.tokenHmacs); (@($markerHmacs | Where-Object { $tokens -contains $_ }).Count -eq $markerHmacs.Count) } | Sort-Object messageCount -Descending | Select-Object -First 1
            if(-not $roundCapture){throw "round $round canonical chat capture missing"}; $counts += $roundCapture.messageCount
        }
        $last=Get-Captures $capture | Where-Object { $tokens=@($_.messages.tokenHmacs); (@($markers | ForEach-Object { Get-MarkerHmac $_ } | Where-Object { $tokens -contains $_ }).Count -eq $markers.Count) } | Sort-Object messageCount -Descending | Select-Object -First 1
        foreach($marker in $markers){$h=Get-MarkerHmac $marker; if((@($last.messages.tokenHmacs | Where-Object {$_ -eq $h}).Count) -ne 1){throw 'full-array resend did not preserve exactly one marker'}}; if($counts[2] -gt ($counts[1]+2) -or $counts[1] -gt ($counts[0]+2)){throw 'full-array resend model input did not grow linearly'}
        # Drive the client tool through the actual CopilotKit browser action. The
        # browser test observes switchView's rendered state and the handler's own
        # result; this runner then proves the canonical model input contains one
        # matching call/result id, with no orphan result.
        Reset-Captures $capture; $toolMarker="__evidence_tool_call__ e03tool_$([guid]::NewGuid().ToString('N'))"
        $browserToolPassed=$false
        try {
            $e03Environment = Set-ProcessEnvironment @{ EVIDENCE_DIR=$run.FullPath; PW_BASE_URL='http://127.0.0.1:5180'; EVIDENCE_TOOL_PROMPT=$toolMarker; EVIDENCE_USER_A=$browserUsers.a.Username; EVIDENCE_PASSWORD_A=$browserUsers.a.Password; EVIDENCE_TENANT_A=$browserUsers.a.TenantCode }
            $playwrightArtifactDir=Join-Path $run.FullPath 'playwright'
            Push-Location (Join-Path $repoRoot 'frontend')
            try { $playwrightOutput=(npm run test:evidence -- e2e/session-tool.evidence.spec.ts 2>&1 | Out-String); $playwrightOutput | Out-Host; $code=$LASTEXITCODE }
            finally { Pop-Location; Restore-ProcessEnvironment $e03Environment }
            $junitPath=Join-Path $run.FullPath 'junit/playwright.xml'
            $safeJUnit=ConvertTo-SafePlaywrightJUnit $junitPath 'junit/e03-browser.xml' 'EvidenceGate.E-03.browser'; $browserTests=$safeJUnit.Tests; $browserSkipped=$safeJUnit.Skipped
            if($code -ne 0){if($playwrightOutput -match 'Executable doesn.t exist|browserType.launch'){throw 'BROWSER_UNAVAILABLE'};throw 'Playwright browser client-tool assertion failed'}; if($browserTests -ne 1 -or $browserSkipped -ne 0){throw 'BROWSER_SKIPPED'}
            if(Test-Path -LiteralPath $playwrightArtifactDir){Remove-Item -LiteralPath $playwrightArtifactDir -Recurse -Force}
            $toolCapture = @()
            foreach ($attempt in 1..20) {
                $toolCapture = @(Get-Captures $capture | Where-Object { @($_.messages | Where-Object { $_.toolCallIdHmac }).Count -gt 0 } | Select-Object -Last 1)
                if ($toolCapture.Count -eq 1) { break }
                Start-Sleep -Milliseconds 250
            }
            if($toolCapture.Count -ne 1){throw 'canonical model capture lacked a final browser tool-result request'}
            $assistantCalls=@($toolCapture[0].messages.toolCalls | ForEach-Object { $_ })
            $toolResults=@($toolCapture[0].messages | Where-Object { $_.toolCallIdHmac })
            if($assistantCalls.Count -ne 1 -or $toolResults.Count -ne 1){throw 'canonical model capture did not contain exactly one browser tool call/result'}
            $callHmac=[string]$assistantCalls[0].idHmac
            if([string]::IsNullOrWhiteSpace($callHmac) -or [string]$toolResults[0].toolCallIdHmac -ne $callHmac){throw 'canonical model capture tool call/result ids did not match'}
            $switchViewHmac=Get-MarkerHmac 'switchView'; $documentsArgumentsHmac=Get-CanonicalObjectHmac '{"view":"documents"}'
            if([string]$assistantCalls[0].nameHmac -ne $switchViewHmac){throw 'canonical model capture did not contain the controlled switchView call'}
            if([string]$assistantCalls[0].canonicalArgumentsHmac -ne $documentsArgumentsHmac){throw 'canonical model capture did not contain the controlled documents argument'}
            $knownCallIds=@($assistantCalls | ForEach-Object { [string]$_.idHmac })
            if(@($toolResults | Where-Object { $knownCallIds -notcontains [string]$_.toolCallIdHmac }).Count -ne 0){throw 'canonical model capture contained an orphan tool result'}
            $browserToolPassed=$true
            Write-EvidenceArtifact -Run $run -RelativePath 'traces/e03-model-input.json' -Value @{ messageCounts=$counts; uniqueUserMarkerCount=$markers.Count; browserTests=$browserTests; browserSkipped=$browserSkipped; toolCallIdHmac=$callHmac; actionNameHmac=$switchViewHmac; argumentsHmac=$documentsArgumentsHmac; matchedToolCallCount=$assistantCalls.Count; matchedToolResultCount=$toolResults.Count; orphanToolResultCount=0 }
        } catch {
            if((Test-Path -LiteralPath $playwrightArtifactDir) -and $playwrightArtifactDir.StartsWith($run.FullPath,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $playwrightArtifactDir -Recurse -Force}
            if($_.Exception.Message -match '^BROWSER_(UNAVAILABLE|SKIPPED)'){Set-EvidenceGate -Run $run -Gate E-03 -Status BLOCKED -Detail $_.Exception.Message}else{Set-EvidenceGate -Run $run -Gate E-03 -Status FAIL -Detail $_.Exception.Message}
        }
        if($browserToolPassed){Set-EvidenceGate -Run $run -Gate E-03 -Status PASS -Detail 'Trait tests, full-array canonical model-input checks, and one real CopilotKit browser client-tool call with one logical wire result passed.'}
    } catch { Set-EvidenceGate -Run $run -Gate E-02 -Status FAIL -Detail $_.Exception.Message; Set-EvidenceGate -Run $run -Gate E-03 -Status FAIL -Detail $_.Exception.Message }
    # E-06 exercises both deliberate SSE formats through a production-like nginx image
    # and a fresh Vite dev proxy. Trace/body temp files are deleted before artifacts are
    # written; only safe counts and relative timing projections are retained.
    $e06Environment = Set-ProcessEnvironment @{ EVIDENCE_USER_A=$browserUsers.a.Username; EVIDENCE_PASSWORD_A=$browserUsers.a.Password; EVIDENCE_TENANT_A=$browserUsers.a.TenantCode }
    try {
        $chatBody = @{message="e06_$([guid]::NewGuid().ToString('N'))";conversationId="e06-$([guid]::NewGuid().ToString('N'))"} | ConvertTo-Json -Compress
        $aguiToken = New-EvidenceJwt $users[0] 'demo-a'
        $aguiBody = @{threadId="e06-$([guid]::NewGuid().ToString('N'))";runId=[guid]::NewGuid().ToString('N');state=@{};messages=@(@{id=[guid]::NewGuid().ToString('N');role='user';content="e06_$([guid]::NewGuid().ToString('N'))"});tools=@();context=@();forwardedProps=@{}} | ConvertTo-Json -Depth 12 -Compress
        $controlledContent = @('evidence', 'model', 'reply')
        $browserCases = @(
            @{name='chat';path='/api/chat/stream';expectedFramePrefix='data:';streamKind='chat';controlledContent=$controlledContent;method='POST';body=$chatBody;useBrowserSession=$false},
            @{name='agui';path='/api/copilot/agui';expectedFramePrefix='data: ';streamKind='agui';controlledContent=$controlledContent;method='POST';body=$aguiBody;useBrowserSession=$true}
        )
        $nginx = @{
            chat = Invoke-CurlStreamEvidence 'nginx-chat' 'http://127.0.0.1:8181/api/chat/stream' $chatBody '(?m)^data:evidence\r?$' -Kind Chat -ControlledContent $controlledContent
            agui = Invoke-CurlStreamEvidence 'nginx-agui' 'http://127.0.0.1:8181/api/copilot/agui' $aguiBody '(?m)^data: \{' -Kind Agui -ControlledContent $controlledContent -Token $aguiToken
            browser = Invoke-BrowserStreamEvidence 'nginx' 'http://127.0.0.1:5180' $browserCases
        }
        $oldViteTarget = $env:VITE_API_PROXY_TARGET; $hadViteTarget = Test-Path Env:VITE_API_PROXY_TARGET
        try {
            $env:VITE_API_PROXY_TARGET = 'http://127.0.0.1:8180'
            Start-EvidenceViteProxy
            $vite = @{
                chat = Invoke-CurlStreamEvidence 'vite-chat' 'http://127.0.0.1:5181/api/chat/stream' $chatBody '(?m)^data:evidence\r?$' -Kind Chat -ControlledContent $controlledContent
                agui = Invoke-CurlStreamEvidence 'vite-agui' 'http://127.0.0.1:5181/api/copilot/agui' $aguiBody '(?m)^data: \{' -Kind Agui -ControlledContent $controlledContent -Token $aguiToken
                browser = Invoke-BrowserStreamEvidence 'vite' 'http://127.0.0.1:5181' $browserCases
            }
        } finally {
            Stop-EvidenceViteProxy
            if ($hadViteTarget) { $env:VITE_API_PROXY_TARGET = $oldViteTarget } else { Remove-Item Env:VITE_API_PROXY_TARGET -ErrorAction SilentlyContinue }
        }
        Write-EvidenceArtifact -Run $run -RelativePath 'requests/e06-sse-summary.json' -Value @{ controlledFrameDelayMs=$script:controlledFrameDelayMs; nginx=$nginx; vite=$vite }
        Set-EvidenceGate -Run $run -Gate E-06 -Status PASS -Detail 'chat data: and authenticated AG-UI data: streams passed curl timing and browser ReadableStream checks through nginx and Vite.'
    } catch { if($_.Exception.Message -match '^(BROWSER_(UNAVAILABLE|SKIPPED)|NODE_UNAVAILABLE)'){Set-EvidenceGate -Run $run -Gate E-06 -Status BLOCKED -Detail $_.Exception.Message}else{Set-EvidenceGate -Run $run -Gate E-06 -Status FAIL -Detail $_.Exception.Message} }
    finally { Restore-ProcessEnvironment $e06Environment }
    } catch {
        # Account creation is a test prerequisite.  Keep the fixed manifest detail
        # safe rather than letting a transport exception include a response body.
        foreach ($gate in @('E-01', 'E-02', 'E-03', 'E-06')) {
            Set-EvidenceGate -Run $run -Gate $gate -Status BLOCKED -Detail 'Deterministic run-scoped account setup did not complete.'
        }
    } finally {
        try { Cleanup-DeterministicRun $allConversationUsers @($browserUsers.a.Username, $browserUsers.b.Username) }
        catch {
            # A PASS requires the generated state to be gone.  Do not make a
            # best-effort cleanup failure look like a valid release assertion.
            foreach ($gate in @('E-01', 'E-02', 'E-03', 'E-06')) {
                Set-EvidenceGate -Run $run -Gate $gate -Status BLOCKED -Detail 'Deterministic run-scoped cleanup failed.'
            }
        }
    }
}

# RealModel lane --------------------------------------------------------------
# All values written to the evidence bundle below are either public deployment
# identifiers, counters/statuses, or keyed HMACs.  In particular, never write a
# JWT, a generated marker, a document body, a mem0 result, or an HTTP body.
$script:realModelAlias = 'evidence-gpt-4o-mini-2024-07-18'
$script:realEmbeddingAlias = 'text-embedding-3-small'
$script:realEnvSnapshot = @{}
$script:realServicesChanged = $false

function Get-RealHmac([string]$Value) {
    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($script:realEvidenceHmacKey))
    'hmac:' + ([Convert]::ToHexString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant())
}
function Set-RealEvidenceEnvironment {
    $names = @('EVIDENCE_HMAC_KEY','EVIDENCE_REAL_MEM0_MODEL','EVIDENCE_REAL_EMBEDDING_MODEL','EVIDENCE_BACKEND_EMBEDDINGS_PROVIDER','EVIDENCE_REAL_WORKFLOW_MODEL','EVIDENCE_PLATFORM_LLM_BASE_URL','EVIDENCE_PLATFORM_LITELLM_KEY','EVIDENCE_PLATFORM_CHAT_MODEL','EVIDENCE_PLATFORM_MEM0_MODE')
    foreach ($name in $names) { $script:realEnvSnapshot[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $bytes = [byte[]]::new(48); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $script:realEvidenceHmacKey = [Convert]::ToBase64String($bytes)
    $env:EVIDENCE_HMAC_KEY = $script:realEvidenceHmacKey
    $env:EVIDENCE_REAL_MEM0_MODEL = $script:realModelAlias
    $env:EVIDENCE_REAL_EMBEDDING_MODEL = $script:realEmbeddingAlias
    $env:EVIDENCE_BACKEND_EMBEDDINGS_PROVIDER = 'openai'
    $env:EVIDENCE_REAL_WORKFLOW_MODEL = $script:realModelAlias
    $env:EVIDENCE_PLATFORM_LLM_BASE_URL = 'http://litellm:4000'
    $env:EVIDENCE_PLATFORM_LITELLM_KEY = 'sk-1234'
    $env:EVIDENCE_PLATFORM_CHAT_MODEL = $script:realModelAlias
    $env:EVIDENCE_PLATFORM_MEM0_MODE = 'http'
}
function Restore-RealEvidenceEnvironment {
    foreach ($entry in $script:realEnvSnapshot.GetEnumerator()) {
        if ($null -eq $entry.Value) { Remove-Item "Env:$($entry.Key)" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$($entry.Key)" $entry.Value }
    }
}
function Test-RealProviderCredential {
    if (-not [string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) { return $true }
    $envFile = Join-Path $infraDir '.env'
    return (Test-Path -LiteralPath $envFile) -and [bool](Select-String -LiteralPath $envFile -Pattern '^\s*OPENAI_API_KEY\s*=\s*[^#\s].*$' -Quiet)
}
function Throw-RealBlocked([string]$Reason) { throw "REAL_BLOCKED:$Reason" }
function Get-SafeRealFailureDetail([Exception]$Exception) {
    # HTTP/JSON libraries may echo a response fragment in their exception text.
    # Only retain our own fixed assertion text in the manifest; otherwise use a
    # generic failure so prompts, replies, and JWT-bearing request context never
    # become an artifact through an exceptional path.
    $message = $Exception.Message
    if ($message -like 'REAL_BLOCKED:*' -or $message -match '^(chat|embedding|history|AG-UI|mem0|document|real evidence|chat routing|AG-UI routing|chat and AG-UI|chat reply|AG-UI reply|workflow|LiteLLM|Authenticated|native TOOL_CALL|unexpected)') { return $message }
    return 'unexpected real evidence assertion error'
}
function Test-RealService([string]$Url, [string]$Name) {
    if (-not (Wait-Http $Url 8)) { Throw-RealBlocked "$Name service is unavailable" }
}
function Invoke-LiteLlmPreflight([string]$Path, [object]$Body, [string]$Kind) {
    # LiteLLM's local virtual key is a compose development credential, not the
    # provider key.  It is never logged or written to the evidence bundle.
    $response = Invoke-Json POST "http://127.0.0.1:4000/v1/$Path" $Body 'sk-1234'
    if ($response.StatusCode -ne 200) {
        if ($response.StatusCode -in @(401,403,404,429,502,503,504)) { Throw-RealBlocked "$Kind provider is unavailable" }
        throw "$Kind LiteLLM preflight returned HTTP $($response.StatusCode)"
    }
    try { $doc = $response.Content | ConvertFrom-Json } catch { throw "$Kind LiteLLM preflight returned invalid JSON" }
    $actual = [string]$doc.model
    if ([string]::IsNullOrWhiteSpace($actual)) { throw "$Kind LiteLLM preflight omitted model metadata" }
    # LiteLLM may normalise response.model to the provider's public model name
    # (for example gpt-4o-mini) even when request.model used the fixed local
    # alias.  Pinning is therefore asserted by the requested alias and compose
    # config, while this field is retained as provider-returned metadata.
    [pscustomobject]@{ Model=$actual; SystemFingerprint=([string]$doc.system_fingerprint) }
}
function Start-RealProfile {
    Set-RealEvidenceEnvironment
    $script:realServicesChanged = $true
    Push-Location $infraDir
    try {
        # The model map is bind-mounted. Compose cannot detect content changes
        # in litellm-config.yaml, so explicitly recreate LiteLLM to guarantee
        # that the dated evidence alias used below is actually loaded.
        docker compose -f docker-compose.yml up -d --force-recreate litellm | Out-Null
        if ($LASTEXITCODE -ne 0) { Throw-RealBlocked 'LiteLLM could not be recreated with the pinned model map' }
        if ($BuildEvidenceProfile) {
            # mem0 is intentionally image-only in the base compose file, so a
            # fresh evidence run must rebuild its local compatibility wrapper
            # explicitly before compose recreates the real dependency set.
            docker build -f mem0.Dockerfile -t springaitest-mem0:latest . | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'unable to build the local mem0 evidence image' }
        }
        $composeArgs = @('-f', 'docker-compose.yml', '-f', 'docker-compose.evidence.yml', '--profile', 'evidence', 'up', '-d')
        if ($BuildEvidenceProfile) { $composeArgs += '--build' }
        $composeArgs += @('backend', 'workflow', 'mem0', 'evidence-model', 'workflow-capture-proxy', 'platform-evidence')
        docker compose @composeArgs | Out-Null
        if ($LASTEXITCODE -ne 0) { Throw-RealBlocked 'real evidence services could not be started' }
    } finally { Pop-Location }
    # `/health` is protected by LiteLLM's virtual key and returns 401 to an
    # anonymous readiness probe. Liveliness is deliberately public; the
    # authenticated chat/embedding preflights below prove provider readiness.
    Test-RealService 'http://127.0.0.1:4000/health/liveliness' 'LiteLLM'
    Test-RealService 'http://127.0.0.1:8000/docs' 'mem0'
    Test-RealService 'http://127.0.0.1:8011/health' 'workflow capture proxy'
    Test-RealService 'http://127.0.0.1:8180/actuator/health' 'real evidence platform'
}
function Record-RealImageMetadata {
    Push-Location $infraDir
    try {
        foreach ($service in @('litellm','mem0','backend','workflow','evidence-model','workflow-capture-proxy','platform-evidence')) {
            $containerId = (docker compose -f docker-compose.yml -f docker-compose.evidence.yml --profile evidence ps -q $service).Trim()
            if ($containerId) {
                $imageId = (docker inspect --format '{{.Image}}' $containerId).Trim()
                if ($imageId) { Set-EvidenceMetadata -Run $run -Section images -Name $service -Value $imageId }
            }
        }
    } catch {
        Add-EvidenceNote -Run $run -Note 'One or more real-lane image digests were unavailable; executable assertions were unaffected.'
    } finally { Pop-Location }
}
function Restore-RealServices {
    if (-not $script:realServicesChanged) { return }
    # Restore core service environment from the normal compose file.  The
    # evidence platform itself is restored to its deterministic/in-memory form.
    Push-Location $infraDir
    try {
        docker compose -f docker-compose.yml up -d backend workflow mem0 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'core service restore failed' }
        $env:EVIDENCE_PLATFORM_LLM_BASE_URL = 'http://evidence-model:8080'
        $env:EVIDENCE_PLATFORM_LITELLM_KEY = 'evidence-not-a-secret'
        $env:EVIDENCE_PLATFORM_CHAT_MODEL = 'evidence-model'
        $env:EVIDENCE_PLATFORM_MEM0_MODE = 'inmemory'
        $env:EVIDENCE_REAL_MEM0_MODEL = 'gpt-4o-mini'
        $env:EVIDENCE_REAL_WORKFLOW_MODEL = 'mock-gpt'
        $env:EVIDENCE_BACKEND_EMBEDDINGS_PROVIDER = 'fake'
        docker compose -f docker-compose.yml -f docker-compose.evidence.yml --profile evidence up -d evidence-model workflow-capture-proxy platform-evidence | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'evidence service restore failed' }
    } finally { Pop-Location }
}
function Get-Mem0Matches([string]$Base, [string]$UserId, [string]$Marker) {
    $response = Invoke-Json POST "$Base/search" @{ query=$Marker; user_id=$UserId; top_k=10 }
    if ($response.StatusCode -ne 200) { throw "mem0 search returned HTTP $($response.StatusCode)" }
    try { $results = @((($response.Content | ConvertFrom-Json).results)) } catch { throw 'mem0 search returned invalid JSON' }
    # The marker is the search query, but mem0 is allowed to paraphrase an
    # extracted fact. This run uses a unique user with exactly one input fact,
    # so any scoped search result is semantically attributable to that fact.
    @($results)
}
function Remove-RealMemories([string]$Base, [string]$UserId) {
    # Cleanup must not depend on semantic search finding the fact. The user id
    # is unique to this run, so list and delete every memory in that scope.
    $encodedUser = [Uri]::EscapeDataString($UserId)
    $response = Invoke-Json GET "$Base/memories?user_id=$encodedUser&top_k=1000"
    if ($response.StatusCode -ne 200) { throw "mem0 list returned HTTP $($response.StatusCode)" }
    try { $memories = @((($response.Content | ConvertFrom-Json).results)) } catch { throw 'mem0 list returned invalid JSON' }
    foreach ($memory in $memories) {
        if ([string]$memory.user_id -ne $UserId) { throw 'mem0 list returned a memory outside the run-scoped user' }
        $id = [string]$memory.id
        if ([string]::IsNullOrWhiteSpace($id)) { throw 'mem0 matching record omitted its id' }
        $response = Invoke-Json DELETE "$Base/memories/$id"
        if ($response.StatusCode -notin @(200,204)) { throw "mem0 delete returned HTTP $($response.StatusCode)" }
    }
}
function Cleanup-RealConversations([string]$Tenant, [string[]]$Users) {
    $quotedUsers = ($Users | ForEach-Object { "'$($_.Replace("'", "''"))'" }) -join ','
    $quotedTenant = $Tenant.Replace("'", "''")
    Push-Location $infraDir
    try {
        docker compose exec -T appdb psql -U postgres -d springaitest -c "DELETE FROM conversations WHERE tenant_id = '$quotedTenant' AND user_id IN ($quotedUsers);" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'conversation cleanup failed' }
    } finally { Pop-Location }
}
function Wait-RealDocument([string]$Base, [string]$Token, [string]$DocumentId) {
    foreach ($attempt in 1..45) {
        $response = Invoke-Json GET "$Base/api/documents" $null $Token
        if ($response.StatusCode -ne 200) { throw "document list returned HTTP $($response.StatusCode)" }
        try { $document = @($response.Content | ConvertFrom-Json | Where-Object { $_.id -eq $DocumentId } | Select-Object -First 1) } catch { throw 'document list returned invalid JSON' }
        if ($document.Count -eq 1 -and $document[0].status -eq 'ready') { return }
        if ($document.Count -eq 1 -and $document[0].status -eq 'failed') { throw 'real evidence document processing failed' }
        Start-Sleep -Seconds 2
    }
    throw 'real evidence document was not ready before timeout'
}
function Remove-RealDocument([string]$Base, [string]$Token, [string]$DocumentId) {
    if (-not $DocumentId) { return }
    $response = Invoke-Json DELETE "$Base/api/documents/$DocumentId" $null $Token
    if ($response.StatusCode -notin @(204,404)) { throw "document cleanup returned HTTP $($response.StatusCode)" }
}
function Assert-RealReplyContainsNumber([string]$Text, [string]$Number, [string]$Channel) {
    if (-not $Text.Contains($Number)) { throw "$Channel reply did not contain the fixture number" }
}
function Get-SingleWorkflowCapture([string]$Capture, [string]$Channel) {
    $records = @(Get-Captures $Capture)
    if ($records.Count -ne 1) { throw "$Channel produced $($records.Count) workflow invokes instead of exactly one" }
    $record = $records[0]
    if ([string]::IsNullOrWhiteSpace([string]$record.skillName) -or [string]::IsNullOrWhiteSpace([string]$record.inputHmac)) { throw "$Channel workflow capture was incomplete" }
    if ([int]$record.upstreamStatus -lt 200 -or [int]$record.upstreamStatus -ge 300 -or $record.upstreamJsonValid -ne $true) { throw "$Channel workflow response was not successful valid JSON" }
    return $record
}
function Invoke-RealModel {
    $required = @{ PlatformModel=$PlatformModel; WorkflowModel=$WorkflowModel; Mem0Model=$Mem0Model; EmbeddingModel=$EmbeddingModel }
    if ($PlatformModel -ne $script:realModelAlias -or $WorkflowModel -ne $script:realModelAlias -or $Mem0Model -ne $script:realModelAlias -or $EmbeddingModel -ne $script:realEmbeddingAlias) {
        foreach ($gate in $gates) { Set-EvidenceGate -Run $run -Gate $gate -Status FAIL -Detail 'RealModel requires the fixed evidence-gpt-4o-mini-2024-07-18 and text-embedding-3-small aliases.' }
        return
    }
    foreach ($entry in $required.GetEnumerator()) { Set-EvidenceMetadata -Run $run -Section models -Name $entry.Key -Value $entry.Value }
    if (-not (Test-RealProviderCredential)) {
        foreach ($gate in $gates) { Set-EvidenceGate -Run $run -Gate $gate -Status BLOCKED -Detail 'OPENAI_API_KEY is not available to the LiteLLM provider.' }
        return
    }

    $base = 'http://127.0.0.1:8180'; $capture = 'http://127.0.0.1:8011'; $mem0 = 'http://127.0.0.1:8000'
    $tenant = "evidence-$([guid]::NewGuid().ToString('N'))"; $e04User = "e04-$([guid]::NewGuid().ToString('N'))"; $e04WrongUser = "e04wrong-$([guid]::NewGuid().ToString('N'))"; $e05User = "e05-$([guid]::NewGuid().ToString('N'))"
    $e04Marker = "e04pref_$([guid]::NewGuid().ToString('N'))"; $e05Marker = "e05doc_$([guid]::NewGuid().ToString('N'))"
    $documentId = ''; $e04Token = ''; $e05Token = ''; $servicesReady = $false
    try {
        Start-RealProfile
        $servicesReady = $true
        Record-RealImageMetadata
        $chatMeta = Invoke-LiteLlmPreflight 'chat/completions' @{ model=$script:realModelAlias; messages=@(@{role='user';content='release evidence chat preflight'}); temperature=0 } 'chat'
        $embeddingMeta = Invoke-LiteLlmPreflight 'embeddings' @{ model=$script:realEmbeddingAlias; input=@('release evidence embedding preflight') } 'embedding'
        Set-EvidenceMetadata -Run $run -Section models -Name 'LiteLlmChatRequestedAlias' -Value $script:realModelAlias
        Set-EvidenceMetadata -Run $run -Section models -Name 'LiteLlmChatConfiguredUpstreamSnapshot' -Value 'openai/gpt-4o-mini-2024-07-18'
        Set-EvidenceMetadata -Run $run -Section models -Name 'LiteLlmChatProviderActualModel' -Value $chatMeta.Model
        Set-EvidenceMetadata -Run $run -Section models -Name 'LiteLlmEmbeddingRequestedAlias' -Value $script:realEmbeddingAlias
        Set-EvidenceMetadata -Run $run -Section models -Name 'LiteLlmEmbeddingConfiguredUpstream' -Value 'openai/text-embedding-3-small'
        Set-EvidenceMetadata -Run $run -Section models -Name 'LiteLlmEmbeddingProviderActualModel' -Value $embeddingMeta.Model
        if ($chatMeta.SystemFingerprint) { Set-EvidenceMetadata -Run $run -Section models -Name 'LiteLlmChatSystemFingerprint' -Value $chatMeta.SystemFingerprint }
        Add-EvidenceNote -Run $run -Note 'LiteLLM chat and embedding preflight completed before E-04/E-05 fixtures; artifacts contain metadata only.'

        try {
            $e04Token = New-EvidenceJwt $e04User $tenant
            $historyBefore = Invoke-Json GET "$base/api/chat/history" $null $e04Token
            if ($historyBefore.StatusCode -ne 200) { throw "history precheck returned HTTP $($historyBefore.StatusCode)" }
            $agui = Invoke-Agui $base $e04Token "e04-$([guid]::NewGuid().ToString('N'))" @((New-UserMsg "My durable user preference is this acceptance code: $e04Marker. Please remember this preference."))
            if ($agui.StatusCode -ne 200) { throw "AG-UI memory turn returned HTTP $($agui.StatusCode)" }
            if (@(Get-AguiFrames $agui.Content | Where-Object { $_.type -eq 'RUN_FINISHED' }).Count -ne 1) { throw 'AG-UI memory turn did not finish normally' }
            $historyAfter = Invoke-Json GET "$base/api/chat/history" $null $e04Token
            if ($historyAfter.StatusCode -ne 200) { throw "history verification returned HTTP $($historyAfter.StatusCode)" }
            $beforeCount = @($historyBefore.Content | ConvertFrom-Json).Count; $afterCount = @($historyAfter.Content | ConvertFrom-Json).Count
            if ($afterCount -le $beforeCount) { throw 'AG-UI turn was not persisted to authenticated chat history' }
            $matches = @()
            foreach ($attempt in 1..30) { $matches = @(Get-Mem0Matches $mem0 "$tenant`:$e04User" $e04Marker); if ($matches.Count -gt 0) { break }; Start-Sleep -Seconds 2 }
            if ($matches.Count -eq 0) { throw 'mem0 did not return the authenticated marker before timeout' }
            $wrongMatches = @(Get-Mem0Matches $mem0 "$tenant`:$e04WrongUser" $e04Marker)
            if ($wrongMatches.Count -ne 0) { throw 'mem0 marker was visible to the wrong user' }
            Write-EvidenceArtifact -Run $run -RelativePath 'traces/e04-memory.json' -Value @{ markerHmac=(Get-RealHmac $e04Marker); correctUserHmac=(Get-RealHmac "$tenant`:$e04User"); wrongUserHmac=(Get-RealHmac "$tenant`:$e04WrongUser"); historyIncrease=$afterCount-$beforeCount; correctUserMatchCount=$matches.Count; wrongUserMatchCount=0; memoryIdHmacs=@($matches | ForEach-Object { Get-RealHmac ([string]$_.id) }) }
            Set-EvidenceGate -Run $run -Gate E-04 -Status PASS -Detail 'Authenticated AG-UI persistence and scoped real mem0 recall assertions passed.'
        } catch {
            $detail = Get-SafeRealFailureDetail $_.Exception
            if ($detail -like 'REAL_BLOCKED:*') { Set-EvidenceGate -Run $run -Gate E-04 -Status BLOCKED -Detail $detail.Substring(13) } else { Set-EvidenceGate -Run $run -Gate E-04 -Status FAIL -Detail $detail }
        }

        try {
            $e05Token = New-EvidenceJwt $e05User $tenant
            # Keep the numeric fixture short enough that a real model will not
            # regroup or truncate it while still providing ample run uniqueness.
            $number = (Get-Random -Minimum 10000000 -Maximum 99999999).ToString()
            $created = Invoke-Json POST "$base/api/documents" @{ title="evidence-$e05Marker"; text="驗收追蹤碼 $e05Marker 的唯一數字是 $number。" } $e05Token
            if ($created.StatusCode -ne 202) { throw "document create returned HTTP $($created.StatusCode)" }
            try { $documentId = [string](($created.Content | ConvertFrom-Json).id) } catch { throw 'document create returned invalid JSON' }
            if ([string]::IsNullOrWhiteSpace($documentId)) { throw 'document create omitted id' }
            Wait-RealDocument $base $e05Token $documentId
            $trials = @()
            foreach ($round in 1..3) {
                # The answer is intentionally absent from the prompt: a reply
                # containing the fixture number must prove document retrieval.
                $question = "本輪驗收清理代碼是 $e05Marker。請查詢該追蹤碼的已上傳文件，告訴我其中的唯一驗收數字。"
                Reset-Captures $capture
                $chat = Invoke-Json POST "$base/api/chat" @{ message=$question; conversationId="e05chat-$round-$([guid]::NewGuid().ToString('N'))" } $e05Token
                if ($chat.StatusCode -ne 200) { throw "chat routing round $round returned HTTP $($chat.StatusCode)" }
                $chatReply = [string](($chat.Content | ConvertFrom-Json).reply); Assert-RealReplyContainsNumber $chatReply $number 'chat'
                $chatCapture = Get-SingleWorkflowCapture $capture 'chat'
                Reset-Captures $capture
                $tools = @(@{ name='switchView'; description='evidence client tool'; parameters=@{type='object';properties=@{view=@{type='string'}}} })
                $agui = Invoke-Agui $base $e05Token "e05agui-$round-$([guid]::NewGuid().ToString('N'))" @((New-UserMsg $question)) $tools
                if ($agui.StatusCode -ne 200) { throw "AG-UI routing round $round returned HTTP $($agui.StatusCode)" }
                $frames = Get-AguiFrames $agui.Content
                if (@($frames | Where-Object { ([string]$_.type) -like 'TOOL_CALL_*' }).Count -ne 0) { throw 'AG-UI emitted a native TOOL_CALL event for a routed server skill' }
                $aguiReply = Get-AssistantReply $agui.Content; Assert-RealReplyContainsNumber $aguiReply.Text $number 'AG-UI'
                $aguiCapture = Get-SingleWorkflowCapture $capture 'AG-UI'
                $chatKeys = @($chatCapture.inputKeys); $aguiKeys = @($aguiCapture.inputKeys)
                if ($chatCapture.skillName -ne $aguiCapture.skillName -or ($chatKeys -join '|') -ne ($aguiKeys -join '|') -or $chatCapture.inputHmac -ne $aguiCapture.inputHmac) { throw "chat and AG-UI workflow captures differed in routing round $round" }
                $trials += @{ round=$round; skillName=[string]$chatCapture.skillName; inputKeys=$chatKeys; inputHmac=[string]$chatCapture.inputHmac; chatCaptureCorrelationHmac=(Get-RealHmac "$round|chat|$($chatCapture.inputHmac)"); aguiCaptureCorrelationHmac=(Get-RealHmac "$round|agui|$($aguiCapture.inputHmac)"); chatReplyContainsNumber=$true; aguiReplyContainsNumber=$true; nativeToolCallEvents=0 }
            }
            Write-EvidenceArtifact -Run $run -RelativePath 'traces/e05-routing.json' -Value @{ documentIdHmac=(Get-RealHmac $documentId); fixtureNumberHmac=(Get-RealHmac $number); trials=$trials; requiredTrials=3; passedTrials=$trials.Count }
            Set-EvidenceGate -Run $run -Gate E-05 -Status PASS -Detail 'Three real-model Chat/AG-UI workflow routing captures matched and returned the fixture number without AG-UI native skill tool calls.'
        } catch {
            $detail = Get-SafeRealFailureDetail $_.Exception
            if ($detail -like 'REAL_BLOCKED:*') { Set-EvidenceGate -Run $run -Gate E-05 -Status BLOCKED -Detail $detail.Substring(13) } else { Set-EvidenceGate -Run $run -Gate E-05 -Status FAIL -Detail $detail }
        }
    } catch {
        $detail = Get-SafeRealFailureDetail $_.Exception; $status = if ($detail -like 'REAL_BLOCKED:*') { 'BLOCKED' } else { 'FAIL' }; if ($status -eq 'BLOCKED') { $detail = $detail.Substring(13) }
        foreach ($gate in $gates) { Set-EvidenceGate -Run $run -Gate $gate -Status $status -Detail $detail }
    } finally {
        $cleanupErrors = @()
        if ($servicesReady) {
            try { Remove-RealDocument $base $e05Token $documentId } catch { $cleanupErrors += 'document cleanup failed' }
            try { Remove-RealMemories $mem0 "$tenant`:$e04User" } catch { $cleanupErrors += 'E-04 memory cleanup failed' }
            try { Remove-RealMemories $mem0 "$tenant`:$e05User" } catch { $cleanupErrors += 'E-05 memory cleanup failed' }
            try { Cleanup-RealConversations $tenant @($e04User, $e04WrongUser, $e05User) } catch { $cleanupErrors += 'conversation cleanup failed' }
        }
        try { Restore-RealServices } catch { $cleanupErrors += 'service environment restore failed' }
        Restore-RealEvidenceEnvironment
        if ($cleanupErrors.Count -gt 0) { foreach ($gate in $gates) { Set-EvidenceGate -Run $run -Gate $gate -Status BLOCKED -Detail ($cleanupErrors -join '; ') } }
    }
}

try {
    if ($Lane -eq 'Deterministic') { Invoke-Deterministic }
    else { Invoke-RealModel }
    $result=Complete-EvidenceRun -Run $run; Write-Host "Evidence bundle: $($run.FullPath)"; Write-Host "Result: $result"; if($result -ne 'PASS'){exit 2}
} catch { Write-Error $_; exit 1 }
