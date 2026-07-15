"""Data Locator：在檢索結果中定位可追溯證據，並用確定性計算組出候選答案。"""

from app.engine.node_registry import node
from app.kbquery import calculator, textutils
from app.kbquery.models import Evidence
from app.kbquery.ports import EvidenceLocatorPort

# 查詢關鍵詞 → (公式, 單位, 運算描述)；依序比對，先中先贏
_FORMULA_RULES: list[tuple[tuple[str, ...], tuple[str, str, str]]] = [
    (("成長", "增長", "年增", "季增"), ("(a - b) / b * 100", "%", "成長率")),
    (("差", "差異"), ("a - b", "", "差異")),
    (("比率", "占比", "佔比"), ("a / b", "", "比率")),
]


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

        # 選擇證據：多期間需求 → 每個目標期間各取分數最高一筆；否則取 top 1
        target = state.get("target_period", "")
        raw_targets = (
            target if isinstance(target, list) else ([target] if target else [])
        )
        targets = [textutils.normalize_period(p) for p in raw_targets]
        multi = (
            isinstance(target, list)
            or state.get("requires_multi_doc")
            or state.get("requires_calculation")
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

        text_claims = [e.exact_excerpt for e in selected if e.exact_excerpt]

        q = state.get("normalized_query", "")
        metric = state.get("canonical_metric", "")
        candidate_answer = ""
        calculation_result = None
        calculation_trace = None

        if state.get("requires_calculation"):
            valued = [e for e in selected if e.exact_value]
            if len(valued) >= 2:
                # 依期間排序：b=較早、a=較晚
                valued.sort(key=lambda e: textutils.normalize_period(e.period))
                earlier, later = valued[0], valued[-1]
                b = textutils.parse_number(earlier.exact_value)
                a = textutils.parse_number(later.exact_value)
                for keywords, (formula, unit, op) in _FORMULA_RULES:
                    if any(k in q for k in keywords):
                        # calculator 拋錯不接：交給 Harness（engine/harness.py）走安全路徑
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
