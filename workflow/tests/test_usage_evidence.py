"""P3-R2 的有界計數、來源映射與 dependency 去重護欄。"""

import json

import pytest
from fastapi.testclient import TestClient

from app import skills, usage_evidence
from app import main as main_module
from app.main import app
from app.skills import config_apply
from tests.conftest import auth_headers, swap_skill

client = TestClient(app)


@pytest.fixture(autouse=True)
def _isolated_counts(monkeypatch):
    async def no_config(_ctx):
        return None, 60.0, None

    monkeypatch.setattr(config_apply, "resolve", no_config)
    usage_evidence.reset()
    yield
    usage_evidence.reset()


def _rows() -> dict[tuple[str, str, str, str, str], int]:
    return usage_evidence.snapshot()


def test_direct_validation_paths_count_separately_but_dependency_does_not_count():
    definition = "name: evidence-probe\nflow:\n  - node: query_intake@1.0\n"

    alias = client.post(
        "/skills/validate", json={"definition": definition}, headers=auth_headers()
    )
    canonical = client.post(
        "/business-workflows/validate",
        json={"definition": definition},
        headers=auth_headers(),
    )
    dependency_headers = {
        **auth_headers(),
        "X-Artifact-Usage-Origin": "dependency",
    }
    dependency = client.post(
        "/skills/validate", json={"definition": definition}, headers=dependency_headers
    )

    assert alias.status_code == canonical.status_code == dependency.status_code == 200
    assert _rows() == {
        ("workflow", "workflow_validate_alias", "validate", "business_workflow", "success"): 1,
        (
            "workflow",
            "workflow_business_workflows_validate",
            "validate",
            "business_workflow",
            "success",
        ): 1,
    }


def test_direct_validation_unexpected_failure_counts_error(monkeypatch):
    def fail_validation(*_args, **_kwargs):
        raise RuntimeError("private validation failure")

    monkeypatch.setattr(main_module, "validate_source", fail_validation)
    response = client.post(
        "/skills/validate",
        json={"definition": "name: evidence-probe\nflow: []\n"},
        headers=auth_headers(),
    )

    assert response.status_code == 500
    assert _rows() == {
        ("workflow", "workflow_validate_alias", "validate", "unknown", "error"): 1
    }


@pytest.mark.parametrize(
    ("path", "surface"),
    [
        ("/skills/validate", "workflow_validate_alias"),
        (
            "/business-workflows/validate",
            "workflow_business_workflows_validate",
        ),
    ],
)
def test_validation_body_422_counts_rejected_once_unless_dependency(path, surface):
    response = client.post(path, json={}, headers=auth_headers())
    dependency = client.post(
        path,
        json={"definition": 1},
        headers={**auth_headers(), "X-Artifact-Usage-Origin": "dependency"},
    )

    assert response.status_code == dependency.status_code == 422
    assert _rows() == {
        ("workflow", surface, "validate", "unknown", "rejected"): 1
    }


@pytest.mark.parametrize(
    ("origin", "surface"),
    [
        ("public_skills", "public_skills"),
        ("workflow_unified_invoke", "workflow_unified_invoke"),
        (None, "unknown_origin"),
        ("caller-controlled-value", "unknown_origin"),
    ],
)
def test_unified_invoke_maps_bounded_origin_once_after_kind_resolution(origin, surface):
    headers = auth_headers()
    if origin is not None:
        headers["X-Artifact-Usage-Origin"] = origin

    response = client.post(
        "/skills/kb-query/invoke", json={"input": {}}, headers=headers
    )

    assert response.status_code == 422
    assert _rows() == {
        ("workflow", surface, "invoke", "business_workflow", "rejected"): 1
    }


@pytest.mark.parametrize("body", [{}, {"input": []}])
def test_invoke_request_body_422_counts_rejected_not_error(body):
    response = client.post(
        "/skills/kb-query/invoke",
        json=body,
        headers={
            **auth_headers(),
            "X-Artifact-Usage-Origin": "caller-controlled-value",
        },
    )

    assert response.status_code == 422
    assert _rows() == {
        ("workflow", "unknown_origin", "invoke", "unknown", "rejected"): 1
    }


def test_unified_invoke_uses_trusted_agent_kind_and_logs_no_request_data(capsys):
    loaded = skills.get("kb-query")
    agentic = loaded.skill.model_copy(update={"kind": "agentic"})
    with swap_skill("__usage-secret__", base=loaded, skill=agentic):
        response = client.post(
            "/skills/__usage-secret__/invoke",
            json={"input": {}},
            headers={
                **auth_headers(tenant_id="secret-tenant"),
                "X-Artifact-Usage-Origin": "public_skills",
            },
        )

    assert response.status_code == 422
    assert _rows() == {
        ("workflow", "public_skills", "invoke", "agent_skill", "rejected"): 1
    }
    output = capsys.readouterr().out
    event = json.loads(output.strip().splitlines()[-1])
    assert event["event"] == "artifact_compatibility_usage_total"
    assert event["resolvedArtifactType"] == "agent_skill"
    assert "__usage-secret__" not in output
    assert "secret-tenant" not in output


def test_unified_invoke_counts_success_and_error_once():
    class Graph:
        def __init__(self, error=False):
            self.error = error

        async def ainvoke(self, _state, config=None):
            if self.error:
                raise RuntimeError("private failure")
            return {"answer": "ok"}

    loaded = skills.get("kb-query")
    agentic = loaded.skill.model_copy(update={"kind": "agentic"})
    headers = {
        **auth_headers(),
        "X-Artifact-Usage-Origin": "workflow_unified_invoke",
    }
    with swap_skill(
        "__usage-success__",
        base=loaded,
        skill=agentic,
        graph=Graph(),
        input_model=None,
    ):
        success = client.post(
            "/skills/__usage-success__/invoke", json={"input": {}}, headers=headers
        )
    with swap_skill(
        "__usage-error__",
        base=loaded,
        skill=agentic,
        graph=Graph(error=True),
        input_model=None,
    ):
        error = client.post(
            "/skills/__usage-error__/invoke", json={"input": {}}, headers=headers
        )

    assert success.status_code == 200
    assert error.status_code == 500
    assert _rows() == {
        ("workflow", "workflow_unified_invoke", "invoke", "agent_skill", "success"): 1,
        ("workflow", "workflow_unified_invoke", "invoke", "agent_skill", "error"): 1,
    }


def test_unknown_artifact_counts_not_found_without_inventing_a_kind():
    response = client.post(
        "/skills/context-enrichment/invoke",
        json={"input": {}},
        headers={
            **auth_headers(),
            "X-Artifact-Usage-Origin": "public_skills",
        },
    )

    assert response.status_code == 404
    assert _rows() == {
        ("workflow", "public_skills", "invoke", "unknown", "not_found"): 1
    }


def test_invoke_rejected_before_context_is_unknown_origin():
    response = client.post(
        "/skills/kb-query/invoke",
        json={"input": {}},
        headers={"X-Artifact-Usage-Origin": "public_skills"},
    )

    assert response.status_code == 401
    assert _rows() == {
        ("workflow", "unknown_origin", "invoke", "unknown", "rejected"): 1
    }


def test_evidence_seam_rejects_unbounded_dimensions():
    with pytest.raises(ValueError, match="unsupported usage evidence surface"):
        usage_evidence.record(
            surface="tenant-derived",
            operation="invoke",
            resolved_artifact_type="business_workflow",
            outcome="success",
        )

    assert _rows() == {}
