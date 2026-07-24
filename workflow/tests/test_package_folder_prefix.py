"""`{name}/SKILL.md` 頂層資料夾結構的匯入測試（標準 §0：`name` 須等於資料夾名）。

匯出端改寫成 `{name}/SKILL.md` 後，匯入端若不同步就打爆 export→import 往返，所以這裡把
三件事一起釘死：新結構可匯入、舊的 root `SKILL.md` 不退化、剝除前綴沒有變成繞過路徑檢查
的新縫（穿越/絕對/drive/symlink/上限的逃逸樣本一律仍拒）。

zip fixture helper 沿用 test_package_parser 的既有那一套，不另建第二套格式定義。
"""

import io
import zipfile

import pytest

from app import tools as _tools  # noqa: F401  # 觸發 @tool 註冊
from app.engine import package
from app.engine.package import LIMITS, PackageError
from tests.test_package_parser import FLOW_YAML, flow_skill_md, make_zip, skill_md

NAME = "sales-helper"
FLOW_NAME = "flow-probe"


def prefixed_agentic_zip(folder=NAME, extra=None, **kwargs) -> bytes:
    files = {f"{folder}/SKILL.md": skill_md(**kwargs)}
    files.update({f"{folder}/{k}": v for k, v in (extra or {}).items()})
    return make_zip(files)


# ---------------------------------------------------------------------------
# 決策表兩半：資料夾名 == frontmatter name → 通過；不等 → folder_name_mismatch
# ---------------------------------------------------------------------------


def test_agentic_package_with_name_folder_prefix_parses():
    parsed = package.parse_package(prefixed_agentic_zip(), NAME)
    assert parsed.kind == "agentic"
    assert parsed.skill.name == NAME
    assert parsed.entries == ("SKILL.md",)  # 剝除後的相對路徑與舊格式一致
    assert parsed.agentic.instruction.strip() == "You are a helpful sales assistant."


def test_flow_package_with_name_folder_prefix_parses_byte_for_byte():
    md = flow_skill_md(FLOW_NAME, "flow 匯出", FLOW_YAML, prose="說明文字\n\n")
    parsed = package.parse_package(make_zip({f"{FLOW_NAME}/SKILL.md": md}), FLOW_NAME)
    assert parsed.kind == "flow"
    assert parsed.canonical_definition == FLOW_YAML  # 萃取邏輯不受前綴影響
    assert parsed.entries == ("SKILL.md",)


def test_root_skill_md_without_prefix_still_accepted():
    # 相容性回歸：舊的 root SKILL.md zip 不得因新規則退化。
    parsed = package.parse_package(make_zip({"SKILL.md": skill_md()}), NAME)
    assert parsed.kind == "agentic"
    assert parsed.entries == ("SKILL.md",)


def test_folder_name_not_equal_to_frontmatter_name_rejected():
    raw = prefixed_agentic_zip(folder="other-folder")
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, NAME)
    assert ei.value.errors[0].code == package.FOLDER_NAME_MISMATCH
    message = ei.value.errors[0].message
    assert "資料夾名" in message and "other-folder" in message and NAME in message


def test_flow_folder_name_not_equal_to_frontmatter_name_rejected():
    md = flow_skill_md(FLOW_NAME, "flow 匯出", FLOW_YAML)
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"wrong/SKILL.md": md}), FLOW_NAME)
    assert ei.value.errors[0].code == package.FOLDER_NAME_MISMATCH


def test_multiple_top_level_entries_without_root_skill_md_still_rejected():
    raw = make_zip({f"{NAME}/SKILL.md": skill_md(), "docs/guide.md": b"x"})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, NAME)
    assert ei.value.errors[0].code == package.MISSING_SKILL_MD


def test_root_file_sharing_folder_name_is_not_a_single_folder_prefix():
    raw = make_zip({NAME: b"decoy", f"{NAME}/SKILL.md": skill_md()})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, NAME)
    assert ei.value.errors[0].code == package.MISSING_SKILL_MD


# ---------------------------------------------------------------------------
# 額外檔案：剝除後相對路徑與舊格式一致、bytes 原封；scripts AST 閘門照樣生效
# ---------------------------------------------------------------------------


def test_extra_files_under_prefix_keep_legacy_relative_paths_and_bytes():
    raw = prefixed_agentic_zip(
        extra={
            "scripts/x.py": 'state["ok"] = 1',
            "references/y.md": b"\xffnotes\x00",
            "LICENSE.txt": b"MIT-ish",
        }
    )
    parsed = package.parse_package(raw, NAME)
    assert parsed.entries == ("LICENSE.txt", "SKILL.md", "references/y.md", "scripts/x.py")
    assert parsed.agentic.scripts == {"scripts/x.py": 'state["ok"] = 1'}
    assert parsed.agentic.resources["references/y.md"] == b"\xffnotes\x00"
    assert parsed.agentic.resources["LICENSE.txt"] == b"MIT-ish"


def test_script_under_prefix_is_still_ast_scanned():
    # 剝除若在 scan 之後或路徑沒對齊，`{name}/scripts/*.py` 會被當唯讀 resource 而躲過掃描。
    raw = prefixed_agentic_zip(extra={"scripts/escape.py": "import os\n"})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, NAME)
    assert ei.value.errors[0].code == package.FORBIDDEN_SCRIPT


# ---------------------------------------------------------------------------
# 巢狀 SKILL.md 誘餌：剝除後只有 root `SKILL.md` 是權威定義，`sub/SKILL.md` 只是名字
# 剛好如此的唯讀 resource。釘住現行行為（entry 順序也不得影響選擇）。
# ---------------------------------------------------------------------------

DECOY = skill_md(name="evil-twin", description="誘餌", body="DECOY instruction").encode()


def test_nested_skill_md_under_prefix_is_read_only_resource():
    raw = make_zip(
        {f"{NAME}/SKILL.md": skill_md(), f"{NAME}/examples/SKILL.md": DECOY}
    )
    parsed = package.parse_package(raw, NAME)
    assert parsed.entries == ("SKILL.md", "examples/SKILL.md")
    assert parsed.skill.name == NAME  # 權威定義來自 root，不受誘餌影響
    assert parsed.agentic.instruction.strip() == "You are a helpful sales assistant."
    assert parsed.agentic.resources["examples/SKILL.md"] == DECOY  # bytes 原封保留


def test_nested_skill_md_written_before_root_does_not_shadow_it():
    # zip 內把巢狀那份寫在前面：選擇依相對路徑，不依 entry 順序。
    raw = make_zip(
        [(f"{NAME}/examples/SKILL.md", DECOY), (f"{NAME}/SKILL.md", skill_md())]
    )
    parsed = package.parse_package(raw, NAME)
    assert parsed.kind == "agentic"
    assert parsed.skill.name == NAME
    assert parsed.agentic.instruction.strip() == "You are a helpful sales assistant."
    assert parsed.agentic.resources["examples/SKILL.md"] == DECOY


def test_flow_nested_skill_md_decoy_does_not_shadow_root():
    # 誘餌連 frontmatter 都沒有：若被誤取為權威定義，這裡會是 invalid_frontmatter 而非通過。
    md = flow_skill_md(FLOW_NAME, "flow 匯出", FLOW_YAML)
    raw = make_zip(
        [(f"{FLOW_NAME}/docs/SKILL.md", b"no frontmatter here"), (f"{FLOW_NAME}/SKILL.md", md)]
    )
    parsed = package.parse_package(raw, FLOW_NAME)
    assert parsed.kind == "flow"
    assert parsed.skill.name == FLOW_NAME
    assert parsed.canonical_definition == FLOW_YAML
    assert parsed.entries == ("SKILL.md", "docs/SKILL.md")


# ---------------------------------------------------------------------------
# 安全回歸：前綴不得成為繞過路徑檢查/上限的新縫
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "entry",
    [
        f"../{NAME}/SKILL.md",
        f"../../{NAME}/SKILL.md",
        f"/{NAME}/SKILL.md",
        f"C:/{NAME}/SKILL.md",
        f"c:/{NAME}/SKILL.md",
        f".//{NAME}/SKILL.md",
        f"{NAME}//SKILL.md",
    ],
)
def test_unsafe_prefix_still_rejected(entry):
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({entry: skill_md()}), NAME)
    assert ei.value.errors[0].code == package.INVALID_PACKAGE


def test_symlink_like_entry_under_prefix_still_rejected():
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(f"{NAME}/SKILL.md", skill_md())
        info = zipfile.ZipInfo(f"{NAME}/references/link")
        info.external_attr = 0o120777 << 16  # S_IFLNK
        z.writestr(info, "/etc/passwd")
    with pytest.raises(PackageError) as ei:
        package.parse_package(buf.getvalue(), NAME)
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "symlink" in ei.value.errors[0].message


def test_prefixed_file_count_on_point_accepts():
    # 上限以 archive entry 數計算，剝除只影響相對路徑呈現 → 邊界與無前綴時相同。
    extra = {f"references/f{i}.md": b"x" for i in range(LIMITS.max_file_count - 1)}
    parsed = package.parse_package(prefixed_agentic_zip(extra=extra), NAME)
    assert len(parsed.entries) == LIMITS.max_file_count


def test_prefixed_file_count_off_point_rejects():
    extra = {f"references/f{i}.md": b"x" for i in range(LIMITS.max_file_count)}
    with pytest.raises(PackageError) as ei:
        package.parse_package(prefixed_agentic_zip(extra=extra), NAME)
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "entry 數" in ei.value.errors[0].message


def test_prefixed_single_file_off_point_rejects():
    raw = make_zip(
        [
            (f"{NAME}/SKILL.md", skill_md()),
            (f"{NAME}/assets/big.bin", b"\0" * (LIMITS.max_single_file_bytes + 1)),
        ],
        zipfile.ZIP_STORED,
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, NAME)
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "單檔" in ei.value.errors[0].message
