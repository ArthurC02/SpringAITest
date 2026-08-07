import asyncio
import logging
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.exception_handlers import request_validation_exception_handler
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from python_multipart import MultipartParser
from python_multipart.exceptions import MultipartParseError
from python_multipart.multipart import parse_options_header
from pydantic import ValidationError
from starlette.formparsers import MultiPartException, MultiPartParser as StarletteMultiPartParser
from starlette.datastructures import FormData, UploadFile

from app import backend_http, correlation, skills, tracing, usage_evidence
from app.business_rules.catalog import catalog_response as business_rule_catalog
from app.business_rules.models import (
    RuleCatalogResponse,
    RuleSimulationRequest,
    RuleSimulationResponse,
    RuleValidationRequest,
    RuleValidationResponse,
)
from app.http_limits import JsonRequestLimitMiddleware
from app.business_rules.simulator import simulate as simulate_business_rules
from app.business_rules.validator import (
    canonical_to_json as canonical_business_rule_set,
    validate_simulation_facts,
    validate_rule_set,
)
from app.engine import compiler, node_registry, package, tool_registry
from app.engine.skill import ValidationResult, clean_invoke_input, validate_source
from app.evals.api import router as evals_router
from app.skills import config_apply, custom


class _PackageTooLarge(MultiPartException):
    """The package file exceeded its raw-byte transport limit while streaming."""


class _PackageUploadParser(StarletteMultiPartParser):
    """Multipart parser that applies the package raw limit before spooling it all."""

    def __init__(self, request: Request) -> None:
        super().__init__(
            request.headers,
            request.stream(),
            max_files=1,
            max_fields=1,
        )
        self._package_bytes = 0
        self._completed = False
        self._seen_fields: set[str] = set()

    def on_headers_finished(self) -> None:
        super().on_headers_finished()
        field_name = self._current_part.field_name
        if field_name in self._seen_fields:
            raise MultiPartException(f"Duplicate multipart field: {field_name}.")
        self._seen_fields.add(field_name)
        if self._current_part.file is not None and field_name != "package":
            raise MultiPartException("Unexpected file field.")
        if self._current_part.file is None and field_name != "expected_name":
            raise MultiPartException("Unexpected scalar field.")

    def on_part_data(self, data: bytes, start: int, end: int) -> None:
        if (
            self._current_part.file is not None
            and self._current_part.field_name == "package"
        ):
            chunk_size = end - start
            if self._package_bytes + chunk_size > package.MAX_PACKAGE_RAW_BYTES:
                raise _PackageTooLarge("Skill package exceeds the raw-byte limit.")
            self._package_bytes += chunk_size
        super().on_part_data(data, start, end)

    def on_end(self) -> None:
        self._completed = True

    async def parse(self) -> FormData:
        """Require the closing boundary and close every tracked file on any abort."""
        _, params = parse_options_header(self.headers["Content-Type"])
        charset = params.get(b"charset", "utf-8")
        self._charset = charset.decode("latin-1") if isinstance(charset, bytes) else charset
        try:
            boundary = params[b"boundary"]
        except KeyError as exc:
            raise MultiPartException("Missing boundary in multipart.") from exc

        parser = MultipartParser(
            boundary,
            {
                "on_part_begin": self.on_part_begin,
                "on_part_data": self.on_part_data,
                "on_part_end": self.on_part_end,
                "on_header_field": self.on_header_field,
                "on_header_value": self.on_header_value,
                "on_header_end": self.on_header_end,
                "on_headers_finished": self.on_headers_finished,
                "on_end": self.on_end,
            },
        )
        try:
            async for chunk in self.stream:
                parser.write(chunk)
                for part, data in self._file_parts_to_write:
                    assert part.file is not None
                    await part.file.write(data)
                for part in self._file_parts_to_finish:
                    assert part.file is not None
                    await part.file.seek(0)
                self._file_parts_to_write.clear()
                self._file_parts_to_finish.clear()
            parser.finalize()
            if not self._completed:
                raise MultiPartException("Incomplete multipart body.")
            return FormData(self.items)
        except BaseException:
            self._close_tracked_files()
            raise

    def _close_tracked_files(self) -> None:
        """Best-effort cleanup must never replace the parser's original failure."""
        for file in self._files_to_close_on_error:
            try:
                file.close()
            except BaseException:
                pass


async def _read_package_upload(request: Request) -> tuple[bytes, str | None]:
    """Parse one package multipart request with raw-byte enforcement during read."""
    try:
        form = await _PackageUploadParser(request).parse()
    except _PackageTooLarge as exc:
        raise HTTPException(
            status_code=413,
            detail={
                "error": "package_too_large",
                "message": "Skill package is too large.",
            },
        ) from exc
    except (KeyError, MultiPartException, MultipartParseError) as exc:
        raise HTTPException(status_code=400, detail="Invalid package multipart form.") from exc

    package_file = form.get("package")
    if not isinstance(package_file, UploadFile):
        raise HTTPException(status_code=422, detail="package file is required.")
    expected_name = form.get("expected_name")
    if expected_name is not None and not isinstance(expected_name, str):
        raise HTTPException(status_code=422, detail="expected_name must be a string.")
    try:
        return await package_file.read(), expected_name
    finally:
        await package_file.close()

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
    ToolInfo,
    ValidatePackageResult,
)
from app.security import FeatureGateMiddleware, RequestContext, get_context, require_internal
from app.runtime.api import router as agent_runtime_router
from app.runtime.orchestrator_api import router as orchestrator_runtime_router
from app.runtime.orchestrator_backend import OrchestratorBackendClient
from app.runtime.orchestrator_supervisor import RootRuntimeSupervisor
from app.orchestration.api import router as workflow_designer_router
from app.runtime.service import RuntimeService
from app.runtime.checkpoints import PostgresCheckpointStore, require_runtime_checkpoint_dsn
from app.runtime.retention import CheckpointRetentionService
from app.runtime.flow_harness import PUBLIC_DENY_KEYS, invoke_flow_with_governance
from app.settings import settings
from app.health import WorkflowReadinessProbe, live_report

logger = logging.getLogger(__name__)


def _invoke_surface(request: Request) -> str:
    origin = request.headers.get("X-Artifact-Usage-Origin")
    if origin in {"public_skills", "workflow_unified_invoke"}:
        return origin
    return "unknown_origin"


def _usage_outcome(status_code: int) -> str:
    if status_code == 404:
        return "not_found"
    if status_code < 500:
        return "rejected"
    return "error"


async def _count_unified_invoke(request: Request):
    """只在 handler 已放入可信 artifact type 後，為一次統一 invoke 收尾計數。"""
    outcome = "success"
    try:
        yield
    except RequestValidationError:
        outcome = "rejected"
        raise
    except HTTPException as exc:
        outcome = _usage_outcome(exc.status_code)
        raise
    except BaseException:
        outcome = "error"
        raise
    finally:
        usage_evidence.record(
            surface=_invoke_surface(request),
            operation="invoke",
            resolved_artifact_type=getattr(
                request.state, "usage_resolved_artifact_type", "unknown"
            ),
            outcome=outcome,
        )
        request.state.usage_evidence_counted = True


def _clean_skill_input(raw: dict) -> dict:
    """skill invoke 的 input 過濾：剝除保留鍵、引擎鍵與引擎內部鍵。

    query_id / original_query / query_timestamp 等保留鍵若被呼叫端夾帶，會原封不動
    流進 audit_trail —— 一般 USER 即可偽造稽核軌跡上的「原始問題」。Harness 的
    IMMUTABLE_KEYS 只擋節點寫入，擋不住 input，所以這一關必須在這裡做。
    引擎鍵（trace / errors / fatal_error，由 Harness 寫入）同樣不可經 input 夾帶：
    夾帶 fatal_error 會讓所有節點走 fatal 短路而跳過，夾帶 trace/errors 更會讓 reducer
    型別不符而 500。引擎內部鍵（__ 前綴）同理一併剝除。
    """
    return clean_invoke_input(raw)


# agentic invoke 的對外遮蔽清單：與 flow 的 `_public_flow_output` 共用同一份
# PUBLIC_DENY_KEYS，只多放行 trace 與（下面會去識別化的）fatal_error 兩個鍵。
_AGENTIC_DENY_KEYS = PUBLIC_DENY_KEYS - {"trace", "fatal_error"}


def _public_agentic_output(state: dict) -> dict:
    """agentic invoke 對外輸出前的遮蔽（規格 §3.3：不外洩原始例外文字）。

    Node Shell 把節點例外吞成 `fatal_error = f"{node}: {e}"`、`errors[].error = str(e)`，
    audit_feedback 又把同一份 errors 抄進 `audit_trail`；agentic 是受控失敗（HTTP 200），
    所以這些原始例外文字（backend 路徑、URL、provider 細節）會隨 200 一起送出去。
    flow 路徑早就用 PUBLIC_DENY_KEYS 擋掉這整組鍵，agentic 只是沒接上同一份清單 ——
    這裡接上，不另立第二套標準。

    兩個例外：`trace` 是 agentic 的公開契約，且 TraceEntry 只帶 `error_code`＝例外型別名
    （無訊息內文），保留；`fatal_error` 保留「這次失敗了」這個訊號、文字換成固定安全訊息，
    要細節請拿回應標頭的 correlation ID 查日誌。
    """
    public = {key: value for key, value in state.items() if key not in _AGENTIC_DENY_KEYS}
    if public.get("fatal_error"):
        public["fatal_error"] = correlation.SAFE_EXECUTION_FAILED_MESSAGE
    return public


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
    except Exception:
        # 原始例外只進日誌（規格 §3.3）；呼叫端拿到的是固定安全訊息 + correlation ID。
        raise correlation.execution_failed(logger, f"skill '{name}' invoke")


@asynccontextmanager
async def lifespan(app: FastAPI):
    """服務生命週期：暖身並在關機時釋放共用 backend HTTP client（連線池不外洩）。"""
    if settings.checkpoint_retention_mode == "delete":
        # Settings rejects this deployment today; retain an assembly-level
        # wall as well so tests or future configuration wiring cannot silently
        # construct delete mode with the default no-op evidence hook.
        raise RuntimeError(
            "checkpoint retention delete requires a backup/restore evidence provider"
        )
    backend_http.get_client()  # 暖身：啟動時就備好連線池，首個請求不必臨時建立
    runtime_service: RuntimeService | None = None
    retention_store: PostgresCheckpointStore | None = None
    root_supervisor: RootRuntimeSupervisor | None = None
    if settings.multi_agent_dispatch_enabled:
        # The durable Backend port is production-wired independently from the
        # D3 direct-Agent manager. Root execution is attached only when every
        # required child/result/terminal adapter is available.
        app.state.orchestrator_backend = OrchestratorBackendClient()
    if settings.agent_test_run_enabled or settings.multi_agent_dispatch_enabled:
        runtime_service = await RuntimeService.open()
        app.state.agent_runtime_manager = runtime_service.manager
    if settings.multi_agent_dispatch_enabled and runtime_service is not None:
        root_supervisor = RootRuntimeSupervisor(
            app.state.orchestrator_backend, runtime_service.manager, runtime_service.checkpoints
        )
        root_supervisor.start()
        app.state.root_runtime_supervisor = root_supervisor
    if settings.checkpoint_retention_mode != "off":
        if runtime_service is not None:
            app.state.checkpoint_retention = CheckpointRetentionService(
                runtime_service.checkpoints
            )
        else:
            retention_store = PostgresCheckpointStore(require_runtime_checkpoint_dsn())
            await retention_store.open()
            app.state.checkpoint_retention = CheckpointRetentionService(retention_store)
    try:
        yield
    finally:
        if root_supervisor is not None:
            await root_supervisor.close()
        if runtime_service is not None:
            await runtime_service.close()
        if retention_store is not None:
            await retention_store.close()
        await backend_http.aclose_client()


app = FastAPI(title="springaitest-workflow", lifespan=lifespan)
app.state.readiness_probe = WorkflowReadinessProbe()


@app.middleware("http")
async def count_pre_controller_invoke(request: Request, call_next):
    response = await call_next(request)
    path = request.url.path
    if (
        path.startswith("/skills/")
        and path.endswith("/invoke")
        and response.status_code >= 400
        and not getattr(request.state, "usage_evidence_counted", False)
    ):
        usage_evidence.record(
            surface="unknown_origin",
            operation="invoke",
            resolved_artifact_type="unknown",
            outcome=_usage_outcome(response.status_code),
        )
    return response


app.add_middleware(JsonRequestLimitMiddleware)
app.add_middleware(
    FeatureGateMiddleware,
    prefix="/agent-runs/",
    flag_name="agent_test_run_enabled",
)
app.add_middleware(
    FeatureGateMiddleware,
    prefix="/orchestrator-runs",
    flag_name="multi_agent_dispatch_enabled",
)
app.add_middleware(
    FeatureGateMiddleware,
    prefix="/evals/",
    flag_name="run_eval_enabled",
)
# 最後 add ＝ 站在最外層（Starlette 把 user_middleware 反序堆疊）：correlation ID 因此對
# 每一個回應都成立，包含被 feature gate / body 上限 middleware 提前擋掉的那些。
app.add_middleware(correlation.CorrelationIdMiddleware)
app.include_router(agent_runtime_router)
app.include_router(orchestrator_runtime_router)
app.include_router(workflow_designer_router)
app.include_router(evals_router)


@app.exception_handler(RequestValidationError)
async def bounded_business_rule_request_error(
    request: Request, exc: RequestValidationError
):
    """Do not reflect unbounded Pydantic errors or attacker-controlled inputs."""
    if request.url.path in {
        "/business-rules/validate",
        "/business-rules/simulate",
    }:
        return JSONResponse(
            status_code=422,
            content={
                "detail": {
                    "error": "business_rule_request_invalid",
                    "message": "Business Rule request body is invalid.",
                    "field_errors": {"request": "Check the request schema and types."},
                }
            },
        )
    validation_surfaces = {
        "/skills/validate": "workflow_validate_alias",
        "/business-workflows/validate": "workflow_business_workflows_validate",
    }
    surface = validation_surfaces.get(request.url.path)
    if (
        surface is not None
        and request.headers.get("X-Artifact-Usage-Origin") != "dependency"
    ):
        usage_evidence.record(
            surface=surface,
            operation="validate",
            resolved_artifact_type="unknown",
            outcome="rejected",
        )
    return await request_validation_exception_handler(request, exc)


@app.get("/health/live")
async def health_live() -> dict:
    """Process/event-loop liveness only; never calls a dependency."""
    return live_report()


async def _health_ready_response(request: Request) -> JSONResponse:
    report = await request.app.state.readiness_probe.check()
    return JSONResponse(status_code=200 if report["ready"] else 503, content=report)


@app.get("/health/ready")
async def health_ready(request: Request) -> JSONResponse:
    return await _health_ready_response(request)


@app.get("/health")
async def health() -> dict:
    """Compatibility alias for liveness; health endpoints require no token."""
    return live_report()


@app.post("/checkpoint-retention/run")
async def run_checkpoint_retention(
    _: None = Depends(require_internal),
) -> dict:
    if settings.checkpoint_retention_mode == "off":
        return {"mode": "off", "candidate_count": 0, "deleted_count": 0}
    service: CheckpointRetentionService = app.state.checkpoint_retention
    return await service.run_once()


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


@app.get("/tools", response_model=list[ToolInfo])
async def list_tools(ctx: RequestContext = Depends(get_context)) -> list[ToolInfo]:
    """安全 Tool 目錄：供 Agent Builder picker 使用，registry 是唯一事實來源。"""
    return [
        ToolInfo(
            name=spec.name,
            kind=spec.kind,
            description=spec.description,
            risk=spec.risk,
            returns=spec.returns,
        )
        for spec in tool_registry.all_specs()
    ]


@app.get("/business-rules/catalog", response_model=RuleCatalogResponse)
async def get_business_rule_catalog(
    ctx: RequestContext = Depends(get_context),
) -> RuleCatalogResponse:
    """Business Rule editor catalog.

    This internal route is the sole source of fact/operator/action semantics for
    .NET and the frontend.  The request gate is deliberately not persisted in a
    RuleSet; consumers pass it to validate/simulate for the intended policy gate.
    """
    return RuleCatalogResponse.model_validate(business_rule_catalog())


@app.post(
    "/business-rules/validate",
    response_model=RuleValidationResponse,
)
async def validate_business_rules(
    req: RuleValidationRequest,
    ctx: RequestContext = Depends(get_context),
) -> RuleValidationResponse:
    """Validate and canonicalize a RuleSet without evaluating or persisting it."""
    outcome = validate_rule_set(
        req.gate,
        req.rule_set,
        (
            req.reference_catalog.model_dump()
            if req.reference_catalog is not None
            else None
        ),
    )
    return RuleValidationResponse(
        valid=outcome.valid,
        canonicalRuleSet=(
            canonical_business_rule_set(outcome.canonical_rule_set)
            if outcome.canonical_rule_set is not None
            else None
        ),
        errors=list(outcome.errors),
    )


@app.post(
    "/business-rules/simulate",
    response_model=RuleSimulationResponse,
)
async def simulate_business_rule_set(
    req: RuleSimulationRequest,
    ctx: RequestContext = Depends(get_context),
) -> RuleSimulationResponse:
    """Dry-run using the exact production evaluator.

    The simulator has no adapter or dependency injection hook through which it
    could reach a tool, network, file, or database.
    """
    outcome = validate_rule_set(
        req.gate,
        req.rule_set,
        (
            req.reference_catalog.model_dump()
            if req.reference_catalog is not None
            else None
        ),
    )
    if outcome.canonical_rule_set is None:
        return RuleSimulationResponse(valid=False, errors=list(outcome.errors))
    canonical = canonical_business_rule_set(outcome.canonical_rule_set)
    fact_errors = validate_simulation_facts(req.facts)
    if fact_errors:
        return RuleSimulationResponse(
            valid=False,
            canonicalRuleSet=canonical,
            errors=list(fact_errors),
        )
    return RuleSimulationResponse(
        valid=True,
        canonicalRuleSet=canonical,
        errors=[],
        simulation=simulate_business_rules(
            req.gate, outcome.canonical_rule_set, req.facts
        ),
    )


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
            bindable=False,
            kind=loaded.skill.kind,
            input_schema=loaded.skill.input_schema or None,
            # 內建骨架帶原文供前端 compose patch;custom 不帶（catalog dict 無此鍵 → None）
            definition=loaded.definition or None,
        )
        for loaded in skills.all_skills()
    ]
    return builtin + [SkillInfo(**entry) for entry in await custom.catalog(ctx)]


@app.post(
    "/business-workflows/validate",
    response_model=ValidationResult,
    response_model_exclude_none=True,
)
@app.post("/skills/validate", response_model=ValidationResult, response_model_exclude_none=True)
async def validate_skill(
    req: SkillValidateRequest,
    request: Request,
    ctx: RequestContext = Depends(get_context),
) -> ValidationResult:
    """對一份 Business Workflow 定義原文跑全部靜態驗證。

    ``/skills/validate`` 是相容 alias；兩條路徑共用此 handler，且都無副作用：
    不編譯、不寫入、不改變 GET /skills。

    永遠回 200 + {valid, errors[]}（含 YAML 解析失敗），錯誤在 body 裡 ——
    前端編輯器要的是可逐條標紅的錯誤清單，不是一個 HTTP 錯誤碼。

    valid=true 時多回一段 skill 中繼資料（name/description/required_role/input_schema）：
    backend 不裝 YAML parser，這是它寫入 DB 的唯一資料來源。exclude_none 保證
    valid=false 時**不出現** skill 欄位 —— backend 以它的有無決定能不能寫。

    撰寫者角色 gate（保守版）：帶入呼叫者真實角色（backend 的 WorkflowSkillValidator 與
    platform proxy 都會轉發真實作者身分），非 ADMIN 提交含 script 步驟的定義即驗證失敗。
    僅在此寫入路徑生效；不改 required_role 語意，也不影響 invoke／custom.load。
    """
    count_usage = request.headers.get("X-Artifact-Usage-Origin") != "dependency"
    surface = (
        "workflow_validate_alias"
        if request.url.path == "/skills/validate"
        else "workflow_business_workflows_validate"
    )
    try:
        result = validate_source(req.definition, author_role=ctx.role)
    except BaseException:
        if count_usage:
            usage_evidence.record(
                surface=surface,
                operation="validate",
                resolved_artifact_type="unknown",
                outcome="error",
            )
        raise
    if count_usage:
        usage_evidence.record(
            surface=surface,
            operation="validate",
            resolved_artifact_type="business_workflow" if result.valid else "unknown",
            outcome="success" if result.valid else "rejected",
        )
    return result


@app.post(
    "/skills/validate-package",
    response_model=ValidatePackageResult,
    response_model_exclude_none=True,
)
async def validate_package(
    request: Request,
    ctx: RequestContext = Depends(get_context),
) -> ValidatePackageResult:
    """驗證上傳的 Skill package zip（設計 §2.2）。internal-only，multipart。

    無副作用：只解析與驗證，不編譯、不寫入、不執行 package 內的 script（規格 R8）。
    valid=true 時回既有 validation 回應的 superset（多 kind、canonical_definition、
    package_manifest）；valid=false 時 exclude_none 保證不出現任何可被寫入的 metadata
    /definition —— backend 以「有沒有 skill/canonical_definition」決定能不能寫。

    Workflow 是 SKILL.md 的唯一 parser 與語意 validator（規格 R3）；backend 不另做一套。

    撰寫者角色 gate：與 /skills/validate 同樣帶入呼叫者真實角色，非 ADMIN 匯入含 script
    步驟的 flow 定義即驗證失敗。呼叫端（backend 三個匯入入口、platform proxy）本身已是
    ADMIN-only，這是同一條寫入路徑最內層的縱深防禦。
    """
    raw, expected_name = await _read_package_upload(request)
    try:
        parsed = package.parse_package(raw, expected_name, author_role=ctx.role)
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
    request: Request,
    ctx: RequestContext = Depends(get_context),
    _usage: None = Depends(_count_unified_invoke),
) -> SkillInvokeResponse:
    """執行指定 skill（內建或本租戶自訂）。驗證順序：
    存在（404）→ 角色（403）→ input schema（422）→ 逾時（504）／未預期例外（500）。

    名稱先查內建、再向 backend 查自訂。backend 不可達 → 500（受控）而不是 404：
    「取不到定義」與「skill 不存在」是兩件事，混為一談會讓呼叫端刪錯東西。
    """
    # This skill is an internal Root runtime component, not an API-invokable
    # user asset.  Hide it before config/backend work regardless of feature
    # state so direct invocation can never bypass Root authority or budgets.
    if name in {"context-enrichment", "context-task-local"}:
        raise HTTPException(status_code=404, detail="Not Found")

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
        except (custom.BackendUnavailable, custom.InvalidCustomSkill):
            # 這些訊息夾帶 backend path/URL 與編譯器例外文字，一律不外流。
            raise correlation.execution_failed(logger, f"custom skill '{name}' load")

    if loaded is None:
        raise HTTPException(
            status_code=404,
            detail={
                "error": "workflow_not_found",
                "message": f"unknown skill: {name}",
                "skills": [s.skill.name for s in skills.all_skills()],
            },
        )

    request.state.usage_resolved_artifact_type = (
        "business_workflow" if loaded.skill.kind == "flow" else "agent_skill"
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

    if loaded.skill.kind == "flow":
        result = await invoke_flow_with_governance(
            skill=loaded.skill,
            raw_input=state,
            deps=loaded.deps,
            timeout_seconds=timeout_seconds,
            step_budget=settings.workflow_step_budget,
            tool_round_budget=max(1, len(loaded.skill.uses_tools) * 10 or 50),
            graph=graph,
            recursion_limit=loaded.recursion_limit,
            definition=loaded.definition,
            definition_sha256=loaded.definition_sha256,
        )
        if result.status == "timeout":
            raise HTTPException(
                status_code=504,
                detail={
                    "error": "workflow_timeout",
                    "message": f"skill '{name}' 執行超過 {timeout_seconds} 秒",
                },
            )
        if result.status == "budget_exhausted":
            # 預算耗盡是列舉出來的治理結果（訊息只含 node 名），不是未預期例外，訊息照舊。
            raise HTTPException(
                status_code=500,
                detail={
                    "error": "workflow_execution_failed",
                    "message": result.governance.get("error", result.status),
                },
            )
        if result.status == "error":
            # governance["error"] 會夾帶節點／harness 的原始例外文字（稽核要留，HTTP 不能給）。
            raise HTTPException(status_code=500, detail=correlation.execution_failed_detail())
        output = result.output
    else:
        output = _public_agentic_output(
            await _run_with_timeout(
                graph.ainvoke(state, config=config), timeout_seconds, name
            )
        )

    return SkillInvokeResponse(skill=name, output=compiler.public_output(output))
