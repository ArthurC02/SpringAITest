"""Compiler 測試（AT2-15 ~ AT2-19、AT2-28、AT-GOV-01）。

用一組 throwaway 節點（測試結束即從 registry 移除）驗證控制流本身：sequence 順序、
branch then/else、loop 的兩種離開方式、編譯快取，以及「稽核節點強制附加」這條
治理硬規則 —— skill 的 YAML 完全沒寫 audit_feedback，trace 裡照樣要有它。
"""

from types import SimpleNamespace

import pytest

from app.engine import compiler, node_registry, skill as skill_mod
from app.engine.skill import Skill
from app.nodes.kbquery.adapters import StaticGlossary

# import 觸發 kb_query 節點註冊（compiler 強制附加的 audit_feedback 來自這裡）
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from tests.kbquery_fakes import RecordingAuditRepo

TEST_NODES = (
    "t_seed",
    "t_a",
    "t_b",
    "t_tick",
    "t_reader",
    "t_open",
    "t_fatal_reader",
    "t_boom",
)

# langgraph 的預設值：等於「沒有護欄」（規格 §6.3-2 就是為了不落回這個值）
LANGGRAPH_DEFAULT_RECURSION_LIMIT = 10007


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

    # 以下四個節點供 effective_reads（compiler._node_reads）的組裝測試使用：
    # 節點把自己看得到的 state 鍵名記進 writes，測試據此斷言過濾結果。
    @node_registry.node(
        name="t_reader",
        reads=["a"],
        dynamic_reads=["src"],
        writes=["seen"],
        description="回報自己看得到的鍵",
    )
    def make_t_reader(src=None):
        async def fn(state: dict) -> dict:
            return {"seen": sorted(state)}

        return fn

    @node_registry.node(name="t_open", writes=["seen"], description="未宣告 reads")
    def make_t_open():
        async def fn(state: dict) -> dict:
            return {"seen": sorted(state)}

        return fn

    @node_registry.node(
        name="t_fatal_reader",
        reads=["a"],
        writes=["seen_on_fatal"],
        run_on_fatal=True,
        description="fatal 後仍執行並回報視野",
    )
    def make_t_fatal_reader():
        async def fn(state: dict) -> dict:
            return {"seen_on_fatal": sorted(state)}

        return fn

    @node_registry.node(name="t_boom", writes=["never"], description="必定失敗")
    def make_t_boom():
        async def fn(state: dict) -> dict:
            raise RuntimeError("boom")

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


def _run(
    flow: list[dict],
    state: dict | None = None,
    deps=None,
    input_schema: dict | None = None,
) -> dict:
    import asyncio

    skill = Skill.model_validate(
        {"name": "probe-skill", "input_schema": input_schema or {}, "flow": flow}
    )
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


def test_sibling_loops_and_branch_inside_loop_keep_index_alignment():
    """_scan 與 _Builder 的走訪順序必須完全一致（compiler.py:87 的明文不變量）。

    只有攤平結構被測過時，這條不變量是「碰巧成立」的。這裡用會拆穿它的形狀：
    兩個同層 loop（上限不同 → 索引錯位就是迭代次數對調）+ 第一個 loop 內含 branch。
    實測（scratchpad 注入迴歸）：_scan 若不遞迴進 loop body，body 內的節點與 branch
    都不會被登記 → 它們的鍵沒有 state 頻道 → 圖照跑，但 `a` 與 `__branch_0_when`
    在最終 state 直接消失（節點寫的值靜默不見）。故斷言同時涵蓋領域鍵與內部鍵。
    """
    import asyncio

    skill = Skill.model_validate(
        {
            "name": "probe-skill",
            "flow": [
                {"node": "t_seed"},
                {
                    "loop": {
                        "max_iterations": 2,
                        "body": [
                            {
                                "branch": {
                                    "when": "state.x == 'go'",
                                    "then": [{"node": "t_a"}],
                                    "else": [{"node": "t_b"}],
                                }
                            },
                            {"node": "t_tick"},
                        ],
                    }
                },
                {"loop": {"max_iterations": 3, "body": [{"node": "t_tick"}]}},
            ],
        }
    )
    raw = asyncio.run(compiler.compile(skill, _deps()).ainvoke({"x": "go"}))

    assert raw["__loop_0_count"] == 2  # 第一個 loop 的上限是 2
    assert raw["__loop_1_count"] == 3  # 第二個是 3；索引對調就會在這裡出局
    assert raw["__branch_0_when"] is True  # loop 內的 branch 有自己的 state 頻道
    assert raw["a"] == "A"  # loop 內 branch 的 then 節點，寫入確實進得了 state
    trace = _traced(compiler.public_output(raw))
    assert trace.count("t_a") == 2 and "t_b" not in trace  # 每輪都走 then
    assert raw["n"] == 5  # 2 + 3 輪 tick


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


@pytest.mark.parametrize(
    "x,expected_audits",
    [("go", 2), ("stop", 1)],
    ids=["then-path-declares-audit", "else-path-declares-none"],
)
def test_audit_appended_even_when_only_one_branch_declares_it(x, expected_audits):
    """稽核只寫在 then 分支 → 兩條終止路徑都要有稽核，且筆數是確切值。

    then 走 2 筆（分支內那顆 + 末端強制附加的那顆），else 走 1 筆。斷言確切值而不是
    `>= 1`：「末端附加的稽核取代了分支內那顆」這種迴歸只會讓 then 從 2 掉到 1，
    在 `>= 1` 底下完全看不見。
    """
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

    assert _traced(result)[-1] == "audit_feedback"  # 終止路徑最後一步永遠是稽核
    assert _traced(result).count("audit_feedback") == expected_audits
    assert len(deps.audit_repo.saved) == expected_audits


# ---------------------------------------------------------------------------
# effective_reads 的組裝（compiler._node_reads + add_node_step 的 `or None`）
# Harness 只驗「給了 reads 就過濾」；這裡驗 compiler 算出來的 reads 對不對。
# ---------------------------------------------------------------------------

READS_SCHEMA = {"a": {"type": "int"}, "b": {"type": "int"}, "c": {"type": "int"}}
READS_STATE = {"a": 1, "b": 2, "c": 3}


@pytest.mark.parametrize(
    "src,expected",
    [
        ("b", {"a", "b"}),  # dynamic_reads 的值是 str → 單鍵
        (["b", "c"], {"a", "b", "c"}),  # list → 逐項取字串元素
        ([1, "c"], {"a", "c"}),  # list 內的非字串元素忽略
        (7, {"a"}),  # 非 str/list 型別整個忽略（只剩靜態 reads）
        (None, {"a"}),  # 沒給 params → 同上
    ],
    ids=["str", "list", "list-mixed", "int", "absent"],
)
def test_effective_reads_built_from_spec_and_params(src, expected):
    """節點的視野 = spec.reads ∪ dynamic_reads 經該步驟 params 解析出來的鍵。

    漏解析 → 節點讀不到自己要的鍵（retrieve 這種 query_key 節點會整個餓死）；
    多放行 → reads 契約形同虛設（宣告 a 卻看得到 c）。
    """
    params = {} if src is None else {"params": {"src": src}}
    result = _run(
        [{"node": "t_reader", **params}],
        READS_STATE,
        input_schema=READS_SCHEMA,
    )

    assert set(result["seen"]) == expected


def test_node_without_reads_declaration_sees_full_state():
    """reads=() 且無 dynamic_reads → `or None`（不過濾）。

    空 reads 契約＝「未宣告」，不是「宣告讀零鍵」—— 誤當後者會餓死節點整個視圖。
    """
    result = _run([{"node": "t_open"}], READS_STATE, input_schema=READS_SCHEMA)

    assert {"a", "b", "c"} <= set(result["seen"])


def test_run_on_fatal_node_reads_include_engine_keys():
    """run_on_fatal 節點的視野必須 ∪ ENGINE_KEYS：看不到 fatal_error/errors/trace 的
    稽核節點等於稽核不到失敗那一次。"""
    result = _run(
        [{"node": "t_boom"}, {"node": "t_fatal_reader"}],
        READS_STATE,
        input_schema=READS_SCHEMA,
    )

    assert set(result["seen_on_fatal"]) == {"a"} | node_registry.ENGINE_KEYS


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


def _min_supersteps(flow: list) -> int:
    """flow 最壞情況下**必須**執行的步驟數（獨立於 compiler.recursion_limit 的簡化重算）。

    節點/script/tool 各 1；branch 取兩條路徑的較大者；loop 是 max_iterations × body。
    刻意不含引擎植入的控制流節點（decide/join/init/tick/exit）與強制附加的稽核 ——
    少算只會讓它成為更保守的**下界**：護欄低於這個值就代表合法的圖會被誤殺。
    """
    total = 0
    for step in flow or []:
        kind, body = skill_mod.parse_step(step)
        if kind in ("node", "script", "tool"):
            total += 1
        elif kind == "sequence":
            total += _min_supersteps(body)
        elif kind == "branch":
            total += max(
                _min_supersteps(body["then"]), _min_supersteps(body.get("else") or [])
            )
        elif kind == "loop":
            total += body["max_iterations"] * _min_supersteps(body["body"])
    return total


def test_recursion_limit_is_bounded_by_flow_structure():
    """護欄由 flow 結構算出上界：足以跑完自己的 flow，且遠低於 langgraph 的預設。

    斷言不寫死魔術區間（改 kb-query.yaml 的形狀就假紅），而是兩側都由結構決定：
    下界＝這張圖至少要跑幾步、上界＝langgraph 預設值（落回預設等於沒有護欄）。
    """
    from app import skills

    kb = skills.get("kb-query").skill
    assert (
        _min_supersteps(kb.flow)
        <= compiler.recursion_limit(kb)
        < LANGGRAPH_DEFAULT_RECURSION_LIMIT
    )

    nested = Skill.model_validate(
        {
            "name": "nested-probe",
            "flow": [
                {
                    "loop": {
                        "max_iterations": 5,
                        "body": [
                            {
                                "loop": {
                                    "max_iterations": 4,
                                    "body": [{"node": "t_a"}],
                                }
                            }
                        ],
                    }
                }
            ],
        }
    )
    # 巢狀 loop 的 superstep 是相乘關係（5×4=20 步）：上界必須跟著相乘長，
    # 否則合法的圖會被誤殺 —— 漏掉乘法的實作算出來的值會低於這個下界。
    assert _min_supersteps(nested.flow) == 20
    assert (
        _min_supersteps(nested.flow)
        <= compiler.recursion_limit(nested)
        < LANGGRAPH_DEFAULT_RECURSION_LIMIT
    )


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


def _counting_build(monkeypatch) -> list[str]:
    """把 _build_graph 換成會計數的版本；回傳的 list 即「實際建圖次數」。"""
    calls: list[str] = []
    real_build = compiler._build_graph

    def counting(skill, deps):
        calls.append(skill.name)
        return real_build(skill, deps)

    monkeypatch.setattr(compiler, "_build_graph", counting)
    return calls


def test_compile_does_not_share_graph_across_different_deps(monkeypatch):
    """快取鍵含 id(deps)：同一個 skill 配不同依賴容器必須各編一張圖。

    共用的話，測試的假依賴（或別的租戶設定容器）建出來的圖會被正式呼叫命中 ——
    compiler.py:623-625 的持有參考 + FIFO 就是為了杜絕這件事。
    """
    calls = _counting_build(monkeypatch)
    skill = Skill.model_validate(
        {"name": "deps-probe", "revision": 1, "flow": [{"node": "t_a"}]}
    )
    first_deps, second_deps = _deps(), _deps()

    first = compiler.compile(skill, first_deps)
    second = compiler.compile(skill, second_deps)

    assert len(calls) == 2
    assert first is not second
    assert compiler.compile(skill, first_deps) is first  # 同 deps 仍命中快取
    assert len(calls) == 2


def test_compile_cache_evicts_beyond_max(monkeypatch):
    """FIFO 上限：第 _CACHE_MAX+1 個 skill 進來時，最早的那個被淘汰（不無限成長）。"""
    calls = _counting_build(monkeypatch)
    deps = _deps()
    probes = [
        Skill.model_validate({"name": f"evict-probe-{i}", "flow": [{"node": "t_a"}]})
        for i in range(compiler._CACHE_MAX + 1)
    ]

    for skill in probes:
        compiler.compile(skill, deps)
    assert len(calls) == compiler._CACHE_MAX + 1

    compiler.compile(probes[-1], deps)  # 最新的仍在快取
    assert len(calls) == compiler._CACHE_MAX + 1

    compiler.compile(probes[0], deps)  # 最早的已被淘汰 → 重新建圖
    assert len(calls) == compiler._CACHE_MAX + 2


# ---------------------------------------------------------------------------
# compile(cache=False):給 eval runner 用,略過全域快取(不讀也不寫)
# ---------------------------------------------------------------------------


def test_compile_with_cache_false_bypasses_global_cache(monkeypatch):
    """cache=False 每次呼叫都重新建圖,且全域快取一格都不會多——eval 每 case 各自
    build_fixture_deps,若走一般快取,id(deps) 永遠不同只會塞滿 FIFO。"""
    calls = _counting_build(monkeypatch)
    deps = _deps()
    skill = Skill.model_validate(
        {"name": "no-cache-probe", "revision": 1, "flow": [{"node": "t_a"}]}
    )
    before = len(compiler._CACHE)

    first = compiler.compile(skill, deps, cache=False)
    second = compiler.compile(skill, deps, cache=False)

    assert len(calls) == 2  # 兩次都真的重建，沒有命中快取
    assert first is not second
    assert len(compiler._CACHE) == before  # 全域快取未被寫入


def test_compile_cache_false_does_not_evict_cached_entries():
    """eval 的 cache=False 呼叫不會擠掉既有的 cache=True 快取項(≥32 筆也不影響)。"""
    deps = _deps()
    cached_skill = Skill.model_validate(
        {"name": "stays-cached-probe", "revision": 1, "flow": [{"node": "t_a"}]}
    )
    cached_first = compiler.compile(cached_skill, deps)  # 一般快取路徑

    for i in range(compiler._CACHE_MAX * 2):
        probe = Skill.model_validate(
            {"name": f"nocache-probe-{i}", "flow": [{"node": "t_a"}]}
        )
        compiler.compile(probe, deps, cache=False)

    assert compiler.compile(cached_skill, deps) is cached_first  # 仍命中，沒被擠掉


def test_compile_rejects_empty_flow():
    """引擎層護欄：flow 為空在驗證 API 已擋（invalid_flow），繞過它直接 compile 也擋。"""
    skill = Skill.model_validate({"name": "probe-skill", "flow": []})

    with pytest.raises(compiler.SkillCompileError) as exc:
        compiler.compile(skill, _deps())

    assert "flow" in str(exc.value)
