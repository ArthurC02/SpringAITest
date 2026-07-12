#!/usr/bin/env bash
# Stop hook(dotnet-implementer 專用):代理要收工前先確認 .NET 方案能編譯。
# 編譯失敗 → exit 2 擋下收工,並把錯誤尾段回饋給代理繼續修。
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# 監管倉庫內所有 .NET 方案(platform 與 backend);尚未建立的方案不擋
for SLN in "$ROOT/platform/Platform.sln" "$ROOT/backend/Backend.sln"; do
  [ -f "$SLN" ] || continue
  out=$(dotnet build "$SLN" -nologo -v q 2>&1)
  if [ $? -ne 0 ]; then
    {
      echo "[hook] dotnet build 失敗（$SLN），不可收工，請先修正："
      echo "$out" | tail -40
    } >&2
    exit 2
  fi
done
exit 0
