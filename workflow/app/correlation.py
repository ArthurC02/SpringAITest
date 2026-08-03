"""為什麼要有這支模組:規格 §3.3「Runtime failures never expose raw exception text」。

未預期例外的原始文字會夾帶 backend 路徑、URL、provider 名稱與內部識別碼,一旦放進 HTTP
回應就是給呼叫端的偵察資料。但把訊息換成固定安全字串之後,排障就少了「使用者看到的那個
500」對回「伺服器上那筆 traceback」的線 —— correlation ID 就是這條線,而且只有這條:
回應 body(snake_case `correlation_id`)與回應標頭各回一份,完整例外只留在日誌。

呼叫端可帶 `X-Correlation-Id` 讓同一個 ID 貫穿跨服務鏈路;帶進來的值會被原樣回寫進回應
標頭,所以它是信任邊界輸入 —— 走白名單、預設拒絕(不合格即改用新生成的 uuid4)。
"""

import json
import logging
import re
import uuid
from contextvars import ContextVar

from fastapi import HTTPException
from starlette.datastructures import Headers, MutableHeaders

logger = logging.getLogger(__name__)

HEADER_NAME = "X-Correlation-Id"

# 固定安全訊息:不含例外文字、路徑、URL、provider 細節。要細節只能拿 correlation_id 查日誌。
SAFE_EXECUTION_FAILED_MESSAGE = "工作流程執行失敗，請提供追蹤編號給管理員"

# 不透明 ID 的字元白名單(uuid / W3C traceparent / 常見 request id 皆涵蓋)。預設拒絕:
# 不符即丟棄改生成新 ID,杜絕標頭注入與把回應標頭當成反射輸出通道。
_SAFE_ID = re.compile(r"\A[A-Za-z0-9._:-]{1,128}\Z")

_correlation_id: ContextVar[str] = ContextVar("correlation_id", default="")


def current_correlation_id() -> str:
    """目前請求的 correlation ID;在請求範圍外(背景工作、單元測試直呼)回空字串。"""
    return _correlation_id.get()


class CorrelationIdMiddleware:
    """純 ASGI middleware(與 FeatureGateMiddleware 同風格,不用 BaseHTTPMiddleware)。

    刻意不走 BaseHTTPMiddleware:後者把下游包成子任務,對串流回應與例外傳遞都有已知副作用,
    而這裡只需要「進來設 contextvar、出去補一個標頭」。
    """

    def __init__(self, app) -> None:
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope.get("type") != "http":
            await self.app(scope, receive, send)
            return
        incoming = (Headers(scope=scope).get(HEADER_NAME) or "").strip()
        correlation_id = incoming if _SAFE_ID.match(incoming) else str(uuid.uuid4())
        token = _correlation_id.set(correlation_id)
        response_started = False

        async def send_with_header(message):
            nonlocal response_started
            if message["type"] == "http.response.start":
                response_started = True
                MutableHeaders(scope=message)[HEADER_NAME] = correlation_id
            await send(message)

        try:
            await self.app(scope, receive, send_with_header)
        except Exception:
            # 這一層外面只剩 Starlette 的 ServerErrorMiddleware —— 它在 user middleware 之外，
            # 既拿不到 contextvar（已被下面的 finally reset），也不經過 send_with_header，
            # 於是未修補路徑的未預期例外會變成 text/plain "Internal Server Error"：無標頭、
            # 無 JSON 信封、日誌對不上任何 ID。同理 @app.exception_handler(Exception) 也在
            # 這兩件事之外，救不了。所以收尾必須在這裡：記一次原始例外（含 traceback 與
            # correlation ID），再自行送出與修補路徑同形的固定安全 500。
            log_unexpected(logger, f"{scope.get('method', '')} {scope.get('path', '')}".strip())
            if response_started:
                raise  # 回應已開始送出，狀態碼/標頭都改不了了，只能讓它照實中斷
            body = json.dumps({"detail": execution_failed_detail()}, ensure_ascii=False).encode()
            await send_with_header(
                {
                    "type": "http.response.start",
                    "status": 500,
                    "headers": [
                        (b"content-type", b"application/json; charset=utf-8"),
                        (b"content-length", str(len(body)).encode("latin-1")),
                    ],
                }
            )
            await send({"type": "http.response.body", "body": body})
        finally:
            _correlation_id.reset(token)


def execution_failed_detail() -> dict:
    """500 body:固定安全訊息 + correlation_id(workflow 內部契約用 snake_case)。"""
    return {
        "error": "workflow_execution_failed",
        "message": SAFE_EXECUTION_FAILED_MESSAGE,
        "correlation_id": current_correlation_id(),
    }


def log_unexpected(logger: logging.Logger, context: str) -> None:
    """在 except 區塊內呼叫:以 exception 級別記完整原始例外 + correlation ID。

    每個未預期例外只在「例外還活著的那一層」記一次;上層改成安全訊息時不再重複記錄。
    """
    logger.exception("%s failed (correlation_id=%s)", context, current_correlation_id())


def execution_failed(logger: logging.Logger, context: str) -> HTTPException:
    """在 except 區塊內呼叫:記錄原始例外,回傳只帶固定安全訊息的 500。"""
    log_unexpected(logger, context)
    return HTTPException(status_code=500, detail=execution_failed_detail())
