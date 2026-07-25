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
from typing import Any, Literal

import yaml
from pydantic import BaseModel, Field, ValidationError, field_validator, model_validator

# 標準 name 規則（agentskills.io）：小寫英數 + 連字號;不可首尾連字號;可數字開頭;1–64 字。
# `--` 的排除在 field_validator 內另做（pydantic v2 rust-regex 不支援負向前瞻）。
_NAME_RE = re.compile(r"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")

from app.engine import expressions, node_registry, script_runner, tool_registry
from app.engine.harness import (
    CONFIG_SEED_KEYS,
    IDENTITY_KEYS,
    IMMUTABLE_KEYS,
    RUNTIME_AUTHORITY_KEYS,
)

# 錯誤碼（規格 §3.4）。
UNKNOWN_NODE = "unknown_node"
UNKNOWN_TOOL = "unknown_tool"
UNBOUNDED_LOOP = "unbounded_loop"
INVALID_EXPRESSION = "invalid_expression"
FORBIDDEN_SCRIPT = "forbidden_script"
DATAFLOW_ERROR = "dataflow_error"
INVALID_FLOW = "invalid_flow"
# schema 層級錯誤依欄位分流（規格 UX 修正）：name 欄位的 pattern/length/type 錯 → invalid_name
# （面向非技術使用者的名稱規則訊息），其餘欄位 → invalid_schema。invalid_flow 只留給
# 真正的 flow 問題（空 flow、未知步驟型別、YAML 解析失敗），標籤才名實相符。
INVALID_NAME = "invalid_name"
INVALID_SCHEMA = "invalid_schema"
# 定義端點只收 flow：authored YAML 宣告 kind: agentic 卻沒有 package（無 script/資源）是漂移，
# 存進去會在 P1 變成 active break。agentic 只能經 /skills/validate-package 匯入。
AGENTIC_REQUIRES_IMPORT = "agentic_requires_import"

# 只有 dataflow_error 是警告級：它是「可能忘了前置步驟」的提示，不阻擋存檔。
WARNING_CODES = frozenset({DATAFLOW_ERROR})

STEP_TYPES = ("node", "sequence", "branch", "loop", "script", "tool")

# loop 的硬上限（規格 §3.2）：1~10，缺或超界 → unbounded_loop
LOOP_MIN, LOOP_MAX = 1, 10

# 由伺服器依 RequestContext 注入、Skill 不必宣告也不可經 input 覆蓋的鍵，
# 以及 Query Intake 建立的不可變鍵、invoke 期注入的 Configuration Set 執行參數（縫⑦）：
# 資料流檢查時視為「一定有」。
RESERVED_KEYS = frozenset(
    IDENTITY_KEYS | IMMUTABLE_KEYS | CONFIG_SEED_KEYS | RUNTIME_AUTHORITY_KEYS
)
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

    @model_validator(mode="after")
    def _check_min_length_type(self) -> "InputField":
        # min_length 只有 str/list/dict 有「至少 N 個字／項」語意。掛在 int/float/bool 上
        # pydantic 無法套用，寫入期看似合法、每次 invoke 卻必炸（死欄位）——寫入期就擋掉，
        # 經 validate_definition 的 ValidationError 分流成 invalid_schema 阻擋級錯誤。
        if self.min_length is not None and self.type in ("int", "float", "bool"):
            raise ValueError("min_length 只適用於 str/list/dict 型別")
        return self


class Skill(BaseModel):
    """Skill 定義（YAML 為權威格式，這裡是解析後的結構）。"""

    # 標準 name 規則（agentskills.io）：小寫英數 + 連字號;不可首尾／連續連字號;可數字開頭;
    # 1–64 字。底線不再合法。用 field_validator（非 Field pattern）—— pydantic v2 的
    # rust-regex 引擎不支援負向前瞻,連續連字號 `--` 的排除改在 Python re 這裡做。
    name: str
    description: str = ""
    # kind 為 additive，預設 flow：既有 flow 定義不帶此欄位仍解析為 flow，所有 flow
    # parse/validate/compile 路徑行為不變。agentic 只由 package 匯入設定（P0 package parser）。
    kind: Literal["flow", "agentic"] = "flow"
    required_role: Literal["USER", "ADMIN"] = "USER"
    input_schema: dict[str, InputField] = Field(default_factory=dict)
    timeout_seconds: int | None = None
    uses_tools: list[str] = Field(default_factory=list)  # P3
    revision: int = 1
    flow: list[dict[str, Any]] = Field(default_factory=list)

    @field_validator("name")
    @classmethod
    def _check_name(cls, v: str) -> str:
        # 白名單、預設拒絕：長度 1–64、符合標準字元集、且不含連續 `--`。任一不符即拒。
        if not (1 <= len(v) <= 64) or not _NAME_RE.match(v) or "--" in v:
            raise ValueError(
                "名稱只能用小寫英文、數字、連字號（-），不可首尾或連續連字號（1–64 字）"
            )
        return v


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
    kind: Literal["flow", "agentic"] = "flow"
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

    def __init__(
        self,
        source: str | None,
        allowed_tools: set[str] | None = None,
        author_role: str | None = None,
    ):
        self.source = source or ""
        self.errors: list[SkillError] = []
        # Skill 可呼叫的 tool 白名單：uses_tools + flow 內宣告的 tool 步驟
        self.allowed_tools: set[str] = allowed_tools or set()
        # 撰寫者角色（保守版 script 撰寫 gate）：只有驗證端點會帶入呼叫者真實角色；
        # None → 不做撰寫權限檢查（custom.load 事後驗證、內建 template 驗證皆走此路，
        # 既有 USER+script 自訂 skill 不追溯失效）。
        self.author_role: str | None = author_role

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


def parse_step(step: Any) -> tuple[str, Any] | None:
    """取出步驟的型別與內容；不是「恰好一個已知型別鍵」的 dict → None。

    step 型別標 Any：呼叫端傳的是 YAML 解析結果（可能不是 dict），下面的 isinstance
    是真實的執行期守衛，不是死碼。
    """
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


def resolve_node(ref: Any) -> node_registry.NodeSpec | None:
    """解析 `name` 或 `name@version` 為 NodeSpec；查無回 None。

    ref 型別標 Any：來源是 YAML 解析值（可能不是 str），isinstance 是執行期守衛。
    """
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
        # dynamic_reads 的值可能是單鍵（retrieve 的 query_key）或鍵清單（nl_logic/nl_extract
        # 的 input_keys: [docs]）——list[str] 逐項做資料流檢查，str 維持單鍵檢查，其他型別
        # （None、非 str 元素…）才報「未指定」。
        if isinstance(key, str):
            keys = [key]
        elif isinstance(key, list) and all(isinstance(k, str) for k in key):
            keys = key
        else:
            v.add(
                DATAFLOW_ERROR,
                f"節點 {spec.name} 需要 params.{param} 指定要讀取的 state 鍵",
                token=str(ref),
            )
            continue
        for k in keys:
            if k not in known:
                v.add(
                    DATAFLOW_ERROR,
                    f"節點 {spec.name} 讀取的鍵 '{k}'（來自 params.{param}）無前置步驟寫入",
                    token=str(ref),
                )
    available.update(spec.writes)


def _check_script(step: dict, source: Any, v: _Validator, available: set[str]) -> None:
    """script 步驟（規格 §3.2／§5）：撰寫者角色 gate + AST 白名單 + timeout_ms 範圍 + 資料流。"""
    if not isinstance(source, str) or not source.strip():
        v.add(INVALID_FLOW, "script 必須是非空的 Python 原始碼字串", token="script")
        return

    # 撰寫者角色 gate（保守版，只在驗證端點生效）：script 步驟的沙箱自陳「authoring 限
    # ADMIN」，這裡在寫入前把它強制起來 —— 非 ADMIN 作者提交含 script 的定義即驗證失敗。
    # 不改 required_role（管誰能呼叫）語意，也不影響 invoke／custom.load（author_role=None）。
    if v.author_role is not None and v.author_role != "ADMIN":
        v.add(
            FORBIDDEN_SCRIPT,
            "script 步驟僅限 ADMIN 撰寫",
            token=source.strip().splitlines()[0],
        )

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
    # writable_key(save_as) 為真已保證 save_as 是 str（isidentifier 等檢查）
    available.add(save_as)  # pyright: ignore[reportArgumentType]


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


def validate_definition(
    data: Any, source: str | None = None, author_role: str | None = None
) -> ValidationResult:
    """對已解析的 skill 定義跑全部靜態驗證（規格 §3.4）。

    author_role 帶入時（驗證端點）啟用 script 撰寫者角色 gate：非 ADMIN 提交含 script 的
    定義即失敗。None（invoke／custom.load／內建載入）→ 不做撰寫權限檢查。
    """
    v = _Validator(source, author_role=author_role)

    if not isinstance(data, dict):
        v.add(INVALID_FLOW, "skill 定義必須是 YAML 對應（mapping）")
        return _result(v)

    try:
        skill = Skill.model_validate(data)
    except ValidationError as e:
        # 依 loc 首元素（欄位名）分流，不再把所有 schema 錯都塞成 invalid_flow，也不外洩
        # Pydantic 的原始多行 dump / pydantic.dev URL。
        for err in e.errors():
            loc = err.get("loc") or ()
            field = str(loc[0]) if loc else ""
            if field == "name":
                v.add(
                    INVALID_NAME,
                    "名稱只能用小寫英文、數字、連字號（-），不可首尾或連續連字號（1–64 字）；中文請放在說明",
                    token="name",
                )
            else:
                v.add(
                    INVALID_SCHEMA,
                    f"欄位 {field} 格式不正確" if field else "skill 定義格式不正確",
                    token=field or None,
                )
        return _result(v)

    # 定義端點是 flow-only 的權威閘門：kind: agentic 需以 package 匯入（含 script/資源），
    # 定義原文自稱 agentic 是漂移 —— 拒絕存檔，不落 meta（valid=false）。
    if skill.kind == "agentic":
        v.add(
            AGENTIC_REQUIRES_IMPORT,
            "agentic skill 不可由定義原文建立，必須以 package 匯入",
            token="agentic",
        )
        return _result(v)

    v.allowed_tools = allowed_tools(skill)
    for name in skill.uses_tools:
        if tool_registry.get(name) is None:
            v.add(UNKNOWN_TOOL, f"未註冊的 tool: {name}", token=str(name))

    available = set(RESERVED_KEYS) | set(ENGINE_KEYS) | set(skill.input_schema)
    _check_steps(skill.flow, v, available)
    return _result(v, skill)


def validate_source(source: str, author_role: str | None = None) -> ValidationResult:
    """對 YAML 原文跑全部靜態驗證；解析失敗收斂成 invalid_flow，不拋未捕捉例外。

    author_role 帶入時啟用 script 撰寫者角色 gate（見 validate_definition）；None → 不檢查。
    """
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
    return validate_definition(data, source=source, author_role=author_role)


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
            kind=skill.kind,
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
