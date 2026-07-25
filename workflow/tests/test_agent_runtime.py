from __future__ import annotations

import base64
import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest
from langgraph.checkpoint.memory import InMemorySaver
from langgraph.types import Command

from app.engine.skill import Skill
from app.runtime.artifacts import LoadedSkillArtifact
from app.runtime.checkpoints import (
    checkpoint_config,
    checkpoint_ref,
    config_from_checkpoint_ref,
    strict_serializer,
)
from app.runtime.facts import (
    ProposedAction,
    RuntimeFactEnvelope,
    materialize_trusted_facts,
)
from app.runtime.graph import build_context, compile_runtime_graph, initial_state
from app.runtime.model import (
    MAX_PROVIDER_INPUT_UTF8_BYTES,
    LangChainRuntimeModel,
    ModelContextTooLarge,
    ModelProtocolError,
    ModelTurn,
)
from app.runtime.models import (
    DirectAgentExecutionSnapshot,
    MAX_CALLER_GROUPS_JOINED_UTF8_BYTES,
    MAX_SNAPSHOT_CANONICAL_BYTES,
    RuntimeCommand,
    canonical_json_bytes,
    canonical_json_sha256,
    parse_json_preserving_numbers,
)
from app.runtime.policy import PreActionPolicy
from app.runtime.tool_boundary import (
    DirectToolDenied,
    effective_specs,
    invoke_direct_tool,
)
from app.nodes.kbquery.adapters import BackendVectorSearch
from app.nodes.kbquery.models import SourceResult
from app.security import RequestContext
from app.settings import settings
from app.llm import get_direct_agent_runtime_llm
from app.workflow_contracts import GRAPH_IR_COMPILER_CONTRACT_VERSION


def _legacy_python_canonical_writer_vector() -> None:
    raw = (
        '{"nested":{"\\uf900":1.230,"\\ud800\\udc00":0.00000100},'
        '"negative":-0,"exp":1e+3,'
        '"large":123456789012345678901234567890,'
        '"bool":true,"null":null,'
        '"strings":"line\\n\\u2028\\u2029\\ud800\\udc00\\u6f22"}'
    )


def test_direct_agent_provider_disables_hidden_retries() -> None:
    get_direct_agent_runtime_llm.cache_clear()
    assert get_direct_agent_runtime_llm().max_retries == 0
    raw = (
        '{"nested":{"\\uf900":1.230,"\\ud800\\udc00":0.00000100},'
        '"negative":-0,"exp":1e+3,'
        '"large":123456789012345678901234567890,'
        '"bool":true,"null":null,'
        '"strings":"line\\n\\u2028\\u2029\\ud800\\udc00\\u6f22"}'
    )
    parsed = parse_json_preserving_numbers(raw)
    expected = (
        '{"bool":true,"exp":1e+3,'
        '"large":123456789012345678901234567890,'
        '"negative":-0,"nested":{"𐀀":0.00000100,"豈":1.230},'
        '"null":null,"strings":"line\\n  𐀀漢"}'
    )
    assert canonical_json_bytes(parsed).decode("utf-8") == expected
    assert (
        canonical_json_sha256(parsed)
        == "81131943b80f1df2f86cc3f9d4798f36f73388376580bb95dfea9511ac412aaa"
    )


def test_v2_checkpoint_ref_round_trips_generation_namespace() -> None:
    config = checkpoint_config(
        tenant_id="tenant-a",
        user_id="user-a",
        run_id="271c9de5-d772-48d7-8236-b6a46f4588f5",
        snapshot_hash="a" * 64,
        lease_generation=7,
    )
    config["configurable"]["checkpoint_id"] = (
        "018f6f21-6c42-7abc-8def-0123456789ab"
    )
    ref = checkpoint_ref(config)
    assert ref and ref.startswith("v2:7:")
    assert config_from_checkpoint_ref(ref) == config


@pytest.mark.parametrize(
    "value",
    [
        "v2:0:" + "a" * 64 + ":018f6f21-6c42-7abc-8def-0123456789ab",
        "v2:01:" + "a" * 64 + ":018f6f21-6c42-7abc-8def-0123456789ab",
        "v2:1:" + "A" * 64 + ":018f6f21-6c42-7abc-8def-0123456789ab",
        "v2:1:" + "a" * 64 + ":NOT-A-UUID",
    ],
)
def test_v2_checkpoint_ref_rejects_noncanonical_identity(value: str) -> None:
    with pytest.raises(ValueError):
        config_from_checkpoint_ref(value)


def test_authoritative_dotnet_canonical_bytes_preserve_numeric_lexemes() -> None:
    raw = (
        b'{"numbers":{"decimal":1.230,"exponent":1e+3,'
        b'"large":9007199254740993123456789,"negative_zero":-0,'
        b'"small":0.00000100},"strings":"line\\u2028paragraph\\u2029next'
        b'\\u0085nbsp\\u00A0control\\u0001astral\\uD83D\\uDE00'
        b'\xe4\xb8\xad","\\uD83D\\uDE00":"astral","\\uE000":"bmp"}'
    )
    assert hashlib.sha256(raw).hexdigest() == (
        "a158ea00b3e6ba6bbe1c749d0fc58f389329fc002877425a28afccba95f4b818"
    )
    parsed = parse_json_preserving_numbers(raw)
    assert parsed["numbers"] == {
        "decimal": "1.230",
        "exponent": "1e+3",
        "large": "9007199254740993123456789",
        "negative_zero": "-0",
        "small": "0.00000100",
    }
    assert parsed["\U0001f600"] == "astral"
    assert parsed["\ue000"] == "bmp"


def test_snapshot_hash_recomputes_from_preserved_numeric_tokens() -> None:
    raw = snapshot().model_dump(mode="python")
    raw.pop("snapshot_hash")
    raw["agent"]["business_rules"]["numeric_vectors"] = parse_json_preserving_numbers(
        '{"scaled":1.230,"tiny":0.00000100,"negativeZero":-0}'
    )
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    restored = DirectAgentExecutionSnapshot.from_preserved_json(raw)
    restored.assert_hash()


def test_snapshot_canonical_envelope_fails_closed() -> None:
    payload = snapshot().model_dump(mode="python", exclude={"snapshot_hash"})
    raw = canonical_json_bytes(payload)
    encoded = base64.b64encode(raw).decode("ascii")
    digest = hashlib.sha256(raw).hexdigest()
    DirectAgentExecutionSnapshot.from_canonical_base64(encoded, digest).assert_hash()
    with pytest.raises(ValueError, match="hash"):
        DirectAgentExecutionSnapshot.from_canonical_base64(encoded, "0" * 64)
    with pytest.raises(ValueError, match="base64"):
        DirectAgentExecutionSnapshot.from_canonical_base64("***", digest)
    invalid_utf8 = base64.b64encode(b"\xff").decode("ascii")
    with pytest.raises((UnicodeDecodeError, ValueError)):
        DirectAgentExecutionSnapshot.from_canonical_base64(
            invalid_utf8, hashlib.sha256(b"\xff").hexdigest()
        )
    oversized = base64.b64encode(
        b"x" * (MAX_SNAPSHOT_CANONICAL_BYTES + 1)
    ).decode("ascii")
    with pytest.raises(ValueError, match="limit"):
        DirectAgentExecutionSnapshot.from_canonical_base64(
            oversized,
            hashlib.sha256(b"x" * (MAX_SNAPSHOT_CANONICAL_BYTES + 1)).hexdigest(),
        )


def snapshot(
    *,
    tools: list[str] | None = None,
    sources: list[str] | None = None,
    with_skill: bool = False,
    skill_kind: str = "agentic",
    rules: dict[str, Any] | None = None,
) -> DirectAgentExecutionSnapshot:
    skills = (
        [
            {
                "name": "research-skill",
                "revision": 3,
                "kind": skill_kind,
                "description": "Pinned research instructions",
                "definition_sha256": "a" * 64,
                "package_sha256": "b" * 64 if skill_kind == "agentic" else None,
                "allowed_tools": [],
            }
        ]
        if with_skill
        else []
    )
    workflow = {
        "schemaVersion": 1,
        "kind": "agent-runtime",
        "nodes": [
            {"id": "start", "type": "start", "config": {}},
            {
                "id": "preflight",
                "type": "dependency_and_capability_preflight",
                "config": {},
            },
            {"id": "context", "type": "inject_authorized_context", "config": {}},
            {"id": "checkpoint", "type": "checkpoint", "config": {}},
            {
                "id": "loop",
                "type": "bounded_agent_loop",
                "config": {"maxIterations": 8},
                "children": [
                    {"id": "model", "type": "model_step"},
                    {"id": "load", "type": "load_skill"},
                    {"id": "gate", "type": "tool_policy_and_approval_gate"},
                    {"id": "call", "type": "tool_call_and_observation"},
                    {"id": "budget", "type": "checkpoint_and_budget_gate"},
                ],
            },
            {"id": "validate", "type": "validate_structured_output", "config": {}},
            {
                "id": "repair",
                "type": "bounded_repair_or_controlled_failure",
                "config": {"maxRepairRounds": 2},
            },
            {"id": "end", "type": "end", "config": {}},
        ],
        "edges": [],
        "governance": {"maxSteps": 20, "maxConcurrency": 1},
    }
    tool_values = tools or []
    source_values = sources or []
    raw: dict[str, Any] = {
        "run_id": "271c9de5-d772-48d7-8236-b6a46f4588f5",
        "agent": {
            "id": "8cdbd5f7-5185-4027-87c7-e6a1adff2ad3",
            "revision": 7,
            "name": "測試助理",
            "system_prompt": "請安全地回答中文問題。",
            "execution_roles": ["worker"],
            "audience": ["ADMIN"],
            "output_contract": {},
            "business_rules": rules or {"version": 1, "rules": []},
            "allowed_tools": tool_values,
            "knowledge_sources": source_values,
            "runtime_limits": {
                "max_tool_rounds": 4,
                "max_context_rounds": 2,
                "timeout_seconds": 10,
                "token_budget": 4_000,
                "step_budget": 12,
            },
        },
        "workflow": {
            "id": "00000000-0000-4000-8000-000000000001",
            "revision": 2,
            "definition": workflow,
            "definition_sha256": canonical_json_sha256(workflow),
            "compiler_contract_version": GRAPH_IR_COMPILER_CONTRACT_VERSION,
        },
        "skills": skills,
        "caller": {
            "tenant_id": "tenant-甲",
            "user_id": "admin-1",
            "role": "ADMIN",
            "groups": [],
            "tool_grants": tool_values,
            "knowledge_source_grants": source_values,
        },
        "mode": "test",
    }
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    return DirectAgentExecutionSnapshot.model_validate(raw)


class FakeModel:
    def __init__(self, commands: list[RuntimeCommand]):
        self.commands = list(commands)
        self.seen: list[dict[str, Any]] = []

    async def next_command(self, **kwargs: Any) -> ModelTurn:
        self.seen.append(kwargs)
        return ModelTurn(self.commands.pop(0), token_usage=10)


class UsageModel:
    def __init__(self, usage: Any):
        self.usage = usage
        self.calls = 0

    async def next_command(self, **kwargs: Any) -> ModelTurn:
        self.calls += 1
        return ModelTurn(
            RuntimeCommand(kind="final", content="done"),
            token_usage=self.usage,
        )


@pytest.mark.asyncio
async def test_registered_write_tool_enters_waiting_approval_without_invoking_tool(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    run_snapshot = snapshot(tools=["runtime.write_evidence"])
    monkeypatch.setattr("app.runtime.graph.settings.agent_write_tools_enabled", True)
    monkeypatch.setattr(
        "app.runtime.tool_boundary.settings.agent_write_tools_enabled", True
    )
    monkeypatch.setattr(
        "app.runtime.tool_boundary.settings.agent_write_tools_allowlist",
        "runtime.write_evidence",
    )
    monkeypatch.setattr(
        "app.runtime.tool_boundary.settings.agent_write_tools_tenant_allowlist",
        run_snapshot.caller.tenant_id,
    )

    class MustNotWrite:
        calls = 0

        async def write(self, *_args, **_kwargs):
            self.calls += 1
            raise AssertionError("approval gate must run before a write tool")

    sink = MustNotWrite()
    graph = compile_runtime_graph(InMemorySaver(serde=strict_serializer()))
    context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=FakeModel(
            [
                RuntimeCommand(
                    kind="tool_call",
                    name="runtime.write_evidence",
                    arguments={"record_id": "refund-1", "value": "approved"},
                )
            ]
        ),
        artifact_reader=FakeArtifactReader(),
        deps=SimpleNamespace(write_evidence_sink=sink),
    )
    config = checkpoint_config(
        tenant_id=run_snapshot.caller.tenant_id,
        user_id=run_snapshot.caller.user_id,
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    interrupted = await graph.ainvoke(initial_state(run_snapshot, "write"), config, context=context)

    assert interrupted["__interrupt__"]
    state = await graph.aget_state(config)
    assert state.interrupts
    assert state.values["pending_approval"]["required_role"] == "ADMIN"
    assert state.values["pending_approval"]["action_fingerprint"]
    assert sink.calls == 0


@pytest.mark.asyncio
async def test_registered_write_tool_fails_closed_when_write_flag_is_disabled(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    run_snapshot = snapshot(tools=["runtime.write_evidence"])
    monkeypatch.setattr("app.runtime.graph.settings.agent_write_tools_enabled", False)
    graph = compile_runtime_graph(InMemorySaver(serde=strict_serializer()))
    context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=FakeModel(
            [
                RuntimeCommand(
                    kind="tool_call",
                    name="runtime.write_evidence",
                    arguments={"record_id": "refund-1", "value": "approved"},
                )
            ]
        ),
        artifact_reader=FakeArtifactReader(),
    )
    result = await graph.ainvoke(
        initial_state(run_snapshot, "write"),
        checkpoint_config(
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            run_id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
        ),
        context=context,
    )
    assert result["status"] == "failed"
    assert result["error_code"] == "write_tools_disabled"


@pytest.mark.asyncio
@pytest.mark.parametrize("usage", [-1, float("nan"), 4_001])
async def test_invalid_or_overshoot_model_usage_fails_before_final_accept(
    usage: Any,
) -> None:
    run_snapshot = snapshot()
    model = UsageModel(usage)
    graph = compile_runtime_graph(InMemorySaver(serde=strict_serializer()))
    context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=model,
        artifact_reader=FakeArtifactReader(),
    )
    result = await graph.ainvoke(
        initial_state(run_snapshot, "start"),
        checkpoint_config(
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            run_id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
        ),
        context=context,
    )
    assert result["status"] == "failed"
    assert result["error_code"] == "model_usage_invalid"
    assert result["final_output"] is None
    assert not any(
        event["event_type"] == "response_proposed" for event in result["events"]
    )


def test_namespaced_audience_allows_role_or_exact_group() -> None:
    raw = snapshot().model_dump(mode="python")
    raw["agent"]["audience"] = ["role:ADMIN"]
    raw["snapshot_hash"] = canonical_json_sha256(
        {key: value for key, value in raw.items() if key != "snapshot_hash"}
    )
    DirectAgentExecutionSnapshot.model_validate(raw)

    raw["agent"]["audience"] = ["group:finance"]
    raw["caller"]["groups"] = ["finance"]
    raw["snapshot_hash"] = canonical_json_sha256(
        {key: value for key, value in raw.items() if key != "snapshot_hash"}
    )
    DirectAgentExecutionSnapshot.model_validate(raw)


def test_caller_groups_are_sorted_unique_raw_canonical_ids() -> None:
    raw = snapshot().model_dump(mode="python")
    raw["agent"]["audience"] = ["group:finance.apac"]
    raw["caller"]["groups"] = ["engineering", "finance.apac"]
    raw["snapshot_hash"] = canonical_json_sha256(
        {key: value for key, value in raw.items() if key != "snapshot_hash"}
    )
    DirectAgentExecutionSnapshot.model_validate(raw)

    for groups in (
        ["Finance"],
        ["group:finance"],
        ["finance:*"],
        [" finance"],
        ["finance", "engineering"],
        ["finance", "finance"],
    ):
        invalid = dict(raw)
        invalid["caller"] = {**raw["caller"], "groups": groups}
        invalid["snapshot_hash"] = canonical_json_sha256(
            {key: value for key, value in invalid.items() if key != "snapshot_hash"}
        )
        with pytest.raises(ValueError):
            DirectAgentExecutionSnapshot.model_validate(invalid)


def test_caller_groups_joined_utf8_exact_boundary_and_plus_one() -> None:
    exact = [
        f"{index:02d}" + "a" * (126 if index == 0 else 125)
        for index in range(16)
    ]
    assert len(" ".join(exact).encode("utf-8")) == (
        MAX_CALLER_GROUPS_JOINED_UTF8_BYTES
    )
    raw = snapshot().model_dump(mode="python")
    raw["agent"]["audience"] = [f"group:{exact[0]}"]
    raw["caller"]["groups"] = exact
    raw["snapshot_hash"] = canonical_json_sha256(
        {key: value for key, value in raw.items() if key != "snapshot_hash"}
    )
    DirectAgentExecutionSnapshot.model_validate(raw)

    over = list(exact)
    over[1] += "a"
    assert len(" ".join(over).encode("utf-8")) == (
        MAX_CALLER_GROUPS_JOINED_UTF8_BYTES + 1
    )
    invalid = {**raw, "caller": {**raw["caller"], "groups": over}}
    invalid["snapshot_hash"] = canonical_json_sha256(
        {key: value for key, value in invalid.items() if key != "snapshot_hash"}
    )
    with pytest.raises(ValueError, match="joined UTF-8 limit"):
        DirectAgentExecutionSnapshot.model_validate(invalid)


def test_canonical_artifact_rejects_overbound_joined_groups() -> None:
    groups = [
        f"{index:02d}" + "a" * (126 if index < 2 else 125)
        for index in range(16)
    ]
    raw = snapshot().model_dump(mode="python", exclude={"snapshot_hash"})
    raw["agent"]["audience"] = [f"group:{groups[0]}"]
    raw["caller"]["groups"] = groups
    canonical = canonical_json_bytes(raw)
    with pytest.raises(ValueError, match="joined UTF-8 limit"):
        DirectAgentExecutionSnapshot.from_canonical_base64(
            base64.b64encode(canonical).decode("ascii"),
            hashlib.sha256(canonical).hexdigest(),
        )


def test_canonical_artifact_with_malformed_group_is_rejected_after_hash_check() -> None:
    raw = snapshot().model_dump(mode="python", exclude={"snapshot_hash"})
    raw["caller"]["groups"] = ["GROUP"]
    canonical = canonical_json_bytes(raw)
    with pytest.raises(ValueError, match="canonical raw group IDs"):
        DirectAgentExecutionSnapshot.from_canonical_base64(
            base64.b64encode(canonical).decode("ascii"),
            hashlib.sha256(canonical).hexdigest(),
        )


def test_rule_role_catalog_does_not_inherit_agent_audience_entries() -> None:
    rules = {
        "version": 1,
        "rules": [
            {
                "id": "bad-role",
                "name": "Bad role",
                "enabled": True,
                "priority": 1,
                "when": {"fact": "action.type", "op": "eq", "value": "response"},
                "then": [{"action": "escalate", "role": "group:finance"}],
                "onUnknown": [{"action": "deny", "reason": "unknown"}],
            }
        ],
    }
    raw = snapshot(rules=rules).model_dump(mode="python")
    raw["agent"]["audience"] = ["group:finance"]
    raw["caller"]["groups"] = ["finance"]
    raw["snapshot_hash"] = canonical_json_sha256(
        {key: value for key, value in raw.items() if key != "snapshot_hash"}
    )
    run_snapshot = DirectAgentExecutionSnapshot.model_validate(raw)
    with pytest.raises(Exception, match="business rules"):
        build_context(
            snapshot=run_snapshot,
            request_context=request_context(),
            model=FakeModel([]),
            artifact_reader=FakeArtifactReader(),
        )


@pytest.mark.parametrize(
    "audience",
    [
        ["group:*"],
        ["ADMINISTRATORS"],
        ["role:"],
        ["role:OWNER"],
        ["group: finance"],
        ["group:Finance"],
        ["group:finance"],
        ["finance"],
    ],
)
def test_malformed_or_unrelated_audience_fails_closed(audience: list[str]) -> None:
    raw = snapshot().model_dump(mode="python")
    raw["agent"]["audience"] = audience
    raw["snapshot_hash"] = canonical_json_sha256(
        {key: value for key, value in raw.items() if key != "snapshot_hash"}
    )
    with pytest.raises(ValueError):
        DirectAgentExecutionSnapshot.model_validate(raw)


@pytest.mark.asyncio
async def test_matched_response_policy_fails_closed_and_audit_tags_are_durable() -> None:
    rules = {
        "version": 1,
        "rules": [
            {
                "id": "response-effects",
                "name": "Response effects",
                "enabled": True,
                "priority": 10,
                "when": {"fact": "action.type", "op": "eq", "value": "response"},
                "then": [
                    {"action": "set_response_policy", "policy": "unsupported"},
                    {"action": "add_audit_tag", "tag": "reviewed"},
                ],
                "onUnknown": [{"action": "deny", "reason": "unknown"}],
            }
        ],
    }
    run_snapshot = snapshot(rules=rules)
    graph = compile_runtime_graph(InMemorySaver(serde=strict_serializer()))
    context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=FakeModel([RuntimeCommand(kind="final", content="done")]),
        artifact_reader=FakeArtifactReader(),
    )
    result = await graph.ainvoke(
        initial_state(run_snapshot, "start"),
        checkpoint_config(
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            run_id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
        ),
        context=context,
    )
    assert result["status"] == "failed"
    assert result["error_code"] == "unsupported_response_policy"
    decision = next(
        event for event in result["events"] if event["event_type"] == "rule_decision"
    )
    assert decision["payload"]["matched_rule_ids"] == ["response-effects"]
    assert decision["payload"]["audit_tags"] == ["reviewed"]


@pytest.mark.asyncio
async def test_nonmatching_response_policy_does_not_block() -> None:
    rules = {
        "version": 1,
        "rules": [
            {
                "id": "tools-only",
                "name": "Tools only",
                "enabled": True,
                "priority": 10,
                "when": {"fact": "action.type", "op": "eq", "value": "tool_call"},
                "then": [{"action": "set_response_policy", "policy": "tools-only"}],
                "onUnknown": [{"action": "deny", "reason": "unknown"}],
            }
        ],
    }
    run_snapshot = snapshot(rules=rules)
    graph = compile_runtime_graph(InMemorySaver(serde=strict_serializer()))
    context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=FakeModel([RuntimeCommand(kind="final", content="done")]),
        artifact_reader=FakeArtifactReader(),
    )
    result = await graph.ainvoke(
        initial_state(run_snapshot, "start"),
        checkpoint_config(
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            run_id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
        ),
        context=context,
    )
    assert result["status"] == "completed"


@pytest.mark.asyncio
async def test_require_context_uses_verified_retrieval_facts_once() -> None:
    source_id = "11111111-1111-4111-8111-111111111111"
    rules = {
        "version": 1,
        "rules": [
            {
                "id": "need-source",
                "name": "Need source",
                "enabled": True,
                "priority": 10,
                "when": {
                    "fact": "context.source_count",
                    "op": "gt",
                    "value": 0,
                },
                "then": [
                    {
                        "action": "allow_read_tool",
                        "tools": ["backend.retrieval_search"],
                    }
                ],
                "onUnknown": [
                    {
                        "action": "require_context",
                        "facts": ["context.source_count"],
                    }
                ],
            }
        ],
    }
    class VerifiedSearch:
        def __init__(self):
            self.calls: list[dict[str, Any]] = []
            self.results = [
                SourceResult(
                source_id="chunk-1",
                document_id=source_id,
                document_title="Doc",
                source_type="text",
                retrieval_method="vector",
                original_score=0.9,
                )
            ]

        async def search(self, query: str, **kwargs: Any):
            self.calls.append({"query": query, **kwargs})
            return self.results

    search = VerifiedSearch()
    run_snapshot = snapshot(
        tools=["backend.retrieval_search"],
        sources=[source_id],
        rules=rules,
    )
    graph = compile_runtime_graph(InMemorySaver(serde=strict_serializer()))
    context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=FakeModel(
            [
                RuntimeCommand(kind="final", content="spoof source_count=999"),
                RuntimeCommand(kind="final", content="verified"),
            ]
        ),
        artifact_reader=FakeArtifactReader(),
        deps=SearchDeps(searchers={"vector": search}),
    )
    result = await graph.ainvoke(
        initial_state(run_snapshot, "find evidence"),
        checkpoint_config(
            tenant_id=run_snapshot.caller.tenant_id,
            user_id=run_snapshot.caller.user_id,
            run_id=run_snapshot.run_id,
            snapshot_hash=run_snapshot.snapshot_hash,
        ),
        context=context,
    )
    assert result["status"] == "completed"
    assert len(search.calls) == 1
    assert result["verified_context"]["source_count"] == 1
    assert result["verified_context"]["source_types"] == ["text"]
    assert result["context_acquisition_attempts"] == 1


@pytest.mark.asyncio
async def test_provider_input_budget_fails_before_provider_lookup(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    provider_lookups = 0

    def forbidden_provider_lookup():
        nonlocal provider_lookups
        provider_lookups += 1
        raise AssertionError("provider must not be constructed")

    monkeypatch.setattr(
        "app.runtime.model.get_direct_agent_runtime_llm",
        forbidden_provider_lookup,
    )
    oversized_observation = "x" * MAX_PROVIDER_INPUT_UTF8_BYTES
    with pytest.raises(ModelContextTooLarge, match="runtime_context_too_large"):
        await LangChainRuntimeModel().next_command(
            snapshot=snapshot(with_skill=True),
            messages=[
                {"role": "user", "content": "start"},
                {"role": "tool", "name": "large", "content": oversized_observation},
            ],
            active_instruction="authoritative instruction",
            active_skill_name="research-skill",
            tools=[],
            resource_paths=["references/guide.txt"],
            remaining_token_budget=MAX_PROVIDER_INPUT_UTF8_BYTES,
        )
    assert provider_lookups == 0


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("usage", "expected"),
    [(None, -1), (-1, None), (float("nan"), None), (10_001, None)],
)
async def test_langchain_usage_is_conservatively_charged_or_rejected(
    monkeypatch: pytest.MonkeyPatch,
    usage: Any,
    expected: int | None,
) -> None:
    class Provider:
        bound: dict[str, Any] = {}

        def bind_tools(self, _schemas):
            return self

        def bind(self, **kwargs):
            self.bound = kwargs
            return self

        async def ainvoke(self, _messages):
            metadata = {} if usage is None else {"total_tokens": usage}
            return SimpleNamespace(
                content="done", tool_calls=[], usage_metadata=metadata
            )

    provider = Provider()
    monkeypatch.setattr(
        "app.runtime.model.get_direct_agent_runtime_llm", lambda: provider
    )
    call = LangChainRuntimeModel().next_command(
        snapshot=snapshot(),
        messages=[{"role": "user", "content": "hello"}],
        active_instruction="",
        active_skill_name="",
        tools=[],
        resource_paths=[],
        remaining_token_budget=10_000,
    )
    if expected is None:
        with pytest.raises(ModelProtocolError):
            await call
    else:
        turn = await call
        if usage is None:
            assert 0 < turn.token_usage < 10_000
            assert (
                turn.audit_metadata["token_usage_source"]
                == "conservative_input_plus_output_bound"
            )
        else:
            assert turn.token_usage == expected
        assert turn.command.kind == "final"
        assert 0 < provider.bound["max_tokens"] <= (
            settings.runtime_model_output_reserve_tokens
        )


@pytest.mark.asyncio
@pytest.mark.parametrize("content", ["漢" * 2_000, "\U0001f600" * 2_000])
async def test_remaining_run_token_budget_blocks_multibyte_input_before_provider(
    monkeypatch: pytest.MonkeyPatch,
    content: str,
) -> None:
    provider_lookups = 0

    def forbidden_provider_lookup():
        nonlocal provider_lookups
        provider_lookups += 1
        raise AssertionError("provider must not be constructed")

    monkeypatch.setattr(
        "app.runtime.model.get_direct_agent_runtime_llm",
        forbidden_provider_lookup,
    )
    with pytest.raises(ModelContextTooLarge, match="runtime_context_too_large"):
        await LangChainRuntimeModel().next_command(
            snapshot=snapshot(),
            messages=[{"role": "tool", "name": "observation", "content": content}],
            active_instruction="",
            active_skill_name="",
            tools=[],
            resource_paths=[],
            remaining_token_budget=256,
        )
    assert provider_lookups == 0


@pytest.mark.asyncio
async def test_configured_context_reserve_blocks_provider_call(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    provider_lookups = 0

    def forbidden_provider_lookup():
        nonlocal provider_lookups
        provider_lookups += 1
        raise AssertionError("provider must not be constructed")

    monkeypatch.setattr(
        "app.runtime.model.get_direct_agent_runtime_llm",
        forbidden_provider_lookup,
    )
    monkeypatch.setattr("app.runtime.model.settings.runtime_model_context_tokens", 1_024)
    monkeypatch.setattr(
        "app.runtime.model.settings.runtime_model_output_reserve_tokens", 512
    )
    with pytest.raises(ModelContextTooLarge):
        await LangChainRuntimeModel().next_command(
            snapshot=snapshot(),
            messages=[{"role": "user", "content": "x" * 512}],
            active_instruction="",
            active_skill_name="",
            tools=[],
            resource_paths=[],
            remaining_token_budget=100_000,
        )
    assert provider_lookups == 0


class FakeArtifactReader:
    def __init__(self, uses_tools: list[str] | None = None):
        self.calls = 0
        self.artifact = LoadedSkillArtifact(
            name="research-skill",
            revision=3,
            kind="agentic",
            definition_sha256="a" * 64,
            package_sha256="b" * 64,
            skill=Skill(
                name="research-skill",
                kind="agentic",
                uses_tools=uses_tools or [],
            ),
            instruction="Only use verified evidence.",
            instruction_sha256=canonical_json_sha256("Only use verified evidence."),
            resources={"references/guide.txt": b"bounded evidence"},
            scripts_present=True,
        )
        # Artifact instruction hashes are SHA over the raw UTF-8 instruction,
        # not canonical JSON string encoding.
        import hashlib

        object.__setattr__(
            self.artifact,
            "instruction_sha256",
            hashlib.sha256(self.artifact.instruction.encode()).hexdigest(),
        )

    async def read(self, pin, ctx):
        self.calls += 1
        return self.artifact


def request_context() -> RequestContext:
    return RequestContext(tenant_id="tenant-甲", user_id="admin-1", role="ADMIN")


def test_shared_d4_default_fixture_passes_direct_runtime_preflight() -> None:
    fixture_path = (
        Path(__file__).resolve().parents[2]
        / "plans"
        / "agent-platform-redesign"
        / "fixtures"
        / "default-agent-runtime-workflow.json"
    )
    fixture_bytes = fixture_path.read_bytes()
    assert not fixture_bytes.startswith(b"\xef\xbb\xbf")
    assert not fixture_bytes.endswith((b"\n", b"\r"))
    definition = json.loads(fixture_bytes)
    backend_pin = hashlib.sha256(fixture_bytes).hexdigest()
    assert backend_pin == canonical_json_sha256(definition)
    raw = snapshot().model_dump(mode="json", exclude={"snapshot_hash"})
    raw["workflow"]["definition"] = definition
    raw["workflow"]["definition_sha256"] = backend_pin
    raw["workflow"][
        "compiler_contract_version"
    ] = GRAPH_IR_COMPILER_CONTRACT_VERSION
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    value = DirectAgentExecutionSnapshot.model_validate(raw)

    build_context(
        snapshot=value,
        request_context=request_context(),
        model=FakeModel([]),
        artifact_reader=FakeArtifactReader(),
    )


@pytest.mark.parametrize("unsupported_contract", ["1", "graph-ir/1", "unknown"])
def test_direct_runtime_rejects_legacy_and_unknown_compiler_contracts(
    unsupported_contract: str,
) -> None:
    raw = snapshot().model_dump(mode="json", exclude={"snapshot_hash"})
    raw["workflow"]["compiler_contract_version"] = unsupported_contract
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    value = DirectAgentExecutionSnapshot.model_validate(raw)

    with pytest.raises(RuntimeError, match="unsupported runtime compiler contract"):
        build_context(
            snapshot=value,
            request_context=request_context(),
            model=FakeModel([]),
            artifact_reader=FakeArtifactReader(),
        )


def test_snapshot_hash_is_unicode_stable_and_tampering_fails() -> None:
    value = snapshot()
    value.assert_hash()
    tampered = value.model_copy(
        update={"agent": value.agent.model_copy(update={"name": "另一個名稱"})}
    )
    with pytest.raises(ValueError, match="snapshot_hash"):
        tampered.assert_hash()


def test_inner_workflow_hash_rejects_tamper_even_with_valid_outer_hash() -> None:
    raw = snapshot().model_dump(mode="json", exclude={"snapshot_hash"})
    raw["workflow"]["definition"]["governance"]["maxSteps"] = 19
    # Simulate a caller that recomputes only the outer envelope hash while
    # leaving the pinned Workflow artifact hash untouched.
    raw["snapshot_hash"] = canonical_json_sha256(raw)
    tampered = DirectAgentExecutionSnapshot.model_validate(raw)
    with pytest.raises(RuntimeError, match="workflow definition hash"):
        build_context(
            snapshot=tampered,
            request_context=request_context(),
            model=FakeModel([]),
            artifact_reader=FakeArtifactReader(),
        )


def test_untrusted_or_wrong_provenance_fact_becomes_unknown_and_denies() -> None:
    facts = materialize_trusted_facts(
        "pre-action",
        [
            RuntimeFactEnvelope(
                name="action.amount",
                value="999.00",
                producer="model",
                provenance="llm-inferred",
                trust_tier="inferred",
            )
        ],
    )
    assert facts == {}
    rules = {
        "version": 1,
        "rules": [
            {
                "id": "amount-gate",
                "name": "Amount gate",
                "enabled": True,
                "priority": 10,
                "when": {"fact": "action.amount", "op": "gt", "value": "1.00"},
                "then": [{"action": "allow_read_tool", "tools": ["local.calculator"]}],
                "onUnknown": [{"action": "deny", "reason": "missing verified amount"}],
            }
        ],
    }
    policy = PreActionPolicy(
        rules,
        pinned_skills=[],
        registered_tools=["local.calculator"],
        roles=["ADMIN"],
    )
    decision = policy.decide(ProposedAction(action_type="response"), [])
    assert decision.outcome == "blocked"
    assert decision.code == "rule_deny"
    assert decision.summary and decision.summary["from_unknown"] is True


@dataclass
class ScopedSearch:
    calls: list[dict[str, Any]]

    async def search(self, query: str, **kwargs: Any) -> list[Any]:
        self.calls.append({"query": query, **kwargs})
        return []


@dataclass
class SearchDeps:
    searchers: dict[str, Any]


@pytest.mark.asyncio
async def test_retrieval_uses_server_scope_and_rejects_scope_arguments() -> None:
    source_id = "11111111-1111-4111-8111-111111111111"
    run_snapshot = snapshot(
        tools=["backend.retrieval_search"], sources=[source_id]
    )
    search = ScopedSearch([])
    deps = SearchDeps(searchers={"vector": search})
    specs = effective_specs(
        run_snapshot, artifact=None, rule_tools=None, deps=deps
    )
    assert [item.name for item in specs] == ["backend.retrieval_search"]
    await invoke_direct_tool(
        name="backend.retrieval_search",
        arguments={"query": "q", "top_k": 2},
        snapshot=run_snapshot,
        artifact=None,
        rule_tools=None,
        deps=deps,
        timeout_seconds=1,
    )
    assert search.calls[0]["filters"] == {"knowledge_sources": [source_id]}
    with pytest.raises(DirectToolDenied, match="undeclared"):
        await invoke_direct_tool(
            name="backend.retrieval_search",
            arguments={
                "query": "q",
                "top_k": 2,
                "knowledge_sources": [
                    "22222222-2222-4222-8222-222222222222"
                ],
            },
            snapshot=run_snapshot,
            artifact=None,
            rule_tools=None,
            deps=deps,
            timeout_seconds=1,
        )
    assert effective_specs(
        run_snapshot, artifact=None, rule_tools=None, deps=None
    ) == []


@pytest.mark.asyncio
async def test_production_retrieval_adapter_sends_only_server_scope(
    monkeypatch,
) -> None:
    source_id = "11111111-1111-4111-8111-111111111111"
    run_snapshot = snapshot(
        tools=["backend.retrieval_search"], sources=[source_id]
    )

    class Response:
        def raise_for_status(self) -> None:
            return None

        def json(self) -> dict[str, Any]:
            return {"chunks": []}

    class Client:
        def __init__(self) -> None:
            self.calls: list[dict[str, Any]] = []

        async def post(self, path: str, **kwargs: Any) -> Response:
            self.calls.append({"path": path, **kwargs})
            return Response()

    client = Client()
    monkeypatch.setattr("app.backend_http.get_client", lambda: client)
    deps = SearchDeps(searchers={"vector": BackendVectorSearch()})
    await invoke_direct_tool(
        name="backend.retrieval_search",
        arguments={"query": "q", "top_k": 2},
        snapshot=run_snapshot,
        artifact=None,
        rule_tools=None,
        deps=deps,
        timeout_seconds=1,
    )
    assert client.calls[0]["path"] == "/api/retrieval/search"
    assert client.calls[0]["json"] == {
        "query": "q",
        "top_k": 2,
        "knowledge_sources": [source_id],
        "scope_contract_version": 1,
    }


@pytest.mark.asyncio
async def test_progressive_skill_scope_is_ephemeral_and_narrows_tools() -> None:
    run_snapshot = snapshot(
        tools=["local.calculator", "local.glossary"], with_skill=True
    )
    model = FakeModel(
        [
            RuntimeCommand(kind="load_skill", name="research-skill"),
            RuntimeCommand(kind="exit_skill"),
            RuntimeCommand(kind="final", content="done"),
        ]
    )
    reader = FakeArtifactReader(["local.calculator"])
    saver = InMemorySaver(serde=strict_serializer())
    graph = compile_runtime_graph(saver)
    ctx = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=model,
        artifact_reader=reader,
    )
    config = checkpoint_config(
        tenant_id="tenant-甲",
        user_id="admin-1",
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    result = await graph.ainvoke(
        initial_state(run_snapshot, "start"), config, context=ctx
    )
    assert result["status"] == "completed"
    assert result["active_skill_scope"] is None
    assert model.seen[0]["active_instruction"] == ""
    assert model.seen[1]["active_instruction"] == "Only use verified evidence."
    assert [tool.name for tool in model.seen[1]["tools"]] == ["local.calculator"]
    assert model.seen[2]["active_instruction"] == ""
    assert "Only use verified evidence." not in str(result["messages"])
    assert any(
        item["event_type"] == "skill_scope_entered" for item in result["events"]
    )


@pytest.mark.asyncio
async def test_waiting_input_resumes_after_graph_recreation_with_same_scope() -> None:
    run_snapshot = snapshot(tools=["local.calculator"], with_skill=True)
    saver = InMemorySaver(serde=strict_serializer())
    reader = FakeArtifactReader(["local.calculator"])
    first_model = FakeModel(
        [
            RuntimeCommand(kind="load_skill", name="research-skill"),
            RuntimeCommand(kind="request_input", content="Which period?"),
        ]
    )
    first_graph = compile_runtime_graph(saver)
    first_context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=first_model,
        artifact_reader=reader,
    )
    config = checkpoint_config(
        tenant_id="tenant-甲",
        user_id="admin-1",
        run_id=run_snapshot.run_id,
        snapshot_hash=run_snapshot.snapshot_hash,
    )
    interrupted = await first_graph.ainvoke(
        initial_state(run_snapshot, "start"), config, context=first_context
    )
    assert interrupted["__interrupt__"]
    state = await first_graph.aget_state(config)
    assert state.interrupts
    assert state.values["active_skill_scope"]["revision"] == 3

    second_model = FakeModel(
        [
            RuntimeCommand(kind="final", content="FY2025"),
            RuntimeCommand(kind="final", content="Final answer: FY2025"),
        ]
    )
    second_graph = compile_runtime_graph(saver)
    second_context = build_context(
        snapshot=run_snapshot,
        request_context=request_context(),
        model=second_model,
        artifact_reader=FakeArtifactReader(["local.calculator"]),
    )
    resumed = await second_graph.ainvoke(
        Command(resume="FY2025"), config, context=second_context
    )
    assert resumed["status"] == "completed"
    assert resumed["final_output"] == "Final answer: FY2025"
    assert second_model.seen[0]["active_skill_name"] == "research-skill"
    assert second_model.seen[1]["active_skill_name"] == ""
    assert second_model.seen[0]["active_skill_name"] == "research-skill"
    assert resumed["active_skill_scope"] is None
