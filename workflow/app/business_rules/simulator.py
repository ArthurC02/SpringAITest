"""Side-effect-free simulator that is only a thin wrapper around evaluator."""

from __future__ import annotations

from typing import Any

from app.business_rules.evaluator import evaluate
from app.business_rules.models import CanonicalRuleSet


def simulate(
    gate: str, rule_set: CanonicalRuleSet, facts: dict[str, Any]
) -> dict[str, Any]:
    """Run the production evaluator; never invokes tools, network, files, or DB."""
    return evaluate(gate, rule_set, facts)
