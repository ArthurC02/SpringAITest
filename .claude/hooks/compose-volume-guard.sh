#!/usr/bin/env bash
# PreToolUse hook(dotnet-verifier 專用):擋掉會刪除 docker volume 的指令,
# 保護 Langfuse/postgres 等既有資料(驗證只允許 down,不允許 down -v / volume rm / prune)。
input=$(cat)
cmd=$(printf '%s' "$input" | python -c "import json,sys; d=json.load(sys.stdin); print(d.get('tool_input',{}).get('command',''))" 2>/dev/null)
if printf '%s' "$cmd" | grep -Eq 'compose[^|;&]*down[^|;&]*(-v|--volumes)|docker[[:space:]]+volume[[:space:]]+(rm|prune)|system[[:space:]]+prune'; then
  echo "[hook] 禁止刪除 docker volume（驗證流程只允許 docker compose down，不得帶 -v/--volumes 或 volume rm/prune）" >&2
  exit 2
fi
exit 0
