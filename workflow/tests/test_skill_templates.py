"""P2a 五支 template-* 骨架的 workflow 側就地驗（SSR-P2A-001~004、SSR-P2A-002 catalog、
SSR-P4-013 topK 縫⑦）。

這是把「組出的 YAML 過不過得了 workflow 驗證/編譯/執行」就地驗掉（設計 §8：不在瀏覽器
重做）。前端 compose 只是純字串 patch，這裡以同一格式 patch 骨架後跑真 compiler/Harness：
- 冷啟動五支全載入編譯成功、GET /skills 帶回非空 definition；
- 逐支把規則槽 patch 成一句自然語言規則(nl_logic)後 validate 仍 valid、
  invoke 得到 business_result；
- compare/stats 的檢索筆數槽 patch 後，通用 retrieve 實收覆寫值（不誤讀模組全域）。

B2-py 後五支骨架皆為 nl_logic 形狀（compare/stats 由 script 槽遷至 nl_logic）：驗的是
資料流與槽機制（規則進 system、docs 進 user message、business_result 落地、topK 槽生效），
不驗 LLM 行為（不試圖讓 nl_logic 真的排序/聚合）。

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
from tests.kbquery_fakes import (
    TEXT_2025Q3,
    FakeSearch,
    FakeStructuredLLM,
    RecordingLLM,
    make_deps,
)

client = TestClient(app)

TEMPLATE_NAMES = (
    "template-retrieval",
    "template-compare",
    "template-stats",
    "template-infer",
    "template-inspire",
)
# B2-py 後五支骨架的商業邏輯槽全是 nl_logic 的 instruction（compare/stats 由 script 遷入），
# 分類塌成單一 NL 類。
NL_TEMPLATES = TEMPLATE_NAMES
# 這兩支由 script 骨架遷至 nl_logic（B2-py）——舊 SCRIPT_TEMPLATES 的成員,遷移後併入 NL。
SCRIPT_TEMPLATES_MIGRATED = ("template-compare", "template-stats")
# 遷移後與 infer/inspire 同形狀的 thin-retrieval NL 骨架（query_intake → retrieve → nl_logic）
NL_THIN_RETRIEVAL_TEMPLATES = (
    "template-infer",
    "template-inspire",
    "template-compare",
    "template-stats",
)


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


# B2-py:規則槽現為自然語言(nl_logic 的 instruction),不再是 Python script。
# 驗的是資料流(規則進 system、docs 進 user、business_result 落地),不試圖讓 nl_logic 真的排序。
COMPARE_NL_RULE = "依 score 由高到低比較 docs,列出標題排序"
STATS_NL_RULE = "統計 docs 的筆數與 score 總和"

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
                "name": "sales-rule",
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
    assert body["sales-rule"]["definition"] is None
    # 內建仍帶定義（未被 custom 影響）
    assert body["kb-query"]["definition"]


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


@pytest.mark.parametrize("name", SCRIPT_TEMPLATES_MIGRATED)
def test_migrated_compare_stats_have_no_script_step(name):
    """B2-py:compare/stats 遷移後,flow 內不再有任何 script 步驟。

    決策表另一半 —— 舊 test_script_rule_slot_is_in_script_block 驗「規則在 script block」;
    遷移後「規則落在 nl_logic 的 instruction block」已由上面
    test_nl_rule_slot_is_in_nl_logic_instruction_block[compare/stats] 覆蓋（NL_TEMPLATES
    已塌成五支全含）,此處只留遷移專屬、別處沒有的那一條:script 步驟真的消失了。
    """
    data = yaml.safe_load(skills.get(name).definition)
    assert not any(isinstance(s, dict) and "script" in s for s in data["flow"])


@pytest.mark.parametrize("name", TEMPLATE_NAMES)
def test_unpatched_skeleton_is_valid(name):
    result = skill_mod.validate_source(skills.get(name).definition)
    assert result.valid is True


# ---------------------------------------------------------------------------
# SSR-P2A-004:patch 規則 → valid + invoke（三支 NL、兩支 script）
# ---------------------------------------------------------------------------


def test_patched_retrieval_invoke_produces_business_result():
    raw = skills.get("template-retrieval").definition
    patched = patch_rule(raw, "把答案濃縮成一句話")
    assert skill_mod.validate_source(patched).valid is True

    llm = FakeStructuredLLM(outputs={_NlLogicOutput: _NlLogicOutput(result="濃縮答案")})
    deps = make_deps({"vector": FakeSearch(lambda q, f: [TEXT_2025Q3])}, llm=llm)
    out = _invoke(patched, deps, query="2025Q3 稅後淨利是多少？")

    assert out["business_result"] == "濃縮答案"
    assert "final_answer" in out  # kb_query 族確實跑過
    assert out["trace"][-1].node_name == "audit_feedback"


@pytest.mark.parametrize("name", NL_THIN_RETRIEVAL_TEMPLATES)
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


@pytest.mark.parametrize(
    "name, rule",
    [
        ("template-compare", COMPARE_NL_RULE),
        ("template-stats", STATS_NL_RULE),
    ],
)
def test_patched_migrated_invoke_flows_docs_through_nl_logic(name, rule, monkeypatch):
    """B2-py:compare/stats 遷至 nl_logic 後,規則進 system、docs 進 user、business_result 落地。

    NL 規則語意（不試圖讓 nl_logic 真的排序/聚合）:用 RecordingLLM 回傳可控值,驗資料流與槽機制。
    """
    raw = skills.get(name).definition
    patched = patch_rule(raw, rule)
    assert skill_mod.validate_source(patched).valid is True

    _install_fake_retrieve(monkeypatch)
    llm = RecordingLLM(output=_NlLogicOutput(result="規則結果"))
    out = _invoke(patched, make_deps({}, llm=llm), query="q")

    # business_result = nl_logic 落地的 LLM 結果
    assert out["business_result"] == "規則結果"
    # 規則進 system、docs（input_keys:[docs]）進 user message
    call = llm.calls[-1]
    assert call["schema"] is _NlLogicOutput
    assert call["system"].strip() == rule
    assert call["user"].startswith("docs:")  # build_user_message 以 input_keys 組裝
    assert "B" in call["user"]  # docs 的內容（title）確實餵進 user message
    # 資料流:retrieve → nl_logic 皆跑過
    node_names = [t.node_name for t in out["trace"]]
    assert "retrieve" in node_names and "nl_logic" in node_names


def test_empty_retrieval_docs_still_flow_through_nl_logic(monkeypatch):
    """檢索 0 筆時（docs=[]）nl_logic 照跑、user message 仍是 `docs: []`、business_result 照樣落地。

    BVT:docs 集合大小的下界（既有 invoke 測試一律吃固定 3 筆的 DOCS）。build_user_message
    對 input_keys 一律組 `k: repr(value)`,對空集合沒有任何守衛,nl_logic 也不短路;
    若日後有人加「docs 為空就跳過 LLM／回預設值」的守衛,這條會轉紅而非靜默改語意。
    """
    _install_fake_retrieve(monkeypatch, docs=[])
    patched = patch_rule(skills.get("template-compare").definition, COMPARE_NL_RULE)
    llm = RecordingLLM(output=_NlLogicOutput(result="空集合結果"))
    out = _invoke(patched, make_deps({}, llm=llm), query="q")

    assert out["docs"] == []
    assert out["business_result"] == "空集合結果"
    assert llm.calls[-1]["user"] == "docs: []"
    node_names = [t.node_name for t in out["trace"]]
    assert "retrieve" in node_names and "nl_logic" in node_names


# ---------------------------------------------------------------------------
# SSR-P4-013 / 縫⑦:compare/stats 的檢索筆數槽 —— 通用 retrieve 實收覆寫值
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "name, default_top_k",
    [("template-compare", 8), ("template-stats", 50)],
)
def test_topk_slot_default_is_carried_to_retrieve(name, default_top_k, monkeypatch):
    """未覆寫時,通用 retrieve 收到骨架顯式帶的 top_k（不是 retrieve.py 的模組全域 4）。"""
    from app.settings import settings

    assert settings.retrieval_top_k != default_top_k  # 骨架值刻意不同於全域,才驗得出縫⑦
    captured: dict = {}
    _install_fake_retrieve(monkeypatch, captured=captured)
    patched = patch_rule(skills.get(name).definition, COMPARE_NL_RULE)
    llm = RecordingLLM(output=_NlLogicOutput(result="x"))
    _invoke(patched, make_deps({}, llm=llm), query="q")
    assert captured["json"]["top_k"] == default_top_k


def test_topk_slot_override_is_carried_to_retrieve(monkeypatch):
    """把 # __SLOT_topK__ 那行 patch 成 17 → 通用 retrieve 實收 17。"""
    captured: dict = {}
    _install_fake_retrieve(monkeypatch, captured=captured)
    raw = skills.get("template-compare").definition
    patched = patch_topk(raw, 17)
    patched = patch_rule(patched, COMPARE_NL_RULE)
    assert skill_mod.validate_source(patched).valid is True
    llm = RecordingLLM(output=_NlLogicOutput(result="x"))
    _invoke(patched, make_deps({}, llm=llm), query="q")
    assert captured["json"]["top_k"] == 17


def test_topk_slot_override_zero_is_carried_to_retrieve(monkeypatch):
    """把 # __SLOT_topK__ 那行 patch 成 0 → 通用 retrieve 仍實收 0（BVT，決策表 2 缺口）。

    retrieve.py 用 `top_k is not None` 當守衛（非真值判斷）：0 是 falsy 但合法的覆寫值。
    若日後被「簡化」成 `if top_k:`，0 會被誤判成「未覆寫」而靜默退回骨架/全域預設值，
    且沒有任何既有測試會變紅（上一測試只覆蓋 17 這個真值為真的案例）。
    """
    captured: dict = {}
    _install_fake_retrieve(monkeypatch, captured=captured)
    raw = skills.get("template-compare").definition
    patched = patch_topk(raw, 0)
    patched = patch_rule(patched, COMPARE_NL_RULE)
    assert skill_mod.validate_source(patched).valid is True
    llm = RecordingLLM(output=_NlLogicOutput(result="x"))
    _invoke(patched, make_deps({}, llm=llm), query="q")
    assert captured["json"]["top_k"] == 0


def test_topk_slot_override_is_carried_to_retrieve_for_stats(monkeypatch):
    """template-stats 也走得通覆寫路徑:patch 成 3 → 通用 retrieve 實收 3（all-pairs 缺口）。

    上面兩支覆寫測試（17、0）都寫死 template-compare,stats 只被驗過「不覆寫吃骨架的 50」;
    3 同時異於骨架值 50、compare 骨架值 8 與模組全域 4,patch 沒生效就只會看到 50。
    """
    captured: dict = {}
    _install_fake_retrieve(monkeypatch, captured=captured)
    raw = skills.get("template-stats").definition
    patched = patch_topk(raw, 3)
    patched = patch_rule(patched, STATS_NL_RULE)
    assert skill_mod.validate_source(patched).valid is True
    llm = RecordingLLM(output=_NlLogicOutput(result="x"))
    _invoke(patched, make_deps({}, llm=llm), query="q")
    assert captured["json"]["top_k"] == 3
