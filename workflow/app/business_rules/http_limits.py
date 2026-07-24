"""Transport limits for the untrusted Business Rule JSON endpoints."""

from __future__ import annotations

from collections.abc import Awaitable, Callable
from typing import Any

from starlette.responses import JSONResponse
from starlette.types import Message, Receive, Scope, Send

from app.business_rules.catalog import LIMITS

_RULE_PATHS = frozenset(
    {
        "/business-rules/validate",
        "/business-rules/simulate",
    }
)


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


class BusinessRuleRequestLimitMiddleware:
    """Bound body bytes and JSON nesting before FastAPI's JSON decoder runs."""

    def __init__(self, app: Callable[..., Awaitable[Any]]) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if (
            scope["type"] != "http"
            or scope.get("method") != "POST"
            or scope.get("path") not in _RULE_PATHS
        ):
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
                if int(content_length) > LIMITS["maxRequestBytes"]:
                    await self._reject(
                        scope,
                        receive,
                        send,
                        "request_too_large",
                        "Request body is too large.",
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
            buffered.extend(message.get("body", b""))
            if len(buffered) > LIMITS["maxRequestBytes"]:
                await self._reject(
                    scope,
                    receive,
                    send,
                    "request_too_large",
                    "Request body is too large.",
                )
                return
            if not message.get("more_body", False):
                break

        body = bytes(buffered)
        if _json_depth_exceeds(body, LIMITS["maxJsonDepth"]):
            await self._reject(
                scope,
                receive,
                send,
                "json_depth_exceeded",
                "JSON nesting is too deep.",
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
    async def _reject(
        scope: Scope,
        receive: Receive,
        send: Send,
        code: str,
        message: str,
    ) -> None:
        response = JSONResponse(
            status_code=413,
            content={"detail": {"error": code, "message": message}},
        )
        await response(scope, receive, send)
