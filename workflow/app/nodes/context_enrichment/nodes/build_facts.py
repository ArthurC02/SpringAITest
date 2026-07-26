from app.engine.node_registry import node


@node(
    name="context_build_facts", version="1.0", reads=["evidence", "conflicts"], writes=["facts"],
    description="Create only evidence-indexed document facts; E1 contains no business calculations.",
)
def make_build_facts_node():
    async def build_facts(state: dict) -> dict:
        conflicts = {item["content_ref"] for item in state.get("conflicts", []) if isinstance(item, dict) and isinstance(item.get("content_ref"), str)}
        facts = [
            {"fact_id": f"document:{item['document_id']}:{item['chunk_id']}", "evidence_refs": [item["content_ref"]], "conflicted": item["content_ref"] in conflicts}
            for item in state.get("evidence", [])
            if isinstance(item, dict) and item.get("document_id") and item.get("chunk_id") and item.get("content_ref")
        ]
        return {"facts": facts}

    return build_facts
