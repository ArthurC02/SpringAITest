"""以固定維度記錄相容介面用量，避免證據本身洩漏租戶或 artifact 內容。"""

import json
import os
from datetime import datetime, timezone
from collections import Counter
from threading import Lock

_ALLOWED = {
    "service": {"workflow"},
    "surface": {
        "public_skills",
        "workflow_validate_alias",
        "workflow_business_workflows_validate",
        "workflow_unified_invoke",
        "unknown_origin",
    },
    "operation": {"validate", "invoke"},
    "resolved_artifact_type": {"agent_skill", "business_workflow", "unknown"},
    "outcome": {"success", "rejected", "not_found", "error"},
}
_counts: Counter[tuple[str, str, str, str, str]] = Counter()
_lock = Lock()


def record(
    *,
    surface: str,
    operation: str,
    resolved_artifact_type: str,
    outcome: str,
) -> None:
    """記一筆有界事件；未知維度值一律拒絕，不能形成高基數標籤。"""
    values = ("workflow", surface, operation, resolved_artifact_type, outcome)
    for dimension, value in zip(_ALLOWED, values, strict=True):
        if value not in _ALLOWED[dimension]:
            raise ValueError(f"unsupported usage evidence {dimension}")
    with _lock:
        _counts[values] += 1
    print(
        json.dumps(
            {
                "schemaVersion": 1,
                "event": "artifact_compatibility_usage_total",
                "timestampUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                "deploymentVersion": os.getenv("DEPLOYMENT_VERSION", "unknown"),
                "service": "workflow",
                "surface": surface,
                "operation": operation,
                "resolvedArtifactType": resolved_artifact_type,
                "outcome": outcome,
                "count": 1,
            },
            separators=(",", ":"),
        ),
        flush=True,
    )


def snapshot() -> dict[tuple[str, str, str, str, str], int]:
    """回傳 process-local 副本，供測試與既有 telemetry adapter 擷取。"""
    with _lock:
        return dict(_counts)


def reset() -> None:
    """只供測試隔離 process-local 計數。"""
    with _lock:
        _counts.clear()
