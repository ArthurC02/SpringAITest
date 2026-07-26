from __future__ import annotations

import json
from pathlib import Path

import pytest

from app.runtime.output_contract import (
    MAX_CONTRACT_DEPTH,
    MAX_CONTRACT_UTF8_BYTES,
    MAX_ENUM_STRING_CHARS,
    MAX_ENUM_VALUES,
    MAX_PROPERTIES,
    MAX_PROPERTY_NAME_CHARS,
    MAX_REQUIRED,
    output_matches,
    validate_output_contract,
)


# 這份向量目前只有 workflow 讀（.NET 端是各自實作），檔名刻意不帶 "shared"／"v1"，
# 免得給人「跨服務一致性已被鎖住」的錯覺。
VECTORS = json.loads(
    (Path(__file__).parent / "fixtures" / "output_contract_vectors.json").read_text(
        encoding="utf-8"
    )
)


@pytest.mark.parametrize("contract", VECTORS["valid"])
def test_valid_contract_vectors(contract) -> None:
    validate_output_contract(contract)


@pytest.mark.parametrize("contract", VECTORS["invalid"])
def test_invalid_contract_vectors(contract) -> None:
    with pytest.raises(ValueError):
        validate_output_contract(contract)


def test_recursive_runtime_validation_is_strict() -> None:
    # 依內容挑向量而非索引：fixture 重排不會讓這個測試靜默改測別的 schema。
    contract = next(item for item in VECTORS["valid"] if item.get("type") == "object")
    assert set(contract["properties"]) == {"name", "items"}
    assert output_matches(contract, '{"name":"n","items":[{"ok":true}]}')
    assert not output_matches(contract, '{"name":"n","extra":1,"items":[]}')
    assert not output_matches(contract, '{"name":"n","items":[{"ok":1}]}')


def test_scalar_root_and_json_type_enum_identity() -> None:
    assert output_matches({"type": "integer", "enum": [1]}, "1.0")
    assert not output_matches({"type": "integer", "enum": [1]}, "true")
    assert not output_matches({"type": "number"}, "NaN")


def test_empty_output_contract_means_string() -> None:
    """D1/D2 已發布 Agent 的相容路徑：空契約＝任意字串輸出。"""
    validate_output_contract({})

    assert output_matches({}, "任意文字") is True
    assert output_matches({}, "") is True
    assert output_matches({}, '{"not":"parsed as json"}') is True


def _canonical_size(contract: dict) -> int:
    return len(
        json.dumps(
            contract,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
            default=str,
        ).encode("utf-8")
    )


def _sized_contract(target_bytes: int) -> dict:
    """組出 canonical bytes 剛好等於 target 的合法契約（用 enum 字串填充）。"""
    values = [
        f"{index:03d}" + "x" * (MAX_ENUM_STRING_CHARS - 3)
        for index in range(MAX_ENUM_VALUES - 1)
    ]
    contract: dict = {"type": "string", "enum": values}
    filler = target_bytes - _canonical_size(contract) - 3
    assert 0 < filler <= MAX_ENUM_STRING_CHARS, filler
    values.append("z" * filler)
    assert _canonical_size(contract) == target_bytes
    return contract


def _nested(levels: int) -> dict:
    contract: dict = {"type": "string"}
    for _ in range(levels - 1):
        contract = {"type": "array", "items": contract}
    return contract


def _fanout(properties: int, children: int, extra_leaves: int = 0) -> dict:
    schema: dict = {
        "type": "object",
        "properties": {
            f"p{index}": {
                "type": "object",
                "properties": {
                    f"c{child}": {"type": "string"} for child in range(children)
                },
            }
            for index in range(properties)
        },
    }
    schema["properties"].update(
        {f"leaf{index}": {"type": "string"} for index in range(extra_leaves)}
    )
    return schema


def _properties(count: int, *, required: int | None = None) -> dict:
    names = [f"p{index:03d}" for index in range(count)]
    schema: dict = {
        "type": "object",
        "properties": {name: {"type": "string"} for name in names},
    }
    if required is not None:
        schema["required"] = (
            names[:required]
            if required <= count
            else names + [f"absent{index}" for index in range(required - count)]
        )
    return schema


_LIMIT_CASES = [
    ("canonical-bytes", _sized_contract(MAX_CONTRACT_UTF8_BYTES), True),
    ("depth", _nested(MAX_CONTRACT_DEPTH), True),
    ("depth", _nested(MAX_CONTRACT_DEPTH + 1), False),
    # 節點數 = 1 + 51*(1+4) = 256；多一個葉屬性就是 257。
    ("nodes", _fanout(51, 4), True),
    ("nodes", _fanout(51, 4, extra_leaves=1), False),
    ("properties", _properties(MAX_PROPERTIES), True),
    ("properties", _properties(MAX_PROPERTIES + 1), False),
    ("required", _properties(MAX_REQUIRED, required=MAX_REQUIRED), True),
    ("required", _properties(MAX_REQUIRED, required=MAX_REQUIRED + 1), False),
    (
        "enum-values",
        {"type": "string", "enum": [f"v{index}" for index in range(MAX_ENUM_VALUES)]},
        True,
    ),
    (
        "enum-values",
        {
            "type": "string",
            "enum": [f"v{index}" for index in range(MAX_ENUM_VALUES + 1)],
        },
        False,
    ),
    (
        "property-name",
        {
            "type": "object",
            "properties": {"n" * MAX_PROPERTY_NAME_CHARS: {"type": "string"}},
        },
        True,
    ),
    (
        "property-name",
        {
            "type": "object",
            "properties": {"n" * (MAX_PROPERTY_NAME_CHARS + 1): {"type": "string"}},
        },
        False,
    ),
    ("enum-string", {"type": "string", "enum": ["e" * MAX_ENUM_STRING_CHARS]}, True),
    (
        "enum-string",
        {"type": "string", "enum": ["e" * (MAX_ENUM_STRING_CHARS + 1)]},
        False,
    ),
]


@pytest.mark.parametrize(
    ("contract", "accepted"),
    [(case[1], case[2]) for case in _LIMIT_CASES],
    ids=[f"{case[0]}-{'at' if case[2] else 'over'}" for case in _LIMIT_CASES],
)
def test_output_contract_structural_limits(contract: dict, accepted: bool) -> None:
    """八個結構上限各測「剛好」與「超一個」——過去全部零覆蓋。"""
    if accepted:
        validate_output_contract(contract)
    else:
        with pytest.raises(ValueError):
            validate_output_contract(contract)


def test_output_contract_rejects_oversized_canonical_bytes() -> None:
    oversized = _sized_contract(MAX_CONTRACT_UTF8_BYTES)
    oversized["enum"][-1] += "z"

    with pytest.raises(ValueError, match="canonical byte limit"):
        validate_output_contract(oversized)
