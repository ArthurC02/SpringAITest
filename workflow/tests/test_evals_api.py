"""Phase E2 versioned eval runner 測試(POST /evals/run)。

覆蓋:旗標 fail-closed、candidate/mode 決策表的合法/非法兩側、deterministic fixture
執行下的 PASS/FAIL/ERROR 三種 verdict(單一 case 失敗不中斷 suite)、canonical_identity
與 verdict 的重複執行一致性、budget_ms 用盡的 fail-safe,以及 side-effect-free 驗收
(不打真實 backend HTTP、write_evidence 工具即使全域旗標開啟仍 fail closed)。
"""

from dataclasses import replace

import httpx
import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from app import skills
from app.engine.tool_registry import ToolContext
from app.evals.fixtures import build_fixture_deps
from app.evals.models import EvalSuite
from app.main import app
from app.settings import settings
from app.skills import custom
from app.tools import WriteEvidenceDenied, write_evidence
from tests.conftest import auth_headers, install_fake_get

client = TestClient(app)


@pytest.fixture(autouse=True)
def _eval_flag_on(monkeypatch):
    monkeypatch.setattr(settings, "run_eval_enabled", True)


def _suite(cases, *, candidate_kind="skill", skill_name="triage", budget_ms=None):
    payload = {
        "suite": {"suite_id": "csr-eval-001", "revision": 1, "cases": cases},
        "candidate": {
            "kind": candidate_kind,
            "ref": {"name": skill_name},
            "pins": {"skill_revision": 1, "model": "mock-gpt"},
        },
        "runner": {},
    }
    if budget_ms is not None:
        payload["runner"]["budget_ms"] = budget_ms
    return payload


def _run(payload):
    return client.post("/evals/run", headers=auth_headers(role="ADMIN"), json=payload)


def _pass_case(case_id="c-pass"):
    return {
        "case_id": case_id,
        "mode": "deterministic",
        "input": {"question": "今天天氣如何？"},
        "expected": {"category": "SIMPLE", "answer": "今天天氣晴朗。"},
        "fixtures": {
            "llm_responses": [{"category": "SIMPLE"}, {"answer": "今天天氣晴朗。"}]
        },
    }


# ---------------------------------------------------------------------------
# 旗標 fail-closed
# ---------------------------------------------------------------------------


def test_eval_run_feature_off_returns_404_before_auth(monkeypatch) -> None:
    monkeypatch.setattr(settings, "run_eval_enabled", False)
    response = client.post("/evals/run", json={})  # 連 X-Internal-Token 都沒帶
    assert response.status_code == 404


# ---------------------------------------------------------------------------
# 決策表:非法輸入拒、合法輸入過
# ---------------------------------------------------------------------------


def test_eval_run_unknown_mode_rejected_with_422() -> None:
    case = _pass_case()
    case["mode"] = "live_shadow"
    response = _run(_suite([case]))
    assert response.status_code == 422


def test_eval_run_unsupported_candidate_kind_rejected_with_422() -> None:
    response = _run(_suite([], candidate_kind="agent"))
    assert response.status_code == 422


def test_eval_run_agent_skill_candidate_rejected_before_cases(monkeypatch) -> None:
    """Agent Skill artifact 在載入層 fail closed，不會變成 per-case ERROR。"""
    builtin = skills.get("triage")
    loaded = replace(
        builtin,
        skill=builtin.skill.model_copy(
            update={"name": "agent-skill-eval-probe", "kind": "agentic", "flow": []}
        ),
    )

    async def load_agent_skill(name, ctx):
        return loaded

    monkeypatch.setattr(custom, "load", load_agent_skill)

    response = _run(_suite([], skill_name="agent-skill-eval-probe"))

    assert response.status_code == 422
    assert response.json()["detail"]["error"] == "workflow_eval_unsupported_candidate"


def test_eval_run_unknown_skill_returns_404(monkeypatch) -> None:
    """名稱不是內建 skill 時會 fallback 向 backend 查本租戶自訂 skill;不打真網路
    （backend 一律用手寫 fake,同 tests/test_skills_custom.py 的既有慣例)。"""

    class _NotFound:
        status_code = 404

        def raise_for_status(self) -> None:
            return None

        def json(self):
            return None

    install_fake_get(monkeypatch, lambda url, headers: _NotFound())
    response = _run(_suite([], skill_name="does-not-exist"))
    assert response.status_code == 404


def test_eval_run_blank_candidate_ref_name_rejected_with_422() -> None:
    """candidate.ref 沒有可用的名稱字串(null／空白)→ 請求層級 422,不查 backend。"""
    for blank in (None, "   "):
        response = _run(_suite([], skill_name=blank))
        assert response.status_code == 422
        assert response.json()["detail"]["error"] == "workflow_eval_invalid_candidate"


def test_eval_run_hidden_internal_skill_candidate_returns_404() -> None:
    """Root runtime 內部 skill 不是可評測資產:比照 invoke 的前置隱藏檢查回 404。"""
    response = _run(_suite([], skill_name="context-enrichment"))
    assert response.status_code == 404
    assert response.json()["detail"] == "Not Found"  # 不洩漏 workflow_not_found 細節


def test_eval_run_backend_unavailable_during_candidate_load_returns_500(
    monkeypatch,
) -> None:
    """名稱不是內建 skill 且 backend 不可達時受控失敗(500),不偽裝成 404。"""

    def _unreachable(url, headers):
        raise httpx.ConnectError("backend down")

    install_fake_get(monkeypatch, _unreachable)
    response = _run(_suite([], skill_name="does-not-exist"))
    assert response.status_code == 500
    assert response.json()["detail"]["error"] == "workflow_execution_failed"


def test_eval_run_non_admin_caller_against_admin_skill_returns_403() -> None:
    """analyze-report 是 ADMIN-only skill:USER 呼叫端在跑任何 case 之前就被擋。"""
    response = client.post(
        "/evals/run",
        headers=auth_headers(role="USER"),
        json=_suite([], skill_name="analyze-report"),
    )
    assert response.status_code == 403
    assert response.json()["detail"]["error"] == "workflow_forbidden"


def test_eval_run_role_gate_passes_for_matching_caller_and_skill_roles() -> None:
    """角色決策表的另一半:USER×USER-required 與 ADMIN×ADMIN-required 都應放行。"""
    user_on_user_skill = client.post(
        "/evals/run",
        headers=auth_headers(role="USER"),
        json=_suite([], skill_name="triage"),
    )
    admin_on_admin_skill = _run(_suite([], skill_name="analyze-report"))

    for response in (user_on_user_skill, admin_on_admin_skill):
        assert response.status_code == 200
        assert response.json()["cases"] == []


def test_eval_run_replay_mode_executes_like_deterministic_with_own_identity() -> None:
    """replay 與 deterministic 共用同一套 fixture 執行機制(runner 不讀 mode),但 mode
    進 canonical_identity,所以兩者是不同身分。"""
    replay_case = _pass_case("c-mode")
    replay_case["mode"] = "replay"
    replay = _run(_suite([replay_case])).json()["cases"][0]
    deterministic = _run(_suite([_pass_case("c-mode")])).json()["cases"][0]

    assert replay["verdict"] == deterministic["verdict"] == "PASS"
    assert replay["canonical_identity"] != deterministic["canonical_identity"]


def test_eval_run_deterministic_case_passes_with_valid_fixtures_and_input() -> None:
    response = _run(_suite([_pass_case()]))
    assert response.status_code == 200
    body = response.json()
    assert body["runner_version"] == "eval-runner/1"
    assert body["suite_id"] == "csr-eval-001"
    assert len(body["cases"]) == 1
    case = body["cases"][0]
    assert case["verdict"] == "PASS"
    assert case["failure_reason"] is None
    assert isinstance(case["metrics"]["latency_ms"], (int, float))
    assert case["canonical_identity"]


# ---------------------------------------------------------------------------
# 單一 case 失敗不中斷整個 suite:PASS/FAIL/ERROR 各留一筆
# ---------------------------------------------------------------------------


def test_eval_run_case_isolation_pass_fail_error_in_one_suite() -> None:
    fail_case = _pass_case("c-fail")
    fail_case["expected"] = {"category": "SIMPLE", "answer": "錯誤答案"}
    error_case = {
        "case_id": "c-error",
        "mode": "deterministic",
        "input": {},  # question 為必填非空字串,缺漏應被歸為該 case 的 ERROR
        "expected": {},
        "fixtures": {},
    }

    response = _run(_suite([_pass_case(), fail_case, error_case]))

    assert response.status_code == 200
    verdicts = {c["case_id"]: c for c in response.json()["cases"]}
    assert verdicts["c-pass"]["verdict"] == "PASS"
    assert verdicts["c-fail"]["verdict"] == "FAIL"
    assert verdicts["c-fail"]["failure_reason"] is not None
    assert verdicts["c-error"]["verdict"] == "ERROR"
    assert verdicts["c-error"]["failure_reason"] is not None


# ---------------------------------------------------------------------------
# Determinism:相同 deterministic 輸入 → 相同 canonical_identity 與相同 verdict
# ---------------------------------------------------------------------------


def test_eval_run_repeated_execution_yields_same_identity_and_verdict() -> None:
    payload = _suite([_pass_case()])
    first = _run(payload).json()["cases"][0]
    second = _run(payload).json()["cases"][0]
    assert first["canonical_identity"] == second["canonical_identity"]
    assert first["verdict"] == second["verdict"] == "PASS"


# ---------------------------------------------------------------------------
# cases 長度上限:on/off-point(規格數字邊界,純模型層級——不需要真的跑 200 次 skill)
# ---------------------------------------------------------------------------


def test_eval_suite_cases_at_max_length_is_accepted() -> None:
    cases = [_pass_case(f"c{i}") for i in range(200)]
    suite = EvalSuite.model_validate({"suite_id": "s", "revision": 1, "cases": cases})
    assert len(suite.cases) == 200


def test_eval_suite_cases_over_max_length_rejected() -> None:
    cases = [_pass_case(f"c{i}") for i in range(201)]
    with pytest.raises(ValidationError):
        EvalSuite.model_validate({"suite_id": "s", "revision": 1, "cases": cases})


def test_eval_run_empty_cases_returns_200_with_no_case_results() -> None:
    """長度下界 0:合法 suite 只是沒有 case,整條請求仍成功(不是錯誤輸入)。"""
    response = _run(_suite([], skill_name="triage"))
    assert response.status_code == 200
    body = response.json()
    assert body["cases"] == []
    assert body["runner_version"] == "eval-runner/1"
    assert body["suite_id"] == "csr-eval-001"
    assert body["revision"] == 1


# ---------------------------------------------------------------------------
# 單一 case 不無界執行:沿用 /skills/{name}/invoke 同一顆 settings.workflow_timeout_seconds
# 逾時預算(asyncio.wait_for);逾時只讓該 case 變 ERROR,不掛住整個請求／後續 case。
# ---------------------------------------------------------------------------


def test_eval_run_case_execution_is_bounded_by_workflow_timeout(monkeypatch) -> None:
    import asyncio

    from app.evals import runner as runner_mod

    class _SlowGraph:
        async def ainvoke(self, state, config=None):
            await asyncio.sleep(0.5)
            return {}

    class _FastGraph:
        async def ainvoke(self, state, config=None):
            # 返回期望值以通過比對
            return {"category": "SIMPLE", "answer": "今天天氣晴朗。"}

    # 逾時 case：0.05s timeout,graph 睡 0.5s → 應該 ERROR,failure_reason 含秒數
    monkeypatch.setattr(runner_mod.compiler, "compile", lambda skill, deps=None, *, cache=True: _SlowGraph())
    monkeypatch.setattr(settings, "workflow_timeout_seconds", 0.05)

    response = _run(_suite([_pass_case()]))

    assert response.status_code == 200
    case = response.json()["cases"][0]
    assert case["verdict"] == "ERROR"
    assert case["failure_reason"]  # 不是空字串
    assert "0.05" in case["failure_reason"]  # 含秒數

    # 快速 case：same timeout,graph 立即返回 → 應該 PASS
    monkeypatch.setattr(runner_mod.compiler, "compile", lambda skill, deps=None, *, cache=True: _FastGraph())
    response = _run(_suite([_pass_case()]))

    assert response.status_code == 200
    case = response.json()["cases"][0]
    assert case["verdict"] == "PASS"


# ---------------------------------------------------------------------------
# pins 正規化:null / 省略 / {} 三者視為同一 identity(.NET Web defaults 不省略 null)
# ---------------------------------------------------------------------------


def test_eval_run_pins_null_and_omitted_both_succeed_with_same_identity_as_empty() -> None:
    null_payload = _suite([_pass_case()])
    null_payload["candidate"]["pins"] = None
    null_response = _run(null_payload)

    omitted_payload = _suite([_pass_case()])
    del omitted_payload["candidate"]["pins"]
    omitted_response = _run(omitted_payload)

    empty_payload = _suite([_pass_case()])
    empty_payload["candidate"]["pins"] = {}
    empty_response = _run(empty_payload)

    for response in (null_response, omitted_response, empty_response):
        assert response.status_code == 200
        assert response.json()["cases"][0]["verdict"] == "PASS"

    identities = {
        r.json()["cases"][0]["canonical_identity"]
        for r in (null_response, omitted_response, empty_response)
    }
    assert len(identities) == 1


# ---------------------------------------------------------------------------
# Budget:on/off-point
# ---------------------------------------------------------------------------


def test_eval_run_budget_ms_zero_rejected_with_422() -> None:
    response = _run(_suite([_pass_case()], budget_ms=0))
    assert response.status_code == 422


def test_eval_run_budget_ms_exhausted_marks_remaining_cases_error() -> None:
    cases = [_pass_case(f"c{i}") for i in range(10)]
    response = _run(_suite(cases, budget_ms=1))
    assert response.status_code == 200
    results = response.json()["cases"]
    assert len(results) == 10
    # 極小預算下,真正跑完編圖＋執行的 case 數必然遠少於全部 10 筆——最後一筆一定
    # 因為預算已耗盡而未被執行。
    assert results[-1]["verdict"] == "ERROR"
    assert results[-1]["failure_reason"] == "budget exhausted"


def test_eval_run_generous_budget_does_not_exhaust() -> None:
    response = _run(_suite([_pass_case()], budget_ms=60_000))
    assert response.status_code == 200
    case = response.json()["cases"][0]
    assert case["failure_reason"] != "budget exhausted"
    assert case["verdict"] == "PASS"


# ---------------------------------------------------------------------------
# Side-effect-free:不打真實 backend HTTP
# ---------------------------------------------------------------------------


def test_eval_run_does_not_call_backend_http(monkeypatch) -> None:
    async def _forbidden(self, *args, **kwargs):
        raise AssertionError("eval 執行不應發出任何 backend HTTP 呼叫")

    monkeypatch.setattr(httpx.AsyncClient, "post", _forbidden)
    monkeypatch.setattr(httpx.AsyncClient, "get", _forbidden)

    response = _run(_suite([_pass_case()]))

    assert response.status_code == 200
    assert response.json()["cases"][0]["verdict"] == "PASS"


# ---------------------------------------------------------------------------
# Side-effect-free:write_evidence 工具即使全域旗標開啟仍 fail closed
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_write_evidence_tool_fails_closed_with_fixture_deps(monkeypatch) -> None:
    monkeypatch.setattr(settings, "agent_write_tools_enabled", True)
    monkeypatch.setattr(
        settings, "agent_write_tools_allowlist", "runtime.write_evidence"
    )
    monkeypatch.setattr(settings, "agent_write_tools_tenant_allowlist", "demo-a")

    deps = build_fixture_deps({})
    ctx = ToolContext(
        tenant_id="demo-a", user_id="u", role="ADMIN", deps=deps, effect_id="eff-1"
    )

    with pytest.raises(WriteEvidenceDenied):
        await write_evidence(ctx, record_id="rec-1", value="v")
