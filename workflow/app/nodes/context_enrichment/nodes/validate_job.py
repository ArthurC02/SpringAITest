from app.engine.node_registry import node


@node(
    name="context_validate_job", version="1.0", reads=["job"],
    writes=["validated_job", "remaining_context_rounds", "context_attempt"],
    description="Validate the Backend-issued root context job before any external work.",
)
def make_validate_job_node():
    async def validate_job(state: dict) -> dict:
        job = state.get("job")
        if not isinstance(job, dict):
            raise ValueError("context enrichment requires a job")
        for key in ("context_id", "root_run_id", "message", "observed_at"):
            if not isinstance(job.get(key), str) or not job[key].strip():
                raise ValueError(f"context job is missing {key}")
        context_round = job.get("context_round")
        max_context_rounds = job.get("max_context_rounds")
        if (
            not isinstance(context_round, int)
            or isinstance(context_round, bool)
            or not isinstance(max_context_rounds, int)
            or isinstance(max_context_rounds, bool)
            or context_round < 1
            or context_round > max_context_rounds
        ):
            raise ValueError("context job round is outside the Backend-issued budget")
        deadline = job.get("deadline_monotonic")
        if isinstance(deadline, bool) or not isinstance(deadline, (int, float)) or deadline <= 0:
            raise ValueError("context job is missing its runtime deadline")
        return {
            "validated_job": dict(job),
            "remaining_context_rounds": max_context_rounds - context_round + 1,
            "context_attempt": 0,
        }

    return validate_job
