from __future__ import annotations

import hashlib
import base64
import binascii
import json
import math
import re
import uuid
from decimal import Decimal
from typing import Any, Literal, TypedDict

from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    PrivateAttr,
    field_validator,
    model_validator,
)
from app.runtime.output_contract import validate_output_contract

_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
_GROUP_ID_RE = re.compile(r"^[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?$")
MAX_CALLER_GROUPS_JOINED_UTF8_BYTES = 2_048
MAX_BACKEND_RESULT_JSON_BYTES = 1024 * 1024
MAX_SNAPSHOT_CANONICAL_BYTES = 8 * 1024 * 1024
MAX_SNAPSHOT_CANONICAL_BASE64_CHARS = (
    4 * ((MAX_SNAPSHOT_CANONICAL_BYTES + 2) // 3)
)


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class AgentRuntimeLimits(StrictModel):
    """Snapshot limits. Zero means "use the server's conservative default"."""

    max_tool_rounds: int = Field(default=0, ge=0, le=1_000)
    max_context_rounds: int = Field(default=0, ge=0, le=1_000)
    timeout_seconds: int = Field(default=0, ge=0, le=86_400)
    effective_timeout_seconds: int | None = Field(
        default=None,
        ge=1,
        le=86_400,
        exclude_if=lambda value: value is None,
    )
    token_budget: int = Field(default=0, ge=0, le=10_000_000)
    step_budget: int = Field(default=0, ge=0, le=10_000)


class AgentExecutionSnapshot(StrictModel):
    id: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=1)
    name: str = Field(min_length=1, max_length=256)
    system_prompt: str = Field(max_length=200_000)
    execution_roles: list[str] = Field(default_factory=list, max_length=16)
    audience: list[str] = Field(default_factory=list, max_length=256)
    output_contract: dict[str, Any] = Field(default_factory=dict)
    business_rules: dict[str, Any] = Field(default_factory=dict)
    allowed_tools: list[str] = Field(default_factory=list, max_length=256)
    knowledge_sources: list[str] = Field(default_factory=list, max_length=512)
    runtime_limits: AgentRuntimeLimits = Field(default_factory=AgentRuntimeLimits)

    @field_validator(
        "execution_roles", "audience", "allowed_tools", "knowledge_sources"
    )
    @classmethod
    def unique_nonblank(cls, values: list[str]) -> list[str]:
        if any(not item.strip() for item in values):
            raise ValueError("list values must not be blank")
        if len(set(values)) != len(values):
            raise ValueError("list values must be unique")
        return values

    @field_validator("audience")
    @classmethod
    def audience_entries_are_namespaced(cls, values: list[str]) -> list[str]:
        for value in values:
            if value in {"ADMIN", "USER"}:
                continue
            if value.startswith("role:"):
                subject = value[5:]
                if subject not in {"ADMIN", "USER"}:
                    raise ValueError("audience role is unsupported")
            elif value.startswith("group:"):
                subject = value[6:]
                if not _GROUP_ID_RE.fullmatch(subject):
                    raise ValueError("audience group is not canonical")
            else:
                raise ValueError("audience entries must use role: or group:")
            if (
                not subject
                or subject != subject.strip()
                or any(character.isspace() for character in subject)
                or "*" in subject
                or len(subject) > 128
            ):
                raise ValueError("audience entry has an invalid subject")
        return values

    @field_validator("output_contract")
    @classmethod
    def output_contract_is_supported(cls, value: dict[str, Any]) -> dict[str, Any]:
        validate_output_contract(value)
        return value


class WorkflowExecutionSnapshot(StrictModel):
    id: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=1)
    definition: dict[str, Any]
    definition_sha256: str
    compiler_contract_version: str = Field(min_length=1, max_length=128)

    @field_validator("definition_sha256")
    @classmethod
    def hash_is_lower_hex(cls, value: str) -> str:
        if not _SHA256_RE.fullmatch(value):
            raise ValueError("definition_sha256 must be a lowercase SHA-256")
        return value


class PinnedSkillSummary(StrictModel):
    name: str = Field(min_length=1, max_length=64)
    revision: int = Field(ge=1)
    kind: Literal["agentic", "flow"]
    description: str = Field(default="", max_length=4_096)
    definition_sha256: str
    package_sha256: str | None = None
    # This is display-only in D3. Exact artifact metadata is authoritative.
    allowed_tools: list[str] = Field(default_factory=list, max_length=256)

    @field_validator("definition_sha256")
    @classmethod
    def definition_hash_is_lower_hex(cls, value: str) -> str:
        if not _SHA256_RE.fullmatch(value):
            raise ValueError("definition_sha256 must be a lowercase SHA-256")
        return value

    @field_validator("package_sha256")
    @classmethod
    def package_hash_is_lower_hex(cls, value: str | None) -> str | None:
        if value is not None and not _SHA256_RE.fullmatch(value):
            raise ValueError("package_sha256 must be a lowercase SHA-256")
        return value


class CallerExecutionSnapshot(StrictModel):
    tenant_id: str = Field(min_length=1, max_length=256)
    user_id: str = Field(min_length=1, max_length=256)
    role: str = Field(min_length=1, max_length=64)
    groups: list[str] = Field(default_factory=list, max_length=256)
    tool_grants: list[str] = Field(default_factory=list, max_length=256)
    knowledge_source_grants: list[str] = Field(default_factory=list, max_length=512)

    @field_validator("groups", "tool_grants", "knowledge_source_grants")
    @classmethod
    def grants_are_unique_nonblank(cls, values: list[str]) -> list[str]:
        if any(not item.strip() for item in values):
            raise ValueError("grant values must not be blank")
        if len(set(values)) != len(values):
            raise ValueError("grant values must be unique")
        return values

    @field_validator("groups")
    @classmethod
    def groups_are_sorted_canonical_ids(cls, values: list[str]) -> list[str]:
        if any(not _GROUP_ID_RE.fullmatch(value) for value in values):
            raise ValueError("caller groups must be canonical raw group IDs")
        if values != sorted(values):
            raise ValueError("caller groups must be sorted")
        if (
            len(" ".join(values).encode("utf-8"))
            > MAX_CALLER_GROUPS_JOINED_UTF8_BYTES
        ):
            raise ValueError("caller groups exceed their joined UTF-8 limit")
        return values

    @field_validator("knowledge_source_grants")
    @classmethod
    def data_grants_are_canonical_ids(cls, values: list[str]) -> list[str]:
        for value in values:
            try:
                parsed = uuid.UUID(value)
            except ValueError as exc:
                raise ValueError(
                    "knowledge-source grants must be canonical UUIDs"
                ) from exc
            if str(parsed) != value:
                raise ValueError(
                    "knowledge-source grants must be lowercase canonical UUIDs"
                )
        return values


class DirectAgentExecutionSnapshot(StrictModel):
    """The immutable Backend-produced execution authority for one run."""

    run_id: str = Field(min_length=1, max_length=128)
    snapshot_hash: str
    agent: AgentExecutionSnapshot
    workflow: WorkflowExecutionSnapshot
    skills: list[PinnedSkillSummary] = Field(default_factory=list, max_length=128)
    caller: CallerExecutionSnapshot
    mode: Literal["test"]
    execution_kind: Literal[
        "direct-worker", "orchestrator-worker", "orchestrator-verifier"
    ] = Field(
        default="direct-worker",
        exclude_if=lambda value: value == "direct-worker",
    )
    orchestrator_token_cap: int | None = Field(
        default=None, ge=1, le=1_000_000,
        exclude_if=lambda value: value is None,
    )
    _canonical_source: dict[str, Any] | None = PrivateAttr(default=None)
    _canonical_bytes: bytes | None = PrivateAttr(default=None)

    @field_validator("snapshot_hash")
    @classmethod
    def snapshot_hash_is_lower_hex(cls, value: str) -> str:
        if not _SHA256_RE.fullmatch(value):
            raise ValueError("snapshot_hash must be a lowercase SHA-256")
        return value

    @model_validator(mode="after")
    def validate_pins(self) -> "DirectAgentExecutionSnapshot":
        keys = [(skill.name, skill.revision) for skill in self.skills]
        if len(keys) != len(set(keys)):
            raise ValueError("skill revision pins must be unique")
        required_role = (
            "verifier"
            if self.execution_kind == "orchestrator-verifier"
            else "worker"
        )
        if (
            self.execution_kind != "direct-worker"
            and self.orchestrator_token_cap is None
        ):
            raise ValueError(
                "orchestrator child snapshot requires its reserved token cap"
            )
        if required_role not in self.agent.execution_roles:
            raise ValueError(
                f"{self.execution_kind} runs require {required_role} execution eligibility"
            )
        audience = set(self.agent.audience)
        role_allowed = (
            f"role:{self.caller.role}" in audience
            or self.caller.role in {"ADMIN", "USER"} and self.caller.role in audience
        )
        group_allowed = any(f"group:{group}" in audience for group in self.caller.groups)
        if not role_allowed and not group_allowed:
            raise ValueError("caller role is outside this Agent revision's audience")
        effective_sources = set(self.agent.knowledge_sources) & set(
            self.caller.knowledge_source_grants
        )
        if len(effective_sources) > len(self.agent.knowledge_sources):
            raise ValueError("invalid knowledge-source authority")
        return self

    def canonical_payload(self) -> dict[str, Any]:
        if self._canonical_source is not None:
            return {
                key: value
                for key, value in self._canonical_source.items()
                if key != "snapshot_hash"
            }
        return self.model_dump(mode="python", exclude={"snapshot_hash"})

    def computed_hash(self) -> str:
        if self._canonical_bytes is not None:
            return hashlib.sha256(self._canonical_bytes).hexdigest()
        return canonical_json_sha256(self.canonical_payload())

    def assert_hash(self) -> None:
        if self.computed_hash() != self.snapshot_hash:
            raise ValueError("snapshot_hash does not match canonical snapshot")

    def skill_pin(self, name: str) -> PinnedSkillSummary | None:
        return next((skill for skill in self.skills if skill.name == name), None)

    @classmethod
    def from_preserved_json(
        cls, raw: dict[str, Any]
    ) -> "DirectAgentExecutionSnapshot":
        validated = cls.model_validate(_validation_json_value(raw))
        validated._canonical_source = raw
        return validated

    @classmethod
    def from_canonical_base64(
        cls, value: str, snapshot_hash: str
    ) -> "DirectAgentExecutionSnapshot":
        if (
            not isinstance(value, str)
            or not value
            or len(value) > MAX_SNAPSHOT_CANONICAL_BASE64_CHARS
        ):
            raise ValueError("snapshot canonical payload exceeds its limit")
        try:
            raw = base64.b64decode(value, validate=True)
        except (binascii.Error, ValueError) as exc:
            raise ValueError("snapshot canonical payload is not valid base64") from exc
        if len(raw) > MAX_SNAPSHOT_CANONICAL_BYTES:
            raise ValueError("snapshot canonical payload exceeds its limit")
        if base64.b64encode(raw).decode("ascii") != value:
            raise ValueError("snapshot canonical payload is not canonical base64")
        if hashlib.sha256(raw).hexdigest() != snapshot_hash:
            raise ValueError("snapshot canonical payload hash does not match")
        parsed = parse_json_preserving_numbers(raw)
        if not isinstance(parsed, dict) or "snapshot_hash" in parsed:
            raise ValueError("snapshot canonical payload has an invalid shape")
        envelope = dict(parsed)
        envelope["snapshot_hash"] = snapshot_hash
        validated = cls.from_preserved_json(envelope)
        validated._canonical_bytes = raw
        return validated


class RawNumberToken(str):
    """A syntactically validated JSON number lexeme."""


def parse_json_preserving_numbers(raw: bytes | str) -> Any:
    return json.loads(
        raw,
        parse_float=RawNumberToken,
        parse_int=RawNumberToken,
    )


def _validation_json_value(value: Any) -> Any:
    if isinstance(value, RawNumberToken):
        return Decimal(value) if any(c in value for c in ".eE") else int(value)
    if isinstance(value, dict):
        return {key: _validation_json_value(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_validation_json_value(item) for item in value]
    return value


def canonical_json_bytes(value: Any) -> bytes:
    """Cross-service canonical JSON using .NET StringComparer.Ordinal keys."""
    return _write_canonical_json(value).encode("utf-8")


def _utf16_ordinal_key(value: str) -> bytes:
    # Big-endian bytes preserve lexicographic UTF-16 code-unit ordering.
    return value.encode("utf-16-be", errors="surrogatepass")


def _write_canonical_json(value: Any) -> str:
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, RawNumberToken):
        return str(value)
    if isinstance(value, int):
        return str(value)
    if isinstance(value, Decimal):
        if not value.is_finite():
            raise ValueError("canonical JSON numbers must be finite")
        return str(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("canonical JSON numbers must be finite")
        return json.dumps(value, allow_nan=False)
    if isinstance(value, str):
        return json.dumps(value, ensure_ascii=False)
    if isinstance(value, dict):
        if any(not isinstance(key, str) for key in value):
            raise TypeError("canonical JSON object keys must be strings")
        return "{" + ",".join(
            f"{json.dumps(key, ensure_ascii=False)}:{_write_canonical_json(value[key])}"
            for key in sorted(value, key=_utf16_ordinal_key)
        ) + "}"
    if isinstance(value, list | tuple):
        return "[" + ",".join(_write_canonical_json(item) for item in value) + "]"
    raise TypeError(f"unsupported canonical JSON value: {type(value).__name__}")


def canonical_json_sha256(value: Any) -> str:
    return hashlib.sha256(canonical_json_bytes(value)).hexdigest()


def backend_result_wire_size(result: dict[str, Any]) -> int:
    """Match httpx's compact UTF-8 JSON request encoding."""
    return len(
        json.dumps(
            result,
            ensure_ascii=False,
            separators=(",", ":"),
            allow_nan=False,
        ).encode("utf-8")
    )


class SnapshotCanonicalEnvelope(StrictModel):
    snapshot_hash: str
    snapshot_canonical_base64: str = Field(min_length=1)

    @field_validator("snapshot_hash")
    @classmethod
    def hash_is_lower_hex(cls, value: str) -> str:
        if not _SHA256_RE.fullmatch(value):
            raise ValueError("snapshot_hash must be a lowercase SHA-256")
        return value

    def decode(self) -> DirectAgentExecutionSnapshot:
        return DirectAgentExecutionSnapshot.from_canonical_base64(
            self.snapshot_canonical_base64,
            self.snapshot_hash,
        )


class StartRunRequest(StrictModel):
    command_id: str = Field(min_length=1, max_length=128)


class ResumeRunRequest(StrictModel):
    command_id: str = Field(min_length=1, max_length=128)


class CancelRunRequest(StrictModel):
    command_id: str = Field(min_length=1, max_length=128)


class RuntimeCommand(StrictModel):
    """Single model-selected action. At most one is accepted per model step."""

    kind: Literal[
        "load_skill",
        "exit_skill",
        "tool_call",
        "read_resource",
        "request_input",
        "final",
    ]
    name: str | None = Field(default=None, max_length=128)
    arguments: dict[str, Any] = Field(default_factory=dict)
    content: str | None = Field(default=None, max_length=200_000)


class ActiveSkillScope(StrictModel):
    name: str
    revision: int
    kind: Literal["agentic", "flow"]
    definition_sha256: str
    package_sha256: str | None = None
    instruction_sha256: str
    effective_tools: list[str]
    resource_paths: list[str]


class RuntimeState(TypedDict, total=False):
    """Checkpointed values only. Every member is JSON/msgpack safe."""

    run_id: str
    snapshot_hash: str
    messages: list[dict[str, Any]]
    pending_command: dict[str, Any] | None
    pending_input: dict[str, Any] | None
    pending_approval: dict[str, Any] | None
    active_skill_scope: dict[str, Any] | None
    step_count: int
    tool_rounds: int
    context_rounds: int
    context_acquisition_attempts: int
    verified_context: dict[str, Any] | None
    estimated_tokens: int
    status: str
    final_output: str | None
    error_code: str | None
    audit_tags: list[str]
    response_policies: list[str]
    last_rule_decision: dict[str, Any] | None
    rule_allowed_tools: list[str] | None
    checkpoint_version: int | None
    tool_fingerprints: list[str]
    events: list[dict[str, Any]]
    event_cursor_base: int


class RuntimeRunResult(StrictModel):
    run_id: str
    status: Literal[
        "accepted",
        "running",
        "waiting_input",
        "waiting_approval",
        "completed",
        "failed",
        "cancelled",
    ]
    snapshot_hash: str
    checkpoint_version: int | None = None
    message: str | None = None
