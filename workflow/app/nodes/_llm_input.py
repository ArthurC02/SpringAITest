"""節點共用的 LLM 輸入組裝與呼叫樣板：user 訊息、docs context、取單一欄位。

為什麼集中在此：多顆節點（summarize/triage/analyze_report/nl_logic/rag_answer）都重複
「呼叫 llm.structured → 取單一欄位 → None 退回預設值」與「docs → context 字串」兩段樣板；
收斂成一處,行為逐字不變(default 對齊各節點原本的 `else ""`),避免各自漂移。
"""


def build_user_message(state: dict, input_keys: tuple) -> str:
    """input_keys 依序組成 `k: repr(value)` 多行字串；空時退回
    normalized_query → query → 空字串（不讀未宣告的任意 state）。
    """
    if input_keys:
        return "\n".join(f"{k}: {state.get(k)!r}" for k in input_keys)
    return str(state.get("normalized_query") or state.get("query") or "")


def format_docs_context(docs) -> str:
    """把 docs 逐項組成 `[n] title：content`,以空行分隔（rag_answer / doc_insights 共用）。

    空 docs 守衛（各節點回傳文案不同）留在各節點，此處只負責 join。
    """
    return "\n\n".join(
        f"[{i + 1}] {doc['title']}：{doc['content']}" for i, doc in enumerate(docs)
    )


async def structured_field(llm, *, system, user, schema, field, default=""):
    """呼叫 llm.structured 後取單一欄位；LLM 回 None 時退回 default（對齊各節點原樣）。"""
    out = await llm.structured(system=system, user=user, schema=schema)
    return getattr(out, field) if out is not None else default
