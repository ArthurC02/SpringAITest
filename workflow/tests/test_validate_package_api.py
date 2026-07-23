"""POST /skills/validate-package 端點測試（設計 §2.2 的 multipart + JSON 契約）。

真 FastAPI TestClient、真 multipart、真 zip bytes：釘死 backend 端要 diff 的線上契約，
不打 private method、不斷言 prompt 字串。valid=false 的回應不得含任何可被寫入的
metadata/definition（backend 以此決定能不能寫）。
"""

import io
import zipfile

from fastapi.testclient import TestClient

from app.engine.package import LIMITS
from app.main import app
from tests.conftest import auth_headers as _headers

client = TestClient(app)


def _zip(files, compression=zipfile.ZIP_DEFLATED) -> bytes:
    items = files.items() if isinstance(files, dict) else files
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", compression) as z:
        for name, data in items:
            z.writestr(name, data)
    return buf.getvalue()


AGENTIC_SKILL_MD = """---
name: sales-helper
description: 銷售小幫手
allowed-tools: local.calculator
metadata:
  kind: agentic
  required_role: USER
  timeout_seconds: "30"
  input_schema: '{"query": {"type": "str", "required": true, "min_length": 1}}'
---
You are a helpful sales assistant.
"""


def _post(raw: bytes, expected_name: str | None = None, **headers_kw):
    data = {"expected_name": expected_name} if expected_name is not None else {}
    return client.post(
        "/skills/validate-package",
        files={"package": ("skill.zip", raw, "application/zip")},
        data=data,
        headers=_headers(**headers_kw),
    )


def _corrupt_stored_skill_md_crc() -> bytes:
    content = AGENTIC_SKILL_MD.encode()
    raw = bytearray(_zip({"SKILL.md": content}, zipfile.ZIP_STORED))
    payload_at = raw.find(content)
    assert payload_at >= 0
    raw[payload_at] ^= 0x01
    return bytes(raw)


# ---------------------------------------------------------------------------
# 服務間標頭契約（沿用 internal-token + 身分頭模型）
# ---------------------------------------------------------------------------


def test_requires_internal_token():
    raw = _zip({"SKILL.md": AGENTIC_SKILL_MD})
    resp = client.post(
        "/skills/validate-package",
        files={"package": ("skill.zip", raw, "application/zip")},
    )
    assert resp.status_code == 401


def test_missing_tenant_returns_400():
    raw = _zip({"SKILL.md": AGENTIC_SKILL_MD})
    resp = _post(raw, "sales-helper", tenant_id=None)
    assert resp.status_code == 400


# ---------------------------------------------------------------------------
# 成功回應 = 既有 validation 回應的 internal superset
# ---------------------------------------------------------------------------


def test_valid_agentic_package_response_shape():
    raw = _zip(
        {
            "SKILL.md": AGENTIC_SKILL_MD,
            "references/guide.md": b"guide",
            "assets/logo.txt": b"logo",
        }
    )
    resp = _post(raw, "sales-helper")

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is True
    assert body["errors"] == []
    assert body["skill"] == {
        "name": "sales-helper",
        "description": "銷售小幫手",
        "required_role": "USER",
        "input_schema": {"query": {"type": "str", "required": True, "min_length": 1}},
        "kind": "agentic",
    }
    # §3 canonical 為 skills-ref-friendly：頂層只有標準欄位（name 起首，kind 在 metadata 內）
    assert body["canonical_definition"].startswith("name: sales-helper")
    assert "kind: agentic" in body["canonical_definition"]
    manifest = body["package_manifest"]
    assert manifest["entries"] == [
        "SKILL.md",
        "assets/logo.txt",
        "references/guide.md",
    ]
    assert len(manifest["sha256"]) == 64


def test_valid_agentic_without_expected_name_derives_canonical_name():
    resp = _post(_zip({"SKILL.md": AGENTIC_SKILL_MD}))
    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is True
    assert body["skill"]["name"] == "sales-helper"
    assert body["skill"]["kind"] == "agentic"


def test_valid_flow_package_response_shape():
    flow_yaml = (
        "name: flow-probe\n"
        "description: flow 匯出\n"
        "input_schema:\n"
        "  query: {type: str, required: true, min_length: 1}\n"
        "flow:\n"
        "  - node: query_intake@1.0\n"
    )
    # §3.1 flow package = 單一 SKILL.md：frontmatter + 內嵌 ```yaml 定義區塊
    md = f"---\nname: flow-probe\ndescription: flow 匯出\n---\n\n```yaml\n{flow_yaml}\n```\n"
    raw = _zip({"SKILL.md": md})
    resp = _post(raw, "flow-probe")

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is True
    assert body["skill"]["kind"] == "flow"
    # flow 的 canonical_definition = 內嵌區塊抽出的原文（byte-preserving，不重新序列化）
    assert body["canonical_definition"] == flow_yaml


def test_valid_flow_without_expected_name_derives_canonical_name():
    flow_yaml = (
        "name: flow-probe\n"
        "description: flow 匯出\n"
        "flow:\n"
        "  - node: query_intake@1.0\n"
    )
    md = (
        "---\nname: flow-probe\ndescription: flow 匯出\n---\n\n"
        f"```yaml\n{flow_yaml}\n```\n"
    )
    resp = _post(_zip({"SKILL.md": md}))
    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is True
    assert body["skill"]["name"] == "flow-probe"
    assert body["skill"]["kind"] == "flow"


# ---------------------------------------------------------------------------
# 失敗回應：不含任何可被寫入的 metadata/definition
# ---------------------------------------------------------------------------


def test_invalid_package_omits_writable_metadata():
    raw = _zip({"SKILL.md": AGENTIC_SKILL_MD.replace("kind: agentic", "kind: flow")})
    resp = _post(raw, "sales-helper")

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "invalid_frontmatter"
    assert "skill" not in body
    assert "canonical_definition" not in body
    assert "package_manifest" not in body


def test_name_mismatch_rejected_via_endpoint():
    raw = _zip({"SKILL.md": AGENTIC_SKILL_MD})
    resp = _post(raw, "different-name")

    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "name_mismatch"
    assert "skill" not in body


def test_flow_name_mismatch_rejected_when_expected_name_is_provided():
    definition = (
        "name: flow-probe\n"
        "description: flow 匯出\n"
        "flow:\n"
        "  - node: query_intake@1.0\n"
    )
    md = (
        "---\nname: flow-probe\ndescription: flow 匯出\n---\n\n"
        f"```yaml\n{definition}\n```\n"
    )
    body = _post(_zip({"SKILL.md": md}), "other-name").json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "name_mismatch"


def test_illegal_package_name_rejected_without_expected_name():
    md = AGENTIC_SKILL_MD.replace("name: sales-helper", "name: Sales")
    body = _post(_zip({"SKILL.md": md})).json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "invalid_frontmatter"


def test_oversize_archive_rejected_via_endpoint():
    # off-point 檔案數：SKILL.md + max = max+1 → invalid_package（限制常數走 LIMITS 單一來源）
    files = {"SKILL.md": AGENTIC_SKILL_MD}
    files.update({f"references/f{i}.md": b"x" for i in range(LIMITS.max_file_count)})
    resp = _post(_zip(files), "sales-helper")

    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "invalid_package"
    assert "canonical_definition" not in body


def test_path_traversal_rejected_via_endpoint():
    raw = _zip([("SKILL.md", AGENTIC_SKILL_MD), ("../evil.md", b"x")])
    resp = _post(raw, "sales-helper")

    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "invalid_package"


def test_crc_failure_returns_validation_error_instead_of_500():
    resp = _post(_corrupt_stored_skill_md_crc(), "sales-helper")
    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "invalid_package"
    assert "skill" not in body


def test_overlong_timeout_returns_validation_error_instead_of_500():
    md = AGENTIC_SKILL_MD.replace(
        'timeout_seconds: "30"', f'timeout_seconds: "{"9" * 10_000}"'
    )
    resp = _post(_zip({"SKILL.md": md}), "sales-helper")
    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == "invalid_frontmatter"
