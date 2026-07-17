"""Evidence Verification：全部確定性檢查的驗證閘門，不用 LLM；未通過不放行任何答案。"""

from app.engine.node_registry import node
from app.nodes.kbquery import calculator, textutils
from app.nodes.kbquery.models import Evidence, FailureCode, VerificationResult
from app.nodes.kbquery.nodes.context_resolver import VERSION_TERMS

_EPS = 1e-6
# candidate_answer 中的計算結果是 round(…, 4) 的顯示值，比對容差需對齊捨入精度
_DISPLAY_EPS = 5e-5


def _safe_parse(value: str) -> float | None:
    try:
        return textutils.parse_number(value)
    except ValueError:
        return None


@node(
    name="evidence_verification",
    version="1.0",
    description="確定性驗證閘門",
    # calculation_result 亦為讀取鍵（數值一致性檢查用），規格書 §2.1 範例漏列
    reads=[
        "selected_evidence",
        "target_period",
        "canonical_metric",
        "excluded_terms",
        "metric_terms",
        "candidate_answer",
        "calculation_result",
        "calculation_trace",
        "requires_calculation",
    ],
    writes=[
        "verification_result",
        "confidence",
        "failure_reason",
        "failure_codes",
        "verified_evidence",
    ],
    deps=[],
    requires_tools=[],
)
def make_evidence_verification_node():
    """建立 evidence_verification 節點：逐筆檢查期間、指標、排除詞、表格對位與可追溯性。"""

    async def evidence_verification_node(state: dict) -> dict:
        evidences: list[Evidence] = state.get("selected_evidence") or []
        codes: list[FailureCode] = []

        if not evidences:
            codes.append(FailureCode.INSUFFICIENT_EVIDENCE)

        target = state.get("target_period", "")
        raw_targets = (
            target if isinstance(target, list) else ([target] if target else [])
        )
        targets = [textutils.normalize_period(p) for p in raw_targets]
        canonical = state.get("canonical_metric", "")
        metric_terms = state.get("metric_terms") or []
        excluded_terms = state.get("excluded_terms") or []

        for e in evidences:
            # 1 期間：證據必須「證明」期間，不可含糊放行
            if targets and (
                not e.period or textutils.normalize_period(e.period) not in targets
            ):
                codes.append(FailureCode.PERIOD_MISMATCH)
            # 2 指標
            if canonical and (not e.metric or e.metric != canonical):
                codes.append(FailureCode.METRIC_MISMATCH)
            # 3 排除詞：口徑詞 → VERSION_MISMATCH，其餘 → METRIC_MISMATCH
            haystack = " ".join(
                (
                    e.row_identifier,
                    e.column_identifier,
                    e.exact_excerpt,
                    e.metric,
                    e.document_title,
                )
            )
            for term in excluded_terms:
                if term and term in haystack:
                    codes.append(
                        FailureCode.VERSION_MISMATCH
                        if term in VERSION_TERMS
                        else FailureCode.METRIC_MISMATCH
                    )
            # 4 表格證據：列名須含指標詞、欄名期間須為目標期間
            if e.source_type == "table":
                if metric_terms and not any(
                    t in e.row_identifier for t in metric_terms
                ):
                    codes.append(FailureCode.TABLE_CELL_MISMATCH)
                if (
                    targets
                    and textutils.normalize_period(e.column_identifier) not in targets
                ):
                    codes.append(FailureCode.TABLE_CELL_MISMATCH)
            # 5 可追溯性
            if not e.document_id or (
                e.page_number is None
                and not e.exact_excerpt
                and not (e.row_identifier and e.column_identifier)
                and e.source_type != "structured"
            ):
                codes.append(FailureCode.SOURCE_NOT_TRACEABLE)

        # 數值一致性：candidate_answer 的數字必須有出處
        pairs = textutils.extract_value_unit(state.get("candidate_answer", ""))
        if pairs:
            cand_val = _safe_parse(pairs[0][0])
            calc_result = state.get("calculation_result")
            if cand_val is not None:
                if calc_result is not None:
                    if abs(cand_val - calc_result) > _DISPLAY_EPS:
                        codes.append(FailureCode.VALUE_MISMATCH)
                else:
                    values = [
                        v
                        for e in evidences
                        if e.exact_value
                        if (v := _safe_parse(e.exact_value)) is not None
                    ]
                    if not any(abs(v - cand_val) <= _EPS for v in values):
                        codes.append(FailureCode.VALUE_MISMATCH)

        # 衝突：同 (指標, 期間) 的證據數值不一致
        for i, a in enumerate(evidences):
            for b in evidences[i + 1 :]:
                if a.metric == b.metric and textutils.normalize_period(
                    a.period
                ) == textutils.normalize_period(b.period):
                    va, vb = _safe_parse(a.exact_value), _safe_parse(b.exact_value)
                    if va is not None and vb is not None and abs(va - vb) > _EPS:
                        codes.append(FailureCode.CONFLICTING_EVIDENCE)

        # 計算複驗：公式重算 + 每個輸入值必須能對回證據
        if state.get("requires_calculation"):
            trace = state.get("calculation_trace")
            if trace is None:
                codes.append(FailureCode.INSUFFICIENT_EVIDENCE)
            else:
                try:
                    if (
                        abs(
                            calculator.evaluate(trace.formula, trace.inputs)
                            - trace.result
                        )
                        > _EPS
                    ):
                        codes.append(FailureCode.CALCULATION_ERROR)
                except Exception:
                    codes.append(FailureCode.CALCULATION_ERROR)
                evidence_values = [
                    v
                    for e in evidences
                    if e.exact_value
                    if (v := _safe_parse(e.exact_value)) is not None
                ]
                for value in trace.inputs.values():
                    if not any(abs(value - v) <= _EPS for v in evidence_values):
                        codes.append(FailureCode.CALCULATION_ERROR)
                        break

        codes = list(dict.fromkeys(codes))
        if not codes:
            return {
                "verification_result": VerificationResult.PASS,
                "confidence": min(
                    1.0, sum(e.locator_score for e in evidences) / len(evidences)
                ),
                "failure_reason": "",
                "failure_codes": [],
                "verified_evidence": evidences,
            }

        hard_fail = {FailureCode.CONFLICTING_EVIDENCE, FailureCode.CALCULATION_ERROR}
        return {
            "verification_result": (
                VerificationResult.FAIL
                if hard_fail & set(codes)
                else VerificationResult.RETRY
            ),
            "confidence": 0.0,
            "failure_reason": "; ".join(c.value for c in codes)
            + "：證據未通過確定性驗證",
            "failure_codes": codes,
            "verified_evidence": [],
        }

    return evidence_verification_node
