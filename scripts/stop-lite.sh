#!/bin/bash
# 收攤 Lite 模式：讀 start-lite.pids，逐一終止程序，刪 PID 檔。
# 重複執行安全——無 PID 檔時回「No running processes found.」而不報錯。
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PIDS_FILE="$REPO_ROOT/.lite/start-lite.pids"

# 遞迴終止整棵行程樹：uv→uvicorn、npm→node/vite 的子行程不會隨父行程被殺而終止，
# 只殺父 PID 會留孤兒佔住 8001/5173。先深入殺子孫，再殺自己。
kill_tree() {
  local pid=$1 child
  for child in $(pgrep -P "$pid" 2>/dev/null); do
    kill_tree "$child"
  done
  kill -9 "$pid" 2>/dev/null || true
}

if [ -f "$PIDS_FILE" ]; then
  while IFS= read -r pid; do
    [ -n "$pid" ] && kill_tree "$pid"
  done < "$PIDS_FILE"
  rm -f "$PIDS_FILE"
  echo "All services stopped."
else
  echo "No running processes found."
fi
