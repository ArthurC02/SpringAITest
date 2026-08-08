# 共用前置：切到 infra\、檢查 .env、先起 postgres 並備妥 mem0 需要的庫。
# 由 start-infra.ps1 / start-full.ps1 以 dot-source（.）載入——cd 與 exit 都作用在呼叫端。
. (Join-Path $PSScriptRoot '_development-environment.ps1')
Set-Location (Join-Path (Join-Path $PSScriptRoot '..') 'infra')   # 巢狀兩參數版：PS 5.1 不支援 3-arg Join-Path

if (-not (Test-Path .env)) {
    Write-Error "找不到 infra\.env。請先建立：copy .env.example .env 後填入 OPENAI_API_KEY"
    exit 1
}

# $ErrorActionPreference='Stop' 攔不到原生 exe 的非零離開碼，每個 docker compose 後都要顯式檢查
docker compose up -d postgres                 # 先起 postgres，好在 mem0 之前備妥它需要的庫
if ($LASTEXITCODE -ne 0) { Write-Error "docker compose up postgres 失敗（離開碼 $LASTEXITCODE）"; exit 1 }
& (Join-Path $PSScriptRoot 'ensure-mem0-db.ps1')
if ($LASTEXITCODE -ne 0) { Write-Error "ensure-mem0-db 失敗（離開碼 $LASTEXITCODE）"; exit 1 }
