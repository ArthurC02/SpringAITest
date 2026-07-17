"""P2a 五支 template_* 骨架的 workflow 側就地驗（SSR-P2A-001~004、SSR-P2A-002 catalog、
SSR-P4-013 topK 縫⑦）。

這是把「組出的 YAML 過不過得了 workflow 驗證/編譯/執行」就地驗掉（設計 §8：不在瀏覽器
重做）。前端 compose 只是純字串 patch，這裡以同一格式 patch 骨架後跑真 compiler/Harness：
- 冷啟動五支全載入編譯成功、GET /skills 帶回非空 definition；
- 逐支把規則槽 patch 成一句規則(nl_logic)/一段 Python(script)後 validate 仍 valid、
  invoke 得到 business_result(三支 NL)/精確排序聚合(兩支 script)；
- compare/stats 的檢索筆數槽 patch 後，通用 retrieve 實收覆寫值（不誤讀模組全域）。

拿掉任一 template 的 deps mapping（縫③）或 app/skills/__init__.py 的 nl_logic/retrieve
import（縫⑥）→ 冷啟動 _load_builtin 編譯失敗、本檔轉紅（真護欄）。main.py 的 nl_logic
import 僅 GET /nodes catalog 的 defence-in-depth，非唯一觸發點，拔掉它本檔仍綠。
無 pytest-asyncio，統一 asyncio.run。
"""

import asyncio

import httpx
import pytest
import yaml
from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.engine import skill as skill_mod
from app.main import app
from app.nodes.nl_logic import _NlLogicOutput
from tests.conftest import FakeBackendResponse, auth_headers
from tests.kbquery_fakes import TEXT_2025Q3, FakeSearch, FakeStructuredLLM, make_deps

client = TestClient(app)

TEMPLATE_NAMES = (
    "template_retrieval",
    "template_compare",
    "template_stats",
    "template_infer",
    "template_inspire",
)
NL_TEMPLATES = ("template_retrieval", "template_infer", "template_inspire")
SCRIPT_TEMPLATES = ("template_compare", "template_stats")


# ---------------------------------------------------------------------------
# patch helpers（與前端 compose 同格式的純字串 patch）
# ---------------------------------------------------------------------------


def patch_rule(raw: str, rule: str) -> str:
    """把含 __RULE_SLOT__ 的行,以同一縮排逐行換成 rule。"""
    out = []
    for line in raw.splitlines():
        if "__RULE_SLOT__" in line:
            indent = line[: len(line) - len(line.lstrip())]
            out.extend(indent + rl for rl in rule.splitlines())
        else:
            out.append(line)
    return "\n".join(out) + "\n"


def patch_topk(raw: str, value: int) -> str:
    """重寫帶 # __SLOT_topK__ 尾註那行的 top_k 純量值（數字原樣）。"""
    out = []
    for line in raw.splitlines():
        if "# __SLOT_topK__" in line:
            indent = line[: len(line) - len(line.lstrip())]
            out.append(f"{indent}top_k: {value}  # __SLOT_topK__")
        else:
            out.append(line)
    return "\n".join(out) + "\n"


# 排序 docs → 標題(依 score 遞減);sandbox 無 lambda,以 for 建 tuple list 再 sorted
COMPARE_RULE = """pairs = []
for d in state["docs"]:
    pairs = pairs + [(d["score"], d["title"])]
ranked = sorted(pairs, reverse=True)
titles = []
for p in ranked:
    titles = titles + [p[1]]
state["business_result"] = titles"""

# 聚合:筆數 + score 總和
STATS_RULE = """total = 0.0
n = 0
for d in state["docs"]:
    total = total + d["score"]
    n = n + 1
state["business_result"] = {"count": n, "total": total}"""

DOCS = [
    {"document_id": "a", "title": "A", "content": "x", "score": 0.3},
    {"document_id": "b", "title": "B", "content": "y", "score": 0.9},
    {"document_id": "c", "title": "C", "content": "z", "score": 0.6},
]


def _install_fake_retrieve(monkeypatch, docs=DOCS, captured=None):
    async def fake_post(self, url, json=None, headers=None, **kwargs):
        if captured is not None:
            captured["json"] = json
        return FakeBackendResponse(docs)

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)


def _invoke(definition: str, deps, **state) -> dict:
    skill = skill_mod.parse_source(definition)
    graph = compiler.compile(skill, deps)
    out = asyncio.run(graph.ainvoke({"tenant_id": "t", **state}))
    return compiler.public_output(out)


# ---------------------------------------------------------------------------
# SSR-P2A-001:冷啟動五支全載入編譯成功、health ok
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", TEMPLATE_NAMES)
def test_cold_start_compiles_every_template(name):
    loaded = skills.get(name)
    assert loaded is not None
    assert loaded.source == "builtin"
    assert loaded.deps is not None  # 縫③:deps mapping 缺失 → None → 編譯期早已炸
    assert loaded.graph is not None  # 編譯成功的圖


def test_health_ok_after_templates_loaded():
    assert client.get("/health").json() == {"status": "ok"}


# ---------------------------------------------------------------------------
# SSR-P2A-002:catalog 五支 builtin 帶非空 definition;custom 不帶
# ---------------------------------------------------------------------------


def test_catalog_builtin_templates_carry_non_empty_definition():
    body = {i["name"]: i for i in client.get("/skills", headers=auth_headers()).json()}
    for name in TEMPLATE_NAMES:
        assert name in body
        assert body[name]["source"] == "builtin"
        assert body[name]["definition"]  # 非空原文
        # 原文可被獨立 parse 回同名 skill（compose 的定點事實來源）
        assert skill_mod.parse_source(body[name]["definition"]).name == name


def test_catalog_custom_entry_has_no_definition(monkeypatch):
    """custom 項不帶 definition（catalog dict 無此鍵 → SkillInfo 預設 None）。"""

    async def fake_catalog(ctx):
        return [
            {
                "name": "sales_rule",
                "description": "租戶自訂",
                "required_role": "USER",
                "source": "custom",
                "revision": 2,
                "input_schema": None,
            }
        ]

    from app.skills import custom

    monkeypatch.setattr(custom, "catalog", fake_catalog)
    body = {i["name"]: i for i in client.get("/skills", headers=auth_headers()).json()}
    assert body["sales_rule"]["definition"] is None
    # 內建仍帶定義（未被 custom 影響）
    assert body["kb_query"]["definition"]


# ---------------------------------------------------------------------------
# SSR-P2A-003:sentinel 不變式（恰一個 __RULE_SLOT__、位於正確 block scalar、未 patch 仍 valid）
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", TEMPLATE_NAMES)
def test_exactly_one_rule_slot(name):
    assert skills.get(name).definition.count("__RULE_SLOT__") == 1


@pytest.mark.parametrize("name", NL_TEMPLATES)
def test_nl_rule_slot_is_in_nl_logic_instruction_block(name):
    data = yaml.safe_load(skills.get(name).definition)
    nl_steps = [
        s for s in data["flow"] if isinstance(s, dict) and str(s.get("node", "")).startswith("nl_logic")
    ]
    assert len(nl_steps) == 1
    assert nl_steps[0]["params"]["instruction"].strip() == "__RULE_SLOT__"


@pytest.mark.parametrize("name", SCRIPT_TEMPLATES)
def test_script_rule_slot_is_in_script_block(name):
    data = yaml.safe_load(skills.get(name).definition)
    script_steps = [s for s in data["flow"] if isinstance(s, dict) and "script" in s]
    assert len(script_steps) == 1
    assert "__RULE_SLOT__" in script_steps[0]["script"]


@pytest.mark.parametrize("name", TEMPLATE_NAMES)
def test_unpatched_skeleton_is_valid(name):
    result = skill_mod.validate_source(skills.get(name).definition)
    assert result.valid is True


# ---------------------------------------------------------------------------
# SSR-P2A-004:patch 規則 → valid + invoke（三支 NL、兩支 script）
# ---------------------------------------------------------------------------


def test_patched_retrieval_invoke_produces_business_result():
    raw = skills.get("template_retrieval").definition
    patched = patch_rule(raw, "把答案濃縮成一句話")
    assert skill_mod.validate_source(patched).valid is True

    llm = FakeStructuredLLM(outputs={_NlLogicOutput: _NlLogicOutput(result="濃縮答案")})
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])}, llm=llm)
    out = _invoke(patched, deps, query="2025Q3 稅後淨利是多少？")

    assert out["business_result"] == "濃縮答案"
    assert "final_answer" in out  # kb_query 族確實跑過
    assert out["trace"][-1].node_name == "audit_feedback"


@pytest.mark.parametrize("name", ("template_infer", "template_inspire"))
def test_patched_nl_thin_retrieval_invoke_produces_business_result(name, monkeypatch):
    raw = skills.get(name).definition
    patched = patch_rule(raw, "根據 docs 推論一句話")
    assert skill_mod.validate_source(patched).valid is True

    _install_fake_retrieve(monkeypatch)
    llm = FakeStructuredLLM(outputs={_NlLogicOutput: _NlLogicOutput(result="推論輸出")})
    deps = make_deps({}, llm=llm)
    out = _invoke(patched, deps, query="請推論")

    assert out["business_result"] == "推論輸出"
    node_names = [t.node_name for t in out["trace"]]
    assert "retrieve" in node_names and "nl_logic" in node_names


def test_patched_compare_invoke_sorts_docs_by_score(monkeypatch):
    raw = skills.get("template_compare").definition
    patched = patch_rule(raw, COMPARE_RULE)
    assert skill_mod.validate_source(patched).valid is True

    _install_fake_retrieve(monkeypatch)
    out = _invoke(patched, make_deps({}), query="比較")

    # 依 score 遞減:B(0.9) > C(0.6) > A(0.3)
    assert out["business_result"] == ["B", "C", "A"]


def test_patched_stats_invoke_aggregates(monkeypatch):
    raw = skills.get("template_stats").definition
    patched = patch_rule(raw, STATS_RULE)
    assert skill_mod.validate_source(patched).valid is True

    _install_fake_retrieve(monkeypatch)
    out = _invoke(patched, make_deps({}), query="統計")

    assert out["business_result"] == {"count": 3, "total": pytest.approx(1.8)}


# ---------------------------------------------------------------------------
# SSR-P4-013 / 縫⑦:compare/stats 的檢索筆數槽 —— 通用 retrieve 實收覆寫值
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "name, default_top_k",
    [("template_compare", 8), ("template_stats", 50)],
)
def test_topk_slot_default_is_carried_to_retrieve(name, default_top_k, monkeypatch):
    """未覆寫時,通用 retrieve 收到骨架顯式帶的 top_k（不是 retrieve.py 的模組全域 4）。"""
    from app.settings import settings

    assert settings.retrieval_top_k != default_top_k  # 骨架值刻意不同於全域,才驗得出縫⑦
    captured: dict = {}
    _install_fake_retrieve(monkeypatch, captured=captured)
    patched = patch_rule(skills.get(name).definition, COMPARE_RULE)
    _invoke(patched, make_deps({}), query="q")
    assert captured["json"]["top_k"] == default_top_k


def test_topk_slot_override_is_carried_to_retrieve(monkeypatch):
    """把 # __SLOT_topK__ 那行 patch 成 17 → 通用 retrieve 實收 17。"""
    captured: dict = {}
    _install_fake_retrieve(monkeypatch, captured=captured)
    raw = skills.get("template_compare").definition
    patched = patch_topk(raw, 17)
    patched = patch_rule(patched, COMPARE_RULE)
    assert skill_mod.validate_source(patched).valid is True
    _invoke(patched, make_deps({}), query="q")
    assert captured["json"]["top_k"] == 17
