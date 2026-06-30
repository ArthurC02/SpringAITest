#!/usr/bin/env pwsh
# 模式 A：只啟動基礎設施（7 個服務：LiteLLM + Langfuse 全套）。
# 前端 (npm run dev) 與後端 (.\mvnw.cmd spring-boot:run) 請另在主機上跑——見 README 模式 A。
$ErrorActionPreference = 'Stop'

# 切到 repo 根目錄下的 infra\（本腳本在 scripts\，故往上一層）
Set-Location (Join-Path $PSScriptRoot '..' 'infra')

if (-not (Test-Path .env)) {
    Write-Error "找不到 infra\.env。請先建立：copy .env.example .env 後填入 OPENAI_API_KEY"
    exit 1
}

Write-Host "▶ 啟動基礎設施（不含前後端）…"
docker compose up -d
Write-Host ""
docker compose ps
Write-Host ""
Write-Host "✓ 完成。接著在主機啟動後端與前端："
Write-Host "    後端： cd backend  ; .\mvnw.cmd -pl springaitest-web spring-boot:run   # :8080"
Write-Host "    前端： cd frontend ; npm install ; npm run dev                         # :5173"
