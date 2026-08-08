"""summarize_text 節點 + 編譯後的 app/skills/summarize.yaml。"""

import asyncio
import dataclasses

from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.main import app
from app.nodes.summarize_text import _SummarizeOutput, make_summarize_text_node
from tests.conftest import auth_headers
from tests.kbquery_fakes import FakeStructuredLLM, RecordingLLM, make_deps

client = TestClient(app)

# ---------------------------------------------------------------------------
# 節點單元測試：prompt 逐字對照 workflows/summarize.py::summarize
# ---------------------------------------------------------------------------


def test_summarize_text_sends_expected_prompt_and_returns_summary():
    llm = RecordingLLM(output=_SummarizeOutput(summary="三句摘要"))
    node = make_summarize_text_node(llm)

    out = asyncio.run(node({"text": "很長的原文……"}))

    assert out == {"summary": "三句摘要"}
    call = llm.calls[0]
    assert call["system"] == "你是摘要助手，將輸入濃縮成三句以內的繁體中文摘要。"
    assert call["user"] == "很長的原文……"
    assert call["schema"] is _SummarizeOutput


def test_summarize_text_returns_empty_string_when_llm_returns_none():
    node = make_summarize_text_node(RecordingLLM(output=None))

    out = asyncio.run(node({"text": "原文"}))

    assert out == {"summary": ""}


# ---------------------------------------------------------------------------
# 編譯後的引擎路徑
# ---------------------------------------------------------------------------


def test_summarize_skill_cold_start_compiles():
    loaded = skills.get("summarize")
    assert loaded is not None
    assert loaded.source == "builtin"
    assert loaded.skill.required_role == "USER"
    assert set(loaded.skill.input_schema) == {"text"}


def test_summarize_skill_invoke_produces_summary():
    llm = FakeStructuredLLM(outputs={_SummarizeOutput: _SummarizeOutput(summary="濃縮摘要")})
    deps = make_deps({}, llm=llm)
    skill = skills.get("summarize").skill
    graph = compiler.compile(skill, deps)

    out = asyncio.run(graph.ainvoke({"tenant_id": "t", "text": "很長很長的原文"}))
    out = compiler.public_output(out)

    assert out["summary"] == "濃縮摘要"
    assert out["trace"][-1].node_name == "audit_feedback"


# ---------------------------------------------------------------------------
# API 級：POST /skills/summarize/invoke 的 text min_length: 1 邊界兩側
# ---------------------------------------------------------------------------


def test_summarize_invoke_api_level_rejects_blank_text():
    resp = client.post(
        "/skills/summarize/invoke", json={"input": {"text": ""}}, headers=auth_headers()
    )
    assert resp.status_code == 422
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_input_invalid"
    assert detail["field_errors"] == {"text": "「text」至少需 1 個字。"}


def test_summarize_invoke_api_level_accepts_single_character_text():
    original = skills.get("summarize")
    llm = FakeStructuredLLM(outputs={_SummarizeOutput: _SummarizeOutput(summary="一字摘要")})
    deps = make_deps({}, llm=llm)
    skills._SKILLS["summarize"] = dataclasses.replace(
        original, graph=compiler.compile(original.skill, deps), deps=deps
    )
    try:
        resp = client.post(
            "/skills/summarize/invoke",
            json={"input": {"text": "x"}},
            headers=auth_headers(),
        )

        assert resp.status_code == 200
        body = resp.json()
        assert body["skill"] == "summarize"
        assert body["output"]["summary"] == "一字摘要"
    finally:
        skills._SKILLS["summarize"] = original
