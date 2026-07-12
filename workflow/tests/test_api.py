"""測試 FastAPI 端點：以 stub LLM 取代真正的 LiteLLM 呼叫，全程不打真的網路。

所有 /workflows* 端點都需要服務間認證與租戶 context 標頭，因此一律透過
_headers() 輔助函式組出符合契約的標頭。rag_qa／analyze_report 會透過
app.nodes.retrieve 呼叫 backend 的檢索 API，測試裡一律用 _mock_backend_chunks()
把 httpx.AsyncClient.post 換成假的實作，避免打真的網路（backend 檢索邏輯本身的
測試屬於 backend/ 的範圍，不在這裡重複）。
"""

import asyncio
from types import SimpleNamespace
from typing import TypedDict

import httpx
import pytest
from fastapi.testclient import TestClient
from langgraph.graph import END, START, StateGraph

from app.main import app
from app.settings import settings
from app.workflows import registry
from tests.conftest import FakeBackendResponse

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


def _mock_backend_chunks(monkeypatch, chunks: list[dict]) -> dict:
    """讓 retrieve 節點呼叫 backend 檢索 API 時，直接拿到指定的 chunks，不打真的網路。

    回傳 captured dict，記錄最後一次送往 backend 的 url / json / headers，
    供需要檢查出站請求內容的測試（如租戶隔離）斷言使用；不需要的測試可忽略。
    """
    captured = {}

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        captured["url"] = url
        captured["json"] = json
        captured["headers"] = headers
        return FakeBackendResponse(chunks)

    monkeypatch.setattr("httpx.AsyncClient.post", fake_post)
    return captured


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


@pytest.mark.parametrize("token", [None, "wrong-token"], ids=["missing", "wrong"])
def test_list_workflows_bad_token_returns_401(token):
    """缺標頭與錯 token 是同一個 401 等價類（安全上刻意不區分原因）。"""
    resp = client.get("/workflows", headers=_headers(token=token))
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
    # backend 回傳空 chunks，retrieve 節點對應得到空 docs，
    # 因此 analyze 節點會走固定文案分支、不呼叫 LLM，只有 synthesize 會呼叫一次。
    _mock_backend_chunks(monkeypatch, [])
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


def test_analyze_report_with_chunks_runs_analyze_then_synthesize(monkeypatch):
    """有資料分支：analyze 與 synthesize 各呼叫一次 LLM（消耗式 StubLLM 依序吐兩則）。"""
    _mock_backend_chunks(
        monkeypatch,
        [
            {
                "document_id": "doc-1",
                "title": "示例文件",
                "content": "本季銷售成長一成。",
                "score": 0.9,
            }
        ],
    )
    stub = StubLLM(["要點", "報告"])
    monkeypatch.setattr("app.workflows.analyze_report.get_llm", lambda: stub)

    resp = client.post(
        "/workflows/analyze_report/invoke",
        json={"input": {"topic": "本季銷售"}},
        headers=ADMIN_HEADERS,
    )

    assert resp.status_code == 200
    body = resp.json()["output"]
    assert body["insights"] == "要點"
    assert body["report"] == "報告"


# ---------------------------------------------------------------------------
# rag_qa 輸入驗證
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "input_body",
    [{}, {"question": ""}],
    ids=["missing-question", "blank-question"],
)
def test_rag_qa_invalid_question_returns_422_workflow_input_invalid(input_body):
    """缺 question 與空字串 question 都應被 input_model 擋下（min_length=1 的存在理由）。"""
    resp = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": input_body},
        headers=_headers(),
    )

    assert resp.status_code == 422
    assert resp.json()["detail"]["error"] == "workflow_input_invalid"


def test_rag_qa_without_documents_returns_fixed_not_found_answer(monkeypatch):
    _mock_backend_chunks(monkeypatch, [])

    resp = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": {"question": "退款政策是什麼？"}},
        headers=_headers(tenant_id="demo-b"),
    )

    assert resp.status_code == 200
    body = resp.json()["output"]
    assert body["answer"] == "在你的租戶資料中找不到相關內容，請先上傳文件。"
    assert body["citations"] == []


def test_rag_qa_with_backend_chunks_returns_answer_and_citations(monkeypatch):
    """backend 回傳非空 chunks 時，rag_qa 應呼叫 LLM 作答，並依 docs 組出引用清單。

    文件的切塊／嵌入／向量檢索本身已搬到 backend/，這裡只驗證 workflow 這端
    「收到 backend 回傳的 chunks 後，rag_qa 圖的行為是否正確」。
    """
    _mock_backend_chunks(
        monkeypatch,
        [
            {
                "document_id": "doc-1",
                "title": "示例文件",
                "content": "公司地址在台北市信義區。",
                "score": 0.9,
            }
        ],
    )
    stub = StubLLM(["這是根據租戶文件的回答"])
    monkeypatch.setattr("app.workflows.rag_qa.get_llm", lambda: stub)

    resp = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": {"question": "公司地址在哪？"}},
        headers=_headers(tenant_id="demo-a"),
    )

    assert resp.status_code == 200
    body = resp.json()["output"]
    assert body["answer"] == "這是根據租戶文件的回答"
    assert len(body["citations"]) == 1
    assert body["citations"][0]["document_id"] == "doc-1"


# ---------------------------------------------------------------------------
# 多租戶隔離邊界：保留鍵剝除
# ---------------------------------------------------------------------------


def test_reserved_tenant_id_in_input_cannot_override_caller_tenant(monkeypatch):
    """body input 夾帶 tenant_id 不得覆蓋呼叫者標頭的租戶（_RESERVED_INPUT_KEYS 剝除）。

    關鍵斷言：retrieve 節點實際送往 backend 的 X-Tenant-Id 必須是合法呼叫者的
    demo-a，而不是 body 夾帶的 evil-tenant。
    """
    captured = _mock_backend_chunks(monkeypatch, [])

    resp = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": {"question": "公司地址在哪？", "tenant_id": "evil-tenant"}},
        headers=_headers(tenant_id="demo-a"),
    )

    assert resp.status_code == 200
    assert captured["headers"]["X-Tenant-Id"] == "demo-a"


# ---------------------------------------------------------------------------
# 執行期錯誤契約：500 / 504
# ---------------------------------------------------------------------------


def test_backend_connect_error_returns_500_workflow_execution_failed(monkeypatch):
    """backend 連不上（httpx.ConnectError）時，對外契約是 500 workflow_execution_failed。"""

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        raise httpx.ConnectError("connection refused")

    monkeypatch.setattr("httpx.AsyncClient.post", fake_post)

    resp = client.post(
        "/workflows/rag_qa/invoke",
        json={"input": {"question": "公司地址在哪？"}},
        headers=_headers(),
    )

    assert resp.status_code == 500
    assert resp.json()["detail"]["error"] == "workflow_execution_failed"


def test_slow_workflow_returns_504_workflow_timeout(monkeypatch):
    """執行超過逾時上限時，對外契約是 504 workflow_timeout。

    用 throwaway 工作流（sleep 節點）搭配 monkeypatch 極小的全域逾時，
    不動生產程式的 `spec.timeout_seconds or settings...` 語義，也不讓測試久等。
    """
    name = "__throwaway_slow_for_timeout_test__"

    class _SlowState(TypedDict, total=False):
        tenant_id: str

    async def slow(state: _SlowState) -> dict:
        await asyncio.sleep(0.5)
        return {}

    def build():
        g = StateGraph(_SlowState)
        g.add_node("slow", slow)
        g.add_edge(START, "slow")
        g.add_edge("slow", END)
        return g.compile()

    monkeypatch.setattr(settings, "workflow_timeout_seconds", 0.05)
    try:
        registry.register(name, "逾時測試用")(build)

        resp = client.post(
            f"/workflows/{name}/invoke", json={"input": {}}, headers=_headers()
        )

        assert resp.status_code == 504
        assert resp.json()["detail"]["error"] == "workflow_timeout"
    finally:
        # 清理，避免污染其他測試（仿 test_registry.py 的 throwaway 模式）。
        registry._REGISTRY.pop(name, None)
