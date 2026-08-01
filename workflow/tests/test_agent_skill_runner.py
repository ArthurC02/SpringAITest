"""agentic runner 的 P1 驗收測試（AST-P1-001..010、014 的護欄由既有 suite 承擔）。

核心不變式：agentic 自主性活在一顆受 Harness 管理的 node 內，不是頂層 orchestrator。
因此逐項以可觀察行為驗收 allowlist、身分、稽核、輸出契約、step/timeout 界限與租戶隔離。
無 mocking library：LLM 是確定性 ScriptedChatModel，tool 是手寫 @tool + recorder。
"""

import ast
import asyncio
import hashlib
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from langchain_core.language_models.chat_models import BaseChatModel

from app.engine import compiler, tool_registry
from app.engine.node_shell import harnessed
from app.engine.package_reader import MemoryPackageReader
from app.engine.tool_registry import ToolContext
from app.main import app
from app.nodes import agent_skill_runner
from app.nodes.agent_skill_runner import (
    AGENT_RECURSION_LIMIT,
    MAX_TOOL_ROUNDS,
    RESOURCE_MAX_BYTES,
    make_registry_tool,
    make_resource_tool,
    read_resource,
)
from app.settings import settings
from app.skills import custom
from tests.agentic_fakes import (
    make_agentic_deps,
    make_agentic_skill,
    make_package,
    model_factory,
    run_agentic,
)
from tests.conftest import auth_headers as _headers, install_fake_get
from tests.kbquery_fakes import RecordingAuditRepo

# ---------------------------------------------------------------------------
# 測試用 tool（手寫 @tool + module 級 recorder）
# ---------------------------------------------------------------------------

RECORDER: dict = {}


@pytest.fixture(autouse=True)
def _reset_recorder():
    RECORDER.clear()
    RECORDER.update(
        {"echo": [], "ping": 0, "forbidden": 0, "slow_started": 0, "slow_completed": 0}
    )
    yield


@pytest.fixture(scope="module", autouse=True)
def _register_test_tools():
    @tool_registry.tool(name="test.echo", kind="local", args_schema={"value": str})
    async def _echo(ctx: ToolContext, value: str = "") -> str:
        RECORDER["echo"].append(
            {"tenant": ctx.tenant_id, "user": ctx.user_id, "role": ctx.role, "value": value}
        )
        return f"echoed:{value}"

    @tool_registry.tool(name="test.ping", kind="local", args_schema={})
    async def _ping(ctx: ToolContext) -> str:
        RECORDER["ping"] += 1
        return "pong"

    @tool_registry.tool(name="test.forbidden", kind="local", args_schema={})
    async def _forbidden(ctx: ToolContext) -> str:
        RECORDER["forbidden"] += 1
        return "should not happen"

    @tool_registry.tool(name="test.slow", kind="local", args_schema={})
    async def _slow(ctx: ToolContext) -> str:
        RECORDER["slow_started"] += 1
        await asyncio.sleep(30)
        RECORDER["slow_completed"] += 1
        return "done"

    yield
    for name in ("test.echo", "test.ping", "test.forbidden", "test.slow"):
        tool_registry._REGISTRY.pop(name, None)


def _deps_for(skill, *steps, resources=None, scripts=None, instruction="Be helpful."):
    """組出：MemoryPackageReader(裝好 package) + scripted model factory + audit repo。"""
    pkg = make_package(
        skill, instruction=instruction, resources=resources, scripts=scripts
    )
    reader = MemoryPackageReader()
    reader.put("demo-a", pkg)
    factory, model = model_factory(*steps)
    audit = RecordingAuditRepo()
    deps = make_agentic_deps(reader, factory, audit_repo=audit)
    return deps, model, audit


# ---------------------------------------------------------------------------
# AST-P1-001：flow 走既有 graph；agentic graph 含 runner 且終端必有 audit
# ---------------------------------------------------------------------------


def test_compile_dispatch_flow_vs_agentic():
    from app import skills

    flow_graph = compiler.compile(skills.get("rag-qa").skill, skills.get("rag-qa").deps)
    flow_nodes = set(flow_graph.get_graph().nodes)
    assert "agent_skill_runner" not in flow_nodes  # flow 不含 runner

    skill = make_agentic_skill(uses_tools=[])
    deps, _model, _audit = _deps_for(skill, ("final", "hi"))
    agentic_graph = compiler.compile(skill, deps)
    nodes = set(agentic_graph.get_graph().nodes)
    assert "agent_skill_runner" in nodes  # agentic 有 runner
    assert any("audit_feedback" in n for n in nodes)  # 終端強制附加稽核


def test_agent_skill_graph_has_no_compiler_dependency():
    """Agent Skill bridge may use neutral primitives, never compiler internals."""
    source = (
        Path(__file__).resolve().parents[1]
        / "app"
        / "engine"
        / "agent_skill_graph.py"
    )
    tree = ast.parse(source.read_text(encoding="utf-8"))
    compiler_imports = [
        node
        for node in ast.walk(tree)
        if (
            isinstance(node, ast.ImportFrom)
            and (
                node.module == "app.engine.compiler"
                or (
                    node.module == "app.engine"
                    and any(alias.name == "compiler" for alias in node.names)
                )
            )
        )
        or (
            isinstance(node, ast.Import)
            and any(alias.name == "app.engine.compiler" for alias in node.names)
        )
    ]

    assert compiler_imports == []


def test_agent_skill_compile_cache_keys_revision_content_and_deps(monkeypatch):
    """同 revision/content/deps 命中；任一維度變動都必須重建圖。"""
    builds: list[tuple[int, str, int]] = []
    real_build = compiler._build_graph

    def counting_build(skill, deps):
        builds.append((skill.revision, skill.description, id(deps)))
        return real_build(skill, deps)

    monkeypatch.setattr(compiler, "_build_graph", counting_build)
    skill = make_agentic_skill(name="agent-cache-contract")
    deps, _model, _audit = _deps_for(skill, ("final", "hi"))

    first = compiler.compile(skill, deps)
    assert compiler.compile(skill, deps) is first

    revision_changed = skill.model_copy(update={"revision": skill.revision + 1})
    compiler.compile(revision_changed, deps)

    content_changed = skill.model_copy(update={"description": "cache content changed"})
    compiler.compile(content_changed, deps)

    other_deps, _other_model, _other_audit = _deps_for(skill, ("final", "hi"))
    compiler.compile(skill, other_deps)

    assert builds == [
        (skill.revision, skill.description, id(deps)),
        (revision_changed.revision, revision_changed.description, id(deps)),
        (content_changed.revision, content_changed.description, id(deps)),
        (skill.revision, skill.description, id(other_deps)),
    ]


def test_agentic_run_terminates_with_audit():
    """agentic 執行後終端 trace 必為 audit_feedback（治理硬規則對 agentic 一樣成立）。"""
    skill = make_agentic_skill(uses_tools=[])
    deps, _model, audit = _deps_for(skill, ("final", "hi"))
    out = run_agentic(skill, deps, query="hello")
    assert out["trace"][-1].node_name == "audit_feedback"
    assert len(audit.saved) == 1


# ---------------------------------------------------------------------------
# AST-P1-002：explicit invoke → {skill, output}，output 含固定 answer key
# ---------------------------------------------------------------------------


class _FakeResp:
    def __init__(self, status_code, payload=None):
        self.status_code = status_code
        self._payload = payload

    def raise_for_status(self):
        return None

    def json(self):
        return self._payload


CANONICAL = """name: sales-helper
description: 銷售小幫手
metadata:
  kind: agentic
  required_role: USER
  timeout_seconds: "30"
  input_schema: '{"query": {"type": "str", "required": true, "min_length": 1}}'
"""


def _install_agentic_backend(monkeypatch, *, definition=CANONICAL, tenant="demo-a"):
    def handle(url, headers):
        if url.endswith("/configuration-sets/active"):
            return _FakeResp(404, None)
        if headers.get("X-Tenant-Id") != tenant:
            return _FakeResp(404, None)
        if url == "/api/skills/sales-helper":
            return _FakeResp(
                200,
                {
                    "name": "sales-helper",
                    "description": "銷售小幫手",
                    "required_role": "USER",
                    "enabled": True,
                    "current_revision": 2,
                    "kind": "agentic",
                    "definition": definition,
                    "definition_sha256": hashlib.sha256(definition.encode("utf-8")).hexdigest(),
                },
            )
        return _FakeResp(404, None)

    install_fake_get(monkeypatch, handle)


def test_explicit_invoke_returns_skill_and_answer(monkeypatch):
    """AST-P1-002：HTTP /skills/{name}/invoke → {skill, output}，output 有固定 answer。"""
    client = TestClient(app)
    skill = make_agentic_skill()
    pkg = make_package(skill, instruction="Help.")
    reader = MemoryPackageReader()
    reader.put("demo-a", pkg)
    factory, _model = model_factory(("final", "the answer"))
    deps = make_agentic_deps(reader, factory)
    monkeypatch.setattr(custom, "_deps", deps)
    _install_agentic_backend(monkeypatch)

    resp = client.post(
        "/skills/sales-helper/invoke",
        json={"input": {"query": "hi"}},
        headers=_headers(),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["skill"] == "sales-helper"
    assert body["output"]["answer"] == "the answer"
    assert [t["node_name"] for t in body["output"]["trace"]][-1] == "audit_feedback"


# ---------------------------------------------------------------------------
# AST-P1-003：Harness 對 runner 的 declared-writes 剝除 + immutable 身分
# ---------------------------------------------------------------------------


def test_harness_strips_forged_identity_and_undeclared_keys():
    """runner 的 harness 契約 writes=['answer']：偽造身分/未宣告鍵一律剝除，answer 保留。"""

    async def forging_fn(state):
        return {
            "answer": "real",
            "tenant_id": "evil-tenant",  # 偽造身分
            "query_id": "FORGED",  # immutable
            "leaked": "secret",  # 未宣告鍵
        }

    wrapped = harnessed("agent_skill_runner", forging_fn, writes=["answer"])
    out = asyncio.run(wrapped({"tenant_id": "demo-a", "query_id": "real-id"}))

    assert out["answer"] == "real"
    assert "tenant_id" not in out  # 未在 writes → 剝除（身分無法在流程中途被換）
    assert "query_id" not in out
    assert "leaked" not in out
    assert set(out) == {"answer", "trace"}


# ---------------------------------------------------------------------------
# AST-P1-004：未列在 uses_tools 的 tool → call counter 0 + 受控續行 + 稽核
# ---------------------------------------------------------------------------


def test_disallowed_tool_not_called_audit_still_runs():
    skill = make_agentic_skill(uses_tools=["test.ping"])  # forbidden 不在白名單
    deps, _model, audit = _deps_for(
        skill, ("tool", "test_forbidden", {}), ("final", "refused")
    )
    out = run_agentic(skill, deps, query="x")

    assert RECORDER["forbidden"] == 0  # 未被呼叫（連 adapter 都沒有）
    assert out["answer"] == "refused"  # 受控續行
    assert len(audit.saved) == 1  # 稽核仍可見


def test_registry_rejects_tool_outside_allowlist():
    """防禦縱深：即使有 adapter，registry.invoke 的 allowed 白名單也會擋（不真的發出呼叫）。"""
    spec = tool_registry.get("test.forbidden")
    ctx = ToolContext(tenant_id="demo-a", user_id="alice", role="USER")
    adapter = make_registry_tool(spec, ctx, allowed=frozenset({"test.ping"}))

    with pytest.raises(tool_registry.ToolNotAllowed):
        asyncio.run(adapter.ainvoke({}))
    assert RECORDER["forbidden"] == 0


def test_unregistered_tool_in_uses_tools_is_controlled_fatal_with_audit():
    """uses_tools 列了未註冊的 tool（validate 之外的路徑進來）→ 受控 fatal，不是 500。

    runner 建 adapter 前 tool_registry.get() 取不到 spec 就 raise，例外穿出 node fn →
    Harness 轉 fatal_error；model 從未被呼叫、稽核仍執行（與 step limit / timeout 同一條路）。
    """
    skill = make_agentic_skill(uses_tools=["test.unregistered"])
    deps, model, audit = _deps_for(skill, ("final", "never reached"))
    out = run_agentic(skill, deps, query="x")

    assert "answer" not in out  # 未產出答案
    assert "test.unregistered" in out["fatal_error"]  # 受控失敗且指名壞掉的 tool
    assert model.counter["calls"] == 0  # tool 解析在 ReAct loop 之前就擋下
    assert len(audit.saved) == 1
    assert out["trace"][-1].node_name == "audit_feedback"


# ---------------------------------------------------------------------------
# AST-P1-005：adapter 呼叫 registry 一次、收到 server-injected 身分、不持 token
# ---------------------------------------------------------------------------


def test_adapter_calls_registry_once_with_identity_and_holds_no_token():
    spec = tool_registry.get("test.echo")
    ctx = ToolContext(tenant_id="demo-a", user_id="alice", role="USER")
    adapter = make_registry_tool(spec, ctx, allowed=frozenset({"test.echo"}))

    result = asyncio.run(adapter.ainvoke({"value": "hi"}))

    assert result == "echoed:hi"
    assert RECORDER["echo"] == [
        {"tenant": "demo-a", "user": "alice", "role": "USER", "value": "hi"}
    ]  # 一次，且帶 server-injected 身分

    # 不直持 internal token：閉包只捕獲 (spec, ctx, allowed)，settings 不在 freevars，
    # token 值也不在任何 closure cell（token 由 http tool 實作在呼叫當下自 settings 取）。
    co = adapter.coroutine
    assert "settings" not in co.__code__.co_freevars
    cells = [c.cell_contents for c in (co.__closure__ or ())]
    assert settings.internal_api_token not in cells


# ---------------------------------------------------------------------------
# AST-P1-006：resource 唯讀 allow/deny；不洩漏 host 檔案
# ---------------------------------------------------------------------------


def test_resource_tool_allows_references_and_assets():
    skill = make_agentic_skill(uses_tools=[])
    pkg = make_package(
        skill,
        resources={"references/a.md": b"ref-body", "assets/a.txt": b"asset-body"},
    )
    tool = make_resource_tool(pkg)
    assert asyncio.run(tool.ainvoke({"path": "references/a.md"})) == "ref-body"
    assert asyncio.run(tool.ainvoke({"path": "assets/a.txt"})) == "asset-body"


@pytest.mark.parametrize(
    "bad_path",
    [
        "../secret",
        "../../etc/passwd",
        "/etc/passwd",
        "C:/Windows/win.ini",
        "scripts/x.py",  # scripts/ 不可讀
        "references/missing.md",  # package 外不可見
        "SKILL.md",  # references/assets 以外
    ],
)
def test_resource_tool_denies_bad_paths(bad_path):
    skill = make_agentic_skill(uses_tools=[])
    pkg = make_package(skill, resources={"references/a.md": b"ok"})
    with pytest.raises(ValueError):
        read_resource(pkg, bad_path)


@pytest.mark.parametrize("blank_path", ["", "   ", "\t\n"])
def test_resource_tool_denies_empty_or_blank_path(blank_path):
    """空字串／純空白是與 traversal 不同的邊界：走的是「不可為空」那道 guard。"""
    skill = make_agentic_skill(uses_tools=[])
    pkg = make_package(skill, resources={"references/a.md": b"ok"})
    with pytest.raises(ValueError, match="不可為空"):
        read_resource(pkg, blank_path)


@pytest.mark.parametrize("bad_path", [None, 123, b"references/a.md"])
def test_resource_tool_denies_non_string_path(bad_path):
    """白名單、預設拒絕：非字串 path（tool schema 被繞過時）同樣 raise，不是 TypeError/AttributeError。"""
    skill = make_agentic_skill(uses_tools=[])
    pkg = make_package(skill, resources={"references/a.md": b"ok"})
    with pytest.raises(ValueError, match="不可為空"):
        read_resource(pkg, bad_path)


def test_resource_read_truncates_at_max_bytes_boundary():
    """RESOURCE_MAX_BYTES 邊界：恰好上限全文回傳，多一個 byte 就被截到上限（不整包塞進 prompt）。"""
    skill = make_agentic_skill(uses_tools=[])
    pkg = make_package(
        skill,
        resources={
            "references/exact.md": b"x" * RESOURCE_MAX_BYTES,
            "references/over.md": b"y" * (RESOURCE_MAX_BYTES + 1),
        },
    )
    assert read_resource(pkg, "references/exact.md") == "x" * RESOURCE_MAX_BYTES
    assert read_resource(pkg, "references/over.md") == "y" * RESOURCE_MAX_BYTES


def test_resource_read_never_touches_host_filesystem(tmp_path):
    """讀取只來自 in-memory pkg.resources；host 上真實存在的檔案也讀不到。"""
    secret = tmp_path / "secret.txt"
    secret.write_text("HOST SECRET")
    skill = make_agentic_skill(uses_tools=[])
    pkg = make_package(skill, resources={"references/a.md": b"ok"})
    with pytest.raises(ValueError):
        read_resource(pkg, str(secret))
    with pytest.raises(ValueError):
        read_resource(pkg, "references/../../" + secret.name)


# ---------------------------------------------------------------------------
# AST-P1-007：step limit on-point 完成 / off-point 受控終止 + 稽核
# ---------------------------------------------------------------------------


def test_step_limit_on_point_completes():
    skill = make_agentic_skill(uses_tools=["test.ping"])
    steps = [("tool", "test_ping", {})] * MAX_TOOL_ROUNDS + [("final", "done")]
    deps, _model, audit = _deps_for(skill, *steps)
    out = run_agentic(skill, deps, query="x")
    assert out["answer"] == "done"
    assert RECORDER["ping"] == MAX_TOOL_ROUNDS
    assert out["trace"][-1].node_name == "audit_feedback"


def test_step_limit_off_point_controlled_terminate_with_audit():
    skill = make_agentic_skill(uses_tools=["test.ping"])
    steps = [("tool", "test_ping", {})] * (MAX_TOOL_ROUNDS + 1) + [("final", "done")]
    deps, _model, audit = _deps_for(skill, *steps)
    out = run_agentic(skill, deps, query="x")
    assert "answer" not in out  # 未完成，不產出答案
    assert out.get("fatal_error")  # 受控失敗（GraphRecursionError → Harness fatal）
    assert len(audit.saved) == 1  # 稽核仍執行
    assert out["trace"][-1].node_name == "audit_feedback"


# ---------------------------------------------------------------------------
# AST-P1-008：timeout 受控、無背景 tool 續跑、稽核記錄失敗
# ---------------------------------------------------------------------------


def test_timeout_is_controlled_and_no_background_tool_continues():
    skill = make_agentic_skill(uses_tools=["test.slow"], timeout_seconds=1)
    deps, _model, audit = _deps_for(skill, ("tool", "test_slow", {}))
    out = run_agentic(skill, deps, query="x")

    assert RECORDER["slow_started"] == 1
    assert RECORDER["slow_completed"] == 0  # 被 asyncio.timeout 取消，沒有背景 tool 續跑
    assert "answer" not in out
    assert out.get("fatal_error")
    assert len(audit.saved) == 1
    assert out["trace"][-1].node_name == "audit_feedback"


# AST-P1-008b：走真實 production invoke 路徑（/skills/{name}/invoke → _run_with_timeout）
# 的 timeout 稽核保證。上面的 test 走 run_agentic/graph.ainvoke，繞過 invoke wrapper，抓不到
# 「invoke-level outer timeout 搶先取消 runner → 稽核被跳過」這個縫。此 test 用宣告 timeout_seconds
# 的 agentic skill hang 在 slow tool，斷言 node-level timeout 先觸發 → fatal → 稽核仍執行、
# 無背景 tool 續跑。修正前 outer 與 inner 同值、outer 早幾 ms 起跑而搶先 → 504、稽核被跳過。

TIMEOUT_CANONICAL = """name: sales-helper
description: 銷售小幫手
allowed-tools: test.slow
metadata:
  kind: agentic
  required_role: USER
  timeout_seconds: "1"
  input_schema: '{"query": {"type": "str", "required": true, "min_length": 1}}'
"""


def test_invoke_agentic_timeout_runs_audit_via_production_path(monkeypatch):
    client = TestClient(app)
    skill = make_agentic_skill(uses_tools=["test.slow"], timeout_seconds=1)
    pkg = make_package(skill, instruction="Help.")
    reader = MemoryPackageReader()
    reader.put("demo-a", pkg)
    factory, _model = model_factory(("tool", "test_slow", {}))
    audit = RecordingAuditRepo()
    deps = make_agentic_deps(reader, factory, audit_repo=audit)
    monkeypatch.setattr(custom, "_deps", deps)
    _install_agentic_backend(monkeypatch, definition=TIMEOUT_CANONICAL)

    resp = client.post(
        "/skills/sales-helper/invoke",
        json={"input": {"query": "hi"}},
        headers=_headers(),
    )

    # 修正前：outer invoke timeout 搶先取消 runner → HTTPException 504、稽核未跑。
    # 修正後：inner node timeout 先觸發 → Harness fatal → 圖跑完 audit → 200。
    assert resp.status_code == 200
    output = resp.json()["output"]
    assert "answer" not in output  # 逾時未完成，不產出答案
    assert output.get("fatal_error")  # node-level timeout → Harness fatal
    assert [t["node_name"] for t in output["trace"]][-1] == "audit_feedback"
    assert len(audit.saved) == 1  # 可觀察的稽核效果存在（稽核仍執行）
    assert RECORDER["slow_started"] == 1
    assert RECORDER["slow_completed"] == 0  # 無背景 tool 續跑（asyncio.timeout 取消 in-flight）


# ---------------------------------------------------------------------------
# AST-P1-009：交錯 invoke 同 name，各用自己 tenant 的 package instruction
# ---------------------------------------------------------------------------


def test_per_tenant_instruction_no_cross_pollution(monkeypatch):
    captured: list = []
    real_create = agent_skill_runner.create_react_agent

    def spy_create(model, tools, prompt=None):
        captured.append(prompt)
        return real_create(model, tools, prompt=prompt)

    monkeypatch.setattr(agent_skill_runner, "create_react_agent", spy_create)

    skill = make_agentic_skill(uses_tools=[])
    pkg_a = make_package(skill, instruction="INSTRUCTION-A")
    pkg_b = make_package(skill, instruction="INSTRUCTION-B")
    reader = MemoryPackageReader()
    reader.put("tenant-a", pkg_a)
    reader.put("tenant-b", pkg_b)
    factory, _model = model_factory(("final", "ok"))
    deps = make_agentic_deps(reader, factory)

    for tenant in ("tenant-a", "tenant-b", "tenant-a"):
        run_agentic(skill, deps, tenant_id=tenant, query="x")

    assert captured == ["INSTRUCTION-A", "INSTRUCTION-B", "INSTRUCTION-A"]


# ---------------------------------------------------------------------------
# AST-P1-010：含 scan-passing scripts/x.py → 不執行、明確忽略；其他行為正常
# ---------------------------------------------------------------------------


def test_bundle_scripts_are_stored_but_disabled():
    skill = make_agentic_skill(uses_tools=[])
    # 這段 script 若被當步驟執行會寫 state；agentic runner 從不執行 pkg.scripts。
    scripts = {"scripts/probe.py": "state['leaked'] = 'SCRIPT RAN'\n"}
    deps, _model, audit = _deps_for(
        skill, ("final", "answer_ok"),
        scripts=scripts, resources={"references/a.md": b"ok"},
    )
    out = run_agentic(skill, deps, query="x")

    assert out["answer"] == "answer_ok"  # 其他 agentic 行為正常
    assert "leaked" not in out  # script 未執行、無副作用
    assert len(audit.saved) == 1

    # scripts/ 連唯讀都不可（更遑論執行）：package 內 script 是 stored-but-disabled 資源
    pkg = deps.agent_package_reader._by_tenant[("demo-a", "sales-helper")]
    assert "scripts/probe.py" in pkg.scripts  # 有存下來
    with pytest.raises(ValueError):
        read_resource(pkg, "scripts/probe.py")  # 但讀不到、也沒有對應 tool


# ---------------------------------------------------------------------------
# A2 reads 契約強制化：過濾不得吃掉使用者輸入（input_schema 的鍵必在 runner 視圖內）
# ---------------------------------------------------------------------------


class _EchoChatModel(BaseChatModel):
    """把收到的最後一則 user message 原文回傳（不是固定字串）→ 驗過濾沒吃掉使用者輸入。"""

    @property
    def _llm_type(self) -> str:
        return "echo"

    def bind_tools(self, tools, **kwargs):
        return self

    def _generate(self, messages, stop=None, run_manager=None, **kwargs):
        from langchain_core.messages import AIMessage
        from langchain_core.outputs import ChatGeneration, ChatResult

        user_texts = [m.content for m in messages if getattr(m, "type", "") == "human"]
        echoed = user_texts[-1] if user_texts else ""
        return ChatResult(
            generations=[ChatGeneration(message=AIMessage(content=echoed))]
        )


def test_reads_filter_preserves_user_input_for_agentic_runner():
    """runner 的 harness reads 過濾必須含 input_schema：回顯 model 的 answer 帶得出 query 值。"""
    skill = make_agentic_skill(uses_tools=[])
    pkg = make_package(skill, instruction="Be helpful.")
    reader = MemoryPackageReader()
    reader.put("demo-a", pkg)
    echo = _EchoChatModel()
    deps = make_agentic_deps(reader, lambda: echo)

    out = run_agentic(skill, deps, query="MAGIC-VALUE-42")

    # answer = 回顯的 user message；build_user_message 把 input_schema 的 query 組進去。
    # 若過濾吃掉 query，回顯就不會含 MAGIC-VALUE-42（現有固定字串 fake 抓不到此縫）。
    assert "MAGIC-VALUE-42" in out["answer"]


# ---------------------------------------------------------------------------
# LangGraph 計數語意 pinning test（runner 的 step limit 直接建立在這個觀察之上）
# ---------------------------------------------------------------------------


def test_pin_langgraph_recursion_semantics():
    """釘住安裝版 create_react_agent 的實際計數/終止語意（設計 §4.3-5 要求先驗後用）。

    觀察：以 runner 使用的 AGENT_RECURSION_LIMIT，做 MAX_TOOL_ROUNDS 輪 tool call 的
    model 正常完成；多一輪即 raise GraphRecursionError（受控、非無限迴圈、非 canned 訊息）。
    runner 取奇數 recursion_limit 正是為了讓超界走 raise → Harness fatal，而非偶數的
    'need more steps' canned 訊息（那會繞過 fatal 路徑）。
    """
    from langchain_core.language_models.chat_models import BaseChatModel
    from langchain_core.messages import AIMessage
    from langchain_core.outputs import ChatGeneration, ChatResult
    from langchain_core.tools import tool as lctool
    from langgraph.errors import GraphRecursionError
    from langgraph.prebuilt import create_react_agent

    @lctool
    def ping() -> str:
        """ping"""
        return "pong"

    def build(rounds):
        class M(BaseChatModel):
            @property
            def _llm_type(self):
                return "m"

            def bind_tools(self, tools, **kw):
                return self

            def _generate(self, messages, stop=None, run_manager=None, **kw):
                M.count += 1
                if M.count > rounds:
                    msg = AIMessage(content="FINAL")
                else:
                    msg = AIMessage(
                        content="", tool_calls=[{"name": "ping", "args": {}, "id": f"c{M.count}"}]
                    )
                return ChatResult(generations=[ChatGeneration(message=msg)])

        M.count = 0
        return M()

    # on-point：恰好 MAX_TOOL_ROUNDS 輪 → 完成
    agent = create_react_agent(build(MAX_TOOL_ROUNDS), [ping])
    r = asyncio.run(
        agent.ainvoke(
            {"messages": [("user", "hi")]},
            config={"recursion_limit": AGENT_RECURSION_LIMIT},
        )
    )
    assert r["messages"][-1].content == "FINAL"

    # off-point：多一輪 → 受控 raise（非 canned 訊息）
    agent2 = create_react_agent(build(MAX_TOOL_ROUNDS + 1), [ping])
    with pytest.raises(GraphRecursionError):
        asyncio.run(
            agent2.ainvoke(
                {"messages": [("user", "hi")]},
                config={"recursion_limit": AGENT_RECURSION_LIMIT},
            )
        )
