"""正式環境 package reader：呼叫 Backend 的 internal-only package endpoint（設計 §2.3／§4.2）。

runner node 不碰 Backend HTTP，也不持有 internal token —— 這一層才是信任邊界：以
`X-Internal-Token` + tenant/user/role identity headers 向 `GET /api/skills/{name}/package`
取回原始 zip bytes，再交給 P0 的 parser 解析成 AgentSkillPackage。跨租戶與不存在的
package 由 Backend 統一回 404（設計 §2.3）；此處把 404 轉成 LookupError，讓 runner 走
Harness 的 fatal 路徑（稽核仍落地），而不是把整個服務炸掉。
"""

import httpx

from app.backend_http import get_client
from app.engine import package
from app.engine.package import AgentSkillPackage
from app.engine.tool_registry import ToolContext
from app.settings import settings


class PackageUnavailable(RuntimeError):
    """Backend 不可達、回非預期狀態，或回的 bytes 解析不了 package。"""


class BackendPackageReader:
    """production adapter：tenant-scoped internal Backend package endpoint → AgentSkillPackage。"""

    async def read(self, name: str, ctx: ToolContext) -> AgentSkillPackage:
        headers = {
            "X-Internal-Token": settings.internal_api_token,
            "X-Tenant-Id": ctx.tenant_id,
            "X-User-Id": ctx.user_id,
            "X-User-Role": ctx.role,
        }
        try:
            resp = await get_client().get(
                f"/api/skills/{name}/package",
                headers=headers,
                timeout=httpx.Timeout(10.0),
            )
            if resp.status_code == 404:
                # 跨租戶與不存在一律 404（Backend 的租戶隔離慣例）
                raise LookupError(f"tenant {ctx.tenant_id!r} 無 agentic package: {name!r}")
            resp.raise_for_status()
            raw = resp.content
        except httpx.HTTPError as e:
            raise PackageUnavailable(f"Backend 取 package 失敗（{name}）: {e}") from e

        parsed = package.parse_package(raw, name)
        if parsed.agentic is None:
            raise PackageUnavailable(f"package {name!r} 不是 agentic package")
        return parsed.agentic
