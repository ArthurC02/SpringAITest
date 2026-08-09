#!/usr/bin/env sh
# 模式 A：啟動基礎設施 + 常駐應用層（backend/workflow/appdb/rabbitmq 等），不含 frontend/platform（profile full）。
# 前端 (npm run dev) 與後端 (dotnet run --project src/Platform.Web) 請另在主機上跑——見 README 模式 A。
# 預設不 build（多數人只在主機上改 platform/frontend，無條件 build 會拖慢日常啟動）；
# backend/ 或 workflow/ 原始碼有異動時，加 --build 才會重建容器 image。
set -e

BUILD=0
for arg in "$@"; do
  case "$arg" in
    --build) BUILD=1 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
echo "▶ 啟動基礎設施（不含前後端）…"
# 共用前置：切到 infra、檢查 .env、起 postgres、備妥 mem0 庫
. "$SCRIPT_DIR/_bootstrap.sh"

if [ "$BUILD" -eq 1 ]; then
  docker compose build backend workflow
else
  echo "提示：backend/ 或 workflow/ 原始碼有異動時，這個模式不會自動重建容器。請改用 ./scripts/start-infra.sh --build"
fi

docker compose up -d
echo
docker compose ps
echo
echo "✓ 完成。接著在主機啟動平台閘道與前端："
echo "    平台閘道： cd platform  && dotnet run --project src/Platform.Web     # :8080"
echo "    前端：     cd frontend && npm install && npm run dev               # :5173"
