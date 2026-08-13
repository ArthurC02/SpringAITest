"""Skill script 的行程級隔離 adapter（Phase S1，旗標 ISOLATED_SKILL_SCRIPTS_ENABLED）。

## 為什麼要有這一層

`script_runner` 的 in-process path 誠實記載了它擋不住的東西：CPU 與記憶體炸彈沒有
process boundary 可以攔，密鑰隔離仰賴 AST 白名單一個洞都沒有。這裡把 execution
boundary 換成短生命週期子行程，讓「擋得住」由 OS 保證，而不是由白名單無漏洞這個假設
保證。白名單、預算、保留鍵剝除、寫入上限**不重寫一份**——子行程呼叫的是
`script_runner.execute_sync`，跟 in-process path 同一份實作。

## 信任方向

parent → child 是可信的（parent 是權威）；child → parent 一律當成不可信輸入：

- 每則訊息都是 JSON 一行，`readline(MAX_IPC_BYTES + 1)` 直接把過長的行擋在讀取層。
- tool 呼叫由 parent 重新驗證：Skill 白名單（`ToolBag.call`，與 in-process path 同一條）、
  risk（只放行 low/read，write 一律拒絕）、次數上限、結果序列化大小。child 手上沒有
  憑證，只能請 parent 代打。
- 回傳的 writes 在 parent 端**再過一次**契約：靜態 writes 子集 ∩ 非保留鍵 ∩ 非 `__`
  前綴 ∩ 總大小上限。child 就算說謊也拿不到多的鍵。
- 錯誤類別名只接受合法識別字，避免 child 往 trace 的 error_code 塞任意字串。

## 為什麼是 blocking `Popen` + worker thread，不是 asyncio subprocess

`app/runtime/checkpoints.py` 在 Windows 匯入期就把事件圈政策換成 Selector（psycopg
的硬需求），而 Windows 的 Selector loop **不支援子行程**（`NotImplementedError`）。
所以 IPC 走同步 pipe 跑在 worker thread 上，逾時由事件圈的 `call_later` watchdog 殺
行程來收尾（pipe 一關，阻塞中的 readline 立刻拿到 EOF）。附帶好處是 tool 代打可以
直接用既有的 `ToolBag.call`（它本來就是設計給 worker thread 呼叫的），不必為隔離
adapter 另開一條 async 版本。

## OS 上限與 fail closed

POSIX 走 `resource.setrlimit`（child 自己設，見 script_child）+ `start_new_session`
讓逾時能整組 `killpg`；Windows 沒有 setrlimit，改用 Job Object：ProcessMemoryLimit +
ActiveProcessLimit + KILL_ON_JOB_CLOSE，關掉 handle 就整棵樹一起收。

`os_limits_available()` 為 False 的平台 —— 沒有 `resource` 也建不出 Job —— `run()` 直接
拒絕執行 script，不回退成「看似 sandbox」的 in-process 執行（計畫 §2.2）。

## 已知差異（相對 in-process path）

- state 投影與 tool 結果都走 JSON（`default=str`）：pydantic 物件會變成字串。白名單
  允許的運算對非 JSON 值本來就做不了什麼（不能取屬性、不能呼叫），這是刻意的收斂。
- Windows 的 CPU 上限靠 parent 的 wall-clock 強制終止，不是 `RLIMIT_CPU`；記憶體與
  行程數則由 Job Object 提供，與 POSIX 等價。
- 每個 script 步驟要付一次直譯器啟動 + 匯入的成本（本機約 300ms）。這段不計入
  script 自己的 `timeout_ms`（deadline 從送出 job 才起算）。
"""

import asyncio
import json
import logging
import os
import subprocess
import sys
from pathlib import Path
from typing import Any

from app.engine import tool_registry
from app.engine.script_runner import (
    FORBIDDEN_WRITE_KEYS,
    ScriptContract,
    ScriptError,
    ScriptLimitExceeded,
    ScriptLimits,
    ScriptTimeout,
    ScriptViolation,
    _value_size,
    scan,
)

logger = logging.getLogger(__name__)

# 子行程的位址空間上限。基準線（直譯器 + pydantic）約 60MB，`sorted(range(10**6))`
# 這種白名單允許的大配置約 40MB —— 512MB 讓正常 script 綽綽有餘，炸彈則很快撞牆。
CHILD_MEMORY_BYTES = 512 * 1024 * 1024
# 單一 script 步驟可發出的 tool 呼叫次數上限（parent 端重新計數，child 說的不算）。
MAX_TOOL_CALLS = 16
# 單則 IPC 訊息上限（雙向）。>256KB 的寫回本來就會被 max_write_bytes 擋，這裡是防止
# 惡意 child 用一條無限長的行把 parent 的記憶體吃光。
MAX_IPC_BYTES = 1024 * 1024
# 子行程啟動 + 匯入的等候上限（不計入 script 自己的 timeout_ms）。
CHILD_START_TIMEOUT_S = 30.0
# wall-clock 強制終止的寬限：涵蓋 child 收尾與序列化，不放寬 script 自身的預算。
KILL_GRACE_S = 0.5
# script 可呼叫的 tool risk：write／privileged 一律拒絕（計畫 §2.3「write tools 預設禁止」）。
ALLOWED_TOOL_RISKS = frozenset({"low", "read"})

_CHILD_PATH = Path(__file__).with_name("script_child.py")
_REPO_ROOT = Path(__file__).resolve().parents[2]  # workflow/


# ---------------------------------------------------------------------------
# 子行程環境：從零重建，不繼承 parent 的任何一個變數
# ---------------------------------------------------------------------------


def child_env() -> dict[str, str]:
    """child 的環境變數表。空字典起手，只補平台**啟動不了就沒得談**的那幾個。

    `INTERNAL_API_TOKEN`、`CHECKPOINT_DATABASE_URL`、`JWT_SECRET`、`LLM_API_KEY` 這類
    一律不在（連名字都不出現）——不是靠黑名單過濾，是根本沒有繼承。
    """
    env: dict[str, str] = {}
    if sys.platform == "win32":
        # 空環境下 Winsock 初始化會失敗（_overlapped 匯入炸掉），asyncio 連載入都不行。
        system_root = os.environ.get("SYSTEMROOT")
        if system_root:
            env["SYSTEMROOT"] = system_root
    else:
        # 完全空的環境會讓 CPython 偵測到 legacy C locale，自己觸發 PEP 538 coercion
        # 並把 LC_CTYPE 寫回 os.environ（-I 也擋不住，這是 libc getenv 層級的行為）。
        # 自己先設好 LC_ALL，讓這個過程不會被觸發，環境內容才是決定性的。
        env["LC_ALL"] = "C.UTF-8"
    return env


# ---------------------------------------------------------------------------
# OS 上限：POSIX rlimits / Windows Job Object
# ---------------------------------------------------------------------------


def os_limits_available() -> bool:
    """本平台是否提供可信的 OS 資源上限；False → script 步驟 fail closed。"""
    if sys.platform == "win32":
        return _kernel32() is not None
    try:
        import resource  # noqa: F401
    except ImportError:  # pragma: no cover - 只有非 POSIX 非 Windows 會走到
        return False
    return True


def _kernel32() -> Any:
    if sys.platform != "win32":
        return None
    try:
        import ctypes
        from ctypes import wintypes
    except ImportError:  # pragma: no cover
        return None
    try:
        dll = ctypes.WinDLL("kernel32", use_last_error=True)
        # 預設 restype 是 c_int，64 位元的 HANDLE 會被截斷 —— 必須明講。
        dll.CreateJobObjectW.restype = wintypes.HANDLE
        dll.OpenProcess.restype = wintypes.HANDLE
        return dll
    except OSError:  # pragma: no cover
        return None


def _assign_job(pid: int, memory_bytes: int) -> Any:
    """把 child 綁進一個帶記憶體/行程數上限的 Job；回傳 handle（關掉即整棵樹終止）。"""
    import ctypes
    from ctypes import wintypes

    class _IoCounters(ctypes.Structure):
        _fields_ = [(name, ctypes.c_ulonglong) for name in (
            "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
            "ReadTransferCount", "WriteTransferCount", "OtherTransferCount",
        )]

    class _BasicLimits(ctypes.Structure):
        _fields_ = [
            ("PerProcessUserTimeLimit", ctypes.c_int64),
            ("PerJobUserTimeLimit", ctypes.c_int64),
            ("LimitFlags", wintypes.DWORD),
            ("MinimumWorkingSetSize", ctypes.c_size_t),
            ("MaximumWorkingSetSize", ctypes.c_size_t),
            ("ActiveProcessLimit", wintypes.DWORD),
            ("Affinity", ctypes.c_size_t),
            ("PriorityClass", wintypes.DWORD),
            ("SchedulingClass", wintypes.DWORD),
        ]

    class _ExtendedLimits(ctypes.Structure):
        _fields_ = [
            ("BasicLimitInformation", _BasicLimits),
            ("IoInfo", _IoCounters),
            ("ProcessMemoryLimit", ctypes.c_size_t),
            ("JobMemoryLimit", ctypes.c_size_t),
            ("PeakProcessMemoryUsed", ctypes.c_size_t),
            ("PeakJobMemoryUsed", ctypes.c_size_t),
        ]

    kernel32 = _kernel32()
    job = kernel32.CreateJobObjectW(None, None)
    if not job:
        raise ScriptError("無法建立 Job Object，script 步驟拒絕執行")

    info = _ExtendedLimits()
    info.BasicLimitInformation.LimitFlags = (
        0x00000100  # PROCESS_MEMORY
        | 0x00000200  # JOB_MEMORY
        | 0x00000008  # ACTIVE_PROCESS
        | 0x00002000  # KILL_ON_JOB_CLOSE
        | 0x00000400  # DIE_ON_UNHANDLED_EXCEPTION（不要跳 WER 對話框）
    )
    # 不能設 1：venv 的 python.exe 是 trampoline，它得再 CreateProcess 一次真正的直譯器
    # （設 1 會讓子行程連啟動都失敗）。留一點餘裕仍足以擋 fork bomb ——
    # 白名單本來就沒有任何生行程的語法，這條是縱深防禦。
    info.BasicLimitInformation.ActiveProcessLimit = 4
    info.ProcessMemoryLimit = memory_bytes
    info.JobMemoryLimit = memory_bytes
    if not kernel32.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info)):
        kernel32.CloseHandle(job)
        raise ScriptError("無法設定 Job Object 資源上限，script 步驟拒絕執行")

    handle = kernel32.OpenProcess(0x0100 | 0x0001, False, pid)  # SET_QUOTA | TERMINATE
    if not handle:
        kernel32.CloseHandle(job)
        raise ScriptError("無法取得子行程 handle，script 步驟拒絕執行")
    try:
        if not kernel32.AssignProcessToJobObject(job, handle):
            kernel32.CloseHandle(job)
            raise ScriptError("無法將子行程納入 Job Object，script 步驟拒絕執行")
    finally:
        kernel32.CloseHandle(handle)
    return job


# ---------------------------------------------------------------------------
# IPC 解碼（child → parent 一律當不可信輸入）
# ---------------------------------------------------------------------------


def decode_message(line: bytes) -> dict:
    """把 child 的一行輸出解成訊息；任何不合格式的東西一律 ScriptError（fail closed）。"""
    if not line:
        raise ScriptError("隔離子行程沒有回應就結束了")
    try:
        message = json.loads(line)
    except (json.JSONDecodeError, UnicodeDecodeError) as e:
        raise ScriptError(f"隔離子行程回傳的訊息無法解析: {e}") from e
    if not isinstance(message, dict) or not isinstance(message.get("op"), str):
        raise ScriptError("隔離子行程回傳的訊息格式不正確")
    return message


def _remote_error(message: dict) -> Exception:
    """把 child 回報的錯誤還原成同名例外：trace 的 error_code 與 in-process 一致。"""
    known = {
        cls.__name__: cls
        for cls in (ScriptViolation, ScriptTimeout, ScriptLimitExceeded, ScriptError)
    }
    name = message.get("type")
    text = str(message.get("message", ""))[:500]
    if not (isinstance(name, str) and name.isidentifier() and len(name) <= 64):
        return ScriptError(text or "隔離子行程回報了無法辨識的錯誤")
    cls = known.get(name)
    if cls is None:
        # script 寫不出自訂例外（白名單沒有 raise/class/def），名字必然來自 CPython 內建
        cls = type(name, (ScriptError,), {})
    return cls(text)


def accept_writes(
    payload: Any, contract: ScriptContract, limits: ScriptLimits
) -> dict:
    """child 回傳的寫入在 parent 端**重新**過契約：說謊也拿不到多的鍵。"""
    if not isinstance(payload, dict):
        raise ScriptError("隔離子行程回傳的 writes 格式不正確")
    writes = {
        key: value
        for key, value in payload.items()
        if isinstance(key, str)
        and key in contract.writes
        and key not in FORBIDDEN_WRITE_KEYS
        and not key.startswith("__")
    }
    total = sum(_value_size(v) for v in writes.values())
    if total > limits.max_write_bytes:
        raise ScriptLimitExceeded(
            f"script 寫入 state 的值總大小 {total} 位元組，"
            f"超過上限 {limits.max_write_bytes} 位元組"
        )
    return writes


def _read_line(stream: Any) -> bytes:
    """讀一行，長度上限就在讀取層 —— child 沒機會用一條無限長的行灌爆 parent。"""
    line = stream.readline(MAX_IPC_BYTES + 1)
    if len(line) > MAX_IPC_BYTES:
        raise ScriptLimitExceeded(
            f"隔離子行程的輸出超過 IPC 上限 {MAX_IPC_BYTES} 位元組"
        )
    return line


# ---------------------------------------------------------------------------
# Runner
# ---------------------------------------------------------------------------


class IsolatedSubprocessRunner:
    """Phase S1 的 `ScriptRunnerPort`：短生命週期子行程 + OS 級上限 + 空環境。"""

    def __init__(self, memory_bytes: int = CHILD_MEMORY_BYTES):
        self.memory_bytes = memory_bytes
        # ponytail: last_process / last_child_pid / last_stderr 是同一個 runner
        # 實例上的 per-run 欄位，不是每次呼叫各自隔離的回傳值——多個 run() 併發
        # 共用同一個 IsolatedSubprocessRunner 時彼此會互相覆蓋。僅供單執行緒測試
        # 斷言與部署期診斷用，並行下不保證對應「這一次」呼叫；要併發安全就得把
        # 這三個值改成 run() 的回傳值或每呼叫一個 runner 實例。
        # 最近一次的子行程；收工後 returncode 必為非 None（逾時也不留 orphan）。
        self.last_process: subprocess.Popen | None = None
        # 真正跑 script 的行程 pid（Popen.pid 在 venv 上是 trampoline，不是它）。
        self.last_child_pid: int | None = None
        self.last_stderr: str = ""  # child 的 stderr 摘要，供部署期診斷啟動失敗

    async def run(
        self, source: str, state_view: dict, tools: Any, limits: ScriptLimits
    ) -> dict:
        contract = scan(source)  # AST 白名單仍是第一道靜態 guard（計畫 §2.1）
        if not os_limits_available():
            raise ScriptError("本平台無法提供可信的 OS 資源上限，script 步驟拒絕執行")
        job_message = json.dumps(
            {
                "op": "run",
                "source": source,
                "sha256": contract.sha256,
                "state": state_view,
                "limits": {
                    "timeout_ms": limits.timeout_ms,
                    "max_ipc_bytes": MAX_IPC_BYTES,
                    "memory_bytes": self.memory_bytes,
                    # RLIMIT_CPU 只吃整秒；wall-clock 才是主要的逾時機制，這是 OS 後盾。
                    "cpu_seconds": max(1, -(-limits.timeout_ms // 1000)),
                },
            },
            ensure_ascii=False,
            default=str,
        ).encode("utf-8")
        if len(job_message) > MAX_IPC_BYTES:
            raise ScriptLimitExceeded(
                f"script 的輸入 state 投影超過 IPC 上限 {MAX_IPC_BYTES} 位元組"
            )

        proc = subprocess.Popen(
            [
                sys.executable,
                "-I",  # isolated：忽略 PYTHONPATH／PYTHONHOME／user site
                # IPC 兩端固定 UTF-8。-I 連帶忽略 PYTHONIOENCODING，所以只能用 -X；
                # 不設的話 Windows 的 stdout 會走 locale（cp950），中文訊息一送就壞。
                "-X",
                "utf8",
                str(_CHILD_PATH),
                str(_REPO_ROOT),
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=child_env(),
            # POSIX：自成 process group，逾時才殺得掉整組（含它生出來的東西）
            start_new_session=(sys.platform != "win32"),
        )
        self.last_process = proc
        self.last_child_pid = None
        self.last_stderr = ""
        job = None
        expired = ""  # 非空 ＝ watchdog 開過槍，字串就是要回報的原因

        def _expire(reason: str) -> None:
            nonlocal expired
            expired = reason
            _kill(proc)  # pipe 一斷，worker thread 的阻塞 readline 立刻拿到 EOF

        loop = asyncio.get_running_loop()
        timer = loop.call_later(
            CHILD_START_TIMEOUT_S,
            _expire,
            f"隔離子行程在 {CHILD_START_TIMEOUT_S} 秒內沒有啟動完成",
        )
        try:
            if sys.platform == "win32":
                # ponytail: spawn 與 assign 之間有極短的空窗（child 還在匯入、尚未讀
                # job），要完全消除得走 CREATE_SUSPENDED + ResumeThread。
                job = _assign_job(proc.pid, self.memory_bytes)
            ready = decode_message(await asyncio.to_thread(_read_line, proc.stdout))
            if ready.get("op") != "ready" or not isinstance(ready.get("pid"), int):
                raise ScriptError("隔離子行程沒有完成啟動握手")
            self.last_child_pid = ready["pid"]

            timer.cancel()
            # deadline 從送出 job 起算：子行程啟動與匯入不吃 script 自己的預算
            timer = loop.call_later(
                limits.timeout_ms / 1000 + KILL_GRACE_S,
                _expire,
                f"script 執行超過 {limits.timeout_ms}ms",
            )
            payload = await asyncio.to_thread(self._pump, proc, job_message, tools)
        except ScriptError:
            # watchdog 砍掉 child 後，thread 端看到的是 EOF/BrokenPipe —— 那其實是逾時
            if expired:
                raise ScriptTimeout(expired) from None
            raise
        finally:
            timer.cancel()
            # 收屍段（proc.wait + stderr read）本身會阻塞：丟進 worker thread，不讓
            # 事件圈被一個沒收乾淨的 child 卡住整個服務。收尾失敗只記 log、絕不讓
            # 它蓋掉正在傳播的原始例外（例如 _kill 失敗時 proc.wait 逾時炸出的
            # TimeoutExpired，不該蓋過真正的 ScriptTimeout/ScriptError）。
            try:
                await asyncio.to_thread(self._terminate, proc, job)
            except Exception as e:
                logger.warning("隔離子行程收尾失敗（已盡力清理）: %s", e)

        return accept_writes(payload, contract, limits)

    # -- worker thread 這一側（同步 IPC） ------------------------------------

    def _pump(self, proc: subprocess.Popen, job_message: bytes, tools: Any) -> Any:
        """送出 job，在 tool 請求與最終結果之間輪替，直到 child 回報 done/error。"""
        self._write(proc, job_message)
        calls = 0
        while True:
            message = decode_message(_read_line(proc.stdout))
            op = message.get("op")
            if op == "done":
                return message.get("writes")
            if op == "error":
                raise _remote_error(message)
            if op != "tool":
                raise ScriptError(f"隔離子行程送出未知的 IPC 指令: {op}")

            calls += 1
            reply = self._broker(message, tools, calls)
            self._write(
                proc, json.dumps(reply, ensure_ascii=False, default=str).encode("utf-8")
            )

    @staticmethod
    def _write(proc: subprocess.Popen, payload: bytes) -> None:
        try:
            proc.stdin.write(payload + b"\n")
            proc.stdin.flush()
        except OSError as e:  # child 已被 watchdog 砍掉／自己死了
            raise ScriptError(f"隔離子行程的 IPC 中斷: {e}") from e

    def _broker(self, message: dict, tools: Any, calls: int) -> dict:
        """代 child 打 tool：名稱／請求形狀／risk／次數／輸出大小全部在這裡重驗。"""
        name = message.get("name")
        args = message.get("args")
        if not isinstance(name, str) or not isinstance(args, dict):
            return _denied("ToolError", "tool 請求格式不正確")
        if calls > MAX_TOOL_CALLS:
            return _denied(
                "ToolError", f"單一 script 的 tool 呼叫次數超過上限 {MAX_TOOL_CALLS}"
            )
        spec = tool_registry.get(name)
        if spec is None:
            return _denied("UnknownTool", f"未註冊的 tool: {name}")
        if spec.risk not in ALLOWED_TOOL_RISKS:
            return _denied(
                "ToolNotAllowed",
                f"tool '{name}' 的 risk={spec.risk} 不得由隔離 script 呼叫",
            )
        try:
            # ToolBag.call 才是 Skill uses_tools 白名單與 trace 子項的來源，不繞過它。
            # 它本來就是設計給 worker thread 呼叫的（排回事件圈再等結果）。
            result = tools.call(name, **args)
            encoded = json.dumps(result, ensure_ascii=False, default=str)
        except Exception as e:
            return _denied(type(e).__name__, str(e)[:500])
        if len(encoded.encode("utf-8")) > MAX_IPC_BYTES:
            return _denied(
                "ScriptLimitExceeded",
                f"tool '{name}' 的結果超過 IPC 上限 {MAX_IPC_BYTES} 位元組",
            )
        return {"op": "tool_result", "ok": True, "result": json.loads(encoded)}

    # -- 收屍 ---------------------------------------------------------------

    def _terminate(self, proc: subprocess.Popen, job: Any) -> None:
        """強制收掉整組行程並等它真的死透 —— 逾時不留 orphan。"""
        _kill(proc)
        if job is not None:
            kernel32 = _kernel32()
            # 顯式 Terminate 再 Close：KILL_ON_JOB_CLOSE 是後盾，但要「回到呼叫端時
            # 孫行程已經死透」就得同步殺一次（Popen.pid 只是 venv trampoline）。
            kernel32.TerminateJobObject(job, 1)
            kernel32.CloseHandle(job)
        try:
            proc.wait(timeout=CHILD_START_TIMEOUT_S)
        finally:
            for stream, keep in ((proc.stdin, False), (proc.stdout, False), (proc.stderr, True)):
                if stream is None or stream.closed:
                    continue
                if keep:
                    self.last_stderr = stream.read(2000).decode("utf-8", "replace")
                stream.close()


def _denied(error_type: str, error: str) -> dict:
    return {"op": "tool_result", "ok": False, "type": error_type, "error": error}


def _kill(proc: subprocess.Popen) -> None:
    if proc.returncode is not None or proc.poll() is not None:
        return
    if sys.platform == "win32":
        proc.kill()  # 孫行程由 Job 的 KILL_ON_JOB_CLOSE 收
        return
    import signal

    try:
        os.killpg(proc.pid, signal.SIGKILL)  # 整個 process group，不留 orphan
    except (ProcessLookupError, PermissionError):
        proc.kill()
