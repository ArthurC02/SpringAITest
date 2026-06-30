#!/usr/bin/env sh
# 模式 B：全容器——基礎設施 + 前端 + 後端，一鍵起整套（見 README 模式 B）。
# 加 --build 確保跑的是目前的原始碼（首次或改過程式碼後會重建映像，需幾分鐘）。
set -e

# 切到 repo 根目錄下的 infra/（本腳本在 scripts/，故往上一層）
cd "$(dirname "$0")/../infra"

if [ ! -f .env ]; then
  echo "✗ 找不到 infra/.env。請先建立：cp .env.example .env 後填入 OPENAI_API_KEY" >&2
  exit 1
fi

echo "▶ 啟動全容器（infra + 前端 + 後端）…"
docker compose --profile full up -d --build
echo
docker compose --profile full ps
echo
echo "✓ 完成。前端 http://localhost:5173 ／ Langfuse http://localhost:3000"
echo "  停止：docker compose --profile full down"
