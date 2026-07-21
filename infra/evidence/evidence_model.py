#!/usr/bin/env python3
"""Loopback-only deterministic OpenAI-compatible evidence model.

It deliberately records only role/length/keyed-HMAC projections of model input.
The HMAC key is process-local and is never returned by this service.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import threading
import time
import uuid
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

HMAC_KEY = os.environ.get("EVIDENCE_HMAC_KEY", "")
if len(HMAC_KEY) < 32:
    raise RuntimeError("EVIDENCE_HMAC_KEY must contain at least 32 characters")

FRAME_DELAY_SECONDS = float(os.environ.get("EVIDENCE_FRAME_DELAY_SECONDS", "0.25"))
CAPTURES: list[dict[str, Any]] = []
CAPTURES_LOCK = threading.Lock()


def keyed_hmac(value: Any) -> str:
    encoded = json.dumps(value, sort_keys=True, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    return "hmac:" + hmac.new(HMAC_KEY.encode("utf-8"), encoded, hashlib.sha256).hexdigest()


def content_projection(message: dict[str, Any]) -> dict[str, Any]:
    content = message.get("content")
    text = content if isinstance(content, str) else json.dumps(content, sort_keys=True, ensure_ascii=True)
    tool_calls = [call for call in message.get("tool_calls", []) if isinstance(call, dict)]
    def canonical_arguments_hmac(call: dict[str, Any]) -> str | None:
        arguments = (call.get("function") or {}).get("arguments")
        if not isinstance(arguments, str):
            return None
        try:
            return keyed_hmac(json.loads(arguments))
        except json.JSONDecodeError:
            return None

    return {
        "role": str(message.get("role", "unknown")),
        "contentLength": len(text),
        "contentHmac": keyed_hmac(content),
        # Markers are generated as single non-sensitive tokens. HMAC membership
        # proves a marker is absent without storing any request text.
        "tokenHmacs": sorted({keyed_hmac(token) for token in re.findall(r"[A-Za-z0-9_-]+", text)}),
        "toolCallIdHmac": keyed_hmac(message.get("tool_call_id")) if message.get("tool_call_id") else None,
        "toolCalls": [
            {
                "idHmac": keyed_hmac(call.get("id")),
                "nameHmac": keyed_hmac((call.get("function") or {}).get("name")),
                "argumentsHmac": keyed_hmac((call.get("function") or {}).get("arguments")),
                "canonicalArgumentsHmac": canonical_arguments_hmac(call),
            }
            for call in tool_calls
        ],
    }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, _format: str, *_args: object) -> None:
        # Requests may include test markers. Do not emit request paths/bodies to container logs.
        return

    def _json(self, status: int, value: Any) -> None:
        body = json.dumps(value, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json(self) -> dict[str, Any]:
        size = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(size)
        value = json.loads(raw or b"{}")
        if not isinstance(value, dict):
            raise ValueError("request must be a JSON object")
        return value

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/health":
            self._json(HTTPStatus.OK, {
                "status": "UP",
                "service": "deterministic-evidence-model",
                "frameDelaySeconds": FRAME_DELAY_SECONDS,
            })
        elif self.path == "/v1/evidence/stream":
            # Body-less controlled source for the browser ReadableStream check.
            # Platform chat/AG-UI themselves still use POST chat/completions.
            self._stream("chatcmpl-evidence-browser", {"model": "evidence-model"}, False)
        elif self.path == "/captures":
            with CAPTURES_LOCK:
                self._json(HTTPStatus.OK, {"captures": list(CAPTURES)})
        else:
            self._json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def do_DELETE(self) -> None:  # noqa: N802
        if self.path != "/captures":
            self._json(HTTPStatus.NOT_FOUND, {"error": "not found"})
            return
        with CAPTURES_LOCK:
            CAPTURES.clear()
        self._json(HTTPStatus.OK, {"cleared": True})

    def do_POST(self) -> None:  # noqa: N802
        # OpenAI-compatible clients differ on whether their configured base URL
        # already includes `/v1`; support both forms used by this repository.
        if self.path not in {"/v1/chat/completions", "/chat/completions"}:
            size = int(self.headers.get("Content-Length", "0"))
            if size:
                self.rfile.read(size)
            self._json(HTTPStatus.NOT_FOUND, {"error": "not found"})
            return
        try:
            request = self._read_json()
        except (ValueError, json.JSONDecodeError):
            self._json(HTTPStatus.BAD_REQUEST, {"error": "invalid JSON"})
            return

        messages = request.get("messages", [])
        if not isinstance(messages, list):
            self._json(HTTPStatus.BAD_REQUEST, {"error": "messages must be an array"})
            return

        capture = {
            "captureId": uuid.uuid4().hex,
            "capturedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "model": str(request.get("model", "")),
            "messageCount": len(messages),
            "messages": [content_projection(message) for message in messages if isinstance(message, dict)],
            "requestHmac": keyed_hmac(request),
        }
        with CAPTURES_LOCK:
            CAPTURES.append(capture)

        last_user = next(
            (message.get("content", "") for message in reversed(messages)
             if isinstance(message, dict) and message.get("role") == "user"),
            "",
        )
        # Test-only deterministic trigger: the runner sends the marker through
        # the real platform and must also supply a real AG-UI client tool.
        has_evidence_tool_result = any(
            isinstance(message, dict)
            and message.get("role") == "tool"
            and message.get("tool_call_id") == "call_evidence_1"
            for message in messages
        )
        wants_tool_call = (
            "__evidence_tool_call__" in str(last_user)
            and bool(request.get("tools"))
            and not has_evidence_tool_result
        )
        response_id = "chatcmpl-evidence-" + uuid.uuid4().hex
        if request.get("stream") is True:
            self._stream(response_id, request, wants_tool_call)
        else:
            self._completion(response_id, request, wants_tool_call)

    def _completion(self, response_id: str, request: dict[str, Any], wants_tool_call: bool) -> None:
        message: dict[str, Any] = {"role": "assistant", "content": "evidence model reply"}
        finish_reason = "stop"
        if wants_tool_call:
            message = self._tool_call_message(request)
            finish_reason = "tool_calls"
        self._json(HTTPStatus.OK, {
            "id": response_id,
            "object": "chat.completion",
            "created": int(time.time()),
            "model": str(request.get("model", "evidence-model")),
            "choices": [{"index": 0, "message": message, "finish_reason": finish_reason}],
        })

    def _stream(self, response_id: str, request: dict[str, Any], wants_tool_call: bool) -> None:
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        # No Content-Length is known for SSE. Close after [DONE] so HTTP/1.1
        # readers see completion instead of waiting forever for a persistent socket.
        self.send_header("Connection", "close")
        self.end_headers()
        if wants_tool_call:
            deltas = [{"role": "assistant", "tool_calls": self._tool_call_message(request)["tool_calls"]}]
            self._write_frame(response_id, request, deltas[0], "tool_calls")
        else:
            for index, text in enumerate(("evidence", " model", " reply")):
                delta: dict[str, Any] = {"content": text}
                if index == 0:
                    delta["role"] = "assistant"
                self._write_frame(response_id, request, delta, None)
                time.sleep(FRAME_DELAY_SECONDS)
            self._write_frame(response_id, request, {}, "stop")
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()
        self.close_connection = True

    def _write_frame(self, response_id: str, request: dict[str, Any], delta: dict[str, Any], finish_reason: str | None) -> None:
        frame = {
            "id": response_id,
            "object": "chat.completion.chunk",
            "created": int(time.time()),
            "model": str(request.get("model", "evidence-model")),
            "choices": [{"index": 0, "delta": delta, "finish_reason": finish_reason}],
        }
        self.wfile.write(("data: " + json.dumps(frame, separators=(",", ":")) + "\n\n").encode("utf-8"))
        self.wfile.flush()

    @staticmethod
    def _tool_call_message(request: dict[str, Any]) -> dict[str, Any]:
        tools = request.get("tools") or []
        # The real browser registers production actions in a stable order whose
        # first item creates a document. Evidence must never select it with a
        # synthetic payload: choose the existing side-effect-free view action by
        # name and provide its valid argument instead.
        selected = next(
            (
                tool for tool in tools
                if isinstance(tool, dict)
                and str((tool.get("function") or {}).get("name")) == "switchView"
            ),
            None,
        )
        name = "evidence_client_tool"
        if selected is not None:
            name = "switchView"
        return {
            "role": "assistant",
            "content": None,
            "tool_calls": [{"id": "call_evidence_1", "type": "function", "function": {"name": name, "arguments": '{"view":"documents"}'}}],
        }


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("PORT", "8080"))), Handler).serve_forever()
