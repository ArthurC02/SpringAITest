"""Query Intake：登記原始問題與環境資訊，是唯一可寫入 query_id 等不可變鍵的節點。"""

import uuid
from datetime import datetime, timezone

from app.engine.node_registry import node


@node(
    name="query_intake",
    version="1.0",
    description="登記原始問題與環境資訊；唯一可寫入不可變鍵的節點",
    reads=[
        "query",
        "session_context",
        "user_role",
        "system_entrypoint",
        "max_retrieval_attempts",
    ],
    writes=[
        "query_id",
        "original_query",
        "query_timestamp",
        "session_context",
        "user_role",
        "system_entrypoint",
        "retrieval_attempt",
        "max_retrieval_attempts",
        "answer_format_policy",
    ],
    deps=["max_retrieval_attempts"],
    requires_tools=[],
)
def make_query_intake_node(max_retrieval_attempts: int):
    """建立 query_intake 節點：只做登記，不改寫問題、不判斷意圖、不檢索。"""

    async def query_intake_node(state: dict) -> dict:
        query = (state.get("query") or "").strip()
        if not query:
            # 由 Harness（engine/harness.py::harnessed）轉為 fatal_error，走安全 ABSTAIN + 稽核路徑
            raise ValueError("query 不可為空白")
        return {
            "query_id": str(uuid.uuid4()),
            "original_query": query,
            "query_timestamp": datetime.now(timezone.utc).isoformat(),
            "session_context": state.get("session_context") or {},
            "user_role": state.get("user_role") or "USER",
            "system_entrypoint": state.get("system_entrypoint") or "api",
            "retrieval_attempt": 0,
            "max_retrieval_attempts": state.get("max_retrieval_attempts")
            or max_retrieval_attempts,
            "answer_format_policy": "structured_text_zh",
        }

    return query_intake_node
