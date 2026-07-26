"""Fixed, auditable value shapes used by the context-enrichment nodes."""

from __future__ import annotations

from pydantic import BaseModel, ConfigDict, Field


class _StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class ContextEvidenceObservations(_StrictModel):
    """Only per-record measurements live here.

    Requirement coverage is a context-level ratio and is submitted as a
    measurement instead; Backend derives readiness from its own policy
    coverage and never reads a caller-claimed completeness.
    """

    retrieved_at: str = Field(min_length=1, max_length=128)
    source_authority_class: str = Field(min_length=1, max_length=128)
    catalog_source_id: str = Field(min_length=1, max_length=256)
    adapter_id: str = Field(min_length=1, max_length=256)


class ContextEvidenceLineage(_StrictModel):
    document_id: str = Field(min_length=1, max_length=256)
    chunk_id: str = Field(min_length=1, max_length=256)
    catalog_source_id: str = Field(min_length=1, max_length=256)
    adapter_id: str = Field(min_length=1, max_length=256)


class ContextEvidence(_StrictModel):
    source_id: str = Field(min_length=1, max_length=256)
    document_id: str = Field(min_length=1, max_length=256)
    chunk_id: str = Field(min_length=1, max_length=256)
    content_ref: str = Field(min_length=1, max_length=1_024)
    content_hash: str = Field(pattern=r"^[0-9a-f]{64}$")
    content: str = Field(min_length=1, max_length=65_536)
    snapshot_id: str = Field(min_length=1, max_length=256)
    observed_at: str = Field(min_length=1, max_length=128)
    observations: ContextEvidenceObservations
    lineage: ContextEvidenceLineage


class ContextReference(_StrictModel):
    context_id: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=1)
    view_id: str = Field(min_length=1, max_length=128)
