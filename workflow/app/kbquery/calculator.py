"""確定性計算器：以 AST 白名單求值，取代 LLM 心算，結果可重現、可稽核。"""

import ast
import operator
from typing import Callable

from app.kbquery.models import CalculationTrace

# 允許的二元運算子白名單
_BIN_OPS: dict[type[ast.operator], Callable[[float, float], float]] = {
    ast.Add: operator.add,
    ast.Sub: operator.sub,
    ast.Mult: operator.mul,
    ast.Div: operator.truediv,
    ast.Pow: operator.pow,
}


def _eval_node(node: ast.expr, inputs: dict[str, float]) -> float:
    """遞迴求值單一 AST 節點；白名單外的節點一律 raise ValueError。"""
    if isinstance(node, ast.Constant):
        if isinstance(node.value, (int, float)) and not isinstance(node.value, bool):
            return float(node.value)
        raise ValueError(f"不支援的常數型別: {type(node.value).__name__}")
    if isinstance(node, ast.Name):
        if node.id in inputs:
            return inputs[node.id]
        raise ValueError(f"未提供的變數: {node.id}")
    if isinstance(node, ast.BinOp) and type(node.op) in _BIN_OPS:
        # 除以零讓 ZeroDivisionError 自然拋出，由節點層轉為 CALCULATION_ERROR
        return _BIN_OPS[type(node.op)](
            _eval_node(node.left, inputs), _eval_node(node.right, inputs)
        )
    if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub):
        return -_eval_node(node.operand, inputs)
    raise ValueError(f"不支援的節點型別: {type(node).__name__}")


def evaluate(formula: str, inputs: dict[str, float]) -> float:
    """求值公式字串；只允許數字、已提供變數、+ - * / **、一元負號與括號。"""
    tree = ast.parse(formula, mode="eval")
    return _eval_node(tree.body, inputs)


def build_trace(
    formula: str, inputs: dict[str, float], input_sources: list[str]
) -> CalculationTrace:
    """求值並組出完整計算軌跡（公式、輸入、輸入出處、結果）。"""
    return CalculationTrace(
        formula=formula,
        inputs=inputs,
        input_sources=input_sources,
        result=evaluate(formula, inputs),
    )
