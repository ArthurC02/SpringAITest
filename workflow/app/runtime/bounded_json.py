"""共用的「序列化為 JSON 字串並截斷字元上限」邏輯。

`flow_harness` 與 `tool_boundary` 原有的 JSON 邊界處理逐行相同：
都是 `json.dumps(..., ensure_ascii=False, allow_nan=False, sort_keys=True,
separators=(",", ":"), default=str)`，失敗轉錯誤、成功則截斷。差異只在序列化
失敗時的回退策略（前者直接拒絕、後者退回一個佔位結果）與截斷後綴，因此用參數
表達差異，讓兩處呼叫端各自保留原本的失敗語意。
"""

from __future__ import annotations

import json
from typing import Any, Callable


def bounded_canonical_json(
    value: Any,
    *,
    max_chars: int,
    on_unserializable: Callable[[TypeError | ValueError], str],
    truncated_suffix: str = "",
) -> str:
    """序列化並截斷到 `max_chars`；後綴只在真的發生截斷時附加。"""
    try:
        text = json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            sort_keys=True,
            separators=(",", ":"),
            default=str,
        )
    except (TypeError, ValueError) as exc:
        text = on_unserializable(exc)
    if len(text) > max_chars:
        return text[:max_chars] + truncated_suffix
    return text
