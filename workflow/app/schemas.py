from datetime import datetime
from typing import Any

from pydantic import BaseModel, Field


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


class DocumentCreate(BaseModel):
    """新增文件的請求本體：title 與 text 皆須為非空字串。"""

    title: str = Field(min_length=1)
    text: str = Field(min_length=1)


class DocumentCreated(BaseModel):
    """新增文件成功後的回應本體。"""

    id: str
    title: str
    chunk_count: int


class DocumentInfo(BaseModel):
    """文件清單項目：供 GET /documents 回傳，僅含中繼資料、不含片段內容。"""

    id: str
    title: str
    chunk_count: int
    created_at: datetime
