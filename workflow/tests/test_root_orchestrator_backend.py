from __future__ import annotations

import httpx
import pytest

from app.runtime.orchestrator_backend import (
    OrchestratorBackendClient,
    OrchestratorBackendConflict,
    OrchestratorBackendError,
    OrchestratorBackendPermanentError,
    RootCommandClaim,
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


async def _claim_json(monkeypatch, body: dict) -> RootCommandClaim | None:
    async def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json=body, request=request)

    client = httpx.AsyncClient(
        transport=httpx.MockTransport(handler), base_url="http://backend"
    )
    monkeypatch.setattr(
        "app.runtime.orchestrator_backend.get_client", lambda: client
    )
    try:
        return await OrchestratorBackendClient().claim(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            RequestContext(tenant_id="tenant", user_id="user", role="USER"),
        )
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_claim_parses_the_backend_issued_command_claim(monkeypatch):
    claim = await _claim_json(
        monkeypatch,
        {
            "command_id": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            "run_id": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "command_type": "start",
            "claim_token": "claim",
            "claim_expires_at": "2026-01-01T00:00:00Z",
            "lease_generation": 3,
            "snapshot_hash": "a" * 64,
            "snapshot_canonical_base64": "e30=",
        },
    )

    assert claim is not None
    assert claim.command_id == "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
    assert claim.run_id == "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
    assert claim.command_type == "start"
    assert claim.claim_token == "claim"
    assert claim.lease_generation == 3
    assert claim.snapshot_hash == "a" * 64
    # Optional wire fields are absent here: a claim without a checkpoint or
    # resume payload is a valid start claim, not a schema error.
    assert claim.resume_input is None
    assert claim.checkpoint_ref is None
    assert claim.checkpoint_version is None
    assert claim.deadline_at is None


@pytest.mark.asyncio
async def test_claim_body_failing_the_wire_schema_stays_retryable(monkeypatch):
    with pytest.raises(OrchestratorBackendError, match="invalid Root claim") as raised:
        await _claim_json(monkeypatch, {})
    # Unlike a bad snapshot envelope, an unparsable claim body is not permanent.
    assert not isinstance(raised.value, OrchestratorBackendPermanentError)
    assert not isinstance(raised.value, OrchestratorBackendConflict)


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


@pytest.mark.asyncio
async def test_claim_rejects_a_non_uuid_id_before_reaching_backend(monkeypatch):
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
        with pytest.raises(OrchestratorBackendError, match="not a UUID") as raised:
            await OrchestratorBackendClient().claim(
                "not-a-uuid",
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                RequestContext(tenant_id="tenant", user_id="user", role="USER"),
            )
    finally:
        await client.aclose()

    # A malformed id never reaches the URL: no request is issued at all.
    assert seen is None
    assert not isinstance(raised.value, OrchestratorBackendPermanentError)
