"""nl_extract 節點測試:抽 typed 數值進 state["extracted"],交給 script 做算術。

為什麼要這顆節點:nl_logic 讓 LLM「讀文件又算術」會算錯(revenue_qa 實測 32.6% vs
正解 32.8%)。切開後 LLM 只抽數字、script 做確定性算術。本檔驗:
- 單元:instruction→system(含固定抽取指令)、input_keys→user 依序 k: repr、抽出的
  欄位都是 float、LLM 回 None 時大聲 raise(不吐 silent 錯數字)。
- 註冊:GET /nodes 含 nl_extract@1.0。
- 整合:retrieve→nl_extract→script 編譯 + 執行,給 5200/6905 → business_result 含 "32.8%"。
- 決策表收尾:最終 revenue_qa YAML 過 /skills/validate(valid=True),印證 script 過白名單。

無 pytest-asyncio,async 節點統一 asyncio.run(對齊 test_nl_logic.py 慣例)。
"""

import asyncio

import pytest
from fastapi.testclient import TestClient

from app.engine import compiler
from app.engine import skill as skill_mod
from app.main import app
from app.nodes.nl_extract import make_nl_extract_node
from tests.conftest import auth_headers, patch_retrieve
from tests.kbquery_fakes import make_deps

client = TestClient(app)


class RecordingExtractLLM:
    """手寫 fake:記錄 system/user,依 values(dict/None)以 schema(**values) 回傳或 None。

    nl_extract 的 schema 是 factory 內 create_model 動態建的,測試無法引用該類別,
    故直接 schema(**values) 建構 —— 動態欄位名正好對得上 values 的鍵。
    """

    version = "rec-extract-v1"

    def __init__(self, values=None):
        self.values = values
        self.calls: list[dict] = []

    async def structured(self, system, user, schema):
        self.calls.append({"system": system, "user": user, "schema": schema})
        if self.values is None:
            return None
        return schema(**self.values)


# ---------------------------------------------------------------------------
# 單元:instruction→system(含固定抽取指令)、input_keys→user、抽出欄位皆 float
# ---------------------------------------------------------------------------


def test_nl_extract_writes_typed_floats_and_assembles_prompt():
    llm = RecordingExtractLLM(values={"revenue_2024": 5200, "revenue_2025": 6905})
    node = make_nl_extract_node(
        llm, fields=["revenue_2024", "revenue_2025"], input_keys=["docs"]
    )

    out = asyncio.run(node({"docs": [{"content": "2024:5200 2025:6905"}], "x": "z"}))

    assert set(out) == {"extracted"}
    assert out["extracted"] == {"revenue_2024": 5200.0, "revenue_2025": 6905.0}
    # 值即使 LLM 回 int,也一律轉成 float(script 端 float() 再算)
    assert all(isinstance(v, float) for v in out["extracted"].values())

    call = llm.calls[0]
    # 固定抽取指令逐字進 system
    assert "只輸出這些欄位的純數值" in call["system"]
    assert "不得推估或計算" in call["system"]
    # input_keys → user 依序 k: repr(value);未列的鍵不進 user
    assert call["user"] == f"docs: {[{'content': '2024:5200 2025:6905'}]!r}"
    assert "x: " not in call["user"]


def test_nl_extract_appends_optional_instruction_to_system():
    llm = RecordingExtractLLM(values={"a": 1})
    node = make_nl_extract_node(llm, fields=["a"], instruction="額外指引XYZ")
    asyncio.run(node({"query": "q"}))
    assert "額外指引XYZ" in llm.calls[0]["system"]


def test_nl_extract_empty_input_keys_fallback_to_query():
    llm = RecordingExtractLLM(values={"a": 1})
    node = make_nl_extract_node(llm, fields=["a"])  # input_keys 預設空
    asyncio.run(node({"normalized_query": "NQ", "query": "Q"}))
    assert llm.calls[0]["user"] == "NQ"  # normalized_query 優先


# ---------------------------------------------------------------------------
# 決策表收尾:LLM 回 None → 大聲 raise(不吐 silent 錯數字,這正是要修的 bug)
# ---------------------------------------------------------------------------


def test_nl_extract_raises_loudly_when_llm_returns_none():
    node = make_nl_extract_node(
        RecordingExtractLLM(values=None), fields=["revenue_2024"]
    )
    with pytest.raises(ValueError, match="抽取失敗"):
        asyncio.run(node({"query": "q"}))


# ---------------------------------------------------------------------------
# 註冊:GET /nodes 含 nl_extract@1.0,契約如宣告
# ---------------------------------------------------------------------------


def test_nodes_catalog_lists_nl_extract():
    resp = client.get("/nodes", headers=auth_headers())
    assert resp.status_code == 200
    by_name = {(n["name"], n["version"]): n for n in resp.json()}
    assert ("nl_extract", "1.0") in by_name
    spec = by_name[("nl_extract", "1.0")]
    assert spec["writes"] == ["extracted"]
    assert spec["reads"] == ["normalized_query", "query"]


# ---------------------------------------------------------------------------
# 整合:retrieve→nl_extract→script 編譯 + 執行,5200/6905 → business_result 含 32.8%
# ---------------------------------------------------------------------------

_FLOW_DEFINITION = """
name: nl-extract-flow-probe
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - node: retrieve@1.0
    params: {query_key: query, top_k: 50}
  - node: nl_extract@1.0
    params: {input_keys: [docs], fields: [revenue_2024, revenue_2025]}
  - script: |
      r24 = float(state["extracted"]["revenue_2024"])
      r25 = float(state["extracted"]["revenue_2025"])
      yoy = round((r25 - r24) / r24 * 100, 1)
      state["business_result"] = "2024 營收 " + str(r24) + "、2025 營收 " + str(r25) + " → YoY " + str(yoy) + "%"
"""


def test_retrieve_nl_extract_script_compiles_and_computes(monkeypatch):
    patch_retrieve(
        monkeypatch,
        [
            {
                "document_id": "d1",
                "title": "年報",
                "content": "2024 營收 5,200 萬、2025 營收 6,905 萬",
                "score": 0.9,
            }
        ],
    )

    llm = RecordingExtractLLM(values={"revenue_2024": 5200, "revenue_2025": 6905})
    deps = make_deps({}, llm=llm)
    skill = skill_mod.parse_source(_FLOW_DEFINITION)
    graph = compiler.compile(skill, deps)
    out = compiler.public_output(
        asyncio.run(graph.ainvoke({"query": "營收年增率", "tenant_id": "t"}))
    )

    # 確定性算術:(6905-5200)/5200*100 = 32.788… → round(,1) = 32.8
    assert "32.8%" in out["business_result"]
    assert "5200.0" in out["business_result"]


# ---------------------------------------------------------------------------
# 決策表另一半:最終 revenue_qa YAML 過 /skills/validate(印證 script 過白名單分析器)
# ---------------------------------------------------------------------------

REVENUE_QA_YAML = """name: revenue-qa
description: 從年報文件抽取 2024 與 2025 營收數字,計算 YoY 年增率,回答營收數字比較類問題。
required_role: USER
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - node: query_intake@1.0
  - node: retrieve@1.0
    params: {query_key: query, top_k: 50}
  - node: nl_extract@1.0
    params: {input_keys: [docs], fields: [revenue_2024, revenue_2025]}
  - script: |
      r24 = float(state["extracted"]["revenue_2024"])
      r25 = float(state["extracted"]["revenue_2025"])
      yoy = round((r25 - r24) / r24 * 100, 1)
      state["business_result"] = "2024 營收 " + str(r24) + "、2025 營收 " + str(r25) + " → YoY " + str(yoy) + "%"
"""


def test_final_revenue_qa_yaml_validates():
    # REVENUE_QA_YAML 含 script 步驟 → 撰寫者角色 gate 要求 ADMIN 身分頭驗證
    # （required_role 仍是 USER：ADMIN 作者撰寫、USER 呼叫，是新語意下的合法組合）。
    resp = client.post(
        "/skills/validate",
        json={"definition": REVENUE_QA_YAML},
        headers=auth_headers(role="ADMIN"),
    )
    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is True, body
