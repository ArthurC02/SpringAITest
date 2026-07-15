#!/usr/bin/env bash
# Stop hook(python-implementer 專用):代理要收工前先確認 workflow 服務測試全綠。
# 測試失敗 → exit 2 擋下收工,並把錯誤尾段回饋給代理繼續修。
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT/workflow" || exit 0
out=$(uv run pytest -q 2>&1)
if [ $? -ne 0 ]; then
  {
    echo "[hook] workflow 測試未全綠,不可收工,請先修正:"
    echo "$out" | tail -40
  } >&2
  exit 2
fi
exit 0
