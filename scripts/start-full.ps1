#!/usr/bin/env pwsh
# 模式 B：全容器——基礎設施 + 前端 + 後端，一鍵起整套（見 README 模式 B）。
# 加 --build 確保跑的是目前的原始碼（首次或改過程式碼後會重建映像，需幾分鐘）。
$ErrorActionPreference = 'Stop'

# 切到 repo 根目錄下的 infra\（本腳本在 scripts\，故往上一層）
Set-Location (Join-Path $PSScriptRoot '..' 'infra')

if (-not (Test-Path .env)) {
    Write-Error "找不到 infra\.env。請先建立：copy .env.example .env 後填入 OPENAI_API_KEY"
    exit 1
}

Write-Host "▶ 啟動全容器（infra + 前端 + 後端）…"
docker compose --profile full up -d --build
Write-Host ""
docker compose --profile full ps
Write-Host ""
Write-Host "✓ 完成。前端 http://localhost:5173 ／ Langfuse http://localhost:3000"
Write-Host "  停止：docker compose --profile full down"
