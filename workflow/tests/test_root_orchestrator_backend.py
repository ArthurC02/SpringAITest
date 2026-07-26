from __future__ import annotations

import httpx
import pytest

from app.runtime.orchestrator_backend import (
    OrchestratorBackendClient,
    OrchestratorBackendConflict,
    OrchestratorBackendError,
    OrchestratorBackendPermanentError,
)
from app.security import RequestContext


@pytest.mark.asyncio
async def test_claim_uses_internal_identity_and_returns_none_for_contended_claim(
    monkeypatch,
):
    seen: httpx.Request | None = None

    async def handler(request: httpx.Request) -> httpx.Response:
        nonlocal seen
        seen = request
        return httpx.Response(204, request=request)

    client = httpx.AsyncClient(
        transport=httpx.MockTransport(handler), base_url="http://backend"
    )
    monkeypatch.setattr(
        "app.runtime.orchestrator_backend.get_client", lambda: client
    )
    try:
        result = await OrchestratorBackendClient(owner="workflow:test").claim(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            RequestContext(tenant_id="tenant", user_id="user", role="USER"),
        )
    finally:
        await client.aclose()

    assert result is None
    assert seen is not None
    assert seen.headers["X-Tenant-Id"] == "tenant"
    assert seen.headers["X-User-Id"] == "user"
    assert seen.headers["X-User-Role"] == "USER"
    assert seen.headers["X-Internal-Token"]
    assert b'"worker_id":"workflow:test"' in seen.content


async def _claim_status(monkeypatch, status_code: int) -> None:
    async def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(status_code, request=request)

    client = httpx.AsyncClient(
        transport=httpx.MockTransport(handler), base_url="http://backend"
    )
    monkeypatch.setattr(
        "app.runtime.orchestrator_backend.get_client", lambda: client
    )
    try:
        await OrchestratorBackendClient().claim(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            RequestContext(tenant_id="tenant", user_id="user", role="USER"),
        )
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_backend_conflict_is_fail_closed(monkeypatch):
    with pytest.raises(OrchestratorBackendConflict, match="changed concurrently") as raised:
        await _claim_status(monkeypatch, 409)
    # A conflict is retryable by recovery; only a permanent error cancels.
    assert not isinstance(raised.value, OrchestratorBackendPermanentError)


@pytest.mark.asyncio
@pytest.mark.parametrize("status_code", [400, 404, 413, 422, 428])
async def test_backend_contract_rejections_are_permanent_and_never_retried(
    monkeypatch, status_code
):
    with pytest.raises(OrchestratorBackendPermanentError, match=f"HTTP {status_code}"):
        await _claim_status(monkeypatch, status_code)


@pytest.mark.asyncio
@pytest.mark.parametrize("status_code", [429, 500, 503])
async def test_backend_transport_failures_stay_retryable(monkeypatch, status_code):
    with pytest.raises(OrchestratorBackendError) as raised:
        await _claim_status(monkeypatch, status_code)
    assert not isinstance(raised.value, OrchestratorBackendPermanentError)
    assert not isinstance(raised.value, OrchestratorBackendConflict)
