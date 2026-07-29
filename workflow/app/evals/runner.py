"""Phase E2 eval runner 執行核心。

候選 skill 解出後（app.evals.api），逐 case 以 fixture 依賴重新編圖並執行一次——沿用
`/skills/{name}/invoke` 同一條 compiler+harness 路徑，不另闢旁路。Workflow 對 eval
全程 stateless：這裡只算 canonical_identity、跑一次 side-effect-free 的 invoke、
比對 expected，不落地任何資料。
"""

import asyncio
import time
from typing import Any

from pydantic import ValidationError

from app import tracing
from app.canonical_json import canonical_json_sha256
from app.engine import compiler
from app.engine.node_registry import ENGINE_KEYS
from app.engine.skill import RESERVED_KEYS, Skill, build_input_model
from app.evals.fixtures import build_fixture_deps
from app.evals.models import EvalCase, EvalCaseResult
from app.settings import settings

# 常數而非設定：runner 的身分本身也是 canonical_identity 的一部分（規格 §5.3），
# 版本一變就代表結果不可再視為同一份 evidence，不應該被環境變數悄悄覆寫。
RUNNER_VERSION = "eval-runner/1"


def _clean_case_input(raw: dict[str, Any]) -> dict[str, Any]:
    """比照 app.main._clean_skill_input：eval case 的 input 同樣是不可信輸入，
    剝除保留鍵／引擎鍵／__ 前綴，不讓呼叫端經 case.input 夾帶偽造的保留鍵。
    """
    return {
        k: v
        for k, v in raw.items()
        if k not in RESERVED_KEYS and k not in ENGINE_KEYS and not k.startswith("__")
    }


def case_canonical_identity(
    suite_id: str, revision: int, case: EvalCase, candidate_pins: dict[str, Any]
) -> str:
    """SHA-256（canonical JSON）：同一組 deterministic 輸入永遠算出同一個 identity
    （規格 §5.3/§9 驗收）。"""
    return canonical_json_sha256(
        {
            "suite_id": suite_id,
            "revision": revision,
            "case_id": case.case_id,
            "mode": case.mode,
            "input": case.input,
            "expected": case.expected,
            "fixtures": case.fixtures,
            "candidate_pins": candidate_pins,
            "runner_version": RUNNER_VERSION,
        }
    )


def _first_mismatch(expected: dict[str, Any], actual: dict[str, Any]) -> str | None:
    """expected 是子集比對：逐鍵比對 actual，回第一個不符的描述；全部相符回 None。"""
    missing = object()
    for key, want in expected.items():
        got = actual.get(key, missing)
        if got != want:
            return f"欄位 '{key}' 預期 {want!r}，實得 {got!r}"
    return None


async def _execute_case(
    skill: Skill,
    case: EvalCase,
    identity: str,
    *,
    tenant_id: str,
    user_id: str,
    role: str,
) -> EvalCaseResult:
    t0 = time.perf_counter()

    def _latency_ms() -> int:
        # 契約型別是 int（規格 §5.3 metrics.latency_ms）；四捨五入取毫秒，不回浮點。
        return round((time.perf_counter() - t0) * 1000)

    try:
        cleaned = _clean_case_input(case.input)
        input_model = build_input_model(skill)
        if input_model is not None:
            input_model.model_validate(cleaned)

        deps = build_fixture_deps(case.fixtures)
        # cache=False：每個 case 各自的 deps 都是新物件，走全域編譯快取只會把正式
        # skill 的圖擠出 32 格 FIFO（規格見 compiler.compile 的 docstring）。
        graph = compiler.compile(skill, deps, cache=False)
        state = {"tenant_id": tenant_id, "user_id": user_id, "role": role, **cleaned}
        config = {
            **tracing.runnable_config(),
            "recursion_limit": compiler.recursion_limit(skill),
        }
        raw = await asyncio.wait_for(
            graph.ainvoke(state, config=config),
            timeout=settings.workflow_timeout_seconds,
        )
        output = compiler.public_output(raw)
    except ValidationError as e:
        return EvalCaseResult(
            case_id=case.case_id,
            canonical_identity=identity,
            verdict="ERROR",
            metrics={"latency_ms": _latency_ms()},
            failure_reason=f"input 驗證失敗: {e}",
        )
    except asyncio.TimeoutError:
        return EvalCaseResult(
            case_id=case.case_id,
            canonical_identity=identity,
            verdict="ERROR",
            metrics={"latency_ms": _latency_ms()},
            failure_reason=f"case 執行超過 {settings.workflow_timeout_seconds}s",
        )
    except Exception as e:
        return EvalCaseResult(
            case_id=case.case_id,
            canonical_identity=identity,
            verdict="ERROR",
            metrics={"latency_ms": _latency_ms()},
            failure_reason=str(e),
        )

    mismatch = _first_mismatch(case.expected, output)
    return EvalCaseResult(
        case_id=case.case_id,
        canonical_identity=identity,
        verdict="FAIL" if mismatch else "PASS",
        metrics={"latency_ms": _latency_ms()},
        failure_reason=mismatch,
    )


async def run_cases(
    *,
    skill: Skill,
    suite_id: str,
    revision: int,
    cases: list[EvalCase],
    candidate_pins: dict[str, Any],
    budget_ms: int | None,
    tenant_id: str,
    user_id: str,
    role: str,
) -> list[EvalCaseResult]:
    """逐 case 執行；單一 case 失敗（ERROR）不中斷整個 suite（規格 §9）。

    budget_ms 用盡時，剩餘 case 一律標 ERROR("budget exhausted")而不執行，已完成的
    部分正常回傳（規格 §5.2 budget 驗收）。
    """
    results: list[EvalCaseResult] = []
    loop_start = time.monotonic()
    for case in cases:
        identity = case_canonical_identity(suite_id, revision, case, candidate_pins)
        if (
            budget_ms is not None
            and (time.monotonic() - loop_start) * 1000 >= budget_ms
        ):
            results.append(
                EvalCaseResult(
                    case_id=case.case_id,
                    canonical_identity=identity,
                    verdict="ERROR",
                    metrics={"latency_ms": 0},
                    failure_reason="budget exhausted",
                )
            )
            continue
        results.append(
            await _execute_case(
                skill, case, identity, tenant_id=tenant_id, user_id=user_id, role=role
            )
        )
    return results
