from __future__ import annotations

import asyncio
import time
from collections.abc import Awaitable, Callable
from typing import Any

import httpx
from psycopg import AsyncConnection

from app import backend_http
from app.settings import settings

Check = Callable[[], Awaitable[bool]]


class WorkflowReadinessProbe:
    """Bounded, cached readiness checks; probes never create schemas or call an LLM."""

    def __init__(
        self,
        *,
        backend_check: Check | None = None,
        checkpoint_check: Check | None = None,
        litellm_check: Check | None = None,
    ) -> None:
        self._backend_check = backend_check or self._check_backend
        self._checkpoint_check = checkpoint_check or self._check_checkpoint
        self._litellm_check = litellm_check or self._check_litellm
        self._lock = asyncio.Lock()
        self._cached: tuple[float, dict[str, Any]] | None = None

    async def check(self) -> dict[str, Any]:
        now = time.monotonic()
        if self._cached is not None and now - self._cached[0] < 2.0:
            return self._cached[1]
        async with self._lock:
            now = time.monotonic()
            if self._cached is not None and now - self._cached[0] < 2.0:
                return self._cached[1]
            checkpoint_required = (
                settings.agent_test_run_enabled
                or settings.multi_agent_dispatch_enabled
            )
            checks: list[tuple[str, bool, Check]] = [
                ("backend", True, self._backend_check),
                ("litellm", False, self._litellm_check),
            ]
            if checkpoint_required:
                checks.append(("checkpoint_store", True, self._checkpoint_check))
            results = await asyncio.gather(
                *(self._bounded(check) for _, _, check in checks)
            )
            components = {
                name: {
                    "status": "UP" if healthy else ("DOWN" if required else "DEGRADED"),
                    "required": required,
                }
                for (name, required, _), healthy in zip(checks, results, strict=True)
            }
            ready = all(
                result or not required
                for (_, required, _), result in zip(checks, results, strict=True)
            )
            degraded = any(not result for result in results)
            report = {
                "status": "DOWN" if not ready else "DEGRADED" if degraded else "UP",
                "ready": ready,
                "components": components,
            }
            self._cached = (time.monotonic(), report)
            return report

    @staticmethod
    async def _bounded(check: Check) -> bool:
        try:
            async with asyncio.timeout(1.0):
                return await check()
        except TimeoutError:
            return False
        except asyncio.CancelledError:
            raise
        except Exception:
            return False

    @staticmethod
    async def _check_backend() -> bool:
        response = await backend_http.get_client().get("/health/ready")
        return response.status_code == 200 and response.json().get("ready") is True

    @staticmethod
    async def _check_checkpoint() -> bool:
        dsn = settings.checkpoint_database_url
        if not dsn:
            return False
        async with await AsyncConnection.connect(dsn, connect_timeout=1) as connection:
            cursor = await connection.execute(
                """
                SELECT to_regclass('public.checkpoints') IS NOT NULL
                   AND to_regclass('public.workflow_root_context_checkpoint') IS NOT NULL
                """
            )
            row = await cursor.fetchone()
            return row is not None and row[0] is True

    @staticmethod
    async def _check_litellm() -> bool:
        async with httpx.AsyncClient(base_url=settings.llm_base_url) as client:
            response = await client.get("/health/liveliness")
            return response.status_code == 200


def live_report() -> dict[str, Any]:
    return {
        "status": "UP",
        "ready": True,
        "components": {"event_loop": {"status": "UP", "required": True}},
    }
