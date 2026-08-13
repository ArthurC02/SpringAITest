"""Phase S1：Skill script 的行程級隔離（`ISOLATED_SKILL_SCRIPTS_ENABLED`）。

計畫 §8 的驗收順序就是本檔的順序：

1. **首要 —— 資源隔離**：CPU 與記憶體炸彈被 OS 擋下，且 worker 行程照常服務下一個請求。
   in-process path 對這兩者是誠實的空白（見 test_script_runner_sandbox 的 honest-gap
   測試），所以這裡是整個 S1 唯一不能打折的部分。
2. **Behavior parity**：既有 valid script corpus 在兩條 adapter 上輸出同一份 state。
   逐鍵比對，不放寬斷言。
3. **縱深防禦**：child 看不到 secrets/env、逾時不留 orphan、malformed IPC fail closed、
   超大輸出被拒、child 說謊也拿不到未宣告的鍵。

旗標關閉時的行為由既有測試檔（test_script_runner_sandbox / test_engine_script_steps）
原封不動地守著 —— 本檔不碰它們。
"""

import asyncio
import io
import json
import os
import subprocess
import sys

import pytest

from app.engine import script_isolation
from app.engine.script_isolation import (
    ALLOWED_TOOL_RISKS,
    MAX_IPC_BYTES,
    MAX_TOOL_CALLS,
    IsolatedSubprocessRunner,
    accept_writes,
    child_env,
    decode_message,
)
from app.engine.script_runner import (
    DisabledScriptRunner,
    MAX_ITERATIONS,
    MAX_WRITE_BYTES,
    RestrictedInProcessRunner,
    ScriptExecutionDisabled,
    ScriptError,
    ScriptLimitExceeded,
    ScriptLimits,
    ScriptTimeout,
    ScriptViolation,
    scan,
)

# import 觸發 tool 註冊（broker 的 risk 判斷查的是這張表）
import app.tools as _tools  # noqa: F401


def _run(runner, source: str, state: dict | None = None, tools=None, **limits):
    return asyncio.run(runner.run(source, state or {}, tools, ScriptLimits(**limits)))


def _isolated(source: str, state: dict | None = None, tools=None, **limits):
    return _run(IsolatedSubprocessRunner(), source, state, tools, **limits)


class FakeToolBag:
    """parent 端的 tool 代打對象；比照 ToolBag 的同步 call 簽名（專案慣例：手寫 fake）。"""

    def __init__(self, result=None, error: Exception | None = None):
        self.result = result if result is not None else {"value": 2}
        self.error = error
        self.calls: list[tuple[str, dict]] = []

    def call(self, name: str, **args):
        self.calls.append((name, args))
        if self.error is not None:
            raise self.error
        return self.result


# ---------------------------------------------------------------------------
# 1. 首要驗收：CPU / memory 炸彈被 OS 擋下，worker 繼續服務
# ---------------------------------------------------------------------------


def _process_is_alive(pid: int) -> bool:
    """pid 是否還活著（不依賴 psutil；只用 stdlib + ctypes）。"""
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.restype = wintypes.HANDLE
        handle = kernel32.OpenProcess(0x1000, False, pid)  # QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        code = wintypes.DWORD()
        ok = kernel32.GetExitCodeProcess(handle, ctypes.byref(code))
        kernel32.CloseHandle(handle)
        return bool(ok) and code.value == 259  # STILL_ACTIVE
    try:
        os.kill(pid, 0)
    except (ProcessLookupError, PermissionError):
        return False
    return True


def test_cpu_bomb_is_killed_and_worker_keeps_serving():
    """協作式檢查點碰不到的 CPU 炸彈（單一大整數次方）→ wall clock 強制終止整組行程。

    in-process path 對這條只能「讓步驟出場」，那條 worker thread 會繼續燒 CPU 到跑完
    （§5.0 明載）。隔離 path 是真的把行程殺掉，而且是**整組**：last_child_pid 是實際
    跑 script 的行程（venv 上的 Popen.pid 只是 trampoline），逾時後它必須已經不在。
    """
    runner = IsolatedSubprocessRunner()

    with pytest.raises(ScriptTimeout):
        _run(runner, "state['x'] = 3 ** 10 ** 7", timeout_ms=200)

    assert runner.last_process.returncode is not None  # 直接子行程已收屍
    assert not _process_is_alive(runner.last_child_pid)  # 孫行程沒有變成 orphan
    # 同一個 runner 立刻服務下一個請求：worker 沒有被炸彈拖垮
    assert _run(runner, "state['ok'] = True") == {"ok": True}


def test_memory_bomb_is_refused_by_os_limit_and_worker_keeps_serving():
    """序列加法倍增 40 圈（2^40 元素）→ OS 位址空間/Job 記憶體上限先擋下來。

    迭代上限只界定圈數（40 遠低於 10000）、256KB 只管寫回，所以 in-process path 對這條
    完全沒有護欄；這裡驗的是 OS 上限接手，且 parent 的記憶體不受影響。
    """
    runner = IsolatedSubprocessRunner()
    source = "xs = [0]\nfor i in range(40):\n    xs = xs + xs\nstate['n'] = len(xs)"

    with pytest.raises(ScriptError) as exc:
        _run(runner, source, timeout_ms=9000)

    assert type(exc.value).__name__ == "MemoryError"  # 配置被 OS 拒絕，不是靜默成功
    assert runner.last_process.returncode is not None
    assert not _process_is_alive(runner.last_child_pid)
    assert _run(runner, "state['ok'] = True") == {"ok": True}


def test_cpu_bomb_inside_loop_still_hits_the_cooperative_budget():
    """決策表另一半：有 for 迴圈的 CPU 炸彈仍由 _Budget 的協作式檢查點先擋（行為與 in-process 同）。

    圈數刻意壓在迭代上限（10000）以下、每圈的運算刻意做大 —— 讓「先出局的是逾時
    檢查點而不是迭代計數」是確定的，不是兩個護欄在賽跑。
    """
    with pytest.raises(ScriptTimeout):
        _isolated("x = 0\nfor i in range(9000):\n    x = 3 ** 10 ** 5", timeout_ms=1)


# ---------------------------------------------------------------------------
# 2. Behavior parity：同一份 corpus，兩條 adapter 逐鍵相同
# ---------------------------------------------------------------------------

PARITY_CORPUS = [
    (
        "assign-if-fstring",
        "values = sorted(state['scores'])\n"
        "top = max(values)\n"
        "if top > 0.5:\n"
        "    state['grade'] = 'high'\n"
        "else:\n"
        "    state['grade'] = 'low'\n"
        "state['note'] = f'共 {len(values)} 筆，最高 {top}'\n",
        {"scores": [0.2, 0.9, 0.4]},
    ),
    ("loop-accumulate", "t = 0\nfor i in range(100):\n    t = t + i\nstate['t'] = t", {}),
    ("membership", "state['has'] = 'a' in state", {"a": 1}),
    ("nested-read", "state['first'] = state['items'][0]", {"items": [7, 8, 9]}),
    ("dict-build", "state['d'] = {'a': 1, 'b': [1, 2]}", {}),
    ("arithmetic", "state['x'] = 2 ** state['n']", {"n": 10}),
    ("string-mul", "state['s'] = 'ab' * 3", {}),
    ("range-len", "state['n'] = len(range(1000))", {}),
    (
        "iteration-on-point",
        f"total = 0\nfor i in range({MAX_ITERATIONS}):\n    total = total + 1\n"
        "state['total'] = total",
        {},
    ),
    # 保留鍵：兩條路都必須剝除，且 script 其餘寫入照常落地
    ("reserved-key-stripped", "state['tenant_id'] = 'other'\nstate['ran'] = True", {"tenant_id": "t-1"}),
    # 未宣告寫入（就地改巢狀物件）：兩條路都不得穿透
    ("nested-mutation", "state['items'][0] = 999\nstate['ok'] = True", {"items": [1, 2, 3]}),
]


@pytest.mark.parametrize(
    "source,state", [(s, st) for _, s, st in PARITY_CORPUS], ids=[i for i, _, _ in PARITY_CORPUS]
)
def test_behavior_parity_between_adapters(source, state):
    """既有 valid script 在隔離 adapter 下輸出**完全相同**的 state 寫入。"""
    in_process = _run(RestrictedInProcessRunner(), source, dict(state), timeout_ms=10000)
    isolated = _isolated(source, dict(state), timeout_ms=10000)

    assert isolated == in_process


def test_parity_of_static_contract_and_trace_material():
    """trace 的素材（sha256 + 讀寫鍵名）由 parent 的靜態掃描產生 → 兩條路必然一致。"""
    source = "state['b'] = state['a'] + 1"
    contract = scan(source)

    assert _isolated(source, {"a": 1}) == {"b": 2}
    assert contract.reads == ("a",) and contract.writes == ("b",)


@pytest.mark.parametrize(
    "source",
    ["import os", "state.__class__", "while True:\n    pass", "xs = [i for i in range(3)]"],
)
def test_whitelist_violations_are_rejected_before_any_process_is_spawned(source):
    """AST 白名單仍是第一道 guard（計畫 §2.1）：違規連子行程都不會啟動。"""
    runner = IsolatedSubprocessRunner()

    with pytest.raises(ScriptViolation):
        _run(runner, source)

    assert runner.last_process is None


def test_runtime_exception_keeps_its_error_code():
    """執行期例外的類別名要能穿過 IPC 回來 —— Harness 的 error_code 取的就是它。"""
    with pytest.raises(ScriptError) as exc:
        _isolated("state['x'] = 1 / 0")

    assert type(exc.value).__name__ == "ZeroDivisionError"


def test_missing_key_raises_controlled_error():
    """讀不存在的鍵：兩條路都是受控例外（Harness 轉 fatal），不是靜默 None。"""
    with pytest.raises(ScriptError) as exc:
        _isolated("state['x'] = state['no_such_key']")

    assert type(exc.value).__name__ == "KeyError"


# ---------------------------------------------------------------------------
# 3a. 縱深防禦：child 環境從零重建
# ---------------------------------------------------------------------------

SECRET_ENVS = {
    "INTERNAL_API_TOKEN": "super-secret-token",
    "CHECKPOINT_DATABASE_URL": "postgresql://u:p@db/x",
    "JWT_SECRET": "jwt-secret-value",
    "LLM_API_KEY": "sk-provider-key",
}


def test_child_process_sees_no_parent_environment(monkeypatch):
    """實際啟一個 child 印出自己的 os.environ：secrets 連名字都不該在。"""
    for name, value in SECRET_ENVS.items():
        monkeypatch.setenv(name, value)

    result = subprocess.run(
        [sys.executable, "-I", "-c", "import os, json; print(json.dumps(dict(os.environ)))"],
        env=child_env(),
        capture_output=True,
        text=True,
        check=True,
    )
    child = json.loads(result.stdout)

    # Windows 留 SYSTEMROOT（啟動不了就沒得談）；POSIX 留 LC_ALL（擋 CPython 自己的
    # legacy locale coercion，見 child_env() 註解）。兩者都不是 secret。
    allowed = {"SYSTEMROOT"} if sys.platform == "win32" else {"LC_ALL"}
    assert set(child) <= allowed
    for name, value in SECRET_ENVS.items():
        assert name not in child
        assert value not in json.dumps(child)


def test_child_env_is_rebuilt_not_filtered(monkeypatch):
    """不是黑名單過濾而是從零重建：parent 多一個新變數，child 也不會突然看得到。"""
    monkeypatch.setenv("SOME_FUTURE_SECRET", "leaked")

    assert "SOME_FUTURE_SECRET" not in child_env()


# ---------------------------------------------------------------------------
# 3b. 縱深防禦：fail closed
# ---------------------------------------------------------------------------


def test_script_is_disabled_when_os_limits_are_unavailable(monkeypatch):
    """平台給不出可信 OS 上限 → 直接拒絕執行，不回退成「看似 sandbox」（計畫 §2.2）。"""
    runner = IsolatedSubprocessRunner()
    monkeypatch.setattr(script_isolation, "os_limits_available", lambda: False)

    with pytest.raises(ScriptError) as exc:
        _run(runner, "state['x'] = 1")

    assert "OS 資源上限" in str(exc.value)
    assert runner.last_process is None


@pytest.mark.parametrize(
    "line",
    [
        b"",  # child 沒回應就結束（EOF）
        b"not json at all\n",
        b"[1, 2, 3]\n",  # 合法 JSON 但不是訊息物件
        b'{"writes": {}}\n',  # 缺 op
        b'{"op": 7}\n',  # op 型別錯
    ],
    ids=["eof", "garbage", "not-object", "missing-op", "op-wrong-type"],
)
def test_malformed_ipc_fails_closed(line):
    """child → parent 一律當不可信輸入：解不開就是受控錯誤，不是例外穿出去或靜默通過。"""
    with pytest.raises(ScriptError):
        decode_message(line)


def test_wellformed_ipc_decodes():
    """決策表另一半：格式正確的訊息要真的解得開。"""
    assert decode_message(b'{"op": "done", "writes": {"x": 1}}\n') == {
        "op": "done",
        "writes": {"x": 1},
    }


@pytest.mark.parametrize(
    "line",
    [
        b'{"op": "hello", "pid": 1}\n',  # 解得開但不是 ready
        b'{"op": "ready", "pid": "1"}\n',  # pid 不是整數（child 說謊或版本不合）
        b'{"op": "ready"}\n',  # 根本沒回報 pid
    ],
    ids=["op-not-ready", "pid-not-int", "pid-missing"],
)
def test_bad_startup_handshake_fails_closed_and_still_reaps_the_child(monkeypatch, line):
    """握手不合格 → 連 job 都不送出，且子行程照樣收屍（孤兒 pid 追蹤不到就不能放過它）。

    真實的 child 只會送出合格的握手，所以這條 fail-closed 分支只能靠替換讀取層來驗。
    """
    runner = IsolatedSubprocessRunner()
    monkeypatch.setattr(script_isolation, "_read_line", lambda stream: line)

    with pytest.raises(ScriptError) as exc:
        _run(runner, "state['x'] = 1")

    assert "握手" in str(exc.value)
    assert runner.last_child_pid is None  # 沒有可信的 pid 就不記
    assert runner.last_process.returncode is not None  # 但行程一定收乾淨


def test_ipc_read_size_boundary_is_at_max_ipc_bytes():
    """讀取層的 1MB 上限：恰好 MAX_IPC_BYTES 讀得回來，多一個位元組就出局。

    上限擋在 readline 這一層，child 才沒機會用一條無限長的行灌爆 parent 的記憶體。
    """
    on_point = b"x" * (MAX_IPC_BYTES - 1) + b"\n"  # 含換行恰好 MAX_IPC_BYTES

    assert script_isolation._read_line(io.BytesIO(on_point)) == on_point

    with pytest.raises(ScriptLimitExceeded):
        script_isolation._read_line(io.BytesIO(b"x" * MAX_IPC_BYTES + b"\n"))


def test_oversized_input_state_projection_is_rejected_before_any_process_is_spawned():
    """送出方向的同一個上限：state 投影超過 1MB → 受控例外，連子行程都不啟動。

    決策表另一半（正常大小的投影照常送得出去）是本檔其餘每一個 `_isolated` 測試。
    """
    runner = IsolatedSubprocessRunner()

    with pytest.raises(ScriptLimitExceeded) as exc:
        _run(runner, "state['n'] = len(state['blob'])", {"blob": "x" * MAX_IPC_BYTES})

    assert "IPC 上限" in str(exc.value)
    assert runner.last_process is None


# ---------------------------------------------------------------------------
# 3c. 縱深防禦：parent 端重新過契約（child 說謊拿不到好處）
# ---------------------------------------------------------------------------


def _contract(source: str):
    return scan(source)


def test_lying_child_cannot_write_undeclared_or_reserved_keys():
    """child 回傳未宣告 / 保留 / __ 前綴的鍵：parent 端一律剝除。"""
    contract = _contract("state['ok'] = 1")
    payload = {
        "ok": 1,
        "sneaky": "未宣告",
        "tenant_id": "other-tenant",
        "fatal_error": "forged",
        "retrieval_top_k": 99,
        "__loop_0_count": 0,
    }

    assert accept_writes(payload, contract, ScriptLimits()) == {"ok": 1}


def test_lying_child_cannot_exceed_write_size_cap():
    """256KB 上限在 parent 端重驗：child 的自我檢查不算數。"""
    contract = _contract("state['big'] = 1")

    with pytest.raises(ScriptLimitExceeded):
        accept_writes({"big": "x" * MAX_WRITE_BYTES}, contract, ScriptLimits())


def test_parent_write_size_boundary_is_on_the_same_point_as_in_process():
    """邊界 on/off-point 與 in-process 同一個點（序列化後 256KB 含 json 的兩個引號）。"""
    contract = _contract("state['big'] = 1")

    on_point = {"big": "x" * (MAX_WRITE_BYTES - 2)}
    assert accept_writes(on_point, contract, ScriptLimits()) == on_point

    with pytest.raises(ScriptLimitExceeded):
        accept_writes({"big": "x" * (MAX_WRITE_BYTES - 1)}, contract, ScriptLimits())


def test_malformed_writes_payload_fails_closed():
    with pytest.raises(ScriptError):
        accept_writes(["not", "a", "dict"], _contract("state['ok'] = 1"), ScriptLimits())


def test_oversized_state_write_is_rejected_end_to_end():
    """走完整條 IPC：script 寫 300KB → 受控例外，一個鍵都不落。"""
    with pytest.raises(ScriptLimitExceeded):
        _isolated("state['big'] = 'x' * (300 * 1024)")


@pytest.mark.parametrize(
    "forged",
    ["not an identifier", "os.system", "E" * 65, 7, None],
    ids=["spaces", "dotted", "too-long-65", "not-str", "missing"],
)
def test_lying_child_cannot_forge_an_arbitrary_error_code(forged):
    """trace 的 error_code 取自例外類別名 → 只收合法識別字且 ≤64 字元，其餘退回 ScriptError。"""
    error = script_isolation._remote_error({"type": forged, "message": "boom"})

    assert type(error) is ScriptError  # 不是以 child 給的字串動態命名的子類別
    assert str(error) == "boom"


def test_child_error_type_length_boundary_still_names_the_exception():
    """邊界另一半：恰好 64 字元的合法識別字仍還原成同名例外（ScriptError 的子類別）。"""
    error = script_isolation._remote_error({"type": "E" * 64, "message": "boom"})

    assert type(error).__name__ == "E" * 64
    assert isinstance(error, ScriptError)


# ---------------------------------------------------------------------------
# 3d. 縱深防禦：tool broker（child 不持憑證，parent 重新驗證）
# ---------------------------------------------------------------------------


def test_tool_call_round_trips_through_the_parent_broker():
    """決策表另一半：允許的 tool 照常打得通，結果回得到 script。"""
    tools = FakeToolBag(result={"value": 42})

    writes = _isolated(
        "state['out'] = tools.call('local.calculator', expression='1+1', inputs={})",
        tools=tools,
        timeout_ms=10000,
    )

    assert writes == {"out": {"value": 42}}
    assert tools.calls == [("local.calculator", {"expression": "1+1", "inputs": {}})]


def test_write_risk_tool_is_denied_and_never_invoked():
    """write tools 預設禁止（計畫 §2.3）：broker 在代打之前就拒絕，不得真的發出呼叫。"""
    tools = FakeToolBag()

    with pytest.raises(ScriptError) as exc:
        _isolated(
            "state['out'] = tools.call('runtime.write_evidence', record_id='r', value='v')",
            tools=tools,
            timeout_ms=10000,
        )

    assert type(exc.value).__name__ == "ToolNotAllowed"
    assert tools.calls == []  # 連一次都沒打出去


def test_allowed_tool_risks_are_a_whitelist():
    """釘住 allowlist 本身：新增 risk 類別預設就是拒絕，不必回頭補黑名單。"""
    assert ALLOWED_TOOL_RISKS == frozenset({"low", "read"})


@pytest.mark.parametrize(
    "message",
    [
        {"op": "tool", "name": 7, "args": {}},
        {"op": "tool", "name": "local.calculator"},  # 沒有 args
        {"op": "tool", "name": "local.calculator", "args": ["expression", "1+1"]},
    ],
    ids=["name-not-str", "args-missing", "args-not-dict"],
)
def test_malformed_tool_request_is_denied_before_the_registry_is_consulted(message):
    """child 的 tool 請求形狀不對 → broker 直接回拒，不查表也不代打（fail closed）。"""
    tools = FakeToolBag()

    reply = IsolatedSubprocessRunner()._broker(message, tools, 1)

    assert reply == {
        "op": "tool_result",
        "ok": False,
        "type": "ToolError",
        "error": "tool 請求格式不正確",
    }
    assert tools.calls == []


def test_tool_result_ipc_size_boundary_is_at_max_ipc_bytes():
    """代打結果吃的是同一個 IPC 上限：序列化後恰好 1MB 送得回去，多一個位元組被擋。"""
    request = {"op": "tool", "name": "local.calculator", "args": {}}
    runner = IsolatedSubprocessRunner()

    on_point = "x" * (MAX_IPC_BYTES - 2)  # 加上 json 的兩個引號恰好 MAX_IPC_BYTES
    assert runner._broker(request, FakeToolBag(result=on_point), 1) == {
        "op": "tool_result",
        "ok": True,
        "result": on_point,
    }

    denied = runner._broker(request, FakeToolBag(result="x" * (MAX_IPC_BYTES - 1)), 1)
    assert denied["ok"] is False and denied["type"] == "ScriptLimitExceeded"


def test_unknown_tool_is_rejected_by_the_broker():
    tools = FakeToolBag()

    with pytest.raises(ScriptError) as exc:
        _isolated("state['out'] = tools.call('no.such.tool')", tools=tools, timeout_ms=10000)

    assert type(exc.value).__name__ == "UnknownTool"
    assert tools.calls == []


def _tool_loop(count: int) -> str:
    return (
        f"for i in range({count}):\n"
        "    state['out'] = tools.call('local.calculator', expression='1+1', inputs={})"
    )


def test_tool_call_count_on_point_passes():
    """邊界 on-point：恰好 16 次呼叫跑得完。"""
    tools = FakeToolBag()

    writes = _isolated(_tool_loop(MAX_TOOL_CALLS), tools=tools, timeout_ms=10000)

    assert writes == {"out": {"value": 2}}
    assert len(tools.calls) == MAX_TOOL_CALLS


def test_tool_call_count_off_point_rejected():
    """邊界 off-point：第 17 次被 parent 擋下，且那一次沒有真的代打。"""
    tools = FakeToolBag()

    with pytest.raises(ScriptError):
        _isolated(_tool_loop(MAX_TOOL_CALLS + 1), tools=tools, timeout_ms=10000)

    assert len(tools.calls) == MAX_TOOL_CALLS


def test_tool_error_reaches_the_script_as_a_controlled_failure():
    """tool 自己失敗時錯誤類別要傳得回來（trace 的 error_code 取的就是它）。"""
    tools = FakeToolBag(error=RuntimeError("backend 掛了"))

    with pytest.raises(ScriptError) as exc:
        _isolated(
            "state['out'] = tools.call('local.calculator', expression='1+1', inputs={})",
            tools=tools,
            timeout_ms=10000,
        )

    assert type(exc.value).__name__ == "RuntimeError"


# ---------------------------------------------------------------------------
# 3e. 縱深防禦：收屍失敗不得蓋過正在傳播的原始例外
# ---------------------------------------------------------------------------


def test_cleanup_failure_does_not_mask_the_original_script_error(monkeypatch):
    """`_terminate` 收屍失敗(例如 `_kill` 沒殺乾淨、`proc.wait` 逾時炸出
    `TimeoutExpired`)不得蓋過正在傳播的原始例外——Harness 的 error_code 取的是
    ZeroDivisionError,不是收尾時的清理錯誤。"""
    runner = IsolatedSubprocessRunner()
    original_terminate = IsolatedSubprocessRunner._terminate

    def _terminate_then_raise(self, proc, job):
        original_terminate(self, proc, job)  # 仍然真的收屍，只是之後炸一個清理例外
        raise subprocess.TimeoutExpired(cmd="child", timeout=1)

    monkeypatch.setattr(IsolatedSubprocessRunner, "_terminate", _terminate_then_raise)

    with pytest.raises(ScriptError) as exc:
        _run(runner, "state['x'] = 1 / 0")

    assert type(exc.value).__name__ == "ZeroDivisionError"


def test_cleanup_happy_path_still_returns_normally_after_move_to_thread():
    """決策表另一半：收屍正常時（多數情況）結果照常回傳，搬進 to_thread 不改變行為。"""
    assert _isolated("state['ok'] = True") == {"ok": True}


# ---------------------------------------------------------------------------
# 2b. Behavior parity（Harness 層）：trace 契約與治理硬規則不因換 adapter 而改變
# ---------------------------------------------------------------------------


def _compiled_run(flow: list[dict], state: dict | None = None, input_schema=None):
    """把 flow 用隔離 adapter 編譯執行，回傳 (public state, deps)。"""
    from types import SimpleNamespace

    from app.engine import compiler
    from app.engine.skill import Skill
    from app.nodes.kbquery import nodes as _nodes  # noqa: F401  觸發節點註冊
    from app.nodes.kbquery.adapters import StaticGlossary
    from tests.kbquery_fakes import RecordingAuditRepo

    deps = SimpleNamespace(
        audit_repo=RecordingAuditRepo(),
        llm=None,
        glossary=StaticGlossary(),
        max_retrieval_attempts=2,
        script_runner=IsolatedSubprocessRunner(),
    )
    skill = Skill.model_validate(
        {"name": "probe-skill", "input_schema": input_schema or {}, "flow": flow}
    )
    graph = compiler.compile(skill, deps)
    result = compiler.public_output(
        asyncio.run(graph.ainvoke({"tenant_id": "t-test", **(state or {})}))
    )
    return result, deps


def _script_entry(result: dict):
    from app.engine.script_runner import ScriptTraceEntry

    entries = [e for e in result["trace"] if isinstance(e, ScriptTraceEntry)]
    assert len(entries) == 1
    return entries[0]


def test_trace_contract_is_unchanged_under_isolation():
    """sha256 + 讀寫鍵名 + 狀態照舊入 trace，原始碼全文照舊不落（AT3-14 的隔離版）。"""
    import hashlib

    sentinel = "SCRIPT_SOURCE_SENTINEL"
    source = f"state['note'] = '{sentinel}' + str(state['seed'])"
    digest = hashlib.sha256(source.encode("utf-8")).hexdigest()

    result, deps = _compiled_run(
        [{"script": source}], {"seed": 1}, input_schema={"seed": {"type": "int"}}
    )

    entry = _script_entry(result)
    assert entry.script_sha256 == digest
    assert entry.input_summary == "seed"
    assert entry.output_summary == "note"
    assert entry.status == "ok"
    assert result["note"] == f"{sentinel}1"

    trace_json = "".join(e.model_dump_json() for e in result["trace"])
    assert sentinel not in trace_json and "state[" not in trace_json
    assert sentinel not in deps.audit_repo.saved[0].model_dump_json()


def test_timeout_still_triggers_the_fatal_short_circuit():
    """AT3-12 的隔離版：逾時 → 步驟錯誤、後續節點短路、稽核節點照樣落地。"""
    result, deps = _compiled_run(
        [
            {"script": "state['x'] = 3 ** 10 ** 7", "timeout_ms": 200},
            {"node": "query_intake"},
        ],
        {"query": "測試"},
    )

    entry = _script_entry(result)
    assert entry.status == "error"
    assert "Timeout" in entry.error_code
    assert "fatal_error" in result
    assert result["trace"][-1].node_name == "audit_feedback"
    assert len(deps.audit_repo.saved) == 1


def test_reserved_keys_stay_unforgeable_under_isolation():
    """AT-GOV-03 的隔離版：保留鍵被剝除，script 其餘寫入照常落地。"""
    result, _ = _compiled_run(
        [{"script": "state['tenant_id'] = 'other'\nstate['ran'] = True"}]
    )

    assert result["tenant_id"] == "t-test"
    assert result["ran"] is True


# ---------------------------------------------------------------------------
# 旗標接線：off = 既有 in-process path，on = 隔離 adapter
# ---------------------------------------------------------------------------


def test_development_flag_off_leaves_the_in_process_path_in_place(monkeypatch):
    from app.engine.compiler import _Builder
    from app.engine.script_runner import RestrictedInProcessRunner as InProcess

    monkeypatch.setattr("app.settings.settings.app_environment", "development")
    monkeypatch.setattr("app.settings.settings.isolated_skill_scripts_enabled", False)
    from app.skills.deps import _default_deps

    deps = _default_deps()
    assert isinstance(deps.script_runner, InProcess)
    assert _Builder(graph=None, deps=deps, allowed_tools=set()).runner is deps.script_runner


def test_production_without_isolation_fails_closed_instead_of_using_in_process(monkeypatch):
    from app.engine.compiler import _Builder

    monkeypatch.setattr("app.settings.settings.app_environment", "production")
    monkeypatch.setattr("app.settings.settings.isolated_skill_scripts_enabled", False)
    from app.skills.deps import _default_deps

    deps = _default_deps()
    assert isinstance(deps.script_runner, DisabledScriptRunner)
    assert _Builder(graph=None, deps=deps, allowed_tools=set()).runner is deps.script_runner
    with pytest.raises(ScriptExecutionDisabled):
        _run(deps.script_runner, "state['x'] = 1")


def test_flag_on_injects_the_isolated_adapter(monkeypatch):
    from app.engine.compiler import _Builder

    monkeypatch.setattr("app.settings.settings.isolated_skill_scripts_enabled", True)
    from app.skills.deps import _default_deps

    deps = _default_deps()
    assert isinstance(deps.script_runner, IsolatedSubprocessRunner)
    assert (
        _Builder(graph=None, deps=deps, allowed_tools=set()).runner is deps.script_runner
    )


def test_os_limits_are_available_on_this_platform():
    """本機（開發/CI）必須提供可信 OS 上限，否則上面的炸彈測試根本沒在測隔離。"""
    assert script_isolation.os_limits_available() is True
