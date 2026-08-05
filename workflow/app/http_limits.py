"""Bound untrusted JSON bodies before FastAPI decodes route models."""

from __future__ import annotations

import json
import re
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from typing import Any

from starlette.responses import JSONResponse
from starlette.types import Message, Receive, Scope, Send

from app.business_rules.catalog import LIMITS

_SKILL_INVOKE_MAX_REQUEST_BYTES = 1_048_576
_SKILL_INVOKE_MAX_JSON_DEPTH = 32
_SKILL_INVOKE_MAX_OBJECT_FIELDS = 256
_SKILL_INVOKE_MAX_TOTAL_OBJECT_FIELDS = 256
_SKILL_INVOKE_MAX_STRING_BYTES = 256 * 1024
_SKILL_INVOKE_MAX_INTEGER_DIGITS = 4096


@dataclass(frozen=True)
class _JsonRequestPolicy:
    """Limits and error semantics for one exact set or family of JSON paths."""

    exact_paths: frozenset[str] = frozenset()
    path_pattern: re.Pattern[str] | None = None
    max_request_bytes: int = 0
    max_json_depth: int = 0
    max_object_fields: int | None = None
    max_total_object_fields: int | None = None
    max_string_bytes: int | None = None
    structural_status_code: int = 413

    def matches(self, path: str) -> bool:
        return path in self.exact_paths or (
            self.path_pattern is not None
            and self.path_pattern.fullmatch(path) is not None
        )


_POLICIES = (
    _JsonRequestPolicy(
        exact_paths=frozenset(
            {
                "/business-rules/validate",
                "/business-rules/simulate",
            }
        ),
        max_request_bytes=LIMITS["maxRequestBytes"],
        max_json_depth=LIMITS["maxJsonDepth"],
    ),
    _JsonRequestPolicy(
        # This deliberately excludes the YAML validation compatibility alias and
        # multipart package endpoint; neither has the invoke JSON contract.
        path_pattern=re.compile(r"/skills/[^/]+/invoke"),
        max_request_bytes=_SKILL_INVOKE_MAX_REQUEST_BYTES,
        max_json_depth=_SKILL_INVOKE_MAX_JSON_DEPTH,
        max_object_fields=_SKILL_INVOKE_MAX_OBJECT_FIELDS,
        max_total_object_fields=_SKILL_INVOKE_MAX_TOTAL_OBJECT_FIELDS,
        max_string_bytes=_SKILL_INVOKE_MAX_STRING_BYTES,
        structural_status_code=422,
    ),
)


@dataclass(frozen=True)
class _LimitViolation(Exception):
    code: str
    message: str


class _IntegerDigitsExceeded(ValueError):
    pass


def _json_depth_exceeds(body: bytes, maximum: int) -> bool:
    """Scan JSON structural depth without parsing or inspecting string contents."""
    depth = 0
    in_string = False
    escaped = False
    for byte in body:
        if in_string:
            if escaped:
                escaped = False
            elif byte == 0x5C:  # backslash
                escaped = True
            elif byte == 0x22:  # double quote
                in_string = False
            continue
        if byte == 0x22:
            in_string = True
        elif byte in (0x5B, 0x7B):  # [ {
            depth += 1
            if depth > maximum:
                return True
        elif byte in (0x5D, 0x7D):  # ] }
            depth = max(0, depth - 1)
    return False


def _json_structure_violation(
    body: bytes, policy: _JsonRequestPolicy
) -> _LimitViolation | None:
    if _json_depth_exceeds(body, policy.max_json_depth):
        return _LimitViolation("json_depth_exceeded", "JSON nesting is too deep.")

    if (
        policy.max_object_fields is None
        and policy.max_total_object_fields is None
        and policy.max_string_bytes is None
    ):
        return None

    total_object_fields = 0

    def string_too_long(value: str) -> bool:
        assert policy.max_string_bytes is not None
        return (
            len(value.encode("utf-8", errors="surrogatepass"))
            > policy.max_string_bytes
        )

    def bounded_object(pairs: list[tuple[str, Any]]) -> list[tuple[str, Any]]:
        nonlocal total_object_fields
        field_count = len(pairs)
        if (
            policy.max_object_fields is not None
            and field_count > policy.max_object_fields
        ):
            raise _LimitViolation(
                "object_fields_exceeded", "A JSON object has too many fields."
            )
        total_object_fields += field_count
        if (
            policy.max_total_object_fields is not None
            and total_object_fields > policy.max_total_object_fields
        ):
            raise _LimitViolation(
                "total_object_fields_exceeded",
                "The JSON body has too many object fields.",
            )
        # Preserve pairs instead of building a dict: duplicate values must remain
        # visible to the string scan, and this bounded preparse does not need a map.
        return pairs

    def bounded_integer(raw: str) -> int:
        digits = raw[1:] if raw.startswith("-") else raw
        if len(digits) > _SKILL_INVOKE_MAX_INTEGER_DIGITS:
            raise _IntegerDigitsExceeded
        return int(raw)

    try:
        parsed = json.loads(
            body,
            object_pairs_hook=bounded_object,
            parse_int=bounded_integer,
        )
    except _LimitViolation as exc:
        return exc
    except (json.JSONDecodeError, UnicodeDecodeError):
        # FastAPI owns ordinary syntax/type errors; this layer only enforces limits.
        return None
    except _IntegerDigitsExceeded:
        return _LimitViolation(
            "json_structure_invalid", "JSON structure is invalid."
        )

    if policy.max_string_bytes is None:
        return None

    pending = [parsed]
    while pending:
        value = pending.pop()
        if isinstance(value, str):
            if string_too_long(value):
                return _LimitViolation(
                    "string_too_long", "A JSON string is too long."
                )
        elif isinstance(value, (list, tuple)):
            pending.extend(value)
    return None


class JsonRequestLimitMiddleware:
    """Apply the matching JSON path policy before FastAPI's decoder runs."""

    def __init__(self, app: Callable[..., Awaitable[Any]]) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        policy = self._policy_for(scope)
        if policy is None:
            await self.app(scope, receive, send)
            return

        content_length = next(
            (
                value
                for name, value in scope.get("headers", ())
                if name.lower() == b"content-length"
            ),
            None,
        )
        if content_length is not None:
            try:
                if int(content_length) > policy.max_request_bytes:
                    await self._reject(
                        scope,
                        receive,
                        send,
                        413,
                        _LimitViolation(
                            "request_too_large", "Request body is too large."
                        ),
                    )
                    return
            except ValueError:
                pass

        buffered = bytearray()
        while True:
            message = await receive()
            if message["type"] == "http.disconnect":
                return
            if message["type"] != "http.request":
                continue
            chunk = message.get("body", b"")
            if len(chunk) > policy.max_request_bytes - len(buffered):
                await self._reject(
                    scope,
                    receive,
                    send,
                    413,
                    _LimitViolation(
                        "request_too_large", "Request body is too large."
                    ),
                )
                return
            buffered.extend(chunk)
            if not message.get("more_body", False):
                break

        body = bytes(buffered)
        buffered.clear()
        violation = _json_structure_violation(body, policy)
        if violation is not None:
            await self._reject(
                scope,
                receive,
                send,
                policy.structural_status_code,
                violation,
            )
            return

        replayed = False

        async def replay() -> Message:
            nonlocal replayed
            if replayed:
                return {"type": "http.disconnect"}
            replayed = True
            return {"type": "http.request", "body": body, "more_body": False}

        await self.app(scope, replay, send)

    @staticmethod
    def _policy_for(scope: Scope) -> _JsonRequestPolicy | None:
        if scope["type"] != "http" or scope.get("method") != "POST":
            return None
        path = scope.get("path", "")
        return next((policy for policy in _POLICIES if policy.matches(path)), None)

    @staticmethod
    async def _reject(
        scope: Scope,
        receive: Receive,
        send: Send,
        status_code: int,
        violation: _LimitViolation,
    ) -> None:
        detail: dict[str, Any] = {
            "error": violation.code,
            "message": violation.message,
        }
        if status_code == 422:
            detail["field_errors"] = {"request": violation.message}
        response = JSONResponse(status_code=status_code, content={"detail": detail})
        await response(scope, receive, send)
