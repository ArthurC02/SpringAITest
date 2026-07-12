import asyncio

from fastapi import Depends, FastAPI, HTTPException
from pydantic import ValidationError

from app import tracing
from app.schemas import InvokeRequest, InvokeResponse, WorkflowInfo
from app.security import RequestContext, get_context
from app.settings import settings

# 這行 import 除了取得 registry 模組本身，也會連帶執行
# app/workflows/__init__.py 內各工作流模組的 @register 裝飾器，
# 讓 registry 在應用程式啟動時就已經填好所有工作流。
from app.workflows import registry

# 這些鍵一律由伺服器依 RequestContext 自動注入 state，不允許呼叫端經 input 蓋掉，
# 避免有心（或不小心）的呼叫端夾帶假的租戶／使用者資訊，破壞多租戶隔離邊界。
_RESERVED_INPUT_KEYS = {"tenant_id", "user_id", "role"}


app = FastAPI(title="springaitest-workflow")


@app.get("/health")
async def health() -> dict:
    """健康檢查端點，供 compose / Spring 端探活使用；刻意不掛任何驗證，避免探活受認證設定影響。"""
    return {"status": "ok"}


@app.get("/workflows", response_model=list[WorkflowInfo])
async def list_workflows(ctx: RequestContext = Depends(get_context)) -> list[WorkflowInfo]:
    """列出目前已註冊的所有工作流（含各自的最低角色需求）。"""
    return [
        WorkflowInfo(
            name=spec.name,
            description=spec.description,
            required_role=spec.required_role,
        )
        for spec in registry.all_specs()
    ]


@app.post("/workflows/{name}/invoke", response_model=InvokeResponse)
async def invoke_workflow(
    name: str,
    req: InvokeRequest,
    ctx: RequestContext = Depends(get_context),
) -> InvokeResponse:
    """觸發指定工作流，同步執行到底並回傳最終 state。

    驗證順序：工作流是否存在（404）→ 呼叫者角色是否足夠（403）→
    input 是否符合該工作流宣告的 schema（422）→ 執行圖並套用逾時保護（504／500）。
    """
    spec = registry.get(name)
    if spec is None:
        raise HTTPException(
            status_code=404,
            detail={
                "error": "workflow_not_found",
                "message": f"unknown workflow: {name}",
                "workflows": [s.name for s in registry.all_specs()],
            },
        )

    if spec.required_role == "ADMIN" and ctx.role != "ADMIN":
        raise HTTPException(
            status_code=403,
            detail={
                "error": "workflow_forbidden",
                "message": f"workflow '{name}' 需要 {spec.required_role} 角色權限",
            },
        )

    if spec.input_model is not None:
        try:
            spec.input_model.model_validate(req.input)
        except ValidationError as e:
            raise HTTPException(
                status_code=422,
                detail={
                    "error": "workflow_input_invalid",
                    "message": str(e),
                },
            )

    cleaned_input = {k: v for k, v in req.input.items() if k not in _RESERVED_INPUT_KEYS}
    state = {"tenant_id": ctx.tenant_id, **cleaned_input}
    timeout_seconds = spec.timeout_seconds or settings.workflow_timeout_seconds

    try:
        async with asyncio.timeout(timeout_seconds):
            output = await spec.graph.ainvoke(state, config=tracing.runnable_config())
    except TimeoutError:
        raise HTTPException(
            status_code=504,
            detail={
                "error": "workflow_timeout",
                "message": f"workflow '{name}' 執行超過 {timeout_seconds} 秒",
            },
        )
    except Exception as e:
        raise HTTPException(
            status_code=500,
            detail={
                "error": "workflow_execution_failed",
                "message": str(e),
            },
        )

    return InvokeResponse(workflow=name, output=output)
