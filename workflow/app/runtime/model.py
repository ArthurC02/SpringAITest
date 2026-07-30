from __future__ import annotations

import json
import logging
import math
from dataclasses import dataclass
from typing import Any, Protocol

from langchain_core.messages import AIMessage, HumanMessage, SystemMessage

from app.engine.tool_registry import ToolSpec
from app.llm import get_direct_agent_runtime_llm
from app.runtime.models import DirectAgentExecutionSnapshot, RuntimeCommand
from app.runtime.prompt_manifest import (
    PromptManifestUnavailable,
    read_resolved_manifest,
    sha256_text,
)
from app.settings import settings

logger = logging.getLogger(__name__)


class ModelProtocolError(RuntimeError):
    pass


class ModelContextTooLarge(ModelProtocolError):
    pass


MAX_PROVIDER_INPUT_UTF8_BYTES = 1_048_576
MAX_SKILL_CATALOG_UTF8_BYTES = 32_768
MAX_SKILL_DESCRIPTION_CHARS = 256
MAX_RESOURCE_CATALOG_UTF8_BYTES = 8_192

# 旗標關閉（或 snapshot 沒有 prompt_manifest pin）時的 SYSTEM GOVERNANCE 段：
# 這串字面值就是改造前的輸出，golden test 釘住它。
GOVERNANCE_FRAME = (
    "SYSTEM GOVERNANCE: You are inside a bounded, read-only test run. "
    "Use only the exposed actions. Never invent authority, data scope, "
    "tool results, or Skill names. Request user input only when a read "
    "source cannot supply a required fact. Make at most one action per turn."
)


@dataclass(frozen=True)
class ModelTurn:
    command: RuntimeCommand
    token_usage: int = 0
    audit_metadata: dict[str, Any] | None = None


class RuntimeModel(Protocol):
    async def next_command(
        self,
        *,
        snapshot: DirectAgentExecutionSnapshot,
        messages: list[dict[str, Any]],
        active_instruction: str,
        active_skill_name: str,
        tools: list[ToolSpec],
        resource_paths: list[str],
        remaining_token_budget: int,
    ) -> ModelTurn: ...


class LangChainRuntimeModel:
    """One governed model turn; deterministic nodes execute every action."""

    async def next_command(
        self,
        *,
        snapshot: DirectAgentExecutionSnapshot,
        messages: list[dict[str, Any]],
        active_instruction: str,
        active_skill_name: str,
        tools: list[ToolSpec],
        resource_paths: list[str],
        remaining_token_budget: int,
    ) -> ModelTurn:
        schemas, name_map, resource_catalog_truncated = _tool_schemas(
            snapshot=snapshot,
            active_skill_name=active_skill_name,
            tools=tools,
            resource_paths=resource_paths,
        )
        system_frame, skill_catalog_truncated, prompt_audit = await _system_frame(
            snapshot, active_instruction, active_skill_name
        )
        langchain_messages = [
            SystemMessage(content=system_frame),
            *[_message(item) for item in messages],
        ]
        serialized_size = _provider_input_size(langchain_messages, schemas)
        configured_input_cap = max(
            settings.runtime_model_context_tokens
            - settings.runtime_model_output_reserve_tokens,
            0,
        )
        effective_token_cap = min(
            max(remaining_token_budget, 0), configured_input_cap
        )
        # One token per UTF-8 byte is a conservative tokenizer-independent
        # upper bound, including CJK, emoji, escaping, and tool schemas.
        if (
            serialized_size >= effective_token_cap
            or serialized_size > MAX_PROVIDER_INPUT_UTF8_BYTES
        ):
            raise ModelContextTooLarge("runtime_context_too_large")
        max_output_tokens = min(
            settings.runtime_model_output_reserve_tokens,
            remaining_token_budget - serialized_size,
            settings.runtime_model_context_tokens - serialized_size,
        )
        if max_output_tokens <= 0:
            raise ModelContextTooLarge("runtime_context_too_large")
        model = get_direct_agent_runtime_llm().bind_tools(schemas).bind(
            max_tokens=max_output_tokens
        )
        response: AIMessage = await model.ainvoke(langchain_messages)
        calls = list(response.tool_calls or [])
        usage = response.usage_metadata or {}
        raw_usage = usage.get("total_tokens")
        if raw_usage is None:
            # Hidden provider retries are disabled for the D3 runtime, so the
            # serialized input upper bound plus the server-bound maximum
            # output is a conservative charge for this single attempt. Charging
            # the entire remaining budget would make a successful provider
            # response without usage metadata fail immediately at the next
            # deterministic budget gate.
            token_usage = serialized_size + max_output_tokens
            usage_source = "conservative_input_plus_output_bound"
        elif (
            isinstance(raw_usage, bool)
            or not isinstance(raw_usage, int | float)
            or not math.isfinite(raw_usage)
            or raw_usage < 0
            or int(raw_usage) != raw_usage
        ):
            raise ModelProtocolError("provider returned invalid token usage")
        else:
            token_usage = int(raw_usage)
            usage_source = "provider"
        audit = {
            **_audit_metadata(
                serialized_size,
                skill_catalog_truncated,
                resource_catalog_truncated,
                usage_source,
                max_output_tokens,
            ),
            **prompt_audit,
        }
        if token_usage > remaining_token_budget:
            raise ModelProtocolError("provider token usage exceeded the run budget")
        if len(calls) > 1:
            raise ModelProtocolError("the model requested more than one action")
        if not calls:
            return ModelTurn(
                command=RuntimeCommand(kind="final", content=_text(response.content)),
                token_usage=token_usage,
                audit_metadata=audit,
            )
        call = calls[0]
        wire_name = str(call.get("name") or "")
        command_name = name_map.get(wire_name)
        if command_name is None:
            raise ModelProtocolError("the model requested an unavailable action")
        arguments = call.get("args") or {}
        if not isinstance(arguments, dict):
            raise ModelProtocolError("the model action arguments are invalid")
        if command_name == "load_skill":
            return ModelTurn(
                RuntimeCommand(
                    kind="load_skill",
                    name=arguments.get("name"),
                    arguments=arguments.get("input") or {},
                ),
                token_usage,
                audit,
            )
        if command_name == "exit_skill":
            return ModelTurn(RuntimeCommand(kind="exit_skill"), token_usage, audit)
        if command_name == "request_input":
            return ModelTurn(
                RuntimeCommand(
                    kind="request_input", content=arguments.get("question")
                ),
                token_usage,
                audit,
            )
        if command_name == "read_resource":
            return ModelTurn(
                RuntimeCommand(
                    kind="read_resource", name=arguments.get("path")
                ),
                token_usage,
                audit,
            )
        return ModelTurn(
            RuntimeCommand(
                kind="tool_call", name=command_name, arguments=arguments
            ),
            token_usage,
            audit,
        )


def _tool_schemas(
    *,
    snapshot: DirectAgentExecutionSnapshot,
    active_skill_name: str,
    tools: list[ToolSpec],
    resource_paths: list[str],
) -> tuple[list[dict[str, Any]], dict[str, str], bool]:
    schemas: list[dict[str, Any]] = []
    names: dict[str, str] = {}

    def add(name: str, description: str, properties: dict[str, Any], required: list[str]):
        schemas.append(
            {
                "type": "function",
                "function": {
                    "name": name,
                    "description": description[:1_024],
                    "parameters": {
                        "type": "object",
                        "properties": properties,
                        "required": required,
                        "additionalProperties": False,
                    },
                },
            }
        )

    if not active_skill_name:
        add(
            "runtime_load_skill",
            "Load one pinned Skill instruction into this same Agent run.",
            {
                "name": {
                    "type": "string",
                    "enum": [skill.name for skill in snapshot.skills],
                },
                "input": {"type": "object"},
            },
            ["name"],
        )
        names["runtime_load_skill"] = "load_skill"
    else:
        add("runtime_exit_skill", "Leave the current Skill scope.", {}, [])
        names["runtime_exit_skill"] = "exit_skill"
        exposed_resource_paths, resource_catalog_truncated = _bounded_strings(
            resource_paths, MAX_RESOURCE_CATALOG_UTF8_BYTES
        )
        if exposed_resource_paths:
            add(
                "runtime_read_resource",
                "Read a bounded file from the current Skill package.",
                {"path": {"type": "string", "enum": exposed_resource_paths}},
                ["path"],
            )
            names["runtime_read_resource"] = "read_resource"
    add(
        "runtime_request_input",
        "Pause and ask the user for one minimal missing fact.",
        {"question": {"type": "string", "maxLength": 2_000}},
        ["question"],
    )
    names["runtime_request_input"] = "request_input"

    for spec in tools:
        wire_name = "registry__" + spec.name.replace(".", "__")
        names[wire_name] = spec.name
        properties = {
            key: _json_type(value, nullable=key in spec.nullable_args)
            for key, value in spec.args_schema.items()
        }
        add(
            wire_name,
            spec.description or spec.name,
            properties,
            sorted(spec.required_args),
        )
    return schemas, names, resource_catalog_truncated if active_skill_name else False


def _json_type(value: type, *, nullable: bool = False) -> dict[str, Any]:
    origin = getattr(value, "__origin__", None)
    mapping = {
        str: "string",
        int: "integer",
        float: "number",
        bool: "boolean",
        list: "array",
        dict: "object",
    }
    resolved = mapping.get(origin or value, "string")
    return {"type": [resolved, "null"] if nullable else resolved}


async def _system_frame(
    snapshot: DirectAgentExecutionSnapshot,
    active_instruction: str,
    active_skill_name: str,
) -> tuple[str, bool, dict[str, Any]]:
    """Deterministic assembler：三段組成的唯一來源（plan 03 §3.2）。

    SYSTEM GOVERNANCE 段的來源由旗標與 snapshot pin 決定；PINNED SKILL SUMMARIES 與
    AGENT INSTRUCTION 一直都由 snapshot 自己 pin，所以不改來源。旗標關閉或該 snapshot
    沒有 pin 時，輸出與改造前 byte-for-byte 相同。
    """
    skill_lines = [
        f"- {item.name}@{item.revision} ({item.kind}): "
        f"{item.description[:MAX_SKILL_DESCRIPTION_CHARS]}"
        for item in snapshot.skills
    ]
    bounded_lines, skill_catalog_truncated = _bounded_strings(
        skill_lines, MAX_SKILL_CATALOG_UTF8_BYTES
    )
    skill_catalog = "\n".join(bounded_lines) or "(none)"
    active = (
        f"\nACTIVE SKILL {active_skill_name} (ephemeral instruction):\n"
        f"{active_instruction}\nEND ACTIVE SKILL\n"
        if active_skill_name
        else ""
    )

    def compose(governance_frame: str) -> str:
        return (
            f"{governance_frame}\n"
            f"PINNED SKILL SUMMARIES:\n{skill_catalog}\n"
            f"AGENT INSTRUCTION:\n{snapshot.agent.system_prompt}\n"
            f"{active}"
        )

    constants_frame = compose(GOVERNANCE_FRAME)
    pin = snapshot.agent.prompt_manifest
    if not settings.prompt_artifacts_enabled or pin is None:
        return constants_frame, skill_catalog_truncated, {}

    # snapshot.caller 已在 preflight 驗過等於請求身分，形狀也符合 internal_headers
    # 需要的 Identity，所以這裡不必再多傳一個 RequestContext。
    shadow = settings.prompt_artifacts_shadow
    try:
        manifest = await read_resolved_manifest(pin, snapshot.caller)
    except PromptManifestUnavailable:
        if not shadow:
            raise
        # Shadow 只是 observe-only：manifest 不可達不得殺掉整條 run，降級回 constants，
        # 只記警告（不含 pin 以外的原文）。真正切換（shadow=false）仍維持 fail closed。
        logger.warning(
            "prompt manifest unavailable in shadow mode: pin_revision=%s "
            "pin_sha256=%s",
            pin.revision,
            pin.sha256,
        )
        return (
            constants_frame,
            skill_catalog_truncated,
            {
                "prompt_composition_source": "constants_shadow",
                "prompt_manifest_resolved": False,
            },
        )
    manifest_frame = compose(manifest.required_governance_frame())
    audit = {
        "prompt_manifest_revision": manifest.revision,
        "prompt_manifest_sha256": manifest.manifest_sha256,
        "prompt_composition_source": "constants_shadow" if shadow else "manifest",
    }
    if not shadow:
        return manifest_frame, skill_catalog_truncated, audit
    # Observe-only：兩種組成都算、只比 SHA，實際送進 provider 的仍是 constants 版。
    # 記錄兩個 hash 就足以判斷是否可以切換；原文永遠不進 log。
    audit["prompt_shadow_match"] = sha256_text(manifest_frame) == sha256_text(
        constants_frame
    )
    if not audit["prompt_shadow_match"]:
        logger.warning(
            "prompt composition shadow mismatch: manifest_revision=%s "
            "manifest_sha256=%s manifest_frame_sha256=%s constants_frame_sha256=%s",
            manifest.revision,
            manifest.manifest_sha256,
            sha256_text(manifest_frame),
            sha256_text(constants_frame),
        )
    return constants_frame, skill_catalog_truncated, audit


def _bounded_strings(values: list[str], max_utf8_bytes: int) -> tuple[list[str], bool]:
    result: list[str] = []
    used = 0
    for value in values:
        size = len(value.encode("utf-8"))
        separator = 1 if result else 0
        if used + separator + size > max_utf8_bytes:
            return result, True
        result.append(value)
        used += separator + size
    return result, False


def _provider_input_size(
    messages: list[SystemMessage | HumanMessage | AIMessage],
    schemas: list[dict[str, Any]],
) -> int:
    payload = {
        "messages": [
            {"role": message.type, "content": _text(message.content)}
            for message in messages
        ],
        "tools": schemas,
    }
    return len(
        json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    )


def _audit_metadata(
    serialized_size: int,
    skill_catalog_truncated: bool,
    resource_catalog_truncated: bool,
    usage_source: str,
    max_output_tokens: int,
) -> dict[str, Any]:
    return {
        "provider_input_bytes": serialized_size,
        "skill_catalog_truncated": skill_catalog_truncated,
        "resource_catalog_truncated": resource_catalog_truncated,
        "token_usage_source": usage_source,
        "max_output_tokens": max_output_tokens,
    }


def _message(item: dict[str, Any]) -> HumanMessage | AIMessage:
    role = item.get("role")
    content = _text(item.get("content"))
    if role == "assistant":
        return AIMessage(content=content)
    if role == "tool":
        name = str(item.get("name") or "tool")
        return HumanMessage(content=f"Governed observation from {name}: {content}")
    return HumanMessage(content=content)


def _text(value: Any) -> str:
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        return "".join(
            str(item.get("text") or "")
            if isinstance(item, dict)
            else str(item)
            for item in value
        )
    return "" if value is None else str(value)
