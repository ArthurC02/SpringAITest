"""nl_extract / nl_logic 共用：把 state 指定鍵組成 LLM 的 user 訊息。"""


def build_user_message(state: dict, input_keys: tuple) -> str:
    """input_keys 依序組成 `k: repr(value)` 多行字串；空時退回
    normalized_query → query → 空字串（不讀未宣告的任意 state）。
    """
    if input_keys:
        return "\n".join(f"{k}: {state.get(k)!r}" for k in input_keys)
    return str(state.get("normalized_query") or state.get("query") or "")
