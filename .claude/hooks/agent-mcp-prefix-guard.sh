#!/usr/bin/env bash
# PreToolUse hook:擋掉 .claude/agents/*.md 裡寫錯的 codebase-memory MCP 前綴。
# 實際 server 名是 codebase-memory-mcp(見 ~/.claude.json),但 mcp__codebase-memory__*
# 這個少了 -mcp 後綴的前綴會靜默拿不到任何工具、不報錯 —— 2026-07-24 修過一次,2026-08-13 又錯回來。
input=$(cat)
path=$(printf '%s' "$input" | python -c "import json,sys; d=json.load(sys.stdin); ti=d.get('tool_input',{}); print(ti.get('file_path') or ti.get('notebook_path') or '')" 2>/dev/null)
norm=$(printf '%s' "$path" | tr '\\' '/')
case "$norm" in
  */.claude/agents/*.md) ;;
  *) exit 0 ;;
esac
content=$(printf '%s' "$input" | python -c "import json,sys; d=json.load(sys.stdin); ti=d.get('tool_input',{}); print(ti.get('new_string') or ti.get('content') or '')" 2>/dev/null)
if printf '%s' "$content" | grep -q 'mcp__codebase-memory__'; then
  echo "[hook] 禁止寫入 mcp__codebase-memory__(缺 -mcp 後綴)—— 實際 server 名是 codebase-memory-mcp,錯的前綴會靜默拿不到工具、不報錯。請改用 mcp__codebase-memory-mcp__。" >&2
  exit 2
fi
exit 0
