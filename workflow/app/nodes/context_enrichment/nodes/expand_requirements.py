from app.engine.node_registry import node


@node(
    name="context_expand_requirements", version="1.0", reads=["unmet_requirements", "requirements", "context_attempt"],
    writes=["requirements", "context_attempt"], description="Carry Backend-reported unmet requirements into the next YAML loop round.",
)
def make_expand_requirements_node():
    async def expand_requirements(state: dict) -> dict:
        unmet = state.get("unmet_requirements", [])
        if not isinstance(unmet, list):
            raise ValueError("Backend unmet requirements are invalid")
        # The requirement list keeps its Backend-issued shape across rounds: a
        # round only flags which entries Backend still reports as unmet, so the
        # next retrieval widens the query instead of replacing the template.
        names = {str(item) for item in unmet}
        requirements = [
            {**item, "unmet": item.get("name") in names} if isinstance(item, dict) else item
            for item in state["requirements"]
        ]
        return {"requirements": requirements, "context_attempt": state["context_attempt"] + 1}

    return expand_requirements
