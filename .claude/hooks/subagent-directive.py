# PreToolUse hook (matcher: Agent|Task): 每次 spawn subagent 時自動在 prompt 尾端附加標準指示。
import json
import sys

DIRECTIVE = (
    "\n\n[標準指示(自動附加)] 盡可能使用專用工具而不是 Grep 文本分析:"
    "結構、引用、呼叫鏈問題優先用 LSP 與 codebase-memory MCP(search_code/query_graph/trace_path),"
    "找檔案用 Glob、讀碼用 Read;Grep 只用於精確字串定位,不要靠大範圍文本掃描做推理。"
)

data = json.loads(sys.stdin.buffer.read().decode("utf-8"))
ti = data.get("tool_input") or {}
prompt = ti.get("prompt")
if isinstance(prompt, str) and DIRECTIVE not in prompt:
    ti["prompt"] = prompt + DIRECTIVE
    # json.dumps 預設 ensure_ascii=True,輸出純 ASCII,避開 Windows 主控台編碼問題
    print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "updatedInput": ti}}))
