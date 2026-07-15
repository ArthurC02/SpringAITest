"""條件式求值器（Skill 的 `when` / `until`）：AST 白名單，預設拒絕。

為什麼不是 `eval`：Skill 定義由使用者（ADMIN）撰寫，條件式是資料不是程式碼。
`eval` 一旦放行，`__import__("os").system(...)` 就是一行的事。這裡沿用
app/kbquery/calculator.py 的作法 —— 先 ast.parse，再逐節點比對白名單，
**白名單外的語法一律 raise**（不是黑名單過濾），因此新增的 Python 語法
（walrus、f-string、comprehension…）預設就是拒絕，不需要回頭補黑名單。

驗證期與執行期用同一份檢查（_check 在 evaluate 前必跑）：AT2-13/14 要求
「繞過存檔驗證直接求值」也不得靜默通過。_check 是完整走訪、不短路，
`state.a and <非法>` 不會因為左邊為假就漏掉右邊的違規。
"""

import ast
import operator
from functools import lru_cache
from typing import Any, Callable


class ExpressionError(ValueError):
    """條件式含白名單外語法、或語法錯誤（對應錯誤碼 invalid_expression）。"""


# 比較運算子白名單（規格 §3.3：== != < <= > >= 與 in）
_CMP_OPS: dict[type[ast.cmpop], Callable[[Any, Any], Any]] = {
    ast.Eq: operator.eq,
    ast.NotEq: operator.ne,
    ast.Lt: operator.lt,
    ast.LtE: operator.le,
    ast.Gt: operator.gt,
    ast.GtE: operator.ge,
    ast.In: lambda a, b: a in b,
    ast.NotIn: lambda a, b: a not in b,
}

_CONST_TYPES = (str, int, float, bool, type(None))


def _state_key(node: ast.expr) -> str | None:
    """是 `state.<key>` 就回傳 key，否則 None（屬性鏈只允許一層）。"""
    if (
        isinstance(node, ast.Attribute)
        and isinstance(node.value, ast.Name)
        and node.value.id == "state"
    ):
        return node.attr
    return None


def _check(node: ast.expr) -> None:
    """遞迴驗證：白名單外的節點型別一律 ExpressionError。"""
    if isinstance(node, ast.Constant):
        if not isinstance(node.value, _CONST_TYPES):
            raise ExpressionError(f"不支援的常數型別: {type(node.value).__name__}")
        return

    if isinstance(node, ast.Attribute):
        key = _state_key(node)
        if key is None:
            # state.x.y 的外層 Attribute 走到這裡；純函式呼叫 os.system 也是
            raise ExpressionError("只允許 state.<key> 一層屬性讀取，不允許屬性鏈")
        if key.startswith("__"):
            # __loop_<id>_count 等引擎鍵是 Skill 不可讀寫的範圍（規格 §4）
            raise ExpressionError(f"不允許存取引擎保留鍵: state.{key}")
        return

    if isinstance(node, ast.Name):
        # `state` 只能當 state.<key> 的前綴出現，裸名（含 os、len）一律拒絕
        raise ExpressionError(f"不允許的名稱: {node.id}")

    if isinstance(node, ast.BoolOp) and isinstance(node.op, (ast.And, ast.Or)):
        for v in node.values:
            _check(v)
        return

    if isinstance(node, ast.UnaryOp) and isinstance(node.op, (ast.Not, ast.USub)):
        _check(node.operand)
        return

    if isinstance(node, ast.Compare):
        if any(type(op) not in _CMP_OPS for op in node.ops):
            raise ExpressionError("不支援的比較運算子")
        _check(node.left)
        for c in node.comparators:
            _check(c)
        return

    if isinstance(node, (ast.List, ast.Tuple)):
        # 只為了 `state.x in ["A", "B"]` 這種字面量集合
        for e in node.elts:
            _check(e)
        return

    raise ExpressionError(f"不支援的語法: {type(node).__name__}")


def _eval(node: ast.expr, state: dict) -> Any:
    """求值（呼叫前 _check 已保證節點型別在白名單內）。"""
    if isinstance(node, ast.Constant):
        return node.value
    if isinstance(node, ast.Attribute):
        # 不存在的鍵求值為 None（規格 §3.3：不拋錯，可與 None 比較）
        return state.get(node.attr)
    if isinstance(node, ast.BoolOp):
        if isinstance(node.op, ast.And):
            result: Any = True
            for v in node.values:
                result = _eval(v, state)
                if not result:
                    return result
            return result
        result = False
        for v in node.values:
            result = _eval(v, state)
            if result:
                return result
        return result
    if isinstance(node, ast.UnaryOp):
        if isinstance(node.op, ast.Not):
            return not _eval(node.operand, state)
        return -_eval(node.operand, state)
    if isinstance(node, ast.Compare):
        left = _eval(node.left, state)
        for op, comparator in zip(node.ops, node.comparators):
            right = _eval(comparator, state)
            try:
                if not _CMP_OPS[type(op)](left, right):
                    return False
            except TypeError as e:
                # None < 0.7（鍵不存在時）這類不相容比較：明確報錯而不是靜默當成 False——
                # 迴圈的離開條件若靜默失真，等於治理護欄失效。作者應改用 `== None` 判存在性。
                raise ExpressionError(f"不相容的比較: {e}")
            left = right
        return True
    if isinstance(node, (ast.List, ast.Tuple)):
        return [_eval(e, state) for e in node.elts]
    raise ExpressionError(f"不支援的語法: {type(node).__name__}")  # pragma: no cover


@lru_cache(maxsize=256)
def _parse(expression: str) -> ast.expr:
    """解析並驗證，結果快取（迴圈每輪都會求值同一條條件式）。"""
    try:
        tree = ast.parse(expression, mode="eval")
    except SyntaxError as e:
        raise ExpressionError(f"條件式語法錯誤: {e}")
    _check(tree.body)
    return tree.body


def validate(expression: str) -> None:
    """存檔期檢查：不合白名單就 ExpressionError（訊息即 invalid_expression 的 message）。"""
    _parse(expression)


def evaluate(expression: str, state: dict) -> Any:
    """執行期求值；一樣先過白名單（繞過存檔驗證也不得靜默通過）。"""
    return _eval(_parse(expression), state)
