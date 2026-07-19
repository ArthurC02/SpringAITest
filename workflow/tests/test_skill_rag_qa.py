"""Node-First 遷移（Phase 1）：rag_answer 節點 + 編譯後的 app/skills/rag_qa.yaml。

語意逐字移植自 app/workflows/rag_qa.py（手寫圖已於 Phase 3a 退役刪除）。
無 pytest-asyncio，async 一律以 asyncio.run 執行（對齊 test_retrieve.py 慣例）。
"""

import asyncio

import httpx
from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.main import app
from app.nodes.rag_answer import _RagAnswerOutput, make_rag_answer_node
from tests.conftest import (
    FakeBackendResponse,
    auth_headers,
    invoke_builtin,
    patch_retrieve,
)
from tests.kbquery_fakes import FakeStructuredLLM, RecordingLLM, make_deps

client = TestClient(app)


# ---------------------------------------------------------------------------
# 節點單元測試：docs 空 → 固定文案不呼叫 LLM／有 docs → 正常呼叫（決策表兩半）
# ---------------------------------------------------------------------------


def test_rag_answer_empty_docs_returns_fixed_answer_without_calling_llm():
    llm = RecordingLLM()
    node = make_rag_answer_node(llm)

    out = asyncio.run(node({"question": "公司地址在哪？", "docs": []}))

    assert out == {
        "answer": "在你的租戶資料中找不到相關內容，請先上傳文件。",
        "citations": [],
    }
    assert llm.calls == []


def test_rag_answer_with_docs_calls_llm_and_builds_citations():
    llm = RecordingLLM(output=_RagAnswerOutput(answer="答案"))
    node = make_rag_answer_node(llm)
    docs = [{"document_id": "doc-1", "title": "示例文件", "content": "x" * 100, "score": 0.9}]

    out = asyncio.run(node({"question": "問題", "docs": docs}))

    assert out["answer"] == "答案"
    # snippet = content[:80]（80 字元截斷，on-point）
    assert out["citations"] == [
        {"document_id": "doc-1", "title": "示例文件", "snippet": "x" * 80}
    ]
    call = llm.calls[0]
    assert call["system"] == (
        "你是問答助手，請只根據下方提供的租戶文件內容回答問題，"
        "不要編造文件中沒有的資訊；請用繁體中文作答。"
    )
    assert call["user"] == f"文件內容：\n[1] 示例文件：{'x' * 100}\n\n問題：問題"
    assert call["schema"] is _RagAnswerOutput


def test_rag_answer_multiple_docs_build_citations_in_order():
    llm = RecordingLLM(output=_RagAnswerOutput(answer="答案"))
    node = make_rag_answer_node(llm)
    docs = [
        {"document_id": "a", "title": "A", "content": "短內容", "score": 0.5},
        {"document_id": "b", "title": "B", "content": "另一段內容", "score": 0.4},
    ]

    out = asyncio.run(node({"question": "問題", "docs": docs}))

    assert out["citations"] == [
        {"document_id": "a", "title": "A", "snippet": "短內容"},
        {"document_id": "b", "title": "B", "snippet": "另一段內容"},
    ]


# ---------------------------------------------------------------------------
# 編譯後的引擎路徑：走真正的 rag_qa.yaml（retrieve@1.0 → rag_answer@1.0）
# ---------------------------------------------------------------------------


def _invoke(deps, **state) -> dict:
    return invoke_builtin("rag_qa", deps, **state)


def test_rag_qa_skill_cold_start_compiles():
    loaded = skills.get("rag_qa")
    assert loaded is not None
    assert loaded.source == "builtin"
    assert loaded.deps is not None
    assert loaded.skill.required_role == "USER"
    assert set(loaded.skill.input_schema) == {"question"}


def test_rag_qa_skill_no_docs_returns_fixed_answer(monkeypatch):
    patch_retrieve(monkeypatch, [])

    out = _invoke(make_deps({}), question="沒人上傳過的問題")

    assert out["answer"] == "在你的租戶資料中找不到相關內容，請先上傳文件。"
    assert out["citations"] == []
    assert out["trace"][-1].node_name == "audit_feedback"  # 稽核強制附加於終止路徑


def test_rag_qa_skill_with_docs_returns_citations(monkeypatch):
    patch_retrieve(
        monkeypatch,
        [{"document_id": "doc-1", "title": "示例文件", "content": "內容" * 50, "score": 0.9}],
    )
    llm = FakeStructuredLLM(outputs={_RagAnswerOutput: _RagAnswerOutput(answer="最終答案")})

    out = _invoke(make_deps({}, llm=llm), question="這是什麼？")

    assert out["answer"] == "最終答案"
    assert out["citations"] == [
        {"document_id": "doc-1", "title": "示例文件", "snippet": ("內容" * 50)[:80]}
    ]


# ---------------------------------------------------------------------------
# API 級：POST /skills/rag_qa/invoke 走通（mock LLM + mock backend 檢索）
# ---------------------------------------------------------------------------


def test_rag_qa_invoke_api_level_with_docs(monkeypatch):
    patch_retrieve(
        monkeypatch,
        [{"document_id": "doc-1", "title": "文件", "content": "內容片段", "score": 0.9}],
    )

    original = skills.get("rag_qa")
    llm = FakeStructuredLLM(outputs={_RagAnswerOutput: _RagAnswerOutput(answer="API 答案")})
    deps = make_deps({}, llm=llm)
    skills._SKILLS["rag_qa"] = original.__class__(
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
            "/skills/rag_qa/invoke",
            json={"input": {"question": "這是什麼？"}},
            headers=auth_headers(),
        )

        assert resp.status_code == 200
        body = resp.json()
        assert body["skill"] == "rag_qa"
        assert body["output"]["answer"] == "API 答案"
        assert body["output"]["citations"] == [
            {"document_id": "doc-1", "title": "文件", "snippet": "內容片段"}
        ]
        assert not any(k.startswith("__") for k in body["output"])
    finally:
        skills._SKILLS["rag_qa"] = original


def test_rag_qa_invoke_api_level_rejects_blank_question():
    """input_schema 的 required/min_length：422（決策表另一半見上面 happy path）。"""
    resp = client.post(
        "/skills/rag_qa/invoke", json={"input": {"question": ""}}, headers=auth_headers()
    )
    assert resp.status_code == 422
    assert resp.json()["detail"]["error"] == "workflow_input_invalid"


def test_rag_qa_invoke_api_level_reserved_tenant_id_cannot_override_caller(monkeypatch):
    """真正的跨租戶隔離鏈路：input 夾帶 tenant_id 不得改變 retrieve 節點送往 backend 的
    X-Tenant-Id（承接 test_api.py 已刪除的
    test_reserved_tenant_id_in_input_cannot_override_caller_tenant，換到 skill 端點）。
    """
    captured: dict = {}

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        captured["headers"] = headers
        return FakeBackendResponse([])

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)

    resp = client.post(
        "/skills/rag_qa/invoke",
        json={"input": {"question": "公司地址在哪？", "tenant_id": "evil-tenant"}},
        headers=auth_headers(tenant_id="demo-a"),
    )

    assert resp.status_code == 200
    assert captured["headers"]["X-Tenant-Id"] == "demo-a"
