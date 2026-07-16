"""nl_extract 節點:把「文件裡的具名數值」抽成 TYPED 的 dict 寫進 state["extracted"]。

為什麼需要這顆節點:統計／比較類 skill 若讓 LLM「一手讀文件、一手算術」(nl_logic 的
用法),算術會出錯(revenue_qa 實測 32.6% vs 正解 32.8%)。原始設計是把兩件事切開 ——
LLM 只負責「抽數字」,算術交給 deterministic 的 script 節點做。但 script 沙箱讀不了
字串(沒有 .split()/re/json,屬性存取只剩 tools.call),所以 nl_logic 那種「只寫一段
字串到 business_result」的輸出,script 根本解析不出數字。

本節點用 pydantic.create_model 依 fields 動態建一個「每欄都是 float」的結構化輸出模型,
逼 LLM 只回傳這幾個純數值,寫成 {field: float} 的 dict 進 state["extracted"] ——
script 就能用字面鍵 state["extracted"]["revenue_2024"] 直接讀出 typed 數字來算。

抽取失敗一律大聲失敗(raise ValueError → Harness 轉 fatal_error):silent 的錯數字正是
我們要修掉的 bug,寧可讓步驟 fatal 也不要吐一個看似正常的錯答案。
"""

from pydantic import create_model

from app.engine.node_registry import node

# system prompt 的固定前綴(規格逐字):只抽純數值,不做任何推估或計算。
_EXTRACT_INSTRUCTION = (
    "只輸出這些欄位的純數值,去除單位(萬/元/%)與千分位逗號,"
    "直接引用文件中的數字,不得推估或計算。"
)


@node(
    name="nl_extract",
    version="1.0",
    description="以 LLM 從 state 指定鍵抽出具名數值,寫成 {field: float} 的 dict 進 extracted",
    reads=[],  # 固定讀取鍵:無(要餵什麼由 params.input_keys 決定,比照 nl_logic)
    dynamic_reads=["input_keys"],  # params 指定要餵 LLM 的 state 鍵(資料流檢查用)
    writes=["extracted"],
    deps=["llm"],
    requires_tools=[],
)
def make_nl_extract_node(llm, *, fields=(), input_keys=(), instruction: str = ""):
    """建立 nl_extract 節點函式。

    - fields:要抽取的欄位名清單(如 ["revenue_2024", "revenue_2025"]);動態 pydantic
      模型每欄鎖死 float,LLM 結構化輸出被逼只回這幾個數值欄位。
    - input_keys:要餵給 LLM 的 state 鍵,依序組成 user 訊息(`k: repr(value)`,比照
      nl_logic);空時退回 normalized_query → query → 空字串。
    - instruction:選填的額外指引,附在固定抽取指令之後。
    """

    field_names = tuple(fields)
    # 動態結構化輸出模型:每個欄位都是必填 float,LLM 只能回這幾個純數值。
    output_model = create_model(
        "NlExtractOutput",
        **{name: (float, ...) for name in field_names},
    )

    system = _EXTRACT_INSTRUCTION
    if field_names:
        system = system + "\n需要抽取的欄位:" + "、".join(field_names)
    if instruction:
        system = system + "\n" + instruction

    async def nl_extract(state: dict) -> dict:
        keys = tuple(input_keys)
        if keys:
            user = "\n".join(f"{k}: {state.get(k)!r}" for k in keys)
        else:
            user = str(state.get("normalized_query") or state.get("query") or "")

        out = await llm.structured(system=system, user=user, schema=output_model)
        if out is None:
            # 大聲失敗:silent 的錯數字正是本節點要修掉的 bug(見 module docstring)。
            raise ValueError(
                f"nl_extract 抽取失敗:LLM 未回傳結構化結果(fields={list(field_names)})"
            )

        return {"extracted": {name: float(getattr(out, name)) for name in field_names}}

    return nl_extract
