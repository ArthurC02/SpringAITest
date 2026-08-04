#!/usr/bin/env bash
# 守護式資料庫遷移指令(03-design §1.3 末段)。
#
# 順序是刻意的:先解析、再列印精確目標,最後才問確認 —— 操作員確認的是他眼前看到的那一行,
# 不是他以為的環境變數。空變數永遠不會被推導成一個「預設」目標。
#
# 真正的守門邏輯與 .NET 端 MigrationTarget 是同一套(腳本問、程式再驗一次);
# ALLOW_DESTRUCTIVE_MIGRATION 只在這支腳本啟動的專用 migration 行程內、且連同命令列確認詞
# 一起出現時才成立。Backend 一般啟動完全忽略它。
#
# 用法:
#   ./scripts/migrate-db.sh [--connection <connstring>] [--allow-remote] [--lock-timeout <5-300>]
#                           [--compose-project <name>]
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

CONNECTION="${DB_CONNECTION_STRING:-}"
ALLOW_REMOTE=0
LOCK_TIMEOUT=60
COMPOSE_PROJECT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --connection) CONNECTION="${2:-}"; shift 2 ;;
    --allow-remote) ALLOW_REMOTE=1; shift ;;
    --lock-timeout) LOCK_TIMEOUT="${2:-}"; shift 2 ;;
    --compose-project) COMPOSE_PROJECT="${2:-}"; shift 2 ;;
    *) echo "✗ 無法辨識的參數:$1" >&2; exit 2 ;;
  esac
done

if [ -z "$CONNECTION" ]; then
  echo "✗ 未提供連線字串(DB_CONNECTION_STRING 或 --connection),拒絕推導預設目標。" >&2
  exit 2
fi

# 由連線字串取欄位(大小寫不敏感、缺欄位回空字串)。
field() {
  printf '%s' "$CONNECTION" | tr ';' '\n' | awk -F= -v key="$1" '
    { k = $1; gsub(/^[ \t]+|[ \t]+$/, "", k) }
    tolower(k) == tolower(key) { print substr($0, index($0, "=") + 1); exit }'
}

DB_HOST="$(field Host)"
DB_PORT="$(field Port)"
DB_NAME="$(field Database)"
DB_USER="$(field Username)"

if [ -z "$DB_HOST" ] || [ -z "$DB_NAME" ] || [ -z "$DB_USER" ]; then
  echo "✗ 連線字串缺少 Host/Database/Username 其中之一,拒絕推導預設目標。" >&2
  exit 2
fi

case "$(printf '%s' "$DB_NAME" | tr '[:upper:]' '[:lower:]')" in
  postgres|template0|template1)
    echo "✗ 拒絕以受保護的系統資料庫 \`$DB_NAME\` 為 migration 目標。" >&2
    exit 2 ;;
esac

echo "migration 目標:host=$DB_HOST port=${DB_PORT:-5432} database=$DB_NAME user=$DB_USER${COMPOSE_PROJECT:+ composeProject=$COMPOSE_PROJECT}"

CONFIRM_TOKEN="RESET $DB_NAME"
printf '輸入確認詞以繼續(%s):' "$CONFIRM_TOKEN"
IFS= read -r CONFIRMATION
if [ "$CONFIRMATION" != "$CONFIRM_TOKEN" ]; then
  echo "✗ 確認詞不符,未做任何變更。" >&2
  exit 2
fi

EXTRA_ARGS=()
case "$DB_HOST" in
  localhost|127.0.0.1|::1|"[::1]") ;;
  *)
    if [ "$ALLOW_REMOTE" -ne 1 ]; then
      echo "✗ 目標 \`$DB_HOST\` 不是本機,需要 --allow-remote 才能對遠端資料庫執行破壞性 migration。" >&2
      exit 2
    fi
    REMOTE_TOKEN="$DB_HOST/$DB_NAME"
    printf '遠端目標需要第二次精確確認(%s):' "$REMOTE_TOKEN"
    IFS= read -r REMOTE_CONFIRMATION
    if [ "$REMOTE_CONFIRMATION" != "$REMOTE_TOKEN" ]; then
      echo "✗ 遠端第二次確認不符,未做任何變更。" >&2
      exit 2
    fi
    EXTRA_ARGS+=(--allow-remote --confirm-remote "$REMOTE_TOKEN")
    ;;
esac

if [ -n "$COMPOSE_PROJECT" ]; then
  EXTRA_ARGS+=(--compose-project "$COMPOSE_PROJECT")
fi

# ALLOW_DESTRUCTIVE_MIGRATION 只存在於這個子行程的環境裡。
# `${arr[@]+...}` 是必要的:bash 3.2(macOS 內建)在 set -u 下把「空陣列展開」當成未綁定變數而中止。
ALLOW_DESTRUCTIVE_MIGRATION=true dotnet run --project "$REPO_ROOT/backend/src/Backend.Api" -- \
  migrate-db \
  --connection "$CONNECTION" \
  --confirm "$CONFIRM_TOKEN" \
  --lock-timeout "$LOCK_TIMEOUT" \
  "${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}"
