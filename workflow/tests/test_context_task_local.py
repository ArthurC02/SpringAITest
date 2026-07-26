"""A-CTX-40..46 task-local context contract tests."""

from __future__ import annotations

import json

import httpx
import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from app.engine import node_registry
from app.main import app
from app.runtime.orchestrator_backend import (
    ContextDelta,
    ContextRequest,
    OrchestratorBackendClient,
    OrchestratorBackendConflict,
    OrchestratorBackendPermanentError,
    RootCommandClaim,
    VersionedContextRequest,
)
from app.runtime.task_local_context import TaskLocalContextRuntime
from app.security import RequestContext
from app.settings import settings
from tests.conftest import auth_headers

ROOT = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
CHILD = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
REQUEST = "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
CONTEXT = "dddddddd-dddd-4ddd-8ddd-dddddddddddd"
VIEW = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"
CTX = RequestContext(tenant_id="tenant", user_id="user", role="USER")
client = TestClient(app)


def _request_wire(*, version=1, current_revision=1):
    base_ref = {"context_id": CONTEXT, "revision": 1, "view_id": VIEW}
    current_ref = (
        {"context_id": CONTEXT, "revision": current_revision, "view_id": VIEW}
        if current_revision is not None
        else None
    )
    return {
        "id": REQUEST, "root_run_id": ROOT, "child_id": CHILD,
        "task_id": "task-1", "role": "worker", "context_id": CONTEXT,
        "base_context_ref": base_ref, "current_context_ref": current_ref,
        "version": version, "created_at": "2026-01-01T00:00:00Z",
        "updated_at": "2026-01-01T00:00:00Z",
    }


def _delta_wire():
    return {
        "definition": {"facts": [], "conflicts": []},
        "evidence": [],
        "views": [{"view_type": "worker", "definition": {"task": "local"}}],
        "measurements": {
            "context_round": 1, "max_context_rounds": 3,
            "critical_ambiguity": False, "deadline_exhausted": False,
            "assumptions_count": 0, "policy_violations": [],
            "retrieval_gaps": [],
        },
        "as_of": None, "expires_at": None,
    }


def _revision_wire(*, status="READY"):
    return {
        "context_id": CONTEXT, "revision": 2, "root_run_id": ROOT,
        "status": status, "readiness": 1.0, "unmet_requirements": [],
        "policy_id": "ffffffff-ffff-4fff-8fff-ffffffffffff",
        "definition": {"facts": [], "conflicts": []},
        "as_of": "2026-01-01T00:00:00Z", "created_at": "2026-01-01T00:00:00Z",
        "expires_at": None,
        "context_ref": {"context_id": CONTEXT, "revision": 2, "view_id": VIEW},
        "selected_source_id": "backend_documents",
        "adapter_id": "backend.retrieval_search",
    }


@pytest.fixture(autouse=True)
def _enable(monkeypatch):
    monkeypatch.setattr(settings, "multi_agent_dispatch_enabled", True)
    monkeypatch.setattr(settings, "context_enrichment_enabled", True)


@pytest.mark.asyncio
async def test_a40_create_request_sends_no_trust_fields_and_accepts_backend_identity(monkeypatch):
    backend, captured = OrchestratorBackendClient(), {}

    async def response(method, path, ctx, **kwargs):
        captured.update(method=method, path=path, kwargs=kwargs)
        return httpx.Response(200, headers={"ETag": '"1"'}, json=_request_wire())

    monkeypatch.setattr(backend, "_request", response)
    result = await backend.create_context_request(ROOT, CHILD, CTX)

    assert result.value.role == "worker"
    assert result.value.current_context_ref.view_id == VIEW
    assert captured["kwargs"] == {}
    assert captured["path"].endswith(f"/children/{CHILD}/context-requests")


@pytest.mark.asyncio
async def test_a41_etag_conflict_rereads_for_version_but_never_reposts(monkeypatch):
    backend, calls = OrchestratorBackendClient(), []

    async def response(method, path, ctx, **kwargs):
        calls.append((method, kwargs))
        if method == "POST" and len(calls) == 1:
            raise OrchestratorBackendConflict("stale")
        if method == "GET":
            return httpx.Response(200, headers={"ETag": '"2"'}, json=_request_wire(version=2))
        pytest.fail("stale delta must never be silently rebased")

    monkeypatch.setattr(backend, "_request", response)
    request = VersionedContextRequest(
        value=ContextRequest.model_validate(_request_wire()), etag='"1"'
    )
    with pytest.raises(OrchestratorBackendConflict, match="current version 2"):
        await backend.apply_context_delta(
            ROOT, CHILD, request, ContextDelta.model_validate(_delta_wire()), CTX
        )

    assert [item[0] for item in calls] == ["POST", "GET"]
    assert calls[0][1]["headers"] == {"If-Match": '"1"'}


@pytest.mark.asyncio
async def test_a41_unchanged_version_is_permanent_governance_rejection(monkeypatch):
    backend, calls = OrchestratorBackendClient(), []

    async def response(method, path, ctx, **kwargs):
        calls.append(method)
        if method == "POST":
            raise OrchestratorBackendConflict("governance")
        return httpx.Response(200, headers={"ETag": '"1"'}, json=_request_wire())

    monkeypatch.setattr(backend, "_request", response)
    request = VersionedContextRequest(
        value=ContextRequest.model_validate(_request_wire()), etag='"1"'
    )

    with pytest.raises(OrchestratorBackendPermanentError, match="governance"):
        await backend.apply_context_delta(
            ROOT, CHILD, request, ContextDelta.model_validate(_delta_wire()), CTX
        )
    assert calls == ["POST", "GET"]


@pytest.mark.asyncio
async def test_a41_nonready_concurrent_delta_with_null_refs_is_never_rebased(monkeypatch):
    backend, calls = OrchestratorBackendClient(), []

    async def response(method, path, ctx, **kwargs):
        calls.append(method)
        if method == "POST":
            raise OrchestratorBackendConflict("stale If-Match")
        return httpx.Response(
            200,
            headers={"ETag": '"2"'},
            json=_request_wire(version=2, current_revision=None),
        )

    monkeypatch.setattr(backend, "_request", response)
    request = VersionedContextRequest(
        value=ContextRequest.model_validate(
            _request_wire(current_revision=None)
        ),
        etag='"1"',
    )

    with pytest.raises(OrchestratorBackendConflict, match="current version 2"):
        await backend.apply_context_delta(
            ROOT, CHILD, request, ContextDelta.model_validate(_delta_wire()), CTX
        )

    assert calls == ["POST", "GET"]


@pytest.mark.asyncio
async def test_a42_concurrent_context_revision_is_not_silently_overwritten(monkeypatch):
    backend = OrchestratorBackendClient()

    async def response(method, path, ctx, **kwargs):
        if method == "POST":
            raise OrchestratorBackendConflict("stale")
        return httpx.Response(
            200, headers={"ETag": '"2"'},
            json=_request_wire(version=2, current_revision=2),
        )

    monkeypatch.setattr(backend, "_request", response)
    request = VersionedContextRequest(
        value=ContextRequest.model_validate(_request_wire()), etag='"1"'
    )
    with pytest.raises(OrchestratorBackendConflict, match="current version 2"):
        await backend.apply_context_delta(
            ROOT, CHILD, request, ContextDelta.model_validate(_delta_wire()), CTX
        )


@pytest.mark.asyncio
async def test_a43_role_view_is_adopted_only_from_backend_delta_response(monkeypatch):
    backend, sent = OrchestratorBackendClient(), {}

    async def response(method, path, ctx, **kwargs):
        sent.update(kwargs["json"])
        return httpx.Response(200, headers={"ETag": '"2"'}, json=_revision_wire())

    monkeypatch.setattr(backend, "_request", response)
    request = VersionedContextRequest(
        value=ContextRequest.model_validate(_request_wire()), etag='"1"'
    )
    result = await backend.apply_context_delta(
        ROOT, CHILD, request, ContextDelta.model_validate(_delta_wire()), CTX
    )

    assert set(sent) == {"definition", "evidence", "views", "measurements", "as_of", "expires_at"}
    assert result.revision.context_ref.view_id == VIEW


@pytest.mark.asyncio
async def test_a43_delta_cannot_choose_a_view_other_than_backend_issued_role(monkeypatch):
    backend = OrchestratorBackendClient()
    monkeypatch.setattr(backend, "_request", lambda *_args, **_kwargs: pytest.fail("HTTP must not run"))
    request = VersionedContextRequest(
        value=ContextRequest.model_validate(_request_wire()), etag='"1"'
    )
    delta = _delta_wire()
    delta["views"] = [{"view_type": "verifier", "definition": {}}]

    with pytest.raises(OrchestratorBackendPermanentError, match="Backend-issued task role"):
        await backend.apply_context_delta(
            ROOT, CHILD, request, ContextDelta.model_validate(delta), CTX
        )


def test_a44_delta_wire_rejects_arbitrary_context_event_or_trust_fields():
    for key in ("events", "event_type", "root_run_id", "child_id", "role", "base_context_ref"):
        with pytest.raises(ValidationError):
            ContextDelta.model_validate(_delta_wire() | {key: "forged"})


@pytest.mark.asyncio
async def test_a44_workflow_cannot_append_backend_owned_context_events(monkeypatch):
    backend = OrchestratorBackendClient()
    monkeypatch.setattr(backend, "get_root", lambda *_args: pytest.fail("must reject before HTTP"))
    claim = RootCommandClaim(
        command_id=REQUEST, run_id=ROOT, command_type="start",
        claim_token="claim", claim_expires_at="2026-01-01T00:00:00Z",
        lease_generation=1, snapshot_hash="a" * 64,
        snapshot_canonical_base64="e30=",
    )
    with pytest.raises(OrchestratorBackendPermanentError, match="Backend-owned context events"):
        await backend.transition_root(
            claim, CTX, to_status="completed", result=None,
            error_code=None, error_message=None,
            events=[{"event_type": "context.delta_applied", "payload": {}}],
        )


@pytest.mark.asyncio
async def test_a45_backend_readiness_is_returned_without_workflow_recalculation(monkeypatch):
    backend = OrchestratorBackendClient()
    async def response(*_args, **_kwargs):
        return httpx.Response(200, headers={"ETag": '"2"'}, json=_revision_wire(status="NEEDS_CLARIFICATION"))
    monkeypatch.setattr(backend, "_request", response)
    request = VersionedContextRequest(value=ContextRequest.model_validate(_request_wire()), etag='"1"')

    result = await backend.apply_context_delta(ROOT, CHILD, request, ContextDelta.model_validate(_delta_wire()), CTX)

    assert result.revision.status == "NEEDS_CLARIFICATION"


@pytest.mark.asyncio
async def test_a46_both_flags_are_required_before_task_local_api_is_called(monkeypatch):
    backend = OrchestratorBackendClient()
    monkeypatch.setattr(settings, "context_enrichment_enabled", False)
    monkeypatch.setattr(backend, "_request", lambda *_args, **_kwargs: pytest.fail("HTTP must not run"))
    with pytest.raises(OrchestratorBackendPermanentError, match="disabled"):
        await backend.create_context_request(ROOT, CHILD, CTX)


@pytest.mark.asyncio
@pytest.mark.parametrize("status", [400, 404, 428])
async def test_a41_contract_rejections_are_permanent_and_never_retried(monkeypatch, status):
    class Client:
        def __init__(self): self.calls = 0
        async def request(self, *_args, **_kwargs):
            self.calls += 1
            return httpx.Response(
                status,
                request=httpx.Request("POST", "http://backend/test"),
                json={
                    "timestamp": "2026-01-01T00:00:00Z",
                    "status": status,
                    "message": "contract rejected",
                    "fieldErrors": {},
                },
            )

    client, backend = Client(), OrchestratorBackendClient()
    monkeypatch.setattr("app.runtime.orchestrator_backend.get_client", lambda: client)

    with pytest.raises(OrchestratorBackendPermanentError, match=f"HTTP {status}"):
        await backend.create_context_request(ROOT, CHILD, CTX)
    assert client.calls == 1


@pytest.mark.asyncio
async def test_production_runtime_compiles_internal_skill_and_uses_exact_backend_contract(monkeypatch):
    calls = []

    def handler(request: httpx.Request) -> httpx.Response:
        calls.append((request.method, request.url.path, dict(request.headers), request.content))
        if request.method == "POST" and request.url.path.endswith("/context-requests"):
            assert request.content == b""
            return httpx.Response(200, headers={"ETag": '"1"'}, json=_request_wire())
        if request.method == "POST" and request.url.path.endswith(f"/{REQUEST}/deltas"):
            assert request.headers["if-match"] == '"1"'
            assert set(json.loads(request.content)) == {
                "definition", "evidence", "views", "measurements", "as_of", "expires_at",
            }
            return httpx.Response(200, headers={"ETag": '"2"'}, json=_revision_wire())
        return httpx.Response(
            404,
            json={
                "timestamp": "2026-01-01T00:00:00Z", "status": 404,
                "message": "unexpected route", "fieldErrors": {},
            },
        )

    http = httpx.AsyncClient(
        transport=httpx.MockTransport(handler), base_url="http://backend"
    )
    monkeypatch.setattr("app.runtime.orchestrator_backend.get_client", lambda: http)
    runtime = TaskLocalContextRuntime()
    try:
        created = await runtime.execute(
            {"operation": "create", "root_run_id": ROOT, "child_id": CHILD}, CTX
        )
        applied = await runtime.execute(
            {
                "operation": "apply",
                "root_run_id": ROOT,
                "child_id": CHILD,
                "request": created["context_request"],
                "delta": _delta_wire(),
            },
            CTX,
        )
    finally:
        await http.aclose()

    expected_base = f"/api/orchestrator-runs/{ROOT}/children/{CHILD}/context-requests"
    assert [(method, path) for method, path, _, _ in calls] == [
        ("POST", expected_base), ("POST", f"{expected_base}/{REQUEST}/deltas")
    ]
    assert applied["context_delta_result"]["revision"]["status"] == "READY"


def test_task_local_skill_cannot_be_invoked_through_public_api():
    response = client.post(
        "/skills/context-task-local/invoke",
        json={"input": {"task_context_job": {}}},
        headers=auth_headers(role="ADMIN"),
    )

    assert response.status_code == 404


@pytest.mark.asyncio
async def test_trusted_node_uses_explicit_child_execution_context():
    class Port:
        async def create_context_request(self, root, child, ctx):
            assert (root, child, ctx) == (ROOT, CHILD, CTX)
            return VersionedContextRequest(
                value=ContextRequest.model_validate(_request_wire()), etag='"1"'
            )

    node = node_registry.get("context_task_local_update").build(
        type("Deps", (), {"context_task_backend": Port()})()
    )
    output = await node({
        "task_context_job": {"operation": "create", "root_run_id": ROOT, "child_id": CHILD},
        "tenant_id": "tenant", "user_id": "user", "role": "USER",
    })

    assert output["context_request"]["value"]["task_id"] == "task-1"
