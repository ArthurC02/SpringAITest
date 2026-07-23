"""agentic runner 的 package reader port（設計 §4.2）。

## 為什麼是一個 port，而不是把 Backend HTTP 塞進 node

runner node 要拿到 package（instruction／resources／scripts）才能跑，但「怎麼拿」是
基礎設施細節：正式環境走 internal-token、tenant-scoped 的 Backend endpoint，測試給
記憶體 fixture。把 HTTP client 與 internal token 直接寫進 node 會讓 node 綁死一種
取得方式、也讓 node 之外的信任邊界（token）滲進資料流層。故以 Protocol 當 port，
adapter 由 skills 層（DI 組裝點）注入。

## 租戶隔離：不快取

reader 對每次 read 都重新取回：name→package 的對應是租戶內的（同名在不同租戶是不同
package）。一旦快取就得在這裡自己重做租戶隔離，快取毒化即跨租戶洩漏（設計 §4.2、
規格 §3）。真正該省的重活是「解析後的建圖」，那層由 compiler 既有的快取吸收；這裡不開
第二套快取（若日後要快取，key 至少含 (tenant_id, name, package_sha256) 且 package
更新即失效）。
"""

from typing import Protocol

from app.engine.package import AgentSkillPackage
from app.engine.tool_registry import ToolContext


class AgentSkillPackageReader(Protocol):
    """按需取得某個 agentic skill 的 package（以 ToolContext 的 server-injected 身分做租戶界定）。"""

    async def read(self, name: str, ctx: ToolContext) -> AgentSkillPackage: ...


class MemoryPackageReader:
    """測試用 reader：以 (tenant_id, name) → AgentSkillPackage 的 fixture 回應。

    以 tenant 分租戶存放，因此「跨租戶各取自己的 instruction、不互相污染」不是靠斷言
    假設，而是 reader 真的依 ctx.tenant_id 分流（AST-P1-009）。查無 → KeyError（runner
    走 Harness fatal 路徑，稽核仍落地）。
    """

    def __init__(self, by_tenant: dict[tuple[str, str], AgentSkillPackage] | None = None):
        # key = (tenant_id, name)
        self._by_tenant: dict[tuple[str, str], AgentSkillPackage] = dict(by_tenant or {})

    def put(self, tenant_id: str, pkg: AgentSkillPackage) -> None:
        self._by_tenant[(tenant_id, pkg.skill.name)] = pkg

    async def read(self, name: str, ctx: ToolContext) -> AgentSkillPackage:
        try:
            return self._by_tenant[(ctx.tenant_id, name)]
        except KeyError:
            raise KeyError(
                f"tenant {ctx.tenant_id!r} 無 agentic package: {name!r}"
            )
