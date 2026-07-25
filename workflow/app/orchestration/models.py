"""Wire models for the constrained Workflow Designer Graph IR."""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, ConfigDict, Field


class GraphDiagnostic(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    path: str
    code: str
    message: str
    node_id: str | None = Field(default=None, alias="nodeId")
    edge_id: str | None = Field(default=None, alias="edgeId")


class GraphValidationRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="forbid")

    definition: Any
    ui_metadata: Any = Field(default_factory=dict, alias="uiMetadata")


class GraphValidationResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    valid: bool
    canonical_definition: dict[str, Any] | None = Field(
        default=None, alias="canonicalDefinition"
    )
    canonical_ui_metadata: Any | None = Field(
        default=None, alias="canonicalUiMetadata"
    )
    definition_sha256: str | None = Field(default=None, alias="definitionSha256")
    ui_metadata_sha256: str | None = Field(default=None, alias="uiMetadataSha256")
    compiler_contract_version: str = Field(alias="compilerContractVersion")
    errors: list[GraphDiagnostic] = Field(default_factory=list)


class GraphSimulationResponse(GraphValidationResponse):
    trace: list[dict[str, str]] | None = None
