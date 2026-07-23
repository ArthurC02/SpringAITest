"""引擎鍵不可經 invoke input 夾帶（_clean_skill_input 除保留鍵/__ 前綴，另剝 ENGINE_KEYS）。

威脅：呼叫端在 input 夾帶 fatal_error 會讓 Harness 對所有節點走 fatal 短路（整張圖
skip、拿不到實質答案）；夾帶 trace/errors 會讓 LangGraph reducer 收到型別不符的初值
→ TypeError → 500。既有 test_skills_custom.py / test_skills_api.py 的保留鍵測試只涵蓋
RESERVED_KEYS，這裡補上 ENGINE_KEYS 這一路的護欄（縫：main._clean_skill_input）。
"""

import pytest
from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler, node_registry
from app.engine.skill import Skill
from app.main import _clean_skill_input, app
from tests.conftest import auth_headers

client = TestClient(app)

HEADERS = auth_headers()


# ---------------------------------------------------------------------------
# 純函式層：三個引擎鍵一律剝除，合法鍵原樣保留（決策表兩半）
# ---------------------------------------------------------------------------


def test_clean_skill_input_strips_all_engine_keys_keeps_legit():
    cleaned = _clean_skill_input(
        {"query": "x", "fatal_error": "forged", "trace": "t", "errors": [{"e": 1}]}
    )
    assert cleaned == {"query": "x"}


@pytest.mark.parametrize("engine_key", sorted(node_registry.ENGINE_KEYS))
def test_clean_skill_input_strips_each_engine_key(engine_key):
    assert _clean_skill_input({"query": "x", engine_key: "forged"}) == {"query": "x"}


def test_engine_keys_are_exactly_trace_errors_fatal_error():
    # 若日後有人擴充 ENGINE_KEYS，這條會提醒回來補剝除路徑的護欄
    assert node_registry.ENGINE_KEYS == {"trace", "errors", "fatal_error"}


# ---------------------------------------------------------------------------
# API 端：夾帶引擎鍵的 input 被剝除，且 skill 照常執行（不 500、不全 skip）
# ---------------------------------------------------------------------------


def test_invoke_input_engine_keys_are_stripped_from_state():
    """capture graph 釘住：進 state 的 dict 不含任何引擎鍵，query 照常在。"""
    captured: dict = {}
    original = skills.get("kb-query")

    class _CaptureGraph:
        async def ainvoke(self, state, config=None):
            captured.update(state)
            return {**state, "ran": True}

    skills._SKILLS["__engine-probe__"] = original.__class__(
        skill=Skill.model_validate({"name": "engine-probe", "flow": [{"node": "t"}]}),
        graph=_CaptureGraph(),
        input_model=None,
        deps=None,
    )
    try:
        resp = client.post(
            "/skills/__engine-probe__/invoke",
            json={
                "input": {
                    "query": "x",
                    "fatal_error": "forged",
                    "trace": "forged",
                    "errors": [{"node": "n", "error": "forged"}],
                }
            },
            headers=HEADERS,
        )

        assert resp.status_code == 200
        assert captured["query"] == "x"
        assert "fatal_error" not in captured
        assert "trace" not in captured
        assert "errors" not in captured
    finally:
        skills._SKILLS.pop("__engine-probe__", None)


def test_invoke_input_engine_keys_do_not_break_real_kb_query_graph():
    """行為證據：若沒剝除，trace 初值會讓 reducer TypeError→500、fatal_error 會全 skip。

    這裡以真圖 + 假依賴跑一次（不打網路、不用 LLM），同時夾帶三個引擎鍵：
    仍拿到 ANSWER + 正解，等於證明兩種攻擊都被關掉了。
    """
    from tests.kbquery_fakes import TEXT_2025Q3, FakeSearch, make_deps

    original = skills.get("kb-query")
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    skills._SKILLS["__kb_engine-probe__"] = original.__class__(
        skill=original.skill,
        graph=compiler.compile(original.skill, deps),
        input_model=original.input_model,
        deps=deps,
    )
    try:
        resp = client.post(
            "/skills/__kb_engine-probe__/invoke",
            json={
                "input": {
                    "query": "2025Q3 稅後淨利是多少？",
                    "fatal_error": "forged",
                    "trace": "forged",
                    "errors": "forged",
                }
            },
            headers=HEADERS,
        )

        assert resp.status_code == 200  # trace 初值沒把 reducer 打成 TypeError
        body = resp.json()
        assert body["output"]["answer_mode"] == "ANSWER"  # fatal_error 沒把全圖 skip 掉
        assert "1,234" in body["output"]["final_answer"]
    finally:
        skills._SKILLS.pop("__kb_engine-probe__", None)
