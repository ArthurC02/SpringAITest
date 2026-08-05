#!/bin/bash
# 模式 Lite：無容器啟動——四個服務全在主機本機程序跑（backend/workflow/platform/frontend），
# 外加 uvx 起 LiteLLM 供 mock-gpt 路由。不碰 Docker、不需 PostgreSQL/RabbitMQ/mem0/Langfuse。
#   backend : DB_PROVIDER=inmemory EMBEDDINGS_PROVIDER=fake  （以 InMemory repo 取代 PostgreSQL，無 DbBootstrap；完整清單見 backend/AGENTS.md）
#   workflow: LLM_MODEL=mock-gpt LANGFUSE_ENABLED=false
#   platform: MEM0_MODE=inmemory OTEL_MODE=console CHAT_MODEL=mock-gpt
#   frontend: npm run dev
# 收攤：./scripts/stop-lite.sh   （用 --skip-litellm 可略過自帶的 LiteLLM）
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEVELOPMENT_ENVIRONMENT_IGNORE_DOT_ENV=true
. "$SCRIPT_DIR/_development-environment.sh"
unset DEVELOPMENT_ENVIRONMENT_IGNORE_DOT_ENV
RUN_DIR="$REPO_ROOT/.lite"   # 執行期產物（log / pid）集中處，已 gitignore
mkdir -p "$RUN_DIR"
PIDS_FILE="$RUN_DIR/start-lite.pids"

SKIP_LITELLM=0
[ "$1" = "--skip-litellm" ] && SKIP_LITELLM=1

echo "▶ 啟動 Lite 模式（無容器）…"

# ── 前置檢查 ───────────────────────────────────────────────────────────────
errors=()

if command -v dotnet >/dev/null 2>&1; then
  major=$(dotnet --version | cut -d. -f1)
  [ "$major" -ge 10 ] 2>/dev/null || errors+=("dotnet 10 SDK not found. Install from https://dotnet.microsoft.com/download")
else
  errors+=("dotnet 10 SDK not found. Install from https://dotnet.microsoft.com/download")
fi

command -v uv >/dev/null 2>&1 || errors+=("uv not found. Install from https://astral.sh/uv")
command -v node >/dev/null 2>&1 || errors+=("node not found. Install from https://nodejs.org/")

if command -v python3 >/dev/null 2>&1; then
  pyver=$(python3 -c 'import sys; print("%d.%d" % sys.version_info[:2])')
  pymaj=${pyver%%.*}; pymin=${pyver#*.}
  { [ "$pymaj" -gt 3 ] || { [ "$pymaj" -eq 3 ] && [ "$pymin" -ge 12 ]; }; } \
    || errors+=("Python 3.12+ not found. Install from https://www.python.org/downloads/")
else
  errors+=("Python 3.12+ not found. Install from https://www.python.org/downloads/")
fi

if [ ${#errors[@]} -gt 0 ]; then
  for err in "${errors[@]}"; do echo "ERROR: $err" >&2; done
  exit 1
fi
echo "✓ 前置檢查通過（dotnet 10 / uv / node / python 3.12+）"

# 每次啟動重置 PID 檔
: > "$PIDS_FILE"

# ── 平行啟動服務 ───────────────────────────────────────────────────────────
if [ "$SKIP_LITELLM" -eq 0 ]; then
  echo "  起 LiteLLM  :4000 …"
  ( cd "$REPO_ROOT"
    # config 的 master_key 讀 os.environ/LITELLM_MASTER_KEY；不設就靜默視為無 master key（免驗證），故給預設值
    LITELLM_MASTER_KEY=sk-1234 \
    nohup uvx --from 'litellm[proxy]' litellm --config infra/litellm-config.lite.yaml --port 4000 \
      > "$RUN_DIR/litellm.log" 2>&1 &
    echo $! >> "$PIDS_FILE" )
else
  echo "  略過 LiteLLM（--skip-litellm）——請自行確認 :4000 已在跑"
fi

echo "  起 backend   :8002 …"
( cd "$REPO_ROOT/backend"
  DB_PROVIDER=inmemory EMBEDDINGS_PROVIDER=fake \
  ASPNETCORE_URLS=http://localhost:8002 \
  ASPNETCORE_ENVIRONMENT="$ASPNETCORE_ENVIRONMENT" \
  nohup dotnet run --no-launch-profile --project src/Backend.Api/Backend.Api.csproj \
    > "$RUN_DIR/backend.log" 2>&1 &
  echo $! >> "$PIDS_FILE" )

echo "  起 workflow  :8001 …"
( cd "$REPO_ROOT/workflow"
  APP_ENVIRONMENT="$APP_ENVIRONMENT" \
  BACKEND_BASE_URL=http://localhost:8002 \
  LLM_BASE_URL=http://localhost:4000 \
  LLM_MODEL=mock-gpt \
  LANGFUSE_ENABLED=false \
  nohup uv run uvicorn app.main:app --host 127.0.0.1 --port 8001 \
    > "$RUN_DIR/workflow.log" 2>&1 &
  echo $! >> "$PIDS_FILE" )

echo "  起 platform  :8080 …"
( cd "$REPO_ROOT/platform"
  MEM0_MODE=inmemory OTEL_MODE=console CHAT_MODEL=mock-gpt \
  ASPNETCORE_URLS=http://localhost:8080 \
  ASPNETCORE_ENVIRONMENT="$ASPNETCORE_ENVIRONMENT" \
  nohup dotnet run --no-launch-profile --project src/Platform.Web/Platform.Web.csproj \
    > "$RUN_DIR/platform.log" 2>&1 &
  echo $! >> "$PIDS_FILE" )

echo "  起 frontend  :5173 …"
( cd "$REPO_ROOT/frontend"
  nohup npm run dev > "$RUN_DIR/frontend.log" 2>&1 &
  echo $! >> "$PIDS_FILE" )

# ── 健康檢查 ───────────────────────────────────────────────────────────────
check_health() {
  local url=$1 name=$2 elapsed=0
  while [ $elapsed -lt 60 ]; do
    if curl -fs "$url" >/dev/null 2>&1; then
      echo "✓ $name healthy"
      return 0
    fi
    sleep 2
    elapsed=$((elapsed + 2))
  done
  echo "⚠ $name failed health check after 60s（見 .lite/$name.log）" >&2
  return 1
}

# 先收集完所有服務再判定：`|| unhealthy+=()` 同時擋掉 set -e 的中途中斷，也避免只回報第一個失敗的
unhealthy=()
check_health "http://localhost:8002/health"          "backend"  || unhealthy+=("backend")
check_health "http://localhost:8001/health"          "workflow" || unhealthy+=("workflow")
check_health "http://localhost:8080/actuator/health" "platform" || unhealthy+=("platform")

if [ ${#unhealthy[@]} -gt 0 ]; then
  echo "✗ Lite 模式啟動失敗：${unhealthy[*]} 未通過健康檢查（程序仍在跑，收攤用 ./scripts/stop-lite.sh）" >&2
  exit 1
fi

# ── 完成輸出 ───────────────────────────────────────────────────────────────
echo ""
echo "✓ Lite 模式已啟動"
echo "  backend:  http://localhost:8002  (logs: .lite/backend.log)"
echo "  workflow: http://localhost:8001  (logs: .lite/workflow.log)"
echo "  platform: http://localhost:8080  (logs: .lite/platform.log)"
echo "  frontend: http://localhost:5173  (logs: .lite/frontend.log)"
[ "$SKIP_LITELLM" -eq 0 ] && echo "  litellm:  http://localhost:4000  (logs: .lite/litellm.log)"
echo ""
echo "  停止：./scripts/stop-lite.sh"
exit 0
