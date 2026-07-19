#!/usr/bin/env pwsh
# 模式 B：全容器——基礎設施 + 前端 + 後端，一鍵起整套（見 README 模式 B）。
# 加 --build 確保跑的是目前的原始碼（首次或改過程式碼後會重建映像，需幾分鐘）。
$ErrorActionPreference = 'Stop'

Write-Host "▶ 啟動全容器（infra + 前端 + 後端）…"
. (Join-Path $PSScriptRoot '_bootstrap.ps1')   # 切到 infra、檢查 .env、起 postgres、備妥 mem0 庫

docker compose --profile full up -d --build
if ($LASTEXITCODE -ne 0) { Write-Error "docker compose --profile full up 失敗（離開碼 $LASTEXITCODE）"; exit 1 }
Write-Host ""
docker compose --profile full ps
Write-Host ""
Write-Host "✓ 完成。前端 http://localhost:5173 ／ Langfuse http://localhost:3000"
Write-Host "  停止：docker compose --profile full down"
