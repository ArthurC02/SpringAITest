"""Backend-owned policy/store ports used by context-enrichment nodes."""

from __future__ import annotations

from typing import TYPE_CHECKING, Any, Protocol

if TYPE_CHECKING:
    from app.runtime.orchestrator_backend import (
        AppliedContextDelta, ContextDelta, VersionedContextRequest,
    )
    from app.security import RequestContext


class ContextPolicyPort(Protocol):
    async def get_active(self, *, tenant_id: str, user_id: str, role: str) -> dict[str, Any]: ...


class ContextStorePort(Protocol):
    async def submit_revision(
        self,
        *,
        context_id: str,
        tenant_id: str,
        user_id: str,
        role: str,
        candidate: dict[str, Any],
    ) -> dict[str, Any]: ...


class ContextRetrievalPort(Protocol):
    async def retrieve(
        self,
        *,
        query: str,
        tenant_id: str,
        knowledge_sources: list[str],
        adapter_id: str,
        timeout_seconds: float,
    ) -> list[dict[str, Any]]: ...


class TaskContextPort(Protocol):
    async def create_context_request(
        self, root_run_id: str, child_id: str, ctx: "RequestContext"
    ) -> "VersionedContextRequest": ...

    async def apply_context_delta(
        self, root_run_id: str, child_id: str,
        request: "VersionedContextRequest", delta: "ContextDelta",
        ctx: "RequestContext",
    ) -> "AppliedContextDelta": ...
