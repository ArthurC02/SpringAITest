"""Script Runner 沙箱逃逸測試集（AT3-01 ~ AT3-11 + 上限邊界）。

規格 §4.1 表格的 11 個攻擊樣本逐一 parametrize：AT3-01~10 存檔期即拒絕
（forbidden_script），AT3-11 靜態算不出上界（range(n) 的 n 可能來自 state）→
執行期以受控例外中止（不是未捕捉 crash、不是靜默通過）。

白名單的兩個對稱性一併驗：
1. **驗證期與執行期擋同一套**：繞過存檔驗證直接呼叫 runner.run()，同一份攻擊樣本
   一樣 ScriptViolation。只在存檔擋 = 只要有第二個入口就破功。
2. **決策表另一半**：合法的 script 要真的跑得起來、寫得進 state ——
   「全部拒絕」不是安全，是壞掉。
"""

import asyncio

import pytest

from app.engine.script_runner import (
    MAX_ALLOC,
    MAX_ITERATIONS,
    MAX_TIMEOUT_MS,
    MAX_WRITE_BYTES,
    RestrictedInProcessRunner,
    ScriptLimitExceeded,
    ScriptLimits,
    ScriptTimeout,
    ScriptViolation,
    scan,
)
from app.engine.skill import FORBIDDEN_SCRIPT, INVALID_FLOW, validate_definition

# import 觸發節點註冊（validate_definition 會查 registry）
from app.kbquery import nodes as _kbquery_nodes  # noqa: F401

# ---------------------------------------------------------------------------
# 規格 §4.1 的 11 個攻擊樣本
# ---------------------------------------------------------------------------

STATIC_ATTACKS = [
    ("AT3-01-import", "import os"),
    ("AT3-02-from-import", "from os import system"),
    ("AT3-03-exec", "exec(\"state['x']=1\")"),
    ("AT3-04-eval", "eval('1+1')"),
    ("AT3-05-compile", "compile('1+1', '<s>', 'eval')"),
    ("AT3-06-open", "open('/etc/passwd')"),
    ("AT3-07-dunder-import", "__import__('os')"),
    ("AT3-08-dunder-attr", "state.__class__.__bases__"),
    ("AT3-09-global", "global x\nx = 1"),
    ("AT3-09-nonlocal", "nonlocal x"),
    ("AT3-10-while", "while True:\n    pass"),
]

# 白名單「預設拒絕」的其餘面：這些沒有出現在規格的表格裡，但同樣不在允許清單內，
# 因此必須是拒絕（黑名單寫法會全部漏掉）。
EXTRA_ATTACKS = [
    ("lambda", "f = lambda: 1"),
    ("def", "def f():\n    return 1"),
    ("class", "class C:\n    pass"),
    ("comprehension", "xs = [i for i in range(10)]"),  # 會繞過 for 的迭代計數器
    ("generator", "xs = sum(i for i in range(10))"),
    ("try", "try:\n    x = 1\nexcept Exception:\n    pass"),
    ("with", "with open('x') as f:\n    pass"),
    ("attribute-non-dunder", "x = 'abc'.upper()"),  # 只允許 tools.call 一個屬性
    ("subclasses-chain", "x = ().__class__.__mro__"),
    ("getattr-builtin", "x = getattr(state, 'keys')"),
    ("import-alias", "import os as o"),
    ("assert", "assert state['x'] == 1"),
    ("del-state-key", "del state['x']"),
    ("engine-key-write", "state['__loop_0_count'] = 99"),  # AT2-20 的 script 那一半
    ("engine-key-read", "x = state['__loop_0_count']"),
    ("dynamic-state-key", "k = 'x'\nstate[k] = 1"),  # 動態鍵無法靜態稽核
    ("call-local", "f = state['fn']\nf()"),  # 呼叫區域變數：state 內若有 callable 就是逃逸
    ("walrus", "x = (y := 1)"),
    # range() 是唯一有靜態上限的配置護欄（長度精確可算、無等價改寫繞過）。
    # 序列乘法／pow 的上限已移除 —— 見 test_memory_bombs_are_an_honest_gap。
    ("mem-bomb-range", f"xs = range({MAX_ALLOC + 1})"),
]

ALL_ATTACKS = STATIC_ATTACKS + EXTRA_ATTACKS


def _save(source: str, **step_fields):
    """存檔驗證（等同 POST /skills/validate 的程式路徑）。"""
    return validate_definition(
        {"name": "probe_skill", "flow": [{"script": source, **step_fields}]}
    )


def _codes(result) -> list[str]:
    return [e.code for e in result.errors]


def _run(source: str, state: dict | None = None, tools=None, **limits):
    return asyncio.run(
        RestrictedInProcessRunner().run(
            source, state or {}, tools, ScriptLimits(**limits)
        )
    )


@pytest.mark.parametrize("source", [s for _, s in ALL_ATTACKS], ids=[i for i, _ in ALL_ATTACKS])
def test_attack_sample_rejected_at_save_time(source):
    """【AT3-01 ~ AT3-10】存檔靜態掃描 → {valid: false, errors: [{code: forbidden_script}]}。"""
    result = _save(source)

    assert result.valid is False
    assert FORBIDDEN_SCRIPT in _codes(result)


@pytest.mark.parametrize("source", [s for _, s in ALL_ATTACKS], ids=[i for i, _ in ALL_ATTACKS])
def test_attack_sample_rejected_at_run_time(source):
    """繞過存檔驗證直接執行 → 一樣 ScriptViolation（執行前再驗一次，規格 §5.2）。"""
    with pytest.raises(ScriptViolation):
        _run(source)


def test_forbidden_script_survives_yaml_entry_point():
    """走 YAML 原文（前端編輯器的實際路徑）一樣擋得下來。"""
    from app.engine.skill import validate_source

    result = validate_source(
        "name: probe_skill\nflow:\n  - script: |\n      import os\n      state['x'] = 1\n"
    )

    assert result.valid is False
    assert FORBIDDEN_SCRIPT in _codes(result)


# ---------------------------------------------------------------------------
# AT3-11 迭代上限：靜態算不出來 → 執行期計數中止
# ---------------------------------------------------------------------------


def test_iteration_limit_is_not_a_save_time_error():
    """【AT3-11】前置：range(10001) 是合法語法，存檔不該擋（否則就沒有「執行期擋」這回事）。"""
    result = _save(f"for i in range({MAX_ITERATIONS + 1}):\n    pass")

    assert result.valid is True
    assert FORBIDDEN_SCRIPT not in _codes(result)


def test_iteration_limit_exceeded_at_runtime():
    """【AT3-11】for i in range(10001) → 執行期受控例外（非未捕捉 crash）。"""
    with pytest.raises(ScriptLimitExceeded) as exc:
        _run(f"for i in range({MAX_ITERATIONS + 1}):\n    pass")

    assert str(MAX_ITERATIONS) in str(exc.value)


def test_iteration_limit_exceeded_with_dynamic_bound_from_state():
    """【AT3-11】上界來自 state（靜態完全算不出來）→ 一樣執行期擋。"""
    with pytest.raises(ScriptLimitExceeded):
        _run(
            "for i in range(state['n']):\n    pass",
            {"n": MAX_ITERATIONS + 1},
        )


def test_iteration_limit_on_point_passes():
    """邊界 on-point：恰好 10000 次迭代跑得完（決策表另一半）。"""
    writes = _run(
        f"total = 0\nfor i in range({MAX_ITERATIONS}):\n    total = total + 1\n"
        "state['total'] = total"
    )

    assert writes == {"total": MAX_ITERATIONS}


def test_iteration_limit_counts_across_nested_loops():
    """迭代計數是整段 script 的總量：巢狀迴圈不能靠拆成兩層繞過上限。"""
    with pytest.raises(ScriptLimitExceeded):
        _run("for i in range(200):\n    for j in range(200):\n        pass")


# ---------------------------------------------------------------------------
# 記憶體炸彈：常數上限（靜態）+ 動態上限（執行期）
# ---------------------------------------------------------------------------


def test_range_alloc_limit_on_point_passes():
    """邊界 on-point：range(10**6) 剛好在上限內。"""
    writes = _run(f"state['n'] = len(range({MAX_ALLOC}))")

    assert writes == {"n": MAX_ALLOC}


def test_range_alloc_limit_from_state_blocked_at_runtime():
    """動態 range(n)（n 來自 state）超過 10^6 → 執行期擋（常數上限漏掉的那半邊）。"""
    with pytest.raises(ScriptLimitExceeded):
        _run("state['n'] = len(range(state['n']))", {"n": MAX_ALLOC + 1})


def test_sequence_mul_within_limit_passes():
    """序列乘法沒有專屬護欄了（見 honest-gap 測試）；一般用法照常運作。"""
    writes = _run("state['s'] = 'ab' * 3")

    assert writes == {"s": "ababab"}


def test_pow_within_limit_passes():
    """pow 沒有專屬護欄了（見 honest-gap 測試）；一般次方運算照常運作。"""
    writes = _run("state['x'] = 2 ** state['n']", {"n": 10})

    assert writes == {"x": 1024}


# ---------------------------------------------------------------------------
# 誠實邊界：§5.0「防意外不防惡意」—— 記憶體/CPU 炸彈 in-process 版就是擋不住。
# 這不是待修的洞，是刻意記錄的邊界（釘住行為，免得日後有人誤加半套黑名單 guard：
# 擋了 `*` 有 `+`、擋了 `+` 有 `sum`，逐點式 guard 只會給人虛假安全感）。
# ---------------------------------------------------------------------------


def test_memory_bombs_are_an_honest_gap():
    """序列加法倍增：10 圈就 2^10 元素 —— 迭代上限(圈數)與 256KB(寫回)都攔不住。

    不斷言「被擋」（in-process 版擋不住，§5.0 已聲明），只釘住「這條路沒有半套 guard」：
    - 迭代計數界定的是圈數不是量級（10 圈遠低於 10000）。
    - 256KB 只在寫回 state 時檢查；`xs` 沒被宣告寫入 → 從沒觸發大小檢查。
    真正的牆是 v2 subprocess + rlimits（§5.4）。此處用小指數確保測試本身快而不炸記憶體。
    """
    writes = _run("xs = [0]\nfor i in range(10):\n    xs = xs + xs\nstate['n'] = len(xs)")

    assert writes == {"n": 1024}  # 2^10，白名單內、無護欄攔阻


def test_pow_accumulation_is_an_honest_gap():
    """pow 累乘同理：單次指數再小，累乘量級照樣爆 —— 逐點式 guard 擋不完，故一律不擋。"""
    writes = _run("x = 2\nfor i in range(4):\n    x = x ** 2\nstate['x'] = x")

    assert writes == {"x": 65536}  # (((2^2)^2)^2)^2 = 2^16，無護欄攔阻


# ---------------------------------------------------------------------------
# timeout_ms 邊界（預設 2000、上限 10000）
# ---------------------------------------------------------------------------


def test_timeout_ms_upper_bound_on_point():
    """邊界 on-point：timeout_ms = 10000 可用。"""
    assert ScriptLimits(timeout_ms=MAX_TIMEOUT_MS).timeout_ms == MAX_TIMEOUT_MS
    assert _save("state['x'] = 1", timeout_ms=MAX_TIMEOUT_MS).valid is True


def test_timeout_ms_upper_bound_off_point():
    """邊界 off-point：timeout_ms = 10001 → 引擎拒絕、存檔拒絕（invalid_flow）。"""
    with pytest.raises(ScriptViolation):
        ScriptLimits(timeout_ms=MAX_TIMEOUT_MS + 1)

    result = _save("state['x'] = 1", timeout_ms=MAX_TIMEOUT_MS + 1)
    assert result.valid is False
    assert INVALID_FLOW in _codes(result)


def test_timeout_ms_lower_bound():
    """下界：0 不合法、1 合法。"""
    with pytest.raises(ScriptViolation):
        ScriptLimits(timeout_ms=0)
    assert ScriptLimits(timeout_ms=1).timeout_ms == 1


def test_default_timeout_is_2000ms():
    assert ScriptLimits().timeout_ms == 2000


def test_cpu_bound_loop_times_out():
    """【AT3-12 的沙箱那一半】合法語法但跑滿 CPU + timeout_ms=1 → ScriptTimeout。"""
    with pytest.raises(ScriptTimeout):
        _run("x = 0\nfor i in range(1000000):\n    x = i * i", timeout_ms=1)


# ---------------------------------------------------------------------------
# state 寫入大小上限 256KB（AT3-13 的沙箱那一半）
# ---------------------------------------------------------------------------


def test_state_write_size_off_point_rejected():
    """【AT3-13】寫入 300KB → 拒絕（受控例外，不寫入 state）。"""
    with pytest.raises(ScriptLimitExceeded):
        _run("state['big'] = 'x' * (300 * 1024)")


def test_state_write_size_on_point_accepted():
    """邊界 on-point：序列化後恰好 256KB（字串長度 + 兩個引號）→ 放行。"""
    length = MAX_WRITE_BYTES - 2  # json.dumps 的兩個引號
    writes = _run(f"state['big'] = 'x' * {length}")

    assert len(writes["big"]) == length


def test_state_write_size_off_by_one_rejected():
    """邊界 off-point：多一個位元組就拒絕。"""
    with pytest.raises(ScriptLimitExceeded):
        _run(f"state['big'] = 'x' * {MAX_WRITE_BYTES - 1}")


# ---------------------------------------------------------------------------
# 決策表另一半：合法 script 正常運作 + 靜態契約正確
# ---------------------------------------------------------------------------


def test_legal_script_writes_declared_keys():
    """賦值、if、for、f-string、白名單 builtins：都在允許清單內 → 正常執行。"""
    source = (
        "values = sorted(state['scores'])\n"
        "top = max(values)\n"
        "if top > 0.5:\n"
        "    state['grade'] = 'high'\n"
        "else:\n"
        "    state['grade'] = 'low'\n"
        "state['note'] = f'共 {len(values)} 筆，最高 {top}'\n"
    )

    writes = _run(source, {"scores": [0.2, 0.9, 0.4]})

    assert writes == {"grade": "high", "note": "共 3 筆，最高 0.9"}


def test_scan_reports_read_write_keys_and_sha256():
    """靜態契約：讀寫鍵名清單供 trace／資料流檢查，sha256 供稽核（規格 §5.3）。"""
    contract = scan("state['b'] = state['a'] + 1")

    assert contract.reads == ("a",)
    assert contract.writes == ("b",)
    assert len(contract.sha256) == 64


def test_script_cannot_reach_out_of_state_by_mutating_nested_objects():
    """深拷貝：script 就地改 state 內的物件，不得穿透回真正的 state。"""
    state = {"items": [1, 2, 3]}

    writes = _run("state['items'][0] = 999\nstate['ok'] = True", state)

    assert state["items"] == [1, 2, 3]  # 原 state 未被就地改寫
    assert writes == {"ok": True}  # items 沒被宣告寫入 → 不進 state


def test_missing_key_raises_controlled_error():
    """讀不存在的鍵 → KeyError（受控例外 → Harness 轉 fatal），不是靜默 None。"""
    with pytest.raises(KeyError):
        _run("state['x'] = state['no_such_key']")


def test_key_existence_check_is_available():
    """存在性檢查用 `in`（`state.get` 屬於被禁的屬性存取）。"""
    writes = _run("state['has'] = 'a' in state", {"a": 1})

    assert writes == {"has": True}


def test_callable_in_state_cannot_be_reached():
    """防禦縱深(L2)：白名單禁止呼叫區域變數，但 sorted(seq, key=f) 會在內部呼叫 callable。

    從源頭斷 —— StateView.__getitem__ 對 callable 值直接 raise，比在 call() 逐一擋
    key= 引數簡單。目前不可達（現行節點寫回 state 的都是 dict/pydantic、無 callable），
    此測試釘住「未來有節點把 callable 放進 state 也不會活」。
    """
    called: list[int] = []

    def sneaky(x):  # 若被呼叫就留下痕跡
        called.append(x)
        return x

    with pytest.raises(ScriptViolation):
        _run("f = state['fn']\nstate['out'] = sorted([3, 1], key=f)", {"fn": sneaky})

    assert called == []  # callable 根本沒被取到，遑論呼叫
