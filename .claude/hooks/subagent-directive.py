# PreToolUse hook (matcher: Agent|Task): 每次 spawn subagent 時自動在 prompt 尾端附加標準指示。
# 兩段指示:
#   DIRECTIVE          — 全部 subagent 都附加(優先用專用工具而非 Grep 文本分析)。
#   PONYTAIL_DIRECTIVE — 只對「寫程式」的 implementer 附加(subagent 不觸發 SessionStart,
#                        拿不到主迴圈的 ponytail;靠這裡依 subagent_type 補上精簡準則)。
import json
import sys

DIRECTIVE = (
    "\n\n[標準指示(自動附加)] 盡可能使用專用工具而不是 Grep 文本分析:"
    "結構、引用、呼叫鏈問題優先用 LSP 與 codebase-memory MCP(search_code/query_graph/trace_path),"
    "找檔案用 Glob、讀碼用 Read;Grep 只用於精確字串定位,不要靠大範圍文本掃描做推理。"
)

# ponytail 只適合寫程式(YAGNI/重用/最小 diff),不適合純驗證(e2e-verifier)或文件(docs-updater);
# code-reviewer 專注正確性/契約,不加簡化偏好以免稀釋。
PONYTAIL_AGENTS = {"dotnet-implementer", "frontend-implementer", "python-implementer"}

PONYTAIL_DIRECTIVE = (
    "\n\n[Ponytail 模式(自動附加,full)] 你是懶惰的資深工程師 —— 懶=高效,不是隨便。"
    "先讀懂問題與這次要改的程式、追過真正的流程,再爬階梯,停在第一個成立的層級:"
    "① 這需要存在嗎?投機需求就跳過並一句說明(YAGNI);"
    "② 這 repo 已有的 helper/util/型別/模式能重用嗎?先看再寫,重造幾個檔外就有的東西是最常見的爛;"
    "③ 標準庫能做嗎?④ 已安裝的相依能做嗎?別為幾行程式加新相依;⑤ 能一行就一行;⑥ 否則寫剛好可動的最小量。"
    "規則:不做沒要求的抽象(單一實作不開 interface/factory、不變的值不開 config)、刪優於加、"
    "無聊優於聰明、最短可動 diff 優先 —— 但先真的懂問題,錯地方的最小改動不是懶而是第二個 bug。"
    "bug 修根因不修症狀:改前 grep 所有 caller,修在共用處一次。"
    "刻意簡化用 `ponytail:` 註解標明理由與已知上限。"
    "非瑣碎邏輯(分支/迴圈/parser/金額/安全路徑)留一個可跑的最小檢查(assert 的 demo 或一支 test_*),瑣碎一行免測。"
    "絕不簡化掉:信任邊界的輸入驗證、防資料遺失的錯誤處理、安全、無障礙、以及使用者明確要求的東西。"
    "產出:程式優先,最多三句說明,格式 `[做了什麼] → 略過:[X],需要時再加:[Y]`。"
)

data = json.loads(sys.stdin.buffer.read().decode("utf-8"))
ti = data.get("tool_input") or {}
prompt = ti.get("prompt")
if isinstance(prompt, str):
    changed = False
    if DIRECTIVE not in prompt:
        prompt += DIRECTIVE
        changed = True
    if ti.get("subagent_type") in PONYTAIL_AGENTS and PONYTAIL_DIRECTIVE not in prompt:
        prompt += PONYTAIL_DIRECTIVE
        changed = True
    if changed:
        ti["prompt"] = prompt
        # json.dumps 預設 ensure_ascii=True,輸出純 ASCII,避開 Windows 主控台編碼問題
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "updatedInput": ti}}))
