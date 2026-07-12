#!/usr/bin/env bash
# Stop hook(frontend-implementer 專用):代理收工前 frontend 的 lint 與 build 必須全綠。
# 失敗 → exit 2 擋下收工,把錯誤尾段回饋給代理繼續修。
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT/frontend" || exit 0   # 沒有 frontend 目錄就不擋

out=$(npm run lint 2>&1)
if [ $? -ne 0 ]; then
  {
    echo "[hook] npm run lint 失敗,不可收工,請先修正:"
    echo "$out" | tail -40
  } >&2
  exit 2
fi

out=$(npm run build 2>&1)
if [ $? -ne 0 ]; then
  {
    echo "[hook] npm run build 失敗,不可收工,請先修正:"
    echo "$out" | tail -40
  } >&2
  exit 2
fi
exit 0
