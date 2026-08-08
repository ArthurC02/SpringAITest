"""租戶自訂 artifact 的 backend 取回、驗證與編譯邊界。

名稱與定義每次都向 backend 取得，因為它們是租戶內資料，在這一層快取會
引入跨租戶污染風險。靜態驗證與建圖才交由 compiler 的
``(name, revision, content hash, deps)`` 快取吸收。

清單與執行對 backend 故障的態度刻意不同：清單降級為只回內建項目；執行無法
取得權威 artifact 時則受控失敗，不偽裝成 404。
"""

import asyncio
import hashlib
import hmac
import logging
from typing import Any

import httpx
import yaml

from app.backend_http import get_client, internal_headers
from app.engine import compiler, package
from app.engine import skill as skill_mod
from app.engine.skill import InputField
from app.security import RequestContext
from app.skills import LoadedSkill
from app.skills.deps import _default_deps

logger = logging.getLogger(__name__)


class BackendUnavailable(RuntimeError):
    """backend 不可達或回非預期狀態碼。"""


class InvalidCustomSkill(RuntimeError):
    """已儲存 artifact 無法解析、驗證或編譯。"""


# 依賴容器是單例：compiler 快取鍵含 id(deps)，每次重建會使快取永遠 miss。
_deps: Any = None


def singleton_deps() -> Any:
    """自訂 artifact 共用的延遲依賴容器。"""
    global _deps
    if _deps is None:
        _deps = _default_deps()
    return _deps


async def _fetch(path: str, ctx: RequestContext) -> Any | None:
    """GET backend；404 回 None，其餘傳輸失敗轉為受控例外。"""
    try:
        resp = await get_client().get(
            path, headers=internal_headers(ctx), timeout=httpx.Timeout(10.0)
        )
        if resp.status_code == 404:
            return None
        resp.raise_for_status()
        return resp.json()
    except httpx.HTTPError as e:
        raise BackendUnavailable(f"backend 取 skill 失敗（{path}）: {e}") from e


async def catalog(ctx: RequestContext) -> list[dict]:
    """租戶自訂 artifact 清單；backend 不可達時記錄並回空清單。"""
    try:
        infos = await _fetch("/api/skills", ctx) or []
    except BackendUnavailable as e:
        logger.warning("自訂 skill 清單取得失敗，只回內建 skill: %s", e)
        return []
    return list(await asyncio.gather(*(_entry(ctx, info) for info in infos)))


async def _entry(ctx: RequestContext, info: dict) -> dict:
    """建立單一 catalog 項目；kind 以 backend list 為唯一事實來源。

    detail GET 仍然必要，因為 input_schema 只存在 definition。detail 不可讀或
    schema 失效時保留清單項目，僅將 input_schema 降級為 null。
    """
    schema: dict[str, InputField] | None = None
    kind = info["kind"]
    try:
        data = await _fetch(f"/api/skills/{info['name']}", ctx)
        if data:
            definition = data["definition"]
            if kind == "agentic":
                parsed = package.skill_from_agentic_meta(yaml.safe_load(definition))[0]
            else:
                parsed = skill_mod.parse_source(definition)
            schema = parsed.input_schema
    except Exception as e:
        logger.warning("自訂 skill %s 的 input_schema 取得失敗: %s", info.get("name"), e)

    raw_revision = info.get("current_revision")
    revision = (
        raw_revision
        if isinstance(raw_revision, int)
        and not isinstance(raw_revision, bool)
        and raw_revision > 0
        else None
    )
    return {
        "name": info["name"],
        "description": info.get("description") or "",
        "required_role": info.get("required_role") or "USER",
        "source": "custom",
        "revision": revision,
        "bindable": revision is not None,
        "kind": kind,
        "input_schema": schema,
    }


def _compile_loaded(skill: skill_mod.Skill, data: dict, deps: Any) -> LoadedSkill:
    """共用編譯與 LoadedSkill 組裝；kind 解析由各載入管線自己負責。"""
    definition = data.get("definition") or ""
    expected_hash = data.get("definition_sha256")
    actual_hash = hashlib.sha256(definition.encode("utf-8")).hexdigest()
    if not isinstance(expected_hash, str) or not expected_hash.strip():
        raise InvalidCustomSkill(f"custom skill '{skill.name}' has no authoritative definition hash")
    if not hmac.compare_digest(actual_hash, expected_hash):
        raise InvalidCustomSkill(f"custom skill '{skill.name}' definition hash mismatch")
    try:
        skill = skill.model_copy(
            update={"revision": int(data.get("current_revision") or 1)}
        )
        container = deps if deps is not None else singleton_deps()
        graph = compiler.compile(skill, container)
    except Exception as e:
        raise InvalidCustomSkill(f"自訂 skill '{skill.name}' 編譯失敗: {e}") from e
    return LoadedSkill(
        skill=skill,
        graph=graph,
        input_model=skill_mod.build_input_model(skill),
        deps=container,
        recursion_limit=compiler.recursion_limit(skill),
        source="custom",
        definition=definition,
        definition_sha256=expected_hash,
    )


def load_business_workflow(name: str, data: dict, deps: Any = None) -> LoadedSkill:
    """驗證並編譯 backend 取回的 Business Workflow YAML。"""
    definition = data.get("definition") or ""
    result = skill_mod.validate_source(definition)
    if not result.valid:
        raise InvalidCustomSkill(
            f"自訂 skill '{name}' 的定義未通過靜態驗證: "
            f"{[e.code for e in result.errors]}"
        )
    return _compile_loaded(skill_mod.parse_source(definition), data, deps)


def load_agent_skill(name: str, data: dict, deps: Any = None) -> LoadedSkill:
    """解析並編譯 backend 儲存的 Agent Skill canonical 投影。"""
    try:
        skill = package.skill_from_agentic_meta(
            yaml.safe_load(data.get("definition") or "")
        )[0]
    except (yaml.YAMLError, package.PackageError) as e:
        raise InvalidCustomSkill(
            f"自訂 skill '{name}' 的 agentic canonical 定義解析失敗: {e}"
        ) from e
    return _compile_loaded(skill, data, deps)


async def load(
    name: str, ctx: RequestContext, deps: Any = None
) -> LoadedSkill | None:
    """取回自訂 artifact，依 backend 的可信 kind 分派載入管線。

    deps 可帶入 per-config 依賴容器；None 時回退共用單例，使 compiler 的
    ``id(deps)`` 快取鍵維持穩定。本租戶查無（含軟刪）回 None。
    """
    data = await _fetch(f"/api/skills/{name}", ctx)
    if data is None or not data.get("enabled", True):
        return None
    kind = data.get("kind")
    if kind == "flow":
        return load_business_workflow(name, data, deps)
    if kind == "agentic":
        return load_agent_skill(name, data, deps)
    raise InvalidCustomSkill(f"自訂 skill '{name}' 的 kind 無效: {kind!r}")
