"""Skill → LangGraph CompiledStateGraph 的編譯器（規格 §4）。

三件事值得先講清楚：

1. **State schema 是編譯期組出來的動態 TypedDict**（不是固定的 KbQueryState）：
   鍵 = 所引用節點的 reads/writes 聯集 + input_schema + 保留鍵 + 引擎鍵 + 迴圈計數鍵。
   reducer 只有兩種來源：引擎鍵（trace/errors 一律 operator.add）與節點自己宣告的
   `appends`（如 retrieval_planner 的 retrieval_plans）。領域鍵的累加語意屬於節點，
   不寫死在引擎裡。

2. **治理硬規則在這一層，不在 API 層**（規格 §6.3）：即使繞過 POST /skills/validate
   直接呼叫 compile()，缺 max_iterations 的 loop 一樣拒絕編譯；稽核節點由編譯器強制
   附加於終止路徑，Skill 的 YAML 不必也不該自己寫。

3. 只用 add_node / add_edge / add_conditional_edges 三個 API（規格風險表：LangGraph
   的其餘 API 面變動風險高）。branch/loop 的控制流靠引擎植入的 no-op 節點表達 ——
   它們不經 Harness、不寫 trace，因此對稽核軌跡完全透明。
"""

import asyncio
import hashlib
import operator
from collections import OrderedDict
from typing import Annotated, Any, Iterable, TypedDict

from langgraph.graph import END, START, StateGraph
from langgraph.graph.state import CompiledStateGraph

from app.engine import expressions, script_runner, skill as skill_mod, tool_registry
from app.engine.harness import IDENTITY_KEYS, describe, harnessed
from app.engine.node_registry import NodeSpec
from app.engine.script_runner import (
    RestrictedInProcessRunner,
    ScriptLimits,
    ScriptRunnerPort,
    ScriptTraceEntry,
)
from app.engine.skill import Skill
from app.engine.tool_registry import ToolBag, ToolContext

# 稽核節點名（治理硬規則 §6.3-3：每條終止路徑強制附加）。是引擎常數不是設定：
# 「稽核可以被關掉或換掉」本身就是治理漏洞。
AUDIT_NODE = "audit_feedback"

# 引擎內部鍵前綴：Skill 的條件式讀不到（expressions 直接拒絕 __ 開頭的鍵），
# 也不會出現在對外回傳的 output（見 public_output）。
INTERNAL_PREFIX = "__"


class SkillCompileError(ValueError):
    """Skill 無法編譯（未知節點、缺 max_iterations、不支援的步驟型別…）。"""


def public_output(state: dict) -> dict:
    """剝除引擎內部鍵（__loop_<id>_count）後的對外 state。"""
    return {k: v for k, v in state.items() if not k.startswith(INTERNAL_PREFIX)}


# ---------------------------------------------------------------------------
# 前置掃描：收集引用的節點與迴圈計數鍵（動態 TypedDict 必須在建圖前組好）
# ---------------------------------------------------------------------------


class _Scan:
    def __init__(self, allowed_tools: set[str] | None = None) -> None:
        self.specs: list[NodeSpec] = []
        self.internal_keys: list[str] = []  # 迴圈計數 + 條件式求值結果（都是 state 頻道）
        self.extra_keys: list[str] = []  # script 寫入的鍵 + tool 步驟的 save_as
        self.loops = 0
        self.branches = 0
        self.allowed_tools: set[str] = allowed_tools or set()


def _loop_key(index: int) -> str:
    return f"{INTERNAL_PREFIX}loop_{index}_count"


def _loop_exit_key(index: int) -> str:
    return f"{INTERNAL_PREFIX}loop_{index}_until"


def _branch_key(index: int) -> str:
    return f"{INTERNAL_PREFIX}branch_{index}_when"


def _scan(steps: Iterable[Any], out: _Scan) -> None:
    """走訪順序必須與 _Builder 的 emit 完全一致（loop/branch 的流水號兩邊要對得上）。"""
    for step in steps or []:
        parsed = skill_mod.parse_step(step)
        if parsed is None:
            raise SkillCompileError(f"未知的步驟型別: {step}")
        kind, body = parsed
        if kind == "node":
            spec = skill_mod.resolve_node(body)
            if spec is None:
                raise SkillCompileError(f"unknown node: {body}")
            out.specs.append(spec)
        elif kind == "sequence":
            _scan(body, out)
        elif kind == "branch":
            _require_expression(body, "when")
            if not body.get("then"):
                raise SkillCompileError("branch 的 then 不可為空")
            out.internal_keys.append(_branch_key(out.branches))
            out.branches += 1
            _scan(body["then"], out)
            _scan(body.get("else") or [], out)
        elif kind == "loop":
            _check_loop_bound(body)
            out.internal_keys += [_loop_key(out.loops), _loop_exit_key(out.loops)]
            out.loops += 1
            _scan(body.get("body") or [], out)
        elif kind == "script":
            # 治理硬規則同 loop：繞過 POST /skills/validate 直接 compile() 也要擋
            contract = _script_contract(body)
            _script_limits(step)
            for name in contract.tools:
                _require_tool(name, out.allowed_tools)
            out.extra_keys += list(contract.writes)
        elif kind == "tool":
            name, save_as, _ = _tool_step(body, step, out.allowed_tools)
            out.extra_keys.append(save_as)
        else:  # pragma: no cover —— parse_step 已保證 kind 在 STEP_TYPES 內
            raise SkillCompileError(f"未知的步驟型別: {kind}")


def _require_expression(body: Any, field: str) -> None:
    if not isinstance(body, dict) or not isinstance(body.get(field), str):
        raise SkillCompileError(f"{field} 必須是條件式字串")
    expressions.validate(body[field])  # 白名單外語法 → ExpressionError


def _script_contract(source: Any) -> script_runner.ScriptContract:
    """script 的白名單掃描（引擎層護欄：繞過 validate API 直接 compile 一樣拒編）。"""
    if not isinstance(source, str) or not source.strip():
        raise SkillCompileError("script 必須是非空的 Python 原始碼字串")
    try:
        return script_runner.scan(source)
    except script_runner.ScriptViolation as e:
        raise SkillCompileError(f"forbidden_script: {e}")


def _script_limits(step: dict) -> ScriptLimits:
    """timeout_ms：預設 2000、上限 10000（規格 §3.2）。超界 → 拒編。"""
    timeout_ms = step.get("timeout_ms", script_runner.DEFAULT_TIMEOUT_MS)
    try:
        return ScriptLimits(timeout_ms=timeout_ms)
    except script_runner.ScriptViolation as e:
        raise SkillCompileError(f"script 的 {e}")


def _require_tool(name: Any, allowed: set[str]) -> None:
    if tool_registry.get(name) is None:
        raise SkillCompileError(f"unknown tool: {name}")
    if name not in allowed:
        raise SkillCompileError(f"tool '{name}' 不在 Skill 的 uses_tools 清單中")


def _tool_step(name: Any, step: dict, allowed: set[str]) -> tuple[str, str, dict]:
    """tool 步驟的靜態檢查：tool 已註冊且在白名單、args 是 mapping、save_as 是合法鍵。"""
    _require_tool(name, allowed)
    args = step.get("args") or {}
    if not isinstance(args, dict):
        raise SkillCompileError("tool 的 args 必須是對應（mapping）")
    save_as = step.get("save_as")
    if not skill_mod.writable_key(save_as):
        raise SkillCompileError(
            f"tool 步驟的 save_as 必須是合法的 state 鍵（不可為保留鍵／__ 前綴）: {save_as!r}"
        )
    return name, save_as, args


def _check_loop_bound(body: Any) -> None:
    """治理硬規則：loop 必有 1~10 的上限，缺或超界一律拒編（AT-GOV-02）。"""
    if not isinstance(body, dict):
        raise SkillCompileError("loop 必須是含 max_iterations / body 的對應")
    n = body.get("max_iterations")
    if not isinstance(n, int) or isinstance(n, bool):
        raise SkillCompileError(
            f"loop 缺少 max_iterations（必填，{skill_mod.LOOP_MIN}~{skill_mod.LOOP_MAX}）"
        )
    if not skill_mod.LOOP_MIN <= n <= skill_mod.LOOP_MAX:
        raise SkillCompileError(
            f"loop 的 max_iterations={n} 超出允許範圍 "
            f"{skill_mod.LOOP_MIN}~{skill_mod.LOOP_MAX}"
        )
    if body.get("until") is not None:
        _require_expression(body, "until")
    if not body.get("body"):
        raise SkillCompileError("loop 的 body 不可為空")


# ---------------------------------------------------------------------------
# State schema
# ---------------------------------------------------------------------------


def build_state_schema(skill: Skill, scan: _Scan, audit_spec: NodeSpec) -> type:
    """依引用節點的契約組出動態 TypedDict（見 module docstring 第 1 點）。"""
    appends: set[str] = set()
    keys: set[str] = set(skill_mod.RESERVED_KEYS) | set(skill.input_schema)
    for spec in [*scan.specs, audit_spec]:
        keys.update(spec.reads)
        keys.update(spec.writes)
        appends.update(spec.appends)
    keys.update(scan.internal_keys)
    keys.update(scan.extra_keys)  # script 寫入的鍵 / tool 的 save_as 也要有 state 頻道
    keys -= skill_mod.ENGINE_KEYS  # 引擎鍵在下面單獨掛 reducer

    annotations: dict[str, Any] = {k: Any for k in sorted(keys)}
    annotations["trace"] = Annotated[list, operator.add]
    annotations["errors"] = Annotated[list, operator.add]
    annotations["fatal_error"] = Any
    for key in sorted(appends):
        annotations[key] = Annotated[list, operator.add]
    return TypedDict("SkillState", annotations, total=False)  # type: ignore[operator]


# ---------------------------------------------------------------------------
# 建圖
# ---------------------------------------------------------------------------


async def _noop(state: dict) -> dict:
    """控制流用的空節點（join / loop 出口）：不碰 state、不寫 trace。"""
    return {}


def _safe_eval(expr: str, state: dict, key: str, *, on_error: bool, where: str) -> dict:
    """求值條件式並存進內部鍵；求值失敗 → 取 on_error 這個安全值 + 記一筆 errors。

    安全值的定義：loop 取 True（離開迴圈）、branch 取 False（走 else／join）——
    兩者都通往「往終止路徑走」，因此圖必定抵達強制附加的稽核節點。
    """
    try:
        return {key: bool(expressions.evaluate(expr, state))}
    except expressions.ExpressionError as e:
        return {
            key: on_error,
            "errors": [
                {
                    "node": where,
                    "error": f"條件式求值失敗，改走安全路徑: {e}",
                    "error_type": type(e).__name__,
                }
            ],
        }


def _node_reads(spec: NodeSpec, params: dict) -> set[str]:
    """node 步驟餵給 harnessed 的 effective_reads（reads 契約強制化的建置點）。

    = spec.reads ∪ dynamic_reads 經該步驟 params 解析（值為 str 取單鍵、list 取全部 str
    元素、其他型別忽略）∪（run_on_fatal 節點加 ENGINE_KEYS —— answer_composer/audit_feedback
    宣告了 trace/errors/fatal_error 為 reads，此處保底重複無害）。
    """
    reads: set[str] = set(spec.reads)
    for param in spec.dynamic_reads:
        value = params.get(param)
        if isinstance(value, str):
            reads.add(value)
        elif isinstance(value, list):
            reads.update(v for v in value if isinstance(v, str))
    if spec.run_on_fatal:
        reads |= skill_mod.ENGINE_KEYS
    return reads


class _Builder:
    def __init__(self, graph: StateGraph, deps: Any, allowed_tools: set[str]):
        self.g = graph
        self.deps = deps
        self.n = 0  # 節點步驟流水號（同一節點可在 flow 出現多次 → 圖上的 id 必須唯一）
        self.b = 0  # branch 流水號
        self.loops = 0  # loop 流水號（必須與 _scan 的走訪順序一致）
        self.llm_version = str(getattr(getattr(deps, "llm", None), "version", "") or "")
        self.allowed_tools = frozenset(allowed_tools)
        # ScriptRunnerPort 的注入點（規格 §5.4）：deps 給了就用 deps 的（v2 subprocess
        # 只要換這一個依賴，編譯器與 Skill 都不用動），沒給就用 v1 in-process。
        self.runner: ScriptRunnerPort = (
            getattr(deps, "script_runner", None) or RestrictedInProcessRunner()
        )

    def tool_context(self, state: dict) -> ToolContext:
        """身分一律取自 state 的保留鍵（伺服器注入、Script 寫不得）——不由 args 決定。"""
        return ToolContext(
            tenant_id=state.get("tenant_id", ""),
            user_id=state.get("user_id", ""),
            role=state.get("role", ""),
            deps=self.deps,
        )

    def add_node_step(self, ref: str, params: dict) -> str:
        spec = skill_mod.resolve_node(ref)
        if spec is None:  # _scan 已擋過，這裡是防呆
            raise SkillCompileError(f"unknown node: {ref}")
        self.n += 1
        node_id = f"n{self.n}_{spec.name}"
        self.g.add_node(
            node_id,
            harnessed(
                spec.name,  # trace 用契約名（Harness 的不可變鍵防護也認這個名字）
                spec.build(self.deps, **params),
                run_on_fatal=spec.run_on_fatal,
                # 呼叫 LLM 的節點（deps 含 llm）才在 trace 記模型版本
                component_version=self.llm_version if "llm" in spec.deps else "",
                writes=spec.writes,
                # 空 effective_reads（節點 reads=() 且無 dynamic_reads 解析、非 run_on_fatal）
                # → 傳 None（不過濾）。reads 在 @node 是可選、預設 ()（writes 才是必填），故
                # 空 reads 契約＝「未宣告」，比照 harnessed 的 reads=None 慣例不強制，避免把
                # 「沒宣告 read 契約」誤當成「宣告讀零鍵」而餓死節點的整個 state 視圖。
                reads=_node_reads(spec, params) or None,
            ),
        )
        return node_id

    def emit_steps(self, steps: list) -> tuple[str, str]:
        """回傳這段 flow 的 (入口節點 id, 出口節點 id)。"""
        entry: str | None = None
        prev_exit: str | None = None
        for step in steps:
            kind, body = skill_mod.parse_step(step)  # type: ignore[misc]
            step_entry, step_exit = self.emit_step(kind, body, step)
            if entry is None:
                entry = step_entry
            else:
                self.g.add_edge(prev_exit, step_entry)  # type: ignore[arg-type]
            prev_exit = step_exit
        if entry is None or prev_exit is None:
            raise SkillCompileError("flow 不可為空")
        return entry, prev_exit

    def add_script_step(self, source: str, step: dict) -> str:
        """script 步驟 → 經 Harness 包裝的節點（治理硬規則對 script 與對 node 是同一套）。"""
        contract = _script_contract(source)
        limits = _script_limits(step)
        runner = self.runner
        allowed = self.allowed_tools

        async def run_script(state: dict) -> dict:
            # trace：sha256 + 讀寫鍵名 + 耗時 + 狀態 + 錯誤類別（原始碼全文不落，規格 §5.3）
            describe(
                model=ScriptTraceEntry,
                script_sha256=contract.sha256,
                input_summary=",".join(contract.reads)[:200],
            )
            tools = ToolBag(
                ctx=self.tool_context(state),
                allowed=allowed,
                loop=asyncio.get_running_loop(),
                timeout_s=limits.timeout_ms / 1000,
            )
            # script 只看得到 public state（__ 前綴的引擎內部鍵不外流）
            return await runner.run(source, public_output(state), tools, limits)

        self.n += 1
        node_id = f"n{self.n}_script"
        # writes=None：script 的寫入白名單由 runner 自己把關（保留鍵／引擎鍵／__ 前綴一律
        # 剝除），Harness 的 IMMUTABLE_KEYS 防護是第二層（AT-GOV-03）。
        self.g.add_node(node_id, harnessed("script", run_script))
        return node_id

    def add_tool_step(self, name: str, step: dict) -> str:
        """tool 步驟 → 經 Harness 包裝的節點；結果寫入 save_as。"""
        name, save_as, args = _tool_step(name, step, self.allowed_tools)
        allowed = self.allowed_tools

        async def run_tool(state: dict) -> dict:
            resolved = {
                key: state.get(ref) if (ref := skill_mod.state_ref(value)) else value
                for key, value in args.items()
            }
            result = await tool_registry.invoke(
                name, self.tool_context(state), allowed, resolved, as_step=True
            )
            return {save_as: result}

        self.n += 1
        node_id = f"n{self.n}_tool"
        self.g.add_node(node_id, harnessed(f"tool:{name}", run_tool, writes=[save_as]))
        return node_id

    def emit_step(self, kind: str, body: Any, step: dict) -> tuple[str, str]:
        if kind == "node":
            node_id = self.add_node_step(body, step.get("params") or {})
            return node_id, node_id
        if kind == "sequence":
            return self.emit_steps(body)
        if kind == "branch":
            return self.emit_branch(body)
        if kind == "loop":
            return self.emit_loop(body)
        if kind == "script":
            node_id = self.add_script_step(body, step)
            return node_id, node_id
        if kind == "tool":
            node_id = self.add_tool_step(body, step)
            return node_id, node_id
        raise SkillCompileError(f"未知的步驟型別: {kind}")  # pragma: no cover

    def emit_branch(self, body: dict) -> tuple[str, str]:
        index = self.b
        self.b += 1
        key = _branch_key(index)
        decide = f"{INTERNAL_PREFIX}branch_{index}"
        join = f"{INTERNAL_PREFIX}join_{index}"
        when = body["when"]

        async def evaluate_when(state: dict) -> dict:
            # 條件式在「節點」裡求值而不是在 route 裡：條件式踩到 None < 0.7 這類
            # 求值錯誤時，route 沒有寫 state 的能力，例外會直接穿出 ainvoke → 500，
            # 稽核節點永遠跑不到。在節點裡求值 → 失敗走安全分支（else）並記一筆 errors，
            # 圖照樣流到強制附加的稽核節點（治理硬規則不因作者寫錯條件式而失效）。
            return _safe_eval(when, state, key, on_error=False, where=decide)

        self.g.add_node(decide, evaluate_when)
        self.g.add_node(join, _noop)

        then_entry, then_exit = self.emit_steps(body["then"])
        self.g.add_edge(then_exit, join)

        path_map = {"then": then_entry, "else": join}
        if body.get("else"):
            else_entry, else_exit = self.emit_steps(body["else"])
            self.g.add_edge(else_exit, join)
            path_map["else"] = else_entry

        self.g.add_conditional_edges(
            decide, lambda state: "then" if state.get(key) else "else", path_map
        )
        return decide, join

    def emit_loop(self, body: dict) -> tuple[str, str]:
        index = self.loops
        self.loops += 1
        key = _loop_key(index)
        until_key = _loop_exit_key(index)
        init = f"{INTERNAL_PREFIX}loop_{index}_init"
        tick = f"{INTERNAL_PREFIX}loop_{index}_tick"
        done = f"{INTERNAL_PREFIX}loop_{index}_exit"
        max_iterations: int = body["max_iterations"]
        until: str | None = body.get("until")

        async def enter(state: dict) -> dict:
            return {key: 0, until_key: False}

        async def count(state: dict) -> dict:
            out = {key: state.get(key, 0) + 1}
            if until is None:
                return out
            # 同 branch：until 求值失敗 → 安全離開迴圈（不是炸圖），並記一筆 errors
            return {**out, **_safe_eval(until, state, until_key, on_error=True, where=tick)}

        def route(state: dict) -> str:
            # 語意：先跑 body 再驗 until；達 max_iterations 強制離開（規格 §3.2）
            if state.get(key, 0) >= max_iterations or state.get(until_key):
                return "exit"
            return "loop"

        self.g.add_node(init, enter)
        self.g.add_node(tick, count)
        self.g.add_node(done, _noop)

        body_entry, body_exit = self.emit_steps(body["body"])
        self.g.add_edge(init, body_entry)
        self.g.add_edge(body_exit, tick)
        self.g.add_conditional_edges(tick, route, {"loop": body_entry, "exit": done})
        return init, done


def _ends_with_audit(flow: list) -> bool:
    """頂層 flow 的**最後一個**步驟是不是 `node: audit_feedback`。

    只認末端位置：flow 裡任何地方出現 audit_feedback 就不附加（原本的寫法）會被
    兩種形狀繞過 —— (a) 稽核寫在答案節點之前 → 稽核到的 state 沒有最終答案；
    (b) 稽核只寫在 branch 的其中一條分支 → 另一條終止路徑完全沒稽核。
    中途出現的 audit_feedback 一律當成普通節點，末端照樣再附加一顆。
    """
    parsed = skill_mod.parse_step(flow[-1]) if flow else None
    return (
        parsed is not None
        and parsed[0] == "node"
        and (spec := skill_mod.resolve_node(parsed[1])) is not None
        and spec.name == AUDIT_NODE
    )


AGENT_RUNNER_NODE = "agent_skill_runner"


def _build_agentic_graph(skill: Skill, deps: Any) -> CompiledStateGraph:
    """agentic 分派（設計 §4.1／§4.3）：單一 agent_skill_runner node + 強制附加的稽核。

    治理與 flow 完全一致：runner 由 harnessed 包裝（fatal 短路、例外安全、immutable 身分、
    declared-writes 剝除、trace），終端仍由既有規則串 audit_feedback。作者的 flow 不被讀取
    —— canonical definition 把 flow 視為 internal detail，package 作者無法注入 node contracts。
    """
    audit_spec = skill_mod.resolve_node(AUDIT_NODE)
    if audit_spec is None:
        raise SkillCompileError(f"稽核節點 {AUDIT_NODE} 未註冊")
    runner_spec = skill_mod.resolve_node(AGENT_RUNNER_NODE)
    if runner_spec is None:
        raise SkillCompileError(f"agentic runner 節點 {AGENT_RUNNER_NODE} 未註冊")

    reader = getattr(deps, "agent_package_reader", None)
    if reader is None:
        raise SkillCompileError(
            "agentic skill 需要 deps.agent_package_reader（package reader port 未注入）"
        )
    chat_model_factory = getattr(deps, "agent_chat_model", None)
    if chat_model_factory is None:
        raise SkillCompileError(
            "agentic skill 需要 deps.agent_chat_model（LLM factory 未注入）"
        )

    # state schema：runner（writes=answer）+ 強制附加的 audit 的 reads/writes 聯集
    scan = _Scan()
    scan.specs.append(runner_spec)

    g = StateGraph(build_state_schema(skill, scan, audit_spec))
    builder = _Builder(g, deps, set(skill.uses_tools))

    runner_fn = runner_spec.build(
        deps,
        reader=reader,
        chat_model_factory=chat_model_factory,
        container_deps=deps,
        skill_name=skill.name,
        uses_tools=list(skill.uses_tools),
        input_keys=tuple(skill.input_schema),
        timeout_s=skill.timeout_seconds,
    )
    # runner 呼叫 get_llm() → 與 flow LLM 節點一致，trace 記模型版本（觀測性，非行為）
    llm_version = str(getattr(getattr(deps, "llm", None), "version", "") or "")
    # runner 不經 add_node_step，effective_reads 必須顯式傳：身分三鍵（建 ToolContext）
    # ∪ runner 宣告的 reads ∪ input_schema（build_user_message 要讀使用者輸入，漏了就收不到）
    runner_reads = (
        set(IDENTITY_KEYS) | set(runner_spec.reads) | set(skill.input_schema)
    )
    g.add_node(
        AGENT_RUNNER_NODE,
        harnessed(
            runner_spec.name,
            runner_fn,
            run_on_fatal=runner_spec.run_on_fatal,
            component_version=llm_version,
            writes=runner_spec.writes,
            reads=runner_reads,
        ),
    )
    g.add_edge(START, AGENT_RUNNER_NODE)
    audit_id = builder.add_node_step(f"{AUDIT_NODE}@{audit_spec.version}", {})
    g.add_edge(AGENT_RUNNER_NODE, audit_id)
    g.add_edge(audit_id, END)
    return g.compile()


def _build_graph(skill: Skill, deps: Any) -> CompiledStateGraph:
    """實際建圖（快取未命中時才會被呼叫 —— AT2-28 以此為計數點）。"""
    if skill.kind == "agentic":
        return _build_agentic_graph(skill, deps)

    audit_spec = skill_mod.resolve_node(AUDIT_NODE)
    if audit_spec is None:  # 稽核節點必須存在，否則治理硬規則無從落實
        raise SkillCompileError(f"稽核節點 {AUDIT_NODE} 未註冊")

    tools = skill_mod.allowed_tools(skill)
    scan = _Scan(tools)
    _scan(skill.flow, scan)
    if not skill.flow:
        raise SkillCompileError("flow 不可為空")

    g = StateGraph(build_state_schema(skill, scan, audit_spec))
    builder = _Builder(g, deps, tools)
    entry, exit_ = builder.emit_steps(skill.flow)
    g.add_edge(START, entry)

    # 治理硬規則：稽核節點強制附加於終止路徑（branch/loop 的所有分支都匯流到 exit_）。
    if _ends_with_audit(skill.flow):
        g.add_edge(exit_, END)
    else:
        audit_id = builder.add_node_step(f"{AUDIT_NODE}@{audit_spec.version}", {})
        g.add_edge(exit_, audit_id)
        g.add_edge(audit_id, END)
    return g.compile()


def recursion_limit(skill: Skill) -> int:
    """全圖 recursion_limit 護欄（規格 §6.3-2）：由 flow 結構算出 superstep 上界。

    langgraph 的預設是 10007（不是舊版的 25），等於實務上沒有護欄：巢狀 loop 的
    superstep 數是各層上限相乘，失控時只會撞預設值 → GraphRecursionError → 500 →
    稽核不落地。這裡給的是「照定義最多可能跑幾步」＋固定寬裕量，超過即代表圖本身
    有問題（而不是某個輸入特別慢）。
    """

    def bound(steps: list) -> int:
        total = 0
        for step in steps or []:
            kind, body = skill_mod.parse_step(step)  # type: ignore[misc]
            if kind in ("node", "script", "tool"):
                total += 1
            elif kind == "sequence":
                total += bound(body)
            elif kind == "branch":
                total += 2 + max(bound(body["then"]), bound(body.get("else") or []))
            elif kind == "loop":
                total += 2 + body["max_iterations"] * (bound(body["body"]) + 1)
        return total

    return bound(skill.flow) + 10  # +10：START/END、強制附加的稽核節點與寬裕量


# ---------------------------------------------------------------------------
# 編譯快取（規格 §4：同一 skill revision 只編譯一次）
# ---------------------------------------------------------------------------

# 值一併留住 deps：快取鍵含 id(deps)，若 deps 被回收、id 被重用，就會拿到用別組依賴
# 建的圖（測試的假依賴 vs 正式依賴）。持有參考即杜絕；FIFO 上限保證不會無限成長
# （被淘汰的 entry 連圖帶 deps 一起釋放，該 id 之後即使重用也不會有殘留的快取命中）。
# ponytail: FIFO 而非 LRU —— skill 數量級是「幾十」，換 LRU 只是多寫一行 move_to_end，
# 等到熱門 skill 被冷門 skill 擠掉真的變成問題再說。
_CACHE_MAX = 32
_CACHE: OrderedDict[tuple, tuple[Any, CompiledStateGraph]] = OrderedDict()


def cache_key(skill: Skill) -> tuple[str, int, str]:
    """(name, revision, 定義內容 sha256)：revision 沒 bump 但內容改了也會重編。"""
    digest = hashlib.sha256(
        skill.model_dump_json(exclude={"revision"}).encode("utf-8")
    ).hexdigest()
    return (skill.name, skill.revision, digest)


def compile(skill: Skill, deps: Any = None) -> CompiledStateGraph:
    """Skill → CompiledStateGraph；同 revision + 同內容 + 同依賴第二次呼叫直接命中快取。"""
    key = (*cache_key(skill), id(deps))
    cached = _CACHE.get(key)
    if cached is None:
        cached = (deps, _build_graph(skill, deps))
        _CACHE[key] = cached
        while len(_CACHE) > _CACHE_MAX:
            _CACHE.popitem(last=False)
    return cached[1]
