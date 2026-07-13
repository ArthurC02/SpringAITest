"""kb_query 圖的條件路由。"""

from app.kbquery.models import VerificationResult


def route_after_verification(state) -> str:
    """驗證後的三分支路由：

    - fatal_error 已設定 → "compose"（直接走安全 ABSTAIN + 稽核路徑）。
    - 驗證結果為 RETRY 且 retrieval_attempt < max_retrieval_attempts → "retry"
      （回到 retrieval_planner 換打法重查）。
    - 其餘（PASS、FAIL、重試額度耗盡）→ "compose"。

    無限循環防護：retrieval_attempt 由 retrieval_planner 每次規劃 +1，
    上限 max_retrieval_attempts 在 intake 時固定，RETRY 分支必然收斂。
    """
    if state.get("fatal_error"):
        return "compose"
    if state.get("verification_result") == VerificationResult.RETRY and state.get(
        "retrieval_attempt", 0
    ) < state.get("max_retrieval_attempts", 0):
        return "retry"
    return "compose"
