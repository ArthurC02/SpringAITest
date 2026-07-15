"""節點統一包裝器（保留路徑）：實作已泛化進 app/engine/harness.py。

這裡只留別名，讓 `from app.kbquery.runtime import traced` 的既有呼叫端與測試照常運作；
新程式請直接用 app.engine.harness.harnessed（多一個 writes 契約參數）。
"""

from app.engine.harness import IMMUTABLE_KEYS, harnessed as traced

__all__ = ["IMMUTABLE_KEYS", "traced"]
