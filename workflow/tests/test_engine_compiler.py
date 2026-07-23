"""Compiler 測試（AT2-15 ~ AT2-19、AT2-28、AT-GOV-01）。

用一組 throwaway 節點（測試結束即從 registry 移除）驗證控制流本身：sequence 順序、
branch then/else、loop 的兩種離開方式、編譯快取，以及「稽核節點強制附加」這條
治理硬規則 —— skill 的 YAML 完全沒寫 audit_feedback，trace 裡照樣要有它。
"""

from types import SimpleNamespace

import pytest

from app.engine import compiler, node_registry
from app.engine.skill import Skill
from app.nodes.kbquery.adapters import StaticGlossary

# import 觸發 kb_query 節點註冊（compiler 強制附加的 audit_feedback 來自這裡）
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from tests.kbquery_fakes import RecordingAuditRepo

TEST_NODES = ("t_seed", "t_a", "t_b", "t_tick")


@pytest.fixture(autouse=True, scope="module")
def _register_test_nodes():
    """註冊 throwaway 節點；模組結束後移除，不污染 GET /nodes 與其他測試。"""

    @node_registry.node(name="t_seed", writes=["x", "n"], description="種子")
    def make_t_seed():
        async def fn(state: dict) -> dict:
            return {"x": state.get("x", "seed"), "n": 0}

        return fn

    @node_registry.node(name="t_a", writes=["a"], description="A")
    def make_t_a():
        async def fn(state: dict) -> dict:
            return {"a": "A"}

        return fn

    @node_registry.node(name="t_b", writes=["b"], description="B")
    def make_t_b():
        async def fn(state: dict) -> dict:
            return {"b": "B"}

        return fn

    @node_registry.node(name="t_tick", reads=["n"], writes=["n"], description="n+1")
    def make_t_tick():
        async def fn(state: dict) -> dict:
            return {"n": state.get("n", 0) + 1}

        return fn

    yield
    for name in TEST_NODES:
        node_registry._REGISTRY.pop((name, "1.0"), None)


def _deps():
    """依賴一律注入：compiler 只認 NodeSpec.deps 宣告的欄位名，測試給一個剛好夠用的容器。

    audit_repo 是 compiler 強制附加的 audit_feedback 需要的；llm/glossary/
    max_retrieval_attempts 供 AT2-15 用到的 query_intake / query_rewrite。
    """
    return SimpleNamespace(
        audit_repo=RecordingAuditRepo(),
        llm=None,
        glossary=StaticGlossary(),
        max_retrieval_attempts=2,
    )


def _run(flow: list[dict], state: dict | None = None, deps=None) -> dict:
    import asyncio

    skill = Skill.model_validate({"name": "probe-skill", "flow": flow})
    graph = compiler.compile(skill, deps or _deps())
    return compiler.public_output(asyncio.run(graph.ainvoke(state or {})))


def _traced(result: dict) -> list[str]:
    return [t.node_name for t in result["trace"]]


# ---------------------------------------------------------------------------
# AT2-15 sequence
# ---------------------------------------------------------------------------


def test_sequence_runs_in_declared_order():
    """【AT2-15】兩個節點依宣告順序出現在 trace。"""
    result = _run([{"node": "query_intake"}, {"node": "query_rewrite"}], {"query": "hi"})

    trace = _traced(result)
    assert trace.index("query_intake") < trace.index("query_rewrite")


def test_nested_sequence_flattens_in_order():
    """sequence 是隱含容器：巢狀寫法與攤平寫法順序一致。"""
    result = _run(
        [{"node": "t_a"}, {"sequence": [{"node": "t_b"}, {"node": "t_tick"}]}]
    )

    assert _traced(result)[:3] == ["t_a", "t_b", "t_tick"]


# ---------------------------------------------------------------------------
# AT2-16 / AT2-17 branch
# ---------------------------------------------------------------------------


def test_branch_then_only():
    """【AT2-16】when 為真：只有 then 的節點執行，else 的節點不出現在 trace。"""
    result = _run(
        [
            {"node": "t_seed"},
            {
                "branch": {
                    "when": "state.x == 'go'",
                    "then": [{"node": "t_a"}],
                    "else": [{"node": "t_b"}],
                }
            },
        ],
        {"x": "go"},
    )

    trace = _traced(result)
    assert "t_a" in trace
    assert "t_b" not in trace
    assert result["a"] == "A"
    assert "b" not in result


def test_branch_else_only():
    """【AT2-17】when 為假且有 else：只有 else 的節點執行。"""
    result = _run(
        [
            {"node": "t_seed"},
            {
                "branch": {
                    "when": "state.x == 'go'",
                    "then": [{"node": "t_a"}],
                    "else": [{"node": "t_b"}],
                }
            },
        ],
        {"x": "stop"},
    )

    trace = _traced(result)
    assert "t_b" in trace
    assert "t_a" not in trace


def test_branch_without_else_skips_to_join():
    """when 為假且沒有 else：整段分支跳過，後續步驟照常執行。"""
    result = _run(
        [
            {"node": "t_seed"},
            {"branch": {"when": "state.x == 'go'", "then": [{"node": "t_a"}]}},
            {"node": "t_b"},
        ],
        {"x": "stop"},
    )

    trace = _traced(result)
    assert "t_a" not in trace
    assert "t_b" in trace


# ---------------------------------------------------------------------------
# AT2-18 / AT2-19 loop
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("max_iterations", [1, 2, 10])
def test_loop_stops_at_max_iterations_when_until_never_true(max_iterations):
    """【AT2-18】until 恆假 → body 恰跑 max_iterations 輪（達上限強制離開，不無限跑）。"""
    result = _run(
        [
            {"node": "t_seed"},
            {
                "loop": {
                    "max_iterations": max_iterations,
                    "until": "state.x == 'never'",
                    "body": [{"node": "t_tick"}],
                }
            },
        ]
    )

    assert result["n"] == max_iterations
    assert _traced(result).count("t_tick") == max_iterations


def test_loop_exits_early_when_until_true():
    """【AT2-19】until 在第 1 輪後即為真、上限 5 → body 只跑 1 輪。"""
    result = _run(
        [
            {"node": "t_seed"},
            {
                "loop": {
                    "max_iterations": 5,
                    "until": "state.n >= 1",
                    "body": [{"node": "t_tick"}],
                }
            },
        ]
    )

    assert result["n"] == 1
    assert _traced(result).count("t_tick") == 1


def test_loop_body_runs_at_least_once():
    """語意是「先跑 body 再驗 until」：until 一開始就為真，body 仍跑一輪。"""
    result = _run(
        [
            {"node": "t_seed"},
            {
                "loop": {
                    "max_iterations": 3,
                    "until": "state.n >= 0",
                    "body": [{"node": "t_tick"}],
                }
            },
        ]
    )

    assert result["n"] == 1


def test_loop_without_until_runs_to_max_iterations():
    """until 選填：不給就跑滿上限（上限本身即護欄）。"""
    result = _run(
        [
            {"node": "t_seed"},
            {"loop": {"max_iterations": 3, "body": [{"node": "t_tick"}]}},
        ]
    )

    assert result["n"] == 3


def test_loop_counter_key_stripped_from_public_output():
    """__loop_<id>_count 是引擎內部鍵：不出現在對外 output。"""
    import asyncio

    skill = Skill.model_validate(
        {
            "name": "probe-skill",
            "flow": [
                {"node": "t_seed"},
                {"loop": {"max_iterations": 2, "body": [{"node": "t_tick"}]}},
            ],
        }
    )
    raw = asyncio.run(compiler.compile(skill, _deps()).ainvoke({}))

    assert raw["__loop_0_count"] == 2  # 引擎內部確實有記
    assert "__loop_0_count" not in compiler.public_output(raw)


# ---------------------------------------------------------------------------
# AT-GOV-01 稽核節點強制附加於每條終止路徑
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("x", ["go", "stop"], ids=["then-path", "else-path"])
def test_audit_node_appended_to_every_terminal_path(x):
    """【AT-GOV-01】flow 完全沒寫 audit_feedback，兩條分支的終止路徑都要有稽核 entry。"""
    deps = _deps()
    result = _run(
        [
            {"node": "t_seed"},
            {
                "branch": {
                    "when": "state.x == 'go'",
                    "then": [{"node": "t_a"}],
                    "else": [{"node": "t_b"}],
                }
            },
        ],
        {"x": x},
        deps=deps,
    )

    assert _traced(result)[-1] == "audit_feedback"  # 稽核永遠是最後一筆
    assert len(deps.audit_repo.saved) == 1  # 稽核確實落地
    assert "audit_trail" in result


def test_audit_node_appended_after_loop_exit():
    """迴圈離開後的路徑一樣附加稽核。"""
    deps = _deps()
    result = _run(
        [
            {"node": "t_seed"},
            {"loop": {"max_iterations": 2, "body": [{"node": "t_tick"}]}},
        ],
        deps=deps,
    )

    assert _traced(result)[-1] == "audit_feedback"
    assert len(deps.audit_repo.saved) == 1


def test_audit_node_not_duplicated_when_skill_ends_with_it():
    """Skill 把 audit_feedback 寫在 flow 的**最後一步** → 不重複附加（同一份稽核不落兩次）。"""
    deps = _deps()
    result = _run([{"node": "t_seed"}, {"node": "audit_feedback"}], deps=deps)

    assert _traced(result).count("audit_feedback") == 1
    assert len(deps.audit_repo.saved) == 1


def test_audit_still_appended_when_skill_declares_it_mid_flow():
    """稽核寫在中途（後面還有節點）→ 末端照樣再附加一顆。

    否則作者把 audit_feedback 放在答案節點之前，稽核到的 state 就沒有最終答案 ——
    「每條終止路徑都有稽核」這條硬規則被一個排序就繞過去了。
    """
    deps = _deps()
    result = _run(
        [{"node": "t_seed"}, {"node": "audit_feedback"}, {"node": "t_a"}], deps=deps
    )

    assert _traced(result)[-1] == "audit_feedback"  # 終止路徑的最後一步仍是稽核
    assert _traced(result).count("audit_feedback") == 2  # 中途那顆只是普通節點
    assert len(deps.audit_repo.saved) == 2
    assert deps.audit_repo.saved[-1] is not None


def test_audit_appended_even_when_only_one_branch_declares_it():
    """稽核只寫在 then 分支 → 走 else 的那條終止路徑照樣要有稽核（不得為 0 筆）。"""
    for x in ("go", "stop"):
        deps = _deps()
        result = _run(
            [
                {"node": "t_seed"},
                {
                    "branch": {
                        "when": "state.x == 'go'",
                        "then": [{"node": "audit_feedback"}],
                        "else": [{"node": "t_b"}],
                    }
                },
            ],
            {"x": x},
            deps=deps,
        )

        assert _traced(result)[-1] == "audit_feedback"
        assert len(deps.audit_repo.saved) >= 1  # else 路徑不得漏掉稽核


# ---------------------------------------------------------------------------
# 條件式求值失敗 → 安全離開 + 稽核照樣落地（不得炸成 500）
# ---------------------------------------------------------------------------


def test_branch_condition_eval_error_takes_safe_path_and_still_audits():
    """when 求值失敗（None < 0.7）→ 走 else／join，不是把例外丟出圖外。

    規格 §3.1 範例的 `state.confidence < 0.7 and ...` 在 fatal 情境下就是這個形狀：
    靜態驗證是 valid 的，執行期卻踩到不存在的鍵。引擎必須兜底，否則稽核不落地。
    """
    deps = _deps()
    result = _run(
        [
            {"node": "t_seed"},
            {
                "branch": {
                    "when": "state.confidence < 0.7",  # confidence 不存在 → None < 0.7
                    "then": [{"node": "t_a"}],
                    "else": [{"node": "t_b"}],
                }
            },
        ],
        deps=deps,
    )

    assert "t_a" not in _traced(result)  # 走安全分支
    assert "t_b" in _traced(result)
    assert _traced(result)[-1] == "audit_feedback"
    assert len(deps.audit_repo.saved) == 1
    assert any("條件式求值失敗" in e["error"] for e in result["errors"])


def test_loop_condition_eval_error_exits_loop_and_still_audits():
    """until 求值失敗 → 安全離開迴圈（跑完當輪 body 即出場），稽核照樣落地。"""
    deps = _deps()
    result = _run(
        [
            {"node": "t_seed"},
            {
                "loop": {
                    "max_iterations": 5,
                    "until": "state.confidence < 0.7",  # 求值必炸
                    "body": [{"node": "t_tick"}],
                }
            },
        ],
        deps=deps,
    )

    assert result["n"] == 1  # 不是跑滿 5 輪，也不是炸圖
    assert _traced(result)[-1] == "audit_feedback"
    assert len(deps.audit_repo.saved) == 1
    assert any("條件式求值失敗" in e["error"] for e in result["errors"])


# ---------------------------------------------------------------------------
# recursion_limit 護欄（規格 §6.3-2）
# ---------------------------------------------------------------------------


def test_recursion_limit_is_bounded_by_flow_structure():
    """護欄由 flow 結構算出上界，而不是沿用 langgraph 的 10007（形同沒有護欄）。"""
    from app import skills

    kb = compiler.recursion_limit(skills.get("kb-query").skill)
    assert 40 < kb < 100  # 4 前置 + 10×(4 body + tick) + 組稿 + 稽核，遠低於預設 10007

    nested = compiler.recursion_limit(
        Skill.model_validate(
            {
                "name": "nested-probe",
                "flow": [
                    {
                        "loop": {
                            "max_iterations": 3,
                            "body": [
                                {
                                    "loop": {
                                        "max_iterations": 3,
                                        "body": [{"node": "t_a"}],
                                    }
                                }
                            ],
                        }
                    }
                ],
            }
        )
    )
    # 巢狀 loop 的 superstep 是相乘關係：上界必須跟著長，否則合法的圖會被誤殺
    assert nested >= 3 * (3 * 2)


# ---------------------------------------------------------------------------
# AT2-28 編譯快取
# ---------------------------------------------------------------------------


def test_compile_caches_by_revision(monkeypatch):
    """【AT2-28】同 revision + 同內容第二次呼叫命中快取，底層建圖只跑一次；改版即重編。"""
    calls: list[str] = []
    real_build = compiler._build_graph

    def counting_build(skill, deps):
        calls.append(skill.name)
        return real_build(skill, deps)

    monkeypatch.setattr(compiler, "_build_graph", counting_build)

    deps = _deps()
    definition = {"name": "cache-probe", "revision": 1, "flow": [{"node": "t_a"}]}
    skill = Skill.model_validate(definition)

    first = compiler.compile(skill, deps)
    second = compiler.compile(Skill.model_validate(definition), deps)

    assert len(calls) == 1  # 第二次沒有重新建圖
    assert first is second  # 拿到的是同一個 CompiledStateGraph

    # revision bump → 重新編譯
    bumped = Skill.model_validate({**definition, "revision": 2})
    third = compiler.compile(bumped, deps)
    assert len(calls) == 2
    assert third is not first

    # revision 沒動但內容改了 → 一樣重新編譯（內容 hash 進快取鍵）
    edited = Skill.model_validate({**definition, "flow": [{"node": "t_b"}]})
    compiler.compile(edited, deps)
    assert len(calls) == 3
