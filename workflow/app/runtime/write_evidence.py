"""The D7 write-evidence adapter is Backend-owned, not process-local.

The Backend effect ledger allocates the only idempotency identity.  This thin
adapter merely carries that identity to Backend's transactional effect/outbox
endpoint, so a Workflow restart can replay a call without producing a second
business write.
"""
from __future__ import annotations

from typing import TYPE_CHECKING

import httpx

from app.backend_http import get_client
from app.settings import settings

if TYPE_CHECKING:
    from app.engine.tool_registry import ToolContext


class WriteEvidenceError(RuntimeError):
    pass


class BackendWriteEvidenceSink:
    async def write(
        self,
        ctx: ToolContext,
        record_id: str,
        value: str,
        *,
        effect_id: str,
    ) -> int:
        if not effect_id or not ctx.run_id:
            raise WriteEvidenceError("durable run and effect identities are required")
        headers = {
            "X-Internal-Token": settings.internal_api_token,
            "X-Tenant-Id": ctx.tenant_id,
            "X-User-Id": ctx.user_id,
            "X-User-Role": ctx.role,
        }
        try:
            response = await get_client().post(
                f"/api/agent-runs/{ctx.run_id}/write-effects/{effect_id}/evidence",
                headers=headers,
                json={"record_id": record_id, "value": value},
                timeout=httpx.Timeout(10.0),
            )
            if response.status_code in {404, 409}:
                raise WriteEvidenceError("durable write evidence was rejected")
            response.raise_for_status()
            body = response.json()
            version = body.get("version") if isinstance(body, dict) else None
            # `bool` is an `int` subclass, so JSON `true` would otherwise be
            # accepted as version 1 and handed back as a durable version number.
            if not isinstance(version, int) or isinstance(version, bool) or version < 1:
                raise WriteEvidenceError("Backend returned invalid durable evidence")
            return version
        except WriteEvidenceError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise WriteEvidenceError("durable write evidence is unavailable") from exc
