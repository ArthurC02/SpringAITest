from app.engine.node_registry import node


@node(
    name="context_resolve_time", version="1.0", reads=["validated_job", "normalized_request"],
    writes=["time_frame"], description="Carry the server observation time; Backend owns relative-period semantics.",
)
def make_resolve_time_node():
    async def resolve_time(state: dict) -> dict:
        return {"time_frame": {"observed_at": state["validated_job"]["observed_at"]}}

    return resolve_time
