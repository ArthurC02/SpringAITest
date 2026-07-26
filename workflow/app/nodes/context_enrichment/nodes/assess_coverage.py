from app.engine.node_registry import node


@node(
    name="context_assess_coverage", version="1.0",
    reads=["requirements", "evidence", "retrieval_gaps", "candidate_sources"],
    writes=["coverage", "assumptions"],
    description="Measure requirement coverage and turn every optional gap into an explicit assumption.",
)
def make_assess_coverage_node():
    async def assess_coverage(state: dict) -> dict:
        covered = sorted({
            item["evidence_type"] for item in state.get("evidence", [])
            if isinstance(item, dict) and isinstance(item.get("evidence_type"), str)
        })
        requirements = [
            item for item in state.get("requirements", [])
            if isinstance(item, dict) and isinstance(item.get("name"), str) and item["name"]
        ]
        optional_sources = {
            item["source_id"] for item in state.get("candidate_sources", [])
            if isinstance(item, dict) and item.get("required") is False
            and isinstance(item.get("source_id"), str)
        }
        # An assumption is a mechanical restatement of a gap the Backend policy
        # marked optional; deciding whether it is acceptable stays in Backend.
        assumptions = [
            {
                "kind": "optional-source-unavailable",
                "source_id": gap["source_id"],
                "failure_code": gap.get("failure_code"),
            }
            for gap in state.get("retrieval_gaps", [])
            if isinstance(gap, dict) and gap.get("source_id") in optional_sources
        ]
        assumptions += [
            {"kind": "optional-requirement-uncovered", "requirement": item["name"]}
            for item in requirements
            if item.get("mandatory") is not True and item["name"] not in covered
        ]
        satisfied = [item for item in requirements if item["name"] in covered]
        return {
            "coverage": {
                "covered_requirements": covered,
                # Objective ratio, not a verdict: Backend still owns every
                # threshold applied to it.
                "completeness": len(satisfied) / len(requirements) if requirements else 1.0,
            },
            "assumptions": assumptions,
        }

    return assess_coverage
