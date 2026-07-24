"""Stable HTTP and runtime DTOs for Business Rules."""

from __future__ import annotations

from enum import Enum
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field


class RuleValidationRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="forbid")

    gate: str
    rule_set: Any = Field(alias="ruleSet")
    reference_catalog: "RuleReferenceCatalog | None" = Field(
        default=None, alias="referenceCatalog"
    )


class RuleSimulationRequest(RuleValidationRequest):
    facts: dict[str, Any]


class RuleReferenceCatalog(BaseModel):
    """Optional tenant/runtime references used for publish-time validation.

    A missing category means that its owning service did not supply a catalog,
    preserving compatibility with existing callers.  An explicitly empty
    category means no references of that kind are allowed.
    """

    model_config = ConfigDict(extra="forbid")

    skills: list[str] | None = None
    tools: list[str] | None = None
    roles: list[str] | None = None
    facts: list[str] | None = None


class RuleError(BaseModel):
    path: str
    code: str
    message: str


class RuleValidationResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    valid: bool
    canonical_rule_set: dict[str, Any] | None = Field(
        default=None, alias="canonicalRuleSet"
    )
    errors: list[RuleError] = Field(default_factory=list)


class RuleSimulationResponse(RuleValidationResponse):
    simulation: dict[str, Any] | None = None


class RuleCatalogResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    version: int
    versions: dict[str, int]
    rule_set_version: int = Field(alias="ruleSetVersion")
    decimal_wire_format: dict[str, Any] = Field(alias="decimalWireFormat")
    gates: list[str]
    limits: dict[str, int]
    facts: list[dict[str, Any]]
    operators: list[dict[str, Any]]
    actions: list[dict[str, Any]]


class TruthValue(str, Enum):
    TRUE = "true"
    FALSE = "false"
    UNKNOWN = "unknown"


class LeafCondition(BaseModel):
    model_config = ConfigDict(extra="forbid")

    fact: str
    op: str
    value: Any = None
    has_value: bool = False


class GroupCondition(BaseModel):
    model_config = ConfigDict(extra="forbid")

    kind: Literal["all", "any", "not"]
    conditions: tuple["Condition", ...]


Condition = LeafCondition | GroupCondition


class CanonicalRule(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str
    name: str
    enabled: bool
    priority: int
    when: Condition
    then: tuple[dict[str, Any], ...]
    on_unknown: tuple[dict[str, Any], ...]


class CanonicalRuleSet(BaseModel):
    model_config = ConfigDict(extra="forbid")

    version: Literal[1]
    rules: tuple[CanonicalRule, ...]


class ValidationOutcome(BaseModel):
    valid: bool
    canonical_rule_set: CanonicalRuleSet | None = None
    errors: tuple[RuleError, ...] = ()
