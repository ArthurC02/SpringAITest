"""Data Locator：在檢索結果中定位可追溯證據，並用確定性計算組出候選答案。"""

from app.engine.node_registry import node
from app.nodes.kbquery import calculator, textutils
from app.nodes.kbquery.models import CalculationTrace, Evidence
from app.nodes.kbquery.ports import EvidenceLocatorPort

# 查詢關鍵詞 → (公式, 單位, 運算描述)；依序比對，先中先贏
_FORMULA_RULES: list[tuple[tuple[str, ...], tuple[str, str, str]]] = [
    (("成長", "增長", "年增", "季增"), ("(a - b) / b * 100", "%", "成長率")),
    (("差", "差異"), ("a - b", "", "差異")),
    (("比率", "占比", "佔比"), ("a / b", "", "比率")),
]


def _select_evidence(
    evidences: list[Evidence],
    target_period,
    requires_multi_doc,
    requires_calculation,
) -> list[Evidence]:
    """依單／多期間需求選證據：多期間 → 每個目標期間各取分數最高一筆；否則取 top 1。"""
    raw_targets = (
        target_period
        if isinstance(target_period, list)
        else ([target_period] if target_period else [])
    )
    targets = [textutils.normalize_period(p) for p in raw_targets]
    multi = (
        isinstance(target_period, list) or requires_multi_doc or requires_calculation
    )
    selected: list[Evidence] = []
    if multi and targets:
        for t in targets:
            for e in evidences:  # 已依 locator_score desc 排序
                if textutils.normalize_period(e.period) == t:
                    selected.append(e)
                    break
    elif evidences:
        selected = [evidences[0]]
    return selected


def _compose_candidate_answer(
    selected: list[Evidence],
    query: str,
    metric: str,
    requires_calculation,
) -> tuple[str, float | None, CalculationTrace | None]:
    """組出 candidate_answer；requires_calculation 時走確定性計算分支。"""
    candidate_answer = ""
    calculation_result = None
    calculation_trace = None

    if requires_calculation:
        valued = [e for e in selected if e.exact_value]
        if len(valued) >= 2:
            # 依期間排序：b=較早、a=較晚
            valued.sort(key=lambda e: textutils.normalize_period(e.period))
            earlier, later = valued[0], valued[-1]
            b = textutils.parse_number(earlier.exact_value)
            a = textutils.parse_number(later.exact_value)
            for keywords, (formula, unit, op) in _FORMULA_RULES:
                if any(k in query for k in keywords):
                    # calculator 拋錯不接：交給 Harness（engine/node_shell.py）走安全路徑
                    trace = calculator.build_trace(
                        formula,
                        {"a": a, "b": b},
                        [earlier.source_id, later.source_id],
                    )
                    calculation_trace = trace
                    calculation_result = trace.result
                    candidate_answer = (
                        f"{metric}{op}為 {round(trace.result, 4)}{unit}"
                    )
                    break
    elif selected:
        top = selected[0]
        if top.exact_value:
            head = " ".join(x for x in (metric or top.metric, top.period) if x)
            prefix = f"{head} " if head else ""
            candidate_answer = f"{prefix}為 {top.exact_value}{top.unit}"
        else:
            # 無精確數值（如原因分析）→ 以原文摘錄為候選答案
            candidate_answer = top.exact_excerpt

    return candidate_answer, calculation_result, calculation_trace


@node(
    name="data_locator",
    version="1.0",
    description="在檢索結果中定位可追溯證據，並用確定性計算組出候選答案",
    reads=[
        "canonical_metric",
        "metric_terms",
        "target_period",
        "excluded_terms",
        "version_policy",
        "normalized_query",
        "ranked_sources",
        "requires_multi_doc",
        "requires_calculation",
    ],
    writes=[
        "selected_evidence",
        "page_evidence",
        "table_cell_evidence",
        "text_claims",
        "candidate_answer",
        "calculation_result",
        "calculation_trace",
    ],
    deps=["locators"],
    requires_tools=[],
)
def make_data_locator_node(locators: dict[str, EvidenceLocatorPort]):
    """建立 data_locator 節點：定位證據、依需求選證據、必要時做確定性計算。"""

    async def data_locator_node(state: dict) -> dict:
        context = {
            "canonical_metric": state.get("canonical_metric", ""),
            "metric_terms": state.get("metric_terms", []),
            "target_period": state.get("target_period", ""),
            "excluded_terms": state.get("excluded_terms", []),
            "version_policy": state.get("version_policy", {}),
            "query": state.get("normalized_query", ""),
        }

        evidences: list[Evidence] = []
        for source in state.get("ranked_sources") or []:
            locator = locators.get(source.source_type)
            if locator is None:
                continue
            evidences.extend(locator.locate(source, context=context))
        evidences.sort(key=lambda e: e.locator_score, reverse=True)

        page_evidence = [e for e in evidences if e.source_type == "text"]
        table_cell_evidence = [e for e in evidences if e.source_type == "table"]

        selected = _select_evidence(
            evidences,
            state.get("target_period", ""),
            state.get("requires_multi_doc"),
            state.get("requires_calculation"),
        )
        text_claims = [e.exact_excerpt for e in selected if e.exact_excerpt]

        candidate_answer, calculation_result, calculation_trace = (
            _compose_candidate_answer(
                selected,
                state.get("normalized_query", ""),
                state.get("canonical_metric", ""),
                state.get("requires_calculation"),
            )
        )

        return {
            "selected_evidence": selected,
            "page_evidence": page_evidence,
            "table_cell_evidence": table_cell_evidence,
            "text_claims": text_claims,
            "candidate_answer": candidate_answer,
            "calculation_result": calculation_result,
            "calculation_trace": calculation_trace,
        }

    return data_locator_node
