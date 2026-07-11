from dataclasses import dataclass
from typing import Callable

from langgraph.graph.state import CompiledStateGraph
from pydantic import BaseModel


@dataclass(frozen=True)
class WorkflowSpec:
    """已註冊工作流的完整規格：名稱、說明文字、編譯完成可執行的圖，
    以及權限邊界（required_role）、輸入驗證（input_model）、逾時秒數（timeout_seconds）。
    """

    name: str
    description: str
    graph: CompiledStateGraph
    required_role: str = "USER"
    input_model: type[BaseModel] | None = None
    timeout_seconds: int | None = None


_REGISTRY: dict[str, WorkflowSpec] = {}


def register(
    name: str,
    description: str = "",
    required_role: str = "USER",
    input_model: type[BaseModel] | None = None,
    timeout_seconds: int | None = None,
):
    """裝飾器：包住工作流的 build() 函式，呼叫後立即編譯並登記到全域註冊表。

    新增工作流時只需在對應模組寫上 `@register("xxx")` 裝飾 build 函式，
    再到 app/workflows/__init__.py import 該模組即可自動生效。

    - required_role："USER"（預設，任何角色皆可呼叫）或 "ADMIN"（僅限管理員）。
    - input_model：若提供，POST /workflows/{name}/invoke 會先用它驗證 input，
      驗證失敗回傳 422 workflow_input_invalid；不提供則維持寬鬆、不做結構驗證。
    - timeout_seconds：覆蓋全域預設的 settings.workflow_timeout_seconds。
    """

    def decorator(build: Callable[[], CompiledStateGraph]):
        if name in _REGISTRY:
            raise ValueError(f"duplicate workflow name: {name}")
        _REGISTRY[name] = WorkflowSpec(
            name,
            description,
            build(),
            required_role,
            input_model,
            timeout_seconds,
        )
        return build

    return decorator


def get(name: str) -> WorkflowSpec | None:
    """依名稱查詢已註冊的工作流，查無則回傳 None。"""
    return _REGISTRY.get(name)


def all_specs() -> list[WorkflowSpec]:
    """取得所有已註冊工作流，依名稱排序以維持穩定輸出順序。"""
    return sorted(_REGISTRY.values(), key=lambda s: s.name)
