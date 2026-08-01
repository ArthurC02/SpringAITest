"""app/canonical_json.py 是 D3/D5/D7 與 D4 共用的唯一事實來源。

稽核發現：兩邊過去各自實作 canonical JSON，一邊用 Python code point 排序，
一邊對齊 .NET StringComparer.Ordinal 的 UTF-16 code-unit 排序，非 BMP key 上
兩者排序相反，導致同一份定義在兩個雜湊契約下算出不同雜湊。這裡用逐鍵比對的
parity 測試背書：統一後兩邊必須對同一份輸入算出相同雜湊。
"""

from __future__ import annotations

import json
from decimal import Decimal

import pytest

from app.canonical_json import (
    RawNumberToken,
    canonical_json_bytes,
    canonical_json_sha256,
)
from app.orchestration.canonical import canonical_json, sha256 as d4_sha256
from app.runtime.models import canonical_json_sha256 as d3_sha256

# 稽核給出的分岔樣本：U+FFFD 是單一 UTF-16 code unit（0xFFFD），
# U+10000 以代理對（0xD800 0xDC00）編碼；code point 排序把 U+FFFD 排前面，
# 但 UTF-16 code-unit 排序因為 0xD800 < 0xFFFD 而把代理對開頭的 key 排前面。
_DIVERGENT_SAMPLE = {"�": 1, "\U00010000": 2}


def test_codepoint_order_diverges_from_utf16_ordinal_order_on_non_bmp_keys() -> None:
    # 反面驗證：確認樣本真的會在兩種排序下給出相反順序，不然這個回歸測試沒有意義。
    assert sorted(_DIVERGENT_SAMPLE) == ["�", "\U00010000"]
    canonical = json.loads(canonical_json_bytes(_DIVERGENT_SAMPLE))
    assert list(canonical.keys()) == ["\U00010000", "�"]


def test_d4_and_d3_canonical_hashes_agree_on_non_bmp_keys() -> None:
    # 核心正確性斷言：D4（orchestration.canonical）與 D3/D5/D7
    # （runtime.models）改用同一份 UTF-16 ordinal 實作後，同一份輸入必須算出
    # 一致的雜湊——這正是稽核指出會分岔的跨服務雜湊契約。
    assert d4_sha256(_DIVERGENT_SAMPLE) == d3_sha256(_DIVERGENT_SAMPLE)
    assert d4_sha256(_DIVERGENT_SAMPLE) == canonical_json_sha256(_DIVERGENT_SAMPLE)


def test_canonical_json_still_normalises_ascii_object_member_order() -> None:
    # canonical_definition() 依賴 canonical_json() 回傳「已排序好的 Python 物件」;
    # 換底層實作後，一般 ASCII key 的排序行為（既有主要使用情境）必須不變。
    result = canonical_json({"b": 1, "a": 2})
    assert list(result.keys()) == ["a", "b"]


def test_non_finite_numbers_are_rejected() -> None:
    # 跨服務雜湊契約不能有「無法被 .NET 端還原」的值：NaN/Infinity 不是合法
    # JSON number，寫入器必須明確拒絕（float 與 Decimal 兩條分支都要）。
    for bad in (float("nan"), float("inf"), float("-inf")):
        with pytest.raises(ValueError, match="must be finite"):
            canonical_json_bytes({"x": bad})
    for bad_decimal in (Decimal("NaN"), Decimal("Infinity"), Decimal("-Infinity")):
        with pytest.raises(ValueError, match="must be finite"):
            canonical_json_bytes({"x": bad_decimal})


def test_non_string_keys_and_unsupported_types_are_rejected() -> None:
    # 白名單、預設拒絕：非字串 key 與未支援型別若被靜默轉型，兩端會算出不同
    # 雜湊，所以兩者都必須是 TypeError 而不是「盡力序列化」。
    with pytest.raises(TypeError, match="object keys must be strings"):
        canonical_json_bytes({1: "a"})
    with pytest.raises(TypeError, match="unsupported canonical JSON value: set"):
        canonical_json_bytes({"x": {"s"}})
    with pytest.raises(TypeError, match="unsupported canonical JSON value: object"):
        canonical_json_bytes({"x": object()})


def test_scalar_and_collection_branches_render_expected_json_literals() -> None:
    # 釘住每個寫入分支的字面輸出：RawNumberToken 是為了保留「已驗證的數字詞法」
    # 而存在（1.50 不可被重新序列化成 1.5），它排在 str 分支之前所以不加引號；
    # 字串用 ensure_ascii=False 直接輸出非 ASCII；tuple 與 list 同樣輸出成陣列。
    document = {
        "a_null": None,
        "b_true": True,
        "c_false": False,
        "d_str": "中",
        "e_list": [1, ("x",)],
        "f_raw": RawNumberToken("1.50"),
    }
    assert canonical_json_bytes(document) == (
        '{"a_null":null,"b_true":true,"c_false":false,'
        '"d_str":"中","e_list":[1,["x"]],"f_raw":1.50}'
    ).encode()


def test_empty_container_boundary_renders_without_separators() -> None:
    # 邊界值：0 個元素時 sorted()/join() 不得產生多餘的逗號或空白，
    # 否則空物件在兩端會算出不同雜湊。
    assert canonical_json_bytes({}) == b"{}"
    assert canonical_json_bytes([]) == b"[]"
