"""P1-05 / 規格 02-spec §3.3「Runtime failures never expose raw exception text」。

驗收的是一組合起來才成立的性質，所以四件事一起測，缺一即失效：
1. 500 body 不含原始例外文字（路徑、URL、provider 細節）。
2. 500 body 與回應標頭都帶同一個 correlation ID。
3. 原始例外（含 traceback）帶著同一個 correlation ID 進了日誌 —— 否則第 1 點等於把
   排障資訊直接刪掉，而不是搬到安全的地方。
4. 呼叫端帶進來的 X-Correlation-Id 原樣回傳（跨服務鏈路），但它是信任邊界輸入，
   走白名單、預設拒絕。
"""

import asyncio
import logging
import re
from contextlib import contextmanager

import pytest
from fastapi.testclient import TestClient

from app import skills
from app.correlation import HEADER_NAME, SAFE_EXECUTION_FAILED_MESSAGE
from app.engine import compiler, node_registry
from app.engine.package_reader import MemoryPackageReader
from app.engine.skill import Skill
from app.main import app
from app.runtime.flow_harness import invoke_flow_with_governance
from app.skills import custom
from tests.agentic_fakes import make_agentic_deps, make_agentic_skill, make_package
from tests.conftest import auth_headers as _headers
from tests.conftest import swap_skill

client = TestClient(app)

# 刻意做成「一眼看得出不該外流」的例外文字：內部主機、埠、憑證、資料庫名。
_SECRET = "connect failed: postgresql://svc:hunter2@10.0.0.7:5432/appdb"

_UUID4 = re.compile(r"\A[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\Z")


class _BoomGraph:
    async def ainvoke(self, state, config=None):
        raise RuntimeError(_SECRET)


@contextmanager
def _boom_skill():
    """一個執行時必炸的 flow skill（invoke 的主路徑：flow governance wrapper）。"""
    with swap_skill(
        "__correlation-boom__",
        base=skills.get("kb-query"),
        skill=Skill.model_validate(
            {"name": "correlation-boom", "flow": [{"node": "t"}]}
        ),
        graph=_BoomGraph(),
        input_model=None,
        deps=None,
    ):
        yield


def _invoke_boom(headers=None):
    return client.post(
        "/skills/__correlation-boom__/invoke",
        json={"input": {}},
        headers=headers or _headers(),
    )


def test_unexpected_exception_returns_safe_message_and_correlation_id():
    with _boom_skill():
        resp = _invoke_boom()

    assert resp.status_code == 500
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_execution_failed"
    assert detail["message"] == SAFE_EXECUTION_FAILED_MESSAGE
    # 例外文字的任何片段都不得出現在回應（body 整體看，不只 message 欄位）。
    for leaked in ("postgresql", "10.0.0.7", "hunter2", "RuntimeError", "Traceback"):
        assert leaked not in resp.text
    assert _UUID4.match(detail["correlation_id"])
    assert resp.headers[HEADER_NAME] == detail["correlation_id"]


def test_unexpected_exception_is_logged_with_the_same_correlation_id(caplog):
    with caplog.at_level(logging.ERROR), _boom_skill():
        resp = _invoke_boom()

    correlation_id = resp.json()["detail"]["correlation_id"]
    # 原始例外（含 traceback）必須真的在日誌裡，而且和回應是同一個 ID 才對得起來。
    assert _SECRET in caplog.text
    assert any(
        correlation_id in record.getMessage()
        and record.exc_info is not None
        and _SECRET in str(record.exc_info[1])
        for record in caplog.records
    )


def test_caller_supplied_correlation_id_is_echoed_unchanged():
    supplied = "0198f0e2-1b3c-4d5e-8f90-a1b2c3d4e5f6"
    with _boom_skill():
        resp = _invoke_boom({**_headers(), HEADER_NAME: supplied})

    assert resp.headers[HEADER_NAME] == supplied
    assert resp.json()["detail"]["correlation_id"] == supplied


@pytest.mark.parametrize(
    "hostile",
    [
        "has spaces",
        "x" * 129,  # off-point：超過 128 字元上限
        "<script>alert(1)</script>",
        "abc\ndef",
        "",
    ],
    ids=["spaces", "too-long", "html", "newline", "blank"],
)
def test_unsafe_caller_correlation_id_is_replaced_not_echoed(hostile):
    """白名單、預設拒絕：這個值會被原樣寫回回應標頭，所以不合格就必須丟棄重生成。"""
    with _boom_skill():
        resp = _invoke_boom({**_headers(), HEADER_NAME: hostile})

    returned = resp.headers[HEADER_NAME]
    assert returned != hostile
    assert _UUID4.match(returned)


def test_maximum_length_caller_correlation_id_is_accepted():
    """on-point：剛好 128 字元仍在白名單內（決策表的合法那半邊）。"""
    supplied = "a" * 128
    with _boom_skill():
        resp = _invoke_boom({**_headers(), HEADER_NAME: supplied})

    assert resp.headers[HEADER_NAME] == supplied


def test_successful_response_also_carries_a_correlation_id():
    """決策表另一半：正常路徑不受影響，但一樣帶得走追蹤編號。"""
    resp = client.get("/health")

    assert resp.status_code == 200
    assert _UUID4.match(resp.headers[HEADER_NAME])


def test_custom_skill_load_failure_does_not_leak_backend_path(monkeypatch, caplog):
    """site 2：BackendUnavailable 的訊息本身就夾帶 backend path 與 httpx 錯誤文字。"""

    async def _boom_load(name, ctx):
        raise custom.BackendUnavailable(_SECRET)

    monkeypatch.setattr(custom, "load", _boom_load)

    with caplog.at_level(logging.ERROR):
        resp = client.post(
            "/skills/no-such-skill/invoke",
            json={"input": {"query": "x"}},
            headers=_headers(),
        )

    assert resp.status_code == 500
    detail = resp.json()["detail"]
    assert detail["message"] == SAFE_EXECUTION_FAILED_MESSAGE
    assert "postgresql" not in resp.text
    assert detail["correlation_id"] and resp.headers[HEADER_NAME] == detail["correlation_id"]
    assert _SECRET in caplog.text


# ---------------------------------------------------------------------------
# site 3：agentic invoke 的 HTTP 200（flow 的 _public_flow_output 保護不到這條路）
# ---------------------------------------------------------------------------


@contextmanager
def _agentic_boom_skill():
    """runner node 內部炸掉的 agentic skill：Node Shell 會把例外吞成 fatal_error/errors。"""
    skill = make_agentic_skill(name="correlation-agentic-boom")
    reader = MemoryPackageReader()
    reader.put("demo-a", make_package(skill, instruction="Help."))

    def _boom_model():
        raise RuntimeError(_SECRET)

    deps = make_agentic_deps(reader, _boom_model)
    with swap_skill(
        "__correlation-agentic-boom__",
        base=skills.get("kb-query"),
        skill=skill,
        graph=compiler.compile(skill, deps),
        input_model=None,
        deps=deps,
    ):
        yield


def test_agentic_invoke_200_does_not_leak_exception_text_but_keeps_trace(caplog):
    """agentic 是受控失敗 → HTTP 200：狀態碼安全不代表 body 安全。

    Node Shell 吞下的 `fatal_error`/`errors` 帶著原始例外文字，而這條路只經過
    `compiler.public_output`（只剝 `__` 前綴）。trace 是 agentic 的公開契約，
    且 TraceEntry 只帶 `error_code`＝例外型別名，必須留著。
    """
    with caplog.at_level(logging.ERROR), _agentic_boom_skill():
        resp = client.post(
            "/skills/__correlation-agentic-boom__/invoke",
            json={"input": {"query": "hi"}},
            headers=_headers(),
        )

    assert resp.status_code == 200
    for leaked in ("postgresql", "10.0.0.7", "hunter2", "Traceback"):
        assert leaked not in resp.text
    output = resp.json()["output"]
    # errors 是主要載體；audit_trail 是第二條 —— audit_feedback 會把同一份 errors 抄進去，
    # 只堵 errors 等於沒堵。兩者都在 flow 的 PUBLIC_DENY_KEYS 裡，agentic 用同一份。
    assert "errors" not in output
    assert "audit_trail" not in output
    assert output["fatal_error"] == SAFE_EXECUTION_FAILED_MESSAGE  # 失敗訊號在、例外文字不在
    assert [t["node_name"] for t in output["trace"]][-1] == "audit_feedback"
    assert resp.headers[HEADER_NAME]
    assert _SECRET in caplog.text  # 排障資訊搬到日誌，不是被刪掉


# ---------------------------------------------------------------------------
# site 4：完全沒被 try/except 修補的路徑（middleware 之外沒人補得了）
# ---------------------------------------------------------------------------


def test_unpatched_route_exception_gets_the_same_safe_500_envelope(monkeypatch, caplog):
    """Starlette 的 ServerErrorMiddleware 站在 user middleware 之外：它回的是 text/plain
    "Internal Server Error"，無標頭、無 JSON、日誌對不上任何 ID。收尾必須在 middleware 內。"""

    def _boom():
        raise RuntimeError(_SECRET)

    monkeypatch.setattr(node_registry, "all_specs", _boom)

    with caplog.at_level(logging.ERROR):
        resp = client.get("/nodes", headers=_headers())

    assert resp.status_code == 500
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_execution_failed"
    assert detail["message"] == SAFE_EXECUTION_FAILED_MESSAGE
    for leaked in ("postgresql", "10.0.0.7", "hunter2", "RuntimeError", "Traceback"):
        assert leaked not in resp.text
    assert _UUID4.match(detail["correlation_id"])
    assert resp.headers[HEADER_NAME] == detail["correlation_id"]
    assert _SECRET in caplog.text


# ---------------------------------------------------------------------------
# 分類界線:列舉式治理拒絕 ≠ 未預期例外
# ---------------------------------------------------------------------------


def test_flow_denied_keeps_its_enumerated_message_without_a_false_traceback(caplog):
    """FlowDenied 的訊息是我們自己寫的固定字串（與 budget_exhausted 同類）：原樣進稽核，
    且不得被記成未預期例外 —— 記了就是在日誌裡種一筆對不到任何真實 bug 的 traceback。"""
    with caplog.at_level(logging.ERROR):
        result = asyncio.run(
            invoke_flow_with_governance(
                skill=Skill(name="pinned", flow=[]),
                raw_input={},
                deps=None,
                timeout_seconds=1,
                step_budget=1,
                tool_round_budget=1,
                definition="name: pinned",
                definition_sha256="0" * 64,
            )
        )

    assert result.status == "error"
    assert result.governance["preflight"]["status"] == "error"
    assert result.governance["error"] == "definition hash mismatch"
    assert all(record.exc_info is None for record in caplog.records)
