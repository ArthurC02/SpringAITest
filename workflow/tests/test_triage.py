"""route() 決策表：直接對 triage 的路由函式做單元測試。

SIMPLE / COMPLEX 兩個標準分支已有 API 級測試（test_api.py），這裡只補
「LLM 回覆非標準字串」的等價類：含 COMPLEX 字樣（不分大小寫、夾雜其他字）
→ complex；其餘（含空字串）一律走確定性預設 simple（見 route() docstring）。
"""

import pytest

from app.workflows.triage import route


@pytest.mark.parametrize(
    ("category", "expected"),
    [
        ("這題很 complex", "complex"),  # 夾雜其他文字且小寫，仍應判為 complex
        ("無法判斷", "simple"),  # 不含 COMPLEX 字樣 → 預設 simple
        ("", "simple"),  # 空字串 → 預設 simple
    ],
    ids=["contains-complex", "unrecognized", "empty"],
)
def test_route_decision_table(category, expected):
    assert route({"question": "q", "category": category}) == expected
