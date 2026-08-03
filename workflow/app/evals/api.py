"""POST /evals/run：Phase E2 versioned eval runner（規格 §5，跨服務 contract 已定案）。

Workflow 只執行 eval candidate，不保存 release state、不建 suite catalog——suite 定義
由 Backend 以 request payload 傳入，結果由 response 回傳。旗標關閉時（RUN_EVAL_ENABLED
預設 false），`FeatureGateMiddleware`（app.main 掛載）在路由與 body 解析之前就回 404，
與既有 /agent-runs、/orchestrator-runs 兩條旗標保護路由同一套慣例。
"""

import logging
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException

from app import correlation, skills
from app.engine.skill import Skill
from app.evals.models import EvalRunRequest, EvalRunResponse
from app.evals.runner import RUNNER_VERSION, run_cases
from app.security import RequestContext, get_context
from app.skills import custom

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/evals", tags=["evals"])

# Root runtime 內部元件，不是可評測的使用者資產——比照 `/skills/{name}/invoke`
# 同名的前置隱藏檢查（app.main.invoke_skill）。
_HIDDEN_SKILL_NAMES = frozenset({"context-enrichment", "context-task-local"})


async def _resolve_candidate_skill(ref: dict, ctx: RequestContext) -> Skill:
    """candidate.ref → Skill 定義。查詢慣例與 `/skills/{name}/invoke` 相同：內建優先、
    再向 backend 查本租戶自訂；查無／角色不符／kind 不支援一律在跑任何 case 之前
    以請求層級錯誤拒絕（候選對整個 suite 只解一次，不是逐 case 的問題）。
    """
    name = ref.get("name")
    if not isinstance(name, str) or not name.strip():
        raise HTTPException(
            status_code=422,
            detail={
                "error": "workflow_eval_invalid_candidate",
                "message": "candidate.ref.name 為必填字串",
            },
        )
    if name in _HIDDEN_SKILL_NAMES:
        raise HTTPException(status_code=404, detail="Not Found")

    loaded = skills.get(name)
    if loaded is None:
        try:
            loaded = await custom.load(name, ctx)
        except (custom.BackendUnavailable, custom.InvalidCustomSkill):
            # 訊息夾帶 backend path/URL 與編譯器例外文字；只進日誌，不進回應（規格 §3.3）。
            raise correlation.execution_failed(logger, f"eval candidate '{name}' load")
    if loaded is None:
        raise HTTPException(
            status_code=404,
            detail={
                "error": "workflow_not_found",
                "message": f"unknown skill: {name}",
            },
        )
    if loaded.skill.required_role == "ADMIN" and ctx.role != "ADMIN":
        raise HTTPException(
            status_code=403,
            detail={
                "error": "workflow_forbidden",
                "message": f"skill '{name}' 需要 ADMIN 角色權限",
            },
        )
    if loaded.skill.kind != "flow":
        # agentic candidate 需要 agent_package_reader/agent_chat_model 等額外注入，
        # 本階段（deterministic fixture / recorded replay）尚未支援，先明確拒絕而非
        # 讓它在編圖時才炸成難懂的錯誤。
        raise HTTPException(
            status_code=422,
            detail={
                "error": "workflow_eval_unsupported_candidate",
                "message": f"candidate skill kind '{loaded.skill.kind}' 本階段尚未支援 eval",
            },
        )
    return loaded.skill


@router.post("/run", response_model=EvalRunResponse)
async def run_eval(
    req: EvalRunRequest, ctx: RequestContext = Depends(get_context)
) -> EvalRunResponse:
    """執行一次 versioned eval。

    candidate.kind 目前只支援 "skill"；"agent" 是契約預留形狀，尚未實作，回 422。
    case.mode 為 "deterministic" 或 "replay" 時共用同一套 fixture 執行機制
    （差別只在 fixtures 資料的來源，見 app.evals.fixtures）；其他值在請求解析階段
    已被 Pydantic Literal 收斂成 422（live shadow 本階段不實作）。
    """
    if req.candidate.kind != "skill":
        raise HTTPException(
            status_code=422,
            detail={
                "error": "workflow_eval_unsupported_candidate",
                "message": f"candidate.kind '{req.candidate.kind}' 本階段尚未支援",
            },
        )
    skill = await _resolve_candidate_skill(req.candidate.ref, ctx)

    started_at = datetime.now(timezone.utc).isoformat()
    cases = await run_cases(
        skill=skill,
        suite_id=req.suite.suite_id,
        revision=req.suite.revision,
        cases=req.suite.cases,
        candidate_pins=req.candidate.pins or {},
        budget_ms=req.runner.budget_ms,
        tenant_id=ctx.tenant_id,
        user_id=ctx.user_id,
        role=ctx.role,
    )
    completed_at = datetime.now(timezone.utc).isoformat()

    return EvalRunResponse(
        runner_version=RUNNER_VERSION,
        suite_id=req.suite.suite_id,
        revision=req.suite.revision,
        started_at=started_at,
        completed_at=completed_at,
        cases=cases,
    )
