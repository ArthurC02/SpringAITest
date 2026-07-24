"""Skill 靜態驗證測試（AT2-01 ~ AT2-08、AT2-20、AT-GOV-02）。

錯誤碼一律照規格 §3.4 逐字比對。決策表兩邊都測：非法輸入被擋下之外，
合法的 kb_query.yaml 也必須「零阻擋級錯誤、零 dataflow 警告」地通過。
"""

import pytest

from app.engine import compiler, skill as skill_mod
from app.engine.skill import (
    DATAFLOW_ERROR,
    INVALID_EXPRESSION,
    INVALID_FLOW,
    INVALID_NAME,
    INVALID_SCHEMA,
    UNBOUNDED_LOOP,
    UNKNOWN_NODE,
    validate_source,
)

# import 觸發節點註冊（驗證器要查 registry）
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from app.nodes import nl_logic as _nl_logic  # noqa: F401
from app.nodes import retrieve as _retrieve_node  # noqa: F401

HEAD = """
name: probe-skill
description: 驗證測試用
required_role: USER
input_schema:
  query: {type: str, required: true, min_length: 1}
"""


def _codes(result) -> list[str]:
    return [e.code for e in result.errors]


def _validate(flow_yaml: str):
    return validate_source(HEAD + flow_yaml)


# ---------------------------------------------------------------------------
# AT2-01 unknown_node
# ---------------------------------------------------------------------------


def test_unknown_node():
    """【AT2-01】引用未註冊的節點 → unknown_node，且不可存檔。"""
    result = _validate("flow:\n  - node: no_such_node\n")

    assert result.valid is False
    assert UNKNOWN_NODE in _codes(result)


def test_unknown_node_version():
    """鎖了不存在的版本一樣是 unknown_node（節點名對、版本錯）。"""
    result = _validate("flow:\n  - node: query_intake@9.9\n")

    assert result.valid is False
    assert UNKNOWN_NODE in _codes(result)


def test_known_node_with_pinned_version_passes():
    """決策表另一半：存在的 node@version 不得誤報。"""
    result = _validate("flow:\n  - node: query_intake@1.0\n")

    assert UNKNOWN_NODE not in _codes(result)
    assert result.valid is True


# ---------------------------------------------------------------------------
# AT2-02 / AT2-03 unbounded_loop（含 1~10 的上下界 on-point / off-point）
# ---------------------------------------------------------------------------


def test_loop_without_max_iterations():
    """【AT2-02】loop 缺 max_iterations → unbounded_loop。"""
    result = _validate(
        "flow:\n"
        "  - loop:\n"
        "      until: \"state.x == 1\"\n"
        "      body:\n"
        "        - node: query_intake\n"
    )

    assert result.valid is False
    assert UNBOUNDED_LOOP in _codes(result)


@pytest.mark.parametrize(
    "max_iterations,expect_valid",
    [
        (0, False),  # 【AT2-03】下界外
        (1, True),  # 下界內（on-point）
        (10, True),  # 上界內（on-point）
        (11, False),  # 【AT2-03】上界外
    ],
)
def test_loop_max_iterations_bounds(max_iterations, expect_valid):
    """【AT2-03】max_iterations 必須落在 1~10；邊界內外各測一次。"""
    result = _validate(
        "flow:\n"
        "  - loop:\n"
        f"      max_iterations: {max_iterations}\n"
        '      until: "state.x == 1"\n'
        "      body:\n"
        "        - node: query_intake\n"
    )

    assert result.valid is expect_valid
    assert (UNBOUNDED_LOOP in _codes(result)) is not expect_valid


# ---------------------------------------------------------------------------
# AT2-04 invalid_expression
# ---------------------------------------------------------------------------


def test_invalid_expression_in_until():
    """【AT2-04】until 含函式呼叫／屬性鏈 → invalid_expression。"""
    result = _validate(
        "flow:\n"
        "  - loop:\n"
        "      max_iterations: 2\n"
        "      until: \"os.system('echo x')\"\n"
        "      body:\n"
        "        - node: query_intake\n"
    )

    assert result.valid is False
    assert INVALID_EXPRESSION in _codes(result)


def test_invalid_expression_in_branch_when():
    """branch 的 when 走同一條白名單。"""
    result = _validate(
        "flow:\n"
        "  - branch:\n"
        "      when: \"len(state.x) > 0\"\n"
        "      then:\n"
        "        - node: query_intake\n"
    )

    assert result.valid is False
    assert INVALID_EXPRESSION in _codes(result)


# ---------------------------------------------------------------------------
# AT2-20 __ 前綴的引擎鍵不可被 Skill 讀寫
# ---------------------------------------------------------------------------


def test_loop_counter_key_not_readable_by_skill():
    """【AT2-20】條件式引用 state.__loop_0_count → invalid_expression（引擎鍵非 Skill 範圍）。"""
    result = _validate(
        "flow:\n"
        "  - branch:\n"
        "      when: \"state.__loop_0_count > 1\"\n"
        "      then:\n"
        "        - node: query_intake\n"
    )

    assert result.valid is False
    assert INVALID_EXPRESSION in _codes(result)


# ---------------------------------------------------------------------------
# AT2-05 ~ AT2-07 invalid_flow
# ---------------------------------------------------------------------------


def test_empty_flow():
    """【AT2-05】flow 為空 → invalid_flow。"""
    result = _validate("flow: []\n")

    assert result.valid is False
    assert INVALID_FLOW in _codes(result)


def test_unknown_step_type():
    """【AT2-06】步驟型別不在 6 種之內（parallel）→ invalid_flow。"""
    result = _validate("flow:\n  - parallel:\n      - node: query_intake\n")

    assert result.valid is False
    assert INVALID_FLOW in _codes(result)


def test_yaml_syntax_error_collapses_to_invalid_flow():
    """【AT2-07】壞 YAML（未閉合引號）→ invalid_flow，不拋未捕捉例外。"""
    result = validate_source('name: probe-skill\nflow:\n  - node: "query_intake\n')

    assert result.valid is False
    assert INVALID_FLOW in _codes(result)
    assert result.errors[0].line is not None  # 錯誤指得到行號


def test_non_mapping_definition_collapses_to_invalid_flow():
    """YAML 合法但不是 mapping（例如只是一個字串）→ 一樣是 invalid_flow。"""
    result = validate_source("just a string")

    assert result.valid is False
    assert INVALID_FLOW in _codes(result)


def test_bad_ascii_name_is_invalid_name():
    """name 不符 ^[a-z][a-z0-9_]{2,63}$（大寫/連字號）→ invalid_name，不再誤標 invalid_flow。"""
    result = validate_source("name: Bad-Name\nflow:\n  - node: query_intake\n")

    assert result.valid is False
    assert INVALID_NAME in _codes(result)
    assert INVALID_FLOW not in _codes(result)


def test_chinese_name_is_invalid_name_with_clean_message():
    """中文 name（正是使用者踩到的案例）→ invalid_name，訊息講名稱規則，且不外洩 pydantic dump/URL。"""
    result = validate_source("name: 測試\nflow:\n  - node: query_intake\n")

    assert result.valid is False
    assert INVALID_NAME in _codes(result)
    msg = next(e.message for e in result.errors if e.code == INVALID_NAME)
    assert "名稱" in msg
    assert "pydantic.dev" not in msg
    assert "validation error for Skill" not in msg


# ---------------------------------------------------------------------------
# 標準 name 規則（§1）on/off-point：^[a-z0-9]([a-z0-9-]*[a-z0-9])?$、1–64、無 `--`、無底線
# ---------------------------------------------------------------------------


def _name_result(name: str):
    return validate_source(f"name: {name}\nflow:\n  - node: query_intake@1.0\n")


@pytest.mark.parametrize("name", ["kb-query", "9x", "x", "a" * 64, "a-b-c", "rag-qa"])
def test_standard_name_accepted(name):
    """標準連字號名稱（含數字開頭、單字、64 字上限）通過 —— 不落 invalid_name。"""
    assert INVALID_NAME not in _codes(_name_result(name))


@pytest.mark.parametrize(
    "name",
    [
        "kb_query",  # 底線不再合法（決策表：舊名現在被拒）
        "-x",  # 開頭連字號
        "x-",  # 結尾連字號
        "x--y",  # 連續連字號
        "a" * 65,  # 65 字（off-point）
        "Bad",  # 大寫
    ],
)
def test_nonstandard_name_rejected(name):
    result = _name_result(name)
    assert result.valid is False
    assert INVALID_NAME in _codes(result)


def test_other_schema_field_error_is_invalid_schema():
    """非 name 欄位的 schema 錯（required_role 值不在 Literal 內）→ invalid_schema，訊息點名該欄位。"""
    result = validate_source(
        "name: probe-skill\nrequired_role: SUPERUSER\nflow:\n  - node: query_intake\n"
    )

    assert result.valid is False
    assert INVALID_SCHEMA in _codes(result)
    assert INVALID_NAME not in _codes(result)
    msg = next(e.message for e in result.errors if e.code == INVALID_SCHEMA)
    assert "required_role" in msg
    assert "pydantic.dev" not in msg


# ---------------------------------------------------------------------------
# AT2-08 dataflow_error（警告級：有錯但仍可存檔）
# ---------------------------------------------------------------------------


def test_dataflow_error_is_warning_level():
    """【AT2-08】直接跑 answer_composer：reads 的鍵無前置 writes → dataflow_error，但 valid 仍為 true。"""
    result = _validate("flow:\n  - node: answer_composer\n")

    codes = _codes(result)
    assert DATAFLOW_ERROR in codes
    assert set(codes) == {DATAFLOW_ERROR}  # 沒有其他阻擋級錯誤混進來
    assert result.valid is True  # 警告級不阻擋存檔
    assert any("verification_result" in e.message for e in result.errors)


def test_dataflow_error_disappears_when_prerequisites_present():
    """決策表另一半：補上前置節點後就不該再報 dataflow_error。"""
    result = _validate(
        "flow:\n"
        "  - node: query_intake\n"
        "  - node: query_rewrite\n"
        "  - node: intent_classification\n"
    )

    assert _codes(result) == []
    assert result.valid is True


def test_retrieve_dynamic_reads_resolved_from_params():
    """retrieve 的 query_key 是建構期參數：靜態檢查用 params 解析成實際 state 鍵。"""
    missing = _validate("flow:\n  - node: retrieve\n    params: {query_key: nowhere}\n")
    assert DATAFLOW_ERROR in _codes(missing)
    assert any("nowhere" in e.message for e in missing.errors)

    supplied = _validate("flow:\n  - node: retrieve\n    params: {query_key: query}\n")
    assert _codes(supplied) == []  # query 來自 input_schema


def test_nl_logic_list_input_keys_dataflow_check():
    """P0-c：dynamic_reads 的 list 值（input_keys: [...]）逐項做資料流檢查，
    不再因 list 型別整包誤報「需要 params.input_keys 指定要讀取的 state 鍵」。"""
    # off-point：list 指向未寫入的鍵 → 該鍵逐項報 dataflow_error
    missing = _validate(
        "flow:\n  - node: nl_logic\n    params: {instruction: r, input_keys: [nowhere]}\n"
    )
    assert DATAFLOW_ERROR in _codes(missing)
    assert any("nowhere" in e.message for e in missing.errors)
    # on-point：list 指向 input_schema 的 query → 既不誤報「需指定」，該鍵也不報「無前置」
    supplied = _validate(
        "flow:\n  - node: nl_logic\n    params: {instruction: r, input_keys: [query]}\n"
    )
    assert not any("需要 params.input_keys" in e.message for e in supplied.errors)
    assert not any(
        "'query'" in e.message and "input_keys" in e.message for e in supplied.errors
    )


def test_min_length_on_numeric_type_rejected_at_write_time():
    """B1-py：int/float/bool + min_length 是執行期必炸的死欄位 → 寫入期即以
    invalid_schema 阻擋（valid=false）。決策表另一半：str + min_length 仍合法。"""
    bad = validate_source(
        "name: probe-skill\n"
        "input_schema:\n"
        "  count: {type: int, min_length: 2}\n"
        "flow:\n  - node: query_intake\n"
    )
    assert bad.valid is False
    assert INVALID_SCHEMA in _codes(bad)

    ok = validate_source(
        "name: probe-skill\n"
        "input_schema:\n"
        "  query: {type: str, required: true, min_length: 2}\n"
        "flow:\n  - node: query_intake\n"
    )
    assert INVALID_SCHEMA not in _codes(ok)


# ---------------------------------------------------------------------------
# 內建 kb_query.yaml：完全乾淨地通過（決策表的「合法輸入」那半邊）
# ---------------------------------------------------------------------------


def test_builtin_kb_query_yaml_validates_clean():
    from pathlib import Path

    import app.skills

    path = Path(app.skills.__file__).parent / "kb-query.yaml"
    result = validate_source(path.read_text(encoding="utf-8"))

    assert result.errors == []  # 連 dataflow 警告都不該有
    assert result.valid is True


# ---------------------------------------------------------------------------
# AT-GOV-02 loop 上限不可省：繞過 API 直接呼叫 compiler.compile() 也擋
# ---------------------------------------------------------------------------


def test_compiler_rejects_unbounded_loop_even_without_api_validation():
    """【AT-GOV-02】治理硬規則寫在引擎層：compile() 對缺 max_iterations 的 loop 一樣拒編。"""
    definition = {
        "name": "probe-skill",
        "flow": [
            {
                "loop": {
                    "until": "state.x == 1",
                    "body": [{"node": "query_intake"}],
                }
            }
        ],
    }
    skill = skill_mod.Skill.model_validate(definition)

    with pytest.raises(compiler.SkillCompileError) as exc:
        compiler.compile(skill, deps=None)

    assert "max_iterations" in str(exc.value)


@pytest.mark.parametrize("max_iterations", [0, 11])
def test_compiler_rejects_out_of_range_loop_bound(max_iterations):
    """【AT-GOV-02】超出 1~10 的上限同樣在編譯期擋下，不只在驗證 API。"""
    skill = skill_mod.Skill.model_validate(
        {
            "name": "probe-skill",
            "flow": [
                {
                    "loop": {
                        "max_iterations": max_iterations,
                        "until": "state.x == 1",
                        "body": [{"node": "query_intake"}],
                    }
                }
            ],
        }
    )

    with pytest.raises(compiler.SkillCompileError):
        compiler.compile(skill, deps=None)


def test_compiler_rejects_invalid_expression():
    """條件式的白名單在編譯期也是硬規則（繞過 validate 一樣過不了）。"""
    skill = skill_mod.Skill.model_validate(
        {
            "name": "probe-skill",
            "flow": [
                {
                    "branch": {
                        "when": "os.system('x')",
                        "then": [{"node": "query_intake"}],
                    }
                }
            ],
        }
    )

    with pytest.raises(Exception) as exc:
        compiler.compile(skill, deps=None)

    assert exc.type.__name__ in {"SkillCompileError", "ExpressionError"}


# ---------------------------------------------------------------------------
# 定義端點 flow-only：kind: agentic 的授權漂移必須被權威拒絕（agentic_requires_import）
# ---------------------------------------------------------------------------


def test_definition_declaring_agentic_kind_rejected():
    """crafted-valid-flow 洞：flow 本身合法但自稱 kind: agentic → 定義端點必須擋下。

    agentic 只能經 package 匯入；定義原文宣稱 agentic 是漂移，會在 P1 變 active break。
    """
    result = validate_source(
        "name: probe-skill\nkind: agentic\nflow:\n  - node: query_intake@1.0\n"
    )

    assert result.valid is False
    assert result.errors[0].code == "agentic_requires_import"
    assert result.skill is None


def test_definition_flow_kind_still_validates():
    """決策表另一半:kind: flow（或省略,預設 flow）的合法定義照舊通過。"""
    assert validate_source(
        "name: probe-skill\nkind: flow\nflow:\n  - node: query_intake@1.0\n"
    ).valid is True
    assert _validate("flow:\n  - node: query_intake@1.0\n").valid is True
