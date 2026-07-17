from typing import Any

from pydantic import BaseModel

from app.engine.skill import InputField


class InvokeRequest(BaseModel):
    """呼叫 skill 的請求本體：input 會（移除保留鍵後）當成圖的初始 state 傳入。"""

    input: dict[str, Any]


class SkillInfo(BaseModel):
    """Skill 清單項目：供 GET /skills 回傳。source 為 "builtin"（repo 檔案）或 "custom"（來自 backend）。"""

    name: str
    description: str
    required_role: str
    source: str
    revision: int
    input_schema: dict[str, InputField] | None = None
    # 內建 template_* 骨架的 YAML 原文,供前端 compose 定點 patch;custom 項不帶（None）。
    definition: str | None = None


class SkillValidateRequest(BaseModel):
    """POST /skills/validate 的請求本體：definition 是 skill 定義的 YAML 原文。

    刻意收 YAML 原文而不是已解析的物件：解析失敗本身就是要回報的錯誤之一
    （invalid_flow），前端編輯器送出的也正是編輯中的原文。
    """

    definition: str


class SkillInvokeResponse(BaseModel):
    """呼叫 skill 的回應本體：output 為圖執行結束後的最終 state（已剝除引擎內部鍵）。"""

    skill: str
    output: dict[str, Any]


class NodeInfo(BaseModel):
    """節點目錄項目：供 GET /nodes 回傳節點的 I/O 契約（Skill 編輯器據此組流程）。"""

    name: str
    version: str
    description: str
    reads: list[str]
    writes: list[str]
    requires_tools: list[str]
