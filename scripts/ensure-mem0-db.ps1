#!/usr/bin/env pwsh
# 確保 mem0 需要的 postgres 前置條件。compose 無法宣告式處理這兩件事，故起 postgres 後由本腳本補上：
#   1) mem0 的關聯狀態庫 mem0_app —— mem0 的 db.py 預設連它，但 POSTGRES_DB 只會建 postgres 這一個庫。
#   2) collation 版本 —— 把 postgres 映像換成 pgvector 版後，沿用舊 data volume 會出現 collation 版本不符，
#      進而擋住「從 template1 複製建新庫」。先刷新即可（版本本來就相同時是無動作）。
# 本腳本可重複執行、無副作用。
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..' 'infra')

# 等 postgres 就緒（最多 ~60s）
for ($i = 0; $i -lt 30; $i++) {
    docker compose exec -T postgres pg_isready -U postgres *> $null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 2
}

# 先刷新 collation 版本（相同時無動作），否則沿用舊 volume 時下面的 CREATE DATABASE 會被擋。
docker compose exec -T postgres psql -U postgres -q `
    -c "ALTER DATABASE template1 REFRESH COLLATION VERSION;" `
    -c "ALTER DATABASE postgres REFRESH COLLATION VERSION;" *> $null

# 建 mem0_app（已存在則略過）。
$exists = docker compose exec -T postgres psql -U postgres -tAc "SELECT 1 FROM pg_database WHERE datname='mem0_app'" 2>$null
if ("$exists".Trim() -eq '1') {
    Write-Host "  ✓ 資料庫 mem0_app 已存在"
} else {
    docker compose exec -T postgres psql -U postgres -q -c "CREATE DATABASE mem0_app;" *> $null
    Write-Host "  ✓ 已建立資料庫 mem0_app"
}
