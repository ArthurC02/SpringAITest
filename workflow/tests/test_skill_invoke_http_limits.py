"""Raw request limits for the unified Skill invoke route."""

from __future__ import annotations

import asyncio
import json
from collections.abc import Iterable

import pytest
from fastapi.testclient import TestClient

from app.http_limits import JsonRequestLimitMiddleware
from app import http_limits
from app.main import app
from tests.conftest import auth_headers

MAX_REQUEST_BYTES = 1_048_576
MAX_JSON_DEPTH = 32
MAX_OBJECT_FIELDS = 256
MAX_STRING_BYTES = 256 * 1024

client = TestClient(app)


def _run_middleware(
    body_chunks: Iterable[bytes],
    *,
    path: str = "/skills/probe/invoke",
    content_length: int | None = None,
) -> tuple[bool, bytes, list[dict]]:
    chunks = list(body_chunks)
    messages = [
        {
            "type": "http.request",
            "body": chunk,
            "more_body": index < len(chunks) - 1,
        }
        for index, chunk in enumerate(chunks)
    ]
    called = False
    replayed_body = b""
    sent: list[dict] = []

    async def downstream(scope, receive, send):  # noqa: ANN001
        nonlocal called, replayed_body
        called = True
        replayed_body = (await receive()).get("body", b"")

    async def receive():
        return messages.pop(0)

    async def send(message):
        sent.append(message)

    headers = []
    if content_length is not None:
        headers.append((b"content-length", str(content_length).encode("ascii")))
    asyncio.run(
        JsonRequestLimitMiddleware(downstream)(
            {
                "type": "http",
                "method": "POST",
                "path": path,
                "headers": headers,
            },
            receive,
            send,
        )
    )
    return called, replayed_body, sent


def _response(sent: list[dict]) -> tuple[int, dict]:
    start = next(message for message in sent if message["type"] == "http.response.start")
    body = next(message for message in sent if message["type"] == "http.response.body")
    return start["status"], json.loads(body["body"])


def test_content_length_accepts_n_and_rejects_n_plus_one_before_receive():
    exact = b"{}" + b" " * (MAX_REQUEST_BYTES - 2)

    called, replayed, sent = _run_middleware(
        [exact], content_length=MAX_REQUEST_BYTES
    )
    assert called is True
    assert replayed == exact
    assert sent == []

    called, _, sent = _run_middleware(
        [b"must not be read"], content_length=MAX_REQUEST_BYTES + 1
    )
    status, body = _response(sent)
    assert called is False
    assert status == 413
    assert body["detail"]["error"] == "request_too_large"


def test_chunked_body_accepts_n_and_rejects_n_plus_one():
    exact = b"{}" + b" " * (MAX_REQUEST_BYTES - 2)

    called, replayed, sent = _run_middleware([exact[:-1], exact[-1:]])
    assert called is True
    assert replayed == exact
    assert sent == []

    called, _, sent = _run_middleware([exact, b" "])
    status, body = _response(sent)
    assert called is False
    assert status == 413
    assert body["detail"]["error"] == "request_too_large"


def test_json_depth_accepts_32_and_rejects_33():
    at_limit = b"[" * MAX_JSON_DEPTH + b"0" + b"]" * MAX_JSON_DEPTH
    over_limit = b"[" * (MAX_JSON_DEPTH + 1) + b"0" + b"]" * (
        MAX_JSON_DEPTH + 1
    )

    called, _, sent = _run_middleware([at_limit])
    assert called is True
    assert sent == []

    called, _, sent = _run_middleware([over_limit])
    status, body = _response(sent)
    assert called is False
    assert status == 422
    assert body["detail"]["error"] == "json_depth_exceeded"
    assert set(body["detail"]["field_errors"]) == {"request"}


def test_object_field_limits_cover_per_object_and_total_boundaries():
    one_object_at_limit = json.dumps(
        {f"k{index}": index for index in range(MAX_OBJECT_FIELDS)}
    ).encode()
    one_object_over_limit = json.dumps(
        {f"k{index}": index for index in range(MAX_OBJECT_FIELDS + 1)}
    ).encode()

    assert _run_middleware([one_object_at_limit])[0] is True
    called, _, sent = _run_middleware([one_object_over_limit])
    status, body = _response(sent)
    assert called is False
    assert status == 422
    assert body["detail"]["error"] == "object_fields_exceeded"

    total_at_limit = json.dumps(
        {
            "left": {f"l{index}": index for index in range(127)},
            "right": {f"r{index}": index for index in range(127)},
        }
    ).encode()
    total_over_limit = json.dumps(
        {
            "left": {f"l{index}": index for index in range(127)},
            "right": {f"r{index}": index for index in range(128)},
        }
    ).encode()

    assert _run_middleware([total_at_limit])[0] is True
    called, _, sent = _run_middleware([total_over_limit])
    status, body = _response(sent)
    assert called is False
    assert status == 422
    assert body["detail"]["error"] == "total_object_fields_exceeded"


def test_single_string_accepts_256_kib_and_rejects_next_byte():
    at_limit = json.dumps({"input": {"value": "x" * MAX_STRING_BYTES}}).encode()
    over_limit = json.dumps(
        {"input": {"value": "x" * (MAX_STRING_BYTES + 1)}}
    ).encode()

    assert _run_middleware([at_limit])[0] is True
    called, _, sent = _run_middleware([over_limit])
    status, body = _response(sent)
    assert called is False
    assert status == 422
    assert body["detail"]["error"] == "string_too_long"


def test_duplicate_key_cannot_hide_an_oversized_string():
    body = (
        b'{"input":{"value":["'
        + b"x" * (MAX_STRING_BYTES + 1)
        + b'"],"value":"small"}}'
    )

    called, _, sent = _run_middleware([body])
    status, response = _response(sent)
    assert called is False
    assert status == 422
    assert response["detail"]["error"] == "string_too_long"


def test_extreme_integer_decoder_value_error_is_bounded_422_without_raw_value():
    digits = "7" * 5000
    body = f'{{"input":{{"value":{digits}}}}}'.encode()

    called, _, sent = _run_middleware([body])
    status, response = _response(sent)
    rendered = json.dumps(response)
    assert called is False
    assert status == 422
    assert response["detail"] == {
        "error": "json_structure_invalid",
        "message": "JSON structure is invalid.",
        "field_errors": {"request": "JSON structure is invalid."},
    }
    assert digits not in rendered
    assert "Exceeds the limit" not in rendered


def test_unexpected_json_decoder_value_error_is_not_swallowed(monkeypatch):
    def fail(*args, **kwargs):
        raise ValueError("unexpected decoder failure")

    monkeypatch.setattr(http_limits.json, "loads", fail)

    with pytest.raises(ValueError, match="unexpected decoder failure"):
        _run_middleware([b'{"input":{}}'])


@pytest.mark.parametrize(
    "path",
    [
        "/skills/validate",
        "/business-workflows/validate",
        "/skills/validate-package",
    ],
)
def test_non_invoke_validation_and_package_paths_keep_their_own_contract(path: str):
    called, replayed, sent = _run_middleware(
        [b"x" * (MAX_REQUEST_BYTES + 1)], path=path
    )
    assert called is True
    assert replayed == b"x" * (MAX_REQUEST_BYTES + 1)
    assert sent == []


def test_structural_limit_rejects_before_invoke_route_model_decode():
    over_limit = "[" * (MAX_JSON_DEPTH + 1) + "0" + "]" * (
        MAX_JSON_DEPTH + 1
    )
    response = client.post(
        "/skills/no-such-skill/invoke",
        headers={**auth_headers(), "content-type": "application/json"},
        content=over_limit,
    )

    assert response.status_code == 422
    assert response.json()["detail"]["error"] == "json_depth_exceeded"
    assert response.json()["detail"]["field_errors"] == {
        "request": "JSON nesting is too deep."
    }


def test_receive_cancellation_propagates_without_calling_downstream():
    called = False

    async def downstream(scope, receive, send):  # noqa: ANN001
        nonlocal called
        called = True

    async def receive():
        raise asyncio.CancelledError

    async def send(message):
        raise AssertionError(f"unexpected response: {message}")

    async def invoke():
        await JsonRequestLimitMiddleware(downstream)(
            {
                "type": "http",
                "method": "POST",
                "path": "/skills/probe/invoke",
                "headers": [],
            },
            receive,
            send,
        )

    with pytest.raises(asyncio.CancelledError):
        asyncio.run(invoke())
    assert called is False
