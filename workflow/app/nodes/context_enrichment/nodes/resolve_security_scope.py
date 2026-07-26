from app.engine.node_registry import node


@node(
    name="context_resolve_security_scope", version="1.0",
    reads=["tenant_id", "user_id", "role", "snapshot_authority"], writes=["security_scope"],
    description="Derive the retrieval scope solely from the immutable root authority.",
)
def make_resolve_security_scope_node():
    async def resolve_security_scope(state: dict) -> dict:
        authority = state.get("snapshot_authority")
        if not isinstance(authority, dict):
            raise ValueError("root authority is required")
        sources = authority.get("knowledge_sources")
        tools = authority.get("context_tools")
        if not isinstance(sources, list) or not all(isinstance(item, str) and item for item in sources):
            raise ValueError("root knowledge-source authority is invalid")
        if not isinstance(tools, list) or not all(isinstance(item, str) and item for item in tools):
            raise ValueError("root context-tool authority is invalid")
        for key in ("tenant_id", "user_id", "role"):
            if not isinstance(state.get(key), str) or not state[key].strip():
                raise ValueError(f"trusted identity {key} is required")
        return {"security_scope": {
            "knowledge_sources": list(dict.fromkeys(sources)),
            "context_tools": list(dict.fromkeys(tools)),
        }}

    return resolve_security_scope
