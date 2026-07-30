"""Pinned prompt composition 的 resolved manifest port（P1，plan 03 §3）。

為什麼需要這一層：`_system_frame()` 的 SYSTEM GOVERNANCE 段原本是 Python constants，
於是 run 能 pin Agent revision，卻回答不出「當時用的是哪一套 composition」。Backend 把
composition 發布成 immutable manifest revision 之後，帶 pin 的 run 其組成就必須來自那個
revision —— 所以這裡每一種失敗（不可達、404、形狀不符、hash 不符、schema 漂移）都是
fail closed，絕不退回 constants：靜默退回會讓「pinned run」這個保證變成謊話。

快取以 `(tenant, revision)` 為鍵。revision immutable，所以沒有失效問題；tenant 進鍵是
租戶隔離（同一個 revision 號在別的租戶是別的東西，快取共用即跨租戶洩漏），不是效能考量。
"""

from __future__ import annotations

import hashlib
import logging

import httpx
from pydantic import BaseModel, ConfigDict, Field

from app.backend_http import Identity, get_client, internal_headers
from app.runtime.models import PromptManifestPin

logger = logging.getLogger(__name__)

GOVERNANCE_FRAME_KIND = "governance_frame"
SUPPORTED_SCHEMA_VERSION = 1
MAX_CACHED_MANIFESTS = 64


class PromptManifestUnavailable(RuntimeError):
    """Pinned manifest 取不到或不符 pin —— 該 run fail closed，不得改用 constants。"""


class ResolvedComponent(BaseModel):
    # extra="ignore"：Backend 之後為 manifest 加欄位不該讓既有 run 失敗；內容完整性
    # 由 content_sha256 / manifest_sha256 把關，不靠「未知欄位就拒收」。
    model_config = ConfigDict(extra="ignore", strict=True)

    kind: str = Field(min_length=1, max_length=64)
    revision: int = Field(ge=1)
    content_sha256: str
    content: str = Field(max_length=200_000)


class ResolvedPromptManifest(BaseModel):
    model_config = ConfigDict(extra="ignore", strict=True)

    revision: int = Field(ge=1)
    manifest_sha256: str
    schema_version: int
    # `str | None`：Backend 的 System.Text.Json Web defaults 不會省略 null 欄位，
    # 沒設 catalog hash 的 manifest 會實際送 `null` 過來（同 E2 的 candidate.pins）。
    skill_catalog_hash: str | None = None
    components: list[ResolvedComponent] = Field(default_factory=list, max_length=32)

    def component(self, kind: str) -> ResolvedComponent | None:
        return next((item for item in self.components if item.kind == kind), None)

    def required_governance_frame(self) -> str:
        """assembler 唯一取用的 component；缺了就帶不動組成，一律 fail closed。"""
        component = self.component(GOVERNANCE_FRAME_KIND)
        if component is None:
            raise PromptManifestUnavailable(
                "resolved prompt manifest has no governance frame component"
            )
        return component.content


_cache: dict[tuple[str, int], ResolvedPromptManifest] = {}


def verify_resolved_manifest(
    raw: object, pin: PromptManifestPin
) -> ResolvedPromptManifest:
    """Backend 回應必須完全對得上 snapshot pin，且每段 content 對得上自己的 hash。"""
    try:
        manifest = ResolvedPromptManifest.model_validate(raw)
    except Exception as exc:
        raise PromptManifestUnavailable(
            "resolved prompt manifest has an invalid shape"
        ) from exc
    if manifest.revision != pin.revision or manifest.manifest_sha256 != pin.sha256:
        raise PromptManifestUnavailable(
            "resolved prompt manifest does not match its snapshot pin"
        )
    if manifest.schema_version != SUPPORTED_SCHEMA_VERSION:
        raise PromptManifestUnavailable(
            "resolved prompt manifest schema version is unsupported"
        )
    for component in manifest.components:
        if sha256_text(component.content) != component.content_sha256:
            raise PromptManifestUnavailable(
                "prompt component content does not match its pinned hash"
            )
    # 取回時就檢查（而不是等 assembler 用到才炸）：缺件的 manifest 不進快取。
    manifest.required_governance_frame()
    return manifest


async def read_resolved_manifest(
    pin: PromptManifestPin, ctx: Identity
) -> ResolvedPromptManifest:
    key = (ctx.tenant_id, pin.revision)
    cached = _cache.get(key)
    if cached is not None:
        # 同一租戶同一 revision 的 canonical SHA 不會變；對不上就是 pin 有問題，
        # 快取命中也必須 fail closed（不能因為省一次請求而放過不符的 pin）。
        if cached.manifest_sha256 != pin.sha256:
            raise PromptManifestUnavailable(
                "resolved prompt manifest does not match its snapshot pin"
            )
        return cached
    try:
        response = await get_client().get(
            f"/api/prompt-manifests/{pin.revision}/resolved",
            headers=internal_headers(ctx),
            timeout=httpx.Timeout(10.0),
        )
        if response.status_code == 404:
            # 旗標關閉、revision 不存在、跨租戶不可見，Backend 一律回 404。
            raise PromptManifestUnavailable(
                "pinned prompt manifest revision is unavailable"
            )
        response.raise_for_status()
        body = response.json()
    except PromptManifestUnavailable:
        raise
    except (httpx.HTTPError, ValueError) as exc:
        raise PromptManifestUnavailable(
            "Backend prompt manifest request failed"
        ) from exc
    manifest = verify_resolved_manifest(body, pin)
    if len(_cache) >= MAX_CACHED_MANIFESTS:
        # ponytail: dict 是插入序 → pop 最舊的一筆即 FIFO；命中率若成為問題再換 LRU。
        _cache.pop(next(iter(_cache)))
    _cache[key] = manifest
    return manifest


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()
