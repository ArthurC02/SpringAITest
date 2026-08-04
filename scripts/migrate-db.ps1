#!/usr/bin/env pwsh
# 守護式資料庫遷移指令(03-design §1.3 末段)。與 migrate-db.sh 同一套目標清單與確認詞。
#
# 順序是刻意的:先解析、再列印精確目標,最後才問確認 —— 操作員確認的是他眼前看到的那一行,
# 不是他以為的環境變數。空變數永遠不會被推導成一個「預設」目標。
#
# ALLOW_DESTRUCTIVE_MIGRATION 只在這支腳本啟動的專用 migration 行程內、且連同命令列確認詞
# 一起出現時才成立;Backend 一般啟動完全忽略它。
param(
    [string]$ConnectionString = $env:DB_CONNECTION_STRING,
    [switch]$AllowRemote,
    [int]$LockTimeoutSeconds = 60,
    [string]$ComposeProject
)

$ErrorActionPreference = 'Stop'
# 由本腳本自己決定 dotnet 的離開碼怎麼處理(往上原樣傳),不讓 PS 7.4+ 的原生指令終止錯誤搶先。
$PSNativeCommandUseErrorActionPreference = $false

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    Write-Host "✗ 未提供連線字串(DB_CONNECTION_STRING 或 -ConnectionString),拒絕推導預設目標。" -ForegroundColor Red
    exit 2
}

function Get-ConnectionField([string]$key) {
    foreach ($pair in $ConnectionString.Split(';')) {
        $index = $pair.IndexOf('=')
        if ($index -lt 0) { continue }
        if ($pair.Substring(0, $index).Trim() -ieq $key) { return $pair.Substring($index + 1).Trim() }
    }
    return ''
}

$dbHost = Get-ConnectionField 'Host'
$dbPort = Get-ConnectionField 'Port'
$dbName = Get-ConnectionField 'Database'
$dbUser = Get-ConnectionField 'Username'

if (-not $dbHost -or -not $dbName -or -not $dbUser) {
    Write-Host "✗ 連線字串缺少 Host/Database/Username 其中之一,拒絕推導預設目標。" -ForegroundColor Red
    exit 2
}

if (@('postgres', 'template0', 'template1') -contains $dbName.ToLowerInvariant()) {
    Write-Host "✗ 拒絕以受保護的系統資料庫 ``$dbName`` 為 migration 目標。" -ForegroundColor Red
    exit 2
}

if (-not $dbPort) { $dbPort = '5432' }
$projectSuffix = if ($ComposeProject) { " composeProject=$ComposeProject" } else { '' }
Write-Host "migration 目標:host=$dbHost port=$dbPort database=$dbName user=$dbUser$projectSuffix"

$confirmToken = "RESET $dbName"
$confirmation = Read-Host "輸入確認詞以繼續($confirmToken)"
if ($confirmation -cne $confirmToken) {
    Write-Host "✗ 確認詞不符,未做任何變更。" -ForegroundColor Red
    exit 2
}

$remoteArgs = @()
if (@('localhost', '127.0.0.1', '::1', '[::1]') -notcontains $dbHost.ToLowerInvariant()) {
    if (-not $AllowRemote) {
        Write-Host "✗ 目標 ``$dbHost`` 不是本機,需要 -AllowRemote 才能對遠端資料庫執行破壞性 migration。" -ForegroundColor Red
        exit 2
    }
    $remoteToken = "$dbHost/$dbName"
    $remoteConfirmation = Read-Host "遠端目標需要第二次精確確認($remoteToken)"
    if ($remoteConfirmation -cne $remoteToken) {
        Write-Host "✗ 遠端第二次確認不符,未做任何變更。" -ForegroundColor Red
        exit 2
    }
    $remoteArgs = @('--allow-remote', '--confirm-remote', $remoteToken)
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectArgs = if ($ComposeProject) { @('--compose-project', $ComposeProject) } else { @() }

# ALLOW_DESTRUCTIVE_MIGRATION 只在這個呼叫的存續期間成立。
$env:ALLOW_DESTRUCTIVE_MIGRATION = 'true'
try {
    & dotnet run --project (Join-Path $repoRoot 'backend/src/Backend.Api') -- `
        migrate-db `
        --connection $ConnectionString `
        --confirm $confirmToken `
        --lock-timeout $LockTimeoutSeconds `
        @projectArgs @remoteArgs
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ALLOW_DESTRUCTIVE_MIGRATION -ErrorAction SilentlyContinue
}
