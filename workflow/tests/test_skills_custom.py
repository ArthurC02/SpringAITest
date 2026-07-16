"""自訂 Skill 的 workflow 側（AT4-09 / AT4-10 / AT4-11）+ /workflows 的 input_schema 回歸。

backend 一律用手寫 fake（monkeypatch httpx.AsyncClient.get，比照 test_api.py 對
httpx.AsyncClient.post 的做法）——不打網路、不引 mocking 套件。fake 依 X-Tenant-Id
分租戶存放，因此「跨租戶不可見」不是靠斷言假設，而是 fake 真的回 404。
"""

import httpx
import pytest
from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.main import app
from app.settings import settings
from app.skills import custom
from tests.kbquery_fakes import make_deps

client = TestClient(app)

INTERNAL_TOKEN = "internal-dev-token"


def _headers(tenant_id="demo-a", user_id="alice", role="USER", token=INTERNAL_TOKEN):
    headers = {}
    if token is not None:
        headers["X-Internal-Token"] = token
    if tenant_id is not None:
        headers["X-Tenant-Id"] = tenant_id
    if user_id is not None:
        headers["X-User-Id"] = user_id
    if role is not None:
        headers["X-User-Role"] = role
    return headers


# 自訂 skill 的定義：刻意只用 script 步驟 —— 不打網路、不呼叫 LLM，
# 但一樣經過編譯器強制附加的 audit_feedback（治理硬規則對自訂 skill 也成立）。
QUARTERLY_QA = """
name: quarterly_qa
description: 季報問答（租戶自訂）
required_role: USER
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - script: |
      state["final_answer"] = "echo: " + state["query"]
"""

ADMIN_ONLY = """
name: admin_only
description: 僅限管理員
required_role: ADMIN
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - script: |
      state["final_answer"] = "ok"
"""

# 存檔時驗過、事後失效的定義（引用了不存在的節點）
BROKEN = """
name: broken_skill
description: 事後失效
flow:
  - node: no_such_node
"""


class _FakeResponse:
    """httpx.Response 的最小替身：custom.py 只用到這三個成員。"""

    def __init__(self, status_code: int, payload=None):
        self.status_code = status_code
        self._payload = payload

    def raise_for_status(self) -> None:
        if self.status_code >= 400:
            raise httpx.HTTPStatusError(
                f"HTTP {self.status_code}", request=None, response=None
            )

    def json(self):
        return self._payload


class FakeSkillBackend:
    """backend 的 /api/skills 與 /api/skills/{name}（依租戶隔離）。

    down=True 模擬 backend 不可達（httpx.ConnectError，與真實連不上時同一個例外家族）。
    """

    def __init__(self, by_tenant: dict[str, list[dict]] | None = None, down=False):
        self.by_tenant = by_tenant or {}
        self.down = down
        self.calls: list[tuple[str, dict]] = []  # (url, headers)

    def install(self, monkeypatch) -> None:
        backend = self

        async def fake_get(self, url, headers=None, **kwargs):  # noqa: ANN001
            return backend.handle(url, headers or {})

        monkeypatch.setattr("httpx.AsyncClient.get", fake_get)

    def handle(self, url: str, headers: dict):
        self.calls.append((url, headers))
        if self.down:
            raise httpx.ConnectError("backend 不可達")
        assert headers.get("X-Internal-Token") == settings.internal_api_token
        rows = self.by_tenant.get(headers.get("X-Tenant-Id", ""), [])

        if url == "/api/skills":
            return _FakeResponse(200, [self._info(r) for r in rows if r["enabled"]])

        name = url.removeprefix("/api/skills/")
        row = next((r for r in rows if r["name"] == name), None)
        if row is None:  # 跨租戶／不存在一律 404（backend 的租戶隔離慣例）
            return _FakeResponse(404, None)
        return _FakeResponse(200, {**self._info(row), "definition": row["definition"]})

    @staticmethod
    def _info(row: dict) -> dict:
        return {
            "name": row["name"],
            "description": row["description"],
            "required_role": row["required_role"],
            "enabled": row["enabled"],
            "current_revision": row["revision"],
        }


def row(
    name: str,
    definition: str,
    description="季報問答（租戶自訂）",
    required_role="USER",
    revision=3,
    enabled=True,
) -> dict:
    return {
        "name": name,
        "definition": definition,
        "description": description,
        "required_role": required_role,
        "revision": revision,
        "enabled": enabled,
    }


@pytest.fixture
def fake_deps(monkeypatch):
    """自訂 skill 的依賴容器換成全假的（audit_repo 可回收斷言）。單例 → compiler 快取才命中。"""
    deps = make_deps({})
    monkeypatch.setattr(custom, "_deps", deps)
    return deps


@pytest.fixture
def backend(monkeypatch):
    def install(by_tenant=None, down=False) -> FakeSkillBackend:
        fake = FakeSkillBackend(by_tenant, down=down)
        fake.install(monkeypatch)
        return fake

    return install


# ---------------------------------------------------------------------------
# AT4-09 GET /skills 合併內建 + 自訂
# ---------------------------------------------------------------------------


def test_list_skills_merges_builtin_and_custom(backend, fake_deps):
    """【AT4-09】kb_query source=builtin、quarterly_qa source=custom，兩者皆有 revision。"""
    fake = backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA, revision=3)]})

    resp = client.get("/skills", headers=_headers())

    assert resp.status_code == 200
    body = {item["name"]: item for item in resp.json()}
    assert body["kb_query"]["source"] == "builtin"
    assert body["kb_query"]["revision"] == 1
    assert body["quarterly_qa"]["source"] == "custom"
    assert body["quarterly_qa"]["revision"] == 3
    assert body["quarterly_qa"]["required_role"] == "USER"
    assert body["quarterly_qa"]["description"] == "季報問答（租戶自訂）"
    # 前端動態渲染執行表單的依據：自訂 skill 的 input_schema 逐鍵比對（不放寬）
    assert body["quarterly_qa"]["input_schema"] == {
        "query": {"type": "str", "required": True, "min_length": 1, "default": None}
    }
    assert body["kb_query"]["input_schema"]["query"]["required"] is True
    # 出站請求帶了內部密鑰與租戶標頭（fake 內已 assert token；這裡釘住租戶）
    assert all(h["X-Tenant-Id"] == "demo-a" for _, h in fake.calls)


def test_list_skills_is_tenant_scoped(backend, fake_deps):
    """跨租戶不可見：demo-b 列不到 demo-a 的自訂 skill，但內建 skill 照列。"""
    backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA)]})

    names = [i["name"] for i in client.get("/skills", headers=_headers(tenant_id="demo-b")).json()]

    assert "quarterly_qa" not in names
    assert "kb_query" in names


def test_list_skills_survives_backend_down(backend, fake_deps):
    """backend 不可達 → 清單端點仍回 200 + 內建 skill（不是 500）。"""
    backend(down=True)

    resp = client.get("/skills", headers=_headers())

    assert resp.status_code == 200
    names = [i["name"] for i in resp.json()]
    # 內建集合 = kb_query + 五支 template_* 骨架（設計 §5.4 縫④：骨架亦入 GET /skills，
    # 由前端過濾 template_ 前綴）。backend 不可達時只缺自訂項，內建照列。
    assert names == [
        "kb_query",
        "template_compare",
        "template_infer",
        "template_inspire",
        "template_retrieval",
        "template_stats",
    ]


def test_list_skills_keeps_entry_when_definition_unreadable(backend, fake_deps):
    """單筆定義取不到／壞掉 → 該筆仍列得出來，只有 input_schema 退成 null。"""
    backend({"demo-a": [row("quarterly_qa", "name: quarterly_qa\nflow: [")]})  # 壞 YAML

    body = {i["name"]: i for i in client.get("/skills", headers=_headers()).json()}

    assert body["quarterly_qa"]["source"] == "custom"
    assert body["quarterly_qa"]["input_schema"] is None


# ---------------------------------------------------------------------------
# AT4-10 POST /skills/{name}/invoke：404 → 403 → 422 → 200 / 500
# ---------------------------------------------------------------------------


def test_invoke_custom_skill_happy_path(backend, fake_deps):
    """決策表的另一半：合法輸入 → 200 {skill, output}，稽核照樣落地。"""
    backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA)]})

    resp = client.post(
        "/skills/quarterly_qa/invoke",
        json={"input": {"query": "2025Q3 稅後淨利？"}},
        headers=_headers(),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["skill"] == "quarterly_qa"
    assert body["output"]["final_answer"] == "echo: 2025Q3 稅後淨利？"
    # 治理硬規則對自訂 skill 一樣成立：稽核節點強制附加、audit trail 落地
    assert [t["node_name"] for t in body["output"]["trace"]][-1] == "audit_feedback"
    assert len(fake_deps.audit_repo.saved) == 1
    assert not any(k.startswith("__") for k in body["output"])


def test_invoke_unknown_custom_skill_returns_404(backend, fake_deps):
    backend({"demo-a": []})

    resp = client.post(
        "/skills/nope/invoke", json={"input": {"query": "x"}}, headers=_headers()
    )

    assert resp.status_code == 404
    assert resp.json()["detail"]["error"] == "workflow_not_found"


def test_invoke_custom_skill_cross_tenant_returns_404(backend, fake_deps):
    """跨租戶不可見：demo-b 執行 demo-a 的 skill → 404（不洩漏存在性）。"""
    backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA)]})

    resp = client.post(
        "/skills/quarterly_qa/invoke",
        json={"input": {"query": "x"}},
        headers=_headers(tenant_id="demo-b"),
    )

    assert resp.status_code == 404


def test_invoke_disabled_custom_skill_returns_404(backend, fake_deps):
    """軟刪（enabled=false）等同不存在。"""
    backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA, enabled=False)]})

    resp = client.post(
        "/skills/quarterly_qa/invoke",
        json={"input": {"query": "x"}},
        headers=_headers(),
    )

    assert resp.status_code == 404


def test_invoke_custom_admin_skill_forbidden_for_user(backend, fake_deps):
    """required_role: ADMIN 的自訂 skill 被 USER 呼叫 → 403（順序上先於 422：input 是空的）。"""
    backend({"demo-a": [row("admin_only", ADMIN_ONLY, required_role="ADMIN")]})

    resp = client.post(
        "/skills/admin_only/invoke", json={"input": {}}, headers=_headers()
    )

    assert resp.status_code == 403
    assert resp.json()["detail"]["error"] == "workflow_forbidden"

    # 決策表另一半：ADMIN 過了角色關卡，才輪到 input 驗證（422）
    forbidden_then_422 = client.post(
        "/skills/admin_only/invoke", json={"input": {}}, headers=_headers(role="ADMIN")
    )
    assert forbidden_then_422.status_code == 422


@pytest.mark.parametrize(
    "input_body", [{}, {"query": ""}], ids=["missing-query", "blank-query"]
)
def test_invoke_custom_skill_invalid_input_returns_422(backend, fake_deps, input_body):
    """input_schema 的 required / min_length 由動態 Pydantic model 執行（同內建 skill）。"""
    backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA)]})

    resp = client.post(
        "/skills/quarterly_qa/invoke", json={"input": input_body}, headers=_headers()
    )

    assert resp.status_code == 422
    assert resp.json()["detail"]["error"] == "workflow_input_invalid"


def test_invoke_custom_skill_backend_down_returns_controlled_500(backend, fake_deps):
    """backend 不可達 → 受控的 500（不是未捕捉例外，也不是謊報 404「skill 不存在」）。"""
    backend(down=True)

    resp = client.post(
        "/skills/quarterly_qa/invoke",
        json={"input": {"query": "x"}},
        headers=_headers(),
    )

    assert resp.status_code == 500
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_execution_failed"
    assert detail["message"]  # 錯誤訊息說得出是取定義失敗，不是空的


def test_invoke_custom_skill_with_broken_definition_returns_500(backend, fake_deps):
    """DB 裡的定義事後失效（節點被下架）→ 500，不是 422：不是呼叫端的錯。"""
    backend({"demo-a": [row("broken_skill", BROKEN)]})

    resp = client.post(
        "/skills/broken_skill/invoke", json={"input": {}}, headers=_headers()
    )

    assert resp.status_code == 500
    assert resp.json()["detail"]["error"] == "workflow_execution_failed"


def test_invoke_custom_skill_reserved_keys_cannot_be_forged(backend, fake_deps):
    """保留鍵剝除對自訂 skill 一樣成立：input 夾帶 tenant_id / query_id 一律無效。"""
    backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA)]})

    resp = client.post(
        "/skills/quarterly_qa/invoke",
        json={
            "input": {
                "query": "x",
                "tenant_id": "evil-tenant",
                "query_id": "FORGED-ID",
                "original_query": "無害的問題",
            }
        },
        headers=_headers(tenant_id="demo-a"),
    )

    assert resp.status_code == 200
    trail = fake_deps.audit_repo.saved[0]
    assert trail.query_id != "FORGED-ID"
    assert trail.original_query != "無害的問題"


# ---------------------------------------------------------------------------
# 編譯快取：(name, revision) —— 規格 §4「同一 skill revision 只編譯一次」
# ---------------------------------------------------------------------------


def test_custom_skill_compiles_once_per_revision(backend, fake_deps, monkeypatch):
    """同 revision 連續 invoke 只建圖一次；revision bump → 重編。"""
    builds: list[str] = []
    real_build = compiler._build_graph

    def counting_build(skill, deps):
        builds.append(f"{skill.name}@{skill.revision}")
        return real_build(skill, deps)

    monkeypatch.setattr(compiler, "_build_graph", counting_build)
    fake = backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA, revision=3)]})

    body = {"input": {"query": "x"}}
    assert client.post("/skills/quarterly_qa/invoke", json=body, headers=_headers()).status_code == 200
    assert client.post("/skills/quarterly_qa/invoke", json=body, headers=_headers()).status_code == 200
    assert builds == ["quarterly_qa@3"]  # 第二次命中快取

    # backend 出新 revision → 重編（快取鍵含 revision 與內容雜湊）
    fake.by_tenant["demo-a"] = [
        row("quarterly_qa", QUARTERLY_QA.replace("echo: ", "echo2: "), revision=4)
    ]
    assert client.post("/skills/quarterly_qa/invoke", json=body, headers=_headers()).status_code == 200
    assert builds == ["quarterly_qa@3", "quarterly_qa@4"]


# ---------------------------------------------------------------------------
# AT4-11 POST /skills/validate：中繼資料 + 無副作用
# ---------------------------------------------------------------------------


def test_validate_returns_skill_metadata_when_valid(backend, fake_deps):
    """【AT4-11】valid=true → 一併回 skill 中繼資料（backend 寫入 DB 的唯一資料來源）。"""
    backend({"demo-a": []})

    resp = client.post(
        "/skills/validate", json={"definition": QUARTERLY_QA}, headers=_headers()
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is True
    assert body["errors"] == []
    assert body["skill"] == {
        "name": "quarterly_qa",
        "description": "季報問答（租戶自訂）",
        "required_role": "USER",
        "input_schema": {"query": {"type": "str", "required": True, "min_length": 1}},
    }


def test_validate_omits_skill_when_invalid(backend, fake_deps):
    """【AT4-11】valid=false → 不得有 skill 欄位（backend 以它的有無決定能不能寫）。"""
    backend({"demo-a": []})

    resp = client.post(
        "/skills/validate", json={"definition": BROKEN}, headers=_headers()
    )

    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "unknown_node"
    assert "skill" not in body


def test_validate_admin_skill_metadata_carries_required_role(backend, fake_deps):
    """required_role 直接來自 YAML：backend 靠這個欄位落 DB，不可預設成 USER。"""
    backend({"demo-a": []})

    resp = client.post(
        "/skills/validate", json={"definition": ADMIN_ONLY}, headers=_headers()
    )

    assert resp.json()["skill"]["required_role"] == "ADMIN"


def test_validate_has_no_side_effects_on_custom_catalog(backend, fake_deps):
    """【AT4-11】驗證三次不得寫入任何東西：清單前後一致、也不會註冊成可執行 skill。"""
    backend({"demo-a": [row("quarterly_qa", QUARTERLY_QA)]})

    before = client.get("/skills", headers=_headers()).json()
    for definition in (QUARTERLY_QA, BROKEN, QUARTERLY_QA):
        client.post("/skills/validate", json={"definition": definition}, headers=_headers())
    after = client.get("/skills", headers=_headers()).json()

    assert after == before
    assert skills.get("quarterly_qa") is None  # 自訂 skill 不會被寫進內建註冊表
    assert skills.get("broken_skill") is None


# ---------------------------------------------------------------------------
# GET /workflows 的 input_schema（前端回歸修復；新增欄位，既有欄位不動）
# ---------------------------------------------------------------------------


def test_list_workflows_exposes_input_schema():
    """有 input_model 的工作流回同一形狀的 input_schema；沒宣告的回 null。"""
    body = {i["name"]: i for i in client.get("/workflows", headers=_headers()).json()}

    assert body["rag_qa"]["input_schema"] == {
        "question": {"type": "str", "required": True, "min_length": 1, "default": None}
    }
    assert body["kb_query"]["input_schema"]["query"]["min_length"] == 1
    assert body["analyze_report"]["input_schema"]["topic"]["required"] is True
    assert body["summarize"]["input_schema"] is None  # 沒宣告 input_model
    # 既有欄位一個沒動（AT-REG-01 只禁止刪欄位／改型別）
    assert body["analyze_report"]["required_role"] == "ADMIN"
    assert body["summarize"]["description"]
