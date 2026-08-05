from __future__ import annotations

import asyncio

import pytest
from fastapi.testclient import TestClient

from app.health import WorkflowReadinessProbe
from app.main import app
from app.settings import settings


@pytest.mark.asyncio
async def test_optional_failure_is_degraded_but_ready(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", False)
    monkeypatch.setattr(settings, "multi_agent_dispatch_enabled", False)
    probe = WorkflowReadinessProbe(
        backend_check=lambda: _result(True),
        litellm_check=lambda: _result(False),
    )

    report = await probe.check()

    assert report["ready"] is True
    assert report["status"] == "DEGRADED"
    assert report["components"]["litellm"] == {
        "status": "DEGRADED",
        "required": False,
    }


@pytest.mark.asyncio
async def test_runtime_flag_requires_checkpoint_authority(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(settings, "agent_test_run_enabled", True)
    monkeypatch.setattr(settings, "multi_agent_dispatch_enabled", False)
    probe = WorkflowReadinessProbe(
        backend_check=lambda: _result(True),
        checkpoint_check=lambda: _result(False),
        litellm_check=lambda: _result(True),
    )

    report = await probe.check()

    assert report["ready"] is False
    assert report["status"] == "DOWN"
    assert report["components"]["checkpoint_store"] == {
        "status": "DOWN",
        "required": True,
    }


@pytest.mark.asyncio
async def test_dependency_timeout_is_bounded() -> None:
    async def hangs() -> bool:
        await asyncio.sleep(10)
        return True

    assert await WorkflowReadinessProbe._bounded(hangs) is False


@pytest.mark.asyncio
async def test_cancellation_is_not_converted_to_degradation() -> None:
    started = asyncio.Event()

    async def hangs() -> bool:
        started.set()
        await asyncio.sleep(10)
        return True

    task = asyncio.create_task(WorkflowReadinessProbe._bounded(hangs))
    await started.wait()
    task.cancel()

    with pytest.raises(asyncio.CancelledError):
        await task


def test_health_routes_are_additive_anonymous_and_ready_returns_503(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class DownProbe:
        async def check(self) -> dict:
            return {
                "status": "DOWN",
                "ready": False,
                "components": {
                    "backend": {"status": "DOWN", "required": True}
                },
            }

    monkeypatch.setattr(app.state, "readiness_probe", DownProbe())
    with TestClient(app) as client:
        for path in ("/health", "/health/live"):
            response = client.get(path)
            assert response.status_code == 200
            assert response.json()["status"] == "UP"
        ready = client.get("/health/ready")
        assert ready.status_code == 503
        assert ready.json()["ready"] is False
        assert "url" not in ready.text.lower()
        assert "token" not in ready.text.lower()


async def _result(value: bool) -> bool:
    return value
