"""自訂 Skill 的 workflow 側（AT4-09 / AT4-10 / AT4-11）。

backend 一律用手寫 fake（monkeypatch httpx.AsyncClient.get）——不打網路、不引 mocking
套件。fake 依 X-Tenant-Id 分租戶存放，因此「跨租戶不可見」不是靠斷言假設，而是 fake
真的回 404。
"""

import hashlib
import httpx
import pytest
from fastapi.testclient import TestClient

from app import skills
from app.engine import compiler
from app.main import app
from app.settings import settings
from app.skills import custom
from tests.conftest import auth_headers as _headers, install_fake_get
from tests.kbquery_fakes import make_deps

client = TestClient(app)


# 自訂 skill 的定義：刻意只用 script 步驟 —— 不打網路、不呼叫 LLM，
# 但一樣經過編譯器強制附加的 audit_feedback（治理硬規則對自訂 skill 也成立）。
QUARTERLY_QA = """
name: quarterly-qa
description: 季報問答（租戶自訂）
required_role: USER
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - script: |
      state["final_answer"] = "echo: " + state["query"]
"""

ADMIN_ONLY = """
name: admin-only
description: 僅限管理員
required_role: ADMIN
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - script: |
      state["final_answer"] = "ok"
"""

# 無 script 步驟的合法定義：撰寫者角色 gate 不該擋 USER 作者提交這種定義。
NODE_ONLY = """
name: node-only-probe
description: 無 script 步驟
input_schema:
  query: {type: str, required: true, min_length: 1}
flow:
  - node: query_intake@1.0
"""

# 存檔時驗過、事後失效的定義（引用了不存在的節點）
BROKEN = """
name: broken-skill
description: 事後失效
flow:
  - node: no_such_node
"""

AGENTIC_CANONICAL = """name: sales-helper
description: 銷售小幫手
metadata:
  kind: agentic
  required_role: USER
  timeout_seconds: "30"
  input_schema: '{"query":{"type":"str","required":true}}'
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
        return _FakeResponse(200, {
            **self._info(row),
            "definition": row["definition"],
            "definition_sha256": row.get("definition_sha256"),
        })

    @staticmethod
    def _info(row: dict) -> dict:
        return {
            "name": row["name"],
            "description": row["description"],
            "required_role": row["required_role"],
            "enabled": row["enabled"],
            "current_revision": row["revision"],
            "kind": row["kind"],
        }


def row(
    name: str,
    definition: str,
    description="季報問答（租戶自訂）",
    required_role="USER",
    revision=3,
    enabled=True,
    kind="flow",
) -> dict:
    return {
        "name": name,
        "definition": definition,
        "definition_sha256": hashlib.sha256(definition.encode("utf-8")).hexdigest(),
        "description": description,
        "required_role": required_role,
        "revision": revision,
        "enabled": enabled,
        "kind": kind,
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
        install_fake_get(monkeypatch, fake.handle)
        return fake

    return install


# ---------------------------------------------------------------------------
# AT4-09 GET /skills 合併內建 + 自訂
# ---------------------------------------------------------------------------


def test_list_skills_merges_builtin_and_custom(backend, fake_deps):
    """【AT4-09】kb-query source=builtin、quarterly-qa source=custom，兩者皆有 revision。"""
    fake = backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA, revision=3)]})

    resp = client.get("/skills", headers=_headers())

    assert resp.status_code == 200
    body = {item["name"]: item for item in resp.json()}
    assert body["kb-query"]["source"] == "builtin"
    assert body["kb-query"]["bindable"] is False
    assert body["quarterly-qa"]["bindable"] is True
    assert body["kb-query"]["revision"] == 1
    assert body["quarterly-qa"]["source"] == "custom"
    assert body["quarterly-qa"]["revision"] == 3
    assert body["quarterly-qa"]["required_role"] == "USER"
    assert body["quarterly-qa"]["description"] == "季報問答（租戶自訂）"
    # 前端動態渲染執行表單的依據：自訂 skill 的 input_schema 逐鍵比對（不放寬）
    assert body["quarterly-qa"]["input_schema"] == {
        "query": {"type": "str", "required": True, "min_length": 1, "default": None}
    }
    assert body["kb-query"]["input_schema"]["query"]["required"] is True
    # 出站請求帶了內部密鑰與租戶標頭（fake 內已 assert token；這裡釘住租戶）
    assert all(h["X-Tenant-Id"] == "demo-a" for _, h in fake.calls)


@pytest.mark.parametrize("revision", [None, 0, -1, True, 1.5, "3"])
def test_custom_catalog_without_positive_persisted_revision_is_not_bindable(
    backend, fake_deps, revision
):
    backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA, revision=revision)]})

    body = {item["name"]: item for item in client.get("/skills", headers=_headers()).json()}

    assert body["quarterly-qa"]["revision"] is None
    assert body["quarterly-qa"]["bindable"] is False


def test_custom_catalog_declares_flow_and_agentic_kind(backend, fake_deps):
    backend(
        {
            "demo-a": [
                row("quarterly-qa", QUARTERLY_QA),
                row(
                    "sales-helper",
                    AGENTIC_CANONICAL,
                    description="銷售小幫手",
                    kind="agentic",
                ),
            ]
        }
    )

    body = {item["name"]: item for item in client.get("/skills", headers=_headers()).json()}
    assert body["quarterly-qa"]["kind"] == "flow"
    assert body["sales-helper"]["kind"] == "agentic"
    assert body["sales-helper"]["input_schema"] == {
        "query": {"type": "str", "required": True, "min_length": None, "default": None}
    }


def test_custom_catalog_kind_does_not_sniff_definition(backend, fake_deps):
    """Backend kind 是唯一事實來源；即使 definition 外觀像 flow 也不改判。"""
    backend(
        {
            "demo-a": [
                row(
                    "trusted-agentic-kind",
                    QUARTERLY_QA,
                    kind="agentic",
                )
            ]
        }
    )

    body = {item["name"]: item for item in client.get("/skills", headers=_headers()).json()}

    assert body["trusted-agentic-kind"]["kind"] == "agentic"
    assert body["trusted-agentic-kind"]["input_schema"] is None


def test_list_skills_is_tenant_scoped(backend, fake_deps):
    """跨租戶不可見：demo-b 列不到 demo-a 的自訂 skill，但內建 skill 照列。"""
    backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA)]})

    names = [i["name"] for i in client.get("/skills", headers=_headers(tenant_id="demo-b")).json()]

    assert "quarterly-qa" not in names
    assert "kb-query" in names


def test_list_skills_survives_backend_down(backend, fake_deps):
    """backend 不可達 → 清單端點仍回 200 + 內建 skill（不是 500）。"""
    backend(down=True)

    resp = client.get("/skills", headers=_headers())

    assert resp.status_code == 200
    names = [i["name"] for i in resp.json()]
    # 內建集合 = kb-query + 五支 template-* 骨架（設計 §5.4 縫④：骨架亦入 GET /skills，
    # 由前端過濾 template- 前綴）+ Node-First 遷移（Phase 1）新增的四顆內建 skill
    # （analyze-report/rag-qa/summarize/triage）。backend 不可達時只缺自訂項，內建照列。
    assert names == [
        "analyze-report",
        "kb-query",
        "rag-qa",
        "summarize",
        "template-compare",
        "template-infer",
        "template-inspire",
        "template-retrieval",
        "template-stats",
        "triage",
    ]


def test_list_skills_keeps_entry_when_definition_unreadable(backend, fake_deps):
    """單筆定義取不到／壞掉 → 該筆仍列得出來，只有 input_schema 退成 null。"""
    backend({"demo-a": [row("quarterly-qa", "name: quarterly-qa\nflow: [")]})  # 壞 YAML

    body = {i["name"]: i for i in client.get("/skills", headers=_headers()).json()}

    assert body["quarterly-qa"]["source"] == "custom"
    assert body["quarterly-qa"]["input_schema"] is None


# ---------------------------------------------------------------------------
# AT4-10 POST /skills/{name}/invoke：404 → 403 → 422 → 200 / 500
# ---------------------------------------------------------------------------


def test_invoke_custom_skill_happy_path(backend, fake_deps):
    """決策表的另一半：合法輸入 → 200 {skill, output}，稽核照樣落地。"""
    backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA)]})

    resp = client.post(
        "/skills/quarterly-qa/invoke",
        json={"input": {"query": "2025Q3 稅後淨利？"}},
        headers=_headers(),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["skill"] == "quarterly-qa"
    assert body["output"]["final_answer"] == "echo: 2025Q3 稅後淨利？"
    # 治理硬規則對自訂 skill 一樣成立：稽核節點強制附加、audit trail 落地
    assert "trace" not in body["output"]
    assert len(fake_deps.audit_repo.saved) == 1
    assert not any(k.startswith("__") for k in body["output"])


@pytest.mark.parametrize("digest", [None, "0" * 64], ids=["missing", "mismatch"])
def test_invoke_custom_flow_fails_closed_before_compile_on_bad_definition_hash(
    backend, fake_deps, monkeypatch, digest
):
    artifact = row("quarterly-qa", QUARTERLY_QA)
    artifact["definition_sha256"] = digest
    backend({"demo-a": [artifact]})
    compile_calls = 0
    original_compile = compiler.compile

    def counting_compile(*args, **kwargs):
        nonlocal compile_calls
        compile_calls += 1
        return original_compile(*args, **kwargs)

    monkeypatch.setattr(compiler, "compile", counting_compile)
    resp = client.post(
        "/skills/quarterly-qa/invoke",
        json={"input": {"query": "q"}},
        headers=_headers(),
    )
    assert resp.status_code == 500
    assert resp.json()["detail"]["error"] == "workflow_execution_failed"
    assert compile_calls == 0


def test_invoke_unknown_custom_skill_returns_404(backend, fake_deps):
    backend({"demo-a": []})

    resp = client.post(
        "/skills/nope/invoke", json={"input": {"query": "x"}}, headers=_headers()
    )

    assert resp.status_code == 404
    assert resp.json()["detail"]["error"] == "workflow_not_found"


def test_invoke_custom_skill_cross_tenant_returns_404(backend, fake_deps):
    """跨租戶不可見：demo-b 執行 demo-a 的 skill → 404（不洩漏存在性）。"""
    backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA)]})

    resp = client.post(
        "/skills/quarterly-qa/invoke",
        json={"input": {"query": "x"}},
        headers=_headers(tenant_id="demo-b"),
    )

    assert resp.status_code == 404


def test_invoke_disabled_custom_skill_returns_404(backend, fake_deps):
    """軟刪（enabled=false）等同不存在。"""
    backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA, enabled=False)]})

    resp = client.post(
        "/skills/quarterly-qa/invoke",
        json={"input": {"query": "x"}},
        headers=_headers(),
    )

    assert resp.status_code == 404


def test_invoke_custom_admin_skill_forbidden_for_user(backend, fake_deps):
    """required_role: ADMIN 的自訂 skill 被 USER 呼叫 → 403（順序上先於 422：input 是空的）。"""
    backend({"demo-a": [row("admin-only", ADMIN_ONLY, required_role="ADMIN")]})

    resp = client.post(
        "/skills/admin-only/invoke", json={"input": {}}, headers=_headers()
    )

    assert resp.status_code == 403
    assert resp.json()["detail"]["error"] == "workflow_forbidden"

    # 決策表另一半：ADMIN 過了角色關卡，才輪到 input 驗證（422）
    forbidden_then_422 = client.post(
        "/skills/admin-only/invoke", json={"input": {}}, headers=_headers(role="ADMIN")
    )
    assert forbidden_then_422.status_code == 422


@pytest.mark.parametrize(
    "input_body", [{}, {"query": ""}], ids=["missing-query", "blank-query"]
)
def test_invoke_custom_skill_invalid_input_returns_422(backend, fake_deps, input_body):
    """input_schema 的 required / min_length 由動態 Pydantic model 執行（同內建 skill）。"""
    backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA)]})

    resp = client.post(
        "/skills/quarterly-qa/invoke", json={"input": input_body}, headers=_headers()
    )

    assert resp.status_code == 422
    assert resp.json()["detail"]["error"] == "workflow_input_invalid"


def test_invoke_custom_skill_backend_down_returns_controlled_500(backend, fake_deps):
    """backend 不可達 → 受控的 500（不是未捕捉例外，也不是謊報 404「skill 不存在」）。"""
    backend(down=True)

    resp = client.post(
        "/skills/quarterly-qa/invoke",
        json={"input": {"query": "x"}},
        headers=_headers(),
    )

    assert resp.status_code == 500
    detail = resp.json()["detail"]
    assert detail["error"] == "workflow_execution_failed"
    assert detail["message"]  # 錯誤訊息說得出是取定義失敗，不是空的


def test_invoke_custom_skill_with_broken_definition_returns_500(backend, fake_deps):
    """DB 裡的定義事後失效（節點被下架）→ 500，不是 422：不是呼叫端的錯。"""
    backend({"demo-a": [row("broken-skill", BROKEN)]})

    resp = client.post(
        "/skills/broken-skill/invoke", json={"input": {}}, headers=_headers()
    )

    assert resp.status_code == 500
    assert resp.json()["detail"]["error"] == "workflow_execution_failed"


def test_invoke_custom_skill_reserved_keys_cannot_be_forged(backend, fake_deps):
    """保留鍵剝除對自訂 skill 一樣成立：input 夾帶 tenant_id / query_id 一律無效。"""
    backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA)]})

    resp = client.post(
        "/skills/quarterly-qa/invoke",
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
    fake = backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA, revision=3)]})

    body = {"input": {"query": "x"}}
    assert client.post("/skills/quarterly-qa/invoke", json=body, headers=_headers()).status_code == 200
    assert client.post("/skills/quarterly-qa/invoke", json=body, headers=_headers()).status_code == 200
    assert builds == ["quarterly-qa@3"]  # 第二次命中快取

    # backend 出新 revision → 重編（快取鍵含 revision 與內容雜湊）
    fake.by_tenant["demo-a"] = [
        row("quarterly-qa", QUARTERLY_QA.replace("echo: ", "echo2: "), revision=4)
    ]
    assert client.post("/skills/quarterly-qa/invoke", json=body, headers=_headers()).status_code == 200
    assert builds == ["quarterly-qa@3", "quarterly-qa@4"]


# ---------------------------------------------------------------------------
# AT4-11 POST /skills/validate：中繼資料 + 無副作用
# ---------------------------------------------------------------------------


def test_validate_returns_skill_metadata_when_valid(backend, fake_deps):
    """【AT4-11】valid=true → 一併回 skill 中繼資料（backend 寫入 DB 的唯一資料來源）。

    QUARTERLY_QA 含 script 步驟 → 撰寫者角色 gate 要求 ADMIN 身分頭（ADMIN 作者撰寫、
    USER 呼叫，正是新語意下的合法組合；required_role 仍是 USER，不受影響）。
    """
    backend({"demo-a": []})

    resp = client.post(
        "/skills/validate",
        json={"definition": QUARTERLY_QA},
        headers=_headers(role="ADMIN"),
    )

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is True
    assert body["errors"] == []
    assert body["skill"] == {
        "name": "quarterly-qa",
        "description": "季報問答（租戶自訂）",
        "required_role": "USER",
        "kind": "flow",
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
    """required_role 直接來自 YAML：backend 靠這個欄位落 DB，不可預設成 USER。

    ADMIN_ONLY 含 script 步驟 → 以 ADMIN 身分頭驗證（撰寫者角色 gate）。
    """
    backend({"demo-a": []})

    resp = client.post(
        "/skills/validate",
        json={"definition": ADMIN_ONLY},
        headers=_headers(role="ADMIN"),
    )

    assert resp.json()["skill"]["required_role"] == "ADMIN"


# ---------------------------------------------------------------------------
# 撰寫者角色 gate（保守版）：script 撰寫限 ADMIN，只在 validate 端點生效
# ---------------------------------------------------------------------------


def test_validate_script_definition_rejected_for_user_author(backend, fake_deps):
    """USER 身分提交含 script 的定義 → 驗證失敗，錯誤明確指向撰寫者角色限制。"""
    backend({"demo-a": []})

    resp = client.post(
        "/skills/validate",
        json={"definition": QUARTERLY_QA},
        headers=_headers(role="USER"),
    )

    assert resp.status_code == 200  # 驗證結果在 body，不是 HTTP 錯誤碼
    body = resp.json()
    assert body["valid"] is False
    codes = {e["code"] for e in body["errors"]}
    assert "forbidden_script" in codes
    msg = next(e["message"] for e in body["errors"] if e["code"] == "forbidden_script")
    assert "ADMIN" in msg  # 訊息明確說得出「僅限 ADMIN 撰寫」
    assert "skill" not in body  # valid=false → 不回中繼資料（backend 據此拒寫）


def test_validate_script_definition_allowed_for_admin_author(backend, fake_deps):
    """決策表另一半：同一份含 script 的定義，ADMIN 身分 → 通過。"""
    backend({"demo-a": []})

    resp = client.post(
        "/skills/validate",
        json={"definition": QUARTERLY_QA},
        headers=_headers(role="ADMIN"),
    )

    body = resp.json()
    assert body["valid"] is True
    assert body["skill"]["required_role"] == "USER"  # gate 不改 required_role 語意


def test_validate_non_script_definition_passes_for_user_author(backend, fake_deps):
    """gate 只針對 script：USER 提交無 script 的合法定義照常通過。"""
    backend({"demo-a": []})

    resp = client.post(
        "/skills/validate",
        json={"definition": NODE_ONLY},
        headers=_headers(role="USER"),
    )

    body = resp.json()
    assert body["valid"] is True
    assert body["skill"]["name"] == "node-only-probe"


def test_validate_has_no_side_effects_on_custom_catalog(backend, fake_deps):
    """【AT4-11】驗證三次不得寫入任何東西：清單前後一致、也不會註冊成可執行 skill。"""
    backend({"demo-a": [row("quarterly-qa", QUARTERLY_QA)]})

    before = client.get("/skills", headers=_headers()).json()
    for definition in (QUARTERLY_QA, BROKEN, QUARTERLY_QA):
        client.post("/skills/validate", json={"definition": definition}, headers=_headers())
    after = client.get("/skills", headers=_headers()).json()

    assert after == before
    assert skills.get("quarterly-qa") is None  # 自訂 skill 不會被寫進內建註冊表
    assert skills.get("broken-skill") is None

