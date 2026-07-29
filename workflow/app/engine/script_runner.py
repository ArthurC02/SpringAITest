"""Script Runner：Skill 內嵌 Python 片段的沙箱（規格 §5）。

## 威脅模型（規格 §5.0 的誠實邊界，v1 in-process：防意外、不防惡意）

- **密鑰隔離繫於白名單不破功**：script 與 `INTERNAL_API_TOKEN` 同一個行程。AST 白名單
  擋掉 import/dunder/exec 後就沒有第二道牆；白名單破了，token 就在同一個 heap 裡。
- **逾時中斷不了同步 CPU-bound 碼**：script 跑在 worker thread，`asyncio.timeout` 到期
  只能讓「步驟」出場（走 Harness 的 fatal 短路），那條執行緒仍會把當下這段跑完。協作式
  檢查點只覆蓋 `for` 迴圈與 `tools.call`（見 _Budget）。
- **記憶體上限只管寫回 state 的值**（256KB）：中間配置（`sorted(range(10**6))`）不管。
- **一般記憶體/CPU 炸彈不擋**：白名單內就做得到 —— 序列加法倍增（`for _ in range(21): xs = xs + xs`
  → 2M 元素、只跑 21 圈）、pow 累乘（`for _ in range(7): x = x ** 1000`）、大型中間配置。
  迭代計數（10000）只界定圈數不界定量級，256KB 只管寫回不管中間值。**這些擋不住是 §5.0 的
  邊界，不是疏漏**：任何逐點式黑名單 guard 都有等價改寫可繞（擋了 `*` 有 `+`、擋了 `+` 有
  `sum`），與其給人虛假安全感不如老實承認。要防惡意作者請升 v2（見 §5.4）。

真正完整、可誠實聲稱的護欄（無等價改寫可繞）：authoring 限 ADMIN（信任邊界內）、
白名單預設拒絕、`range()` 長度上限 10^6（range 物件唯一來源是 `range` 名稱，長度精確可算）、
`for` 迴圈執行期迭代上限 10000、寫回 state 256KB、`timeout_ms`（讓步驟出場，非中斷執行緒）。

**以上只描述 in-process path**（`ISOLATED_SKILL_SCRIPTS_ENABLED=false`，預設）。旗標開啟時
改走 `app/engine/script_isolation.py` 的子行程 adapter：CPU/記憶體/行程數由 OS 上限強制、
環境變數從零重建、逾時整組 process group 強制終止 —— 上面「擋不住」的那些在隔離路徑下
是真的擋得住。兩條路共用本檔的 `execute_sync`（白名單、預算、保留鍵剝除、寫入上限只有
一份實作），差別只在 execution boundary。

## 白名單的形狀

對齊 app/engine/expressions.py：_analyse（parse + 完整走訪、不短路）→ 轉寫植入執行期
護欄 → exec。**未明列為允許的 AST 節點型別一律 raise ScriptViolation**，因此 Python
日後新增的語法（match、walrus、comprehension…）預設就是拒絕，不需要回頭補黑名單。
存檔時掃一次（skill.validate → forbidden_script）、執行前再掃一次（繞過存檔驗證也擋）。

刻意的嚴格處：
- 屬性存取**只允許 `tools.call`**（不只是禁 dunder）。`state.get(...)`、`"".join(...)`
  一律拒絕 —— 存在性檢查請用 `"k" in state`。開放非 dunder 屬性等於把整個物件圖的
  方法表交出去，白名單就只剩紙糊的。
- comprehension／generator 拒絕：它們會繞過 `for` 的迭代計數器，迭代上限形同虛設。
- `state[...]` 的**寫入鍵必須是字面字串**：寫入鍵靜態可知，編譯器才能把它加進 state
  schema，稽核也才知道這段 script 動了哪些鍵（規格 §5.3）。
"""

import ast
import asyncio
import builtins
import copy
import hashlib
import json
import time
from dataclasses import dataclass
from functools import lru_cache
from types import CodeType
from typing import Any, Protocol

from app.engine.expressions import MAX_AST_DEPTH, ast_depth
from app.engine.harness import (
    CONFIG_SEED_KEYS,
    IDENTITY_KEYS,
    IMMUTABLE_KEYS,
    RUNTIME_AUTHORITY_KEYS,
)
from app.engine.node_registry import ENGINE_KEYS
from app.engine.models import TraceEntry

# ---------------------------------------------------------------------------
# 限制（規格 §5.1／§5.2／§5.0）
# ---------------------------------------------------------------------------

DEFAULT_TIMEOUT_MS = 2000
MAX_TIMEOUT_MS = 10000
MAX_ITERATIONS = 10000  # for 迴圈的執行期迭代總數上限
MAX_WRITE_BYTES = 256 * 1024  # 單次寫入 state 的值總大小上限
MAX_ALLOC = 10**6  # range() 長度上限（唯一完整可界定的配置護欄，見 module docstring）

# script 一律不得寫入的鍵：身分鍵（換租戶）、不可變鍵（偽造稽核的原始問題）、
# 引擎鍵（偽造 trace / fatal_error 可繞過短路與稽核）、Configuration Set 執行參數
# （竄改 retrieval_top_k 等伺服器注入的只讀 seed）、D3 授權鍵（關掉 enforce_data_scope
# 或放寬 knowledge_sources 就是替後續 tool 步驟的 ToolContext 提權）。
# 這五組即 skill.RESERVED_KEYS ∪ ENGINE_KEYS —— tool 步驟的 save_as 走 writable_key()
# 擋的是同一組鍵，script 這條路不得比它寬。__ 前綴另外擋（引擎內部鍵）。
FORBIDDEN_WRITE_KEYS = frozenset(
    IDENTITY_KEYS
    | IMMUTABLE_KEYS
    | ENGINE_KEYS
    | CONFIG_SEED_KEYS
    | RUNTIME_AUTHORITY_KEYS
)

# 白名單 builtins（規格 §5.2 逐字）。random/time/datetime 這類非確定性來源不在此列。
SAFE_BUILTIN_NAMES = frozenset(
    {
        "str", "int", "float", "list", "dict", "set", "tuple", "len", "min", "max",
        "sum", "sorted", "round", "abs", "enumerate", "range", "zip",
    }
)

# script 可見的兩個名稱（規格 §5.1）
_GLOBAL_NAMES = frozenset({"state", "tools"})

# 轉寫植入的執行期護欄名（__ 前綴 → script 自己引用不到：Name 檢查會拒絕）
_ITER_GUARD = "__guard_iter"

_BIN_OPS = (ast.Add, ast.Sub, ast.Mult, ast.Div, ast.FloorDiv, ast.Mod, ast.Pow)
_UNARY_OPS = (ast.UAdd, ast.USub, ast.Not)
_CMP_OPS = (
    ast.Eq, ast.NotEq, ast.Lt, ast.LtE, ast.Gt, ast.GtE,
    ast.In, ast.NotIn, ast.Is, ast.IsNot,
)
_CONST_TYPES = (str, int, float, bool, type(None))


# ---------------------------------------------------------------------------
# 例外（Harness 一律轉成 fatal_error；error_code 取類別名）
# ---------------------------------------------------------------------------


class ScriptError(Exception):
    """script 執行失敗的基底類別。"""


class ScriptViolation(ScriptError, ValueError):
    """AST 白名單違規（存檔時對應錯誤碼 forbidden_script）。"""


class ScriptTimeout(ScriptError):
    """script 執行逾時（error_code 含 Timeout 標記，供 trace 辨識）。"""


class ScriptLimitExceeded(ScriptError):
    """迭代次數／配置長度／state 寫入大小超過上限。"""


class ScriptTraceEntry(TraceEntry):
    """script 步驟的 trace entry：**只有** SHA-256，沒有任何放得下原始碼全文的欄位。

    規格 §5.3／§6.3-5：原始碼全文不落 trace（revision 表已存原文，hash 可回查）。
    讀寫的鍵名清單走 TraceEntry 既有的 input_summary / output_summary。
    """

    script_sha256: str


# ---------------------------------------------------------------------------
# 靜態掃描：白名單檢查 + 讀寫鍵名 + 呼叫到的 tool 名
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ScriptContract:
    """一段 script 的靜態事實：讀寫的 state 鍵、呼叫的 tool 名（常數名）、原始碼 SHA-256。"""

    reads: tuple[str, ...]
    writes: tuple[str, ...]
    tools: tuple[str, ...]
    sha256: str


class _Analyser:
    """完整走訪 AST：白名單外的節點型別一律 raise（不短路、不「沒處理到就當沒事」）。"""

    def __init__(self) -> None:
        self.reads: set[str] = set()
        self.writes: set[str] = set()
        self.tools: set[str] = set()
        self.locals: set[str] = set()

    # -- 入口 ---------------------------------------------------------------

    def run(self, tree: ast.Module) -> None:
        self._collect_locals(tree)
        for stmt in tree.body:
            self.stmt(stmt)

    def _collect_locals(self, tree: ast.Module) -> None:
        """先收齊所有被賦值的區域名：Load 檢查要能分辨「區域變數」與「不存在的名稱」。"""
        for n in ast.walk(tree):
            if isinstance(n, ast.Name) and isinstance(n.ctx, (ast.Store, ast.Del)):
                if n.id in _GLOBAL_NAMES:
                    raise ScriptViolation(f"不允許覆寫保留名稱: {n.id}")
                if n.id.startswith("__"):
                    raise ScriptViolation(f"不允許雙底線名稱: {n.id}")
                self.locals.add(n.id)

    # -- 陳述句 -------------------------------------------------------------

    def stmt(self, node: ast.stmt) -> None:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                self.target(target)
            self.expr(node.value)
            return
        if isinstance(node, ast.AugAssign):
            # x += 1 同時是讀也是寫
            self.target(node.target)
            self.read_target(node.target)
            self.expr(node.value)
            return
        if isinstance(node, ast.Expr):
            self.expr(node.value)
            return
        if isinstance(node, ast.If):
            self.expr(node.test)
            self.block(node.body)
            self.block(node.orelse)
            return
        if isinstance(node, ast.For):
            # for 的目標只能是（巢狀的）區域名，不能是 state["k"]
            self.loop_target(node.target)
            self.expr(node.iter)
            self.block(node.body)
            self.block(node.orelse)
            return
        if isinstance(node, (ast.Pass, ast.Break, ast.Continue)):
            return
        # while / import / def / class / global / nonlocal / try / with / raise /
        # return / del / assert / match … 全部走到這裡
        raise ScriptViolation(f"不允許的語法: {type(node).__name__}")

    def block(self, stmts: list[ast.stmt]) -> None:
        for stmt in stmts:
            self.stmt(stmt)

    # -- 賦值目標 -----------------------------------------------------------

    def target(self, node: ast.expr) -> None:
        if isinstance(node, ast.Name):
            return  # _collect_locals 已驗過
        if isinstance(node, ast.Subscript):
            key = self._state_key(node)
            if key is not None:
                self.writes.add(key)
                return
            # 區域變數的下標寫入（d["k"] = 1）：不是 state，照一般運算式檢查
            self.expr(node.value)
            self.expr(node.slice)
            return
        if isinstance(node, (ast.Tuple, ast.List)):
            for elt in node.elts:
                self.target(elt)
            return
        raise ScriptViolation(f"不允許的賦值目標: {type(node).__name__}")

    def read_target(self, node: ast.expr) -> None:
        key = self._state_key(node) if isinstance(node, ast.Subscript) else None
        if key is not None:
            self.reads.add(key)

    def loop_target(self, node: ast.expr) -> None:
        if isinstance(node, ast.Name):
            return
        if isinstance(node, (ast.Tuple, ast.List)):
            for elt in node.elts:
                self.loop_target(elt)
            return
        raise ScriptViolation(f"for 的迴圈變數只能是名稱: {type(node).__name__}")

    def _state_key(self, node: ast.Subscript) -> str | None:
        """`state["literal"]` → 鍵名；不是 state 的下標 → None。"""
        if not (isinstance(node.value, ast.Name) and node.value.id == "state"):
            return None
        if not (isinstance(node.slice, ast.Constant) and isinstance(node.slice.value, str)):
            raise ScriptViolation("state 的鍵必須是字面字串（動態鍵無法靜態稽核）")
        key = node.slice.value
        if key.startswith("__"):
            raise ScriptViolation(f"不允許存取引擎保留鍵: state[{key!r}]")
        return key

    # -- 運算式 -------------------------------------------------------------

    def expr(self, node: ast.expr) -> None:
        if isinstance(node, ast.Constant):
            if not isinstance(node.value, _CONST_TYPES):
                raise ScriptViolation(f"不支援的常數型別: {type(node.value).__name__}")
            return

        if isinstance(node, ast.Name):
            if node.id in _GLOBAL_NAMES or node.id in SAFE_BUILTIN_NAMES:
                return
            if node.id in self.locals:
                return
            # open / exec / eval / compile / __import__ / os … 全部在這裡出局
            raise ScriptViolation(f"不允許的名稱: {node.id}")

        if isinstance(node, ast.BinOp):
            if not isinstance(node.op, _BIN_OPS):
                raise ScriptViolation(f"不支援的運算子: {type(node.op).__name__}")
            self.expr(node.left)
            self.expr(node.right)
            return

        if isinstance(node, ast.UnaryOp):
            if not isinstance(node.op, _UNARY_OPS):
                raise ScriptViolation(f"不支援的一元運算子: {type(node.op).__name__}")
            self.expr(node.operand)
            return

        if isinstance(node, ast.BoolOp):
            for value in node.values:
                self.expr(value)
            return

        if isinstance(node, ast.Compare):
            if any(not isinstance(op, _CMP_OPS) for op in node.ops):
                raise ScriptViolation("不支援的比較運算子")
            self.expr(node.left)
            for comparator in node.comparators:
                self.expr(comparator)
            return

        if isinstance(node, ast.IfExp):
            self.expr(node.test)
            self.expr(node.body)
            self.expr(node.orelse)
            return

        if isinstance(node, ast.Call):
            self.call(node)
            return

        if isinstance(node, ast.Subscript):
            key = self._state_key(node) if isinstance(node.value, ast.Name) else None
            if key is not None:
                self.reads.add(key)
                return
            self.expr(node.value)
            self.expr(node.slice)
            return

        if isinstance(node, ast.Slice):
            for part in (node.lower, node.upper, node.step):
                if part is not None:
                    self.expr(part)
            return

        if isinstance(node, (ast.List, ast.Tuple, ast.Set)):
            for elt in node.elts:
                self.expr(elt)
            return

        if isinstance(node, ast.Dict):
            for key_node in node.keys:
                if key_node is None:  # {**other}
                    raise ScriptViolation("不允許 dict 解包")
                self.expr(key_node)
            for value in node.values:
                self.expr(value)
            return

        if isinstance(node, ast.JoinedStr):  # f-string（規格 §5.2 明列允許）
            for value in node.values:
                self.expr(value)
            return

        if isinstance(node, ast.FormattedValue):
            self.expr(node.value)
            if node.format_spec is not None:
                self.expr(node.format_spec)
            return

        # Attribute（含 state.__class__）、Lambda、ListComp/GeneratorExp、Await、
        # Yield、Starred、NamedExpr… 全部走到這裡
        raise ScriptViolation(f"不允許的語法: {type(node).__name__}")

    def call(self, node: ast.Call) -> None:
        for kw in node.keywords:
            if kw.arg is None:
                raise ScriptViolation("不允許 ** 引數解包")
            self.expr(kw.value)
        for arg in node.args:
            if isinstance(arg, ast.Starred):
                raise ScriptViolation("不允許 * 引數解包")
            self.expr(arg)

        func = node.func
        # 唯一允許的屬性存取：tools.call(name, **args)（規格 §5.1）
        if isinstance(func, ast.Attribute):
            if not (
                isinstance(func.value, ast.Name)
                and func.value.id == "tools"
                and func.attr == "call"
            ):
                raise ScriptViolation("只允許 tools.call(...) 一個方法呼叫")
            if node.args and isinstance(node.args[0], ast.Constant):
                self.tools.add(str(node.args[0].value))
            return

        if isinstance(func, ast.Name):
            if func.id not in SAFE_BUILTIN_NAMES:
                # 區域變數不可當函式呼叫：state 裡若有 callable，f = state["x"]; f() 就是逃逸
                raise ScriptViolation(f"不允許呼叫: {func.id}")
            if func.id == "range":
                self._check_range(node)
            return

        raise ScriptViolation(f"不允許的呼叫對象: {type(func).__name__}")

    # -- range() 長度上限：唯一完整可界定的配置護欄（見 module docstring） ------

    def _check_range(self, node: ast.Call) -> None:
        for arg in node.args:
            if isinstance(arg, ast.Constant) and isinstance(arg.value, int):
                if abs(arg.value) > MAX_ALLOC:
                    raise ScriptViolation(
                        f"range() 的引數超過上限 {MAX_ALLOC}: {arg.value}"
                    )


# ---------------------------------------------------------------------------
# 轉寫：把執行期迭代計數護欄植入 for 迴圈
# ---------------------------------------------------------------------------


class _Guard(ast.NodeTransformer):
    def visit_For(self, node: ast.For) -> ast.For:
        self.generic_visit(node)
        node.iter = ast.Call(
            func=ast.Name(id=_ITER_GUARD, ctx=ast.Load()), args=[node.iter], keywords=[]
        )
        return ast.fix_missing_locations(node)


@lru_cache(maxsize=128)
def _prepare(source: str) -> tuple[ScriptContract, CodeType]:
    """parse → 白名單檢查 → 植入護欄 → compile。以原始碼為鍵快取（迴圈每輪都會跑同一段）。"""
    if not isinstance(source, str) or not source.strip():
        raise ScriptViolation("script 不可為空")
    try:
        tree = ast.parse(source, mode="exec")
    except (SyntaxError, MemoryError, RecursionError) as e:
        # 同 expressions._parse：parser 自己的堆疊溢位是 MemoryError，
        # 早於下面的 MAX_AST_DEPTH 發生
        raise ScriptViolation(f"script 語法錯誤: {e}")
    # 深度先驗（與 expressions 共用同一個上限與量法）：_Analyser 是遞迴走訪，
    # 沒有這道護欄時巢狀輸入會讓 RecursionError 逃出白名單的攔截網 ——
    # /skills/validate-package 這個未信任 zip 的信任邊界會直接 500。
    if ast_depth(tree) > MAX_AST_DEPTH:
        raise ScriptViolation(f"script 的巢狀深度超過上限 {MAX_AST_DEPTH}")

    analyser = _Analyser()
    analyser.run(tree)
    contract = ScriptContract(
        reads=tuple(sorted(analyser.reads)),
        writes=tuple(sorted(analyser.writes)),
        tools=tuple(sorted(analyser.tools)),
        sha256=hashlib.sha256(source.encode("utf-8")).hexdigest(),
    )
    code = compile(_Guard().visit(tree), "<skill_script>", "exec")
    return contract, code


def scan(source: str) -> ScriptContract:
    """存檔期／編譯期的靜態掃描：違規 → ScriptViolation（錯誤碼 forbidden_script）。"""
    return _prepare(source)[0]


# ---------------------------------------------------------------------------
# 執行期護欄
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ScriptLimits:
    """單一 script 步驟的限制。timeout_ms 由 Skill 指定（1~10000），其餘是引擎硬上限。"""

    timeout_ms: int = DEFAULT_TIMEOUT_MS
    max_iterations: int = MAX_ITERATIONS
    max_write_bytes: int = MAX_WRITE_BYTES
    max_alloc: int = MAX_ALLOC

    def __post_init__(self) -> None:
        if not isinstance(self.timeout_ms, int) or isinstance(self.timeout_ms, bool):
            raise ScriptViolation(f"timeout_ms 必須是整數: {self.timeout_ms!r}")
        if not 1 <= self.timeout_ms <= MAX_TIMEOUT_MS:
            raise ScriptViolation(
                f"timeout_ms={self.timeout_ms} 超出允許範圍 1~{MAX_TIMEOUT_MS}"
            )


class _Budget:
    """執行期預算：迭代計數 + 逾時檢查點 + 配置長度上限。"""

    def __init__(self, limits: ScriptLimits):
        self.limits = limits
        self.iterations = 0
        self.deadline = time.monotonic() + limits.timeout_ms / 1000

    def iterate(self, iterable: Any) -> Any:
        """for 的協作式檢查點：每一輪先驗逾時、再驗迭代上限（規格 §5.2 的 10000 次）。"""
        for item in iterable:
            if time.monotonic() > self.deadline:
                raise ScriptTimeout(f"script 執行超過 {self.limits.timeout_ms}ms")
            self.iterations += 1
            if self.iterations > self.limits.max_iterations:
                raise ScriptLimitExceeded(
                    f"for 迴圈迭代次數超過上限 {self.limits.max_iterations}"
                )
            yield item

    def range(self, *args: Any) -> range:
        """白名單的 range：長度上限（`range(n)` 的 n 來自 state → 靜態算不出來）。"""
        r = range(*args)
        if len(r) > self.limits.max_alloc:
            raise ScriptLimitExceeded(f"range() 的長度超過上限 {self.limits.max_alloc}")
        return r


class StateView(dict):
    """script 看到的 state：記錄讀寫鍵名，寫入只落在這份深拷貝上。

    深拷貝而不是淺拷貝：淺拷貝時 script 可以就地改 state 裡的 list/pydantic 物件
    （`state["ranked_sources"][0]` 這種），改動會直接穿透回真正的 state，
    「未宣告的寫入一律剝除」就形同虛設。
    """

    def __init__(self, data: dict):
        super().__init__(copy.deepcopy(data))
        self.read_keys: set[str] = set()
        self.write_keys: set[str] = set()

    def __getitem__(self, key: Any) -> Any:
        self.read_keys.add(str(key))
        value = super().__getitem__(key)
        # 防禦縱深：白名單禁止呼叫區域變數（`f = state['x']; f()`），但 sorted(seq, key=f)
        # 之類會在內部呼叫 callable，繞過那道禁令。從源頭斷：state 值一律不得是 callable。
        # 現行節點寫回 state 的都是 dict/pydantic（無 callable），此路目前不可達，但釘死
        # 「未來有節點把 callable 放進 state」也不會活。
        if callable(value):
            raise ScriptViolation(f"state[{key!r}] 是可呼叫物件，script 不得取用")
        return value

    def __contains__(self, key: Any) -> bool:
        self.read_keys.add(str(key))
        return super().__contains__(key)

    def __setitem__(self, key: Any, value: Any) -> None:
        self.write_keys.add(str(key))
        super().__setitem__(key, value)

    def written(self) -> dict:
        """script 寫入的鍵值；保留鍵（身分／不可變／引擎鍵）一律剝除（AT-GOV-03）。"""
        return {
            k: dict.__getitem__(self, k)
            for k in sorted(self.write_keys)
            if k not in FORBIDDEN_WRITE_KEYS and not k.startswith("__")
        }


def _value_size(value: Any) -> int:
    """序列化後的位元組數（值本身，不含鍵）。"""
    try:
        return len(json.dumps(value, ensure_ascii=False, default=str).encode("utf-8"))
    except (TypeError, ValueError):
        return len(str(value).encode("utf-8"))


# ---------------------------------------------------------------------------
# Port + v1 實作
# ---------------------------------------------------------------------------


def execute_sync(
    source: str, state_view: dict, tools: Any, limits: ScriptLimits
) -> dict:
    """同步執行一段 script，回傳「要合併進 state 的鍵值」（已剝除保留鍵、已驗大小上限）。

    白名單、執行期預算、保留鍵剝除、寫入大小上限**只有這一份實作**：in-process runner
    在 worker thread 呼叫它，隔離子行程（script_child）也呼叫同一份。兩條 adapter 因此
    不可能在治理語義上分岔（Phase S1 的 behavior parity 由此保證，不靠兩邊各寫一次）。
    """
    contract, code = _prepare(source)  # 執行前再驗一次白名單（規格 §5.2）
    view = StateView(state_view)
    budget = _Budget(limits)  # 計時從這裡起算：拷貝的耗時也算進 timeout

    namespace: dict[str, Any] = {
        "__builtins__": {
            **{name: getattr(builtins, name) for name in SAFE_BUILTIN_NAMES},
            "range": budget.range,  # 換成有長度上限的版本
        },
        "state": view,
        "tools": tools,
        _ITER_GUARD: budget.iterate,
    }
    # 沙箱：namespace 的 __builtins__ 已換成白名單，且 code 來自 _prepare 的
    # 白名單 AST —— 這行的安全性完全由上面兩者保證。
    exec(code, namespace)  # noqa: S102

    writes = view.written()
    total = sum(_value_size(v) for v in writes.values())
    if total > limits.max_write_bytes:
        raise ScriptLimitExceeded(
            f"script 寫入 state 的值總大小 {total} 位元組，"
            f"超過上限 {limits.max_write_bytes} 位元組"
        )
    # contract.writes 是靜態可知的寫入鍵；執行期實際寫入必為其子集（鍵是字面字串）
    return {k: v for k, v in writes.items() if k in contract.writes}


class ScriptRunnerPort(Protocol):
    """規格 §5.4 的 v2 升級介面：v1 是 in-process，v2 換 subprocess，呼叫端不變。"""

    async def run(
        self, source: str, state_view: dict, tools: Any, limits: ScriptLimits
    ) -> dict: ...


class RestrictedInProcessRunner:
    """v1：受限 in-process 執行（本行程、worker thread、AST 白名單 + 執行期預算）。"""

    async def run(
        self, source: str, state_view: dict, tools: Any, limits: ScriptLimits
    ) -> dict:
        """執行 script，回傳「要合併進 state 的鍵值」（已剝除保留鍵、已驗大小上限）。"""
        _prepare(source)  # 白名單違規在建立執行緒之前就出局（lru_cache → 下面不重算）
        try:
            async with asyncio.timeout(limits.timeout_ms / 1000):
                # 跑在 worker thread：同步碼不會卡住事件圈，逾時才有辦法讓步驟出場
                # （執行緒本身中斷不了 —— 見 module docstring 的威脅模型）
                return await asyncio.to_thread(
                    execute_sync, source, state_view, tools, limits
                )
        except TimeoutError as e:
            raise ScriptTimeout(f"script 執行超過 {limits.timeout_ms}ms") from e
