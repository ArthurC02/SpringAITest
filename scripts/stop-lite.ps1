#!/usr/bin/env pwsh
# 收攤 Lite 模式：讀 start-lite.pids，逐一終止程序，刪 PID 檔。
# 重複執行安全——無 PID 檔時回「No running processes found.」而不報錯。
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pidsFile = Join-Path $repoRoot '.lite/start-lite.pids'

if (Test-Path $pidsFile) {
    Get-Content $pidsFile | ForEach-Object {
        $procId = $_.Trim()
        if ($procId) {
            # /T 遞迴終止子行程樹：uv→uvicorn、cmd→npm→vite/node 的子行程才不會成孤兒佔埠
            & taskkill /PID $procId /T /F *> $null
        }
    }
    Remove-Item $pidsFile
    Write-Host "All services stopped."
} else {
    Write-Host "No running processes found."
}
