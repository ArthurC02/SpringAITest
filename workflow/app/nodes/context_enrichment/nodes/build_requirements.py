from app.engine.node_registry import node
from app.nodes.context_enrichment.ports import ContextPolicyPort


@node(
    name="context_build_requirements", version="1.0",
    reads=["normalized_request", "tenant_id", "user_id", "role"], writes=["context_policies", "requirements"],
    deps=["context_policy"], description="Load Backend policy and use its requirement template without local policy defaults.",
)
def make_build_requirements_node(context_policy: ContextPolicyPort):
    async def build_requirements(state: dict) -> dict:
        policy = await context_policy.get_active(tenant_id=state["tenant_id"], user_id=state["user_id"], role=state["role"])
        values = policy.get("values") if isinstance(policy, dict) else None
        if not isinstance(values, dict):
            raise ValueError("context policy values are unavailable")
        requirements = values.get("bootstrap_requirements")
        if not isinstance(requirements, list):
            raise ValueError("context policy bootstrap requirements are unavailable")
        return {"context_policies": policy, "requirements": requirements}

    return build_requirements
