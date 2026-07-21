#!/usr/bin/env pwsh
# 模式 A：啟動基礎設施 + 常駐應用層（backend/workflow/appdb/rabbitmq 等），不含 frontend/platform（profile full）。
# 前端 (npm run dev) 與後端 (dotnet run --project src/Platform.Web) 請另在主機上跑——見 README 模式 A。
$ErrorActionPreference = 'Stop'

Write-Host "▶ 啟動基礎設施（不含前後端）…"
. (Join-Path $PSScriptRoot '_bootstrap.ps1')   # 切到 infra、檢查 .env、起 postgres、備妥 mem0 庫

docker compose up -d
if ($LASTEXITCODE -ne 0) { Write-Error "docker compose up 失敗（離開碼 $LASTEXITCODE）"; exit 1 }
Write-Host ""
docker compose ps
Write-Host ""
Write-Host "✓ 完成。接著在主機啟動平台閘道與前端："
Write-Host "    平台閘道： cd platform  ; dotnet run --project src/Platform.Web        # :8080"
Write-Host "    前端：     cd frontend ; npm install ; npm run dev                    # :5173"
