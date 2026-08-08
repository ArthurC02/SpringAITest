"""Internal D4 Graph IR catalogue, validation, and safe simulation endpoints."""

from __future__ import annotations

from fastapi import APIRouter, Depends

from app.orchestration.catalog import CATALOG_VERSION, COMPILER_CONTRACT_VERSION, all_wire
from app.orchestration.models import (
    GraphSimulationResponse,
    GraphValidationRequest,
    GraphValidationResponse,
)
from app.orchestration.simulator import simulated_trace
from app.orchestration.validator import ValidationResult, validate
from app.security import RequestContext, get_context

router = APIRouter(prefix="/workflow-designer", tags=["workflow-designer"])


def _response(result: ValidationResult) -> GraphValidationResponse:
    return GraphValidationResponse(
        valid=result.valid,
        canonical_definition=result.canonical_definition,
        canonical_ui_metadata=result.canonical_ui_metadata,
        definition_sha256=result.definition_sha256,
        ui_metadata_sha256=result.ui_metadata_sha256,
        compiler_contract_version=COMPILER_CONTRACT_VERSION,
        errors=result.errors,
    )


@router.get("/catalog/nodes")
async def catalog_nodes(ctx: RequestContext = Depends(get_context)) -> dict:
    return {
        "catalogVersion": CATALOG_VERSION,
        "compilerContractVersion": COMPILER_CONTRACT_VERSION,
        "nodes": all_wire(),
    }


@router.post("/validate", response_model=GraphValidationResponse)
async def validate_graph(
    request: GraphValidationRequest,
    ctx: RequestContext = Depends(get_context),
) -> GraphValidationResponse:
    return _response(validate(request.definition, request.ui_metadata))


@router.post("/simulate", response_model=GraphSimulationResponse)
async def simulate_graph(
    request: GraphValidationRequest,
    ctx: RequestContext = Depends(get_context),
) -> GraphSimulationResponse:
    result = validate(request.definition, request.ui_metadata)
    base = _response(result)
    return GraphSimulationResponse(
        **base.model_dump(),
        trace=simulated_trace(result.canonical_definition) if result.valid else None,
    )
