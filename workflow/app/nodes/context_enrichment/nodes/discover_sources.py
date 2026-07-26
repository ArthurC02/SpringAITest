from app.engine.node_registry import node


@node(
    name="context_discover_sources", version="1.0", reads=["context_policies", "security_scope"],
    writes=["candidate_sources"], description="Intersect Backend source catalog with the snapshot authority.",
)
def make_discover_sources_node():
    async def discover_sources(state: dict) -> dict:
        policy = state["context_policies"]
        catalog = policy.get("sources", []) if isinstance(policy, dict) else []
        if not isinstance(catalog, list):
            raise ValueError("context source catalog is invalid")
        values = policy.get("values") if isinstance(policy, dict) else None
        precedence = values.get("source_precedence") if isinstance(values, dict) else None
        if (
            not isinstance(precedence, list) or not precedence
            or any(not isinstance(item, str) or not item for item in precedence)
            or len(set(precedence)) != len(precedence)
        ):
            raise ValueError("context source precedence is invalid")
        catalog_by_id = {}
        for item in catalog:
            source_id = item.get("source_id") if isinstance(item, dict) else None
            if not isinstance(source_id, str) or not source_id or source_id in catalog_by_id:
                raise ValueError("context source catalog is invalid")
            catalog_by_id[source_id] = item
        if any(source_id not in catalog_by_id for source_id in precedence):
            raise ValueError("context source precedence references an unknown source")
        allowed_tools = set(state["security_scope"]["context_tools"])
        # Backend selects and pins the authoritative source; discovery only
        # reports the whole authorized intersection, ordered by precedence so
        # the candidate list stays deterministic.
        rank = {source_id: index for index, source_id in enumerate(precedence)}
        authorized = [
            item for item in catalog_by_id.values()
            if item.get("enabled") is True and item.get("adapter_id") in allowed_tools
        ]
        if not authorized:
            raise ValueError("context source precedence has no authorized enabled source")
        return {"candidate_sources": sorted(
            authorized, key=lambda item: rank.get(item["source_id"], len(rank))
        )}

    return discover_sources
