import asyncio
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, File, Form, HTTPException, UploadFile
from pydantic import ValidationError

from app import backend_http, skills, tracing
from app.engine import compiler, node_registry, package
from app.engine.skill import RESERVED_KEYS as skill_reserved_keys
from app.engine.skill import ValidationResult, validate_source
from app.skills import config_apply, custom

# 這幾行 import 執行各節點模組頂層的 @node 裝飾器，讓 GET /nodes 的目錄完整
# （不倚賴「某個 skill 剛好有 import 到該節點」這種間接關係）。同理，app.tools 的 import
# 執行 @tool 裝飾器，讓 Skill 的 tool/script 步驟查得到 Tool Registry。nl_logic 走
# app.nodes、非 kb_query 族，故這裡明列一行讓 catalog 查得到它（縫⑥）。
# 冷啟動「編譯」template_* 所需的 nl_logic / retrieve 註冊由 app.skills 自身負責觸發
# （見 skills/__init__.py），故 app.skills 單獨匯入亦自足。
from app import tools as _tools  # noqa: F401
from app.nodes import agent_skill_runner as _agent_skill_runner  # noqa: F401
from app.nodes.agent_skill_runner import DEFAULT_AGENT_TIMEOUT_S
from app.nodes import analyze_report as _analyze_report_nodes  # noqa: F401
from app.nodes import nl_extract as _nl_extract  # noqa: F401
from app.nodes import nl_logic as _nl_logic  # noqa: F401
from app.nodes import rag_answer as _rag_answer  # noqa: F401
from app.nodes import retrieve as _retrieve_node  # noqa: F401
from app.nodes import summarize_text as _summarize_text  # noqa: F401
from app.nodes import triage as _triage_nodes  # noqa: F401
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from app.schemas import (
    InvokeRequest,
    NodeInfo,
    PackageManifest,
    PackageSkillMeta,
    SkillInfo,
    SkillInvokeResponse,
    SkillValidateRequest,
    ValidatePackageResult,
)
from app.security import RequestContext, get_context


def _clean_skill_input(raw: dict) -> dict:
    """skill invoke 的 input 過濾：剝除保留鍵、引擎鍵與引擎內部鍵。

    query_id / original_query / query_timestamp 等保留鍵若被呼叫端夾帶，會原封不動
    流進 audit_trail —— 一般 USER 即可偽造稽核軌跡上的「原始問題」。Harness 的
    IMMUTABLE_KEYS 只擋節點寫入，擋不住 input，所以這一關必須在這裡做。
    引擎鍵（trace / errors / fatal_error，由 Harness 寫入）同樣不可經 input 夾帶：
    夾帶 fatal_error 會讓所有節點走 fatal 短路而跳過，夾帶 trace/errors 更會讓 reducer
    型別不符而 500。引擎內部鍵（__ 前綴）同理一併剝除。
    """
    return {
        k: v
        for k, v in raw.items()
        if k not in skill_reserved_keys
        and k not in node_registry.ENGINE_KEYS
        and not k.startswith("__")
    }


def _require_role(required_role: str | None, ctx: RequestContext, name: str) -> None:
    """403 角色檢查。"""
    if required_role == "ADMIN" and ctx.role != "ADMIN":
        raise HTTPException(
            status_code=403,
            detail={
                "error": "workflow_forbidden",
                "message": f"skill '{name}' 需要 {required_role} 角色權限",
            },
        )


def _humanize_input_error(err: dict) -> str:
    """把單條 pydantic 錯誤翻成使用者看得懂的中文;絕不外洩 model 名／pydantic.dev URL／
    python 型別字樣。未知 type 給通用「格式不正確」。"""
    loc = err.get("loc") or ()
    field = str(loc[0]) if loc else "輸入"
    etype = err.get("type", "")
    ctx = err.get("ctx") or {}
    if etype == "missing":
        return f"「{field}」為必填。"
    if etype == "string_too_short":
        return f"「{field}」至少需 {ctx.get('min_length')} 個字。"
    if etype in ("int_parsing", "int_type"):
        return f"「{field}」必須是整數。"
    if etype in ("float_parsing", "float_type"):
        return f"「{field}」必須是數字。"
    if etype in ("bool_parsing", "bool_type"):
        return f"「{field}」必須是是/否。"
    if etype == "list_type":
        return f"「{field}」必須是清單（JSON 陣列）。"
    if etype == "dict_type":
        return f"「{field}」必須是 JSON 物件。"
    return f"「{field}」格式不正確。"


def _validate_input(model, raw: dict, name: str) -> None:
    """422 input schema 檢查。model 為 None 時不驗證。

    422 body 為人話化的 field_errors（snake_case 欄位鍵；platform 端會映成 camelCase
    fieldErrors），不再回吐 pydantic 原始多行英文 dump。
    """
    if model is None:
        return
    try:
        model.model_validate(raw)
    except ValidationError as e:
        errs = e.errors()
        field_errors: dict[str, str] = {}
        for err in errs:
            loc = err.get("loc") or ()
            field = str(loc[0]) if loc else "輸入"
            field_errors[field] = _humanize_input_error(err)
        raise HTTPException(
            status_code=422,
            detail={
                "error": "workflow_input_invalid",
                "message": f"輸入資料有 {len(errs)} 個欄位需要修正",
                "field_errors": field_errors,
            },
        )


# agentic runner 的 node-level timeout（skill.timeout_seconds or DEFAULT_AGENT_TIMEOUT_S）必須
# 穩定先於 invoke-level 逾時觸發，才能走 Harness fatal → audit 的稽核保證（設計 §4.3-5）。兩者
# 用同一個 skill.timeout_seconds 時會撞在一起、外層因早幾 ms 起跑而搶先取消 runner、稽核被跳過。
# 故 agentic 的 outer deadline = inner + GRACE：inner 必先 fire 產生 fatal + 稽核，outer 僅在 node
# 自身取消失效時當純 backstop。flow skill 的逾時行為完全不變。
# ponytail: 一個小固定餘裕，語意即「讓 node 逾時穩定先跑」；夠大到蓋過起跑時序差即可
AGENT_INVOKE_TIMEOUT_GRACE_S = 5.0


async def _run_with_timeout(coro, timeout_seconds: float, name: str):
    """套用逾時保護並執行圖，504／500 錯誤處理。"""
    try:
        async with asyncio.timeout(timeout_seconds):
            return await coro
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


@asynccontextmanager
async def lifespan(app: FastAPI):
    """服務生命週期：暖身並在關機時釋放共用 backend HTTP client（連線池不外洩）。"""
    backend_http.get_client()  # 暖身：啟動時就備好連線池，首個請求不必臨時建立
    try:
        yield
    finally:
        await backend_http.aclose_client()


app = FastAPI(title="springaitest-workflow", lifespan=lifespan)


@app.get("/health")
async def health() -> dict:
    """健康檢查端點，供 compose / platform 端（.NET）探活使用；刻意不掛任何驗證，避免探活受認證設定影響。"""
    return {"status": "ok"}


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
            kind=loaded.skill.kind,
            input_schema=loaded.skill.input_schema or None,
            # 內建骨架帶原文供前端 compose patch;custom 不帶（catalog dict 無此鍵 → None）
            definition=loaded.definition or None,
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

    撰寫者角色 gate（保守版）：帶入呼叫者真實角色（backend 的 WorkflowSkillValidator 與
    platform proxy 都會轉發真實作者身分），非 ADMIN 提交含 script 步驟的定義即驗證失敗。
    僅在此寫入路徑生效；不改 required_role 語意，也不影響 invoke／custom.load。
    """
    return validate_source(req.definition, author_role=ctx.role)


@app.post(
    "/skills/validate-package",
    response_model=ValidatePackageResult,
    response_model_exclude_none=True,
)
async def validate_package(
    package_file: UploadFile = File(alias="package"),
    expected_name: str | None = Form(default=None),
    ctx: RequestContext = Depends(get_context),
) -> ValidatePackageResult:
    """驗證上傳的 Skill package zip（設計 §2.2）。internal-only，multipart。

    無副作用：只解析與驗證，不編譯、不寫入、不執行 package 內的 script（規格 R8）。
    valid=true 時回既有 validation 回應的 superset（多 kind、canonical_definition、
    package_manifest）；valid=false 時 exclude_none 保證不出現任何可被寫入的 metadata
    /definition —— backend 以「有沒有 skill/canonical_definition」決定能不能寫。

    Workflow 是 SKILL.md 的唯一 parser 與語意 validator（規格 R3）；backend 不另做一套。
    """
    raw = await package_file.read()
    try:
        parsed = package.parse_package(raw, expected_name)
    except package.PackageError as e:
        return ValidatePackageResult(valid=False, errors=list(e.errors))

    return ValidatePackageResult(
        valid=True,
        errors=[],
        skill=PackageSkillMeta(
            name=parsed.skill.name,
            description=parsed.skill.description,
            required_role=parsed.skill.required_role,
            input_schema=parsed.skill.input_schema,
            kind=parsed.kind,
        ),
        canonical_definition=parsed.canonical_definition,
        package_manifest=PackageManifest(
            entries=list(parsed.entries), sha256=parsed.sha256
        ),
    )


@app.post("/skills/{name}/invoke", response_model=SkillInvokeResponse)
async def invoke_skill(
    name: str,
    req: InvokeRequest,
    ctx: RequestContext = Depends(get_context),
) -> SkillInvokeResponse:
    """執行指定 skill（內建或本租戶自訂）。驗證順序：
    存在（404）→ 角色（403）→ input schema（422）→ 逾時（504）／未預期例外（500）。

    名稱先查內建、再向 backend 查自訂。backend 不可達 → 500（受控）而不是 404：
    「取不到定義」與「skill 不存在」是兩件事，混為一談會讓呼叫端刪錯東西。
    """
    # P4c apply-at-execution：先取本租戶 active Configuration Set 的有效設定與 per-config
    # deps（取不到/故障 → per_config=None + 全域逾時，走全域路徑）。custom skill 直接以
    # per_config 編圖；builtin 若有覆寫則在下方以 per_config 重編（縫⑤）。
    # retrieval_top_k：租戶明確覆寫 retrieval.top_k 時才非 None，下方 seed 進初始 state，讓
    # 通用 retrieve@1.0 執行期收到覆寫值（縫⑦ runtime apply，既有 compare/stats skill 免重 compose）。
    per_config, timeout_default, retrieval_top_k = await config_apply.resolve(ctx)

    loaded = skills.get(name)
    if loaded is None:
        try:
            # per_config=None（無覆寫）時以位置慣例呼叫，沿用原簽章
            loaded = (
                await custom.load(name, ctx, deps=per_config)
                if per_config is not None
                else await custom.load(name, ctx)
            )
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

    _require_role(loaded.skill.required_role, ctx, name)
    _validate_input(loaded.input_model, req.input, name)

    # builtin 有覆寫 → 以 per_config 重編（縫⑤：靠 id(deps) 去重、不沿用啟動圖）；
    # 無覆寫 → 沿用啟動預編圖（零額外成本，回歸現況）。custom 的圖已在 load 時以 per_config 編好。
    graph = loaded.graph
    if per_config is not None and loaded.source == "builtin":
        graph = compiler.compile(loaded.skill, per_config)

    # 身分鍵（tenant_id/user_id/role）一律以呼叫者真實 ctx seed —— 節點/tool 的 ToolContext
    # 從 state 這三鍵取身分。input 偽造的同名鍵已被 _clean_skill_input 剝除（RESERVED_KEYS
    # 涵蓋 IDENTITY_KEYS），故此處是唯一可信注入點，杜絕偽造。
    state = {
        "tenant_id": ctx.tenant_id,
        "user_id": ctx.user_id,
        "role": ctx.role,
        **_clean_skill_input(req.input),
    }
    # 縫⑦ runtime apply：租戶覆寫了 retrieval.top_k 才 seed（retrieval_top_k 是 RESERVED_KEYS，
    # _clean_skill_input 已把呼叫端夾帶的同名 input 剝掉 → 此處是唯一可信注入點，杜絕偽造）。
    if retrieval_top_k is not None:
        state["retrieval_top_k"] = retrieval_top_k
    # 逾時讀有效設定的 workflow.timeout_seconds（skill 自帶的 timeout_seconds 仍優先）。
    # agentic：outer = node 的 inner deadline + GRACE，讓 runner 的 node-level timeout 先觸發
    # （fatal → 稽核仍跑），outer 只當純 backstop；flow 走原本行為，不受影響。
    if loaded.skill.kind == "agentic":
        inner = loaded.skill.timeout_seconds or DEFAULT_AGENT_TIMEOUT_S
        timeout_seconds = inner + AGENT_INVOKE_TIMEOUT_GRACE_S
    else:
        timeout_seconds = loaded.skill.timeout_seconds or timeout_default
    # recursion_limit：規格 §6.3-2 的全圖護欄（langgraph 預設 10007 形同沒有護欄）
    config = {**tracing.runnable_config(), "recursion_limit": loaded.recursion_limit}

    output = await _run_with_timeout(
        graph.ainvoke(state, config=config), timeout_seconds, name
    )

    return SkillInvokeResponse(skill=name, output=compiler.public_output(output))
