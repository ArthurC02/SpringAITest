#!/usr/bin/env bash
# 開發環境外部狀態清理(03-design §7)。只針對固定的 springaitest compose 專案與具名資源。
#
# 預設目標清單(與 reset-development-data.ps1 逐項相同):
#   1. appdb 應用資料庫             DROP + CREATE
#   2. LangGraph checkpoint 資料庫  只在 CHECKPOINT_DATABASE_URL 有值時處理
#   3. mem0 開發狀態                mem0_app 關聯庫 DROP + CREATE
#   4. RabbitMQ 應用佇列            documents.process / documents.process.dlq purge
#   5. .lite 本機狀態               解析後必須落在 repo 目錄內才刪
#
# 刻意不碰:Langfuse 追蹤、這些儲存體以外的上傳原始檔、以及任何不相干的 volume。
# 那些需要另一支有具名選項的指令,不是這裡的預設行為。
#
# 遇到非預期失敗即停(set -e),每清掉一項就回報一項。
set -eu

COMPOSE_PROJECT="springaitest"
CONFIRM_TOKEN="RESET springaitest development data"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# -P:先把 repo 根目錄本身解析成不含 symlink 的實體路徑,否則下面的包含關係比對比的是兩個不同宇宙。
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd -P)"
LITE_DIR="$REPO_ROOT/.lite"

APPDB_DATABASE="springaitest"
MEM0_DATABASE="mem0_app"
RABBIT_QUEUES="documents.process documents.process.dlq"

protected_database() {
  case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')" in
    ""|postgres|template0|template1) return 0 ;;
    *) return 1 ;;
  esac
}

# checkpoint 資料庫是選配的:未設定就不猜,直接回報略過。
CHECKPOINT_DATABASE=""
if [ -n "${CHECKPOINT_DATABASE_URL:-}" ]; then
  CHECKPOINT_DATABASE="$(printf '%s' "$CHECKPOINT_DATABASE_URL" | sed -e 's#.*/##' -e 's#?.*##')"
  if protected_database "$CHECKPOINT_DATABASE"; then
    echo "✗ CHECKPOINT_DATABASE_URL 解析出受保護或空的資料庫名 \`$CHECKPOINT_DATABASE\`,已中止。" >&2
    exit 2
  fi
  # 只接受合法的小寫識別字。`postgresql://appdb:5432`(漏了 /dbname)會解析出 `appdb:5432`,
  # 放行的話 CREATE DATABASE 會建出一個垃圾庫。
  if ! printf '%s' "$CHECKPOINT_DATABASE" | grep -Eq '^[a-z_][a-z0-9_]*$'; then
    echo "✗ CHECKPOINT_DATABASE_URL 解析出的資料庫名 \`$CHECKPOINT_DATABASE\` 不是合法識別字,已中止。" >&2
    exit 2
  fi
fi

if protected_database "$APPDB_DATABASE" || protected_database "$MEM0_DATABASE"; then
  echo "✗ 目標資料庫名不合法,已中止。" >&2
  exit 2
fi

# .lite 必須解析在 repo 目錄內才准遞迴刪除。字串比對擋不住 symlink,
# 所以真的存在時用 `cd` + `pwd -P` 取實體路徑再比對(指到 repo 外的連結一律拒絕,不跟著刪)。
# 確認畫面與實際刪除都用同一個解析後路徑,與 ps1 版語意一致。
RESOLVED_LITE="$LITE_DIR"
if [ -e "$LITE_DIR" ]; then
  RESOLVED_LITE="$(cd "$LITE_DIR" 2>/dev/null && pwd -P || true)"
fi
case "${RESOLVED_LITE:-}" in
  "$REPO_ROOT"/*) ;;
  *) echo "✗ .lite 解析結果 \`${RESOLVED_LITE:-(無法解析)}\` 不在 repo 目錄內,拒絕遞迴刪除。" >&2; exit 2 ;;
esac

echo "compose 專案:$COMPOSE_PROJECT"
echo "將清理的具名目標:"
echo "  1. appdb 資料庫        $APPDB_DATABASE"
echo "  2. checkpoint 資料庫   ${CHECKPOINT_DATABASE:-(CHECKPOINT_DATABASE_URL 未設定,略過)}"
echo "  3. mem0 關聯狀態庫     $MEM0_DATABASE"
echo "  4. RabbitMQ 佇列       $RABBIT_QUEUES"
echo "  5. Lite 本機狀態       $RESOLVED_LITE"
echo "不會被碰到:Langfuse 追蹤、上述儲存體以外的上傳原始檔、其他 volume。"

printf '輸入確認詞以繼續(%s):' "$CONFIRM_TOKEN"
IFS= read -r CONFIRMATION
if [ "$CONFIRMATION" != "$CONFIRM_TOKEN" ]; then
  echo "✗ 確認詞不符,未做任何變更。" >&2
  exit 2
fi

# 這一步只證明 docker compose 可用:`-p` 對不存在的專案同樣回 0,所以它不是專案存在性檢查。
# 真正的保護是後面每個 exec 失敗即中止(set -e,fail closed)。
if ! docker compose -p "$COMPOSE_PROJECT" ps >/dev/null 2>&1; then
  echo "✗ 無法執行 docker compose(專案 \`$COMPOSE_PROJECT\`),已中止。" >&2
  exit 1
fi

recreate_database() {
  service="$1"
  database="$2"
  docker compose -p "$COMPOSE_PROJECT" exec -T "$service" psql -U postgres -d postgres -q \
    -c "DROP DATABASE IF EXISTS \"$database\" WITH (FORCE);" \
    -c "CREATE DATABASE \"$database\";" >/dev/null
  echo "  ✓ 已重建資料庫 $database($service)"
}

recreate_database appdb "$APPDB_DATABASE"

if [ -n "$CHECKPOINT_DATABASE" ]; then
  recreate_database appdb "$CHECKPOINT_DATABASE"
else
  echo "  – checkpoint 資料庫未設定,略過"
fi

recreate_database postgres "$MEM0_DATABASE"
# TODO(P3):mem0 的 sqlite 變更紀錄(mem0_history volume)與 postgres 向量表需要先停容器才能安全清,
# 併入 P3 的切換程序處理;此處只清可在容器運行中驗證的關聯狀態庫。
echo "  – mem0 sqlite 變更紀錄與向量表留待 P3 切換程序(需先停容器)"

for queue in $RABBIT_QUEUES; do
  if docker compose -p "$COMPOSE_PROJECT" exec -T rabbitmq rabbitmqctl purge_queue "$queue" >/dev/null 2>&1; then
    echo "  ✓ 已清空佇列 $queue"
  else
    echo "  – 佇列 $queue 不存在,略過"
  fi
done

if [ -d "$RESOLVED_LITE" ]; then
  rm -rf "$RESOLVED_LITE"
  echo "  ✓ 已刪除 $RESOLVED_LITE"
else
  echo "  – $RESOLVED_LITE 不存在,略過"
fi

echo "完成。接著可執行 ./scripts/start-infra.sh 或 ./scripts/start-lite.sh 重新啟動,再跑開發 seed。"
