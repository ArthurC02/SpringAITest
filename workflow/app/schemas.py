from typing import Any

from pydantic import BaseModel


class InvokeRequest(BaseModel):
    """呼叫工作流的請求本體：input 會（移除保留鍵後）當成圖的初始 state 傳入。"""

    input: dict[str, Any]


class InvokeResponse(BaseModel):
    """呼叫工作流的回應本體：output 為圖執行結束後的最終 state。"""

    workflow: str
    output: dict[str, Any]


class WorkflowInfo(BaseModel):
    """工作流清單項目：供 GET /workflows 回傳。"""

    name: str
    description: str
    required_role: str
