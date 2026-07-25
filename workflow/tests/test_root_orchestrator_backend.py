from __future__ import annotations

import httpx
import pytest

from app.runtime.orchestrator_backend import OrchestratorBackendClient
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


@pytest.mark.asyncio
async def test_backend_conflict_is_fail_closed(monkeypatch):
    async def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(409, request=request)

    client = httpx.AsyncClient(
        transport=httpx.MockTransport(handler), base_url="http://backend"
    )
    monkeypatch.setattr(
        "app.runtime.orchestrator_backend.get_client", lambda: client
    )
    try:
        with pytest.raises(Exception, match="changed concurrently"):
            await OrchestratorBackendClient().claim(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                RequestContext(tenant_id="tenant", user_id="user", role="USER"),
            )
    finally:
        await client.aclose()
