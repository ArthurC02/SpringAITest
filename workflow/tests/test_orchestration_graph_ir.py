from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path

from fastapi.testclient import TestClient

from app.main import app
from app.orchestration.validator import validate
from tests.conftest import auth_headers


def agent_runtime_graph() -> dict:
    return {
        "schemaVersion": 1,
        "kind": "agent-runtime",
        "runtimeVariant": "worker",
        "nodes": [
            {"id": "start", "type": "start", "typeVersion": "1.0", "config": {}},
            {"id": "preflight", "type": "dependency_and_capability_preflight", "typeVersion": "1.0", "config": {}},
            {"id": "context", "type": "inject_authorized_context", "typeVersion": "1.0", "config": {}},
            {"id": "checkpoint", "type": "checkpoint", "typeVersion": "1.0", "config": {}},
            {
                "id": "loop", "type": "bounded_agent_loop", "typeVersion": "1.0",
                "config": {"maxIterations": 8},
                "children": [
                    {"id": "model", "type": "model_step", "typeVersion": "1.0", "config": {}},
                    {"id": "gate", "type": "tool_policy_and_approval_gate", "typeVersion": "1.0", "config": {}},
                    {"id": "call", "type": "tool_call_and_observation", "typeVersion": "1.0", "config": {}},
                    {"id": "budget", "type": "checkpoint_and_budget_gate", "typeVersion": "1.0", "config": {}},
                ],
            },
            {"id": "output", "type": "validate_structured_output", "typeVersion": "1.0", "config": {}},
            {"id": "repair", "type": "bounded_repair_or_controlled_failure", "typeVersion": "1.0", "config": {"maxRepairRounds": 2}},
            {"id": "end", "type": "end", "typeVersion": "1.0", "config": {}},
        ],
        "edges": [
            {"id": "e0", "source": {"nodeId": "start", "port": "out"}, "target": {"nodeId": "preflight", "port": "in"}},
            {"id": "e1", "source": {"nodeId": "preflight", "port": "out"}, "target": {"nodeId": "context", "port": "in"}},
            {"id": "e2", "source": {"nodeId": "context", "port": "out"}, "target": {"nodeId": "checkpoint", "port": "in"}},
            {"id": "e3", "source": {"nodeId": "checkpoint", "port": "out"}, "target": {"nodeId": "loop", "port": "in"}},
            {"id": "e4", "source": {"nodeId": "loop", "port": "out"}, "target": {"nodeId": "output", "port": "in"}},
            {"id": "e5", "source": {"nodeId": "output", "port": "out"}, "target": {"nodeId": "repair", "port": "in"}},
            {"id": "e6", "source": {"nodeId": "repair", "port": "out"}, "target": {"nodeId": "end", "port": "in"}},
        ],
        "governance": {"maxSteps": 40, "maxConcurrency": 1},
    }


def orchestrator_graph() -> dict:
    stages = [
        ("start", "start", {}),
        ("context", "acquire_context_and_analyze_problem", {}),
        ("sufficiency", "sufficiency_gate", {}),
        ("decompose", "decompose_work", {}),
        ("dispatch", "dispatch_agents", {}),
        ("join", "join_worker_results", {}),
        ("verify", "invoke_verifier", {}),
        ("repair", "bounded_repair", {"maxIterations": 2}),
        ("aggregate", "aggregate_results", {}),
        ("respond", "respond", {}),
        ("audit", "audit", {}),
        ("end", "end", {}),
    ]
    return {
        "schemaVersion": 1,
        "kind": "orchestrator",
        "nodes": [{"id": node_id, "type": node_type, "typeVersion": "1.0", "config": config} for node_id, node_type, config in stages],
        "edges": [
            {"id": f"e{index}", "source": {"nodeId": stages[index][0], "port": "out"}, "target": {"nodeId": stages[index + 1][0], "port": "in"}}
            for index in range(len(stages) - 1)
        ] + [
            {"id": "data-tasks", "source": {"nodeId": "decompose", "port": "tasks"}, "target": {"nodeId": "dispatch", "port": "tasks"}},
            {"id": "data-results", "source": {"nodeId": "dispatch", "port": "results"}, "target": {"nodeId": "join", "port": "results"}},
        ],
        "governance": {"maxSteps": 40, "maxConcurrency": 4},
    }


def codes(result) -> set[str]:
    return {error.code for error in result.errors}


def test_default_agent_runtime_fixture_shape_is_fully_supported() -> None:
    fixture_path = (
        Path(__file__).resolve().parents[2]
        / "plans"
        / "agent-platform-redesign"
        / "fixtures"
        / "default-agent-runtime-workflow.json"
    )
    exact_fixture = json.loads(fixture_path.read_text(encoding="utf-8"))
    result = validate(exact_fixture, {"viewport": {"x": 4, "y": 5}})
    assert result.valid
    assert result.canonical_definition is not None
    assert result.canonical_definition["nodes"][0]["id"] == "agent_loop"
    assert result.definition_sha256 and len(result.definition_sha256) == 64


def test_runtime_variant_is_required_and_verifier_rejects_worker_stages() -> None:
    missing = agent_runtime_graph()
    del missing["runtimeVariant"]
    assert "invalid_runtime_variant" in codes(validate(missing))

    verifier = agent_runtime_graph()
    verifier["runtimeVariant"] = "verifier"
    result = validate(verifier)
    assert "verifier_forbidden_stage" in codes(result)

    children = verifier["nodes"][4]["children"]
    verifier["nodes"][4]["children"] = [
        child
        for child in children
        if child["type"]
        not in {
            "load_skill",
            "tool_policy_and_approval_gate",
            "tool_call_and_observation",
        }
    ]
    assert validate(verifier).valid

    worker = agent_runtime_graph()
    worker["nodes"][4]["children"] = [
        child
        for child in worker["nodes"][4]["children"]
        if child["type"] != "checkpoint_and_budget_gate"
    ]
    assert "missing_required_loop_stage" in codes(validate(worker))


def test_runtime_variant_kind_matrix_and_verifier_loop_requirements() -> None:
    not_agent_runtime = orchestrator_graph()
    not_agent_runtime["runtimeVariant"] = "worker"
    assert "runtime_variant_not_allowed" in codes(validate(not_agent_runtime))

    unknown_variant = agent_runtime_graph()
    unknown_variant["runtimeVariant"] = "auditor"
    assert "invalid_runtime_variant" in codes(validate(unknown_variant))

    verifier = agent_runtime_graph()
    verifier["runtimeVariant"] = "verifier"
    verifier["nodes"][4]["children"] = [
        child
        for child in verifier["nodes"][4]["children"]
        if child["type"] == "checkpoint_and_budget_gate"
    ]
    result = validate(verifier)
    assert not result.valid
    assert "missing_required_loop_stage" in codes(result)


def test_semantic_hash_ignores_ui_and_node_order() -> None:
    graph = agent_runtime_graph()
    reordered = deepcopy(graph)
    reordered["nodes"].reverse()
    reordered["edges"].reverse()
    first = validate(graph, {"viewport": {"x": 1}})
    second = validate(reordered, {"viewport": {"x": 999}, "positions": {"start": {"x": 12}}})
    assert first.valid and second.valid
    assert first.definition_sha256 == second.definition_sha256
    assert first.ui_metadata_sha256 != second.ui_metadata_sha256


def test_validator_rejects_port_dangling_cycle_and_forbidden_content() -> None:
    graph = agent_runtime_graph()
    graph["edges"][0]["source"]["port"] = "missing"
    graph["edges"].append({"id": "loopback", "source": {"nodeId": "output", "port": "out"}, "target": {"nodeId": "context", "port": "in"}})
    graph["nodes"][1]["config"] = {"system_prompt": "do not allow"}
    graph["edges"].append(
        {"id": "ghost", "source": {"nodeId": "output", "port": "out"}, "target": {"nodeId": "does-not-exist", "port": "in"}}
    )
    result = validate(graph)
    assert not result.valid
    assert {
        "unknown_port",
        "dangling_edge",
        "unbounded_cycle",
        "forbidden_embedded_content",
    } <= codes(result)


def test_validator_rejects_unknown_versioned_schema_fields() -> None:
    graph = agent_runtime_graph()
    graph["nodes"][3]["config"] = {"unsafe": True}
    graph["not_part_of_v1"] = True
    result = validate(graph, ["ui must be an object"])
    assert not result.valid
    assert {"unknown_config_field", "unknown_definition_field", "invalid_ui_metadata"} <= codes(result)


def test_orchestrator_requires_governance_shell_and_join_for_fanout() -> None:
    graph = orchestrator_graph()
    graph["nodes"] = [node for node in graph["nodes"] if node["type"] != "join_worker_results"]
    graph["edges"] = [edge for edge in graph["edges"] if edge["target"]["nodeId"] != "join"]
    graph["edges"].append({"id": "fan", "source": {"nodeId": "dispatch", "port": "out"}, "target": {"nodeId": "verify", "port": "in"}})
    graph["edges"].append({"id": "fan2", "source": {"nodeId": "dispatch", "port": "out"}, "target": {"nodeId": "aggregate", "port": "in"}})
    result = validate(graph)
    assert not result.valid
    assert {"missing_required_stage", "fanout_missing_join"} <= codes(result)


def test_orchestrator_visible_repair_and_audit_are_required_and_ordered() -> None:
    graph = orchestrator_graph()
    graph["nodes"] = [node for node in graph["nodes"] if node["type"] != "audit"]
    graph["edges"] = [edge for edge in graph["edges"] if edge["source"]["nodeId"] != "audit"]
    result = validate(graph)
    assert not result.valid
    assert "missing_required_stage" in codes(result)

    bypass = orchestrator_graph()
    bypass["edges"].append(
        {"id": "skip-dispatch", "source": {"nodeId": "sufficiency", "port": "out"}, "target": {"nodeId": "aggregate", "port": "in"}}
    )
    result = validate(bypass)
    assert not result.valid
    assert "governance_stage_bypassed" in codes(result)

    # Every required stage is present exactly once and the chain stays acyclic,
    # but bounded_repair now runs before aggregate_results: only the ordering
    # invariant may reject this graph.
    reordered = orchestrator_graph()
    by_edge = {edge["id"]: edge for edge in reordered["edges"]}
    by_edge["e6"]["target"]["nodeId"] = "aggregate"
    by_edge["e7"]["source"]["nodeId"] = "aggregate"
    by_edge["e7"]["target"]["nodeId"] = "repair"
    by_edge["e8"]["source"]["nodeId"] = "repair"
    by_edge["e8"]["target"]["nodeId"] = "respond"
    result = validate(reordered)
    assert not result.valid
    assert codes(result) == {"governance_stage_order"}


def test_control_and_data_edges_are_separate_and_inputs_are_single_writer() -> None:
    graph = orchestrator_graph()
    # This is a typed data edge; it does not create a control fan-out.
    assert validate(graph).valid

    duplicate = deepcopy(graph)
    duplicate["edges"].append(
        {"id": "same-meaning", "source": {"nodeId": "dispatch", "port": "results"}, "target": {"nodeId": "join", "port": "results"}}
    )
    result = validate(duplicate)
    assert not result.valid
    assert "duplicate_semantic_edge" in codes(result)

    over_connected = deepcopy(graph)
    over_connected["edges"].append(
        {"id": "second-context-input", "source": {"nodeId": "sufficiency", "port": "out"}, "target": {"nodeId": "context", "port": "in"}}
    )
    result = validate(over_connected)
    assert not result.valid
    assert "input_port_cardinality_exceeded" in codes(result)


def test_every_control_fanout_needs_a_matching_join_without_bypass() -> None:
    graph = orchestrator_graph()
    graph["edges"].append(
        {"id": "dispatch-bypass", "source": {"nodeId": "dispatch", "port": "out"}, "target": {"nodeId": "verify", "port": "in"}}
    )
    result = validate(graph)
    assert not result.valid
    assert "fanout_missing_matching_join" in codes(result)

    # Both branches of the sufficiency fan-out do reach the join, so a matching
    # join exists; the audit branch may still terminate at End without it.
    bypassing = orchestrator_graph()
    bypassing["edges"].extend(
        [
            {"id": "branch-audit", "source": {"nodeId": "sufficiency", "port": "out"}, "target": {"nodeId": "audit", "port": "in"}},
            {"id": "audit-rejoin", "source": {"nodeId": "audit", "port": "out"}, "target": {"nodeId": "join", "port": "in"}},
        ]
    )
    result = validate(bypassing)
    assert not result.valid
    assert "fanout_branch_bypasses_join" in codes(result)


def test_only_explicit_bounded_body_continue_exit_cycle_is_accepted() -> None:
    graph = agent_runtime_graph()
    graph["nodes"].append(
        {"id": "body", "type": "model_step", "typeVersion": "1.0", "config": {}}
    )
    graph["edges"] = [edge for edge in graph["edges"] if edge["id"] != "e4"]
    graph["edges"].extend(
        [
            {"id": "loop-body", "source": {"nodeId": "loop", "port": "body"}, "target": {"nodeId": "body", "port": "in"}},
            {"id": "loop-return", "source": {"nodeId": "body", "port": "out"}, "target": {"nodeId": "loop", "port": "continue"}},
            {"id": "loop-exit", "source": {"nodeId": "loop", "port": "exit"}, "target": {"nodeId": "output", "port": "in"}},
        ]
    )
    assert validate(graph).valid

    invalid = deepcopy(graph)
    invalid["edges"] = [edge for edge in invalid["edges"] if edge["id"] != "loop-exit"]
    invalid["edges"].append(
        {"id": "fake-exit", "source": {"nodeId": "loop", "port": "out"}, "target": {"nodeId": "output", "port": "in"}}
    )
    result = validate(invalid)
    assert not result.valid
    assert {"invalid_bounded_loop_contract", "unbounded_cycle"} <= codes(result)


def test_subflow_is_rejected_by_validate_and_simulate() -> None:
    graph = agent_runtime_graph()
    graph["kind"] = "subflow"
    result = validate(graph)
    assert not result.valid
    assert "subflow_not_supported" in codes(result)

    client = TestClient(app)
    response = client.post(
        "/workflow-designer/simulate",
        headers=auth_headers(role="ADMIN"),
        json={"definition": graph, "ui_metadata": {}},
    )
    assert response.status_code == 200
    assert response.json()["valid"] is False
    assert response.json()["trace"] is None


def test_required_agent_runtime_stages_and_typed_ports_fail_closed() -> None:
    graph = agent_runtime_graph()
    graph["nodes"] = [node for node in graph["nodes"] if node["type"] != "dependency_and_capability_preflight"]
    graph["edges"] = [edge for edge in graph["edges"] if edge["source"]["nodeId"] != "preflight" and edge["target"]["nodeId"] != "preflight"]
    result = validate(graph)
    assert not result.valid
    assert "missing_required_stage" in codes(result)

    incompatible = orchestrator_graph()
    incompatible["edges"].append(
        {"id": "bad-data-type", "source": {"nodeId": "decompose", "port": "tasks"}, "target": {"nodeId": "join", "port": "results"}}
    )
    result = validate(incompatible)
    assert not result.valid
    assert "incompatible_port_type" in codes(result)


def test_internal_catalog_validate_and_simulate_contracts() -> None:
    client = TestClient(app)
    assert client.get("/workflow-designer/catalog/nodes").status_code == 401
    headers = auth_headers(role="ADMIN")
    catalog = client.get("/workflow-designer/catalog/nodes", headers=headers)
    assert catalog.status_code == 200
    assert catalog.json()["nodes"]
    dispatch = next(node for node in catalog.json()["nodes"] if node["type"] == "dispatch_agents")
    assert {"in", "tasks"} == {port["id"] for port in dispatch["inputs"]}
    # The `/tools` catalogue contract belongs to tests/test_tools_api.py, which
    # asserts it exactly instead of by predicate; do not re-assert it here.
    assert dispatch["workflowKinds"] == ["orchestrator"]
    assert dispatch["requiredStage"] is True
    model_step = next(node for node in catalog.json()["nodes"] if node["type"] == "model_step")
    assert model_step["workflowKinds"] == ["agent-runtime"]
    assert model_step["requiredStage"] is False
    bounded_repair = next(node for node in catalog.json()["nodes"] if node["type"] == "bounded_repair")
    audit = next(node for node in catalog.json()["nodes"] if node["type"] == "audit")
    assert bounded_repair["workflowKinds"] == ["orchestrator"]
    assert bounded_repair["requiredStage"] is True
    assert audit["workflowKinds"] == ["orchestrator"]
    assert audit["requiredStage"] is True
    start = next(node for node in catalog.json()["nodes"] if node["type"] == "start")
    assert start["workflowKinds"] == ["agent-runtime", "orchestrator"]
    assert start["requiredStage"] is True
    body = {"definition": agent_runtime_graph(), "ui_metadata": {"viewport": {"x": 0, "y": 0}}}
    validate_response = client.post("/workflow-designer/validate", headers=headers, json=body)
    assert validate_response.status_code == 200
    payload = validate_response.json()
    assert payload["valid"] is True
    assert payload["canonicalDefinition"]["kind"] == "agent-runtime"
    simulate_response = client.post("/workflow-designer/simulate", headers=headers, json=body)
    assert simulate_response.status_code == 200
    assert all(item["status"] == "simulated" for item in simulate_response.json()["trace"])
