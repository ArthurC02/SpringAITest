"""PreToolUse no-touch guard.

Blocks (for ALL agents including the main loop):
1. Edit/Write/NotebookEdit on .claude/settings*.json  -> always deny (user-owned).
2. Edit/Write/NotebookEdit on dependency manifests    -> deny unless the
   sentinel file .claude/state/allow-manifest-edit exists (user authorized).
3. Bash/PowerShell commands containing `git stash`    -> always deny.

Rationale: repeated real incidents of subagents loosening settings, adding
unauthorized dependencies, and stashing dirty trees (see /insights 2026-08-09).
"""
import json
import os
import re
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stdin, "reconfigure"):
    sys.stdin.reconfigure(encoding="utf-8")


def deny(reason: str) -> None:
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }
    }, ensure_ascii=False))
    sys.exit(0)


def main() -> None:
    try:
        data = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)  # malformed input: fail open, guard is best-effort on parse

    tool = data.get("tool_name", "")
    tool_input = data.get("tool_input") or {}

    if tool in ("Edit", "Write", "NotebookEdit"):
        path = (tool_input.get("file_path") or tool_input.get("notebook_path") or "")
        norm = path.replace("\\", "/").lower()
        if re.search(r"\.claude/settings(\.local)?\.json$", norm):
            deny("BLOCKED: .claude/settings*.json 是使用者所有物,任何代理(含主迴圈)不得修改。"
                 "需要變更時回報使用者,由使用者自行修改或明確授權。")
        if re.search(r"(^|/)(package\.json|package-lock\.json|pyproject\.toml|uv\.lock)$", norm) \
                or norm.endswith(".csproj"):
            repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            sentinel = os.path.join(repo_root, "state", "allow-manifest-edit")
            if not os.path.exists(sentinel):
                deny("BLOCKED: 依賴清單/專案檔(package.json、pyproject.toml、uv.lock、*.csproj)預設禁改"
                     "(AGENTS.md:不得未經授權新增依賴)。子代理:回報主迴圈,不要繞過。"
                     "主迴圈:僅在使用者本輪明確授權後建立 .claude/state/allow-manifest-edit,改完立即刪除。")

    if tool in ("Bash", "PowerShell"):
        command = tool_input.get("command") or ""
        if re.search(r"\bgit\s+stash\b", command):
            deny("BLOCKED: 禁止 git stash(曾造成髒工作樹狀態遺失)。"
                 "改用 commit 到分支,或回報主迴圈處理。")

    sys.exit(0)


if __name__ == "__main__":
    main()
