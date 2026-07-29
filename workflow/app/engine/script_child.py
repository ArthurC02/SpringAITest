"""隔離子行程的進入點（Phase S1）：script 在這裡跑，parent 什麼權限都不給它。

**這支檔案不是給 parent 行程 import 的**（parent 用 `sys.executable` 把它當腳本啟動）。
它只允許 stdlib + `app.engine.script_runner`：白名單、執行期預算、保留鍵剝除、寫入大小
上限全部沿用 runner 那一份 `execute_sync`，隔離 adapter 才不會和 in-process path 分岔。

這個行程手上沒有任何憑證：環境變數由 parent 從零重建（`INTERNAL_API_TOKEN`／DB URL／
JWT secret／provider key 一律不在），tool 呼叫只能把 typed request 從 stdout 送回 parent
broker，由 parent 重新驗證白名單／risk／次數／輸出大小後代打。

OS 上限的分工：
- POSIX：本行程自己 `resource.setrlimit`（CPU、位址空間、fork、fd、檔案寫入）。
  設在「匯入完成、執行 script 之前」——太早設會把 pydantic 的匯入一起壓死。
- Windows：沒有 setrlimit，改由 parent 的 Job Object 在外面套（記憶體、行程數、
  kill-on-close）。子行程這邊無從查驗，因此 parent 在 Job 建不起來時**根本不會啟動**
  這個行程（fail closed，見 script_isolation.os_limits_available）。
"""

import hashlib
import json
import os
import sys

# IPC 單則訊息上限；與 parent 的 script_isolation.MAX_IPC_BYTES 是同一個數字，
# 但這支檔案不 import parent 模組（parent 會在 job 裡把值帶過來）。
_FALLBACK_IPC_BYTES = 1024 * 1024


def _send(message: dict) -> None:
    sys.stdout.write(json.dumps(message, ensure_ascii=False, default=str) + "\n")
    sys.stdout.flush()


def _recv() -> dict:
    line = sys.stdin.readline()
    if not line:
        raise SystemExit(1)  # parent 收掉了 pipe：沒什麼好回報的，直接退場
    return json.loads(line)


def _apply_os_limits(limits: dict) -> None:
    """POSIX：把 CPU／記憶體／fork／fd／檔案寫入壓到 OS 層（Windows 由 parent 的 Job 負責）。"""
    if sys.platform == "win32":
        return
    import resource

    memory_bytes = int(limits["memory_bytes"])
    cpu_seconds = int(limits["cpu_seconds"])
    for name, value in (
        ("RLIMIT_AS", (memory_bytes, memory_bytes)),
        ("RLIMIT_CPU", (cpu_seconds, cpu_seconds + 1)),
        ("RLIMIT_NPROC", (0, 0)),  # fork/exec 一律失敗
        ("RLIMIT_FSIZE", (0, 0)),  # 寫檔一律失敗（stdout/stderr 是 pipe，不受影響）
        ("RLIMIT_NOFILE", (16, 16)),
    ):
        limit = getattr(resource, name, None)
        if limit is not None:
            resource.setrlimit(limit, value)


class _ToolProxy:
    """script 看到的 `tools`：只有 call 一個方法，且不持有任何憑證。

    一次呼叫 = 一個 typed request 送回 parent broker + 一則回覆。parent 才是重新驗證
    白名單／risk／次數／輸出大小的地方；這裡不做任何授權判斷（做了也不可信）。
    """

    def call(self, name: str, **args: object) -> object:
        _send({"op": "tool", "name": name, "args": args})
        reply = _recv()
        if reply.get("op") != "tool_result":
            raise RuntimeError("tool 回覆格式不正確")
        if not reply.get("ok"):
            raise _remote_error(reply.get("type"), reply.get("error"))
        return reply.get("result")


def _remote_error(type_name: object, message: object) -> Exception:
    """把 parent 回報的錯誤還原成同名例外，讓 trace 的 error_code 與 in-process 一致。"""
    name = type_name if isinstance(type_name, str) and type_name.isidentifier() else "ToolError"
    return type(name, (RuntimeError,), {})(str(message))


def main() -> int:
    sys.path.insert(0, sys.argv[1])
    # 匯入放在握手之前：parent 的 script deadline 從送出 job 起算，匯入成本不該算進去。
    from app.engine import script_runner

    # 回報自己的 pid：venv 的 python.exe 是 trampoline，parent 手上的 Popen.pid 是它，
    # 真正跑 script 的是這個行程 —— 「逾時不留 orphan」要驗的就是這個 pid。
    _send({"op": "ready", "pid": os.getpid()})
    job = _recv()

    if job.get("op") != "run":
        _send({"op": "error", "type": "ScriptError", "message": "非預期的 IPC 指令"})
        return 1
    source = job.get("source")
    if not isinstance(source, str) or hashlib.sha256(
        source.encode("utf-8")
    ).hexdigest() != job.get("sha256"):
        # IPC 被截斷／竄改：不執行任何東西（fail closed）
        _send({"op": "error", "type": "ScriptError", "message": "script 內容與雜湊不符"})
        return 1

    try:
        limits_spec = job["limits"]
        _apply_os_limits(limits_spec)
        limits = script_runner.ScriptLimits(timeout_ms=int(limits_spec["timeout_ms"]))
        writes = script_runner.execute_sync(source, job["state"], _ToolProxy(), limits)
        payload = json.dumps(
            {"op": "done", "writes": writes}, ensure_ascii=False, default=str
        )
    except BaseException as e:  # MemoryError／RecursionError 也要走受控回報
        _send({"op": "error", "type": type(e).__name__, "message": str(e)[:500]})
        return 1

    max_bytes = int(limits_spec.get("max_ipc_bytes", _FALLBACK_IPC_BYTES))
    if len(payload.encode("utf-8")) > max_bytes:
        # 序列化後才發現超大：不要送出去撐爆 parent 的讀取上限，這裡就出局
        _send(
            {
                "op": "error",
                "type": "ScriptLimitExceeded",
                "message": f"script 輸出超過 IPC 上限 {max_bytes} 位元組",
            }
        )
        return 1
    sys.stdout.write(payload + "\n")
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
