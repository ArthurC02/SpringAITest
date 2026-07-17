"""三類 Evidence Locator（EvidenceLocatorPort）：在單筆檢索結果中定位可追溯證據。

context 內容：canonical_metric、metric_terms、target_period、excluded_terms、query。
"""

import json
import re
from typing import Any

from app.nodes.kbquery.models import Evidence, SourceResult
from app.nodes.kbquery.textutils import extract_value_unit, find_periods, normalize_period

# 文字證據的切句符號
_SENTENCE_SPLIT_RE = re.compile(r"[。；;\n]")


def _period_targets(context: dict[str, Any]) -> list[str]:
    """target_period 正規化為 list[str]：None→[]、str→[x]、list 原樣。"""
    tp = context.get("target_period")
    if tp is None:
        return []
    if isinstance(tp, str):
        return [normalize_period(tp)]
    return [normalize_period(p) for p in tp]


class TextEvidenceLocator:
    """定位文字段落中的證據句（source_type == "text"）。"""

    def locate(
        self, source: SourceResult, *, context: dict[str, Any]
    ) -> list[Evidence]:
        content = source.metadata.get("content", "")
        metric_terms = context.get("metric_terms") or []
        canonical = context.get("canonical_metric", "")
        targets = _period_targets(context)

        evidences: list[Evidence] = []
        for sentence in _SENTENCE_SPLIT_RE.split(content):
            sentence = sentence.strip()
            if not sentence:
                continue
            term_hit = any(t in sentence for t in metric_terms)
            # metric_terms 為空時（簡化）任何含數值的句子皆候選
            if metric_terms and not term_hit:
                continue

            pairs = extract_value_unit(sentence)
            periods = find_periods(sentence)
            period = periods[0] if periods else ""
            base = Evidence(
                source_id=source.source_id,
                document_id=source.document_id,
                document_title=source.document_title,
                document_version=source.version,
                source_type=source.source_type,
                page_number=source.page,
                exact_excerpt=sentence[:300],
                metric=canonical if term_hit else "",
                period=period,
            )
            if pairs:
                value, unit = pairs[0]
                score = (
                    0.4
                    + (0.3 if period and period in targets else 0.0)
                    + (0.3 if term_hit else 0.0)
                )
                evidences.append(
                    base.model_copy(
                        update={
                            "exact_value": value,
                            "unit": unit,
                            "locator_score": score,
                        }
                    )
                )
            elif term_hit:
                # CAUSE_ANALYSIS 類無數值文字證據：含指標詞即可成立，分數較低
                evidences.append(base.model_copy(update={"locator_score": 0.3}))

        evidences.sort(key=lambda e: e.locator_score, reverse=True)
        return evidences[:3]


class TableCellLocator:
    """定位表格儲存格（source_type == "table"）：必須到 row/column/cell，不回整張表。"""

    def locate(
        self, source: SourceResult, *, context: dict[str, Any]
    ) -> list[Evidence]:
        table = source.metadata.get("table")
        if not table:
            return []
        metric_terms = context.get("metric_terms") or []
        if not metric_terms:
            # 表格問題必須定位到正確 row，無指標詞不回任何證據
            return []
        canonical = context.get("canonical_metric", "")
        targets = _period_targets(context)

        evidences: list[Evidence] = []
        for row in table.get("rows", []):
            row_id = row.get("row_id", "")
            if not any(t in row_id for t in metric_terms):
                continue
            for col, cell in row.get("cells", {}).items():
                col_period = normalize_period(col)
                if targets:
                    if col_period not in targets:
                        continue
                    score = 0.9  # row + column 都精確匹配
                else:
                    score = 0.6  # column 未經期間過濾
                evidences.append(
                    Evidence(
                        source_id=source.source_id,
                        document_id=source.document_id,
                        document_title=source.document_title,
                        document_version=source.version,
                        source_type=source.source_type,
                        page_number=source.page,
                        exact_value=str(cell),
                        table_name=table.get("table_name", ""),
                        sheet_name=table.get("sheet_name", ""),
                        row_identifier=row_id,
                        column_identifier=col,
                        metric=canonical,
                        period=col_period,
                        unit=table.get("unit", ""),
                        locator_score=score,
                    )
                )
        return evidences


class StructuredDataLocator:
    """定位結構化資料列（source_type == "structured"）。"""

    def locate(
        self, source: SourceResult, *, context: dict[str, Any]
    ) -> list[Evidence]:
        record = source.metadata.get("record")
        if not record:
            return []
        metric_terms = context.get("metric_terms") or []
        canonical = context.get("canonical_metric", "")
        metric = record.get("metric", "")
        if metric not in metric_terms and metric != canonical:
            return []
        targets = _period_targets(context)
        period = normalize_period(str(record.get("period", "")))
        if targets and period not in targets:
            return []
        return [
            Evidence(
                source_id=source.source_id,
                document_id=source.document_id,
                document_title=source.document_title,
                document_version=source.version,
                source_type=source.source_type,
                page_number=source.page,
                exact_value=str(record.get("value")),
                # 保存查詢條件與資料列識別，供稽核重現
                row_identifier=json.dumps(
                    record.get("query_conditions", {}), ensure_ascii=False
                ),
                metric=metric,
                period=period,
                unit=record.get("unit", ""),
                locator_score=0.95,
            )
        ]
