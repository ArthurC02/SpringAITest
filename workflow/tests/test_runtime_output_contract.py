from __future__ import annotations

import json
from pathlib import Path

import pytest

from app.runtime.output_contract import output_matches, validate_output_contract


VECTORS = json.loads(
    (Path(__file__).parent / "fixtures" / "output_contract_v1_vectors.json").read_text(
        encoding="utf-8"
    )
)


@pytest.mark.parametrize("contract", VECTORS["valid"])
def test_shared_valid_contract_vectors(contract) -> None:
    validate_output_contract(contract)


@pytest.mark.parametrize("contract", VECTORS["invalid"])
def test_shared_invalid_contract_vectors(contract) -> None:
    with pytest.raises(ValueError):
        validate_output_contract(contract)


def test_recursive_runtime_validation_is_strict() -> None:
    contract = VECTORS["valid"][2]
    assert output_matches(contract, '{"name":"n","items":[{"ok":true}]}')
    assert not output_matches(contract, '{"name":"n","extra":1,"items":[]}')
    assert not output_matches(contract, '{"name":"n","items":[{"ok":1}]}')


def test_scalar_root_and_json_type_enum_identity() -> None:
    assert output_matches({"type": "integer", "enum": [1]}, "1.0")
    assert not output_matches({"type": "integer", "enum": [1]}, "true")
    assert not output_matches({"type": "number"}, "NaN")
