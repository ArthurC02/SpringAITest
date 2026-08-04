#!/usr/bin/env pwsh
#requires -Version 7
# 開發環境外部狀態清理(03-design §7)。與 reset-development-data.sh 逐項相同的目標清單與確認詞。
#
# 預設目標清單:
#   1. appdb 應用資料庫             DROP + CREATE
#   2. LangGraph checkpoint 資料庫  只在 CHECKPOINT_DATABASE_URL 有值時處理
#   3. mem0 開發狀態                mem0_app 關聯庫 DROP + CREATE
#   4. RabbitMQ 應用佇列            documents.process / documents.process.dlq purge
#   5. .lite 本機狀態               解析後必須落在 repo 目錄內才刪
#
# 刻意不碰:Langfuse 追蹤、這些儲存體以外的上傳原始檔、以及任何不相干的 volume。
# 遇到非預期失敗即停,每清掉一項就回報一項。
$ErrorActionPreference = 'Stop'
# PowerShell 7.4+ 預設會把「原生指令非零離開碼」變成終止錯誤,那會讓「佇列不存在就略過」變成中止。
# 這裡一律用顯式的 $LASTEXITCODE 判斷,哪個分支算失敗由腳本自己決定。
$PSNativeCommandUseErrorActionPreference = $false

$composeProject = 'springaitest'
$confirmToken = 'RESET springaitest development data'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$liteDir = Join-Path $repoRoot '.lite'

$appdbDatabase = 'springaitest'
$mem0Database = 'mem0_app'
$rabbitQueues = @('documents.process', 'documents.process.dlq')

function Test-ProtectedDatabase([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $true }
    return @('postgres', 'template0', 'template1') -contains $name.ToLowerInvariant()
}

# checkpoint 資料庫是選配的:未設定就不猜,直接回報略過。
$checkpointDatabase = ''
if ($env:CHECKPOINT_DATABASE_URL) {
    $checkpointDatabase = ($env:CHECKPOINT_DATABASE_URL -split '/')[-1] -replace '\?.*$', ''
    if (Test-ProtectedDatabase $checkpointDatabase) {
        Write-Host "✗ CHECKPOINT_DATABASE_URL 解析出受保護或空的資料庫名 ``$checkpointDatabase``,已中止。" -ForegroundColor Red
        exit 2
    }
    # 只接受合法的小寫識別字。`postgresql://appdb:5432`(漏了 /dbname)會解析出 `appdb:5432`,
    # 放行的話 CREATE DATABASE 會建出一個垃圾庫。
    if ($checkpointDatabase -cnotmatch '^[a-z_][a-z0-9_]*$') {
        Write-Host "✗ CHECKPOINT_DATABASE_URL 解析出的資料庫名 ``$checkpointDatabase`` 不是合法識別字,已中止。" -ForegroundColor Red
        exit 2
    }
}

if ((Test-ProtectedDatabase $appdbDatabase) -or (Test-ProtectedDatabase $mem0Database)) {
    Write-Host "✗ 目標資料庫名不合法,已中止。" -ForegroundColor Red
    exit 2
}

# .lite 必須解析在 repo 目錄內才准遞迴刪除。GetFullPath 只做字串正規化,
# symlink/junction 指到 repo 外仍會通過,所以真的存在時另外擋掉 reparse point。
$resolvedLite = [System.IO.Path]::GetFullPath($liteDir)
if (Test-Path $liteDir) {
    if ((Get-Item $liteDir -Force).Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        Write-Host "✗ .lite 是 symlink/junction,拒絕遞迴刪除。" -ForegroundColor Red
        exit 2
    }
    $resolvedLite = (Resolve-Path $liteDir).Path
}
if (-not $resolvedLite.StartsWith([System.IO.Path]::GetFullPath($repoRoot) + [System.IO.Path]::DirectorySeparatorChar)) {
    Write-Host "✗ .lite 解析結果 ``$resolvedLite`` 不在 repo 目錄內,拒絕遞迴刪除。" -ForegroundColor Red
    exit 2
}

$checkpointLabel = if ($checkpointDatabase) { $checkpointDatabase } else { '(CHECKPOINT_DATABASE_URL 未設定,略過)' }
Write-Host "compose 專案:$composeProject"
Write-Host "將清理的具名目標:"
Write-Host "  1. appdb 資料庫        $appdbDatabase"
Write-Host "  2. checkpoint 資料庫   $checkpointLabel"
Write-Host "  3. mem0 關聯狀態庫     $mem0Database"
Write-Host "  4. RabbitMQ 佇列       $($rabbitQueues -join ' ')"
Write-Host "  5. Lite 本機狀態       $resolvedLite"
Write-Host "不會被碰到:Langfuse 追蹤、上述儲存體以外的上傳原始檔、其他 volume。"

$confirmation = Read-Host "輸入確認詞以繼續($confirmToken)"
if ($confirmation -cne $confirmToken) {
    Write-Host "✗ 確認詞不符,未做任何變更。" -ForegroundColor Red
    exit 2
}

# 這一步只證明 docker compose 可用:`-p` 對不存在的專案同樣回 0,所以它不是專案存在性檢查。
# 真正的保護是後面每個 exec 失敗即中止(fail closed)。
& docker compose -p $composeProject ps *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "✗ 無法執行 docker compose(專案 ``$composeProject``),已中止。" -ForegroundColor Red
    exit 1
}

function Reset-Database([string]$service, [string]$database) {
    $resetArgs = @(
        'compose', '-p', $composeProject, 'exec', '-T', $service, 'psql', '-U', 'postgres', '-d', 'postgres', '-q',
        '-c', "DROP DATABASE IF EXISTS ""$database"" WITH (FORCE);",
        '-c', "CREATE DATABASE ""$database"";"
    )
    & docker @resetArgs *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "✗ 重建資料庫 $database($service)失敗,已中止。" -ForegroundColor Red
        exit 1
    }
    Write-Host "  ✓ 已重建資料庫 $database($service)"
}

Reset-Database 'appdb' $appdbDatabase

if ($checkpointDatabase) {
    Reset-Database 'appdb' $checkpointDatabase
} else {
    Write-Host "  – checkpoint 資料庫未設定,略過"
}

Reset-Database 'postgres' $mem0Database
# TODO(P3):mem0 的 sqlite 變更紀錄(mem0_history volume)與 postgres 向量表需要先停容器才能安全清,
# 併入 P3 的切換程序處理;此處只清可在容器運行中驗證的關聯狀態庫。
Write-Host "  – mem0 sqlite 變更紀錄與向量表留待 P3 切換程序(需先停容器)"

foreach ($queue in $rabbitQueues) {
    & docker compose -p $composeProject exec -T rabbitmq rabbitmqctl purge_queue $queue *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ 已清空佇列 $queue"
    } else {
        Write-Host "  – 佇列 $queue 不存在,略過"
    }
}

if (Test-Path $resolvedLite) {
    Remove-Item $resolvedLite -Recurse -Force
    Write-Host "  ✓ 已刪除 $resolvedLite"
} else {
    Write-Host "  – $resolvedLite 不存在,略過"
}

Write-Host "完成。接著可執行 ./scripts/start-infra.ps1 或 ./scripts/start-lite.ps1 重新啟動,再跑開發 seed。"
