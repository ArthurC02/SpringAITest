#!/usr/bin/env sh
# 模式 A：只啟動基礎設施（7 個服務：LiteLLM + Langfuse 全套）。
# 前端 (npm run dev) 與後端 (dotnet run --project src/Platform.Web) 請另在主機上跑——見 README 模式 A。
set -e

# 切到 repo 根目錄下的 infra/（本腳本在 scripts/，故往上一層）
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/../infra"

if [ ! -f .env ]; then
  echo "✗ 找不到 infra/.env。請先建立：cp .env.example .env 後填入 OPENAI_API_KEY" >&2
  exit 1
fi

echo "▶ 啟動基礎設施（不含前後端）…"
docker compose up -d postgres                 # 先起 postgres，好在 mem0 之前備妥它需要的庫
sh "$SCRIPT_DIR/ensure-mem0-db.sh"
docker compose up -d
echo
docker compose ps
echo
echo "✓ 完成。接著在主機啟動後端與前端："
echo "    後端： cd platform  && dotnet run --project src/Platform.Web         # :8080"
echo "    前端： cd frontend && npm install && npm run dev                   # :5173"
