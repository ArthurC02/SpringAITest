"""Agent Skill package 解析器測試（P0：AST-P0-004..008）。

真 zip bytes、真 AST scan、真 tool registry —— 不用 mocking library。解析器是外部
zip 進入系統的信任邊界，逃逸樣本（path traversal / drive / dup / symlink / zip bomb）
與白名單決策表兩半都必須有測試背書，不能只寫在註解裡。

限制常數一律由 package.LIMITS 單一來源取得（AST-P0-008「限制常數由 parser 單一來源」）：
邊界 fixture 以 LIMITS 的欄位計算，改常數測試自動跟隨。
"""

import io
import base64
import zipfile

import pytest
import yaml

from app import tools as _tools  # noqa: F401  # 觸發 @tool 註冊（unknown_tool 決策表兩半）
from app.engine import package
from app.engine.package import (
    LIMITS,
    MAX_PACKAGE_BASE64_CHARS,
    MAX_PACKAGE_RAW_BYTES,
    PackageError,
)
from app.runtime.artifacts import ArtifactError, _decode_package

# ---------------------------------------------------------------------------
# zip fixture helpers
# ---------------------------------------------------------------------------


def make_zip(files, compression=zipfile.ZIP_DEFLATED) -> bytes:
    """dict[name -> bytes|str] → zip bytes。重複 name 以 list[tuple] 傳入即可。"""
    items = files.items() if isinstance(files, dict) else files
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", compression) as z:
        for name, data in items:
            z.writestr(name, data)
    return buf.getvalue()


def test_package_transport_raw_and_base64_boundaries_are_shared() -> None:
    exact = b"x" * MAX_PACKAGE_RAW_BYTES
    over = exact + b"x"
    exact_b64 = base64.b64encode(exact)
    over_b64 = base64.b64encode(over)
    assert len(exact_b64) == MAX_PACKAGE_BASE64_CHARS
    assert len(over_b64) == MAX_PACKAGE_BASE64_CHARS + 4
    assert _decode_package(exact_b64.decode("ascii")) == exact
    with pytest.raises(PackageError) as exact_error:
        package.parse_package(exact, "boundary")
    assert "transport limit" not in str(exact_error.value)
    with pytest.raises(ArtifactError, match="limit"):
        _decode_package(over_b64.decode("ascii"))
    with pytest.raises(PackageError, match="transport limit"):
        package.parse_package(over, "oversized")


def skill_md(
    *,
    name="sales-helper",
    description="銷售小幫手",
    kind="agentic",
    required_role="USER",
    uses_tools=None,
    input_schema_json='{"query": {"type": "str", "required": true, "min_length": 1}}',
    license=None,
    compatibility=None,
    timeout_seconds="30",
    body="You are a helpful sales assistant.",
) -> str:
    """§3 標準 frontmatter：標準欄位在頂層，引擎專屬需求收進 metadata（string→string）。"""
    lines = ["---", f"name: {name}", f"description: {description}"]
    if license is not None:
        lines.append(f"license: {license}")
    if compatibility is not None:
        lines.append(f"compatibility: {compatibility}")
    if uses_tools:
        lines.append(f"allowed-tools: {' '.join(uses_tools)}")  # 空白分隔字串
    lines.append("metadata:")
    lines.append(f"  kind: {kind}")
    lines.append(f"  required_role: {required_role}")
    if timeout_seconds is not None:
        lines.append(f'  timeout_seconds: "{timeout_seconds}"')  # 字串
    lines.append(f"  input_schema: '{input_schema_json}'")  # JSON 字串
    lines += ["---", body, ""]
    return "\n".join(lines)


def agentic_zip(**kwargs) -> bytes:
    extra = kwargs.pop("extra", {})
    files = {"SKILL.md": skill_md(**kwargs)}
    files.update(extra)
    return make_zip(files)


# ---------------------------------------------------------------------------
# AST-P0-004：path traversal / drive / dup / symlink → invalid_package，零副作用
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "entry_name",
    [
        "../evil.md",
        "../../etc/passwd",
        "/etc/passwd",
        "C:/evil.md",
        "c:/evil.md",
        "",  # 空路徑
        # §4 放寬後 notallowed.txt / config/secret.yaml 這類「佈局白名單外」的檔案已改為
        # 接受（當唯讀 resource），故不再列此處；安全護欄（穿越/絕對/drive/空）維持。
    ],
)
def test_rejects_unsafe_or_disallowed_paths(entry_name):
    raw = make_zip([("SKILL.md", skill_md()), (entry_name, b"x")])
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE


def test_backslash_windows_path_normalized_then_rejected():
    # zipfile 讀取時把 '\\' → '/'；`..\\x` 正規化成 `../x`，仍被 '..' 成分檢查攔下。
    raw = make_zip([("SKILL.md", skill_md()), ("..\\evil.md", b"x")])
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE


def test_rejects_normalized_duplicate_entry():
    raw = make_zip(
        [
            ("SKILL.md", skill_md()),
            ("references/a.md", b"one"),
            ("references/a.md", b"two"),  # 正規化後重複
        ]
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "重複" in ei.value.errors[0].message


def test_rejects_symlink_like_entry():
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("SKILL.md", skill_md())
        info = zipfile.ZipInfo("references/link")
        info.external_attr = 0o120777 << 16  # S_IFLNK
        z.writestr(info, "/etc/passwd")
    with pytest.raises(PackageError) as ei:
        package.parse_package(buf.getvalue(), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "symlink" in ei.value.errors[0].message


@pytest.mark.parametrize(
    "directory_name",
    ["../", "/absolute/", "C:/drive/", "..\\backslash\\", "docs//nested/"],
)
def test_rejects_unsafe_directory_entries(directory_name):
    """directory entry 不能用 is_dir() 繞過與檔案相同的 path 安全護欄。"""
    raw = make_zip([("SKILL.md", skill_md()), (directory_name, b"")])
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE


def test_rejects_symlink_like_directory_entry():
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("SKILL.md", skill_md())
        info = zipfile.ZipInfo("references/link/")
        info.external_attr = (0o120777 << 16) | 0x10
        z.writestr(info, b"")
    with pytest.raises(PackageError) as ei:
        package.parse_package(buf.getvalue(), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "symlink" in ei.value.errors[0].message


def test_accepts_safe_directory_entry():
    raw = make_zip(
        [("SKILL.md", skill_md()), ("docs/", b""), ("docs/guide.md", b"guide")]
    )
    parsed = package.parse_package(raw, "sales-helper")
    assert parsed.agentic.resources["docs/guide.md"] == b"guide"


def test_rejects_non_zip_bytes():
    with pytest.raises(PackageError) as ei:
        package.parse_package(b"not a zip at all", "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE


def _corrupt_stored_skill_md_crc() -> bytes:
    content = skill_md().encode()
    raw = bytearray(make_zip({"SKILL.md": content}, zipfile.ZIP_STORED))
    payload_at = raw.find(content)
    assert payload_at >= 0
    raw[payload_at] ^= 0x01
    return bytes(raw)


def _mark_first_entry_encrypted() -> bytes:
    raw = bytearray(make_zip({"SKILL.md": skill_md()}, zipfile.ZIP_STORED))
    local = raw.find(b"PK\x03\x04")
    central = raw.find(b"PK\x01\x02")
    assert local >= 0 and central >= 0
    raw[local + 6] |= 0x01
    raw[central + 8] |= 0x01
    return bytes(raw)


def _mark_first_entry_unsupported_compression() -> bytes:
    raw = bytearray(make_zip({"SKILL.md": skill_md()}, zipfile.ZIP_STORED))
    local = raw.find(b"PK\x03\x04")
    central = raw.find(b"PK\x01\x02")
    assert local >= 0 and central >= 0
    raw[local + 8 : local + 10] = (99).to_bytes(2, "little")
    raw[central + 10 : central + 12] = (99).to_bytes(2, "little")
    return bytes(raw)


def _entry_header_offsets(raw: bytes, name: str) -> tuple[int, int]:
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        local = z.getinfo(name).header_offset
    encoded = name.encode()
    central = 0
    while True:
        central = raw.find(b"PK\x01\x02", central)
        assert central >= 0
        name_len = int.from_bytes(raw[central + 28 : central + 30], "little")
        if raw[central + 46 : central + 46 + name_len] == encoded:
            return local, central
        central += 4


def _directory_archive(payload=b"", compression=zipfile.ZIP_DEFLATED) -> bytes:
    return make_zip(
        [("SKILL.md", skill_md()), ("docs/", payload)], compression
    )


@pytest.mark.parametrize(
    "compression",
    [
        zipfile.ZIP_STORED,
        zipfile.ZIP_DEFLATED,
        zipfile.ZIP_BZIP2,
        zipfile.ZIP_LZMA,
    ],
    ids=["stored", "deflate", "bzip2", "lzma"],
)
def test_empty_directory_supported_for_every_allowed_compression(compression):
    parsed = package.parse_package(
        _directory_archive(b"", compression), "sales-helper"
    )
    assert parsed.kind == "agentic"


@pytest.mark.parametrize(
    "compression",
    [zipfile.ZIP_DEFLATED, zipfile.ZIP_BZIP2, zipfile.ZIP_LZMA],
    ids=["deflate", "bzip2", "lzma"],
)
def test_large_compressible_directory_payload_uses_bounded_read(
    compression, monkeypatch
):
    # directory 放第一個，讓 instrumentation 只觀測 directory probe，不混入後續 SKILL.md
    # 正常檔案解壓所使用的 decoder。
    raw = make_zip(
        [("docs/", b"x" * 300_000), ("SKILL.md", skill_md())],
        compression,
    )
    max_lengths: list[int] = []

    class _DecoderProbe:
        def __init__(self, inner):
            self._inner = inner

        def decompress(self, data, max_length=-1):
            max_lengths.append(max_length)
            return self._inner.decompress(data, max_length=max_length)

        def __getattr__(self, name):
            return getattr(self._inner, name)

    if compression == zipfile.ZIP_DEFLATED:
        real_factory = package.zlib.decompressobj

        def factory(*args, **kwargs):
            return _DecoderProbe(real_factory(*args, **kwargs))

        monkeypatch.setattr(package.zlib, "decompressobj", factory)
    elif compression == zipfile.ZIP_BZIP2:
        real_factory = package.bz2.BZ2Decompressor

        def factory(*args, **kwargs):
            return _DecoderProbe(real_factory(*args, **kwargs))

        monkeypatch.setattr(package.bz2, "BZ2Decompressor", factory)
    else:
        real_factory = package.lzma.LZMADecompressor

        def factory(*args, **kwargs):
            return _DecoderProbe(real_factory(*args, **kwargs))

        monkeypatch.setattr(package.lzma, "LZMADecompressor", factory)

    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "payload" in ei.value.errors[0].message
    assert max_lengths == [1]


def test_corrupt_empty_directory_codec_is_controlled_invalid_package():
    raw = bytearray(_directory_archive(b"", zipfile.ZIP_DEFLATED))
    local, _central = _entry_header_offsets(bytes(raw), "docs/")
    name_len = int.from_bytes(raw[local + 26 : local + 28], "little")
    extra_len = int.from_bytes(raw[local + 28 : local + 30], "little")
    data_at = local + 30 + name_len + extra_len
    raw[data_at] ^= 0xFF
    with pytest.raises(PackageError) as ei:
        package.parse_package(bytes(raw), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "壓縮內容" in ei.value.errors[0].message


class _NonSeekableZipSink(io.BytesIO):
    def seekable(self) -> bool:
        return False

    def seek(self, *args, **kwargs):
        raise io.UnsupportedOperation("non-seekable")


def _streaming_directory_archive(*, force_zip64=False) -> bytes:
    sink = _NonSeekableZipSink()
    with zipfile.ZipFile(sink, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("SKILL.md", skill_md())
        info = zipfile.ZipInfo("docs/")
        info.compress_type = zipfile.ZIP_DEFLATED
        with z.open(info, "w", force_zip64=force_zip64):
            pass
    return sink.getvalue()


def _forced_zip64_directory_archive() -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("SKILL.md", skill_md())
        info = zipfile.ZipInfo("docs/")
        info.compress_type = zipfile.ZIP_DEFLATED
        with z.open(info, "w", force_zip64=True):
            pass
    return buf.getvalue()


def _streaming_directory_without_descriptor_signature() -> bytes:
    raw = bytearray(_streaming_directory_archive())
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        info = z.getinfo("docs/")
        local = info.header_offset
        name_len = int.from_bytes(raw[local + 26 : local + 28], "little")
        extra_len = int.from_bytes(raw[local + 28 : local + 30], "little")
        descriptor = local + 30 + name_len + extra_len + info.compress_size
    assert raw[descriptor : descriptor + 4] == b"PK\x07\x08"
    del raw[descriptor : descriptor + 4]
    eocd = raw.rfind(b"PK\x05\x06")
    assert eocd >= 0
    central_offset = int.from_bytes(raw[eocd + 16 : eocd + 20], "little")
    raw[eocd + 16 : eocd + 20] = (central_offset - 4).to_bytes(4, "little")
    return bytes(raw)


def _forge_directory_central_empty(payload=b"x") -> bytes:
    raw = bytearray(_directory_archive(payload, zipfile.ZIP_STORED))
    _local, central = _entry_header_offsets(bytes(raw), "docs/")
    raw[central + 16 : central + 20] = (0).to_bytes(4, "little")  # CRC
    raw[central + 24 : central + 28] = (0).to_bytes(4, "little")  # file size
    return bytes(raw)


def _forge_directory_both_headers_empty(payload=b"x") -> bytes:
    raw = bytearray(_directory_archive(payload, zipfile.ZIP_STORED))
    local, central = _entry_header_offsets(bytes(raw), "docs/")
    raw[local + 14 : local + 18] = (0).to_bytes(4, "little")
    raw[local + 22 : local + 26] = (0).to_bytes(4, "little")
    raw[central + 16 : central + 20] = (0).to_bytes(4, "little")
    raw[central + 24 : central + 28] = (0).to_bytes(4, "little")
    return bytes(raw)


def _mark_directory_encrypted() -> bytes:
    raw = bytearray(_directory_archive())
    local, central = _entry_header_offsets(bytes(raw), "docs/")
    raw[local + 6] |= 0x01
    raw[central + 8] |= 0x01
    return bytes(raw)


def _mark_directory_unsupported_compression() -> bytes:
    raw = bytearray(_directory_archive())
    local, central = _entry_header_offsets(bytes(raw), "docs/")
    raw[local + 8 : local + 10] = (99).to_bytes(2, "little")
    raw[central + 10 : central + 12] = (99).to_bytes(2, "little")
    return bytes(raw)


def test_crc_failure_is_controlled_invalid_package():
    with pytest.raises(PackageError) as ei:
        package.parse_package(_corrupt_stored_skill_md_crc(), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "無法安全讀取" in ei.value.errors[0].message


def test_encrypted_entry_runtime_error_is_controlled_invalid_package():
    with pytest.raises(PackageError) as ei:
        package.parse_package(_mark_first_entry_encrypted(), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "encrypted" in ei.value.errors[0].message


def test_unsupported_compression_is_controlled_invalid_package():
    with pytest.raises(PackageError) as ei:
        package.parse_package(
            _mark_first_entry_unsupported_compression(), "sales-helper"
        )
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "不支援的壓縮方法" in ei.value.errors[0].message


def test_directory_forged_central_size_crc_is_rejected():
    with pytest.raises(PackageError) as ei:
        package.parse_package(
            _forge_directory_central_empty(), "sales-helper"
        )
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "local/central" in ei.value.errors[0].message


def test_directory_forged_local_and_central_empty_still_checks_actual_payload():
    with pytest.raises(PackageError) as ei:
        package.parse_package(
            _forge_directory_both_headers_empty(), "sales-helper"
        )
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert (
        "payload" in ei.value.errors[0].message
        or "CRC" in ei.value.errors[0].message
    )


def test_encrypted_directory_is_rejected_before_is_dir_shortcut():
    with pytest.raises(PackageError) as ei:
        package.parse_package(_mark_directory_encrypted(), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "encrypted" in ei.value.errors[0].message


def test_unsupported_directory_compression_is_rejected_before_is_dir_shortcut():
    with pytest.raises(PackageError) as ei:
        package.parse_package(
            _mark_directory_unsupported_compression(), "sales-helper"
        )
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "不支援的壓縮方法" in ei.value.errors[0].message


def test_streaming_data_descriptor_directory_is_accepted_when_proven_empty():
    parsed = package.parse_package(
        _streaming_directory_archive(), "sales-helper"
    )
    assert parsed.kind == "agentic"


def test_streaming_data_descriptor_without_signature_is_accepted():
    parsed = package.parse_package(
        _streaming_directory_without_descriptor_signature(), "sales-helper"
    )
    assert parsed.kind == "agentic"


def test_streaming_zip64_data_descriptor_directory_is_accepted_when_proven_empty():
    parsed = package.parse_package(
        _streaming_directory_archive(force_zip64=True), "sales-helper"
    )
    assert parsed.kind == "agentic"


def test_forced_zip64_local_extra_directory_is_accepted_when_proven_empty():
    parsed = package.parse_package(
        _forced_zip64_directory_archive(), "sales-helper"
    )
    assert parsed.kind == "agentic"


def test_regular_file_forged_local_crc_and_sizes_zero_is_rejected():
    raw = bytearray(make_zip({"SKILL.md": skill_md()}, zipfile.ZIP_STORED))
    local, _central = _entry_header_offsets(bytes(raw), "SKILL.md")
    raw[local + 14 : local + 18] = (0).to_bytes(4, "little")
    raw[local + 18 : local + 22] = (0).to_bytes(4, "little")
    raw[local + 22 : local + 26] = (0).to_bytes(4, "little")
    with pytest.raises(PackageError) as ei:
        package.parse_package(bytes(raw), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "local/central CRC 或大小不一致" in ei.value.errors[0].message


# ---------------------------------------------------------------------------
# AST-P0-005：缺 SKILL.md / 無 frontmatter / name 不符 / 非 agentic kind
# ---------------------------------------------------------------------------


def test_missing_skill_md():
    raw = make_zip({"references/a.md": b"hi"})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.MISSING_SKILL_MD


def test_empty_archive_is_missing_skill_md():
    """entry 數 0 的下邊界：合法但空的 zip 走同一條 missing_skill_md，不是別的未護欄路徑。"""
    raw = make_zip({})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.MISSING_SKILL_MD


def test_no_frontmatter():
    raw = make_zip({"SKILL.md": "just a body, no frontmatter\n"})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


def test_non_utf8_skill_md_and_script_are_controlled_errors():
    """未信任 zip 的 bytes 不保證是文字：兩條解碼路徑都必須是受控錯誤碼，不是 500。"""
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": b"\xff\xfe---\nname: x\n"}), None)
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert "UTF-8" in ei.value.errors[0].message

    with pytest.raises(PackageError) as ei:
        package.parse_package(
            agentic_zip(extra={"scripts/bad.py": b"\xff\xfe"}), "sales-helper"
        )
    assert ei.value.errors[0].code == package.FORBIDDEN_SCRIPT
    assert "UTF-8" in ei.value.errors[0].message


@pytest.mark.parametrize(
    "frontmatter,fragment",
    [
        ("name: [unclosed", "YAML 解析失敗"),  # frontmatter 內是壞 YAML
        ("- a\n- b", "mapping"),  # 合法 YAML 但不是 mapping
    ],
    ids=["broken-yaml", "not-a-mapping"],
)
def test_broken_frontmatter_yaml_is_invalid_frontmatter(frontmatter, fragment):
    """frontmatter 區塊本身壞掉 → invalid_frontmatter（yaml.YAMLError 不得穿出去）。"""
    md = f"---\n{frontmatter}\n---\n\nbody\n"

    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "sales-helper")

    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert fragment in ei.value.errors[0].message


def test_name_mismatch_cannot_rename_via_package():
    raw = agentic_zip(name="other-name")
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.NAME_MISMATCH


def test_expected_name_omitted_lets_frontmatter_name_stand():
    """決策表另一半：expected_name 省略（None）→ 跳過名稱核對，SKILL.md 自己決定名稱。"""
    parsed = package.parse_package(agentic_zip(name="unrouted-name"))
    assert parsed.kind == "agentic"
    assert parsed.skill.name == "unrouted-name"


def test_non_agentic_kind_routed_to_flow_and_needs_yaml_block():
    # metadata.kind != agentic → 走 flow 分派；agentic_zip 的 body 無 ```yaml 區塊 → 拒。
    raw = agentic_zip(kind="flow")
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert "定義區塊" in ei.value.errors[0].message


def test_frontmatter_schema_violation_rejected():
    # name 不符 schema pattern（大寫）→ frontmatter schema 失敗
    raw = agentic_zip(name="Sales")
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "Sales")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


@pytest.mark.parametrize(
    "old_field",
    [
        "kind: agentic",
        "uses_tools: []",
        "required_role: USER",
        "timeout_seconds: 30",
        "input_schema: {}",
    ],
)
def test_agentic_rejects_legacy_top_level_engine_fields(old_field):
    md = skill_md().replace(
        "description: 銷售小幫手\n",
        f"description: 銷售小幫手\n{old_field}\n",
        1,
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert "非標準頂層欄位" in ei.value.errors[0].message


@pytest.mark.parametrize(
    "md",
    [
        skill_md().replace("name: sales-helper\n", "", 1),
        skill_md().replace("description: 銷售小幫手\n", "", 1),
        skill_md().replace("description: 銷售小幫手", "description: '   '", 1),
    ],
)
def test_agentic_requires_name_and_nonblank_description(md):
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


@pytest.mark.parametrize(
    "missing_line",
    [
        "  required_role: USER\n",
        '  timeout_seconds: "30"\n',
        """  input_schema: '{"query": {"type": "str", "required": true, "min_length": 1}}'\n""",
    ],
)
def test_agentic_requires_engine_metadata_fields(missing_line):
    md = skill_md().replace(missing_line, "", 1)
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert "缺少必要欄位" in ei.value.errors[0].message


@pytest.mark.parametrize(
    ("old", "new"),
    [
        ("  required_role: USER", "  required_role: [USER]"),
        ('  timeout_seconds: "30"', "  timeout_seconds: 30"),
        ("metadata:\n", "metadata:\n  owner:\n    team: core\n"),
    ],
)
def test_agentic_metadata_must_be_string_to_string(old, new):
    md = skill_md().replace(old, new, 1)
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert "string→string" in ei.value.errors[0].message


def test_agentic_accepts_custom_string_metadata():
    md = skill_md().replace("metadata:\n", "metadata:\n  owner: core-team\n", 1)
    parsed = package.parse_package(make_zip({"SKILL.md": md}), "sales-helper")
    assert parsed.kind == "agentic"


def test_agentic_rejects_non_string_allowed_tools():
    md = skill_md().replace(
        "description: 銷售小幫手\n",
        "description: 銷售小幫手\nallowed-tools: [local.calculator]\n",
        1,
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


# ---------------------------------------------------------------------------
# AST-P0-006：unknown_tool（決策表兩半：未註冊拒絕 / 已註冊通過）
# ---------------------------------------------------------------------------


def test_unknown_tool_rejected():
    raw = agentic_zip(uses_tools=["no_such_tool"])
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.UNKNOWN_TOOL


def test_registered_tool_accepted():
    raw = agentic_zip(uses_tools=["local.calculator"])
    parsed = package.parse_package(raw, "sales-helper")
    assert parsed.kind == "agentic"
    assert parsed.skill.uses_tools == ["local.calculator"]


# ---------------------------------------------------------------------------
# AST-P0-007：forbidden_script（決策表兩半：不過 scan 拒絕 / 過 scan 儲存但不執行）
# ---------------------------------------------------------------------------


def test_forbidden_script_rejected():
    raw = agentic_zip(extra={"scripts/x.py": "import os\n"})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.FORBIDDEN_SCRIPT


def test_safe_script_stored_not_executed():
    # 通過 AST scan 的 script：只儲存 source，不建立可執行步驟、不執行（規格 R8）
    src = 'state["derived"] = 1 + 1'
    raw = agentic_zip(extra={"scripts/x.py": src})
    parsed = package.parse_package(raw, "sales-helper")
    assert parsed.agentic.scripts == {"scripts/x.py": src}
    # 沒有 derived 這種執行後才會有的痕跡：解析不執行 script
    assert "derived" not in parsed.canonical_definition


def test_non_py_under_scripts_is_read_only_resource_and_byte_preserved():
    raw = agentic_zip(extra={"scripts/README.md": b"\xffnotes\x00"})
    parsed = package.parse_package(raw, "sales-helper")
    assert parsed.agentic.scripts == {}
    assert parsed.agentic.resources["scripts/README.md"] == b"\xffnotes\x00"
    assert "scripts/README.md" in parsed.entries


def test_nested_python_under_scripts_is_scanned():
    raw = agentic_zip(extra={"scripts/nested/escape.py": "import os\n"})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.FORBIDDEN_SCRIPT


def test_nested_safe_python_under_scripts_is_stored_not_executed():
    source = 'state["nested"] = 1'
    parsed = package.parse_package(
        agentic_zip(extra={"scripts/nested/safe.py": source}), "sales-helper"
    )
    assert parsed.agentic.scripts == {"scripts/nested/safe.py": source}
    assert "nested" not in parsed.canonical_definition


# ---------------------------------------------------------------------------
# AST-P0-008：限制 on-point/off-point（常數由 LIMITS 單一來源）
# ---------------------------------------------------------------------------


def test_file_count_on_point_accepts():
    # SKILL.md + (max-1) references = 恰好 max_file_count
    extra = {f"references/f{i}.md": b"x" for i in range(LIMITS.max_file_count - 1)}
    raw = agentic_zip(extra=extra)
    parsed = package.parse_package(raw, "sales-helper")
    assert len(parsed.entries) == LIMITS.max_file_count


def test_file_count_off_point_rejects():
    extra = {f"references/f{i}.md": b"x" for i in range(LIMITS.max_file_count)}
    raw = agentic_zip(extra=extra)  # SKILL.md + max = max+1
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE


def test_entry_path_character_limit_exact_and_plus_one() -> None:
    exact = "references/" + "a" * (LIMITS.max_entry_path_chars - len("references/"))
    parsed = package.parse_package(
        agentic_zip(extra={exact: b"x"}), "sales-helper"
    )
    assert exact in parsed.entries

    over = exact + "a"
    with pytest.raises(PackageError, match="path exceeds"):
        package.parse_package(agentic_zip(extra={over: b"x"}), "sales-helper")


@pytest.mark.parametrize(
    "path",
    [
        "references/guide.txt ",
        " references/guide.txt",
        "references/ nested/guide.txt",
        "references/nested /guide.txt",
    ],
)
def test_resource_paths_with_segment_edge_whitespace_are_rejected(path: str) -> None:
    with pytest.raises(PackageError):
        package.parse_package(
            agentic_zip(extra={path: b"not exactly addressable"}),
            "sales-helper",
        )


def test_aggregate_entry_path_character_limit_exact_and_plus_one() -> None:
    base = len("SKILL.md")
    first = "references/" + "a" * 20
    exact_second_length = LIMITS.max_total_path_chars - base - len(first)
    limits = package.PackageLimits(
        max_file_count=LIMITS.max_file_count,
        max_single_file_bytes=LIMITS.max_single_file_bytes,
        max_total_uncompressed_bytes=LIMITS.max_total_uncompressed_bytes,
        max_compression_ratio=LIMITS.max_compression_ratio,
        max_entry_path_chars=LIMITS.max_total_path_chars,
        max_total_path_chars=LIMITS.max_total_path_chars,
    )
    second = "assets/" + "b" * (exact_second_length - len("assets/"))
    package.parse_package(
        agentic_zip(extra={first: b"x", second: b"x"}),
        "sales-helper",
        limits=limits,
    )
    with pytest.raises(PackageError, match="aggregate entry paths"):
        package.parse_package(
            agentic_zip(extra={first: b"x", second + "b": b"x"}),
            "sales-helper",
            limits=limits,
        )


def test_directory_entries_count_toward_archive_limit_on_point():
    limits = package.PackageLimits(
        max_file_count=2,
        max_single_file_bytes=LIMITS.max_single_file_bytes,
        max_total_uncompressed_bytes=LIMITS.max_total_uncompressed_bytes,
        max_compression_ratio=LIMITS.max_compression_ratio,
    )
    raw = make_zip([("SKILL.md", skill_md()), ("docs/", b"")])
    parsed = package.parse_package(raw, "sales-helper", limits=limits)
    assert parsed.kind == "agentic"


def test_directory_entries_count_toward_archive_limit_off_point():
    limits = package.PackageLimits(
        max_file_count=2,
        max_single_file_bytes=LIMITS.max_single_file_bytes,
        max_total_uncompressed_bytes=LIMITS.max_total_uncompressed_bytes,
        max_compression_ratio=LIMITS.max_compression_ratio,
    )
    raw = make_zip(
        [("SKILL.md", skill_md()), ("docs/", b""), ("assets/", b"")]
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper", limits=limits)
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "entry 數" in ei.value.errors[0].message


def test_directory_entry_with_payload_is_rejected_before_size_ratio_bypass():
    raw = make_zip(
        [("SKILL.md", skill_md()), ("docs/", b"x" * 300_000)]
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "directory entry 不得含 payload" in ei.value.errors[0].message


def test_single_file_on_point_accepts():
    # 恰好 max_single_file_bytes（stored 以免壓縮比干擾）
    data = b"\0" * LIMITS.max_single_file_bytes
    raw = make_zip(
        [("SKILL.md", skill_md()), ("assets/big.bin", data)], zipfile.ZIP_STORED
    )
    parsed = package.parse_package(raw, "sales-helper")
    assert "assets/big.bin" in parsed.entries


def test_single_file_off_point_rejects():
    data = b"\0" * (LIMITS.max_single_file_bytes + 1)
    raw = make_zip(
        [("SKILL.md", skill_md()), ("assets/big.bin", data)], zipfile.ZIP_STORED
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "單檔" in ei.value.errors[0].message


def _files_totalling(total: int) -> list:
    """把 total bytes 切成每個 <= 單檔上限的多個 assets 檔（stored，壓縮比 ~1）。"""
    per = LIMITS.max_single_file_bytes
    files = []
    i = 0
    remaining = total
    while remaining > 0:
        chunk = min(per, remaining)
        files.append((f"assets/p{i}.bin", b"\0" * chunk))
        remaining -= chunk
        i += 1
    return files


def test_total_uncompressed_on_point_passes_size_gate():
    # 恰好 max_total（僅 assets、無 SKILL.md）→ 總大小關卡放行，之後才因缺 SKILL.md 出局。
    # 以「不是總大小錯誤」證明 on-point 被接受（size gate 在 dispatch 前）。
    raw = make_zip(_files_totalling(LIMITS.max_total_uncompressed_bytes), zipfile.ZIP_STORED)
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.MISSING_SKILL_MD


def test_total_uncompressed_off_point_rejects():
    raw = make_zip(
        _files_totalling(LIMITS.max_total_uncompressed_bytes + 1), zipfile.ZIP_STORED
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "總大小" in ei.value.errors[0].message


def test_compression_ratio_on_point_accepts():
    # ZIP_STORED → compress_size == file_size → ratio ~1 ≤ 上限，放行。
    raw = make_zip(
        [("SKILL.md", skill_md()), ("assets/a.bin", b"\0" * 4096)], zipfile.ZIP_STORED
    )
    parsed = package.parse_package(raw, "sales-helper")
    assert "assets/a.bin" in parsed.entries


def test_compression_ratio_off_point_rejects():
    # deflate 的高度可壓縮 bomb：ratio 遠超上限，但單檔/總大小仍在限內 → 只中壓縮比。
    # ponytail: 真 zlib 下無法精準命中 ratio==上限±1，故 accept 側用 stored(ratio 1)、
    # reject 側用遠超上限的 bomb；決策表兩半都有背書，邊界值本身受 zlib 決定不可控。
    payload = b"\0" * 300_000  # 300KB < 1MB 單檔、< 4MB 總量；deflate 後 ratio 破百
    raw = make_zip([("SKILL.md", skill_md()), ("assets/bomb.bin", payload)])
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE
    assert "壓縮比" in ei.value.errors[0].message


# ---------------------------------------------------------------------------
# 正向：合法 agentic + flow package
# ---------------------------------------------------------------------------


def test_valid_agentic_package_parses():
    raw = agentic_zip(
        uses_tools=["local.calculator"],
        extra={"references/g.md": b"guide", "assets/a.txt": b"asset"},
    )
    parsed = package.parse_package(raw, "sales-helper")
    assert parsed.kind == "agentic"
    assert parsed.skill.kind == "agentic"
    assert parsed.skill.name == "sales-helper"
    assert parsed.agentic.instruction.strip() == "You are a helpful sales assistant."
    assert parsed.agentic.resources == {"references/g.md": b"guide", "assets/a.txt": b"asset"}
    # §3 canonical 為 skills-ref-friendly：頂層只有標準欄位（name 起首，kind 在 metadata 內）
    assert parsed.canonical_definition.startswith("name: sales-helper")
    assert "kind: agentic" in parsed.canonical_definition
    assert len(parsed.sha256) == 64


# ---------------------------------------------------------------------------
# §3：標準 frontmatter 往返（metadata kind/required_role/timeout_seconds/input_schema-JSON
#     + allowed-tools + license/compatibility）；canonical 以同一 parser 解回等價 flat Skill。
# ---------------------------------------------------------------------------


def test_agentic_standard_frontmatter_round_trip():
    raw = agentic_zip(
        uses_tools=["local.calculator"],
        timeout_seconds="30",
        license="Proprietary",
        compatibility="Requires the platform skill engine",
    )
    parsed = package.parse_package(raw, "sales-helper")
    s = parsed.skill
    # allowed-tools（空白分隔）→ uses_tools；metadata.timeout_seconds（字串）→ int；
    # metadata.input_schema（JSON 字串）→ dict[str, InputField]
    assert s.uses_tools == ["local.calculator"]
    assert s.timeout_seconds == 30
    assert s.required_role == "USER"
    assert s.input_schema["query"].required is True
    assert s.input_schema["query"].min_length == 1

    # canonical（backend 儲存投影）以同一 skill_from_agentic_meta 解回等價 flat Skill
    # —— 這正是 custom.load 對 agentic 走的重解析路徑（§3 round-trip 契約）。
    meta = yaml.safe_load(parsed.canonical_definition)
    assert set(meta) <= {"name", "description", "license", "compatibility", "allowed-tools", "metadata"}
    assert meta["metadata"]["kind"] == "agentic"
    assert meta["metadata"]["timeout_seconds"] == "30"  # 字串
    skill2, lic, compat = package.skill_from_agentic_meta(meta)
    assert skill2.name == s.name
    assert skill2.uses_tools == s.uses_tools
    assert skill2.timeout_seconds == s.timeout_seconds
    assert skill2.input_schema == s.input_schema
    assert lic == "Proprietary"
    assert compat == "Requires the platform skill engine"


def test_agentic_input_schema_json_drives_schema():
    raw = agentic_zip(
        input_schema_json='{"topic": {"type": "str", "required": true}, "n": {"type": "int"}}'
    )
    parsed = package.parse_package(raw, "sales-helper")
    assert parsed.skill.input_schema["topic"].required is True
    assert parsed.skill.input_schema["n"].type == "int"


def test_description_length_on_off_point():
    from app.engine.package import DESCRIPTION_MAX

    # on-point：恰好上限 → 接受
    package.parse_package(agentic_zip(description="a" * DESCRIPTION_MAX), "sales-helper")
    # off-point：超一字 → invalid_frontmatter
    with pytest.raises(PackageError) as ei:
        package.parse_package(
            agentic_zip(description="a" * (DESCRIPTION_MAX + 1)), "sales-helper"
        )
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


def test_compatibility_length_on_off_point():
    from app.engine.package import COMPATIBILITY_MAX

    package.parse_package(
        agentic_zip(compatibility="c" * COMPATIBILITY_MAX), "sales-helper"
    )
    with pytest.raises(PackageError) as ei:
        package.parse_package(
            agentic_zip(compatibility="c" * (COMPATIBILITY_MAX + 1)), "sales-helper"
        )
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


@pytest.mark.parametrize(
    ("field", "yaml_value", "fragment"),
    [
        ("license", "''", "license 必須是非空字串"),
        ("license", "'   '", "license 必須是非空字串"),
        ("license", "[MIT]", "license 必須是非空字串"),
        ("compatibility", "[a, b]", "compatibility 必須是字串"),
    ],
    ids=["license-empty", "license-blank", "license-non-string", "compatibility-non-string"],
)
def test_license_and_compatibility_type_and_blank_rejected(field, yaml_value, fragment):
    """標準頂層欄位的型別/空白半邊（通過半邊見 round_trip 的 Proprietary/相容性字串）。"""
    with pytest.raises(PackageError) as ei:
        package.parse_package(agentic_zip(**{field: yaml_value}), "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert fragment in ei.value.errors[0].message


def test_agentic_bad_input_schema_json_rejected():
    raw = agentic_zip(input_schema_json="not json at all")
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


@pytest.mark.parametrize(
    "timeout",
    [
        str(package.TIMEOUT_SECONDS_MIN),
        str(package.TIMEOUT_SECONDS_MAX),
    ],
)
def test_agentic_timeout_seconds_on_point_accepts(timeout):
    parsed = package.parse_package(
        agentic_zip(timeout_seconds=timeout), "sales-helper"
    )
    assert parsed.skill.timeout_seconds == int(timeout)


@pytest.mark.parametrize(
    "timeout",
    [
        "0",
        str(package.TIMEOUT_SECONDS_MAX + 1),
        "9" * 10_000,
    ],
    ids=["below-min", "above-max", "overlong"],
)
def test_agentic_timeout_seconds_off_point_is_controlled(timeout):
    with pytest.raises(PackageError) as ei:
        package.parse_package(
            agentic_zip(timeout_seconds=timeout), "sales-helper"
        )
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


# ---------------------------------------------------------------------------
# §4：檔案允許清單放寬 —— 額外 root 檔 + 額外資料夾被接受且 bytes 原封往返；
#     穿越/超限仍拒；scripts/*.py 仍只存不執行（見上方 forbidden/stored 兩半）。
# ---------------------------------------------------------------------------


def test_extra_files_and_dirs_accepted_and_byte_preserved():
    raw = agentic_zip(
        extra={
            "LICENSE.txt": b"MIT-ish license text",
            "docs/guide.md": b"# guide\ncontent\n",
            "data/nested/x.json": b'{"k": 1}',
        }
    )
    parsed = package.parse_package(raw, "sales-helper")
    assert parsed.kind == "agentic"
    for path in ("LICENSE.txt", "docs/guide.md", "data/nested/x.json"):
        assert path in parsed.entries  # 新允許的任意檔案/資料夾都在 manifest
    # 唯讀 resource：bytes 原封保留（round-trip byte-identical）
    assert parsed.agentic.resources["LICENSE.txt"] == b"MIT-ish license text"
    assert parsed.agentic.resources["docs/guide.md"] == b"# guide\ncontent\n"
    assert parsed.agentic.resources["data/nested/x.json"] == b'{"k": 1}'


def test_extra_files_still_reject_traversal():
    # 放寬額外檔案不放寬安全護欄：穿越仍拒。
    raw = make_zip([("SKILL.md", skill_md()), ("docs/../../evil", b"x")])
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "sales-helper")
    assert ei.value.errors[0].code == package.INVALID_PACKAGE


# ---------------------------------------------------------------------------
# §3.1：flow package = 單一自包含 SKILL.md（name+description frontmatter + ```yaml 區塊）
# ---------------------------------------------------------------------------


def flow_skill_md(name, description, definition, prose="") -> str:
    """後端 exporter 的 flow SKILL.md 格式：frontmatter + 可選散文 + 逐字 ```yaml 區塊。

    以 `\\n{definition}\\n` 包夾，抽取即得 definition byte-identical（definition 尾端有無 \\n 皆可）。
    """
    body = f"{prose}```yaml\n{definition}\n```\n"
    return f"---\nname: {name}\ndescription: {description}\n---\n\n{body}"


FLOW_YAML = (
    "name: flow-probe\n"
    "description: flow 匯出\n"
    "input_schema:\n"
    "  query: {type: str, required: true, min_length: 1}\n"
    "flow:\n"
    "  - node: query_intake@1.0\n"
)


def test_flow_package_extracts_embedded_yaml_byte_for_byte():
    md = flow_skill_md("flow-probe", "flow 匯出", FLOW_YAML, prose="說明文字\n\n")
    raw = make_zip({"SKILL.md": md})
    parsed = package.parse_package(raw, "flow-probe")
    assert parsed.kind == "flow"
    assert parsed.skill.kind == "flow"
    # 抽出的定義原封不動作為 definition（byte-identical round-trip）
    assert parsed.canonical_definition == FLOW_YAML


def test_flow_package_crlf_fence_preserves_definition_byte_for_byte():
    definition = FLOW_YAML.replace("\n", "\r\n")
    md = (
        "---\r\n"
        "name: flow-probe\r\n"
        "description: flow 匯出\r\n"
        "---\r\n\r\n"
        f"```yaml\r\n{definition}\r\n```\r\n"
    )
    parsed = package.parse_package(make_zip({"SKILL.md": md}), "flow-probe")
    assert parsed.canonical_definition == definition
    assert parsed.skill.name == "flow-probe"
    assert parsed.skill.description == "flow 匯出"


@pytest.mark.parametrize(
    "tail",
    ["", "\n", "\r\n", "\r"],
    ids=["none", "lf", "crlf", "lone-cr"],
)
def test_flow_fence_preserves_each_definition_tail_byte_for_byte(tail):
    definition = FLOW_YAML.rstrip("\n") + tail
    md = (
        "---\n"
        "name: flow-probe\n"
        "description: flow 匯出\n"
        "---\n\n"
        f"```yaml\n{definition}\n```\n"
    )
    parsed = package.parse_package(make_zip({"SKILL.md": md}), "flow-probe")
    assert parsed.canonical_definition == definition
    assert parsed.skill.name == "flow-probe"


def test_flow_package_ignores_other_language_code_blocks():
    # body 可含 ```python 等其他語言區塊（散文）；只有第一個 ```yaml 是定義。
    prose = "先來段範例：\n\n```python\nprint('not the definition')\n```\n\n再來定義：\n\n"
    md = flow_skill_md("flow-probe", "flow 匯出", FLOW_YAML, prose=prose)
    raw = make_zip({"SKILL.md": md})
    parsed = package.parse_package(raw, "flow-probe")
    assert parsed.canonical_definition == FLOW_YAML  # python 區塊被忽略
    assert "print(" not in parsed.canonical_definition


def test_flow_package_missing_yaml_block_rejected():
    md = "---\nname: flow-probe\ndescription: 無定義區塊\n---\n\n只有散文，沒有 yaml 區塊。\n"
    raw = make_zip({"SKILL.md": md})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "flow-probe")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert "定義區塊" in ei.value.errors[0].message


def test_flow_frontmatter_name_must_match_route_and_embedded_definition():
    md = flow_skill_md("wrong-name", "flow 匯出", FLOW_YAML)
    raw = make_zip({"SKILL.md": md})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "flow-probe")
    assert ei.value.errors[0].code == package.NAME_MISMATCH
    assert "frontmatter" in ei.value.errors[0].message


def test_flow_definition_name_must_equal_frontmatter_name():
    """三方比對的第三格：frontmatter name 對得上匯入路徑，但內嵌 yaml 自己改了名。

    這是「用編輯內嵌定義改名」的身分繞道 —— frontmatter 那關過了，真正被存進 revision
    的卻是另一個名字。package.py:885 擋的就是這一格（前兩格都命中 :867）。
    """
    definition = FLOW_YAML.replace("name: flow-probe", "name: other-flow", 1)
    md = flow_skill_md("flow-probe", "flow 匯出", definition)

    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "flow-probe")

    assert ei.value.errors[0].code == package.NAME_MISMATCH
    assert "flow 定義 name" in ei.value.errors[0].message


@pytest.mark.parametrize("description", ["", "   "])
def test_flow_frontmatter_requires_nonblank_description(description):
    md = flow_skill_md("flow-probe", description, FLOW_YAML)
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "flow-probe")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER


def test_flow_frontmatter_description_must_match_definition():
    md = flow_skill_md("flow-probe", "另一個說明", FLOW_YAML)
    with pytest.raises(PackageError) as ei:
        package.parse_package(make_zip({"SKILL.md": md}), "flow-probe")
    assert ei.value.errors[0].code == package.INVALID_FRONTMATTER
    assert "description" in ei.value.errors[0].message


def test_flow_forbidden_script_is_scanned_and_rejected():
    md = flow_skill_md("flow-probe", "flow 匯出", FLOW_YAML)
    raw = make_zip({"SKILL.md": md, "scripts/escape.py": "import os\n"})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "flow-probe")
    assert ei.value.errors[0].code == package.FORBIDDEN_SCRIPT


def test_flow_safe_script_is_scanned_but_never_executed():
    md = flow_skill_md("flow-probe", "flow 匯出", FLOW_YAML)
    raw = make_zip(
        {
            "SKILL.md": md,
            "scripts/safe.py": 'state["package_script_executed"] = True',
        }
    )
    parsed = package.parse_package(raw, "flow-probe")
    assert parsed.kind == "flow"
    assert "package_script_executed" not in parsed.canonical_definition


def test_flow_script_step_authoring_role_gate_both_halves():
    """author_role 三格：ADMIN 通過、非 ADMIN forbidden_script、省略（runtime 載入）不檢查。

    這是 /skills/validate 同一套撰寫者 gate 在匯入路徑的那一格；package 匯入是寫入路徑，
    非 ADMIN 不得靠「包成 zip 匯入」繞過 script 撰寫限制。
    """
    definition = FLOW_YAML.replace(
        "flow:\n  - node: query_intake@1.0\n",
        'flow:\n  - script: |\n      state["final_answer"] = "echo: " + state["query"]\n',
    )
    raw = make_zip({"SKILL.md": flow_skill_md("flow-probe", "flow 匯出", definition)})

    parsed = package.parse_package(raw, "flow-probe", author_role="ADMIN")
    assert parsed.canonical_definition == definition

    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "flow-probe", author_role="USER")
    assert ei.value.errors[0].code == package.FORBIDDEN_SCRIPT
    assert "僅限 ADMIN 撰寫" in ei.value.errors[0].message

    # 省略 author_role = runtime 載入既有 package 的路徑：gate 關閉，照常解析。
    assert package.parse_package(raw, "flow-probe").kind == "flow"


def test_flow_non_python_file_under_scripts_is_read_only_resource():
    md = flow_skill_md("flow-probe", "flow 匯出", FLOW_YAML)
    raw = make_zip({"SKILL.md": md, "scripts/README.md": b"\xffnotes\x00"})
    parsed = package.parse_package(raw, "flow-probe")
    assert parsed.kind == "flow"
    assert "scripts/README.md" in parsed.entries


def test_flow_package_invalid_yaml_uses_existing_validator_codes():
    md = flow_skill_md("flow-probe", "d", "name: flow-probe\nflow:\n  - node: no_such_node")
    raw = make_zip({"SKILL.md": md})
    with pytest.raises(PackageError) as ei:
        package.parse_package(raw, "flow-probe")
    assert ei.value.errors[0].code == "unknown_node"  # 既有 validator 的錯誤碼
