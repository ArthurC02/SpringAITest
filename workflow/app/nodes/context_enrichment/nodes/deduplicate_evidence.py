from app.engine.node_registry import node


@node(
    name="context_deduplicate_evidence", version="1.0", reads=["evidence_normalized"],
    writes=["evidence"], description="Deduplicate document evidence by its canonical document/chunk/content key.",
)
def make_deduplicate_evidence_node():
    async def deduplicate_evidence(state: dict) -> dict:
        unique: dict[tuple[str, str, str], dict] = {}
        for item in state.get("evidence_normalized", []):
            if not isinstance(item, dict):
                continue
            key = (str(item.get("document_id", "")), str(item.get("chunk_id", "")), str(item.get("content_hash", "")))
            if all(key):
                unique.setdefault(key, item)
        return {"evidence": list(unique.values())}

    return deduplicate_evidence
