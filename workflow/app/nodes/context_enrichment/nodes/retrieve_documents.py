import time

from app.engine.node_registry import node
from app.nodes.context_enrichment.ports import ContextRetrievalPort


@node(
    name="context_retrieve_documents", version="1.0",
    reads=["normalized_request", "candidate_sources", "requirements", "security_scope", "tenant_id", "validated_job"],
    writes=["evidence_raw", "retrieval_gaps", "retrieval_measurements"], appends=["evidence_raw"],
    deps=["context_retrieval"], description="Retrieve only through Backend's scoped document API.",
)
def make_retrieve_documents_node(context_retrieval: ContextRetrievalPort):
    async def retrieve_documents(state: dict) -> dict:
        knowledge_sources = state["security_scope"]["knowledge_sources"]
        if not knowledge_sources:
            return {"evidence_raw": [], "retrieval_gaps": [], "retrieval_measurements": {"attempted": 0, "failed": 0, "optional_skipped": 0}}
        evidence, gaps = [], []
        attempted = failed = optional_skipped = 0
        deadline = float(state["validated_job"]["deadline_monotonic"])
        # An expansion round must actually widen the search, otherwise the
        # bounded loop re-submits identical evidence until it runs out.
        unmet = sorted({
            item["name"] for item in state.get("requirements", [])
            if isinstance(item, dict) and item.get("unmet") is True
            and isinstance(item.get("name"), str) and item["name"]
        })
        query = " ".join([state["normalized_request"]["query"], *unmet])
        for source in state["candidate_sources"]:
            required = source.get("required")
            timeout = source.get("timeout_seconds")
            minimum = source.get("minimum_deadline_seconds")
            adapter_id = source.get("adapter_id")
            if (
                not isinstance(required, bool)
                or isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or timeout <= 0
                or isinstance(minimum, bool) or not isinstance(minimum, (int, float)) or minimum < 0
                or not isinstance(adapter_id, str) or not adapter_id
            ):
                raise ValueError("context source policy is invalid")
            remaining = max(0.0, deadline - time.monotonic())
            if not required and remaining <= float(minimum):
                optional_skipped += 1
                gaps.append({"source_id": source["source_id"], "failure_code": "deadline"})
                continue
            if remaining <= 0:
                failed += 1
                gaps.append({"source_id": source["source_id"], "failure_code": "deadline"})
                continue
            attempted += 1
            try:
                chunks = await context_retrieval.retrieve(
                    query=query, tenant_id=state["tenant_id"],
                    knowledge_sources=knowledge_sources, adapter_id=adapter_id,
                    timeout_seconds=min(float(timeout), remaining),
                )
                evidence.extend(
                    item | {"_context_source_id": source["source_id"]}
                    for item in chunks if isinstance(item, dict)
                )
            except TimeoutError:
                failed += 1
                gaps.append({"source_id": source["source_id"], "failure_code": "timeout"})
            except Exception:
                failed += 1
                gaps.append({"source_id": source["source_id"], "failure_code": "failure"})
        return {
            "evidence_raw": evidence,
            "retrieval_gaps": gaps,
            "retrieval_measurements": {"attempted": attempted, "failed": failed, "optional_skipped": optional_skipped},
        }

    return retrieve_documents
