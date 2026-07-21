#!/usr/bin/env python3
"""A transparent, loopback-published workflow proxy for evidence runs only.

Captured records intentionally contain no request body or identity headers: only
the routed skill name, input keys, and an HMAC over the original body are kept.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import threading
import time
import urllib.error
import urllib.request
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

UPSTREAM = os.environ.get("WORKFLOW_UPSTREAM_URL", "http://workflow:8000").rstrip("/")
HMAC_KEY = os.environ.get("EVIDENCE_HMAC_KEY", "")
if len(HMAC_KEY) < 32:
    raise RuntimeError("EVIDENCE_HMAC_KEY must contain at least 32 characters")

CAPTURES: list[dict[str, Any]] = []
LOCK = threading.Lock()
SKILL_PATH = re.compile(r"^/skills/([^/]+)/invoke$")
HOP_BY_HOP = {"connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailers", "transfer-encoding", "upgrade"}


def hash_body(body: bytes) -> str:
    return "hmac:" + hmac.new(HMAC_KEY.encode("utf-8"), body, hashlib.sha256).hexdigest()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, _format: str, *_args: object) -> None:
        return

    def _json(self, status: int, value: Any) -> None:
        body = json.dumps(value, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/health":
            self._json(HTTPStatus.OK, {"status": "UP", "service": "workflow-capture-proxy"})
            return
        if self.path == "/captures":
            with LOCK:
                self._json(HTTPStatus.OK, {"captures": list(CAPTURES)})
            return
        self._forward(b"")

    def do_DELETE(self) -> None:  # noqa: N802
        if self.path != "/captures":
            self._forward(b"")
            return
        with LOCK:
            CAPTURES.clear()
        self._json(HTTPStatus.OK, {"cleared": True})

    def do_POST(self) -> None:  # noqa: N802
        body = self._read_body()
        capture = self._capture(body)
        self._forward(body, capture)

    def do_PUT(self) -> None:  # noqa: N802
        body = self._read_body()
        capture = self._capture(body)
        self._forward(body, capture)

    def _read_body(self) -> bytes:
        if "chunked" not in self.headers.get("Transfer-Encoding", "").lower():
            return self.rfile.read(int(self.headers.get("Content-Length", "0")))

        chunks: list[bytes] = []
        while True:
            size_line = self.rfile.readline().strip()
            if not size_line:
                raise ValueError("missing chunk size")
            size = int(size_line.split(b";", 1)[0], 16)
            if size == 0:
                # Consume optional trailers and their terminating blank line.
                while self.rfile.readline().strip():
                    pass
                return b"".join(chunks)
            chunks.append(self.rfile.read(size))
            if self.rfile.read(2) != b"\r\n":
                raise ValueError("invalid chunk terminator")

    def _capture(self, body: bytes) -> dict[str, Any] | None:
        match = SKILL_PATH.match(self.path)
        if not match:
            return None
        input_keys: list[str] = []
        try:
            payload = json.loads(body)
            if isinstance(payload, dict) and isinstance(payload.get("input"), dict):
                input_keys = sorted(str(key) for key in payload["input"].keys())
        except json.JSONDecodeError:
            input_keys = ["<invalid-json>"]
        record = {
            "capturedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "method": self.command,
            "skillName": match.group(1),
            "inputKeys": input_keys,
            "inputHmac": hash_body(body),
        }
        with LOCK:
            CAPTURES.append(record)
        return record

    @staticmethod
    def _complete_capture(record: dict[str, Any] | None, status: int, response_body: bytes) -> None:
        if record is None:
            return
        try:
            json.loads(response_body)
            json_valid = True
        except (UnicodeDecodeError, json.JSONDecodeError):
            json_valid = False
        with LOCK:
            record["upstreamStatus"] = status
            record["upstreamJsonValid"] = json_valid

    def _forward(self, body: bytes, capture: dict[str, Any] | None = None) -> None:
        headers = {key: value for key, value in self.headers.items() if key.lower() not in HOP_BY_HOP | {"host", "content-length"}}
        if body:
            headers["Content-Length"] = str(len(body))
        request = urllib.request.Request(UPSTREAM + self.path, data=body if self.command in {"POST", "PUT", "PATCH"} else None, headers=headers, method=self.command)
        try:
            with urllib.request.urlopen(request, timeout=150) as upstream:
                response_body = upstream.read()
                self._complete_capture(capture, upstream.status, response_body)
                self.send_response(upstream.status)
                for key, value in upstream.headers.items():
                    if key.lower() not in HOP_BY_HOP | {"content-length"}:
                        self.send_header(key, value)
                self.send_header("Content-Length", str(len(response_body)))
                self.end_headers()
                self.wfile.write(response_body)
        except urllib.error.HTTPError as error:
            response_body = error.read()
            self._complete_capture(capture, error.code, response_body)
            self.send_response(error.code)
            for key, value in error.headers.items():
                if key.lower() not in HOP_BY_HOP | {"content-length"}:
                    self.send_header(key, value)
            self.send_header("Content-Length", str(len(response_body)))
            self.end_headers()
            self.wfile.write(response_body)
        except urllib.error.URLError:
            self._complete_capture(capture, HTTPStatus.BAD_GATEWAY, b'{"error":"workflow upstream unavailable"}')
            self._json(HTTPStatus.BAD_GATEWAY, {"error": "workflow upstream unavailable"})


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("PORT", "8081"))), Handler).serve_forever()
