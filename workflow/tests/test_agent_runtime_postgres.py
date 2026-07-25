from __future__ import annotations

import os
import uuid
from typing import TypedDict

import pytest
from langgraph.graph import END, START, StateGraph
from langgraph.types import Command, interrupt

from app.runtime.checkpoints import PostgresCheckpointStore


class State(TypedDict, total=False):
    value: str


@pytest.mark.asyncio
async def test_postgres_checkpoint_survives_saver_reopen() -> None:
    dsn = os.getenv("CHECKPOINT_DATABASE_URL")
    if not dsn:
        pytest.skip("CHECKPOINT_DATABASE_URL is absent")

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
