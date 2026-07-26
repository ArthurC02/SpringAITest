import hashlib

from app.engine.node_registry import node
from app.nodes.context_enrichment.models import ContextEvidence


@node(
    name="context_normalize_evidence", version="1.0", reads=["evidence_raw", "validated_job", "security_scope", "candidate_sources"],
    writes=["evidence_normalized"], description="Normalize traceable document chunks; reject records without a server-provided identity.",
)
def make_normalize_evidence_node():
    async def normalize_evidence(state: dict) -> dict:
        allowed = set(state["security_scope"]["knowledge_sources"])
        adapters = {
            item.get("source_id"): item for item in state["candidate_sources"]
            if isinstance(item, dict) and isinstance(item.get("source_id"), str)
        }
        evidence = []
        for raw in state.get("evidence_raw", []):
            if not isinstance(raw, dict):
                continue
            adapter = adapters.get(raw.get("_context_source_id"))
            evidence_type = adapter.get("evidence_type") if isinstance(adapter, dict) else None
            authority_class = adapter.get("authority_class") if isinstance(adapter, dict) else None
            catalog_source_id = adapter.get("source_id") if isinstance(adapter, dict) else None
            adapter_id = adapter.get("adapter_id") if isinstance(adapter, dict) else None
            document_id = raw.get("document_id")
            source_id = document_id
            chunk_id, content = raw.get("chunk_id"), raw.get("content")
            if source_id not in allowed or not isinstance(evidence_type, str) or not evidence_type or not isinstance(authority_class, str) or not authority_class or not all(isinstance(value, str) and value for value in (document_id, chunk_id, content, catalog_source_id, adapter_id)):
                continue
            content_hash = hashlib.sha256(content.encode("utf-8")).hexdigest()
            value = ContextEvidence(
                source_id=source_id, document_id=document_id, chunk_id=chunk_id,
                content_ref=f"document://{document_id}#chunk/{chunk_id}", content_hash=content_hash,
                content=content, snapshot_id=state["validated_job"]["root_run_id"],
                observed_at=state["validated_job"]["observed_at"],
                observations={
                    "retrieved_at": state["validated_job"]["observed_at"],
                    "source_authority_class": authority_class,
                    "catalog_source_id": catalog_source_id,
                    "adapter_id": adapter_id,
                },
                lineage={
                    "document_id": document_id, "chunk_id": chunk_id,
                    "catalog_source_id": catalog_source_id, "adapter_id": adapter_id,
                },
            )
            evidence.append(value.model_dump(mode="json") | {"evidence_type": evidence_type})
        return {"evidence_normalized": evidence}

    return normalize_evidence
