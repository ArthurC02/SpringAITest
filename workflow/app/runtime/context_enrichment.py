"""Root adapter for the server-owned context-enrichment Skill.

It deliberately contains no enrichment decisions: the declarative Skill owns
assembly and Backend owns policy/readiness.  This adapter only bridges the
existing ContextAcquirer seam to the already-compiled Skill and then asks
Backend for the authoritative acquire projection.
"""

from __future__ import annotations

from typing import Any

from app import skills
from app.runtime.orchestrator import ContextAcquisition, RootExecutionSnapshot
from app.runtime.orchestrator_backend import OrchestratorBackendClient
from app.security import RequestContext

# 02-spec §2: these two Backend statuses terminate the run; every other
# not-ready status keeps the existing D5 clarification semantics.
_TERMINAL_STATUS_CODES = {
    "BLOCKED_BY_POLICY": "context-blocked-by-policy",
    "INSUFFICIENT_DATA": "context-insufficient-data",
}


class ContextEnrichmentAcquirer:
    def __init__(self, backend: OrchestratorBackendClient, ctx: RequestContext) -> None:
        self._backend = backend
        self._ctx = ctx

    async def acquire(
        self,
        snapshot: RootExecutionSnapshot,
        current_context: dict[str, Any],
        context_round: int,
    ) -> ContextAcquisition:
        loaded = skills.get("context-enrichment")
        if loaded is None:
            return ContextAcquisition(ready=False, missing=["context-enrichment-unavailable"])
        if snapshot.root_input is None:
            return ContextAcquisition(ready=False, missing=["root-input-unavailable"])
        deadline = current_context.get("__runtime_deadline_monotonic")
        if isinstance(deadline, bool) or not isinstance(deadline, (int, float)) or deadline <= 0:
            return ContextAcquisition(ready=False, missing=["runtime-deadline-unavailable"])
        state = {
            "job": {
                "context_id": snapshot.root_run_id,
                "root_run_id": snapshot.root_run_id,
                "message": snapshot.root_input.message,
                "observed_at": snapshot.root_input.observed_at,
                "context_round": context_round,
                "max_context_rounds": snapshot.limits.max_context_rounds,
                "deadline_monotonic": deadline,
            },
            "tenant_id": self._ctx.tenant_id,
            "user_id": self._ctx.user_id,
            "role": self._ctx.role,
            "snapshot_authority": snapshot.authority.model_dump(mode="json"),
        }
        if isinstance(current_context.get("user_input"), str) and current_context["user_input"].strip():
            state["job"]["trusted_user_input"] = current_context["user_input"].strip()
        output = await loaded.graph.ainvoke(
            state, config={"recursion_limit": loaded.recursion_limit}
        )
        fatal = output.get("fatal_error")
        if fatal:
            code = (
                "context-policy-unavailable"
                if str(fatal).startswith("context_build_requirements")
                else "context-enrichment-unavailable"
            )
            return ContextAcquisition(ready=False, missing=[code])
        # Backend remains the sole source for a ready ContextAcquisition and
        # its provenance.  The Skill's context_ref is only an internal proof
        # that the candidate was accepted by Backend policy.
        status = output.get("context_status")
        if status not in {"READY", "READY_WITH_ASSUMPTIONS"}:
            # Backend distinguishes "ask again" from "stop": a policy block or a
            # declared data insufficiency must not consume another clarification
            # round the user could never satisfy.
            terminal = _TERMINAL_STATUS_CODES.get(status)
            if terminal is not None:
                return ContextAcquisition(ready=False, terminal=True, missing=[terminal])
            unmet = output.get("unmet_requirements")
            return ContextAcquisition(
                ready=False,
                missing=[str(item) for item in unmet] if isinstance(unmet, list) and unmet else ["context-insufficient"],
            )
        # The clarification was consumed into the new immutable revision. Do
        # not resend raw user_input: Backend must project the stored revision.
        projected_context = dict(current_context)
        projected_context.pop("user_input", None)
        projected_context.pop("__runtime_deadline_monotonic", None)
        return await self._backend.acquire_context(
            snapshot, projected_context, context_round, self._ctx
        )
