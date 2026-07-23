"""Agent Skill package 解析器（P0：解壓、安全驗證、frontmatter 解析、canonical 投影）。

## 為什麼要獨立成一層

zip 是外部作者提交的二進位資料，信任邊界從「已解析的 YAML 定義」往外推到「未解壓的
壓縮檔」。解壓本身就是攻擊面（path traversal、drive path、symlink、zip bomb），所以
**安全解壓與白名單佈局檢查集中在這裡一次做完**，HTTP 層只負責把 bytes 交進來、把
結果轉成回應，不各自硬寫任何限制常數（規格 R2：上限不得散落在 controller/parser/UI）。

## 白名單、預設拒絕

- 路徑安全（§4）：**接受任意額外檔案/資料夾**，但一律拒 `..`／`.`、絕對路徑、drive path、
  空路徑、正規化重複、symlink-like entry 與四維上限（`LIMITS` 單一來源）。`SKILL.md` 仍必須
  在 root；額外檔案當唯讀 resource 儲存，永不執行/解讀。
- frontmatter 是 agentic metadata 的**唯一**來源：標準欄位在頂層（name/description/license?/
  compatibility?/allowed-tools?），引擎專屬需求收進 `metadata`（§3），經既有 Skill schema 驗證。
- `allowed-tools`（空白分隔）重用既有 `tool_registry` 的 `unknown_tool` 檢查；`scripts/*.py`
  重用既有 `script_runner.scan`（通過只代表格式合格，**不建立可執行步驟、不執行**，規格 R8）。

## 兩種格式的分派（皆為單一自包含 `SKILL.md`，§3.1）

frontmatter `metadata.kind: agentic` → agentic（body 為 runner instruction）；否則 → flow
（body 內第一個 ```yaml fenced block 的逐字文字即 definition，走既有 YAML validator，
不重新序列化以保 byte-equality）。不再需要獨立 `skill.yaml`（§6 clean break）。
"""

import bz2
import hashlib
import io
import json
import lzma
import re
import struct
import zipfile
import zlib
from dataclasses import dataclass
from typing import Any, Mapping

import yaml
from pydantic import ValidationError

from app.engine import script_runner, tool_registry
from app.engine.skill import Skill, SkillError, parse_source, validate_source

# ---------------------------------------------------------------------------
# 錯誤碼（可定位到 package/frontmatter；沿用既有 unknown_tool / forbidden_script）
# ---------------------------------------------------------------------------

INVALID_PACKAGE = "invalid_package"  # zip 壞、path traversal、drive、dup、symlink、佈局、限制
MISSING_SKILL_MD = "missing_skill_md"
INVALID_FRONTMATTER = "invalid_frontmatter"  # 無 frontmatter、壞 YAML、schema 失敗、非 agentic kind
NAME_MISMATCH = "name_mismatch"
UNKNOWN_TOOL = "unknown_tool"  # = skill.UNKNOWN_TOOL（frontmatter uses_tools 未註冊）
FORBIDDEN_SCRIPT = "forbidden_script"  # = skill.FORBIDDEN_SCRIPT（scripts/*.py 未過 AST scan）


# ---------------------------------------------------------------------------
# 限制：單一不可變來源。所有呼叫端（endpoint、測試）都引用這個物件，不各自硬寫數字。
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class PackageLimits:
    """package 解壓上限（規格 R2）。四個維度各自有 on-point/off-point 測試背書。"""

    max_file_count: int
    max_single_file_bytes: int
    max_total_uncompressed_bytes: int
    max_compression_ratio: int


# 唯一來源常數。endpoint 引用 LIMITS；測試以 LIMITS 的欄位建邊界 fixture（證明單一來源）。
LIMITS = PackageLimits(
    max_file_count=64,
    max_single_file_bytes=1_048_576,  # 1 MiB
    max_total_uncompressed_bytes=4_194_304,  # 4 MiB
    max_compression_ratio=100,
)

# Agent timeout 與 backend `workflow.timeout_seconds` 同樣以 signed 64-bit 整數為既有
# 跨服務上限（backend 用 JsonElement.TryGetInt64）。先限字數再轉 int，避免超長十進位字串
# 觸發 Python 的 int digit-limit ValueError 或浪費不成比例的 CPU。
TIMEOUT_SECONDS_MIN = 1
TIMEOUT_SECONDS_MAX = 9_223_372_036_854_775_807
TIMEOUT_SECONDS_MAX_DIGITS = len(str(TIMEOUT_SECONDS_MAX))


# ---------------------------------------------------------------------------
# 解析結果（不可變）
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class AgentSkillPackage:
    """agentic package 的不可變解析結果（設計 §3）。P1 runner 的輸入。

    scripts 通過 AST scan 只代表格式合格：P0/P1 儲存但不執行（規格 R8）。
    """

    skill: Skill
    instruction: str
    resources: Mapping[str, bytes]
    scripts: Mapping[str, str]
    sha256: str


@dataclass(frozen=True)
class ValidatedPackage:
    """endpoint 用的驗證結果：flow 與 agentic 共用形狀。

    canonical_definition 是「下游 Skill.model_validate 與 compile-cache key 的投影」：
    - flow：SKILL.md 內嵌 ```yaml 區塊抽出的原文（不重新序列化，保 byte-equality）。
    - agentic：從 frontmatter 產出的 YAML 投影（作者權威是 frontmatter，這是相容/編譯投影）。
    """

    kind: str
    skill: Skill
    canonical_definition: str
    entries: tuple[str, ...]
    sha256: str
    agentic: AgentSkillPackage | None  # 只有 agentic 有；flow 為 None


class PackageError(Exception):
    """解析失敗；errors 逐條可定位（code 照上面錯誤碼表）。零副作用由呼叫端保證。"""

    def __init__(self, errors: list[SkillError]):
        self.errors = errors
        super().__init__("; ".join(e.message for e in errors))

    @classmethod
    def of(cls, code: str, message: str, line: int | None = None) -> "PackageError":
        return cls([SkillError(code=code, message=message, line=line)])


# ---------------------------------------------------------------------------
# 安全解壓
# ---------------------------------------------------------------------------

SKILL_MD = "SKILL.md"
_DRIVE_RE = re.compile(r"^[a-zA-Z]:")
_LOCAL_HEADER = struct.Struct("<4s5H3L2H")
_LOCAL_HEADER_SIGNATURE = b"PK\x03\x04"
_DATA_DESCRIPTOR_SIGNATURE = b"PK\x07\x08"
_ZIP64_EXTRA_ID = 0x0001
_ZIP32_SENTINEL = 0xFFFFFFFF
_SUPPORTED_COMPRESSION = frozenset(
    {
        zipfile.ZIP_STORED,
        zipfile.ZIP_DEFLATED,
        zipfile.ZIP_BZIP2,
        zipfile.ZIP_LZMA,
    }
)


def _reject(msg: str) -> "PackageError":
    return PackageError.of(INVALID_PACKAGE, msg)


def _validate_entry_path(name: str) -> str:
    """驗證單一 entry 路徑並回傳正規化路徑；違規一律 raise（安全護欄、預設拒絕）。

    §4 放寬：接受任意額外檔案/資料夾（不再限定 SKILL.md + scripts/references/assets/），
    但**安全護欄一律維持**：空路徑、絕對路徑、drive path、`..`／`.` 成分一律拒。額外檔案由
    上層當唯讀 resource 儲存（永不執行；scripts/*.py 仍過 AST scan，見 _parse_agentic）。
    """
    if not name or not name.strip():
        raise _reject("archive 含空路徑 entry")
    # ZIP 規格使用 `/`，但攻擊者可直接寫入 `\`。先正規化再驗證，讓 `..\x`、
    # `C:\x` 與其 `/` 版本得到完全相同的拒絕結果。
    normalized = name.replace("\\", "/")
    if normalized.startswith("/"):
        raise _reject(f"不允許絕對路徑 entry: {name!r}")
    if _DRIVE_RE.match(normalized):
        raise _reject(f"不允許 Windows drive path entry: {name!r}")
    # directory entry 的尾端 `/` 不構成空名稱；其餘空 segment（`a//b`）一律拒絕，
    # 避免不同解壓器折疊後形成正規化重複。
    canonical = normalized[:-1] if normalized.endswith("/") else normalized
    parts = canonical.split("/")
    if not canonical or any(p in ("", "..", ".") for p in parts):
        raise _reject(f"不允許空白、'..' 或 '.' 路徑成分: {name!r}")
    return canonical


def _is_symlink(info: zipfile.ZipInfo) -> bool:
    """external_attr 高 16 位是 Unix mode；S_IFLNK(0o120000) → symlink-like entry。"""
    return (info.external_attr >> 16) & 0o170000 == 0o120000


@dataclass(frozen=True)
class _LocalEntry:
    flags: int
    method: int
    crc: int
    compressed_size: int
    uncompressed_size: int
    data_offset: int
    uses_zip64: bool


def _resolve_local_zip64_sizes(
    extra: bytes,
    raw_compressed: int,
    raw_uncompressed: int,
    filename: str,
) -> tuple[int, int, bool]:
    """依 APPNOTE 的 ZIP64 extra 欄位順序還原 local 32-bit sentinel。"""
    zip64_data: bytes | None = None
    offset = 0
    while offset < len(extra):
        if offset + 4 > len(extra):
            raise _reject(f"entry local extra 截斷: {filename!r}")
        field_id, field_size = struct.unpack_from("<HH", extra, offset)
        offset += 4
        end = offset + field_size
        if end > len(extra):
            raise _reject(f"entry local extra 長度越界: {filename!r}")
        if field_id == _ZIP64_EXTRA_ID:
            if zip64_data is not None:
                raise _reject(f"entry local ZIP64 extra 重複: {filename!r}")
            zip64_data = extra[offset:end]
        offset = end

    needs_uncompressed = raw_uncompressed == _ZIP32_SENTINEL
    needs_compressed = raw_compressed == _ZIP32_SENTINEL
    if not needs_uncompressed and not needs_compressed:
        return raw_compressed, raw_uncompressed, False
    if zip64_data is None:
        raise _reject(f"entry local ZIP64 sentinel 缺少 extra: {filename!r}")

    cursor = 0

    def take_u64(label: str) -> int:
        nonlocal cursor
        if cursor + 8 > len(zip64_data):
            raise _reject(f"entry local ZIP64 {label} 截斷: {filename!r}")
        value = struct.unpack_from("<Q", zip64_data, cursor)[0]
        cursor += 8
        return value

    uncompressed = (
        take_u64("uncompressed size") if needs_uncompressed else raw_uncompressed
    )
    compressed = take_u64("compressed size") if needs_compressed else raw_compressed
    return compressed, uncompressed, True


def _local_header(raw: bytes, info: zipfile.ZipInfo) -> _LocalEntry:
    """讀取並核對 local header，回 flags/method/crc/compressed size/data offset。

    ZipInfo 只代表 central directory；信任邊界必須確認 local header 指向同一個 entry，
    否則 central 可偽造 size/CRC 讓 directory payload 被略過。
    """
    start = info.header_offset
    end = start + _LOCAL_HEADER.size
    if start < 0 or end > len(raw):
        raise _reject(f"entry local header 越界: {info.filename!r}")
    (
        signature,
        _version,
        flags,
        method,
        _time,
        _date,
        crc,
        compressed_size,
        uncompressed_size,
        name_len,
        extra_len,
    ) = _LOCAL_HEADER.unpack(raw[start:end])
    if signature != _LOCAL_HEADER_SIGNATURE:
        raise _reject(f"entry local header signature 不合法: {info.filename!r}")
    name_end = end + name_len
    data_offset = name_end + extra_len
    if data_offset > len(raw):
        raise _reject(f"entry local header 名稱/extra 越界: {info.filename!r}")

    name_bytes = raw[end:name_end]
    encoding = "utf-8" if flags & 0x800 else "cp437"
    try:
        local_name = name_bytes.decode(encoding)
    except UnicodeDecodeError:
        raise _reject(f"entry local header 名稱編碼不合法: {info.filename!r}")
    if local_name != info.orig_filename:
        raise _reject(f"entry local/central 名稱不一致: {info.filename!r}")
    if flags != info.flag_bits or method != info.compress_type:
        raise _reject(f"entry local/central flags 或壓縮方法不一致: {info.filename!r}")
    if flags & 0x1:
        raise _reject(f"不允許 encrypted entry: {info.filename!r}")
    if method not in _SUPPORTED_COMPRESSION:
        raise _reject(f"不支援的壓縮方法 {method}: {info.filename!r}")
    compressed_size, uncompressed_size, uses_zip64 = _resolve_local_zip64_sizes(
        raw[name_end:data_offset],
        compressed_size,
        uncompressed_size,
        info.filename,
    )
    return _LocalEntry(
        flags=flags,
        method=method,
        crc=crc,
        compressed_size=compressed_size,
        uncompressed_size=uncompressed_size,
        data_offset=data_offset,
        uses_zip64=uses_zip64,
    )


def _validate_data_descriptor(
    raw: bytes,
    start: int,
    end: int,
    uses_zip64: bool,
    info: zipfile.ZipInfo,
) -> None:
    """驗證 data descriptor（optional signature + 32/64-bit sizes）恰好填滿 boundary。"""
    if start < 0 or end < start or end > len(raw):
        raise _reject(f"directory data descriptor 邊界不合法: {info.filename!r}")
    block = raw[start:end]
    size_format = "Q" if uses_zip64 else "L"
    payload_size = 4 + (8 if uses_zip64 else 4) * 2
    if len(block) == payload_size + 4 and block.startswith(
        _DATA_DESCRIPTOR_SIGNATURE
    ):
        block = block[4:]
    elif len(block) != payload_size:
        raise _reject(f"directory data descriptor 長度不合法: {info.filename!r}")
    crc, compressed, uncompressed = struct.unpack(
        f"<L{size_format}{size_format}", block
    )
    if (
        crc != info.CRC
        or compressed != info.compress_size
        or uncompressed != info.file_size
    ):
        raise _reject(f"directory data descriptor 與 central 不一致: {info.filename!r}")


def _validate_empty_directory(
    raw: bytes,
    info: zipfile.ZipInfo,
    local: _LocalEntry,
    next_header_offset: int,
) -> None:
    """證明 explicit directory 實際解壓內容為空，不能只信 central `file_size == 0`。"""
    data_end = local.data_offset + info.compress_size
    if data_end > next_header_offset or data_end > len(raw):
        raise _reject(f"directory entry 實際資料邊界不一致: {info.filename!r}")
    if local.flags & 0x8:
        _validate_data_descriptor(
            raw, data_end, next_header_offset, local.uses_zip64, info
        )
    elif data_end != next_header_offset:
        raise _reject(f"directory entry 實際資料邊界不一致: {info.filename!r}")

    compressed = raw[local.data_offset:data_end]
    try:
        if local.method == zipfile.ZIP_STORED:
            output = compressed[:1]
            eof = len(compressed) == 0
            unused_data = b""
            unconsumed_tail = b""
        elif local.method == zipfile.ZIP_DEFLATED:
            decoder = zlib.decompressobj(-zlib.MAX_WBITS)
            output = decoder.decompress(compressed, max_length=1)
            eof = decoder.eof
            unused_data = decoder.unused_data
            unconsumed_tail = decoder.unconsumed_tail
        elif local.method == zipfile.ZIP_BZIP2:
            decoder = bz2.BZ2Decompressor()
            output = decoder.decompress(compressed, max_length=1)
            eof = decoder.eof
            unused_data = decoder.unused_data
            unconsumed_tail = b""
        elif local.method == zipfile.ZIP_LZMA:
            # ZIP LZMA framing = 4-byte prefix + LZMA1 filter properties + FORMAT_RAW stream；
            # framing 對齊 stdlib zipfile.LZMADecompressor，但真正 decode 直接使用
            # lzma.LZMADecompressor(...).decompress(max_length=1)，避免 wrapper 無上限輸出。
            if len(compressed) < 4:
                raise ValueError("ZIP LZMA header 截斷")
            properties_size = struct.unpack_from("<H", compressed, 2)[0]
            payload_at = 4 + properties_size
            if payload_at > len(compressed):
                raise ValueError("ZIP LZMA properties 截斷")
            filters = [
                lzma._decode_filter_properties(  # type: ignore[attr-defined]
                    lzma.FILTER_LZMA1, compressed[4:payload_at]
                )
            ]
            decoder = lzma.LZMADecompressor(lzma.FORMAT_RAW, filters=filters)
            output = decoder.decompress(
                compressed[payload_at:], max_length=1
            )
            eof = decoder.eof
            unused_data = decoder.unused_data
            unconsumed_tail = b""
        else:  # _local_header 已用 _SUPPORTED_COMPRESSION fail-closed
            raise ValueError(f"unsupported compression method {local.method}")
    except (OSError, EOFError, ValueError, zlib.error, lzma.LZMAError) as e:
        raise _reject(f"directory entry 壓縮內容損壞: {info.filename!r}: {e}")

    if output:
        raise _reject(f"directory entry 不得含 payload: {info.filename!r}")
    if not eof or unused_data or unconsumed_tail:
        raise _reject(f"directory entry 壓縮內容未完整結束: {info.filename!r}")
    # Empty 的 CRC32 固定為 0；搭配 central size=0，補上 decoder EOF 後的語意核對。
    if info.file_size != 0 or info.CRC != 0:
        raise _reject(f"directory entry central size 或 CRC 非空: {info.filename!r}")


def _read_entries(raw: bytes, limits: PackageLimits) -> dict[str, bytes]:
    """安全解壓：白名單佈局 + 四維限制。回傳 正規化路徑 → 內容 bytes。"""
    try:
        zf = zipfile.ZipFile(io.BytesIO(raw))
    except zipfile.BadZipFile as e:
        raise _reject(f"不是合法的 zip 檔: {e}")

    try:
        infos = zf.infolist()
        # 所有 ZipInfo 都消耗 archive 結構預算；否則大量 directory entry 可繞過 entry count。
        if len(infos) > limits.max_file_count:
            raise _reject(
                f"archive entry 數 {len(infos)} 超過上限 {limits.max_file_count}"
            )

        entries: dict[str, bytes] = {}
        seen_paths: set[str] = set()
        total_uncompressed = 0
        total_compressed = 0
        ordered_offsets = sorted(info.header_offset for info in infos)
        next_offsets = {
            offset: (
                ordered_offsets[index + 1]
                if index + 1 < len(ordered_offsets)
                else zf.start_dir
            )
            for index, offset in enumerate(ordered_offsets)
        }
        for info in infos:
            # directory entry 同樣位於不可信 archive 邊界：必須先做 symlink/path/duplicate
            # 驗證，不能因 `is_dir()` 而略過安全護欄。
            if _is_symlink(info):
                raise _reject(f"不允許 symlink-like entry: {info.filename!r}")
            # 在 is_dir() 分支前核對 local/central header，確保 directory 也不能夾帶
            # encrypted/unsupported/mismatched entry。
            local = _local_header(raw, info)
            if not local.flags & 0x8 and (
                local.crc != info.CRC
                or local.compressed_size != info.compress_size
                or local.uncompressed_size != info.file_size
            ):
                raise _reject(
                    f"entry local/central CRC 或大小不一致: {info.filename!r}"
                )
            path = _validate_entry_path(info.filename)
            if path in seen_paths:
                raise _reject(f"正規化後重複的 entry: {path!r}")
            seen_paths.add(path)
            if info.is_dir():
                _validate_empty_directory(
                    raw, info, local, next_offsets[info.header_offset]
                )
                continue

            # 逐段讀取並在解壓當下卡單檔上限：避免相信會說謊的 header size 而先吃下 zip bomb
            with zf.open(info) as f:
                data = f.read(limits.max_single_file_bytes + 1)
            if len(data) > limits.max_single_file_bytes:
                raise _reject(
                    f"檔案 {path!r} 解壓後超過單檔上限 {limits.max_single_file_bytes} bytes"
                )
            entries[path] = data
            total_uncompressed += len(data)
            total_compressed += info.compress_size

        if total_uncompressed > limits.max_total_uncompressed_bytes:
            raise _reject(
                f"archive 解壓總大小 {total_uncompressed} 超過上限 "
                f"{limits.max_total_uncompressed_bytes} bytes"
            )
        ratio = total_uncompressed / max(total_compressed, 1)
        if ratio > limits.max_compression_ratio:
            raise _reject(
                f"archive 壓縮比 {ratio:.1f} 超過上限 {limits.max_compression_ratio}"
            )
        return entries
    except PackageError:
        raise
    except (zipfile.BadZipFile, RuntimeError, NotImplementedError, EOFError) as e:
        # CRC mismatch、截斷 entry 或 encrypted/unsupported archive 都是不可信輸入錯誤，
        # 收斂成受控 validation result，不能讓 `/skills/validate-package` 噴 500。
        raise _reject(f"archive entry 無法安全讀取: {e}")
    finally:
        zf.close()


# ---------------------------------------------------------------------------
# frontmatter 解析（agentic）
# ---------------------------------------------------------------------------

# `---\n<yaml>\n---\n<body>`：起手第一行必須是 `---`
_FRONTMATTER_RE = re.compile(r"^---[ \t]*\r?\n(.*?)\r?\n---[ \t]*\r?\n?(.*)$", re.DOTALL)


def _split_frontmatter(text: str) -> tuple[str, str]:
    """切出 (frontmatter_yaml, body)；沒有 frontmatter → raise invalid_frontmatter。"""
    m = _FRONTMATTER_RE.match(text)
    if m is None:
        raise PackageError.of(
            INVALID_FRONTMATTER, "SKILL.md 缺少 YAML frontmatter（需 --- 包夾的區塊）"
        )
    return m.group(1), m.group(2)


# flow 定義嵌在 SKILL.md body 的第一個 ```yaml fenced block（§3.1）：取開頭 ```yaml 行與配對
# ``` 行之間的**逐字**文字作為 definition（等同舊 skill.yaml bytes 的角色）。其他語言的
# code block（```python…）與散文一律忽略。
# ponytail: 假設 flow YAML 定義本身不含 ``` fence 序列（flow YAML 從不含）→ 非貪婪匹配第一個
# ```yaml 區塊即完整定義。若日後定義可能含 fence，改成逐行掃描配對圍欄。
_FLOW_YAML_OPEN_RE = re.compile(r"^```yaml[ \t]*\r?\n", re.MULTILINE)
_FLOW_YAML_CLOSE_RE = re.compile(r"^```[ \t]*\r?$", re.MULTILINE)


def _extract_flow_definition(body: str) -> str | None:
    """從第一個 yaml fence 抽出逐字 definition；LF/CRLF 都支援且不改內部換行 bytes。"""
    opening = _FLOW_YAML_OPEN_RE.search(body)
    if opening is None:
        return None
    closing = _FLOW_YAML_CLOSE_RE.search(body, opening.end())
    if closing is None:
        return None
    content = body[opening.end() : closing.start()]
    # delimiter 精確沿用 opening fence 的 newline style，不能用 `content.endswith()` 猜：
    # definition 自己若以 lone CR 結尾，後接 LF delimiter 也會形成 CRLF，猜測會誤吞 lone CR。
    delimiter = "\r\n" if opening.group(0).endswith("\r\n") else "\n"
    return content[: -len(delimiter)] if content.endswith(delimiter) else None


# 標準 frontmatter 限制（§0/§3）。description 1–1024、compatibility ≤500。
DESCRIPTION_MAX = 1024
COMPATIBILITY_MAX = 500
_STANDARD_FRONTMATTER_FIELDS = frozenset(
    {"name", "description", "license", "compatibility", "metadata", "allowed-tools"}
)
_AGENTIC_METADATA_FIELDS = frozenset(
    {"kind", "required_role", "timeout_seconds", "input_schema"}
)


def _validate_standard_frontmatter(meta: Mapping[str, Any]) -> tuple[str, str]:
    """驗證 Agent Skills 標準共用 frontmatter，回 `(name, description)`。

    頂層採白名單以拒絕舊的 `kind`/`uses_tools`/engine 欄位；metadata 可帶未來擴充鍵，
    但標準要求所有 key/value 都必須是字串。
    """
    if not isinstance(meta, Mapping):
        raise PackageError.of(INVALID_FRONTMATTER, "frontmatter 必須是 YAML 對應（mapping）")
    if any(not isinstance(key, str) for key in meta):
        raise PackageError.of(INVALID_FRONTMATTER, "frontmatter 欄位名稱必須是字串")
    unknown = set(meta) - _STANDARD_FRONTMATTER_FIELDS
    if unknown:
        raise PackageError.of(
            INVALID_FRONTMATTER,
            f"frontmatter 含非標準頂層欄位: {sorted(unknown)}",
        )

    name = meta.get("name")
    if not isinstance(name, str) or not name:
        raise PackageError.of(INVALID_FRONTMATTER, "frontmatter name 為必填字串")
    description = meta.get("description")
    if not isinstance(description, str) or not description.strip():
        raise PackageError.of(INVALID_FRONTMATTER, "frontmatter description 為必填非空字串")
    if len(description) > DESCRIPTION_MAX:
        raise PackageError.of(
            INVALID_FRONTMATTER, f"description 超過 {DESCRIPTION_MAX} 字上限"
        )

    license_ = meta.get("license")
    if license_ is not None and (
        not isinstance(license_, str) or not license_.strip()
    ):
        raise PackageError.of(INVALID_FRONTMATTER, "license 必須是非空字串")
    compatibility = meta.get("compatibility")
    if compatibility is not None:
        if not isinstance(compatibility, str):
            raise PackageError.of(INVALID_FRONTMATTER, "compatibility 必須是字串")
        if len(compatibility) > COMPATIBILITY_MAX:
            raise PackageError.of(
                INVALID_FRONTMATTER, f"compatibility 超過 {COMPATIBILITY_MAX} 字上限"
            )
    if "allowed-tools" in meta and not isinstance(meta["allowed-tools"], str):
        raise PackageError.of(INVALID_FRONTMATTER, "allowed-tools 必須是空白分隔字串")

    metadata = meta.get("metadata")
    if metadata is not None:
        if not isinstance(metadata, Mapping):
            raise PackageError.of(INVALID_FRONTMATTER, "metadata 必須是 string→string 對應")
        if any(
            not isinstance(key, str) or not isinstance(value, str)
            for key, value in metadata.items()
        ):
            raise PackageError.of(INVALID_FRONTMATTER, "metadata 必須是 string→string 對應")
    return name, description


def _input_schema_json(skill: Skill) -> str:
    """input_schema → JSON 字串（metadata string→string 合規；exclude_none 去掉未設欄位）。"""
    plain = {
        k: v.model_dump(exclude_none=True) for k, v in skill.input_schema.items()
    }
    return json.dumps(plain, ensure_ascii=False, sort_keys=True)


def skill_from_agentic_meta(meta: Mapping[str, Any]) -> tuple[Skill, str | None, str | None]:
    """標準 agentic frontmatter dict → (flat Skill, license, compatibility)（§3）。

    標準欄位在頂層（name/description/license?/compatibility?/allowed-tools?）；引擎專屬需求
    收在 `metadata`（string→string）：kind（須為 agentic）、required_role、timeout_seconds（字串）、
    input_schema（JSON 字串，此處 json-load）。SKILL.md frontmatter 與 canonical 投影共用此解析。
    違規一律 raise PackageError(INVALID_FRONTMATTER)（白名單、預設拒絕）。
    """
    name, description = _validate_standard_frontmatter(meta)

    metadata = meta.get("metadata")
    if not isinstance(metadata, Mapping):
        raise PackageError.of(
            INVALID_FRONTMATTER, "agentic frontmatter 缺少 metadata 對應（含 kind: agentic）"
        )
    missing = _AGENTIC_METADATA_FIELDS - set(metadata)
    if missing:
        raise PackageError.of(
            INVALID_FRONTMATTER,
            f"agentic metadata 缺少必要欄位: {sorted(missing)}",
        )
    if metadata["kind"] != "agentic":
        raise PackageError.of(
            INVALID_FRONTMATTER,
            f"metadata.kind 必須為 'agentic'（得到 {metadata['kind']!r}）",
        )
    compatibility = meta.get("compatibility")
    license_ = meta.get("license")

    # allowed-tools：空白分隔字串（取代舊 uses_tools 頂層 list）。無 → []。
    raw_tools = meta.get("allowed-tools", "")
    uses_tools = raw_tools.split()

    # metadata 已驗證為 string→string；engine 契約要求 input_schema 為 JSON object 字串。
    raw_schema = metadata["input_schema"]
    try:
        input_schema: Any = json.loads(raw_schema)
    except json.JSONDecodeError as e:
        raise PackageError.of(
            INVALID_FRONTMATTER, f"metadata.input_schema 不是合法 JSON: {e}"
        )
    if not isinstance(input_schema, dict):
        raise PackageError.of(
            INVALID_FRONTMATTER, "metadata.input_schema JSON 必須是 object"
        )

    fields: dict[str, Any] = {
        "name": name,
        "description": description,
        "kind": "agentic",
        "input_schema": input_schema,
        "uses_tools": uses_tools,
        "flow": [],
        "required_role": metadata["required_role"],
    }
    ts = metadata["timeout_seconds"]
    if (
        len(ts) > TIMEOUT_SECONDS_MAX_DIGITS
        or not re.fullmatch(r"[1-9][0-9]*", ts)
    ):
        raise PackageError.of(
            INVALID_FRONTMATTER,
            f"metadata.timeout_seconds 必須是 "
            f"{TIMEOUT_SECONDS_MIN}~{TIMEOUT_SECONDS_MAX} 的整數字串",
        )
    try:
        timeout_seconds = int(ts)
    except (ValueError, OverflowError):
        raise PackageError.of(
            INVALID_FRONTMATTER, "metadata.timeout_seconds 無法解析為整數"
        )
    if not TIMEOUT_SECONDS_MIN <= timeout_seconds <= TIMEOUT_SECONDS_MAX:
        raise PackageError.of(
            INVALID_FRONTMATTER,
            f"metadata.timeout_seconds 必須介於 "
            f"{TIMEOUT_SECONDS_MIN}~{TIMEOUT_SECONDS_MAX}",
        )
    fields["timeout_seconds"] = timeout_seconds

    try:
        skill = Skill.model_validate(fields)
    except ValidationError as e:
        raise PackageError.of(INVALID_FRONTMATTER, f"frontmatter 不符合 schema: {e}")
    return skill, license_, compatibility


def _canonical_definition(
    skill: Skill, license_: str | None, compatibility: str | None
) -> str:
    """agentic canonical YAML 投影（§3）：skills-ref-friendly —— 頂層只有標準欄位，引擎專屬
    欄位收進 metadata。round-trip 用同一個 skill_from_agentic_meta 解回。

    以 yaml.safe_dump 寫入 → description 等 scalar 一律 YAML-safe escape。timeout_seconds／
    input_schema 未設則不寫。metadata 值一律字串（string→string map）。
    """
    if skill.timeout_seconds is None:
        raise ValueError("agentic canonical definition 需要 timeout_seconds")
    metadata: dict = {
        "kind": "agentic",
        "required_role": skill.required_role,
        "timeout_seconds": str(skill.timeout_seconds),
        "input_schema": _input_schema_json(skill),
    }

    data: dict = {"name": skill.name, "description": skill.description}
    if license_ is not None:
        data["license"] = license_
    if compatibility is not None:
        data["compatibility"] = compatibility
    if skill.uses_tools:
        data["allowed-tools"] = " ".join(skill.uses_tools)
    data["metadata"] = metadata
    return yaml.safe_dump(data, sort_keys=False, allow_unicode=True)


def _scan_package_files(
    entries: Mapping[str, bytes],
) -> tuple[dict[str, str], dict[str, bytes]]:
    """掃描 package 內所有 scripts/*.py 並分出不可執行 scripts 與唯讀 resources。

    flow 與 agentic 共用這道安全閘門；任何 package 種類都不能靠分派差異繞過 AST scan。
    """
    scripts: dict[str, str] = {}
    resources: dict[str, bytes] = {}
    for path, data in entries.items():
        if path == SKILL_MD:
            continue
        if path.startswith("scripts/") and path.endswith(".py"):
            try:
                source = data.decode("utf-8")
            except UnicodeDecodeError:
                raise PackageError.of(FORBIDDEN_SCRIPT, f"script {path!r} 不是合法 UTF-8")
            try:
                script_runner.scan(source)
            except script_runner.ScriptViolation as e:
                raise PackageError.of(FORBIDDEN_SCRIPT, f"script {path!r}: {e}")
            scripts[path] = source
        else:
            # 標準允許任意額外檔案；scripts/README.md 等非 Python 檔是唯讀 resource，
            # 不解讀、不執行，但必須保留 bytes 供 package round-trip。
            resources[path] = data
    return scripts, resources


def _parse_agentic(
    entries: dict[str, bytes],
    expected_name: str | None,
    sha256: str,
    meta: dict,
    body: str,
    scripts: Mapping[str, str],
    resources: Mapping[str, bytes],
) -> ValidatedPackage:
    # 標準 frontmatter 是 metadata 唯一來源：頂層標準欄位 + metadata（引擎專屬）。
    skill, license_, compatibility = skill_from_agentic_meta(meta)

    if expected_name is not None and skill.name != expected_name:
        raise PackageError.of(
            NAME_MISMATCH,
            f"frontmatter name '{skill.name}' 不等於匯入路徑名稱 '{expected_name}'",
        )

    # allowed-tools 逐一必須在既有 registry（重用既有 unknown_tool 檢查，不重實作）
    for name in skill.uses_tools:
        if tool_registry.get(name) is None:
            raise PackageError.of(UNKNOWN_TOOL, f"frontmatter allowed-tools 含未註冊的 tool: {name}")

    pkg = AgentSkillPackage(
        skill=skill,
        instruction=body,
        resources=resources,
        scripts=scripts,
        sha256=sha256,
    )
    return ValidatedPackage(
        kind="agentic",
        skill=skill,
        canonical_definition=_canonical_definition(skill, license_, compatibility),
        entries=tuple(sorted(entries)),
        sha256=sha256,
        agentic=pkg,
    )


def _parse_flow(
    entries: dict[str, bytes],
    expected_name: str | None,
    sha256: str,
    meta: Mapping[str, Any],
    body: str,
) -> ValidatedPackage:
    """flow package（§3.1）：單一自包含 SKILL.md —— 定義嵌在 body 的第一個 ```yaml 區塊。

    抽出的逐字文字即 definition（等同舊 skill.yaml bytes 的角色，byte-preserving），交既有
    YAML validator 驗證；definition name 必須等於 frontmatter name，若提供 expected_name
    則三者皆須相同。不再需要獨立 skill.yaml（§6 clean break）。
    """
    frontmatter_name, frontmatter_description = _validate_standard_frontmatter(meta)
    if expected_name is not None and frontmatter_name != expected_name:
        raise PackageError.of(
            NAME_MISMATCH,
            f"frontmatter name '{frontmatter_name}' 不等於匯入路徑名稱 '{expected_name}'",
        )

    definition = _extract_flow_definition(body)
    if definition is None:
        raise PackageError.of(
            INVALID_FRONTMATTER,
            "flow package 的 SKILL.md body 缺少 ```yaml 定義區塊",
        )

    # 既有 YAML validator（不另做一套）；抽出的原文作 definition，不重新序列化
    result = validate_source(definition)
    if not result.valid:
        raise PackageError(list(result.errors))
    skill = parse_source(definition)
    if skill.name != frontmatter_name:
        raise PackageError.of(
            NAME_MISMATCH,
            f"flow 定義 name '{skill.name}' 不等於 frontmatter name "
            f"'{frontmatter_name}'",
        )
    if skill.description != frontmatter_description:
        raise PackageError.of(
            INVALID_FRONTMATTER,
            "flow frontmatter description 必須與內嵌定義 description 相同",
        )
    return ValidatedPackage(
        kind="flow",
        skill=skill,
        canonical_definition=definition,  # 抽出的原文，byte-preserving
        entries=tuple(sorted(entries)),
        sha256=sha256,
        agentic=None,
    )


def parse_package(
    raw: bytes, expected_name: str | None = None, limits: PackageLimits = LIMITS
) -> ValidatedPackage:
    """解析上傳的 package zip → ValidatedPackage；任何違規 raise PackageError（零副作用）。

    單一 SKILL.md（root 必要）：frontmatter `metadata.kind: agentic` → agentic；否則 → flow
    （定義嵌在 body 的 ```yaml 區塊，§3.1）。expected_name 是 optional transport guard：
    None 時由 SKILL.md 唯一決定名稱；有值時嚴格核對。sha256 為原始 zip bytes 的雜湊。
    """
    sha256 = hashlib.sha256(raw).hexdigest()
    entries = _read_entries(raw, limits)
    if SKILL_MD not in entries:
        raise PackageError.of(MISSING_SKILL_MD, f"package 缺少根目錄 {SKILL_MD}")

    try:
        text = entries[SKILL_MD].decode("utf-8")
    except UnicodeDecodeError:
        raise PackageError.of(INVALID_FRONTMATTER, "SKILL.md 不是合法的 UTF-8 文字")
    fm_text, body = _split_frontmatter(text)
    try:
        meta = yaml.safe_load(fm_text)
    except yaml.YAMLError as e:
        raise PackageError.of(INVALID_FRONTMATTER, f"frontmatter YAML 解析失敗: {e}")
    if not isinstance(meta, dict):
        raise PackageError.of(INVALID_FRONTMATTER, "frontmatter 必須是 YAML 對應（mapping）")

    # 所有 package 種類共用 scripts AST 安全閘門；掃描只解析、絕不執行。
    scripts, resources = _scan_package_files(entries)

    # 分派：frontmatter metadata.kind == agentic → agentic；否則 flow（body 內嵌 ```yaml 定義）。
    metadata = meta.get("metadata")
    if isinstance(metadata, dict) and metadata.get("kind") == "agentic":
        return _parse_agentic(
            entries, expected_name, sha256, meta, body, scripts, resources
        )
    return _parse_flow(entries, expected_name, sha256, meta, body)
