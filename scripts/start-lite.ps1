#!/usr/bin/env pwsh
# 模式 Lite：無容器啟動——四個服務全在主機本機程序跑（backend/workflow/platform/frontend），
# 外加 uvx 起 LiteLLM 供 mock-gpt 路由。不碰 Docker、不需 PostgreSQL/RabbitMQ/mem0/Langfuse。
#   backend : DB_PROVIDER=inmemory EMBEDDINGS_PROVIDER=fake  （以 InMemory repo 取代 PostgreSQL，無 DbBootstrap；完整清單見 backend/AGENTS.md）
#   workflow: LLM_MODEL=mock-gpt LANGFUSE_ENABLED=false
#   platform: MEM0_MODE=inmemory OTEL_MODE=console CHAT_MODEL=mock-gpt
#   frontend: npm run dev
# 需要 pwsh 7.4+（Start-Process -Environment）。收攤：.\scripts\stop-lite.ps1
[CmdletBinding()]
param(
    [switch]$SkipLiteLlm   # 已自行在 :4000 跑 LiteLLM 時可略過
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runDir   = Join-Path $repoRoot '.lite'   # 執行期產物（log / pid）集中處，已 gitignore
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$pidsFile = Join-Path $runDir 'start-lite.pids'

function LogPath([string]$name) { Join-Path $runDir $name }

Write-Host "▶ 啟動 Lite 模式（無容器）…"

# ── 前置檢查 ───────────────────────────────────────────────────────────────
$errs = @()

$dotnetOk = $false
try {
    $v = (& dotnet --version) 2>$null
    if ($v -and [int](($v -split '\.')[0]) -ge 10) { $dotnetOk = $true }
} catch { }
if (-not $dotnetOk) { $errs += "dotnet 10 SDK not found. Install from https://dotnet.microsoft.com/download" }

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    $errs += "uv not found. Install from https://astral.sh/uv"
}
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    $errs += "node not found. Install from https://nodejs.org/"
}

$pythonOk = $false
try {
    $pv = (& python --version) 2>&1
    if ($pv -match '(\d+)\.(\d+)') {
        if ([int]$Matches[1] -gt 3 -or ([int]$Matches[1] -eq 3 -and [int]$Matches[2] -ge 12)) { $pythonOk = $true }
    }
} catch { }
if (-not $pythonOk) { $errs += "Python 3.12+ not found. Install from https://www.python.org/downloads/" }

if ($errs.Count -gt 0) {
    $errs | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
    exit 1
}
Write-Host "✓ 前置檢查通過（dotnet 10 / uv / node / python 3.12+）"

# ── 平行啟動服務 ───────────────────────────────────────────────────────────
$jobs = @()
Remove-Item $pidsFile -ErrorAction SilentlyContinue   # 清前次殘留；PID 逐一即時寫入，任何一步失敗都不遺失已啟動服務

if (-not $SkipLiteLlm) {
    Write-Host "  起 LiteLLM  :4000 …"
    $jobs += Start-Process -FilePath "uvx" -ArgumentList `
        "--from", "litellm[proxy]", "litellm", `
        "--config", "infra/litellm-config.lite.yaml", "--port", "4000" `
        -WorkingDirectory $repoRoot -PassThru `
        -Environment @{
            # config 的 master_key 讀 os.environ/LITELLM_MASTER_KEY；不設就靜默視為無 master key（免驗證），故給預設值
            "LITELLM_MASTER_KEY" = "sk-1234"
        } `
        -RedirectStandardOutput (LogPath 'litellm.log') `
        -RedirectStandardError  (LogPath 'litellm-err.log')
    $jobs[-1].Id | Add-Content -Path $pidsFile
} else {
    Write-Host "  略過 LiteLLM（-SkipLiteLlm）——請自行確認 :4000 已在跑"
}

Write-Host "  起 backend   :8002 …"
$jobs += Start-Process -FilePath "dotnet" -ArgumentList `
    "run", "--no-launch-profile", "--project", "backend/src/Backend.Api/Backend.Api.csproj" `
    -WorkingDirectory $repoRoot -PassThru `
    -Environment @{
        "DB_PROVIDER"            = "inmemory"
        "EMBEDDINGS_PROVIDER"    = "fake"
        "ASPNETCORE_URLS"        = "http://localhost:8002"
        "ASPNETCORE_ENVIRONMENT" = "Development"
    } `
    -RedirectStandardOutput (LogPath 'backend.log') `
    -RedirectStandardError  (LogPath 'backend-err.log')
$jobs[-1].Id | Add-Content -Path $pidsFile

Write-Host "  起 workflow  :8001 …"
$jobs += Start-Process -FilePath "uv" -ArgumentList `
    "run", "uvicorn", "app.main:app", "--host", "127.0.0.1", "--port", "8001" `
    -WorkingDirectory (Join-Path $repoRoot 'workflow') -PassThru `
    -Environment @{
        "BACKEND_BASE_URL"  = "http://localhost:8002"
        "LLM_BASE_URL"      = "http://localhost:4000"
        "LLM_MODEL"         = "mock-gpt"
        "LANGFUSE_ENABLED"  = "false"
    } `
    -RedirectStandardOutput (LogPath 'workflow.log') `
    -RedirectStandardError  (LogPath 'workflow-err.log')
$jobs[-1].Id | Add-Content -Path $pidsFile

Write-Host "  起 platform  :8080 …"
$jobs += Start-Process -FilePath "dotnet" -ArgumentList `
    "run", "--no-launch-profile", "--project", "platform/src/Platform.Web/Platform.Web.csproj" `
    -WorkingDirectory $repoRoot -PassThru `
    -Environment @{
        "MEM0_MODE"              = "inmemory"
        "OTEL_MODE"              = "console"
        "CHAT_MODEL"             = "mock-gpt"
        "ASPNETCORE_URLS"        = "http://localhost:8080"
        "ASPNETCORE_ENVIRONMENT" = "Development"
    } `
    -RedirectStandardOutput (LogPath 'platform.log') `
    -RedirectStandardError  (LogPath 'platform-err.log')
$jobs[-1].Id | Add-Content -Path $pidsFile

Write-Host "  起 frontend  :5173 …"
# npm 是 npm.cmd（非 Win32 executable），Start-Process -FilePath "npm" 會炸；透過 cmd.exe /c 啟動
$jobs += Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "npm", "run", "dev" `
    -WorkingDirectory (Join-Path $repoRoot 'frontend') -PassThru `
    -RedirectStandardOutput (LogPath 'frontend.log') `
    -RedirectStandardError  (LogPath 'frontend-err.log')
$jobs[-1].Id | Add-Content -Path $pidsFile

# ── 健康檢查 ───────────────────────────────────────────────────────────────
$healthChecks = @(
    @{ url = "http://localhost:8002/health";          name = "backend"  }
    @{ url = "http://localhost:8001/health";          name = "workflow" }
    @{ url = "http://localhost:8080/actuator/health"; name = "platform" }
)

foreach ($check in $healthChecks) {
    $elapsed = 0
    $ok = $false
    while ($elapsed -lt 60) {
        try {
            $resp = Invoke-WebRequest -Uri $check.url -TimeoutSec 2 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) { $ok = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
        $elapsed += 2
    }
    if ($ok) {
        Write-Host "✓ $($check.name) healthy"
    } else {
        Write-Warning "$($check.name) failed health check after 60s（見 $($check.name).log）"
    }
}

# ── 完成輸出 ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "✓ Lite 模式已啟動"
Write-Host "  backend:  http://localhost:8002  (logs: .lite/backend.log)"
Write-Host "  workflow: http://localhost:8001  (logs: .lite/workflow.log)"
Write-Host "  platform: http://localhost:8080  (logs: .lite/platform.log)"
Write-Host "  frontend: http://localhost:5173  (logs: .lite/frontend.log)"
if (-not $SkipLiteLlm) {
    Write-Host "  litellm:  http://localhost:4000  (logs: .lite/litellm.log)"
}
Write-Host ""
Write-Host "  停止：.\scripts\stop-lite.ps1"
