"""引擎鍵不可經 invoke input 夾帶（_clean_skill_input 除保留鍵/__ 前綴，另剝 ENGINE_KEYS）。

威脅：呼叫端在 input 夾帶 fatal_error 會讓 Harness 對所有節點走 fatal 短路（整張圖
skip、拿不到實質答案）；夾帶 trace/errors 會讓 LangGraph reducer 收到型別不符的初值
→ TypeError → 500。既有 test_skills_custom.py / test_skills_api.py 的保留鍵測試只涵蓋
RESERVED_KEYS，這裡補上 ENGINE_KEYS 這一路的護欄（縫：main._clean_skill_input）。
"""

from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler, node_registry
from app.engine.skill import Skill, clean_invoke_input
from app.main import _clean_skill_input, app
from tests.conftest import auth_headers, swap_skill

client = TestClient(app)

HEADERS = auth_headers()


# ---------------------------------------------------------------------------
# 純函式層：三個引擎鍵一律剝除，合法鍵原樣保留（決策表兩半）
# ---------------------------------------------------------------------------


def test_clean_skill_input_strips_all_engine_keys_keeps_legit():
    """直接吃生產常數：日後擴充 ENGINE_KEYS，新鍵自動被這條涵蓋。"""
    forged = {key: "forged" for key in node_registry.ENGINE_KEYS}

    assert _clean_skill_input({"query": "x", **forged}) == {"query": "x"}


def test_clean_skill_input_strips_engine_internal_prefix():
    """__ 前綴的引擎內部鍵一樣不可經 input 夾帶（comprehension 的第三個條件）。

    夾帶 `__loop_0_count: 9` 會讓 compiler.route()（compiler.py:461）第一輪就
    `>= max_iterations` → 迴圈只跑一輪，等於呼叫端從外部關掉了迴圈治理。
    """
    cleaned = _clean_skill_input(
        {"query": "x", "__loop_0_count": 9, "__branch_0_when": True}
    )

    assert cleaned == {"query": "x"}


def test_shared_input_cleaner_handles_non_string_keys():
    assert clean_invoke_input({1: "kept", "__private": "removed"}) == {1: "kept"}


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

    with swap_skill(
        "__engine-probe__",
        base=original,
        skill=Skill.model_validate({"name": "engine-probe", "flow": [{"node": "t"}]}),
        graph=_CaptureGraph(),
        input_model=None,
        deps=None,
    ):
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


def test_invoke_input_engine_keys_do_not_break_real_kb_query_graph():
    """行為證據：若沒剝除，trace 初值會讓 reducer TypeError→500、fatal_error 會全 skip。

    這裡以真圖 + 假依賴跑一次（不打網路、不用 LLM），同時夾帶三個引擎鍵：
    仍拿到 ANSWER + 正解，等於證明兩種攻擊都被關掉了。
    """
    from tests.kbquery_fakes import TEXT_2025Q3, FakeSearch, make_deps

    original = skills.get("kb-query")
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])})
    with swap_skill(
        "__kb_engine-probe__",
        base=original,
        skill=original.skill,
        graph=compiler.compile(original.skill, deps),
        input_model=original.input_model,
        deps=deps,
    ):
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
