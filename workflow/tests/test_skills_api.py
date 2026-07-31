"""/skills* 端點測試:清單、無副作用的驗證、invoke 的錯誤碼順序。

刻意寫成新檔而不動 test_api.py：既有 /workflows 的測試矩陣是回歸底線（AT-REG-01），
一行都不改。這裡只驗證新端點的形狀，並確認 skill 的 404/403/422/504/500
與 /workflows/{name}/invoke 對同一情境的回應碼逐一相同（AT4-10 的 workflow 端）。
"""

import asyncio

import pytest
from fastapi.testclient import TestClient

from app import skills
from app.engine.skill import Skill
from app.main import app
from app.settings import settings
from app.skills import custom
from tests.conftest import auth_headers as _headers

client = TestClient(app)


VALID_YAML = """
name: probe-skill
description: 驗證測試用
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - node: query_intake@1.0
"""


# ---------------------------------------------------------------------------
# GET /skills
# ---------------------------------------------------------------------------


def test_list_skills_returns_builtin_kb_query():
    resp = client.get("/skills", headers=_headers())

    assert resp.status_code == 200
    body = {item["name"]: item for item in resp.json()}
    assert "kb-query" in body
    item = body["kb-query"]
    assert item["source"] == "builtin"
    assert item["bindable"] is False
    assert item["required_role"] == "USER"
    assert item["description"]
    assert isinstance(item["revision"], int)


def test_builtin_catalog_declares_flow_kind():
    body = {item["name"]: item for item in client.get("/skills", headers=_headers()).json()}
    assert body["kb-query"]["kind"] == "flow"
    assert body["template-compare"]["kind"] == "flow"


def test_skills_endpoints_require_internal_token():
    """服務間標頭要求：缺 token → 401，缺租戶標頭 → 400。"""
    assert client.get("/skills").status_code == 401
    assert client.get("/skills", headers=_headers(tenant_id=None)).status_code == 400


@pytest.mark.parametrize("token", [None, "wrong-token"], ids=["missing", "wrong"])
def test_skills_endpoint_bad_token_returns_401(token):
    """承接已刪除的 test_api.py::test_list_workflows_bad_token_returns_401（換到 /skills）。"""
    resp = client.get("/skills", headers=_headers(token=token))
    assert resp.status_code == 401
    assert resp.json()["detail"]["error"] == "unauthorized"


def test_skills_endpoint_missing_role_returns_400():
    """承接已刪除的 test_api.py::test_list_workflows_missing_role_returns_400。"""
    resp = client.get("/skills", headers=_headers(role=None))
    assert resp.status_code == 400
    assert resp.json()["detail"]["error"] == "missing_context"


# ---------------------------------------------------------------------------
# POST /skills/validate（AT4-11：無副作用）
# ---------------------------------------------------------------------------


def test_validate_valid_definition():
    resp = client.post(
        "/skills/validate", json={"definition": VALID_YAML}, headers=_headers()
    )

    assert resp.status_code == 200
    # P4：驗證通過時一併回 skill 中繼資料（backend 寫入 DB 的唯一資料來源），
    # 因此不再是 {valid, errors} 兩鍵而已；中繼資料的逐鍵比對見 test_skills_custom.py。
    body = resp.json()
    assert body["valid"] is True
    assert body["errors"] == []
    assert body["skill"]["name"] == "probe-skill"
    assert body["skill"]["kind"] == "flow"


def test_business_workflow_validate_alias_matches_legacy_path():
    payload = {"definition": VALID_YAML}

    legacy = client.post("/skills/validate", json=payload, headers=_headers())
    named = client.post(
        "/business-workflows/validate", json=payload, headers=_headers()
    )

    assert named.status_code == legacy.status_code == 200
    assert named.json() == legacy.json()


def test_validate_reports_error_codes_in_body():
    resp = client.post(
        "/skills/validate",
        json={"definition": "name: probe-skill\nflow:\n  - node: no_such_node\n"},
        headers=_headers(),
    )

    assert resp.status_code == 200  # 驗證失敗是 body 的事，不是 HTTP 錯誤
    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "unknown_node"
    assert body["errors"][0]["message"]


def test_validate_bad_yaml_does_not_raise():
    """【AT2-07】壞 YAML 也走 {valid:false, errors:[invalid_flow]}，不是 500。"""
    resp = client.post(
        "/skills/validate",
        json={"definition": 'name: probe\nflow:\n  - node: "unclosed\n'},
        headers=_headers(),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "invalid_flow"


def test_validate_has_no_side_effects():
    """【AT4-11】連續驗證三次不得改變 GET /skills 清單，也不得註冊任何 skill。"""
    before = client.get("/skills", headers=_headers()).json()

    for definition in (VALID_YAML, "flow: []", VALID_YAML):
        client.post(
            "/skills/validate", json={"definition": definition}, headers=_headers()
        )

    after = client.get("/skills", headers=_headers()).json()
    assert after == before
    assert skills.get("probe-skill") is None


# ---------------------------------------------------------------------------
# POST /skills/{name}/invoke：驗證順序 404 → 403 → 422 → 504 / 500
# ---------------------------------------------------------------------------


def test_invoke_unknown_skill_returns_404(monkeypatch):
    """【R6】內建查無 + 自訂 loader 正常回 None → 404。

    正式 main.py 的判定順序是「內建 → 自訂 → 都沒有才 404」;此測試釘住第二步「自訂
    loader 回 None」的分流,因此手寫 fake 覆蓋 custom.load,不打真實 backend。R6 的
    另一半(BackendUnavailable → 500)由下一個測試獨立覆蓋,兩案不共用 fake 也不互相
    依賴,才能各自釘死一條錯誤路徑。
    """

    async def _fake_load(name, ctx):
        return None

    monkeypatch.setattr(custom, "load", _fake_load)

    resp = client.post(
        "/skills/nope/invoke", json={"input": {"query": "x"}}, headers=_headers()
    )

    assert resp.status_code == 404
    assert resp.json()["detail"]["error"] == "workflow_not_found"


def test_invoke_backend_unavailable_returns_500(monkeypatch):
    """【R6】自訂 loader 拋 BackendUnavailable → 500 workflow_execution_failed。

    這是 main.py invoke_skill 內註解「取不到定義」與「skill 不存在」是兩件事的護欄:
    後者是 404,前者必須是受控的 500,不能被吞成 404 讓呼叫端誤刪 skill。與上一個
    404 測試分開寫,是為了兩條錯誤路徑各自可獨立紅/綠。
    """

    async def _boom_load(name, ctx):
        raise custom.BackendUnavailable("backend down")

    monkeypatch.setattr(custom, "load", _boom_load)

    resp = client.post(
        "/skills/nope/invoke", json={"input": {"query": "x"}}, headers=_headers()
    )

    assert resp.status_code == 500
    assert resp.json()["detail"]["error"] == "workflow_execution_failed"


@pytest.mark.parametrize(
    "input_body",
    [{}, {"query": ""}],
    ids=["missing-query", "blank-query"],
)
def test_invoke_invalid_input_returns_422(input_body):
    """input_schema 的 required / min_length 由動態 Pydantic model 執行（對齊 KbQueryInput）。"""
    resp = client.post(
        "/skills/kb-query/invoke", json={"input": input_body}, headers=_headers()
    )

    assert resp.status_code == 422
    assert resp.json()["detail"]["error"] == "workflow_input_invalid"


def test_invoke_422_body_is_humanized_field_errors():
    """B1-py: 422 body 為人話化的 field_errors,不外洩 pydantic 原文/URL/model 名。"""
    resp = client.post(
        "/skills/kb-query/invoke", json={"input": {}}, headers=_headers()
    )

    assert resp.status_code == 422
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_input_invalid"
    assert detail["message"] == "輸入資料有 1 個欄位需要修正"
    assert "query" in detail["field_errors"]
    assert "必填" in detail["field_errors"]["query"]
    # 紅線(§6-4):不得含 pydantic model 名／validation error 原文／errors.pydantic.dev URL
    blob = str(detail).lower()
    assert "pydantic" not in blob
    assert "validation error for" not in blob
    assert "errors.pydantic.dev" not in blob
    assert "kbqueryinput" not in blob


def test_invoke_admin_skill_forbidden_for_user_role():
    """required_role: ADMIN 的 skill 被 USER 呼叫 → 403（順序上先於 422：input 是空的）。"""
    loaded = skills.get("kb-query")
    admin_skill = loaded.skill.model_copy(update={"required_role": "ADMIN"})
    skills._SKILLS["__admin-probe__"] = loaded.__class__(
        skill=admin_skill.model_copy(update={"name": "admin-probe"}),
        graph=loaded.graph,
        input_model=loaded.input_model,
        deps=loaded.deps,
    )
    try:
        resp = client.post(
            "/skills/__admin-probe__/invoke", json={"input": {}}, headers=_headers()
        )
        assert resp.status_code == 403
        assert resp.json()["detail"]["error"] == "workflow_forbidden"

        ok = client.post(
            "/skills/__admin-probe__/invoke",
            json={"input": {}},
            headers=_headers(role="ADMIN"),
        )
        assert ok.status_code == 422  # ADMIN 過了角色關卡，才輪到 input 驗證
    finally:
        skills._SKILLS.pop("__admin-probe__", None)


def test_agentic_invoke_timeout_returns_504(monkeypatch):
    """執行超過逾時上限 → 504 workflow_timeout（與 /workflows/{name}/invoke 同碼）。"""
    original = skills.get("kb-query")

    class _SlowGraph:
        async def ainvoke(self, state, config=None):
            await asyncio.sleep(0.5)
            return {}

    skills._SKILLS["__slow-probe__"] = original.__class__(
        skill=Skill.model_validate({"name": "slow-probe", "flow": [{"node": "t"}]}),
        graph=_SlowGraph(),
        input_model=None,
        deps=None,
    )
    monkeypatch.setattr(settings, "workflow_timeout_seconds", 0.05)
    try:
        resp = client.post(
            "/skills/__slow-probe__/invoke", json={"input": {}}, headers=_headers()
        )
        assert resp.status_code == 504
        assert resp.json()["detail"]["error"] == "workflow_timeout"
    finally:
        skills._SKILLS.pop("__slow-probe__", None)


def test_agentic_invoke_unexpected_exception_returns_500():
    original = skills.get("kb-query")

    class _BoomGraph:
        async def ainvoke(self, state, config=None):
            raise RuntimeError("boom")

    skills._SKILLS["__boom-probe__"] = original.__class__(
        skill=Skill.model_validate({"name": "boom-probe", "flow": [{"node": "t"}]}),
        graph=_BoomGraph(),
        input_model=None,
        deps=None,
    )
    try:
        resp = client.post(
            "/skills/__boom-probe__/invoke", json={"input": {}}, headers=_headers()
        )
        assert resp.status_code == 500
        assert resp.json()["detail"]["error"] == "workflow_execution_failed"
    finally:
        skills._SKILLS.pop("__boom-probe__", None)


def test_invoke_reserved_input_keys_cannot_override_caller_tenant(monkeypatch):
    """保留鍵剝除沿用 /workflows 的 _RESERVED_INPUT_KEYS：input 夾帶 tenant_id 無效。"""
    captured: dict = {}
    original = skills.get("kb-query")

    class _CaptureGraph:
        async def ainvoke(self, state, config=None):
            captured.update(state)
            return {**state, "__loop_0_count": 1}

    skills._SKILLS["__capture-probe__"] = original.__class__(
        skill=Skill.model_validate({"name": "capture-probe", "flow": [{"node": "t"}]}),
        graph=_CaptureGraph(),
        input_model=None,
        deps=None,
    )
    try:
        resp = client.post(
            "/skills/__capture-probe__/invoke",
            json={"input": {"query": "x", "tenant_id": "evil-tenant", "role": "ADMIN"}},
            headers=_headers(tenant_id="demo-a", user_id="alice", role="USER"),
        )

        assert resp.status_code == 200
        # 身分鍵一律以呼叫者真實 ctx seed；input 偽造的同名值一律無效（防偽保證等價，
        # 只是從「role 不存在」改成「role/user_id 等於呼叫者真值」）。
        assert captured["tenant_id"] == "demo-a"
        assert captured["role"] == "USER"  # 非 input 偽造的 "ADMIN"
        assert captured["user_id"] == "alice"
        # 引擎內部鍵不外洩到 API 回應
        assert "__loop_0_count" not in resp.json()["output"]
    finally:
        skills._SKILLS.pop("__capture-probe__", None)


def test_invoke_cannot_forge_reserved_audit_keys():
    """保留鍵不可經 input 偽造：query_id / original_query / query_timestamp 一律剝除。

    Harness 的 IMMUTABLE_KEYS 只擋「節點」寫入，擋不住 input。手寫 kb_query 圖沒事
    是因為第一個節點固定是 query_intake（無條件覆寫這三鍵），但 Skill 的 flow 是任意的：
    這裡刻意用一個沒有 query_intake 的 flow，若 input 沒被剝乾淨，呼叫端就能決定
    稽核軌跡上的 query_id 與「原始問題」文字。
    """
    from app.engine import compiler
    from tests.kbquery_fakes import make_deps

    deps = make_deps({})
    skill = Skill.model_validate(
        {
            "name": "forge-probe",
            "input_schema": {"query": {"type": "str", "required": True, "min_length": 1}},
            "flow": [{"node": "answer_composer@1.0"}],  # 刻意不含 query_intake
        }
    )
    original = skills.get("kb-query")
    skills._SKILLS["__forge-probe__"] = original.__class__(
        skill=skill,
        graph=compiler.compile(skill, deps),
        input_model=None,
        deps=deps,
        recursion_limit=compiler.recursion_limit(skill),
    )
    try:
        resp = client.post(
            "/skills/__forge-probe__/invoke",
            json={
                "input": {
                    "query": "真正的問題",
                    "query_id": "FORGED-ID",
                    "original_query": "無害的問題",
                    "query_timestamp": "1999-01-01T00:00:00Z",
                    "__loop_0_count": 99,
                }
            },
            headers=_headers(),
        )

        assert resp.status_code == 200
        trail = deps.audit_repo.saved[0]
        assert trail.query_id != "FORGED-ID"
        assert trail.original_query != "無害的問題"
        assert trail.query_timestamp != "1999-01-01T00:00:00Z"
        output = resp.json()["output"]
        assert output.get("query_id") != "FORGED-ID"
        assert output.get("original_query") != "無害的問題"
    finally:
        skills._SKILLS.pop("__forge-probe__", None)


def test_invoke_kb_query_definition_happy_path_returns_output():
    """完整走一次 API → 編譯圖 → 稽核落地。

    用假依賴編譯的 kb_query 定義掛成 probe skill：測試不打網路、不用 LLM
    （正式 deps 會連 LiteLLM）。這裡驗的是 API 與引擎的接線，圖本身的行為由
    test_skill_kbquery_parity_e2e.py 負責。
    """
    from app.engine import compiler
    from tests.kbquery_fakes import TEXT_2025Q3, FakeSearch, make_deps

    original = skills.get("kb-query")
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    skills._SKILLS["__kb_probe__"] = original.__class__(
        skill=original.skill,
        graph=compiler.compile(original.skill, deps),
        input_model=original.input_model,
        deps=deps,
    )
    try:
        resp = client.post(
            "/skills/__kb_probe__/invoke",
            json={"input": {"query": "2025Q3 稅後淨利是多少？"}},
            headers=_headers(),
        )

        assert resp.status_code == 200
        body = resp.json()
        assert body["skill"] == "__kb_probe__"
        assert body["output"]["answer_mode"] == "ANSWER"
        assert "1,234" in body["output"]["final_answer"]
        assert "trace" not in body["output"]
        assert len(deps.audit_repo.saved) == 1
        assert not any(k.startswith("__") for k in body["output"])
    finally:
        skills._SKILLS.pop("__kb_probe__", None)


def test_invoke_seeds_tool_context_with_caller_identity():
    """正向：invoke 以呼叫者身分頭 seed state → tool 的 ToolContext 拿到真實 role/user_id。

    註冊一個只記錄自己收到的 ToolContext 的臨時 tool，掛進 flow 的 tool 步驟，
    以特定身分頭 invoke，斷言 tool 收到的 role/user_id/tenant_id 等於呼叫者真值。
    """
    from app.engine import compiler, tool_registry
    from tests.kbquery_fakes import make_deps

    captured: dict = {}

    async def _probe(ctx, **args):
        captured.update(role=ctx.role, user_id=ctx.user_id, tenant_id=ctx.tenant_id)
        return "ok"

    tool_registry._REGISTRY["__ctx_probe_tool__"] = tool_registry.ToolSpec(
        name="__ctx_probe_tool__",
        kind="local",
        description="記錄 ToolContext",
        args_schema={},
        returns="str",
        fn=_probe,
    )
    deps = make_deps({})
    skill = Skill.model_validate(
        {
            "name": "ctx-probe",
            "flow": [{"tool": "__ctx_probe_tool__", "save_as": "probe_result"}],
        }
    )
    original = skills.get("kb-query")
    skills._SKILLS["__ctx_probe__"] = original.__class__(
        skill=skill,
        graph=compiler.compile(skill, deps),
        input_model=None,
        deps=deps,
        recursion_limit=compiler.recursion_limit(skill),
    )
    try:
        resp = client.post(
            "/skills/__ctx_probe__/invoke",
            json={"input": {}},
            headers=_headers(tenant_id="demo-a", user_id="alice", role="ADMIN"),
        )

        assert resp.status_code == 200
        assert captured == {"role": "ADMIN", "user_id": "alice", "tenant_id": "demo-a"}
    finally:
        skills._SKILLS.pop("__ctx_probe__", None)
        tool_registry._REGISTRY.pop("__ctx_probe_tool__", None)
