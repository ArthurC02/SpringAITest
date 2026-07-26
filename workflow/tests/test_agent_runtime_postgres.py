from __future__ import annotations

import hashlib
import hmac
import os
import uuid
from typing import TypedDict

import pytest
from langgraph.graph import END, START, StateGraph
from langgraph.types import Command, interrupt

from app.runtime.checkpoints import PostgresCheckpointStore
from app.settings import settings


class State(TypedDict, total=False):
    value: str


def _live_dsn() -> str:
    """生產唯一支援的 checkpointer 需要真 PostgreSQL；沒有 DSN 就沒有東西可驗。"""
    dsn = os.getenv("CHECKPOINT_DATABASE_URL")
    if not dsn:
        pytest.skip("CHECKPOINT_DATABASE_URL is absent")
    return dsn


@pytest.mark.asyncio
async def test_postgres_checkpoint_survives_saver_reopen() -> None:
    dsn = _live_dsn()

    def pause(state: State) -> dict[str, str]:
        resumed = interrupt({"kind": "test"})
        return {"value": str(resumed)}

    graph_builder = StateGraph(State)
    graph_builder.add_node("pause", pause)
    graph_builder.add_edge(START, "pause")
    graph_builder.add_edge("pause", END)
    config = {"configurable": {"thread_id": f"d3-live-{uuid.uuid4()}"}}

    first = PostgresCheckpointStore(dsn)
    first_saver = await first.open()
    first_graph = graph_builder.compile(checkpointer=first_saver)
    result = await first_graph.ainvoke({}, config)
    assert result["__interrupt__"]
    await first.close()

    second = PostgresCheckpointStore(dsn)
    second_saver = await second.open()
    second_graph = graph_builder.compile(checkpointer=second_saver)
    resumed = await second_graph.ainvoke(Command(resume="recovered"), config)
    assert resumed["value"] == "recovered"
    await second.close()


@pytest.mark.asyncio
async def test_root_context_checkpoint_signature_and_digest() -> None:
    """Root context 參照是 HMAC 綁定的：簽章或 payload digest 任一被動過都必須拒絕。"""
    store = PostgresCheckpointStore(_live_dsn())
    await store.open()
    payload = {"scope": "root", "值": 1, "nested": {"b": [1, 2]}}
    try:
        reference, version = await store.put_root_context_checkpoint(payload)
        assert version == 1
        assert await store.get_root_context_checkpoint(reference) == payload

        ident, digest, mac = reference.split(":")[1:]
        assert mac != "0" * 64
        with pytest.raises(ValueError, match="signature is invalid"):
            await store.get_root_context_checkpoint(f"rctx1:{ident}:{digest}:{'0' * 64}")

        # 連 MAC 一起重算的偽造 digest 仍須被 payload_sha256 一致性檢查擋下。
        forged_digest = hashlib.sha256(b"other payload").hexdigest()
        forged_mac = hmac.new(
            settings.checkpoint_hmac_key.encode(),
            f"{ident}:{forged_digest}".encode(),
            hashlib.sha256,
        ).hexdigest()
        with pytest.raises(ValueError, match="missing or corrupt"):
            await store.get_root_context_checkpoint(
                f"rctx1:{ident}:{forged_digest}:{forged_mac}"
            )

        with pytest.raises(ValueError, match="invalid Root context checkpoint"):
            await store.get_root_context_checkpoint(f"rctx2:{ident}:{digest}:{mac}")
    finally:
        await store.close()
