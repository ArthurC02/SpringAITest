# 共用前置：切到 infra/、檢查 .env、先起 postgres 並備妥 mem0 需要的庫。
# 由 start-infra.sh / start-full.sh 以 .（dot-source）載入——呼叫端須先定義 SCRIPT_DIR；cd 與 exit 都作用在呼叫端。
. "$SCRIPT_DIR/_development-environment.sh"
cd "$SCRIPT_DIR/../infra"

if [ ! -f .env ]; then
  echo "✗ 找不到 infra/.env。請先建立：cp .env.example .env 後填入 OPENAI_API_KEY" >&2
  exit 1
fi

docker compose up -d postgres                 # 先起 postgres，好在 mem0 之前備妥它需要的庫
sh "$SCRIPT_DIR/ensure-mem0-db.sh"
