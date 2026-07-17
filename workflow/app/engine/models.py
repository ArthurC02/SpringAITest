"""引擎核心的共用 trace 型別：所有節點執行紀錄的共同基底。"""

from pydantic import BaseModel


class TraceEntry(BaseModel):
    """單一節點的執行紀錄；input/output summary 只放欄位鍵名摘要，不放原文內容。"""

    node_name: str
    start_time: str
    end_time: str
    latency_ms: float
    status: str  # "ok" | "error" | "skipped"
    input_summary: str = ""
    output_summary: str = ""
    error_code: str = ""
    component_version: str = ""  # model / tool / index 版本（可取得時）
