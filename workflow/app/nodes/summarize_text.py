"""summarize_text 節點：逐字移植 app/workflows/summarize.py::summarize()。

節點名 summarize_text（而非 summarize）避免與既有 @register("summarize", ...)
手寫工作流撞名——兩者是不同的登記表（node_registry vs workflows/registry），
不會真的衝突，但沿用不同名字讓 GET /nodes 與 GET /workflows 的目錄各自清楚。
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
    """建立 summarize_text 節點函式（對齊 workflows/summarize.py::summarize 的語意）。"""

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
