"""純函式：把長文字切成適合嵌入／檢索的片段，不依賴任何第三方套件。"""

import re

_PARAGRAPH_SPLIT = re.compile(r"\n\s*\n")


def split_text(text: str, max_chars: int = 800, overlap: int = 100) -> list[str]:
    """把輸入文字切成片段列表。

    策略分兩層：
    1. 先以空白行為界切成段落，盡量保留原文的語意邊界（優先於機械式硬切）。
    2. 段落若仍超過 max_chars，再以滑動視窗（重疊 overlap 字）切成多塊，
       重疊部分可避免關鍵語句被剛好切在區塊邊界上而遺失上下文。

    回傳的片段都會先去除頭尾空白，並濾掉切完後仍是空字串的片段；
    輸入整體為空白時回傳空列表。
    """
    if not text or not text.strip():
        return []

    paragraphs = [p.strip() for p in _PARAGRAPH_SPLIT.split(text) if p.strip()]
    if not paragraphs:
        return []

    # overlap 若大於等於 max_chars，滑動視窗會原地打轉；至少前進 1 字元避免無窮迴圈。
    step = max(max_chars - overlap, 1)

    chunks: list[str] = []
    for paragraph in paragraphs:
        if len(paragraph) <= max_chars:
            chunks.append(paragraph)
            continue

        start = 0
        while start < len(paragraph):
            end = start + max_chars
            piece = paragraph[start:end].strip()
            if piece:
                chunks.append(piece)
            if end >= len(paragraph):
                break
            start += step

    return chunks
