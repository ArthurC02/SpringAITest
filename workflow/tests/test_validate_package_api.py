"""POST /skills/validate-package 端點測試（設計 §2.2 的 multipart + JSON 契約）。

真 FastAPI TestClient、真 multipart、真 zip bytes：釘死 backend 端要 diff 的線上契約，
不打 private method、不斷言 prompt 字串。valid=false 的回應不得含任何可被寫入的
metadata/definition（backend 以此決定能不能寫）。
"""

import io
import zipfile

import pytest
from fastapi.testclient import TestClient

from app.engine.package import LIMITS, TIMEOUT_SECONDS_MAX, TIMEOUT_SECONDS_MIN
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


def test_archive_without_skill_md_rejected():
    """zip 內沒有任何 root／前綴 SKILL.md → missing_skill_md（自成一碼，不是 invalid_package）。"""
    body = _post(_zip({"readme.txt": b"x"}), "sales-helper").json()

    assert body["valid"] is False
    assert body["errors"][0]["code"] == "missing_skill_md"
    assert "skill" not in body
    assert "canonical_definition" not in body


def test_folder_prefix_not_matching_frontmatter_name_rejected():
    """{folder}/SKILL.md 的 folder 須等於 frontmatter name。

    expected_name 這裡刻意給對的（sales-helper），證明這是與 name_mismatch 不同的分支：
    比對對象是 zip 自己的頂層資料夾名，不是匯入路徑名稱。
    """
    body = _post(_zip({"wrong-folder/SKILL.md": AGENTIC_SKILL_MD}), "sales-helper").json()

    assert body["valid"] is False
    assert body["errors"][0]["code"] == "folder_name_mismatch"
    assert "skill" not in body


def test_unknown_allowed_tool_rejected_via_endpoint():
    """frontmatter allowed-tools 引用未註冊 tool → unknown_tool（重用既有 registry 檢查）。"""
    md = AGENTIC_SKILL_MD.replace(
        "allowed-tools: local.calculator", "allowed-tools: nonexistent.tool"
    )
    body = _post(_zip({"SKILL.md": md}), "sales-helper").json()

    assert body["valid"] is False
    assert body["errors"][0]["code"] == "unknown_tool"
    assert "skill" not in body


def test_file_count_at_limit_accepted():
    """檔案數上限的 on-point：恰好 LIMITS.max_file_count 個 entry 仍是 valid=true
    （off-point max+1 由下方 parametrize 的 file-count case 蓋）。"""
    files = {"SKILL.md": AGENTIC_SKILL_MD}
    files.update(
        {f"references/f{i}.md": b"x" for i in range(LIMITS.max_file_count - 1)}
    )
    body = _post(_zip(files), "sales-helper").json()

    assert body["valid"] is True
    assert len(body["package_manifest"]["entries"]) == LIMITS.max_file_count


@pytest.mark.parametrize(
    "timeout_seconds,expected_valid",
    [
        ("0", False),
        (str(TIMEOUT_SECONDS_MIN), True),
        (str(TIMEOUT_SECONDS_MAX), True),
        # MAX+1 與 MAX 同為 19 位數，先過字數/格式前置檢查，真正踩到數值範圍那條分支
        (str(TIMEOUT_SECONDS_MAX + 1), False),
    ],
    ids=["below-min", "min", "max", "above-max"],
)
def test_timeout_seconds_range_boundaries(timeout_seconds, expected_valid):
    """metadata.timeout_seconds 的 int64 範圍：MIN/MAX 兩端各配一個 off-point。"""
    md = AGENTIC_SKILL_MD.replace(
        'timeout_seconds: "30"', f'timeout_seconds: "{timeout_seconds}"'
    )
    body = _post(_zip({"SKILL.md": md}), "sales-helper").json()

    assert body["valid"] is expected_valid
    if expected_valid:
        assert body["errors"] == []
        assert body["skill"]["name"] == "sales-helper"
    else:
        assert body["errors"][0]["code"] == "invalid_frontmatter"
        assert "skill" not in body


def _oversize_archive() -> bytes:
    # off-point 檔案數：SKILL.md + max = max+1（限制常數走 LIMITS 單一來源）
    files = {"SKILL.md": AGENTIC_SKILL_MD}
    files.update({f"references/f{i}.md": b"x" for i in range(LIMITS.max_file_count)})
    return _zip(files)


def _overlong_timeout_archive() -> bytes:
    md = AGENTIC_SKILL_MD.replace(
        'timeout_seconds: "30"', f'timeout_seconds: "{"9" * 10_000}"'
    )
    return _zip({"SKILL.md": md})


@pytest.mark.parametrize(
    "make_archive,expected_code",
    [
        (_oversize_archive, "invalid_package"),
        (
            lambda: _zip([("SKILL.md", AGENTIC_SKILL_MD), ("../evil.md", b"x")]),
            "invalid_package",
        ),
        (_corrupt_stored_skill_md_crc, "invalid_package"),
        (_overlong_timeout_archive, "invalid_frontmatter"),
    ],
    ids=["file-count", "path-traversal", "crc", "overlong-timeout"],
)
def test_package_error_returns_validation_error_instead_of_500(
    make_archive, expected_code
):
    """四種內部例外來源共用 main.py:439 的 `except PackageError`：一律 200 + valid=false，
    且不得帶任何可被寫入的 metadata（backend 以此決定能不能寫）。"""
    resp = _post(make_archive(), "sales-helper")

    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert body["errors"][0]["code"] == expected_code
    assert "skill" not in body
    assert "canonical_definition" not in body
    assert "package_manifest" not in body


# ---------------------------------------------------------------------------
# 撰寫者角色 gate：匯入路徑與 /skills/validate 同一套
# ---------------------------------------------------------------------------

SCRIPT_FLOW_YAML = (
    "name: script-probe\n"
    "description: 腳本探針\n"
    "flow:\n"
    "  - script: |\n"
    "      note = 'ok'\n"
)
SCRIPT_FLOW_MD = (
    "---\nname: script-probe\ndescription: 腳本探針\n---\n\n"
    f"```yaml\n{SCRIPT_FLOW_YAML}\n```\n"
)


def test_script_flow_package_rejected_for_non_admin_author():
    """同一份含 script 的定義，走 /skills/validate 是 forbidden_script；匯入路徑不得放行。

    backend 的三個匯入入口與 platform proxy 都已是 ADMIN-only，這是最內層的縱深防禦。
    """
    body = _post(_zip({"SKILL.md": SCRIPT_FLOW_MD}), "script-probe", role="USER").json()

    assert body["valid"] is False
    assert [e["code"] for e in body["errors"]] == ["forbidden_script"]
    assert "skill" not in body
    assert "canonical_definition" not in body


def test_script_flow_package_accepted_for_admin_author():
    """決策表的另一半：合法作者（ADMIN）匯入同一份定義照樣通過。"""
    body = _post(_zip({"SKILL.md": SCRIPT_FLOW_MD}), "script-probe", role="ADMIN").json()

    assert body["valid"] is True
    assert body["errors"] == []
    assert body["skill"]["name"] == "script-probe"
    assert body["canonical_definition"] == SCRIPT_FLOW_YAML


def test_scriptless_flow_package_accepted_for_non_admin_author():
    """gate 只針對 script 步驟：USER 匯入無 script 的 flow 不受影響（零追溯破壞）。"""
    flow_yaml = (
        "name: flow-probe\n"
        "description: flow 匯出\n"
        "flow:\n"
        "  - node: query_intake@1.0\n"
    )
    md = (
        "---\nname: flow-probe\ndescription: flow 匯出\n---\n\n"
        f"```yaml\n{flow_yaml}```\n"
    )
    body = _post(_zip({"SKILL.md": md}), "flow-probe", role="USER").json()

    assert body["valid"] is True
    assert body["skill"]["kind"] == "flow"
