"""Node-First 遷移（Phase 1）：triage_* 節點 + 編譯後的 app/skills/triage.yaml。

語意逐字移植自 app/workflows/triage.py（手寫圖已於 Phase 3a 退役刪除）。手寫圖把「非標準輸出 → 預設 simple」放在 route()（條件邊），這裡改到
triage_classify 節點內就正規化，因此邊界案例改對 triage_classify 的輸出斷言，
決策表語意（含 COMPLEX 字樣 → complex；其餘含空字串 → simple）與 test_triage.py 一致。
"""

import asyncio
import dataclasses
from contextlib import contextmanager

import pytest
from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.main import app
from app.nodes.triage import (
    _TriageAnswerOutput,
    _TriageClassifyOutput,
    make_triage_classify_node,
    make_triage_deep_answer_node,
    make_triage_quick_answer_node,
)
from tests.conftest import auth_headers, invoke_builtin
from tests.kbquery_fakes import FakeStructuredLLM, RecordingLLM, make_deps

client = TestClient(app)


# ---------------------------------------------------------------------------
# triage_classify：正規化決策表（對照 test_triage.py::test_route_decision_table）
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("raw_category", "expected"),
    [
        ("這題很 complex", "COMPLEX"),  # 夾雜其他文字且小寫，仍應判為 COMPLEX
        ("無法判斷", "SIMPLE"),  # 不含 COMPLEX 字樣 → 預設 SIMPLE
        ("", "SIMPLE"),  # 空字串 → 預設 SIMPLE
    ],
    ids=["contains-complex", "unrecognized", "empty"],
)
def test_triage_classify_normalizes_category(raw_category, expected):
    llm = RecordingLLM(output=_TriageClassifyOutput(category=raw_category))
    node = make_triage_classify_node(llm)

    out = asyncio.run(node({"question": "q"}))

    assert out == {"category": expected}
    call = llm.calls[0]
    assert call["system"] == (
        "判斷使用者問題是簡單(SIMPLE)還是複雜(COMPLEX)，只回覆 SIMPLE 或 COMPLEX 一個詞"
    )
    assert call["user"] == "q"
    assert call["schema"] is _TriageClassifyOutput


def test_triage_classify_defaults_to_simple_when_llm_returns_none():
    node = make_triage_classify_node(RecordingLLM(output=None))

    out = asyncio.run(node({"question": "q"}))

    assert out == {"category": "SIMPLE"}


def test_triage_quick_answer_sends_expected_prompt():
    llm = RecordingLLM(output=_TriageAnswerOutput(answer="一句話答案"))
    node = make_triage_quick_answer_node(llm)

    out = asyncio.run(node({"question": "問題"}))

    assert out == {"answer": "一句話答案"}
    assert llm.calls[0]["system"] == "請用一句話簡潔回答使用者的問題。"
    assert llm.calls[0]["user"] == "問題"


def test_triage_deep_answer_sends_expected_prompt():
    llm = RecordingLLM(output=_TriageAnswerOutput(answer="詳盡答案"))
    node = make_triage_deep_answer_node(llm)

    out = asyncio.run(node({"question": "問題"}))

    assert out == {"answer": "詳盡答案"}
    assert llm.calls[0]["system"] == "請逐步、詳盡地回答使用者的問題。"


# ---------------------------------------------------------------------------
# 編譯後的引擎路徑：branch 兩條分支都要覆蓋
# ---------------------------------------------------------------------------


def _invoke(deps, **state) -> dict:
    return invoke_builtin("triage", deps, **state)


def test_triage_skill_cold_start_compiles():
    loaded = skills.get("triage")
    assert loaded is not None
    assert loaded.source == "builtin"
    assert loaded.skill.required_role == "USER"
    assert set(loaded.skill.input_schema) == {"question"}


def test_triage_skill_simple_branch_runs_quick_answer_only():
    llm = FakeStructuredLLM(
        outputs={
            _TriageClassifyOutput: _TriageClassifyOutput(category="不知道"),
            _TriageAnswerOutput: _TriageAnswerOutput(answer="簡答"),
        }
    )
    out = _invoke(make_deps({}, llm=llm), question="今天星期幾？")

    assert out["category"] == "SIMPLE"
    assert out["answer"] == "簡答"
    node_names = {t.node_name for t in out["trace"]}
    assert "triage_quick_answer" in node_names
    assert "triage_deep_answer" not in node_names
    assert out["trace"][-1].node_name == "audit_feedback"


def test_triage_skill_complex_branch_runs_deep_answer_only():
    llm = FakeStructuredLLM(
        outputs={
            _TriageClassifyOutput: _TriageClassifyOutput(category="COMPLEX"),
            _TriageAnswerOutput: _TriageAnswerOutput(answer="深答"),
        }
    )
    out = _invoke(make_deps({}, llm=llm), question="請證明費馬最後定理")

    assert out["category"] == "COMPLEX"
    assert out["answer"] == "深答"
    node_names = {t.node_name for t in out["trace"]}
    assert "triage_deep_answer" in node_names
    assert "triage_quick_answer" not in node_names


# ---------------------------------------------------------------------------
# API 級：POST /skills/triage/invoke 走通（mock LLM）
# ---------------------------------------------------------------------------


def test_triage_invoke_api_level_simple_branch():
    original = skills.get("triage")
    llm = FakeStructuredLLM(
        outputs={
            _TriageClassifyOutput: _TriageClassifyOutput(category="SIMPLE"),
            _TriageAnswerOutput: _TriageAnswerOutput(answer="API 簡答"),
        }
    )
    deps = make_deps({}, llm=llm)
    skills._SKILLS["triage"] = original.__class__(
        skill=original.skill,
        graph=compiler.compile(original.skill, deps),
        input_model=original.input_model,
        deps=deps,
        recursion_limit=original.recursion_limit,
        source=original.source,
        definition=original.definition,
    )
    try:
        resp = client.post(
            "/skills/triage/invoke",
            json={"input": {"question": "今天天氣如何？"}},
            headers=auth_headers(),
        )

        assert resp.status_code == 200
        body = resp.json()
        assert body["skill"] == "triage"
        assert body["output"]["category"] == "SIMPLE"
        assert body["output"]["answer"] == "API 簡答"
    finally:
        skills._SKILLS["triage"] = original


@contextmanager
def _triage_with_fake_llm(llm):
    """暫時把已載入的 triage 換成假 LLM 編出的圖（API 級測試不打真 LLM），離開時還原。

    只換 graph/deps，definition 與 definition_sha256 原樣保留 —— flow 治理包裝會比對
    定義雜湊，換掉會變成 definition hash mismatch 而不是真的走完圖。
    """
    original = skills.get("triage")
    deps = make_deps({}, llm=llm)
    skills._SKILLS["triage"] = dataclasses.replace(
        original, graph=compiler.compile(original.skill, deps), deps=deps
    )
    try:
        yield
    finally:
        skills._SKILLS["triage"] = original


def test_triage_invoke_api_level_complex_branch():
    llm = FakeStructuredLLM(
        outputs={
            _TriageClassifyOutput: _TriageClassifyOutput(category="COMPLEX"),
            _TriageAnswerOutput: _TriageAnswerOutput(answer="API 深答"),
        }
    )
    with _triage_with_fake_llm(llm):
        resp = client.post(
            "/skills/triage/invoke",
            json={"input": {"question": "請推導廣義相對論場方程"}},
            headers=auth_headers(),
        )

    assert resp.status_code == 200
    body = resp.json()
    assert body["skill"] == "triage"
    assert body["output"]["category"] == "COMPLEX"
    assert body["output"]["answer"] == "API 深答"
    # trace 是引擎鍵，對外輸出一律剝除（兩條分支皆然）
    assert "trace" not in body["output"]


def test_triage_invoke_api_level_accepts_one_char_question():
    """min_length=1 的合法邊界：恰好一個字的 question 要通過驗證並跑完圖（對照下方空字串 422）。"""
    llm = FakeStructuredLLM(
        outputs={
            _TriageClassifyOutput: _TriageClassifyOutput(category="SIMPLE"),
            _TriageAnswerOutput: _TriageAnswerOutput(answer="一個字也答得出來"),
        }
    )
    with _triage_with_fake_llm(llm):
        resp = client.post(
            "/skills/triage/invoke",
            json={"input": {"question": "嗨"}},
            headers=auth_headers(),
        )

    assert resp.status_code == 200
    body = resp.json()
    assert body["output"]["category"] == "SIMPLE"
    assert body["output"]["answer"] == "一個字也答得出來"


def test_triage_invoke_api_level_rejects_blank_question():
    resp = client.post(
        "/skills/triage/invoke", json={"input": {"question": ""}}, headers=auth_headers()
    )
    assert resp.status_code == 422
    assert resp.json()["detail"]["error"] == "workflow_input_invalid"
