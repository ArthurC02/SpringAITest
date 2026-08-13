# UserPromptSubmit hook: 每輪自動注入委派提醒。輸出 ensure_ascii JSON 以避開 Windows 編碼問題。
import json

DIRECTIVE = (
    "[標準指示(每輪自動注入)] 依 AGENTS.md 委派原則:任何非瑣碎工作(實作、審查、驗證、文件同步)"
    "必須先派出對應 subagent(dotnet/frontend/python-implementer、code-reviewer、code-simplifier、e2e-verifier、docs-updater),"
    "主迴圈只負責寫規格、委派、整合;獨立工作並行派出。只有小型編輯與快問快答可留在主迴圈。"
)

print(json.dumps({"hookSpecificOutput": {"hookEventName": "UserPromptSubmit", "additionalContext": DIRECTIVE}}))
