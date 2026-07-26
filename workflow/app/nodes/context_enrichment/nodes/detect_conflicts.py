from app.engine.node_registry import node


@node(
    name="context_detect_conflicts", version="1.0", reads=["evidence"], writes=["conflicts"],
    description="Expose conflicting traceable evidence rather than silently choosing a source.",
)
def make_detect_conflicts_node():
    async def detect_conflicts(state: dict) -> dict:
        by_ref: dict[str, set[str]] = {}
        for item in state.get("evidence", []):
            if isinstance(item, dict):
                by_ref.setdefault(str(item.get("content_ref", "")), set()).add(str(item.get("content_hash", "")))
        return {"conflicts": [{"content_ref": ref, "content_hashes": sorted(hashes)} for ref, hashes in by_ref.items() if ref and len(hashes) > 1]}

    return detect_conflicts
