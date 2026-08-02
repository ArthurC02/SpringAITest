"""兩個引擎護欄缺口的回歸測試：script 對 D3 授權鍵的寫入、AST 走訪的深度上限。

為什麼獨立成檔：這兩件事橫跨 script_runner / expressions / compiler / package / API
四個層面，放進任何單一既有測試檔都會讓「一個缺口的完整證據」被拆散。

- **RUNTIME_AUTHORITY_KEYS**（node_shell.py 明文「Skill input、nodes、scripts 都不得
  覆寫」）：tool 步驟的 save_as 走 skill.writable_key() 早就擋住，script 這條路卻沒有。
  寫得進去 = 後續 tool 步驟的 ToolContext 會拿到被竄改的資料範圍授權（權限提升），
  所以這裡不只斷言「鍵被剝除」，還斷言「後面的 tool 拿到的仍是伺服器注入的值」。
- **AST 深度上限**：白名單走訪本身是遞迴的，沒有上限時巢狀約 1000 層的輸入會讓
  RecursionError 穿過 ExpressionError／ScriptViolation 的攔截網 —— 驗證端點 500、
  未信任 zip 的信任邊界 500、執行期則是圖直接炸掉（稽核節點跑不到，違反 AT-GOV-01）。
"""

import ast
import io
import zipfile

import pytest
from fastapi.testclient import TestClient

from app.engine import compiler, expressions, script_runner
from app.engine import tool_registry
from app.engine.node_shell import RUNTIME_AUTHORITY_KEYS
from app.engine.tool_registry import ToolContext
from app.main import app

# import 觸發 kb_query 節點註冊（compiler 強制附加的 audit_feedback 來自這裡）
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from tests.conftest import auth_headers, run_flow

PROBE_TOOL = "local.authority-probe"

# 每個 D3 授權鍵的（伺服器注入的真值, script 想偽造的值）。
# 兩者必須不同，否則「剝除」與「沒剝除」在斷言上分不出來。
AUTHORITY_VALUES: dict[str, tuple] = {
    "run_id": ("run-authentic", "run-forged"),
    "agent_id": ("agent-authentic", "agent-forged"),
    "agent_revision": (7, 99),
    "knowledge_sources": (["kb-allowed"], ["all"]),
    "enforce_data_scope": (True, False),
}

DEEP = 1000  # 實測 ~500 層就會讓白名單走訪撞上 CPython 遞迴上限


@pytest.fixture(autouse=True, scope="module")
def _register_probe_tool():
    """throwaway tool：把自己收到的 ToolContext 授權欄位記下來。"""
    seen: list[ToolContext] = []

    @tool_registry.tool(
        name=PROBE_TOOL, kind="local", description="授權探針", risk="read"
    )
    async def probe(ctx: ToolContext) -> dict:
        seen.append(ctx)
        return {"called": True}

    yield seen
    tool_registry._REGISTRY.pop(PROBE_TOOL, None)


@pytest.fixture
def probe_contexts(_register_probe_tool):
    _register_probe_tool.clear()
    return _register_probe_tool


def _authority_state() -> dict:
    return {key: authentic for key, (authentic, _) in AUTHORITY_VALUES.items()}


# ---------------------------------------------------------------------------
# P0：script 不得覆寫 D3 RUNTIME_AUTHORITY_KEYS
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("key", sorted(RUNTIME_AUTHORITY_KEYS))
def test_script_cannot_forge_runtime_authority_keys(key):
    """直接吃生產常數集合：日後新增授權鍵會自動被這條測試涵蓋（缺 seed 值即 KeyError）。"""
    assert set(AUTHORITY_VALUES) == set(RUNTIME_AUTHORITY_KEYS)
    authentic, forged = AUTHORITY_VALUES[key]

    result = run_flow(
        [{"script": f"state[{key!r}] = {forged!r}\nstate['ran'] = True"}],
        _authority_state(),
    )

    assert result["ran"] is True  # script 有跑（不是整段被拒）
    assert result[key] == authentic


def test_script_cannot_disable_data_scope_for_a_later_tool_step(probe_contexts):
    """權限提升的完整證據：script 關掉 enforce_data_scope → 後續 tool 的 ToolContext。

    compiler.tool_context()（compiler.py:284-296）從 state 取這兩個值餵給 ToolContext，
    因此「script 寫得進 state」等於「同一個 Skill 內、script 之後的每個 tool 呼叫都在
    未強制資料範圍、且知識來源被放寬成 all 的授權下執行」。
    """
    result = run_flow(
        [
            {
                "script": (
                    "state['enforce_data_scope'] = False\n"
                    "state['knowledge_sources'] = ['all']\n"
                )
            },
            {"tool": PROBE_TOOL, "save_as": "probe_out"},
        ],
        _authority_state(),
    )

    assert result["probe_out"] == {"called": True}  # tool 真的跑過
    assert len(probe_contexts) == 1
    ctx = probe_contexts[0]
    assert ctx.enforce_data_scope is True
    assert ctx.knowledge_sources == frozenset({"kb-allowed"})


# ---------------------------------------------------------------------------
# P1：AST 走訪深度上限（四個入口都不得逃出 RecursionError）
# ---------------------------------------------------------------------------


def _deep_expression(depth: int = DEEP) -> str:
    return ("not " * depth) + "state.a"


def _deep_script(depth: int = DEEP) -> str:
    return "state['x'] = " + ("not " * depth) + "1"


def _expression_of_ast_depth(depth: int) -> str:
    """恰好 depth 層的條件式（`state.a` 自身占 3 層：Attribute → Name → Load）。"""
    source = _deep_expression(depth - 3)
    assert expressions.ast_depth(ast.parse(source, mode="eval").body) == depth
    return source


def _script_of_ast_depth(depth: int) -> str:
    """恰好 depth 層的 script（Module → Assign → 巢狀 UnaryOp → Constant）。"""
    source = _deep_script(depth - 3)
    assert expressions.ast_depth(ast.parse(source, mode="exec")) == depth
    return source


def test_expression_ast_depth_limit_accepts_on_point_and_rejects_off_point():
    expressions.validate(_expression_of_ast_depth(expressions.MAX_AST_DEPTH))

    with pytest.raises(expressions.ExpressionError, match="巢狀深度"):
        expressions.validate(_expression_of_ast_depth(expressions.MAX_AST_DEPTH + 1))


def test_script_ast_depth_limit_accepts_on_point_and_rejects_off_point():
    contract = script_runner.scan(_script_of_ast_depth(script_runner.MAX_AST_DEPTH))
    assert contract.writes == ("x",)

    with pytest.raises(script_runner.ScriptViolation, match="巢狀深度"):
        script_runner.scan(_script_of_ast_depth(script_runner.MAX_AST_DEPTH + 1))


def test_deep_expression_is_expression_error_not_recursion_error():
    with pytest.raises(expressions.ExpressionError):
        expressions.validate(_deep_expression())


def test_deep_script_is_script_violation_not_recursion_error():
    with pytest.raises(script_runner.ScriptViolation):
        script_runner.scan(_deep_script())


def test_deep_expression_at_runtime_takes_safe_path_and_still_audits():
    """入口 4：執行期求值。RecursionError 穿出去 = 圖炸掉、稽核節點永遠跑不到。"""
    with pytest.raises(expressions.ExpressionError):
        expressions.evaluate(_deep_expression(), {"a": 1})

    out = compiler._safe_eval(
        _deep_expression(), {"a": 1}, "__loop_0_exit", on_error=True, where="loop_0"
    )

    assert out["__loop_0_exit"] is True  # 安全值：離開迴圈
    assert out["errors"][0]["error_type"] == "ExpressionError"


def test_validate_endpoint_returns_invalid_expression_for_deep_when():
    """入口 1：POST /skills/validate 永遠 200 + {valid, errors[]}，不得 500。"""
    definition = (
        "name: deep-probe\n"
        "description: 深度探針\n"
        "input_schema:\n"
        "  query: {type: str, required: true, min_length: 1}\n"
        "flow:\n"
        "  - branch:\n"
        f"      when: {_deep_expression()}\n"
        "      then:\n"
        "        - node: query_intake@1.0\n"
    )
    resp = TestClient(app).post(
        "/skills/validate", json={"definition": definition}, headers=auth_headers()
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert [e["code"] for e in body["errors"]] == ["invalid_expression"]


def test_validate_endpoint_returns_forbidden_script_for_deep_script():
    """入口 2：同一端點的 script 那一半（作者是 ADMIN，排除撰寫者角色 gate 的干擾）。"""
    definition = (
        "name: deep-probe\n"
        "description: 深度探針\n"
        "flow:\n"
        f"  - script: {_deep_script()}\n"
    )
    resp = TestClient(app).post(
        "/skills/validate",
        json={"definition": definition},
        headers=auth_headers(role="ADMIN"),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert [e["code"] for e in body["errors"]] == ["forbidden_script"]


def test_validate_package_endpoint_returns_forbidden_script_for_deep_script():
    """入口 3：未信任 zip 的信任邊界 —— package.py 花 500 行硬化 zip，不能倒在這裡。"""
    md = (
        "---\nname: deep-pkg\ndescription: 深度探針\n"
        "metadata:\n  kind: agentic\n---\n\nBody.\n"
    )
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("SKILL.md", md)
        z.writestr("scripts/deep.py", _deep_script())
    resp = TestClient(app).post(
        "/skills/validate-package",
        files={"package": ("skill.zip", buf.getvalue(), "application/zip")},
        data={"expected_name": "deep-pkg"},
        headers=auth_headers(role="ADMIN"),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert [e["code"] for e in body["errors"]] == ["forbidden_script"]
    assert "canonical_definition" not in body  # 失敗回應不得帶可寫入的 metadata


# ---------------------------------------------------------------------------
# P1b：ast.parse 自己丟的 MemoryError（深度上限是在 parse 之後才量的，攔不到）
# ---------------------------------------------------------------------------

# CPython 的 PEG parser 對無括號的一元運算鏈吃 C 堆疊，超過數千層時丟的是
# MemoryError("Parser stack overflowed")——不是 SyntaxError，也還沒輪到 MAX_AST_DEPTH。
# 上面那組深度測試因此只把門檻從 ~1000 抬到 ~15000，門並沒有關上：`'not ' * 20000`
# 約 80KB，definition 欄位沒有長度上限，走得到就是 validate 端點 500。
PARSER_OVERFLOW = 20000


def test_expression_beyond_parser_stack_is_expression_error_not_memory_error():
    with pytest.raises(expressions.ExpressionError):
        expressions.validate(_deep_expression(PARSER_OVERFLOW))


def test_script_beyond_parser_stack_is_script_violation_not_memory_error():
    with pytest.raises(script_runner.ScriptViolation):
        script_runner.scan(_deep_script(PARSER_OVERFLOW))


@pytest.mark.parametrize(
    "definition, role, code",
    [
        pytest.param(
            "name: deep-probe\n"
            "description: 深度探針\n"
            "input_schema:\n"
            "  query: {type: str, required: true, min_length: 1}\n"
            "flow:\n"
            "  - branch:\n"
            f"      when: {_deep_expression(PARSER_OVERFLOW)}\n"
            "      then:\n"
            "        - node: query_intake@1.0\n",
            "USER",
            "invalid_expression",
            id="when",
        ),
        pytest.param(
            "name: deep-probe\n"
            "description: 深度探針\n"
            "flow:\n"
            f"  - script: {_deep_script(PARSER_OVERFLOW)}\n",
            "ADMIN",
            "forbidden_script",
            id="script",
        ),
    ],
)
def test_validate_endpoint_stays_200_beyond_parser_stack(definition, role, code):
    """入口 1／2 × parser 堆疊溢位：上面那兩條端點測試只驗到 MAX_AST_DEPTH+1 這個量級。

    真正先發生的是 ast.parse 自己丟的 MemoryError（深度上限量在 parse 之後，攔不到）。
    definition 欄位沒有長度上限，80KB 的一元運算鏈送得進 HTTP body —— 這裡證明兩種
    形狀（branch 的 when、script 原文）在該量級下仍是 200 + errors[]，不是 500。
    script 那半邊同時是 validate_source 在此量級下唯一的 script 路徑覆蓋。
    """
    resp = TestClient(app).post(
        "/skills/validate",
        json={"definition": definition},
        headers=auth_headers(role=role),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert [e["code"] for e in body["errors"]] == [code]
    assert "skill" not in body  # 失敗回應不得帶可寫入的 metadata


def test_validate_source_returns_invalid_expression_for_parser_overflow():
    """端到端：例外逃出 validate_source = /skills/validate 500（違反「永遠 200 + errors[]」）。"""
    from app.engine.skill import validate_source

    result = validate_source(
        "name: deep-probe\n"
        "description: 深度探針\n"
        "input_schema:\n"
        "  query: {type: str, required: true, min_length: 1}\n"
        "flow:\n"
        "  - branch:\n"
        f"      when: {_deep_expression(PARSER_OVERFLOW)}\n"
        "      then:\n"
        "        - node: query_intake@1.0\n"
    )

    assert result.valid is False
    assert [e.code for e in result.errors] == ["invalid_expression"]
