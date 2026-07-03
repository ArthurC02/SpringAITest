#!/usr/bin/env sh
# 確保 mem0 需要的 postgres 前置條件。compose 無法宣告式處理這兩件事，故起 postgres 後由本腳本補上：
#   1) mem0 的關聯狀態庫 mem0_app —— mem0 的 db.py 預設連它，但 POSTGRES_DB 只會建 postgres 這一個庫。
#   2) collation 版本 —— 把 postgres 映像換成 pgvector 版後，沿用舊 data volume 會出現 collation 版本不符，
#      進而擋住「從 template1 複製建新庫」。先刷新即可（版本本來就相同時是無動作）。
# 本腳本可重複執行、無副作用。
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/../infra"

# 等 postgres 就緒（最多 ~60s）
i=0
until docker compose exec -T postgres pg_isready -U postgres >/dev/null 2>&1; do
  i=$((i + 1))
  [ "$i" -ge 30 ] && { echo "✗ postgres 未就緒，放棄準備 mem0 資料庫" >&2; exit 1; }
  sleep 2
done

# 先刷新 collation 版本（相同時無動作），否則沿用舊 volume 時下面的 CREATE DATABASE 會被擋。
docker compose exec -T postgres psql -U postgres -q \
  -c "ALTER DATABASE template1 REFRESH COLLATION VERSION;" \
  -c "ALTER DATABASE postgres REFRESH COLLATION VERSION;" >/dev/null 2>&1 || true

# 建 mem0_app（已存在則略過）。
if docker compose exec -T postgres psql -U postgres -tAc \
    "SELECT 1 FROM pg_database WHERE datname='mem0_app'" 2>/dev/null | grep -q 1; then
  echo "  ✓ 資料庫 mem0_app 已存在"
else
  docker compose exec -T postgres psql -U postgres -q -c "CREATE DATABASE mem0_app;" >/dev/null
  echo "  ✓ 已建立資料庫 mem0_app"
fi
