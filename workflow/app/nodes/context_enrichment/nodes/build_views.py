from app.engine.node_registry import node
from app.runtime.orchestrator import MAX_TASK_CONTEXT_BYTES


@node(
    name="context_build_views", version="1.0", reads=["facts", "evidence", "conflicts", "requirements", "assumptions", "normalized_request"],
    writes=["views", "view_gaps"], description="Create minimal role projections with document text explicitly marked as untrusted evidence.",
)
def make_build_views_node():
    async def build_views(state: dict) -> dict:
        safe_evidence: list[dict] = []
        view_gaps: list[dict] = []
        used = 0
        # The projection must stay inside the transport bound Backend already
        # enforces; an oversized record is dropped with an explicit gap rather
        # than silently truncated into unciteable text.
        for item in state.get("evidence", []):
            if not isinstance(item, dict):
                continue
            size = len(item["content"].encode("utf-8"))
            if used + size > MAX_TASK_CONTEXT_BYTES:
                view_gaps.append({"content_ref": item["content_ref"], "gap": "evidence-oversized-degraded"})
                continue
            used += size
            safe_evidence.append({"content_ref": item["content_ref"], "source_id": item["source_id"], "content_hash": item["content_hash"], "untrusted_content": item["content"]})
        task = {"query": state["normalized_request"]["query"]}
        if "trusted_user_input" in state["normalized_request"]:
            task["trusted_user_input"] = state["normalized_request"]["trusted_user_input"]
        shared = {"requirements": state.get("requirements", []), "facts": state.get("facts", []), "conflicts": state.get("conflicts", [])}
        gaps = {
            "conflicts": shared["conflicts"],
            "assumptions": state.get("assumptions", []),
            "gaps": view_gaps,
        }

        def sections(*, include_requirements: bool, include_evidence: bool) -> dict:
            return {
                "[SYSTEM_POLICY]": {},
                "[TASK]": task,
                "[DOMAIN_DEFINITIONS]": {"requirements": shared["requirements"]} if include_requirements else {},
                "[STRUCTURED_FACTS]": shared["facts"],
                "[UNTRUSTED_EVIDENCE]": safe_evidence if include_evidence else [],
                "[CONFLICTS_AND_GAPS]": gaps,
                "[OUTPUT_SCHEMA]": {},
            }

        return {"view_gaps": view_gaps, "views": {
            "planner": {"prompt_sections": sections(include_requirements=True, include_evidence=True)},
            "worker": {"prompt_sections": sections(include_requirements=False, include_evidence=True)},
            "verifier": {"prompt_sections": sections(include_requirements=True, include_evidence=True)},
            "synthesizer": {"prompt_sections": sections(include_requirements=False, include_evidence=False)},
        }}

    return build_views
