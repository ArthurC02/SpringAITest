#!/usr/bin/env sh
# 模式 B：全容器——基礎設施 + 前端 + 後端，一鍵起整套（見 README 模式 B）。
# 加 --build 確保跑的是目前的原始碼（首次或改過程式碼後會重建映像，需幾分鐘）。
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
echo "▶ 啟動全容器（infra + 前端 + 後端）…"
# 共用前置：切到 infra、檢查 .env、起 postgres、備妥 mem0 庫
. "$SCRIPT_DIR/_bootstrap.sh"

docker compose --profile full up -d --build
echo
docker compose --profile full ps
echo
echo "✓ 完成。前端 http://localhost:5173 ／ Langfuse http://localhost:3000"
echo "  停止：docker compose --profile full down"
