"""跨服務 canonical JSON 的唯一事實來源。

D3/D5/D7（`app/runtime/`）與 D4（`app/orchestration/`）過去各自實作了一份
canonical JSON：前者刻意手刻 UTF-16 code-unit 排序以對齊 .NET
`StringComparer.Ordinal`（Backend 會驗證 canonical SHA-256），後者用
`json.dumps(sort_keys=True)`（Python code point 排序）。兩者對非 BMP key
（例如代理對、專用區字元）排序結果會分岔，導致同一份定義在兩個雜湊契約下
算出不同雜湊。這裡統一成 UTF-16 ordinal 版本，兩邊都改為呼叫這裡。
"""

from __future__ import annotations

import hashlib
import json
import math
from decimal import Decimal
from typing import Any


class RawNumberToken(str):
    """A syntactically validated JSON number lexeme.

    與 canonical JSON 寫入器同屬一組：寫入器需要辨識「已驗證、要原樣輸出的
    數字詞法」，因此這個標記型別和 writer 放在同一模組，避免兩邊互相 import
    造成循環依賴。
    """


def _utf16_ordinal_key(value: str) -> bytes:
    # Big-endian bytes preserve lexicographic UTF-16 code-unit ordering.
    return value.encode("utf-16-be", errors="surrogatepass")


def _write_canonical_json(value: Any) -> str:
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, RawNumberToken):
        return str(value)
    if isinstance(value, int):
        return str(value)
    if isinstance(value, Decimal):
        if not value.is_finite():
            raise ValueError("canonical JSON numbers must be finite")
        return str(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("canonical JSON numbers must be finite")
        return json.dumps(value, allow_nan=False)
    if isinstance(value, str):
        return json.dumps(value, ensure_ascii=False)
    if isinstance(value, dict):
        if any(not isinstance(key, str) for key in value):
            raise TypeError("canonical JSON object keys must be strings")
        return "{" + ",".join(
            f"{json.dumps(key, ensure_ascii=False)}:{_write_canonical_json(value[key])}"
            for key in sorted(value, key=_utf16_ordinal_key)
        ) + "}"
    if isinstance(value, list | tuple):
        return "[" + ",".join(_write_canonical_json(item) for item in value) + "]"
    raise TypeError(f"unsupported canonical JSON value: {type(value).__name__}")


def canonical_json_bytes(value: Any) -> bytes:
    """Cross-service canonical JSON using .NET StringComparer.Ordinal keys."""
    return _write_canonical_json(value).encode("utf-8")


def canonical_json_sha256(value: Any) -> str:
    return hashlib.sha256(canonical_json_bytes(value)).hexdigest()
