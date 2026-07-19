"""Node Registry 測試（AT1-01 ~ AT1-03）：註冊契約、重名防呆、GET /nodes 目錄形狀。

節點的 @node 宣告在節點模組頂層執行，因此只要 import 到節點模組就已完成註冊。
"""

import pytest
from fastapi.testclient import TestClient

from app import skills
from app.engine import node_registry
from app.engine.node_registry import NodeSpec
from app.main import app

# import 觸發註冊：kb_query 十節點 + 共用 retrieve
from app.nodes import retrieve as _retrieve_node  # noqa: F401
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from tests.conftest import auth_headers

client = TestClient(app)

HEADERS = auth_headers()

KB_QUERY_NODES = [
    "query_intake",
    "query_rewrite",
    "intent_classification",
    "context_resolver",
    "retrieval_planner",
    "source_retrieval_rerank",
    "data_locator",
    "evidence_verification",
    "answer_composer",
    "audit_feedback",
]


# ---------------------------------------------------------------------------
# AT1-01 @node 註冊與 NodeSpec 契約查詢
# ---------------------------------------------------------------------------


def test_get_evidence_verification_spec_matches_declared_contract():
    """【AT1-01】evidence_verification@1.0 的契約與規格書 §2.1 範例逐鍵相等。

    唯一差異：reads 多一個 calculation_result —— 節點實際會讀它做數值一致性檢查
    （evidence_verification.py 的 state.get("calculation_result")），規格書範例漏列。
    契約以程式實際行為為準。
    """
    spec = node_registry.get("evidence_verification", version="1.0")

    assert isinstance(spec, NodeSpec)
    assert spec.name == "evidence_verification"
    assert spec.version == "1.0"
    assert set(spec.reads) == {
        "selected_evidence",
        "target_period",
        "canonical_metric",
        "excluded_terms",
        "metric_terms",
        "candidate_answer",
        "calculation_trace",
        "requires_calculation",
        "calculation_result",
    }
    assert set(spec.writes) == {
        "verification_result",
        "confidence",
        "failure_reason",
        "failure_codes",
        "verified_evidence",
    }
    assert spec.requires_tools == ()


def test_get_without_version_resolves_latest():
    """未指定版本 → 解析到最新版（目前每個節點只有 1.0）。"""
    assert node_registry.get("evidence_verification").version == "1.0"
    assert node_registry.get("no_such_node") is None
    assert node_registry.get("evidence_verification", version="9.9") is None


def test_all_eleven_nodes_registered():
    """kb_query 十節點 + retrieve 皆已註冊，且 run_on_fatal 只給 composer/audit。"""
    names = {spec.name for spec in node_registry.all_specs()}
    assert set(KB_QUERY_NODES + ["retrieve"]).issubset(names)

    on_fatal = {s.name for s in node_registry.all_specs() if s.run_on_fatal}
    assert on_fatal == {"answer_composer", "audit_feedback"}


def test_spec_build_injects_declared_deps_in_order():
    """spec.build(deps) 依 deps 欄位名依序取值當位置參數；retrieve 的靜態參數走 kwargs。"""
    recorded: dict = {}

    def fake_factory(llm, glossary):
        recorded["args"] = (llm, glossary)
        return "node-fn"

    spec = NodeSpec(
        name="__throwaway__",
        version="1.0",
        description="",
        reads=(),
        writes=(),
        deps=("llm", "glossary"),
        requires_tools=(),
        run_on_fatal=False,
        factory=fake_factory,
    )

    class Deps:
        llm = "LLM"
        glossary = "GLOSSARY"

    assert spec.build(Deps()) == "node-fn"
    assert recorded["args"] == ("LLM", "GLOSSARY")

    # retrieve：deps 為空，query_key/top_k 由呼叫端以 kwargs 傳入
    retrieve_fn = node_registry.get("retrieve").build(None, query_key="question", top_k=3)
    assert callable(retrieve_fn)


# ---------------------------------------------------------------------------
# AT1-02 重複 name@version 註冊 → ValueError
# ---------------------------------------------------------------------------


def test_duplicate_name_version_raises_value_error():
    """【AT1-02】同一個 name@version 二度註冊立刻 ValueError，訊息含 name 與 version。"""
    with pytest.raises(ValueError) as exc:
        node_registry.node(
            name="evidence_verification",
            version="1.0",
            description="第二次註冊，應該失敗",
            writes=["verification_result"],
        )(lambda: None)

    assert "evidence_verification" in str(exc.value)
    assert "1.0" in str(exc.value)

    # 第一次註冊的內容沒有被覆蓋
    assert node_registry.get("evidence_verification", version="1.0").description == (
        "確定性驗證閘門"
    )


def test_same_name_different_version_can_coexist_and_skill_pins_v1():
    """同名不同版可並存（Skill 以 node@version 鎖版本）；get() 無版本時取最新。

    關鍵回歸：註冊 v2 後 kb_query.yaml 的 flow 仍寫死 evidence_verification@1.0，
    不得靜默升版（Node-First：版本鎖在 Skill YAML 的 flow，不再是手寫圖的 _NODES tuple）。
    """
    name = "evidence_verification"
    try:
        node_registry.node(name=name, version="2.0", description="v2", writes=["x"])(
            lambda: None
        )

        assert node_registry.get(name, version="1.0").version == "1.0"
        assert node_registry.get(name).version == "2.0"  # 未指定 → 最新版
        assert f"{name}@1.0" in skills.get("kb_query").definition
    finally:
        node_registry._REGISTRY.pop((name, "2.0"), None)


def test_declaring_engine_key_as_writes_raises_value_error():
    """治理硬規則：節點不得宣告 trace/errors/fatal_error 為 writes（Harness 專屬）。"""
    for key in ("trace", "errors", "fatal_error"):
        with pytest.raises(ValueError) as exc:
            node_registry.node(name="__throwaway__", writes=["ok", key])(lambda: None)
        assert key in str(exc.value)

    assert node_registry.get("__throwaway__") is None  # 沒被登記進註冊表


def test_writes_is_required():
    """漏寫 writes → import 期 TypeError（不會靜默變成「輸出全丟棄」的節點）。"""
    with pytest.raises(TypeError):
        node_registry.node(name="__throwaway__")(lambda: None)


# ---------------------------------------------------------------------------
# AT1-03 GET /nodes 回應形狀
# ---------------------------------------------------------------------------


def test_get_nodes_returns_contract_of_at_least_eleven_nodes():
    """【AT1-03】GET /nodes 回陣列，每筆含契約六欄，至少 11 筆。"""
    resp = client.get("/nodes", headers=HEADERS)

    assert resp.status_code == 200
    body = resp.json()
    assert isinstance(body, list)
    assert len(body) >= 11

    by_name = {item["name"]: item for item in body}
    assert set(KB_QUERY_NODES + ["retrieve"]).issubset(by_name.keys())

    item = by_name["evidence_verification"]
    assert item["version"] == "1.0"
    assert item["description"]
    assert isinstance(item["reads"], list)
    assert "selected_evidence" in item["reads"]
    assert isinstance(item["writes"], list)
    assert "verification_result" in item["writes"]
    assert item["requires_tools"] == []


def test_get_nodes_requires_internal_token():
    """服務間標頭要求同 /workflows：缺 token → 401，缺租戶標頭 → 400。"""
    assert client.get("/nodes").status_code == 401

    no_tenant = {k: v for k, v in HEADERS.items() if k != "X-Tenant-Id"}
    assert client.get("/nodes", headers=no_tenant).status_code == 400
