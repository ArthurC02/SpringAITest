#!/usr/bin/env sh
# 模式 A：啟動基礎設施 + 常駐應用層（backend/workflow/appdb/rabbitmq 等），不含 frontend/platform（profile full）。
# 前端 (npm run dev) 與後端 (dotnet run --project src/Platform.Web) 請另在主機上跑——見 README 模式 A。
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
echo "▶ 啟動基礎設施（不含前後端）…"
# 共用前置：切到 infra、檢查 .env、起 postgres、備妥 mem0 庫
. "$SCRIPT_DIR/_bootstrap.sh"

docker compose up -d
echo
docker compose ps
echo
echo "✓ 完成。接著在主機啟動後端與前端："
echo "    後端： cd platform  && dotnet run --project src/Platform.Web         # :8080"
echo "    前端： cd frontend && npm install && npm run dev                   # :5173"
