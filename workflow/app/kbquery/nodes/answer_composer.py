"""Answer Composer：PASS 用確定性模板組稿（不呼叫 LLM，輸出才可稽核），其餘一律 ABSTAIN。"""

from app.kbquery.models import AnswerMode, Citation, VerificationResult

_ABSTAIN_HEADLINE = "【無法提供答案】現有證據不足以可靠回答此問題。"


def _compose_abstain(state: dict) -> dict:
    """標準化 ABSTAIN 訊息：失敗代碼、缺項與系統錯誤，不放堆疊。"""
    lines = [_ABSTAIN_HEADLINE]
    codes = state.get("failure_codes") or []
    if codes:
        lines.append("- 失敗代碼：" + "、".join(c.value for c in codes))
    for item in state.get("unresolved_context") or []:
        lines.append(f"- 缺少或未確認：{item}")
    if state.get("target_period"):
        lines.append(f"- 需要期間 {state['target_period']} 的可追溯資料")
    if state.get("canonical_metric"):
        lines.append(f"- 需要指標 {state['canonical_metric']} 的可追溯資料")
    fatal = state.get("fatal_error", "")
    if fatal:
        node = fatal.split(":", 1)[0].strip()
        error_type = next(
            (
                err.get("error_type", "")
                for err in (state.get("errors") or [])
                if err.get("node") == node
            ),
            "",
        )
        lines.append(f"- 系統錯誤：{node} {error_type}".rstrip())
    return {
        "answer_mode": AnswerMode.ABSTAIN,
        "final_answer": "\n".join(lines),
        "source_citations": [],
        "assumption_note": "",
    }


def _compose_answer(state: dict) -> dict:
    """PASS 路徑：只用 verified evidence 組稿，不引入其他事實。"""
    verified = state["verified_evidence"]
    version_policy = state.get("version_policy") or {}
    policy_desc = "、".join(version_policy.values()) if version_policy else "未指定"

    lines = [f"【結論】{state.get('candidate_answer', '')}"]
    for e in verified:
        lines.append(
            f"【數值】{e.exact_value}{e.unit}（期間 {e.period}；口徑：{policy_desc}）"
        )
    trace = state.get("calculation_trace")
    if trace is not None:
        lines.append(
            f"【計算】{trace.formula}，輸入 {trace.inputs}，結果 {trace.result}"
        )
    lines.append("【引用】")
    for i, e in enumerate(verified, 1):
        parts = [f"{i}. 文件 {e.document_title}"]
        if e.document_version:
            parts.append(f"版本 {e.document_version}")
        if e.page_number is not None:
            parts.append(f"第 {e.page_number} 頁")
        if e.table_name:
            parts.append(f"表:{e.table_name}")
        if e.sheet_name:
            parts.append(f"Sheet:{e.sheet_name}")
        if e.row_identifier:
            parts.append(f"列:{e.row_identifier}")
        if e.column_identifier:
            parts.append(f"欄:{e.column_identifier}")
        lines.append(" ".join(parts))
    warnings = state.get("context_warnings") or []
    if warnings:
        lines.append("【限定條件】")
        lines.extend(f"- {w}" for w in warnings)

    citations = [
        Citation(
            document_title=e.document_title,
            document_version=e.document_version,
            page=e.page_number,
            table_name=e.table_name,
            sheet_name=e.sheet_name,
            row=e.row_identifier,
            column=e.column_identifier,
        )
        for e in verified
    ]
    return {
        "answer_mode": AnswerMode.ANSWER,
        "final_answer": "\n".join(lines),
        "source_citations": citations,
        "assumption_note": "；".join(warnings),
    }


def make_answer_composer_node():
    """建立 answer_composer 節點：不檢索、不呼叫 LLM，只依驗證結果組稿。"""

    async def answer_composer_node(state: dict) -> dict:
        if (
            state.get("fatal_error")
            or state.get("verification_result") != VerificationResult.PASS
        ):
            out = _compose_abstain(state)
        else:
            out = _compose_answer(state)
        out["answer_format_policy"] = (
            state.get("answer_format_policy") or "structured_text_zh"
        )
        return out

    return answer_composer_node
