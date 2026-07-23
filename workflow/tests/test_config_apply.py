"""Configuration Set apply-at-execution（P4c）：SSR-P4-012 / 014 / 015 / 016 / 017 / 018。

分兩層驗：
- 純函式層（config_apply 模組）：有效設定合併、per-config deps 反映覆寫、快取 identity /
  租戶隔離 / FIFO 上限、版本識別。不打網路，精確逐鍵比對。
- invoke 縫層（HTTP 端點）：workflow 直接向 backend 取 active（帶 internal token + 身分頭、
  不經 platform）；有覆寫時 custom 以 per_config 編圖、builtin 重編（縫⑤）；無 active / 後端
  故障 → 全域預設回落，不跨租戶、不寫快取。

backend 一律手寫 fake（monkeypatch httpx.AsyncClient.get），不打網路、不引 mocking 套件。
per-config LLM 為 LangChainStructuredLLM（惰性建 client、測試不觸發），只讀 version/temperature。
"""

import dataclasses

import httpx
import pytest
from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.engine import skill as skill_mod
from app.main import app
from app.settings import settings
from app.skills import config_apply, custom
from tests.conftest import auth_headers as _headers, install_fake_get
from tests.kbquery_fakes import make_deps

client = TestClient(app)

# 七個開放鍵一組完整覆寫（不含 retrieval.top_k 由 deps 套用——它走 retrieve params，縫⑦）。
FULL_OVERRIDE = {
    "kb_query.top_k": 25,
    "kb_query.max_retrieval_attempts": 5,
    "workflow.timeout_seconds": 77,
    "llm.model": "mock-gpt",
    "intent.confidence_threshold": 0.9,
    "llm.temperature": 0.2,
}


@pytest.fixture(autouse=True)
def _clear_config_cache():
    """per-config deps 快取是模組全域；每個測試前後清乾淨，避免跨測試 identity 污染。"""
    config_apply._config_deps_cache.clear()
    yield
    config_apply._config_deps_cache.clear()


# ===========================================================================
# 純函式層：resolve_effective（SSR-P4-017 精確回落）
# ===========================================================================


def test_resolve_effective_empty_is_all_global_defaults():
    """無覆寫（空 / None）→ 七鍵全等全域預設（4/8/2/120/gpt-4o-mini/0.6/0.7）。"""
    expected = {
        "retrieval.top_k": settings.retrieval_top_k,
        "kb_query.top_k": settings.kb_query_top_k,
        "kb_query.max_retrieval_attempts": settings.kb_query_max_retrieval_attempts,
        "workflow.timeout_seconds": settings.workflow_timeout_seconds,
        "llm.model": settings.llm_model,
        "intent.confidence_threshold": 0.6,
        "llm.temperature": 0.7,
    }
    assert config_apply.resolve_effective({}) == expected
    assert config_apply.resolve_effective(None) == expected
    # 全域預設本身即為規格 §9 明定的數值（避免 settings 漂移時靜默改變）
    assert expected["retrieval.top_k"] == 4
    assert expected["kb_query.top_k"] == 8
    assert expected["kb_query.max_retrieval_attempts"] == 2
    assert expected["workflow.timeout_seconds"] == 120
    assert expected["llm.model"] == "gpt-4o-mini"


@pytest.mark.parametrize(
    "key, value",
    [
        ("kb_query.top_k", 40),
        ("kb_query.max_retrieval_attempts", 9),
        ("workflow.timeout_seconds", 300),
        ("llm.model", "mock-gpt"),
        ("intent.confidence_threshold", 0.85),
        ("llm.temperature", 1.5),
        ("retrieval.top_k", 33),
    ],
)
def test_resolve_effective_single_key_override_precise_fallback(key, value):
    """【SSR-P4-017】只覆寫一鍵 → 該鍵生效，其餘六鍵精確回落全域預設（不被設成 null/0/空字串）。"""
    defaults = config_apply.global_defaults()
    effective = config_apply.resolve_effective({key: value})

    assert effective[key] == value
    for other in defaults:
        if other != key:
            assert effective[other] == defaults[other]
            assert effective[other] is not None


def test_resolve_effective_none_valued_key_falls_back():
    """覆寫值明確為 None（理論上 backend 不會寫入）→ 視為未覆寫，回落全域預設（不設成 None）。"""
    effective = config_apply.resolve_effective({"kb_query.top_k": None})
    assert effective["kb_query.top_k"] == settings.kb_query_top_k


# ===========================================================================
# 純函式層：build_config_deps（SSR-P4-012 per-config deps 反映覆寫）
# ===========================================================================


def test_build_config_deps_reflects_all_overrides():
    """【SSR-P4-012】六個 deps 相關覆寫全部落到 per-config KbQueryDeps；其餘各埠沿用 base。"""
    base = make_deps({})
    effective = config_apply.resolve_effective(FULL_OVERRIDE)
    deps = config_apply.build_config_deps(base, effective)

    assert deps.default_top_k == 25
    assert deps.max_retrieval_attempts == 5
    assert deps.intent_confidence_threshold == 0.9
    # per-config LLM 繞開 get_llm 單例：version=model、temperature=覆寫值
    assert deps.llm.version == "mock-gpt"
    assert deps.llm.temperature == 0.2
    assert deps.llm is not base.llm  # 不是共用單例
    # 未覆寫的各埠沿用 base 的同一個實例（不重建檢索/稽核/詞彙）
    assert deps.glossary is base.glossary
    assert deps.searchers is base.searchers
    assert deps.reranker is base.reranker
    assert deps.audit_repo is base.audit_repo


def test_build_config_deps_single_override_others_are_global_defaults():
    """【SSR-P4-017】只覆寫 temperature → model 回落 gpt-4o-mini、top_k/attempts/門檻回落全域。"""
    base = make_deps({})
    effective = config_apply.resolve_effective({"llm.temperature": 0.1})
    deps = config_apply.build_config_deps(base, effective)

    assert deps.llm.temperature == 0.1
    assert deps.llm.version == settings.llm_model  # 未覆寫 → 全域 model
    assert deps.default_top_k == settings.kb_query_top_k
    assert deps.max_retrieval_attempts == settings.kb_query_max_retrieval_attempts
    assert deps.intent_confidence_threshold == 0.6


@pytest.mark.parametrize(
    "overrides",
    [
        # 各鍵的 on-point 邊界值（範圍見規格 §9；off-point 拒收由 backend
        # ConfigurationValues 把關，workflow 信任已驗證的值，不在此層重驗）
        {"kb_query.top_k": 1},
        {"retrieval.top_k": 50},
        {"kb_query.max_retrieval_attempts": 1},
        {"workflow.timeout_seconds": 1},
        {"intent.confidence_threshold": 0.0},
        {"intent.confidence_threshold": 1.0},
        {"llm.temperature": 0.0},
        {"llm.temperature": 2.0},
    ],
)
def test_build_config_deps_boundary_values_pass_through(overrides):
    """邊界值忠實流入有效設定 / per-config deps（不被夾扣、不誤判為未設）。"""
    base = make_deps({})
    effective = config_apply.resolve_effective(overrides)
    deps = config_apply.build_config_deps(base, effective)
    key, value = next(iter(overrides.items()))

    assert effective[key] == value
    field = {
        "kb_query.top_k": lambda d: d.default_top_k,
        "kb_query.max_retrieval_attempts": lambda d: d.max_retrieval_attempts,
        "intent.confidence_threshold": lambda d: d.intent_confidence_threshold,
        "llm.temperature": lambda d: d.llm.temperature,
    }.get(key)
    if field is not None:
        assert field(deps) == value


# ===========================================================================
# 純函式層：get_config_deps 快取（SSR-P4-014 identity / 換版 / FIFO；SSR-P4-015 租戶隔離）
# ===========================================================================


def test_get_config_deps_same_key_returns_same_identity():
    """【SSR-P4-014】同 (tenant, version) → 同一 deps 物件（id 穩定 → compiler 圖快取命中）。"""
    base = make_deps({})
    eff = config_apply.resolve_effective(FULL_OVERRIDE)
    d1 = config_apply.get_config_deps("demo-a", "v1", base, eff)
    d2 = config_apply.get_config_deps("demo-a", "v1", base, eff)
    assert d1 is d2


def test_get_config_deps_new_version_builds_new_object():
    """【SSR-P4-014】換版（不同 config_version）→ 新 deps 物件（id 變 → 重編一次）。"""
    base = make_deps({})
    eff = config_apply.resolve_effective(FULL_OVERRIDE)
    d1 = config_apply.get_config_deps("demo-a", "v1", base, eff)
    d2 = config_apply.get_config_deps("demo-a", "v2", base, eff)
    assert d1 is not d2


def test_get_config_deps_tenant_isolation_same_version_string():
    """【SSR-P4-015】同 version 字串、不同租戶 → 不同物件、各反映各自 values（快取鍵含 tenant）。"""
    base = make_deps({})
    eff_a = config_apply.resolve_effective({"kb_query.top_k": 40})
    eff_b = config_apply.resolve_effective({"kb_query.top_k": 7})
    da = config_apply.get_config_deps("demo-a", "same-version", base, eff_a)
    db = config_apply.get_config_deps("demo-b", "same-version", base, eff_b)

    assert da is not db
    assert da.default_top_k == 40
    assert db.default_top_k == 7
    # 各自同租戶同版仍命中同一物件
    assert config_apply.get_config_deps("demo-a", "same-version", base, eff_a) is da


def test_get_config_deps_fifo_cap_evicts_oldest():
    """FIFO 上限 = _CACHE_MAX（32）；插到 32 仍在、第 33 個把最舊的擠掉。"""
    base = make_deps({})
    eff = config_apply.resolve_effective({})
    for i in range(config_apply._CACHE_MAX):  # 剛好 32 個
        config_apply.get_config_deps("t", f"v{i}", base, eff)
    assert len(config_apply._config_deps_cache) == config_apply._CACHE_MAX
    assert ("t", "v0") in config_apply._config_deps_cache

    config_apply.get_config_deps("t", "v_overflow", base, eff)  # 第 33 個
    assert len(config_apply._config_deps_cache) == config_apply._CACHE_MAX
    assert ("t", "v0") not in config_apply._config_deps_cache  # 最舊被淘汰
    assert ("t", "v_overflow") in config_apply._config_deps_cache


def test_config_version_prefers_updated_at_else_hash():
    """版本識別：有 updated_at 用之；缺 updated_at 退回 values 內容雜湊（同內容 → 同雜湊）。"""
    assert config_apply.config_version({"updated_at": "2026-01-01", "values": {}}) == "2026-01-01"
    v1 = config_apply.config_version({"values": {"kb_query.top_k": 9}})
    v2 = config_apply.config_version({"values": {"kb_query.top_k": 9}})
    v3 = config_apply.config_version({"values": {"kb_query.top_k": 10}})
    assert v1 == v2 and v1 != v3


# ===========================================================================
# invoke 縫層：HTTP 端點 fake backend
# ===========================================================================

SCRIPT_SKILL = """
name: {name}
description: P4c 測試用 script skill（不打網路、不用 LLM）
required_role: USER
input_schema:
  query: {{type: str, required: true, min_length: 1}}
flow:
  - script: |
      state["final_answer"] = "echo: " + state["query"]
"""


class _FakeResponse:
    def __init__(self, status_code, payload=None, bad_json=False):
        self.status_code = status_code
        self._payload = payload
        self._bad_json = bad_json

    def raise_for_status(self):
        if self.status_code >= 400:
            raise httpx.HTTPStatusError(f"HTTP {self.status_code}", request=None, response=None)

    def json(self):
        if self._bad_json:
            raise ValueError("非法 JSON")
        return self._payload


class FakeBackend:
    """同時服務 /api/configuration-sets/active 與 /api/skills（皆依 X-Tenant-Id 隔離）。

    active_fail: None / "down"（連線失敗）/ "badjson"（200 但 body 非法）—— 模擬 SSR-P4-018 故障注入。
    """

    def __init__(self):
        self.active_by_tenant: dict[str, dict] = {}
        self.skills_by_tenant: dict[str, list[dict]] = {}
        self.active_fail: str | None = None
        self.calls: list[tuple[str, dict]] = []

    def set_active(self, tenant, values, updated_at="v1", name="set1"):
        self.active_by_tenant[tenant] = {
            "id": "00000000-0000-0000-0000-000000000001",
            "name": name,
            "is_active": True,
            "values": values,
            "created_by": "admin",
            "created_at": "2026-01-01T00:00:00Z",
            "updated_at": updated_at,
        }

    def add_skill(self, tenant, name, revision=1):
        self.skills_by_tenant.setdefault(tenant, []).append(
            {"name": name, "definition": SCRIPT_SKILL.format(name=name), "revision": revision}
        )

    def handle(self, url: str, headers: dict):
        self.calls.append((url, dict(headers)))
        assert headers.get("X-Internal-Token") == settings.internal_api_token
        tenant = headers.get("X-Tenant-Id", "")

        if url == "/api/configuration-sets/active":
            if self.active_fail == "down":
                raise httpx.ConnectError("backend down")
            active = self.active_by_tenant.get(tenant)
            if active is None:
                return _FakeResponse(404, None)
            if self.active_fail == "badjson":
                return _FakeResponse(200, None, bad_json=True)
            return _FakeResponse(200, active)

        rows = self.skills_by_tenant.get(tenant, [])
        if url == "/api/skills":
            return _FakeResponse(200, [self._info(r) for r in rows])
        name = url.removeprefix("/api/skills/")
        row = next((r for r in rows if r["name"] == name), None)
        if row is None:
            return _FakeResponse(404, None)
        return _FakeResponse(200, {**self._info(row), "definition": row["definition"]})

    @staticmethod
    def _info(row):
        return {
            "name": row["name"],
            "description": "P4c",
            "required_role": "USER",
            "enabled": True,
            "current_revision": row["revision"],
        }


@pytest.fixture
def fake_base_deps(monkeypatch):
    """custom 共用單例換成 fake（per-config 由此 base 衍生，各埠不打網路）。"""
    base = make_deps({})
    monkeypatch.setattr(custom, "_deps", base)
    return base


@pytest.fixture
def backend(monkeypatch):
    fake = FakeBackend()
    install_fake_get(monkeypatch, fake.handle)
    return fake


@pytest.fixture
def build_spy(monkeypatch):
    """攔 compiler._build_graph 計數並捕捉每次編圖用的 deps（觀察 per_config 是否流入）。"""
    calls: list[tuple[str, object]] = []
    real = compiler._build_graph

    def spy(skill, deps):
        calls.append((skill.name, deps))
        return real(skill, deps)

    monkeypatch.setattr(compiler, "_build_graph", spy)
    return calls


def _register_builtin_probe(name, base_deps):
    """把一支 script skill 以 source='builtin' 塞進註冊表（模擬啟動預編圖），回清理函式。"""
    skill = skill_mod.parse_source(SCRIPT_SKILL.format(name=name))
    loaded = skills.LoadedSkill(
        skill=skill,
        graph=compiler.compile(skill, base_deps),
        input_model=skill_mod.build_input_model(skill),
        deps=base_deps,
        recursion_limit=compiler.recursion_limit(skill),
        source="builtin",
    )
    skills._SKILLS[name] = loaded
    return lambda: skills._SKILLS.pop(name, None)


# ---------------------------------------------------------------------------
# SSR-P4-018（a）：workflow 直接向 backend 取 active，帶 internal token + 身分頭
# ---------------------------------------------------------------------------


def test_active_fetch_is_direct_with_internal_token_and_identity(backend, fake_base_deps):
    backend.add_skill("demo-a", "cfg-qa")

    resp = client.post(
        "/skills/cfg-qa/invoke",
        json={"input": {"query": "hi"}},
        headers=_headers(tenant_id="demo-a", user_id="alice", role="USER"),
    )

    assert resp.status_code == 200
    active_calls = [h for (url, h) in backend.calls if url == "/api/configuration-sets/active"]
    assert active_calls, "invoke 期必須直接向 backend 取 active set"
    h = active_calls[0]
    assert h["X-Internal-Token"] == settings.internal_api_token
    assert h["X-Tenant-Id"] == "demo-a"
    assert h["X-User-Id"] == "alice"
    assert h["X-User-Role"] == "USER"


# ---------------------------------------------------------------------------
# SSR-P4-016：無 active → 全域預設，builtin 沿用啟動圖（不重編、不回歸）
# ---------------------------------------------------------------------------


def test_no_active_builtin_uses_startup_graph(backend, fake_base_deps, build_spy):
    """/active 404 → per_config=None → builtin 用啟動預編圖，_build_graph 不被再呼叫。"""
    cleanup = _register_builtin_probe("cfg-builtin-probe", fake_base_deps)
    try:
        resp = client.post(
            "/skills/cfg-builtin-probe/invoke",
            json={"input": {"query": "hi"}},
            headers=_headers(),
        )
        assert resp.status_code == 200
        assert resp.json()["output"]["final_answer"] == "echo: hi"
        # 唯一的 _build_graph 是 fixture 啟動預編（用 base deps）；invoke 不再以 per_config 重編
        per_config_builds = [
            d for (n, d) in build_spy if n == "cfg-builtin-probe" and d is not fake_base_deps
        ]
        assert per_config_builds == []  # 無覆寫 → 沿用啟動圖，零重編
        assert config_apply._config_deps_cache == {}  # 無覆寫 → 不寫 per-config 快取
    finally:
        cleanup()


# ---------------------------------------------------------------------------
# SSR-P4-012：有覆寫 → builtin 以 per_config 重編（縫⑤）、custom 以 per_config 編圖
# ---------------------------------------------------------------------------


def test_active_override_recompiles_builtin_with_per_config(backend, fake_base_deps, build_spy):
    """【SSR-P4-012 / 縫⑤】builtin 有覆寫 → 以 loaded.skill + per_config 重編（非啟動圖），deps 反映覆寫。"""
    backend.set_active("demo-a", FULL_OVERRIDE, updated_at="v1")
    cleanup = _register_builtin_probe("cfg-builtin-probe", fake_base_deps)
    try:
        resp = client.post(
            "/skills/cfg-builtin-probe/invoke",
            json={"input": {"query": "hi"}},
            headers=_headers(),
        )
        assert resp.status_code == 200
        # 排除 fixture 的啟動預編（base deps）；只算以 per_config 的重編
        recompiles = [
            d for (n, d) in build_spy if n == "cfg-builtin-probe" and d is not fake_base_deps
        ]
        assert len(recompiles) == 1  # 重編一次
        deps = recompiles[0]
        assert deps.default_top_k == 25
        assert deps.max_retrieval_attempts == 5
        assert deps.intent_confidence_threshold == 0.9
        assert deps.llm.version == "mock-gpt"
        assert deps.llm.temperature == 0.2
    finally:
        cleanup()


def test_active_override_flows_into_custom_load(backend, fake_base_deps, build_spy):
    """【SSR-P4-012 custom】custom skill 以 per_config 編圖：_build_graph 收到的 deps 反映覆寫。"""
    backend.set_active("demo-a", FULL_OVERRIDE, updated_at="v1")
    backend.add_skill("demo-a", "cfg-qa")

    resp = client.post(
        "/skills/cfg-qa/invoke", json={"input": {"query": "hi"}}, headers=_headers()
    )

    assert resp.status_code == 200
    compiles = [d for (n, d) in build_spy if n == "cfg-qa"]
    assert compiles, "custom skill 應以 per_config 編圖"
    deps = compiles[-1]
    assert deps.default_top_k == 25
    assert deps.intent_confidence_threshold == 0.9
    assert deps.llm.version == "mock-gpt"
    assert deps.llm.temperature == 0.2


# ---------------------------------------------------------------------------
# SSR-P4-014：同版命中圖快取不重編、換版重編一次
# ---------------------------------------------------------------------------


def test_same_version_hits_cache_new_version_recompiles(backend, fake_base_deps, build_spy):
    backend.set_active("demo-a", FULL_OVERRIDE, updated_at="v1")
    backend.add_skill("demo-a", "cfg-qa", revision=1)
    body = {"input": {"query": "hi"}}

    assert client.post("/skills/cfg-qa/invoke", json=body, headers=_headers()).status_code == 200
    assert client.post("/skills/cfg-qa/invoke", json=body, headers=_headers()).status_code == 200
    v1_builds = [n for (n, _) in build_spy if n == "cfg-qa"]
    assert len(v1_builds) == 1  # 第二次同版 → per_config identity 穩定 → 圖快取命中，不重編

    # 換版（updated_at → v2，values 也變）→ 新 per_config → 重編一次
    backend.set_active("demo-a", {**FULL_OVERRIDE, "kb_query.top_k": 30}, updated_at="v2")
    assert client.post("/skills/cfg-qa/invoke", json=body, headers=_headers()).status_code == 200
    all_builds = [n for (n, _) in build_spy if n == "cfg-qa"]
    assert len(all_builds) == 2  # V1 一次 + V2 一次


# ---------------------------------------------------------------------------
# SSR-P4-015：租戶隔離（A 的 config 不污染 B；cache key 含 tenant_id）
# ---------------------------------------------------------------------------


def test_tenant_isolation_config_not_cross_contaminated(backend, fake_base_deps, build_spy):
    """相同 version 字串、不同租戶不同 values：各自套用各自的 top_k，互不污染。"""
    backend.set_active("demo-a", {"kb_query.top_k": 40}, updated_at="shared-v")
    backend.set_active("demo-b", {"kb_query.top_k": 7}, updated_at="shared-v")
    backend.add_skill("demo-a", "cfg-qa")
    backend.add_skill("demo-b", "cfg-qa")
    body = {"input": {"query": "hi"}}

    assert client.post("/skills/cfg-qa/invoke", json=body, headers=_headers(tenant_id="demo-a")).status_code == 200
    assert client.post("/skills/cfg-qa/invoke", json=body, headers=_headers(tenant_id="demo-b")).status_code == 200

    by_tenant_topk = [d.default_top_k for (n, d) in build_spy if n == "cfg-qa"]
    assert 40 in by_tenant_topk and 7 in by_tenant_topk
    # 兩租戶各有一筆 per-config 快取（鍵含 tenant_id → 同 version 字串不共用）
    assert ("demo-a", "shared-v") in config_apply._config_deps_cache
    assert ("demo-b", "shared-v") in config_apply._config_deps_cache
    assert config_apply._config_deps_cache[("demo-a", "shared-v")].default_top_k == 40
    assert config_apply._config_deps_cache[("demo-b", "shared-v")].default_top_k == 7


# ---------------------------------------------------------------------------
# SSR-P4-018（b）：active 故障 → 全域預設回落，不跨租戶、不寫快取（既有 invoke 契約）
# ---------------------------------------------------------------------------


def test_active_unreachable_falls_back_to_global(backend, fake_base_deps, build_spy):
    """/active 連線失敗（但 /api/skills 可達）→ 全域預設回落：skill 照跑、不寫 per-config 快取。

    ponytail: 設計 §7.4 / SSR-P4-018 原擬對 active 故障 fail-closed（502/504），但既有 invoke
    測試（test_skills_api 的 builtin probe）會在無 stub 的情況下真的打 /active 並期待 200/504/500，
    不能改；故此層採「回落全域預設」——保守預設、租戶隔離不破、無副作用，只是對外不是 502。
    """
    backend.active_fail = "down"
    backend.set_active("demo-a", FULL_OVERRIDE)  # 有設定但取不到 → 不得偷用
    backend.add_skill("demo-a", "cfg-qa")

    resp = client.post(
        "/skills/cfg-qa/invoke", json={"input": {"query": "hi"}}, headers=_headers()
    )

    assert resp.status_code == 200  # 全域預設回落，skill 照跑
    # 故障未寫入任何 per-config 快取（不留半成品）
    assert config_apply._config_deps_cache == {}
    # 編圖用的 deps 是全域單例 base（未套覆寫）→ top_k 為全域預設，非 FULL_OVERRIDE 的 25
    compiles = [d for (n, d) in build_spy if n == "cfg-qa"]
    assert compiles
    assert compiles[-1].default_top_k == settings.kb_query_top_k


def test_active_bad_json_falls_back_to_global(backend, fake_base_deps):
    """/active 回 200 但 body 非法 JSON → 回落全域預設（不炸、不寫快取）。"""
    backend.active_fail = "badjson"
    backend.set_active("demo-a", FULL_OVERRIDE)
    backend.add_skill("demo-a", "cfg-qa")

    resp = client.post(
        "/skills/cfg-qa/invoke", json={"input": {"query": "hi"}}, headers=_headers()
    )

    assert resp.status_code == 200
    assert config_apply._config_deps_cache == {}


def test_active_404_does_not_cross_tenant_fallback(backend, fake_base_deps, build_spy):
    """本租戶無 active（404）→ 全域預設；絕不改用他租戶（demo-b）的設定。"""
    backend.set_active("demo-b", FULL_OVERRIDE)  # 只有 demo-b 有 active
    backend.add_skill("demo-a", "cfg-qa")

    resp = client.post(
        "/skills/cfg-qa/invoke", json={"input": {"query": "hi"}}, headers=_headers(tenant_id="demo-a")
    )

    assert resp.status_code == 200
    compiles = [d for (n, d) in build_spy if n == "cfg-qa"]
    assert compiles[-1].default_top_k == settings.kb_query_top_k  # demo-a 走全域，非 demo-b 的 25
    assert ("demo-a", "v1") not in config_apply._config_deps_cache
