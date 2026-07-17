"""純函式文字工具：期間 token 與數值/單位的擷取與正規化，無外部依賴。"""

import re

# 期間 token：2025Q3（Q 前可有空白）、YT09、2025年
PERIOD_RE = re.compile(r"20\d{2}\s*[Qq][1-4]|YT\d{2}|20\d{2}年")

# 數值：可帶正負號與千分位逗號、小數
NUMBER_RE = re.compile(r"[-+]?\d[\d,]*(?:\.\d+)?")

# 單位：長的排前面，避免「百萬元」只吃到「百萬」；允許數字與單位間有空白（如「1,234 百萬元」）
_UNIT_RE = re.compile(r"\s*(百萬元|億元|萬元|百萬|億|元|%|bps)")

_BARE_QUARTER_RE = re.compile(r"[Qq][1-4]")


def normalize_period(p: str) -> str:
    """正規化期間 token：去空白、轉大寫、`XXXX年`→`XXXX`。"""
    return "".join(p.split()).upper().removesuffix("年")


def find_periods(text: str) -> list[str]:
    """回傳文字中所有正規化後的期間 token，去重保序。"""
    seen: dict[str, None] = {}
    for m in PERIOD_RE.findall(text):
        seen.setdefault(normalize_period(m), None)
    return list(seen)


def has_bare_quarter(text: str) -> bool:
    """文字含季別（Q1..Q4）但找不到任何完整期間 → True（只有季別沒有年度）。"""
    return bool(_BARE_QUARTER_RE.search(text)) and not find_periods(text)


def parse_number(s: str) -> float:
    """去千分位逗號後轉 float，失敗 raise ValueError。"""
    return float(s.replace(",", ""))


def extract_value_unit(text: str) -> list[tuple[str, str]]:
    """找出文字中所有 (數字字串, 單位) 配對；單位緊跟數字，無單位回空字串。

    期間 token（2025Q3、YT09、2025年）不得誤判為數值：先以等長空白遮掉
    PERIOD_RE 匹配片段（保持索引不變）再找數字。
    """
    masked = PERIOD_RE.sub(lambda m: " " * len(m.group()), text)
    pairs: list[tuple[str, str]] = []
    for m in NUMBER_RE.finditer(masked):
        unit_match = _UNIT_RE.match(masked, m.end())
        pairs.append((m.group(), unit_match.group(1) if unit_match else ""))
    return pairs
