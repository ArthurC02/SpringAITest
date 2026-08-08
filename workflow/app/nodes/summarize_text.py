"""summarize_text 節點：把輸入文字濃縮成三句以內的繁體中文摘要。

app/skills/summarize.yaml 唯一的步驟；節點名刻意與 skill 名（summarize）分開，
GET /nodes 的節點目錄與 GET /skills 的 skill 目錄因此不會互相混淆。
"""

from pydantic import BaseModel

from app.engine.node_registry import node
from app.nodes._llm_input import structured_field


class _SummarizeOutput(BaseModel):
    """LLM 的結構化輸出：只取一段摘要文字。"""

    summary: str


@node(
    name="summarize_text",
    version="1.0",
    description="將輸入文字濃縮成三句以內的繁體中文摘要",
    reads=["text"],
    writes=["summary"],
    deps=["llm"],
    requires_tools=[],
)
def make_summarize_text_node(llm):
    """建立 summarize_text 節點函式；LLM 由 deps 注入，節點不碰全域 settings。"""

    async def summarize_text(state: dict) -> dict:
        summary = await structured_field(
            llm,
            system="你是摘要助手，將輸入濃縮成三句以內的繁體中文摘要。",
            user=state["text"],
            schema=_SummarizeOutput,
            field="summary",
        )
        return {"summary": summary}

    return summarize_text
