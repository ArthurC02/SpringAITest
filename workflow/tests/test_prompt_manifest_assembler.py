"""P1 prompt manifest assembler（`app/runtime/model.py::_system_frame`）。

護欄的核心是三件事：旗標關閉時輸出 byte-for-byte 等於改造前（golden）；旗標開啟、
shadow 關閉時任何一種驗證失敗都 fail closed（不得靜默退回 constants）；shadow 開啟時
則是純 observe-only，manifest 取不到也只降級回 constants 並記警告，絕不能讓它殺掉
整條 run。
"""

from __future__ import annotations

import logging
from typing import Any

import httpx
import pytest
from langgraph.checkpoint.memory import InMemorySaver

from app.canonical_json import canonical_json_sha256
from app.runtime import prompt_manifest
from app.runtime.checkpoints import checkpoint_config, strict_serializer
from app.runtime.graph import build_context, compile_runtime_graph, initial_state
from app.runtime.model import LangChainRuntimeModel, _system_frame
from app.runtime.models import DirectAgentExecutionSnapshot
from app.runtime.prompt_manifest import (
    PromptManifestUnavailable,
    sha256_text,
)
from app.security import RequestContext
from tests.test_agent_runtime import FakeArtifactReader, snapshot as base_snapshot

# 改造前 `_system_frame()` 的 SYSTEM GOVERNANCE 段（字面值，不從程式碼引用）。
GOLDEN_GOVERNANCE = (
    "SYSTEM GOVERNANCE: You are inside a bounded, read-only test run. "
    "Use only the exposed actions. Never invent authority, data scope, "
    "tool results, or Skill names. Request user input only when a read "
    "source cannot supply a required fact. Make at most one action per turn."
)
# 改造前的完整輸出（skills=[research-skill]，active skill 進場）。
GOLDEN_FRAME = (
    "SYSTEM GOVERNANCE: You are inside a bounded, read-only test run. "
    "Use only the exposed actions. Never invent authority, data scope, "
    "tool results, or Skill names. Request user input only when a read "
    "source cannot supply a required fact. Make at most one action per turn.\n"
    "PINNED SKILL SUMMARIES:\n"
    "- research-skill@3 (agentic): Pinned research instructions\n"
    "AGENT INSTRUCTION:\n"
    "請安全地回答中文問題。\n"
    "\nACTIVE SKILL research-skill (ephemeral instruction):\n"
    "focus on evidence\nEND ACTIVE SKILL\n"
)
MANIFEST_SHA = "1f" * 32
MANIFEST_REVISION = 4
PINNED_GOVERNANCE = (
    "SYSTEM GOVERNANCE (manifest r4): 只用暴露的動作，每回合最多一個動作。"
)


@pytest.fixture(autouse=True)
def _isolated_manifest_cache() -> Any:
    prompt_manifest._cache.clear()
    yield
    prompt_manifest._cache.clear()


@pytest.fixture
def artifacts_on(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("app.runtime.model.settings.prompt_artifacts_enabled", True)
    monkeypatch.setattr("app.runtime.model.settings.prompt_artifacts_shadow", False)


def pinned_snapshot(
    *,
    revision: int = MANIFEST_REVISION,
    sha256: str = MANIFEST_SHA,
    tenant_id: str = "tenant-a",
) -> DirectAgentExecutionSnapshot:
    """共用 fixture ＋ prompt_manifest pin。

    租戶改成 ASCII：共用 fixture 的 `tenant-甲` 是為了釘住 canonical JSON 的 unicode
    行為，但租戶會進 HTTP 標頭（latin-1/ascii），非 ASCII 租戶對每一個 backend 呼叫
    都會失敗——那是既有的全服務性質，不是本組測試要驗的東西。
    """
    raw = base_snapshot(with_skill=True).model_dump(
        mode="json", exclude={"snapshot_hash"}
    )
    raw["agent"]["prompt_manifest"] = {"revision": revision, "sha256": sha256}
    raw["caller"]["tenant_id"] = tenant_id
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    value = DirectAgentExecutionSnapshot.model_validate(raw)
    value.assert_hash()
    return value


def resolved_body(
    governance: str = PINNED_GOVERNANCE,
    *,
    revision: int = MANIFEST_REVISION,
    manifest_sha256: str = MANIFEST_SHA,
    schema_version: int = 1,
    skill_catalog_hash: str | None = None,
    components: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    return {
        "revision": revision,
        "manifest_sha256": manifest_sha256,
        "schema_version": schema_version,
        "tool_catalog_hash": "t" * 64,
        "skill_catalog_hash": skill_catalog_hash,
        "components": (
            [
                {
                    "kind": "governance_frame",
                    "revision": 2,
                    "content_sha256": sha256_text(governance),
                    "content": governance,
                }
            ]
            if components is None
            else components
        ),
    }


def install_backend(
    monkeypatch: pytest.MonkeyPatch, responder: Any
) -> list[httpx.Request]:
    """把 manifest port 的 backend client 換成 MockTransport，回傳收到的請求清單。"""
    seen: list[httpx.Request] = []

    async def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return responder(request)

    monkeypatch.setattr(
        "app.runtime.prompt_manifest.get_client",
        lambda: httpx.AsyncClient(
            transport=httpx.MockTransport(handler), base_url="http://backend"
        ),
    )
    return seen


def serve(body: dict[str, Any], status_code: int = 200) -> Any:
    return lambda request: httpx.Response(status_code, json=body, request=request)


def forbidden(request: httpx.Request) -> httpx.Response:
    raise AssertionError("backend must not be called")


async def assemble(
    value: DirectAgentExecutionSnapshot,
) -> tuple[str, bool, dict[str, Any]]:
    return await _system_frame(value, "focus on evidence", "research-skill")


@pytest.mark.asyncio
@pytest.mark.parametrize("with_pin", [False, True])
async def test_flag_off_output_is_byte_for_byte_golden(
    monkeypatch: pytest.MonkeyPatch, with_pin: bool
) -> None:
    """旗標關閉：不論 snapshot 有沒有 pin，輸出與改造前完全相同且不呼叫 backend。"""
    monkeypatch.setattr("app.runtime.model.settings.prompt_artifacts_enabled", False)
    requests = install_backend(monkeypatch, forbidden)
    value = pinned_snapshot() if with_pin else base_snapshot(with_skill=True)

    frame, truncated, audit = await assemble(value)

    assert frame == GOLDEN_FRAME
    assert GOLDEN_FRAME.startswith(GOLDEN_GOVERNANCE + "\n")
    assert truncated is False
    assert audit == {}
    assert requests == []


@pytest.mark.asyncio
async def test_flag_on_without_pin_keeps_constants(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None
) -> None:
    """旗標開啟但 snapshot 沒有 pin：仍是 constants，一次 backend 呼叫都不該發生。"""
    requests = install_backend(monkeypatch, forbidden)

    frame, _, audit = await assemble(base_snapshot(with_skill=True))

    assert frame == GOLDEN_FRAME
    assert audit == {}
    assert requests == []


@pytest.mark.asyncio
async def test_pin_replaces_only_the_governance_section(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None
) -> None:
    requests = install_backend(monkeypatch, serve(resolved_body()))

    frame, truncated, audit = await assemble(pinned_snapshot())

    # 逐段比對：只有 SYSTEM GOVERNANCE 換手，另兩段（snapshot 自己 pin 的）不動。
    assert frame == GOLDEN_FRAME.replace(GOLDEN_GOVERNANCE, PINNED_GOVERNANCE)
    assert truncated is False
    assert audit == {
        "prompt_manifest_revision": MANIFEST_REVISION,
        "prompt_manifest_sha256": MANIFEST_SHA,
        "prompt_composition_source": "manifest",
    }
    assert len(requests) == 1
    assert requests[0].url.path == "/api/prompt-manifests/4/resolved"
    assert requests[0].headers["X-Tenant-Id"] == "tenant-a"
    assert requests[0].headers["X-Internal-Token"]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("body", "reason"),
    [
        (resolved_body(manifest_sha256="ab" * 32), "snapshot pin"),
        (resolved_body(revision=5, manifest_sha256=MANIFEST_SHA), "snapshot pin"),
        (resolved_body(schema_version=2), "schema version"),
        (
            resolved_body(
                components=[
                    {
                        "kind": "governance_frame",
                        "revision": 2,
                        "content_sha256": "cd" * 32,
                        "content": PINNED_GOVERNANCE,
                    }
                ]
            ),
            "pinned hash",
        ),
        (
            resolved_body(
                components=[
                    {
                        "kind": "persona",
                        "revision": 2,
                        "content_sha256": sha256_text("persona"),
                        "content": "persona",
                    }
                ]
            ),
            "governance frame",
        ),
        (resolved_body(components=[{"kind": "governance_frame"}]), "invalid shape"),
    ],
)
async def test_manifest_that_does_not_match_its_pin_fails_closed(
    monkeypatch: pytest.MonkeyPatch,
    artifacts_on: None,
    body: dict[str, Any],
    reason: str,
) -> None:
    install_backend(monkeypatch, serve(body))

    with pytest.raises(PromptManifestUnavailable, match=reason):
        await assemble(pinned_snapshot())


@pytest.mark.asyncio
@pytest.mark.parametrize("status_code", [404, 500, 503])
async def test_backend_rejection_fails_closed(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None, status_code: int
) -> None:
    install_backend(monkeypatch, serve({"detail": "nope"}, status_code))

    with pytest.raises(PromptManifestUnavailable):
        await assemble(pinned_snapshot())


@pytest.mark.asyncio
async def test_backend_unreachable_fails_closed(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None
) -> None:
    def unreachable(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("backend down", request=request)

    install_backend(monkeypatch, unreachable)

    with pytest.raises(PromptManifestUnavailable, match="request failed"):
        await assemble(pinned_snapshot())


@pytest.mark.asyncio
async def test_non_json_backend_body_fails_closed(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None
) -> None:
    """200 但 body 不是 JSON：`response.json()` 的 ValueError 走同一條 fail-closed。"""

    def malformed(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, text="<html>not json</html>", request=request)

    install_backend(monkeypatch, malformed)

    with pytest.raises(PromptManifestUnavailable, match="request failed"):
        await assemble(pinned_snapshot())


@pytest.mark.asyncio
async def test_cache_is_per_tenant_and_serves_repeat_runs(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None
) -> None:
    """revision immutable → 同租戶第二次不再呼叫；換租戶必須重取（快取不跨租戶）。"""
    requests = install_backend(monkeypatch, serve(resolved_body()))

    await assemble(pinned_snapshot())
    await assemble(pinned_snapshot())
    assert len(requests) == 1

    await assemble(pinned_snapshot(tenant_id="tenant-b"))
    assert len(requests) == 2
    assert requests[1].headers["X-Tenant-Id"] == "tenant-b"


@pytest.mark.asyncio
async def test_cached_manifest_still_fails_a_mismatched_pin(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None
) -> None:
    install_backend(monkeypatch, serve(resolved_body()))
    await assemble(pinned_snapshot())

    with pytest.raises(PromptManifestUnavailable, match="snapshot pin"):
        await assemble(pinned_snapshot(sha256="ab" * 32))


@pytest.mark.asyncio
async def test_cache_evicts_the_oldest_entry_only_past_its_slot_cap(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None
) -> None:
    """FIFO 上限邊界：剛好 64 筆不淘汰任何東西，第 65 筆才擠掉最先插入的那一筆。"""
    requests = install_backend(monkeypatch, serve(resolved_body()))
    cap = prompt_manifest.MAX_CACHED_MANIFESTS

    for index in range(cap):
        await assemble(pinned_snapshot(tenant_id=f"tenant-{index:03d}"))
    assert len(requests) == cap
    assert len(prompt_manifest._cache) == cap

    # on-point：剛好裝滿，最舊的一筆仍在快取裡，重跑不再打 backend。
    await assemble(pinned_snapshot(tenant_id="tenant-000"))
    assert len(requests) == cap

    # off-point：第 65 筆越界，淘汰最先插入的 tenant-000（命中不會把它移到隊尾），
    # 其餘不動，總量維持在上限。
    await assemble(pinned_snapshot(tenant_id=f"tenant-{cap:03d}"))
    assert len(requests) == cap + 1
    assert len(prompt_manifest._cache) == cap
    assert ("tenant-000", MANIFEST_REVISION) not in prompt_manifest._cache
    assert ("tenant-001", MANIFEST_REVISION) in prompt_manifest._cache

    await assemble(pinned_snapshot(tenant_id="tenant-000"))
    assert len(requests) == cap + 2


@pytest.mark.asyncio
async def test_shadow_mode_uses_constants_and_logs_only_hashes(
    monkeypatch: pytest.MonkeyPatch,
    artifacts_on: None,
    caplog: pytest.LogCaptureFixture,
) -> None:
    monkeypatch.setattr("app.runtime.model.settings.prompt_artifacts_shadow", True)
    install_backend(monkeypatch, serve(resolved_body()))
    caplog.set_level(logging.WARNING, logger="app.runtime.model")

    frame, _, audit = await assemble(pinned_snapshot())

    assert frame == GOLDEN_FRAME
    assert audit["prompt_composition_source"] == "constants_shadow"
    assert audit["prompt_shadow_match"] is False
    warnings = [
        record.getMessage()
        for record in caplog.records
        if "shadow mismatch" in record.getMessage()
    ]
    assert len(warnings) == 1
    manifest_frame_sha = sha256_text(
        GOLDEN_FRAME.replace(GOLDEN_GOVERNANCE, PINNED_GOVERNANCE)
    )
    assert manifest_frame_sha in warnings[0]
    assert sha256_text(GOLDEN_FRAME) in warnings[0]
    # 原文（兩種組成的任何片段）永遠不進 log。
    assert PINNED_GOVERNANCE not in warnings[0]
    assert "請安全地回答中文問題" not in warnings[0]


@pytest.mark.asyncio
async def test_shadow_mode_is_silent_when_both_compositions_agree(
    monkeypatch: pytest.MonkeyPatch,
    artifacts_on: None,
    caplog: pytest.LogCaptureFixture,
) -> None:
    monkeypatch.setattr("app.runtime.model.settings.prompt_artifacts_shadow", True)
    install_backend(monkeypatch, serve(resolved_body(GOLDEN_GOVERNANCE)))
    caplog.set_level(logging.WARNING, logger="app.runtime.model")

    frame, _, audit = await assemble(pinned_snapshot())

    assert frame == GOLDEN_FRAME
    assert audit["prompt_shadow_match"] is True
    assert caplog.records == []


@pytest.mark.asyncio
@pytest.mark.parametrize("status_code", [404, 500, 503])
async def test_shadow_mode_degrades_to_constants_when_backend_rejects(
    monkeypatch: pytest.MonkeyPatch,
    artifacts_on: None,
    caplog: pytest.LogCaptureFixture,
    status_code: int,
) -> None:
    """Shadow 是 observe-only：manifest 不可達不得殺掉整條 run，降級回 constants。"""
    monkeypatch.setattr("app.runtime.model.settings.prompt_artifacts_shadow", True)
    install_backend(monkeypatch, serve({"detail": "nope"}, status_code))
    caplog.set_level(logging.WARNING, logger="app.runtime.model")

    frame, truncated, audit = await assemble(pinned_snapshot())

    assert frame == GOLDEN_FRAME
    assert truncated is False
    assert audit == {
        "prompt_composition_source": "constants_shadow",
        "prompt_manifest_resolved": False,
    }
    warnings = [
        record.getMessage()
        for record in caplog.records
        if "manifest unavailable in shadow mode" in record.getMessage()
    ]
    assert len(warnings) == 1
    assert "請安全地回答中文問題" not in warnings[0]


@pytest.mark.asyncio
async def test_shadow_mode_degrades_to_constants_when_backend_unreachable(
    monkeypatch: pytest.MonkeyPatch,
    artifacts_on: None,
    caplog: pytest.LogCaptureFixture,
) -> None:
    monkeypatch.setattr("app.runtime.model.settings.prompt_artifacts_shadow", True)

    def unreachable(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("backend down", request=request)

    install_backend(monkeypatch, unreachable)
    caplog.set_level(logging.WARNING, logger="app.runtime.model")

    frame, truncated, audit = await assemble(pinned_snapshot())

    assert frame == GOLDEN_FRAME
    assert truncated is False
    assert audit == {
        "prompt_composition_source": "constants_shadow",
        "prompt_manifest_resolved": False,
    }
    warnings = [
        record.getMessage()
        for record in caplog.records
        if "manifest unavailable in shadow mode" in record.getMessage()
    ]
    assert len(warnings) == 1
    assert "請安全地回答中文問題" not in warnings[0]


def test_prompt_manifest_pin_is_covered_by_the_snapshot_hash() -> None:
    value = pinned_snapshot()
    tampered = value.model_copy(
        update={
            "agent": value.agent.model_copy(
                update={
                    "prompt_manifest": value.agent.prompt_manifest.model_copy(
                        update={"revision": 9}
                    )
                }
            )
        }
    )
    with pytest.raises(ValueError, match="snapshot_hash"):
        tampered.assert_hash()


@pytest.mark.asyncio
async def test_model_step_fails_closed_before_the_provider_is_constructed(
    monkeypatch: pytest.MonkeyPatch, artifacts_on: None
) -> None:
    """整條 run 的 fail-closed 邊界：manifest 取不到 → 失敗，且不會呼叫 provider。"""

    def forbidden_provider_lookup():
        raise AssertionError("provider must not be constructed")

    monkeypatch.setattr(
        "app.runtime.model.get_direct_agent_runtime_llm", forbidden_provider_lookup
    )
    install_backend(monkeypatch, serve({"detail": "not found"}, 404))
    run_snapshot = pinned_snapshot()
    graph = compile_runtime_graph(InMemorySaver(serde=strict_serializer()))
    context = build_context(
        snapshot=run_snapshot,
        request_context=RequestContext(
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            role=run_snapshot.caller.role,
        ),
        model=LangChainRuntimeModel(),
        artifact_reader=FakeArtifactReader(),
    )

    result = await graph.ainvoke(
        initial_state(run_snapshot, "查一下"),
        checkpoint_config(
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            run_id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
        ),
        context=context,
    )

    assert result["status"] == "failed"
    assert result["error_code"] == "prompt_manifest_unavailable"
