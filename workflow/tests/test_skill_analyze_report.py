"""Node-First 遷移（Phase 1）：doc_insights/report_synthesize 節點 +
編譯後的 app/skills/analyze_report.yaml（required_role: ADMIN）。

語意逐字移植自 app/workflows/analyze_report.py（手寫圖已於 Phase 3a 退役刪除）。
"""

import asyncio
import dataclasses

from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.main import app
from app.nodes.analyze_report import (
    _DocInsightsOutput,
    _ReportSynthesizeOutput,
    make_doc_insights_node,
    make_report_synthesize_node,
)
from tests.conftest import auth_headers, invoke_builtin, patch_retrieve
from tests.kbquery_fakes import FakeStructuredLLM, RecordingLLM, make_deps

client = TestClient(app)


# ---------------------------------------------------------------------------
# doc_insights：docs 空 →（無資料）不呼叫 LLM／有 docs → 正常呼叫（決策表兩半）
# ---------------------------------------------------------------------------


def test_doc_insights_empty_docs_returns_no_data_without_calling_llm():
    llm = RecordingLLM()
    node = make_doc_insights_node(llm)

    out = asyncio.run(node({"topic": "營收", "docs": []}))

    assert out == {"insights": "（無資料）"}
    assert llm.calls == []


def test_doc_insights_with_docs_calls_llm():
    llm = RecordingLLM(output=_DocInsightsOutput(insights="要點一、要點二"))
    node = make_doc_insights_node(llm)
    docs = [{"document_id": "d1", "title": "季報", "content": "本季營收成長", "score": 0.8}]

    out = asyncio.run(node({"topic": "營收", "docs": docs}))

    assert out == {"insights": "要點一、要點二"}
    call = llm.calls[0]
    assert call["system"] == (
        "你是資料分析助手，請從下方提供的租戶文件內容中，"
        "萃取與指定主題相關的重點，以條列式繁體中文呈現。"
    )
    assert call["user"] == "主題：營收\n\n文件內容：\n[1] 季報：本季營收成長"
    assert call["schema"] is _DocInsightsOutput


def test_report_synthesize_sends_expected_prompt():
    llm = RecordingLLM(output=_ReportSynthesizeOutput(report="結構化報告"))
    node = make_report_synthesize_node(llm)

    out = asyncio.run(node({"topic": "營收", "insights": "要點一"}))

    assert out == {"report": "結構化報告"}
    call = llm.calls[0]
    assert call["system"] == (
        "你是資料分析助手，請依提供的要點撰寫結構化報告，"
        "包含摘要、關鍵發現、建議三個段落，全程使用繁體中文。"
    )
    assert call["user"] == "主題：營收\n\n要點：\n要點一"
    assert call["schema"] is _ReportSynthesizeOutput


# ---------------------------------------------------------------------------
# 編譯後的引擎路徑
# ---------------------------------------------------------------------------


def _invoke(deps, **state) -> dict:
    return invoke_builtin("analyze-report", deps, **state)


def test_analyze_report_skill_cold_start_compiles():
    loaded = skills.get("analyze-report")
    assert loaded is not None
    assert loaded.source == "builtin"
    assert loaded.skill.required_role == "ADMIN"
    assert set(loaded.skill.input_schema) == {"topic"}


def test_analyze_report_skill_no_docs_guard(monkeypatch):
    patch_retrieve(monkeypatch, [])
    llm = FakeStructuredLLM(
        outputs={_ReportSynthesizeOutput: _ReportSynthesizeOutput(report="報告（無資料）")}
    )

    out = _invoke(make_deps({}, llm=llm), topic="無資料主題")

    assert out["insights"] == "（無資料）"
    assert out["report"] == "報告（無資料）"


def test_analyze_report_skill_with_docs_produces_report(monkeypatch):
    patch_retrieve(
        monkeypatch,
        [{"document_id": "d1", "title": "季報", "content": "營收成長 10%", "score": 0.8}],
    )
    llm = FakeStructuredLLM(
        outputs={
            _DocInsightsOutput: _DocInsightsOutput(insights="營收成長"),
            _ReportSynthesizeOutput: _ReportSynthesizeOutput(report="完整報告"),
        }
    )

    out = _invoke(make_deps({}, llm=llm), topic="營收")

    assert out["insights"] == "營收成長"
    assert out["report"] == "完整報告"
    assert out["trace"][-1].node_name == "audit_feedback"


# ---------------------------------------------------------------------------
# required_role: ADMIN（決策表兩半：USER → 403／ADMIN → 通過）
# ---------------------------------------------------------------------------


def test_analyze_report_invoke_forbidden_for_user_role():
    resp = client.post(
        "/skills/analyze-report/invoke",
        json={"input": {"topic": "營收"}},
        headers=auth_headers(role="USER"),
    )
    assert resp.status_code == 403
    assert resp.json()["detail"]["error"] == "workflow_forbidden"


def test_analyze_report_invoke_api_level_admin_role(monkeypatch):
    patch_retrieve(
        monkeypatch,
        [{"document_id": "d1", "title": "季報", "content": "營收成長", "score": 0.8}],
    )

    original = skills.get("analyze-report")
    llm = FakeStructuredLLM(
        outputs={
            _DocInsightsOutput: _DocInsightsOutput(insights="要點"),
            _ReportSynthesizeOutput: _ReportSynthesizeOutput(report="API 報告"),
        }
    )
    deps = make_deps({}, llm=llm)
    skills._SKILLS["analyze-report"] = original.__class__(
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
            "/skills/analyze-report/invoke",
            json={"input": {"topic": "營收"}},
            headers=auth_headers(role="ADMIN"),
        )

        assert resp.status_code == 200
        body = resp.json()
        assert body["skill"] == "analyze-report"
        assert body["output"]["report"] == "API 報告"
    finally:
        skills._SKILLS["analyze-report"] = original


def test_analyze_report_invoke_api_level_admin_role_with_no_docs(monkeypatch):
    """ADMIN × 檢索空結果：角色閘門通過後仍走「（無資料）」分支（不呼叫 doc_insights 的 LLM）。

    既有的 no_docs 測試走內部 _invoke 繞過 HTTP 路由，這裡補齊真實端點的那一格。
    """
    patch_retrieve(monkeypatch, [])

    original = skills.get("analyze-report")
    llm = FakeStructuredLLM(
        outputs={_ReportSynthesizeOutput: _ReportSynthesizeOutput(report="API 報告（無資料）")}
    )
    deps = make_deps({}, llm=llm)
    skills._SKILLS["analyze-report"] = dataclasses.replace(
        original, graph=compiler.compile(original.skill, deps), deps=deps
    )
    try:
        resp = client.post(
            "/skills/analyze-report/invoke",
            json={"input": {"topic": "無資料主題"}},
            headers=auth_headers(role="ADMIN"),
        )

        assert resp.status_code == 200
        output = resp.json()["output"]
        assert output["insights"] == "（無資料）"
        assert output["report"] == "API 報告（無資料）"
    finally:
        skills._SKILLS["analyze-report"] = original


# ---------------------------------------------------------------------------
# API 級：ADMIN 通過角色閘門後，topic 的 required / min_length: 1 兩側
# ---------------------------------------------------------------------------


def test_analyze_report_invoke_api_level_rejects_missing_topic():
    resp = client.post(
        "/skills/analyze-report/invoke",
        json={"input": {}},
        headers=auth_headers(role="ADMIN"),
    )
    assert resp.status_code == 422
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_input_invalid"
    assert detail["field_errors"] == {"topic": "「topic」為必填。"}


def test_analyze_report_invoke_api_level_rejects_blank_topic():
    resp = client.post(
        "/skills/analyze-report/invoke",
        json={"input": {"topic": ""}},
        headers=auth_headers(role="ADMIN"),
    )
    assert resp.status_code == 422
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_input_invalid"
    assert detail["field_errors"] == {"topic": "「topic」至少需 1 個字。"}
