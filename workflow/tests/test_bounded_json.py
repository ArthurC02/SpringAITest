"""app/runtime/bounded_json.py 集中 flow_harness/tool_boundary 共用的序列化與截斷邏輯。

兩處呼叫端唯一的差異是失敗回退策略與截斷後綴，因此測試聚焦在：截斷邊界
（on-point/off-point）、成功路徑不受影響、以及兩種失敗回退策略都保留原本語意。
"""

from __future__ import annotations

import json

import pytest

from app.runtime.bounded_json import bounded_canonical_json

_VALUE = {"a": "xxxxx"}
_SERIALIZED = json.dumps(
    _VALUE, ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":")
)


def test_text_at_exact_char_limit_is_not_truncated() -> None:
    # max_chars 剛好等於序列化長度 → 不截斷、不加後綴（off-point）。
    text = bounded_canonical_json(
        _VALUE, max_chars=len(_SERIALIZED), on_unserializable=lambda exc: "unused"
    )
    assert text == _SERIALIZED


def test_text_one_char_over_limit_is_truncated_with_suffix() -> None:
    # max_chars 少一個字元 → 觸發截斷且附加後綴（on-point）。
    text = bounded_canonical_json(
        _VALUE,
        max_chars=len(_SERIALIZED) - 1,
        on_unserializable=lambda exc: "unused",
        truncated_suffix="...",
    )
    assert text == _SERIALIZED[:-1] + "..."


def test_truncation_without_suffix_appends_nothing() -> None:
    # 不傳 truncated_suffix（預設 ""）→ 仍截斷，但不附加任何字元。
    text = bounded_canonical_json(
        _VALUE, max_chars=len(_SERIALIZED) - 1, on_unserializable=lambda exc: "unused"
    )
    assert text == _SERIALIZED[:-1]


def test_unserializable_fallback_can_raise_like_flow_harness_does() -> None:
    class Denied(RuntimeError):
        pass

    def _deny(exc: TypeError | ValueError) -> str:
        raise Denied("not serializable") from exc

    # default=str 會把大多數物件都字串化成功，真正會讓 json.dumps 失敗的是
    # allow_nan=False（NaN/Infinity）或循環參照；這裡用循環參照觸發 ValueError。
    circular: dict[str, object] = {}
    circular["self"] = circular
    with pytest.raises(Denied):
        bounded_canonical_json(circular, max_chars=100, on_unserializable=_deny)


def test_unserializable_fallback_can_return_placeholder_like_tool_boundary_does() -> None:
    text = bounded_canonical_json(
        {"bad": float("nan")},
        max_chars=100,
        on_unserializable=lambda exc: '{"status":"unserializable_result"}',
    )
    assert text == '{"status":"unserializable_result"}'


def test_unserializable_fallback_placeholder_is_also_truncated() -> None:
    # 回退佔位字串同樣走截斷路徑：超過 max_chars 一樣被切斷並附後綴。
    placeholder = '{"status":"unserializable_result"}'
    text = bounded_canonical_json(
        {"bad": float("nan")},
        max_chars=len(placeholder) - 1,
        on_unserializable=lambda exc: placeholder,
        truncated_suffix="...",
    )
    assert text == placeholder[:-1] + "..."
