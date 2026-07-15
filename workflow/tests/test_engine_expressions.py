"""條件式求值器測試（AT2-09 ~ AT2-14）：白名單內求值正確、白名單外一律拒絕。

安全語義的驗收核心在後半段：函式呼叫、屬性鏈、import、dunder、下標、賦值運算子
等一律在**執行期**也拋 ExpressionError —— 存檔驗證可以被繞過（直接呼叫 evaluate），
求值器自己必須是最後一道關。
"""

import pytest

from app.engine import expressions
from app.engine.expressions import ExpressionError, evaluate, validate

# ---------------------------------------------------------------------------
# AT2-09 ~ AT2-12 白名單內的求值行為
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "expr,state,expected",
    [
        # 【AT2-09】比較運算
        ("state.confidence < 0.7", {"confidence": 0.5}, True),
        ("state.confidence < 0.7", {"confidence": 0.9}, False),
        ("state.confidence >= 0.7", {"confidence": 0.7}, True),
        ("state.n == 2", {"n": 2}, True),
        ("state.n != 2", {"n": 2}, False),
        # 【AT2-10】and / or / not
        ("state.a and not state.b", {"a": True, "b": False}, True),
        ("state.a and not state.b", {"a": True, "b": True}, False),
        ("state.a or state.b", {"a": False, "b": False}, False),
        ("state.a or state.b", {"a": False, "b": True}, True),
        # 【AT2-11】in
        ("state.verification_result in ['PASS', 'RETRY']", {"verification_result": "PASS"}, True),
        ("state.verification_result in ['PASS', 'RETRY']", {"verification_result": "FAIL"}, False),
        ("state.x not in ['A']", {"x": "B"}, True),
        # 【AT2-12】不存在的鍵求值為 None（不拋錯，可與 None 比較）
        ("state.no_such_key == None", {}, True),
        ("state.no_such_key != None", {}, False),
        ("state.fatal_error != None", {"fatal_error": "query_intake: 炸了"}, True),
        # 括號與負數常數
        ("(state.a or state.b) and state.c", {"a": False, "b": True, "c": True}, True),
        ("state.delta > -1", {"delta": 0}, True),
    ],
)
def test_evaluate_whitelisted_expressions(expr, state, expected):
    assert evaluate(expr, state) is expected


def test_kb_query_loop_condition_shape():
    """kb_query.yaml 的離開條件：三個 or 子句對照 routing.route_after_verification。"""
    expr = (
        "state.fatal_error != None "
        "or state.verification_result != 'RETRY' "
        "or state.retrieval_attempt >= state.max_retrieval_attempts"
    )
    # fatal → 立刻離開（短路，右側的 attempt 比較不會因鍵不存在而爆炸）
    assert evaluate(expr, {"fatal_error": "query_intake: 空白"}) is True
    # RETRY 且還有額度 → 續跑
    base = {"verification_result": "RETRY", "retrieval_attempt": 1, "max_retrieval_attempts": 2}
    assert evaluate(expr, base) is False
    # RETRY 但額度用盡 → 離開
    assert evaluate(expr, {**base, "retrieval_attempt": 2}) is True
    # PASS → 離開
    assert evaluate(expr, {**base, "verification_result": "PASS"}) is True


# ---------------------------------------------------------------------------
# AT2-13 / AT2-14 與其他逃逸樣本：白名單外語法在求值期也必須拒絕
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "expr",
    [
        "len(state.x)",  # 【AT2-13】函式呼叫
        "os.system('echo x')",  # 【AT2-13】函式呼叫 + 屬性鏈（規格 §3.4 的 invalid_expression 範例）
        "state.x.y",  # 【AT2-14】屬性鏈（state. 一層以外）
        "state.x.__class__",  # dunder 屬性
        "state.__loop_0_count == 1",  # 【AT2-20】__ 前綴鍵非 Skill 可存取範圍
        "state['x'] == 1",  # 下標
        "__import__('os')",
        "state.x + 1 == 2",  # 算術（條件式不需要，未明確允許就拒絕）
        "[x for x in state.y]",  # comprehension
        "lambda: 1",
        "state.x if state.y else state.z",  # 三元
        "True; import os",  # 語法錯誤 → 一樣收斂成 ExpressionError
        "",
    ],
)
def test_non_whitelisted_syntax_rejected_at_both_phases(expr):
    """存檔期（validate）與執行期（evaluate）都拒絕，不得靜默通過。"""
    with pytest.raises(ExpressionError):
        validate(expr)
    with pytest.raises(ExpressionError):
        evaluate(expr, {"x": [1], "y": True, "z": 1})


def test_and_short_circuit_does_not_skip_validation_of_right_operand():
    """左運算元為假也要驗右邊：靜態檢查是完整走訪，不隨求值短路而漏檢。"""
    with pytest.raises(ExpressionError):
        evaluate("state.missing and len(state.x)", {"x": [1]})


def test_incompatible_comparison_raises_instead_of_silently_false():
    """None < 0.7（鍵不存在）明確報錯：迴圈離開條件若靜默失真，治理護欄等於失效。"""
    with pytest.raises(ExpressionError):
        evaluate("state.confidence < 0.7", {})


def test_validate_accepts_state_key_with_single_underscore():
    """單底線鍵是合法領域鍵（只有 __ 前綴屬引擎保留）。"""
    expressions.validate("state._internal_ish == 1")
