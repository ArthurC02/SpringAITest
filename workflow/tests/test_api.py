"""測試 FastAPI 端點：以 stub LLM 取代真正的 LiteLLM 呼叫，全程不打真的網路。

所有 /workflows*、/documents* 端點都需要服務間認證與租戶 context 標頭，
因此一律透過 _headers() 輔助函式組出符合契約的標頭；向量庫固定使用
InMemoryVectorStore（DATABASE_URL 預設為空）＋ fake 嵌入，且每個測試前後都會
重置，避免測試之間的文件資料互相汙染。
"""

import asyncio
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from app.main import app
from app.settings import settings
from app.vectorstore import InMemoryVectorStore, get_vector_store

client = TestClient(app)

INTERNAL_TOKEN = "internal-dev-token"  # 對應 settings.internal_api_token 的預設值


def _headers(tenant_id="demo-a", user_id="alice", role="USER", token=INTERNAL_TOKEN):
    """組出符合服務間契約的標頭；預設是 demo-a 租戶的一般使用者。

    任何欄位傳入 None 代表「刻意不帶這個標頭」，用來測試缺標頭時的行為。
    """
    headers = {}
    if token is not None:
        headers["X-Internal-Token"] = token
    if tenant_id is not None:
        headers["X-Tenant-Id"] = tenant_id
    if user_id is not None:
        headers["X-User-Id"] = user_id
    if role is not None:
        headers["X-User-Role"] = role
    return headers


ADMIN_HEADERS = _headers(role="ADMIN")


@pytest.fixture(autouse=True)
def _reset_vector_store():
    """每個測試前後都清空記憶體向量庫，避免不同測試之間的文件互相汙染。"""
    store = get_vector_store()
    assert isinstance(store, InMemoryVectorStore), "測試預期一律使用記憶體向量庫"
    store.reset()
    yield
    store.reset()


class StubLLM:
    """假的 LLM 客戶端：依序吐出預先準備好的回覆內容，模擬 ChatOpenAI.ainvoke 的介面。"""

    def __init__(self, replies):
        self._replies = list(replies)

    async def ainvoke(self, messages, **kwargs):
        return SimpleNamespace(content=self._replies.pop(0))


# ---------------------------------------------------------------------------
# 健康檢查：不需要任何標頭
# ---------------------------------------------------------------------------


def test_health_without_any_header():
    resp = client.get("/health")
    assert resp.status_code == 200
    assert resp.json() == {"status": "ok"}


# ---------------------------------------------------------------------------
# 服務間認證與租戶 context：401 / 400
# ---------------------------------------------------------------------------


def test_list_workflows_missing_token_returns_401():
    resp = client.get("/workflows", headers=_headers(token=None))
    assert resp.status_code == 401
    assert resp.json()["detail"]["error"] == "unauthorized"


def test_list_workflows_wrong_token_returns_401():
    resp = client.get("/workflows", headers=_headers(token="wrong-token"))
    assert resp.status_code == 401
    assert resp.json()["detail"]["error"] == "unauthorized"


def test_list_workflows_missing_tenant_returns_400():
    resp = client.get("/workflows", headers=_headers(tenant_id=None))
    assert resp.status_code == 400
    assert resp.json()["detail"]["error"] == "missing_context"


def test_list_workflows_missing_role_returns_400():
    resp = client.get("/workflows", headers=_headers(role=None))
    assert resp.status_code == 400
    assert resp.json()["detail"]["error"] == "missing_context"


# ---------------------------------------------------------------------------
# GET /workflows
# ---------------------------------------------------------------------------


def test_list_workflows():
    resp = client.get("/workflows", headers=_headers())
    assert resp.status_code == 200
    body = {item["name"]: item for item in resp.json()}
    assert {"summarize", "triage", "rag_qa", "analyze_report"}.issubset(body.keys())
    assert body["summarize"]["required_role"] == "USER"
    assert body["analyze_report"]["required_role"] == "ADMIN"

    names = [item["name"] for item in resp.json()]
    assert names == sorted(names)


# ---------------------------------------------------------------------------
# 既有工作流：summarize / triage（沿用既有行為，補上標頭）
# ---------------------------------------------------------------------------


def test_invoke_summarize_happy_path(monkeypatch):
    # 注意：要 patch 的是 app.workflows.summarize 模組內的 get_llm 名稱，
    # 因為該模組是用 `from app.llm import get_llm` 匯入，patch app.llm.get_llm 不會生效。
    monkeypatch.setattr(
        "app.workflows.summarize.get_llm", lambda: StubLLM(["這是摘要"])
    )

    resp = client.post(
        "/workflows/summarize/invoke",
        json={"input": {"text": "一段很長的原文"}},
        headers=_headers(),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["workflow"] == "summarize"
    assert body["output"]["summary"] == "這是摘要"


def test_invoke_triage_simple_branch(monkeypatch):
    # 注意：triage 圖會呼叫 get_llm() 兩次（classify、quick_answer 各一次），
    # 因此必須讓兩次呼叫都拿到「同一個」StubLLM 實例，讓回覆列表的消耗狀態延續下去；
    # 若每次呼叫都 new 一個 StubLLM，第二個節點會重新拿到列表第一項而非第二項。
    stub = StubLLM(["SIMPLE", "答案"])
    monkeypatch.setattr("app.workflows.triage.get_llm", lambda: stub)

    resp = client.post(
        "/workflows/triage/invoke",
        json={"input": {"question": "退款要多久？"}},
        headers=_headers(),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["workflow"] == "triage"
    assert body["output"]["category"] == "SIMPLE"
    assert body["output"]["answer"] == "答案"


def test_invoke_triage_complex_branch(monkeypatch):
    stub = StubLLM(["COMPLEX", "深入答案"])
    monkeypatch.setattr("app.workflows.triage.get_llm", lambda: stub)

    resp = client.post(
        "/workflows/triage/invoke",
        json={"input": {"question": "如何設計一個分散式系統？"}},
        headers=_headers(),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["workflow"] == "triage"
    assert body["output"]["category"] == "COMPLEX"
    assert body["output"]["answer"] == "深入答案"


def test_invoke_unknown_workflow_returns_404():
    resp = client.post("/workflows/nope/invoke", json={"input": {}}, headers=_headers())

    assert resp.status_code == 404
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_not_found"


def test_invoke_missing_input_returns_422():
    resp = client.post("/workflows/summarize/invoke", json={}, headers=_headers())

    assert resp.status_code == 422


# ---------------------------------------------------------------------------
# 角色權限邊界：USER 呼叫 ADMIN 限定工作流 → 403，ADMIN → 200
# ---------------------------------------------------------------------------


def test_invoke_admin_workflow_forbidden_for_user_role():
    resp = client.post(
        "/workflows/analyze_report/invoke",
        json={"input": {"topic": "本季銷售"}},
        headers=_headers(role="USER"),
    )

    assert resp.status_code == 403
    assert resp.json()["detail"]["error"] == "workflow_forbidden"


def test_invoke_admin_workflow_allowed_for_admin_role(monkeypatch):
    # 這個租戶沒有任何文件，retrieve 節點會回傳空 docs，
    # 因此 analyze 節點會走固定文案分支、不呼叫 LLM，只有 synthesize 會呼叫一次。
    stub = StubLLM(["結構化報告內容"])
    monkeypatch.setattr("app.workflows.analyze_report.get_llm", lambda: stub)

    resp = client.post(
        "/workflows/analyze_report/invoke",
        json={"input": {"topic": "本季銷售"}},
        headers=ADMIN_HEADERS,
    )

    assert resp.status_code == 200
    body = resp.json()["output"]
    assert body["insights"] == "（無資料）"
    assert body["report"] == "結構化報告內容"


# ---------------------------------------------------------------------------
# rag_qa 輸入驗證
# ---------------------------------------------------------------------------


def test_rag_qa_missing_question_returns_422_workflow_input_invalid():
    resp = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": {}},
        headers=_headers(),
    )

    assert resp.status_code == 422
    assert resp.json()["detail"]["error"] == "workflow_input_invalid"


def test_rag_qa_without_documents_returns_fixed_not_found_answer():
    resp = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": {"question": "退款政策是什麼？"}},
        headers=_headers(tenant_id="demo-b"),
    )

    assert resp.status_code == 200
    body = resp.json()["output"]
    assert body["answer"] == "在你的租戶資料中找不到相關內容，請先上傳文件。"
    assert body["citations"] == []


# ---------------------------------------------------------------------------
# /documents CRUD
# ---------------------------------------------------------------------------


def test_documents_crud_happy_path():
    create_resp = client.post(
        "/documents",
        json={"title": "退款政策", "text": "退款需在七天內申請。\n\n超過七天恕不受理。"},
        headers=_headers(),
    )
    assert create_resp.status_code == 201
    created = create_resp.json()
    assert created["title"] == "退款政策"
    assert created["chunk_count"] >= 1

    list_resp = client.get("/documents", headers=_headers())
    assert list_resp.status_code == 200
    docs = list_resp.json()
    assert any(d["id"] == created["id"] for d in docs)
    assert all("created_at" in d for d in docs)

    delete_resp = client.delete(f"/documents/{created['id']}", headers=_headers())
    assert delete_resp.status_code == 204

    list_resp_after = client.get("/documents", headers=_headers())
    assert all(d["id"] != created["id"] for d in list_resp_after.json())


def test_delete_nonexistent_document_returns_404():
    resp = client.delete("/documents/does-not-exist", headers=_headers())
    assert resp.status_code == 404
    assert resp.json()["detail"]["error"] == "document_not_found"


# ---------------------------------------------------------------------------
# /documents 逾時保護：嵌入呼叫卡住時應回 504，而不是無限阻塞
# ---------------------------------------------------------------------------


def test_create_document_timeout_returns_504(monkeypatch):
    """把逾時秒數 monkeypatch 成極小值，並讓嵌入呼叫睡得比它久，驗證 504 逾時保護生效。"""
    monkeypatch.setattr(settings, "document_timeout_seconds", 0.05)

    class SlowEmbeddings:
        async def aembed_documents(self, chunks):
            await asyncio.sleep(1)
            return [[0.0] * 4 for _ in chunks]

    monkeypatch.setattr("app.main.get_embeddings", lambda: SlowEmbeddings())

    resp = client.post(
        "/documents",
        json={"title": "逾時測試文件", "text": "隨便一段文字"},
        headers=_headers(),
    )

    assert resp.status_code == 504
    assert resp.json()["detail"]["error"] == "document_timeout"


# ---------------------------------------------------------------------------
# 多租戶隔離：demo-a 上傳的文件，demo-b 完全看不到
# ---------------------------------------------------------------------------


def test_tenant_isolation_for_documents_and_rag_qa(monkeypatch):
    stub = StubLLM(["這是根據租戶文件的回答"])
    monkeypatch.setattr("app.workflows.rag_qa.get_llm", lambda: stub)

    create_resp = client.post(
        "/documents",
        json={"title": "示例文件", "text": "公司地址在台北市信義區。"},
        headers=_headers(tenant_id="demo-a"),
    )
    assert create_resp.status_code == 201

    # demo-b 看不到 demo-a 上傳的任何文件。
    other_list = client.get("/documents", headers=_headers(tenant_id="demo-b"))
    assert other_list.json() == []

    # demo-b 呼叫 rag_qa 找不到相關內容，走固定文案分支。
    other_rag = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": {"question": "公司地址在哪？"}},
        headers=_headers(tenant_id="demo-b"),
    )
    assert other_rag.status_code == 200
    assert (
        other_rag.json()["output"]["answer"]
        == "在你的租戶資料中找不到相關內容，請先上傳文件。"
    )

    # demo-a 應該檢索得到剛上傳的文件並附上引用。
    own_rag = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": {"question": "公司地址在哪？"}},
        headers=_headers(tenant_id="demo-a"),
    )
    assert own_rag.status_code == 200
    own_body = own_rag.json()["output"]
    assert own_body["answer"] == "這是根據租戶文件的回答"
    assert len(own_body["citations"]) >= 1
