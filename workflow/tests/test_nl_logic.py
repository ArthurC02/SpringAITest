"""P3 nl_logic 節點測試（SSR-P3-001 ~ 006）。

- 單元:以手寫 recording fake LLM 驗 instruction→system、input_keys→user 組裝、
  空 input_keys 的 normalized_query→query→空字串 fallback、None→空字串。
- Harness 整合:output_key 非 business_result 被靜態 writes 剝除(印證 v1 動態 writes 未開放)。
- 註冊/trace:GET /nodes 含 nl_logic@1.0;含 nl_logic 的 skill invoke → business_result +
  trace 有該節點 + 非空 component_version(LLM 版本)。

無 pytest-asyncio,async 節點統一以 asyncio.run 執行(對齊 test_retrieve.py 慣例)。
縫⑥ 真護欄在 app/skills/__init__.py 的 `nl_logic`/`retrieve` import:拿掉它 →
`import app.skills` 於 _load_builtin 編譯 template_* 時遇 unknown node 崩、本檔與
test_skill_templates.py 一併轉紅。main.py 的 `from app.nodes import nl_logic` 只是
GET /nodes catalog 顯式化的 defence-in-depth,非唯一觸發點——app.skills 單獨匯入即
自足註冊,拔掉 main.py 那行本檔仍綠(勿以它做假綠檢核)。
"""

import asyncio

import pytest
import yaml
from fastapi.testclient import TestClient

from app.engine import compiler
from app.engine import skill as skill_mod
from app.engine.node_registry import get as get_node
from app.main import app
from app.nodes.nl_logic import _NlLogicOutput, make_nl_logic_node
from tests.conftest import auth_headers
from tests.kbquery_fakes import FakeStructuredLLM, make_deps

client = TestClient(app)


class RecordingLLM:
    """手寫 fake:記錄每次 structured 的 system/user,依 result 決定回傳或 None。"""

    version = "rec-llm-v1"

    def __init__(self, result="OUT"):
        self.result = result
        self.calls: list[dict] = []

    async def structured(self, system, user, schema):
        self.calls.append({"system": system, "user": user, "schema": schema})
        if self.result is None:
            return None
        return schema(result=self.result)


# ---------------------------------------------------------------------------
# SSR-P3-002:instruction→system、input_keys→user 依序 k: repr(value)
# ---------------------------------------------------------------------------


def test_nl_logic_sends_instruction_as_system_and_input_keys_as_user():
    llm = RecordingLLM(result="推論結果")
    node = make_nl_logic_node(llm, instruction="這是規則", input_keys=["a", "b"])

    out = asyncio.run(node({"a": 1, "b": "x", "unused": "z"}))

    assert out == {"business_result": "推論結果"}
    call = llm.calls[0]
    assert call["system"] == "這是規則"
    # 依序組裝、值以 repr 呈現;未列於 input_keys 的鍵不進 user
    assert call["user"] == f"a: {1!r}\nb: {'x'!r}"
    assert "unused" not in call["user"]
    assert call["schema"] is _NlLogicOutput


# ---------------------------------------------------------------------------
# SSR-P3-003:input_keys 空 → normalized_query → query → 空字串(on/off point)
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "state, expected_user",
    [
        ({"normalized_query": "NQ", "query": "Q"}, "NQ"),  # 兩者皆有 → 取 normalized_query
        ({"query": "Q"}, "Q"),  # 只有 query → 取 query
        ({}, ""),  # 兩者皆無 → 空字串
        ({"normalized_query": None, "query": "Q"}, "Q"),  # None 不算值 → 退到 query
        ({"normalized_query": "", "query": ""}, ""),  # 皆空字串 → 空字串
    ],
    ids=["both", "query-only", "neither", "nq-none", "both-empty"],
)
def test_nl_logic_empty_input_keys_fallback_priority(state, expected_user):
    llm = RecordingLLM(result="R")
    node = make_nl_logic_node(llm, instruction="rule")  # input_keys 預設空

    asyncio.run(node(dict(state)))

    assert llm.calls[0]["user"] == expected_user


# ---------------------------------------------------------------------------
# SSR-P3-004:有結果 → 字串;None → 空字串
# ---------------------------------------------------------------------------


def test_nl_logic_returns_result_string_when_llm_answers():
    node = make_nl_logic_node(RecordingLLM(result="答案"), instruction="r")
    assert asyncio.run(node({"query": "q"})) == {"business_result": "答案"}


def test_nl_logic_returns_empty_string_when_llm_returns_none():
    node = make_nl_logic_node(RecordingLLM(result=None), instruction="r")
    assert asyncio.run(node({"query": "q"})) == {"business_result": ""}


# ---------------------------------------------------------------------------
# SSR-P3-005:output_key on/off point —— Harness 依靜態 writes 剝除
# ---------------------------------------------------------------------------


def _compile_nl_probe(output_key: str):
    llm = FakeStructuredLLM(outputs={_NlLogicOutput: _NlLogicOutput(result="V")})
    deps = make_deps({}, llm=llm)
    definition = yaml.safe_dump(
        {
            "name": "nl-probe",
            "input_schema": {"query": {"type": "str", "required": True, "min_length": 1}},
            "flow": [
                {"node": "nl_logic@1.0", "params": {"instruction": "x", "output_key": output_key}}
            ],
        },
        allow_unicode=True,
    )
    skill = skill_mod.parse_source(definition)
    graph = compiler.compile(skill, deps)
    return compiler.public_output(asyncio.run(graph.ainvoke({"query": "q", "tenant_id": "t"})))


def test_nl_logic_output_key_business_result_is_kept():
    """on-point:output_key == business_result(宣告過的 writes)→ 保留。"""
    out = _compile_nl_probe("business_result")
    assert out["business_result"] == "V"


def test_nl_logic_output_key_other_is_stripped_by_harness():
    """off-point:output_key == other(未宣告)→ 被 Harness 剝除,證明 v1 動態 writes 未開放。"""
    out = _compile_nl_probe("other")
    assert "other" not in out
    assert "business_result" not in out


# ---------------------------------------------------------------------------
# SSR-P3-001:節點註冊 + validator/compiler 找得到
# ---------------------------------------------------------------------------


def test_nodes_catalog_lists_nl_logic():
    resp = client.get("/nodes", headers=auth_headers())
    assert resp.status_code == 200
    by_name = {(n["name"], n["version"]): n for n in resp.json()}
    assert ("nl_logic", "1.0") in by_name
    spec = by_name[("nl_logic", "1.0")]
    assert spec["writes"] == ["business_result"]
    assert spec["reads"] == []


def test_nl_logic_skill_validates_and_compiles():
    spec = get_node("nl_logic", "1.0")
    assert spec is not None
    definition = (
        "name: nl-validate-probe\n"
        "input_schema:\n"
        "  query: {type: str, required: true, min_length: 1}\n"
        "flow:\n"
        "  - node: nl_logic@1.0\n"
        "    params:\n"
        "      instruction: rule\n"
    )
    resp = client.post("/skills/validate", json={"definition": definition}, headers=auth_headers())
    assert resp.status_code == 200
    assert resp.json()["valid"] is True


# ---------------------------------------------------------------------------
# SSR-P3-006:含 nl_logic 的完整 skill invoke → business_result + trace + component_version
# ---------------------------------------------------------------------------


def test_nl_logic_end_to_end_trace_has_component_version():
    from app import skills

    llm = FakeStructuredLLM(outputs={_NlLogicOutput: _NlLogicOutput(result="最終")})
    deps = make_deps({}, llm=llm)
    definition = (
        "name: nl-e2e-probe\n"
        "input_schema:\n"
        "  query: {type: str, required: true, min_length: 1}\n"
        "flow:\n"
        "  - node: nl_logic@1.0\n"
        "    params:\n"
        "      instruction: 綜合回答\n"
    )
    skill = skill_mod.parse_source(definition)
    original = skills.get("kb-query")
    skills._SKILLS["__nl_e2e__"] = original.__class__(
        skill=skill,
        graph=compiler.compile(skill, deps),
        input_model=skill_mod.build_input_model(skill),
        deps=deps,
        recursion_limit=compiler.recursion_limit(skill),
    )
    try:
        resp = client.post(
            "/skills/__nl_e2e__/invoke",
            json={"input": {"query": "問題"}},
            headers=auth_headers(),
        )
        assert resp.status_code == 200
        output = resp.json()["output"]
        assert output["business_result"] == "最終"
        nl_entries = [t for t in output["trace"] if t["node_name"] == "nl_logic"]
        assert len(nl_entries) == 1
        assert nl_entries[0]["status"] == "ok"
        # deps 含 llm → compiler 在 trace 記 LLM 版本(非空)
        assert nl_entries[0]["component_version"] == "fake-llm-v1"
    finally:
        skills._SKILLS.pop("__nl_e2e__", None)
