"""服務間認證與多租戶 context 解析。

所有 /workflows* 與 /documents* 端點皆須通過這裡的兩道檢查：
1. require_internal：驗證 platform 端（.NET，env 驅動）與本服務共享的內部密鑰（X-Internal-Token）。
2. get_context：解析 platform 端轉送過來的租戶／使用者／角色資訊，組成 RequestContext。

get_context 內部相依 require_internal，因此只要路由掛上 `Depends(get_context)`，
就能保證「先驗證內部密鑰、再解析 context」的順序，不需要在每個路由重複宣告兩個依賴。

旗標保護的內部 runtime 端點（D3 /agent-runs、D5 /orchestrator-runs）另有兩道共用閘門：
FeatureGateMiddleware（路由與 body 解析之前就回 404）與 require_runtime_context
（認證之前先看旗標，且三個身分標頭缺一不可）。兩條 runtime 只差在旗標名與 400 訊息文字，
所以共用同一份實作。
"""

import hmac
from dataclasses import dataclass

from fastapi import Depends, Header, HTTPException, Request, status
from starlette.responses import JSONResponse

from app.settings import settings


async def require_internal(
    x_internal_token: str | None = Header(default=None),
) -> None:
    """驗證共享密鑰；缺漏或不符一律視為未授權（401），不區分是哪種原因以避免洩漏細節。

    以 hmac.compare_digest 做定時比較：一般 == 會在第一個不符的字元短路返回，令
    比對耗時隨相符前綴長度變化，可被拿來逐字元爆破 token。定時安全比較消掉這條側通道。
    """
    # 比較 bytes 而非 str：compare_digest 對含非 ASCII 的 str 會拋 TypeError，
    # 惡意的畸形標頭本該收斂成 401，不能變成未捕捉例外的 500。
    if (
        x_internal_token is None
        or not settings.internal_api_token.strip()
        or not hmac.compare_digest(
            x_internal_token.encode("utf-8"), settings.internal_api_token.encode("utf-8")
        )
    ):
        raise HTTPException(
            status_code=401,
            detail={
                "error": "unauthorized",
                "message": "缺少或錯誤的 X-Internal-Token 標頭",
            },
        )


@dataclass(frozen=True)
class RequestContext:
    """貫穿單一請求的租戶／使用者／角色資訊，一律由 platform 端經 header 轉送過來。"""

    tenant_id: str
    user_id: str
    role: str


async def get_context(
    _: None = Depends(require_internal),
    x_tenant_id: str | None = Header(default=None),
    x_user_id: str | None = Header(default=None),
    x_user_role: str | None = Header(default=None),
) -> RequestContext:
    """解析多租戶／角色 context；缺少租戶或角色視為壞請求（400），無法安全地繼續處理。"""
    if not x_tenant_id or not x_user_role:
        raise HTTPException(
            status_code=400,
            detail={
                "error": "missing_context",
                "message": "缺少 X-Tenant-Id 或 X-User-Role 標頭",
            },
        )
    return RequestContext(tenant_id=x_tenant_id, user_id=x_user_id or "", role=x_user_role)


class FeatureGateMiddleware:
    """旗標關閉時，在路由與 body 解析之前就回 404（能力看起來像沒安裝過）。"""

    def __init__(self, app, prefix: str, flag_name: str) -> None:
        self.app = app
        self.prefix = prefix
        self.flag_name = flag_name

    async def __call__(self, scope, receive, send):
        if (
            scope.get("type") == "http"
            and str(scope.get("path") or "").startswith(self.prefix)
            and not getattr(settings, self.flag_name)
        ):
            await JSONResponse(status_code=404, content={"detail": "Not Found"})(
                scope, receive, send
            )
            return
        await self.app(scope, receive, send)


async def require_runtime_context(
    request: Request, *, flag_name: str, message: str
) -> RequestContext:
    """旗標 → 內部密鑰 → 三個身分標頭缺一不可（runtime 端點的 fail-closed 身分閘門）。"""
    # Feature-off is deliberately resolved before authentication: the route
    # remains indistinguishable from an uninstalled capability.
    if not getattr(settings, flag_name):
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Not Found")
    await require_internal(request.headers.get("X-Internal-Token"))
    tenant = (request.headers.get("X-Tenant-Id") or "").strip()
    user = (request.headers.get("X-User-Id") or "").strip()
    role = (request.headers.get("X-User-Role") or "").strip()
    if not tenant or not user or not role:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"error": "missing_context", "message": message},
        )
    return RequestContext(tenant_id=tenant, user_id=user, role=role)
