"""HTTP adapters for the server-owned Context APIs."""

from __future__ import annotations

import asyncio
from typing import Any

from app.backend_http import get_client, internal_headers, search_chunks_scoped
from app.security import RequestContext


class BackendContextPolicy:
    async def get_active(self, *, ctx: RequestContext) -> dict[str, Any]:
        response = await get_client().get(
            "/api/context-policies",
            headers=internal_headers(ctx),
        )
        response.raise_for_status()
        payload = response.json()
        if not isinstance(payload, dict):
            raise ValueError("Backend returned an invalid context policy")
        return payload


class BackendContextStore:
    async def submit_revision(
        self,
        *,
        context_id: str,
        ctx: RequestContext,
        candidate: dict[str, Any],
    ) -> dict[str, Any]:
        response = await get_client().post(
            f"/api/contexts/{context_id}/revisions",
            json=candidate,
            headers=internal_headers(ctx),
        )
        response.raise_for_status()
        payload = response.json()
        if not isinstance(payload, dict):
            raise ValueError("Backend returned an invalid context revision")
        return payload


class BackendContextRetrieval:
    def __init__(self, top_k: int) -> None:
        self._top_k = top_k

    async def retrieve(
        self,
        *,
        query: str,
        tenant_id: str,
        knowledge_sources: list[str],
        adapter_id: str,
        timeout_seconds: float,
    ) -> list[dict[str, Any]]:
        if adapter_id != "backend.retrieval_search":
            raise ValueError("Unsupported context retrieval adapter")
        async with asyncio.timeout(timeout_seconds):
            return await search_chunks_scoped(query, self._top_k, tenant_id, knowledge_sources)
