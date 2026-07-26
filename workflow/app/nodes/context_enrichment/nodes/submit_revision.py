import time

from app.engine.node_registry import node
from app.nodes.context_enrichment.models import ContextReference
from app.nodes.context_enrichment.ports import ContextStorePort
from app.security import RequestContext


@node(
    name="context_submit_revision", version="1.0",
    reads=["validated_job", "tenant_id", "user_id", "role", "views", "view_gaps", "evidence", "requirements", "facts", "conflicts", "coverage", "assumptions", "time_frame", "retrieval_gaps", "retrieval_measurements", "context_attempt", "remaining_context_rounds"],
    writes=["context_status", "unmet_requirements", "context_ref"], deps=["context_store"],
    description="Submit the candidate artifact to Backend, which is the sole readiness authority.",
)
def make_submit_revision_node(context_store: ContextStorePort):
    async def submit_revision(state: dict) -> dict:
        candidate = {
            "root_run_id": state["validated_job"]["root_run_id"],
            "definition": {
                "requirements": state["requirements"], "facts": state["facts"],
                "conflicts": state["conflicts"], "time_frame": state["time_frame"],
                "assumptions": state["assumptions"],
                "covered_requirements": state["coverage"]["covered_requirements"],
            },
            "evidence": [{
                "evidence_type": item["evidence_type"], "source_id": item["source_id"],
                "snapshot_id": item["snapshot_id"], "content_ref": item["content_ref"],
                "content_hash": item["content_hash"], "scope": {},
                "observations": item["observations"], "observed_at": item["observed_at"],
                "lineage": item["lineage"],
            } for item in state["evidence"] if isinstance(item, dict)],
            "views": [{"view_type": name, "definition": definition} for name, definition in state["views"].items()],
            "measurements": {
                "retrieval_gaps": state["retrieval_gaps"],
                "view_gaps": state["view_gaps"],
                "context_round": state["validated_job"]["context_round"],
                "max_context_rounds": state["validated_job"]["max_context_rounds"],
                "critical_ambiguity": bool(state["conflicts"]),
                "deadline_exhausted": time.monotonic() >= state["validated_job"]["deadline_monotonic"],
                "completeness": state["coverage"]["completeness"],
                "assumptions_count": len(state["assumptions"]),
                # Workflow observes, it does not adjudicate: declaring a policy
                # violation is a Backend judgement over the submitted evidence.
                "policy_violations": [],
            },
        }
        result = await context_store.submit_revision(
            context_id=state["validated_job"]["context_id"],
            ctx=RequestContext(
                tenant_id=state["tenant_id"], user_id=state["user_id"], role=state["role"]
            ),
            candidate=candidate,
        )
        status, unmet, ref = result.get("status"), result.get("unmet_requirements", []), result.get("context_ref")
        if not isinstance(status, str) or not isinstance(unmet, list):
            raise ValueError("Backend returned an invalid readiness decision")
        output = {"context_status": status, "unmet_requirements": unmet}
        if status in {"READY", "READY_WITH_ASSUMPTIONS"}:
            output["context_ref"] = ContextReference.model_validate(ref).model_dump(mode="json")
        return output

    return submit_revision
