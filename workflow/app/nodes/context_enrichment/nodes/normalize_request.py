from app.engine.node_registry import node


@node(
    name="context_normalize_request", version="1.0", reads=["validated_job"],
    writes=["normalized_request"], description="Normalize only the request transport shape; it creates no IDs or policy.",
)
def make_normalize_request_node():
    async def normalize_request(state: dict) -> dict:
        job = state["validated_job"]
        normalized = {"query": job["message"].strip()}
        if isinstance(job.get("trusted_user_input"), str) and job["trusted_user_input"].strip():
            normalized["trusted_user_input"] = job["trusted_user_input"].strip()
        return {"normalized_request": normalized}

    return normalize_request
