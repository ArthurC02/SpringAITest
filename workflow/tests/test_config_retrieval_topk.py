"""縫⑦ runtime apply：通用 retrieve@1.0 的 retrieval.top_k 執行期覆寫（SSR-P4-013）。

設計 §10 縫⑦ 原假設「不改 retrieve，只在 compose 期把 top_k 烤進 YAML」，使用者裁決推翻：
已存的 compare/stats 型 skill（用通用 retrieve@1.0）**不需重新 compose**，靠 activate 一個帶
retrieval.top_k 的 Configuration Set，就能在下次 invoke 讓 retrieve 執行期收到覆寫值。

取值精度（retrieve.py）：per-config 執行期 state seed（Configuration Set，最高）＞ compose 期
SLOT（build 參數 top_k）＞ 模組全域 settings.retrieval_top_k。

驗法：以 HTTP 端點 invoke 一支「已存」的 builtin script probe（含 retrieve@1.0，SLOT top_k=8），
攔截 retrieve 打向 backend /api/retrieval/search 的 json["top_k"] 斷言實收值。整條走真 compiler /
Harness / config_apply.resolve；backend 兩個端點（/active、/retrieval/search）皆手寫 fake，不打網路。

per-config deps 快取是模組全域 → 沿用 test_config_apply 的清快取 fixture 慣例（本檔自帶）。
無 pytest-asyncio，端點測試走 TestClient（同步）。
"""

import httpx
import pytest
from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.engine import skill as skill_mod
from app.main import app
from app.settings import settings
from app.skills import config_apply, custom
from tests.conftest import auth_headers, install_fake_get
from tests.kbquery_fakes import make_deps

client = TestClient(app)

# 已存 skill：薄檢索（通用 retrieve@1.0，SLOT top_k=8）+ 一段 no-op script。模擬 compare/stats
# 範本 compose 後的產物；SLOT 值刻意 = 8 ≠ 全域 4，才驗得出「未覆寫回落 SLOT，不是回落全域」。
RETRIEVE_SKILL = """
name: {name}
description: 縫⑦ runtime-apply 用的 retrieve probe（SLOT top_k=8）
required_role: USER
input_schema:
  query: {{type: str, required: true, min_length: 1}}
flow:
  - node: retrieve@1.0
    params:
      query_key: query
      top_k: 8                    # __SLOT_topK__
  - script: |
      state["final_answer"] = "done"
"""


class _ActiveResponse:
    def __init__(self, status_code, payload=None):
        self.status_code = status_code
        self._payload = payload

    def raise_for_status(self):
        if self.status_code >= 400:
            raise httpx.HTTPStatusError(f"HTTP {self.status_code}", request=None, response=None)

    def json(self):
        return self._payload


class _RetrieveResponse:
    """backend /api/retrieval/search 的回應（空 chunks 足以驗 top_k；不需真檢索結果）。"""

    def raise_for_status(self):
        return None

    def json(self):
        return {"chunks": []}


@pytest.fixture(autouse=True)
def _clear_caches():
    config_apply._config_deps_cache.clear()
    yield
    config_apply._config_deps_cache.clear()


@pytest.fixture
def fake_base_deps(monkeypatch):
    """custom 共用單例換 fake（per_config 由此 base 衍生，各埠不打網路）。"""
    base = make_deps({})
    monkeypatch.setattr(custom, "_deps", base)
    return base


@pytest.fixture
def captured_top_k(monkeypatch):
    """攔 retrieve 打 backend 的 POST，依序記下每次的 top_k。"""
    seen: list[int] = []

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        assert url == "/api/retrieval/search"
        seen.append(json["top_k"])
        return _RetrieveResponse()

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)
    return seen


class _FakeActiveBackend:
    """只服務 /api/configuration-sets/active（依 X-Tenant-Id 隔離）。"""

    def __init__(self):
        self.active_by_tenant: dict[str, dict] = {}

    def set_active(self, tenant, values, updated_at="v1"):
        self.active_by_tenant[tenant] = {
            "id": "00000000-0000-0000-0000-000000000001",
            "name": "set1",
            "is_active": True,
            "values": values,
            "created_by": "admin",
            "created_at": "2026-01-01T00:00:00Z",
            "updated_at": updated_at,
        }

    def handle(self, url, headers):
        assert url == "/api/configuration-sets/active"
        assert headers.get("X-Internal-Token") == settings.internal_api_token
        tenant = headers.get("X-Tenant-Id", "")
        active = self.active_by_tenant.get(tenant)
        if active is None:
            return _ActiveResponse(404, None)
        return _ActiveResponse(200, active)


@pytest.fixture
def active_backend(monkeypatch):
    fake = _FakeActiveBackend()
    install_fake_get(monkeypatch, fake.handle)
    return fake


def _register_builtin_skill(name, template, base_deps):
    """把 template 以 source='builtin' 塞進註冊表（模擬啟動預編圖），回清理函式。"""
    skill = skill_mod.parse_source(template.format(name=name))
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


@pytest.fixture
def probe(fake_base_deps):
    cleanup = _register_builtin_skill("cfg-topk-probe", RETRIEVE_SKILL, fake_base_deps)
    yield "cfg-topk-probe"
    cleanup()


def _invoke(name, tenant_id="demo-a"):
    resp = client.post(
        f"/skills/{name}/invoke",
        json={"input": {"query": "q"}},
        headers=auth_headers(tenant_id=tenant_id),
    )
    assert resp.status_code == 200, resp.text
    return resp


# ---------------------------------------------------------------------------
# SSR-P4-013 核心：activate retrieval.top_k=17 → 既有 skill 免重 compose → retrieve 實收 17
# ---------------------------------------------------------------------------


def test_active_retrieval_topk_override_reaches_retrieve(active_backend, probe, captured_top_k):
    """【SSR-P4-013】activate 帶 retrieval.top_k=17 → invoke 既有 skill（不重 compose）→ retrieve 收 17。"""
    active_backend.set_active("demo-a", {"retrieval.top_k": 17})
    _invoke(probe)
    assert captured_top_k == [17]


def test_no_override_retrieve_gets_slot_value(active_backend, probe, captured_top_k):
    """無 active（404）→ 不 seed → retrieve 回落 compose 期 SLOT 值 8（非模組全域 4），零行為變更。"""
    assert 8 != settings.retrieval_top_k  # SLOT 刻意 ≠ 全域，才驗得出精度
    _invoke(probe)
    assert captured_top_k == [8]


def _unreachable(url, headers):
    """backend 連不上（httpx.ConnectError 由 client.get 直接拋）。"""
    raise httpx.ConnectError("backend 不可達")


def _server_error(url, headers):
    """backend 回 5xx（由 _fetch 的 raise_for_status 轉成 BackendUnavailable）。"""
    return _ActiveResponse(500, None)


@pytest.mark.parametrize("handler", [_unreachable, _server_error], ids=["unreachable", "5xx"])
def test_backend_failure_falls_back_to_slot_value(monkeypatch, probe, captured_top_k, handler):
    """取 active 失敗（不可達／5xx，非 404）→ resolve 記 log 回全域路徑 → retrieve 仍收 SLOT 8。

    與 404 是兩條不同程式路徑（except Exception vs. 空 values 早退），故障不得跨租戶回落，
    也不得寫 per-config 快取。
    """
    install_fake_get(monkeypatch, handler)
    _invoke(probe)
    assert captured_top_k == [8]
    assert config_apply._config_deps_cache == {}  # 故障 → 不寫 per-config 快取


def test_other_keys_overridden_but_retrieval_topk_absent_keeps_slot(
    active_backend, probe, captured_top_k
):
    """active 覆寫別的鍵、但不含 retrieval.top_k → 不 seed → retrieve 仍收 SLOT 8（精度 SLOT > 全域）。"""
    active_backend.set_active("demo-a", {"kb_query.top_k": 25})
    _invoke(probe)
    assert captured_top_k == [8]


# ---------------------------------------------------------------------------
# SSR-P4-013 版本序列：V1=4 → V2=17，換 Configuration Set 版本不需重 compose
# ---------------------------------------------------------------------------


def test_version_bump_reflects_new_topk_without_recompose(active_backend, probe, captured_top_k):
    """先 V1 retrieval.top_k=4 invoke，再 activate/update 到 V2=17，同一 skill 再 invoke → 依序 4、17。"""
    active_backend.set_active("demo-a", {"retrieval.top_k": 4}, updated_at="v1")
    _invoke(probe)
    active_backend.set_active("demo-a", {"retrieval.top_k": 17}, updated_at="v2")
    _invoke(probe)
    assert captured_top_k == [4, 17]


# ---------------------------------------------------------------------------
# 邊界：範圍 1~50 的 on-point（seed 值忠實流到 retrieve，不被夾扣）。off-point（0/51）拒收
# 屬 backend ConfigurationValues 驗證層（SSR-P4-008），workflow 信任已驗證的值。
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("value", [1, 50])
def test_boundary_topk_on_point_passes_through(active_backend, probe, captured_top_k, value):
    active_backend.set_active("demo-a", {"retrieval.top_k": value})
    _invoke(probe)
    assert captured_top_k == [value]


# ---------------------------------------------------------------------------
# 租戶隔離：A 的 retrieval.top_k 不影響 B（相同 version 字串、不同 values）
# ---------------------------------------------------------------------------


def test_tenant_isolation_topk_not_cross_contaminated(active_backend, fake_base_deps, captured_top_k):
    """demo-a=40、demo-b=7（同 updated_at 字串）→ 各收各的，互不污染。"""
    active_backend.set_active("demo-a", {"retrieval.top_k": 40}, updated_at="shared-v")
    active_backend.set_active("demo-b", {"retrieval.top_k": 7}, updated_at="shared-v")
    cleanup = _register_builtin_skill("cfg-topk-probe", RETRIEVE_SKILL, fake_base_deps)
    try:
        _invoke("cfg-topk-probe", tenant_id="demo-a")
        _invoke("cfg-topk-probe", tenant_id="demo-b")
        assert captured_top_k == [40, 7]
    finally:
        cleanup()


# ---------------------------------------------------------------------------
# 全域路徑零回歸：本租戶無 active 時，只有 demo-b 有設定也絕不跨租戶偷用
# ---------------------------------------------------------------------------


# ---------------------------------------------------------------------------
# 只讀守護：script 竄改 retrieval_top_k 被剝除，seed 值守住（三條寫入路徑統一封鎖）
# ---------------------------------------------------------------------------

# retrieve 前放一段 script 試圖竄改伺服器注入的只讀 seed。script_runner 的
# FORBIDDEN_WRITE_KEYS 須含 CONFIG_SEED_KEYS，這行寫入才會被剝除。
TAMPER_SKILL = """
name: {name}
description: 縫⑦ 只讀守護回歸（script 試圖竄改 retrieval_top_k）
required_role: USER
input_schema:
  query: {{type: str, required: true, min_length: 1}}
flow:
  - script: |
      state["retrieval_top_k"] = 999
  - node: retrieve@1.0
    params:
      query_key: query
      top_k: 8                    # __SLOT_topK__
"""


def test_script_cannot_tamper_seeded_retrieval_top_k(active_backend, fake_base_deps, captured_top_k):
    """script 在 retrieve 前寫 retrieval_top_k=999 → 被剝除 → retrieve 實收 active seed 17，非 999。

    還原 bug（FORBIDDEN_WRITE_KEYS 拿掉 CONFIG_SEED_KEYS）→ script 寫入生效 → retrieve 收 999 → 紅。
    """
    active_backend.set_active("demo-a", {"retrieval.top_k": 17})
    cleanup = _register_builtin_skill("cfg-topk-tamper", TAMPER_SKILL, fake_base_deps)
    try:
        _invoke("cfg-topk-tamper")
        assert captured_top_k == [17]
    finally:
        cleanup()


def test_active_only_for_other_tenant_does_not_leak(active_backend, probe, captured_top_k):
    active_backend.set_active("demo-b", {"retrieval.top_k": 17})  # 只有 demo-b
    _invoke(probe, tenant_id="demo-a")
    assert captured_top_k == [8]  # demo-a 走 SLOT，不偷用 demo-b 的 17
    assert config_apply._config_deps_cache == {}  # 無覆寫 → 不寫 per-config 快取
