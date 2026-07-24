"""P3 nl_logic 節點:把「自然語言商業邏輯」當成執行期 LLM 呼叫。

為什麼需要這顆節點:簡單模式的使用者用中文寫「我的規則」,不寫 Python、不編排流程。
規則沒辦法在存檔期就求值(它要看當下的 state:檢索結果、正規化後的問題…),所以把
規則當成 system prompt,在執行期餵給 LLM 解讀,結果寫回 business_result 供後續步驟
或最終輸出使用。這是 template_{retrieval,infer,inspire} 三支骨架的商業邏輯槽。
"""

from pydantic import BaseModel

from app.engine.node_registry import node
from app.nodes._llm_input import build_user_message, structured_field


class _NlLogicOutput(BaseModel):
    """LLM 的結構化輸出:只取一段文字結果寫回 business_result。"""

    result: str


@node(
    name="nl_logic",
    version="1.0",
    description="以自然語言 instruction 當商業邏輯,執行期呼叫 LLM 解讀並寫回 business_result",
    reads=["normalized_query", "query"],  # input_keys 為空時 _llm_input 回落讀這兩鍵
    dynamic_reads=["input_keys"],  # 比照 retrieve 的 query_key:params 指定要餵 LLM 的 state 鍵
    writes=["business_result"],
    deps=["llm"],
    requires_tools=[],
)
def make_nl_logic_node(llm, *, instruction: str, input_keys=()):
    """建立 nl_logic 節點函式。

    - instruction:使用者的自然語言規則,原樣當成 LLM 的 system 提示。
    - input_keys:要餵給 LLM 的 state 鍵,依序組成 user 訊息;組裝規則見
      app.nodes._llm_input.build_user_message。
    """

    async def nl_logic(state: dict) -> dict:
        user = build_user_message(state, tuple(input_keys))
        result = await structured_field(
            llm, system=instruction, user=user, schema=_NlLogicOutput, field="result"
        )
        return {"business_result": result}

    return nl_logic
