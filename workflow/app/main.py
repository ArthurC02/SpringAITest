import asyncio

from fastapi import Depends, FastAPI, HTTPException
from pydantic import ValidationError

from app import skills, tracing
from app.engine import compiler, node_registry
from app.engine.skill import RESERVED_KEYS as skill_reserved_keys
from app.engine.skill import ValidationResult, schema_from_model, validate_source
from app.skills import custom

# 這兩行 import 執行各節點模組頂層的 @node 裝飾器，讓 GET /nodes 的目錄完整
# （不倚賴「某個工作流剛好有 import 到該節點」這種間接關係）。同理，app.tools 的 import
# 執行 @tool 裝飾器，讓 Skill 的 tool/script 步驟查得到 Tool Registry。
from app import tools as _tools  # noqa: F401
from app.kbquery import nodes as _kbquery_nodes  # noqa: F401
from app.nodes import retrieve as _retrieve_node  # noqa: F401
from app.schemas import (
    InvokeRequest,
    InvokeResponse,
    NodeInfo,
    SkillInfo,
    SkillInvokeResponse,
    SkillValidateRequest,
    WorkflowInfo,
)
from app.security import RequestContext, get_context
from app.settings import settings

# 這行 import 除了取得 registry 模組本身，也會連帶執行
# app/workflows/__init__.py 內各工作流模組的 @register 裝飾器，
# 讓 registry 在應用程式啟動時就已經填好所有工作流。
from app.workflows import registry

# 這些鍵一律由伺服器依 RequestContext 自動注入 state，不允許呼叫端經 input 蓋掉，
# 避免有心（或不小心）的呼叫端夾帶假的租戶／使用者資訊，破壞多租戶隔離邊界。
_RESERVED_INPUT_KEYS = {"tenant_id", "user_id", "role"}


def _clean_skill_input(raw: dict) -> dict:
    """skill invoke 的 input 過濾：比 /workflows 嚴格，因為 skill 的 flow 是任意的。

    /workflows 只需剝三個身分鍵，是因為手寫圖的第一個節點固定是 query_intake，
    query_id / original_query / query_timestamp 必被無條件覆寫。Skill 沒有這個保證
    （flow 由作者決定，query_intake 不是強制附加的節點），呼叫端夾帶 query_id 進來
    就會原封不動流進 audit_trail —— 一般 USER 即可偽造稽核軌跡上的「原始問題」。
    Harness 的 IMMUTABLE_KEYS 只擋節點寫入，擋不住 input，所以這一關必須在這裡做。
    引擎內部鍵（__ 前綴）同理一併剝除。
    """
    return {
        k: v
        for k, v in raw.items()
        if k not in skill_reserved_keys and not k.startswith("__")
    }


app = FastAPI(title="springaitest-workflow")


@app.get("/health")
async def health() -> dict:
    """健康檢查端點，供 compose / Spring 端探活使用；刻意不掛任何驗證，避免探活受認證設定影響。"""
    return {"status": "ok"}


@app.get("/workflows", response_model=list[WorkflowInfo])
async def list_workflows(ctx: RequestContext = Depends(get_context)) -> list[WorkflowInfo]:
    """列出目前已註冊的所有工作流（含各自的最低角色需求與輸入 schema）。"""
    return [
        WorkflowInfo(
            name=spec.name,
            description=spec.description,
            required_role=spec.required_role,
            input_schema=schema_from_model(spec.input_model),
        )
        for spec in registry.all_specs()
    ]


@app.get("/nodes", response_model=list[NodeInfo])
async def list_nodes(ctx: RequestContext = Depends(get_context)) -> list[NodeInfo]:
    """節點目錄：列出所有已註冊節點與其 I/O 契約（程式即事實來源，不入 DB）。"""
    return [
        NodeInfo(
            name=spec.name,
            version=spec.version,
            description=spec.description,
            reads=list(spec.reads),
            writes=list(spec.writes),
            requires_tools=list(spec.requires_tools),
        )
        for spec in node_registry.all_specs()
    ]


@app.get("/skills", response_model=list[SkillInfo])
async def list_skills(ctx: RequestContext = Depends(get_context)) -> list[SkillInfo]:
    """Skill 清單：內建（repo 的 skills/*.yaml）+ 本租戶自訂（來自 backend），以 source 區分。

    backend 不可達時只讓自訂清單缺席（custom.catalog 記 log 回空），內建 skill 照列 ——
    這是清單端點不是交易端點，下游故障不該讓整份目錄消失。
    """
    builtin = [
        SkillInfo(
            name=loaded.skill.name,
            description=loaded.skill.description,
            required_role=loaded.skill.required_role,
            source=loaded.source,
            revision=loaded.skill.revision,
            input_schema=loaded.skill.input_schema or None,
        )
        for loaded in skills.all_skills()
    ]
    return builtin + [SkillInfo(**entry) for entry in await custom.catalog(ctx)]


@app.post("/skills/validate", response_model=ValidationResult, response_model_exclude_none=True)
async def validate_skill(
    req: SkillValidateRequest,
    ctx: RequestContext = Depends(get_context),
) -> ValidationResult:
    """對一份 skill 定義原文跑全部靜態驗證。無副作用：不編譯、不寫入、不改變 GET /skills。

    永遠回 200 + {valid, errors[]}（含 YAML 解析失敗），錯誤在 body 裡 ——
    前端編輯器要的是可逐條標紅的錯誤清單，不是一個 HTTP 錯誤碼。

    valid=true 時多回一段 skill 中繼資料（name/description/required_role/input_schema）：
    backend 不裝 YAML parser，這是它寫入 DB 的唯一資料來源。exclude_none 保證
    valid=false 時**不出現** skill 欄位 —— backend 以它的有無決定能不能寫。
    """
    return validate_source(req.definition)


@app.post("/skills/{name}/invoke", response_model=SkillInvokeResponse)
async def invoke_skill(
    name: str,
    req: InvokeRequest,
    ctx: RequestContext = Depends(get_context),
) -> SkillInvokeResponse:
    """執行指定 skill（內建或本租戶自訂）。驗證順序與錯誤碼一律沿用 /workflows/{name}/invoke：
    存在（404）→ 角色（403）→ input schema（422）→ 逾時（504）／未預期例外（500）。

    名稱先查內建、再向 backend 查自訂。backend 不可達 → 500（受控）而不是 404：
    「取不到定義」與「skill 不存在」是兩件事，混為一談會讓呼叫端刪錯東西。
    """
    loaded = skills.get(name)
    if loaded is None:
        try:
            loaded = await custom.load(name, ctx)
        except (custom.BackendUnavailable, custom.InvalidCustomSkill) as e:
            raise HTTPException(
                status_code=500,
                detail={"error": "workflow_execution_failed", "message": str(e)},
            )

    if loaded is None:
        raise HTTPException(
            status_code=404,
            detail={
                "error": "workflow_not_found",
                "message": f"unknown skill: {name}",
                "skills": [s.skill.name for s in skills.all_skills()],
            },
        )

    if loaded.skill.required_role == "ADMIN" and ctx.role != "ADMIN":
        raise HTTPException(
            status_code=403,
            detail={
                "error": "workflow_forbidden",
                "message": f"skill '{name}' 需要 {loaded.skill.required_role} 角色權限",
            },
        )

    if loaded.input_model is not None:
        try:
            loaded.input_model.model_validate(req.input)
        except ValidationError as e:
            raise HTTPException(
                status_code=422,
                detail={
                    "error": "workflow_input_invalid",
                    "message": str(e),
                },
            )

    state = {"tenant_id": ctx.tenant_id, **_clean_skill_input(req.input)}
    timeout_seconds = loaded.skill.timeout_seconds or settings.workflow_timeout_seconds
    # recursion_limit：規格 §6.3-2 的全圖護欄（langgraph 預設 10007 形同沒有護欄）
    config = {**tracing.runnable_config(), "recursion_limit": loaded.recursion_limit}

    try:
        async with asyncio.timeout(timeout_seconds):
            output = await loaded.graph.ainvoke(state, config=config)
    except TimeoutError:
        raise HTTPException(
            status_code=504,
            detail={
                "error": "workflow_timeout",
                "message": f"skill '{name}' 執行超過 {timeout_seconds} 秒",
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

    return SkillInvokeResponse(skill=name, output=compiler.public_output(output))


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
