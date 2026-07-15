"""Skill 定義的 schema 與靜態驗證器（規格 §3）。

為什麼 flow 不做成 Pydantic 的 discriminated union：驗證器要回的是**錯誤清單**
（`{valid, errors[{code, message, line}]}`，前端編輯器逐條標紅），不是「第一個
錯誤就 raise」。Pydantic 的錯誤訊息無法對應規格 §3.4 的錯誤碼表，所以 flow 以
`list[dict]` 收下，型別與語意檢查全部走 validate_definition() 自己走一遍樹。

錯誤碼一律照規格 §3.4 逐字：unknown_node / unknown_tool / unbounded_loop /
invalid_expression / forbidden_script / dataflow_error（**警告級**：有這條 error
但 valid 仍為 true）/ invalid_flow。YAML 解析失敗也收斂成 invalid_flow —— 驗證端點
不得因為輸入是壞 YAML 就拋未捕捉例外。
"""

import re
from types import UnionType
from typing import Any, Literal, Union, get_args, get_origin

import yaml
from pydantic import BaseModel, Field, ValidationError

from app.engine import expressions, node_registry, script_runner, tool_registry
from app.engine.harness import IDENTITY_KEYS, IMMUTABLE_KEYS

# 錯誤碼（規格 §3.4）。
UNKNOWN_NODE = "unknown_node"
UNKNOWN_TOOL = "unknown_tool"
UNBOUNDED_LOOP = "unbounded_loop"
INVALID_EXPRESSION = "invalid_expression"
FORBIDDEN_SCRIPT = "forbidden_script"
DATAFLOW_ERROR = "dataflow_error"
INVALID_FLOW = "invalid_flow"

# 只有 dataflow_error 是警告級：它是「可能忘了前置步驟」的提示，不阻擋存檔。
WARNING_CODES = frozenset({DATAFLOW_ERROR})

STEP_TYPES = ("node", "sequence", "branch", "loop", "script", "tool")

# loop 的硬上限（規格 §3.2）：1~10，缺或超界 → unbounded_loop
LOOP_MIN, LOOP_MAX = 1, 10

# 由伺服器依 RequestContext 注入、Skill 不必宣告也不可經 input 覆蓋的鍵，
# 以及 Query Intake 建立的不可變鍵：資料流檢查時視為「一定有」。
RESERVED_KEYS = frozenset(IDENTITY_KEYS | IMMUTABLE_KEYS)
# 由 Harness 寫入的引擎鍵
ENGINE_KEYS = frozenset(node_registry.ENGINE_KEYS)

_TYPES: dict[str, type] = {
    "str": str,
    "int": int,
    "float": float,
    "bool": bool,
    "list": list,
    "dict": dict,
}


class InputField(BaseModel):
    """input_schema 的單一欄位宣告（規格 §3.1）。"""

    type: Literal["str", "int", "float", "bool", "list", "dict"] = "str"
    required: bool = False
    min_length: int | None = None
    default: Any = None


class Skill(BaseModel):
    """Skill 定義（YAML 為權威格式，這裡是解析後的結構）。"""

    name: str = Field(pattern=r"^[a-z][a-z0-9_]{2,63}$")
    description: str = ""
    required_role: Literal["USER", "ADMIN"] = "USER"
    input_schema: dict[str, InputField] = Field(default_factory=dict)
    timeout_seconds: int | None = None
    uses_tools: list[str] = Field(default_factory=list)  # P3
    revision: int = 1
    flow: list[dict[str, Any]] = Field(default_factory=list)


class SkillError(BaseModel):
    """單一驗證錯誤：code 照規格 §3.4，line 為 YAML 行號（找得到時）。"""

    code: str
    message: str
    line: int | None = None


class SkillMeta(BaseModel):
    """驗證通過時一併回報的 skill 中繼資料。

    backend 不裝 YAML parser：它要寫進 DB 的 name/description/required_role 全部來自這裡，
    這份回應是它唯一的資料來源。input_schema 則是前端動態渲染執行表單的依據。
    """

    name: str
    description: str
    required_role: str
    input_schema: dict[str, InputField] = Field(default_factory=dict)


class ValidationResult(BaseModel):
    """POST /skills/validate 的回應：valid + errors[] + （通過時才有的）skill 中繼資料。

    skill 只在 valid=true 時出現 —— backend 以「有沒有 skill 欄位」決定能不能寫入，
    valid=false 卻帶著中繼資料，等於給了一條繞過驗證閘門的路。
    """

    valid: bool
    errors: list[SkillError] = Field(default_factory=list)
    skill: SkillMeta | None = None


class _Validator:
    """一次驗證的狀態容器（錯誤清單 + 資料流可用鍵集合）。"""

    def __init__(self, source: str | None, allowed_tools: set[str] | None = None):
        self.source = source or ""
        self.errors: list[SkillError] = []
        # Skill 可呼叫的 tool 白名單：uses_tools + flow 內宣告的 tool 步驟
        self.allowed_tools: set[str] = allowed_tools or set()

    def add(self, code: str, message: str, token: str | None = None) -> None:
        self.errors.append(SkillError(code=code, message=message, line=self._line(token)))

    def _line(self, token: str | None) -> int | None:
        """在 YAML 原文找 token 第一次出現的行號；找不到（或沒原文）→ None。"""
        if not token or not self.source:
            return None
        for i, line in enumerate(self.source.splitlines(), start=1):
            if token in line:
                return i
        return None


def parse_step(step: dict[str, Any]) -> tuple[str, Any] | None:
    """取出步驟的型別與內容；不是「恰好一個已知型別鍵」的 dict → None。"""
    if not isinstance(step, dict):
        return None
    kinds = [k for k in step if k in STEP_TYPES]
    if len(kinds) != 1:
        return None
    return kinds[0], step[kinds[0]]


STATE_REF_PREFIX = "$state."


def state_ref(value: Any) -> str | None:
    """`$state.<key>` → key（tool 步驟的 args 引用，規格 §3.2）；一般值 → None。"""
    if isinstance(value, str) and value.startswith(STATE_REF_PREFIX):
        return value[len(STATE_REF_PREFIX) :]
    return None


def resolve_node(ref: str) -> node_registry.NodeSpec | None:
    """解析 `name` 或 `name@version` 為 NodeSpec；查無回 None。"""
    if not isinstance(ref, str):
        return None
    name, _, version = ref.partition("@")
    return node_registry.get(name, version=version or None)


def writable_key(key: Any) -> bool:
    """Skill／Script 可寫入的 state 鍵：不是保留鍵／引擎鍵，也不是 __ 前綴的引擎內部鍵。"""
    return (
        isinstance(key, str)
        and key.isidentifier()
        and not key.startswith("__")
        and key not in RESERVED_KEYS
        and key not in ENGINE_KEYS
    )


def _step_writes(steps: list, out: set[str]) -> None:
    """收集一段 flow（含巢狀）內所有步驟的 writes —— loop body 的資料流前置種子。"""
    for step in steps or []:
        parsed = parse_step(step)
        if parsed is None:
            continue
        kind, body = parsed
        if kind == "node":
            spec = resolve_node(body)
            if spec is not None:
                out.update(spec.writes)
        elif kind == "script":
            try:
                out.update(script_runner.scan(body).writes)
            except script_runner.ScriptViolation:
                pass  # 違規的 script 由 _check_script 報 forbidden_script
        elif kind == "tool":
            save_as = step.get("save_as") if isinstance(step, dict) else None
            if isinstance(save_as, str):
                out.add(save_as)
        elif kind == "sequence":
            _step_writes(body if isinstance(body, list) else [], out)
        elif kind == "branch" and isinstance(body, dict):
            _step_writes(body.get("then") or [], out)
            _step_writes(body.get("else") or [], out)
        elif kind == "loop" and isinstance(body, dict):
            _step_writes(body.get("body") or [], out)


def collect_tools(steps: list) -> set[str]:
    """收集 flow（含巢狀）內所有 `tool:` 步驟引用的 tool 名。"""
    found: set[str] = set()
    for step in steps or []:
        parsed = parse_step(step)
        if parsed is None:
            continue
        kind, body = parsed
        if kind == "tool" and isinstance(body, str):
            found.add(body)
        elif kind == "sequence" and isinstance(body, list):
            found |= collect_tools(body)
        elif kind == "branch" and isinstance(body, dict):
            found |= collect_tools(body.get("then") or [])
            found |= collect_tools(body.get("else") or [])
        elif kind == "loop" and isinstance(body, dict):
            found |= collect_tools(body.get("body") or [])
    return found


def _check_steps(steps: Any, v: _Validator, available: set[str]) -> None:
    """遞迴檢查步驟清單；available 為「此處已可取得的 state 鍵」（資料流檢查用）。"""
    if not isinstance(steps, list) or not steps:
        v.add(INVALID_FLOW, "步驟清單不可為空")
        return

    for step in steps:
        parsed = parse_step(step)
        if parsed is None:
            keys = list(step) if isinstance(step, dict) else [type(step).__name__]
            v.add(
                INVALID_FLOW,
                f"未知的步驟型別: {keys}（只允許 {', '.join(STEP_TYPES)}）",
                token=str(keys[0]) if keys else None,
            )
            continue
        kind, body = parsed

        if kind == "node":
            _check_node(step, body, v, available)
        elif kind == "sequence":
            _check_steps(body, v, available)
        elif kind == "branch":
            _check_branch(body, v, available)
        elif kind == "loop":
            _check_loop(body, v, available)
        elif kind == "script":
            _check_script(step, body, v, available)
        elif kind == "tool":
            _check_tool(step, body, v, available)


def _check_node(step: dict, ref: Any, v: _Validator, available: set[str]) -> None:
    spec = resolve_node(ref)
    if spec is None:
        v.add(UNKNOWN_NODE, f"未註冊的節點: {ref}", token=str(ref))
        return

    params = step.get("params") or {}
    # 節點自己寫入的鍵不算缺前置（read-modify-write：query_intake 讀 max_retrieval_attempts
    # 並寫入預設值、retrieval_planner 讀 retrieval_attempt 並 +1，都不是「忘了前置步驟」）
    #
    # ponytail: 這條規則（連同 _check_loop 把 body 的 writes 整段預先視為可用、_check_branch
    # 把兩條分支的 writes 一律併回）是刻意的**寬鬆**：會漏報「只有第二輪才有值」「只有另一條
    # 分支才寫」的鍵。收緊 → kb_query 這種 read-modify-write / 迴圈回饋的正常形狀會噴一堆假警告，
    # 而 dataflow_error 是警告級、不阻擋存檔，假陽性的代價比假陰性高。升級路徑：真要精確就得做
    # 「必經路徑（must-reach）分析」——每個鍵記錄它在哪些路徑上必然被寫入，而不是現在的單一集合。
    known = available | set(spec.writes)
    for key in spec.reads:
        if key not in known:
            v.add(
                DATAFLOW_ERROR,
                f"節點 {spec.name} 讀取的鍵 '{key}' 無前置步驟寫入，也不在 input_schema／保留鍵",
                token=str(ref),
            )
    for param in spec.dynamic_reads:
        key = params.get(param)
        if not isinstance(key, str):
            v.add(
                DATAFLOW_ERROR,
                f"節點 {spec.name} 需要 params.{param} 指定要讀取的 state 鍵",
                token=str(ref),
            )
        elif key not in known:
            v.add(
                DATAFLOW_ERROR,
                f"節點 {spec.name} 讀取的鍵 '{key}'（來自 params.{param}）無前置步驟寫入",
                token=str(ref),
            )
    available.update(spec.writes)


def _check_script(step: dict, source: Any, v: _Validator, available: set[str]) -> None:
    """script 步驟（規格 §3.2／§5）：AST 白名單 + timeout_ms 範圍 + 資料流。"""
    if not isinstance(source, str) or not source.strip():
        v.add(INVALID_FLOW, "script 必須是非空的 Python 原始碼字串", token="script")
        return

    timeout_ms = step.get("timeout_ms", script_runner.DEFAULT_TIMEOUT_MS)
    if (
        not isinstance(timeout_ms, int)
        or isinstance(timeout_ms, bool)
        or not 1 <= timeout_ms <= script_runner.MAX_TIMEOUT_MS
    ):
        v.add(
            INVALID_FLOW,
            f"script 的 timeout_ms={timeout_ms} 超出允許範圍 "
            f"1~{script_runner.MAX_TIMEOUT_MS}",
            token="timeout_ms",
        )

    try:
        contract = script_runner.scan(source)
    except script_runner.ScriptViolation as e:
        # 白名單外的語法（import / exec / dunder / while …）→ 存檔拒絕
        v.add(FORBIDDEN_SCRIPT, f"script: {e}", token=source.strip().splitlines()[0])
        return

    for name in contract.tools:
        if name not in v.allowed_tools or tool_registry.get(name) is None:
            v.add(
                UNKNOWN_TOOL,
                f"script 呼叫的 tool '{name}' 未註冊或不在 uses_tools 清單中",
                token=name,
            )
    for key in contract.reads:
        if key not in available:
            v.add(
                DATAFLOW_ERROR,
                f"script 讀取的鍵 '{key}' 無前置步驟寫入，也不在 input_schema／保留鍵",
                token=key,
            )
    available.update(contract.writes)


def _check_tool(step: dict, name: Any, v: _Validator, available: set[str]) -> None:
    """tool 步驟（規格 §3.2／§6）：tool 已註冊 + args 的 $state 引用 + save_as 合法。"""
    if not isinstance(name, str) or tool_registry.get(name) is None:
        v.add(UNKNOWN_TOOL, f"未註冊的 tool: {name}", token=str(name))
        return

    args = step.get("args") or {}
    if not isinstance(args, dict):
        v.add(INVALID_FLOW, "tool 的 args 必須是對應（mapping）", token="args")
        args = {}
    for value in args.values():
        key = state_ref(value)
        if key is not None and key not in available:
            v.add(
                DATAFLOW_ERROR,
                f"tool {name} 的 args 引用的鍵 '{key}' 無前置步驟寫入",
                token=str(value),
            )

    save_as = step.get("save_as")
    if not writable_key(save_as):
        v.add(
            INVALID_FLOW,
            f"tool 步驟的 save_as 必須是合法的 state 鍵（不可為保留鍵／__ 前綴）: {save_as!r}",
            token="save_as",
        )
        return
    available.add(save_as)


def _check_expression(expr: Any, v: _Validator, field: str) -> None:
    if not isinstance(expr, str) or not expr.strip():
        v.add(INVALID_EXPRESSION, f"{field} 必須是非空條件式字串", token=field)
        return
    try:
        expressions.validate(expr)
    except expressions.ExpressionError as e:
        v.add(INVALID_EXPRESSION, f"{field}: {e}", token=expr.strip().splitlines()[0])


def _check_branch(body: Any, v: _Validator, available: set[str]) -> None:
    if not isinstance(body, dict):
        v.add(INVALID_FLOW, "branch 必須是含 when / then 的對應", token="branch")
        return
    _check_expression(body.get("when"), v, "when")
    # then / else 各自從同一份 available 出發；之後把兩邊的 writes 都併回來
    # （警告級檢查取寬鬆解讀：只在某一分支寫入的鍵仍視為可用）
    _check_steps(body.get("then"), v, set(available))
    if body.get("else") is not None:
        _check_steps(body.get("else"), v, set(available))
    writes: set[str] = set()
    _step_writes(body.get("then") or [], writes)
    _step_writes(body.get("else") or [], writes)
    available.update(writes)


def _check_loop(body: Any, v: _Validator, available: set[str]) -> None:
    if not isinstance(body, dict):
        v.add(INVALID_FLOW, "loop 必須是含 max_iterations / body 的對應", token="loop")
        return

    max_iterations = body.get("max_iterations")
    if not isinstance(max_iterations, int) or isinstance(max_iterations, bool):
        v.add(
            UNBOUNDED_LOOP,
            f"loop 缺少 max_iterations（必填，{LOOP_MIN}~{LOOP_MAX}）",
            token="loop",
        )
    elif not LOOP_MIN <= max_iterations <= LOOP_MAX:
        v.add(
            UNBOUNDED_LOOP,
            f"loop 的 max_iterations={max_iterations} 超出允許範圍 {LOOP_MIN}~{LOOP_MAX}",
            token="max_iterations",
        )

    if body.get("until") is not None:
        _check_expression(body.get("until"), v, "until")

    # 迴圈 body 內的鍵互為前置：第二輪起，body 後段節點寫的鍵前段節點讀得到
    # （retrieval_planner 讀 evidence_verification 寫的 failure_codes 就是這個形狀）
    seed: set[str] = set()
    _step_writes(body.get("body") or [], seed)
    inner = available | seed
    _check_steps(body.get("body"), v, inner)
    available.update(seed)


def allowed_tools(skill: Skill) -> set[str]:
    """Skill 可呼叫的 tool 白名單：uses_tools + flow 內宣告的 tool 步驟。

    tool 步驟在 flow 裡是明擺著的（存檔時看得見、稽核時看得見），要求作者再去 uses_tools
    抄一次沒有增加任何保證。uses_tools 真正的作用是**約束 script 內的動態呼叫**
    （script 的 tools.call 名字可能是變數，靜態看不出來）——那才是需要白名單的地方。
    """
    return set(skill.uses_tools) | collect_tools(skill.flow)


def validate_definition(data: Any, source: str | None = None) -> ValidationResult:
    """對已解析的 skill 定義跑全部靜態驗證（規格 §3.4）。"""
    v = _Validator(source)

    if not isinstance(data, dict):
        v.add(INVALID_FLOW, "skill 定義必須是 YAML 對應（mapping）")
        return _result(v)

    try:
        skill = Skill.model_validate(data)
    except ValidationError as e:
        v.add(INVALID_FLOW, f"skill 定義不符合 schema: {e}")
        return _result(v)

    v.allowed_tools = allowed_tools(skill)
    for name in skill.uses_tools:
        if tool_registry.get(name) is None:
            v.add(UNKNOWN_TOOL, f"未註冊的 tool: {name}", token=str(name))

    available = set(RESERVED_KEYS) | set(ENGINE_KEYS) | set(skill.input_schema)
    _check_steps(skill.flow, v, available)
    return _result(v, skill)


def validate_source(source: str) -> ValidationResult:
    """對 YAML 原文跑全部靜態驗證；解析失敗收斂成 invalid_flow，不拋未捕捉例外。"""
    try:
        data = yaml.safe_load(source)
    except yaml.YAMLError as e:
        mark = getattr(e, "problem_mark", None)
        return ValidationResult(
            valid=False,
            errors=[
                SkillError(
                    code=INVALID_FLOW,
                    message=f"YAML 解析失敗: {e}",
                    line=(mark.line + 1) if mark is not None else None,
                )
            ],
        )
    return validate_definition(data, source=source)


def parse_source(source: str) -> Skill:
    """YAML 原文 → Skill；語法或 schema 有問題直接 raise（載入內建 skill 時 fail fast）。"""
    return Skill.model_validate(yaml.safe_load(source))


def _result(v: _Validator, skill: Skill | None = None) -> ValidationResult:
    blocking = [e for e in v.errors if e.code not in WARNING_CODES]
    valid = not blocking
    meta = (
        SkillMeta(
            name=skill.name,
            description=skill.description,
            required_role=skill.required_role,
            input_schema=skill.input_schema,
        )
        if valid and skill is not None
        else None
    )
    return ValidationResult(valid=valid, errors=v.errors, skill=meta)


def build_input_model(skill: Skill) -> type[BaseModel] | None:
    """input_schema → 動態 Pydantic model（invoke 的 422 驗證用）；無宣告則回 None。"""
    if not skill.input_schema:
        return None

    from typing import Annotated, Optional

    from pydantic import create_model

    fields: dict[str, Any] = {}
    for key, field in skill.input_schema.items():
        # 約束掛在內層型別上（不是 Optional 外層）：pydantic 無法把 min_length 套到 NoneType
        annotation: Any = _TYPES[field.type]
        if field.min_length is not None:
            annotation = Annotated[annotation, Field(min_length=field.min_length)]
        if field.required:
            fields[key] = (annotation, ...)
        else:
            fields[key] = (Optional[annotation], field.default)
    model_name = "".join(p.capitalize() for p in re.split(r"[^a-z0-9]+", skill.name))
    return create_model(f"{model_name}Input", **fields)


def schema_from_model(model: type[BaseModel] | None) -> dict[str, InputField] | None:
    """Pydantic model → input_schema（build_input_model 的反向；規格 §7.1「清單可標註」）。

    為什麼需要反向轉：前端的執行表單改成依 input_schema 動態渲染後，`@register` 的舊工作流
    因為清單沒有這個欄位，全部退成 JSON textarea。WorkflowSpec 本來就持有 input_model
    （唯一事實來源），在清單端點轉成同一形狀即可 —— 不必在四個工作流模組各抄一份 schema，
    也不會出現「schema 改了但表單沒改」的兩份事實。

    對應不到 InputField 六種型別的欄位（巢狀 model、Literal…）→ 整份回 None：
    寧可讓前端安全退回 JSON textarea，也不要渲染出一個型別是猜的表單。
    """
    if model is None:
        return None

    by_type = {t: n for n, t in _TYPES.items()}
    schema: dict[str, InputField] = {}
    for key, field in model.model_fields.items():
        annotation = field.annotation
        if get_origin(annotation) in (Union, UnionType):
            args = [a for a in get_args(annotation) if a is not type(None)]
            annotation = args[0] if len(args) == 1 else None
        type_name = by_type.get(get_origin(annotation) or annotation)
        if type_name is None:
            return None
        schema[key] = InputField(
            type=type_name,
            required=field.is_required(),
            # min_length 由 annotated_types.MinLen 帶進 metadata（Field(min_length=1)）
            min_length=next(
                (m.min_length for m in field.metadata if hasattr(m, "min_length")), None
            ),
            default=None if field.is_required() else field.default,
        )
    return schema or None
