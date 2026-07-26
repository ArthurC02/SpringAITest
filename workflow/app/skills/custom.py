"""租戶自訂 Skill：定義存在 backend，workflow 只負責取回 → 靜態驗證 → 編譯 → 執行。

為什麼「name → 定義」這層對應**每次都向 backend 取**、不在本服務快取：那份對應關係是
租戶內的（同一個名字在不同租戶是不同的 skill，甚至只在某些租戶存在）。一旦快取，租戶隔離
就得由這裡自己重做一遍 —— 快取毒化即跨租戶洩漏。真正該省的是取回之後的重活（靜態驗證 +
建圖），那一層由 compiler 既有的快取（(name, revision, 內容雜湊, deps) → 圖）吸收：
同 revision 不重編（規格 §4）。所以這裡不開第二套快取。

清單與執行對 backend 故障的態度刻意不同：
- GET /skills 是**清單**端點 —— backend 不可達只讓自訂清單缺席（記 log），內建 skill 照列。
- invoke 是**交易**端點 —— 取不到定義就不能執行，回受控的 500（不是未捕捉例外，也不是
  假裝 404「skill 不存在」；後者會讓呼叫端把「引擎壞了」誤讀成「skill 被刪了」）。
"""

import asyncio
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

# 自訂 skill 沒有專屬的依賴組裝：沿用 kb_query 那組正式依賴（節點需要 llm/檢索/稽核時取得到）
from app.skills.deps import _default_deps

logger = logging.getLogger(__name__)


class BackendUnavailable(RuntimeError):
    """backend 不可達或回非預期狀態碼（定義的唯一事實來源取不到）。"""


class InvalidCustomSkill(RuntimeError):
    """DB 裡的定義解析不了／過不了靜態驗證／編不出圖（存檔時通過，事後節點被下架也算）。"""


# 依賴容器是單例：compiler 的快取鍵含 id(deps)，每次 invoke 新建一組會讓快取永遠 miss
# （規格 §8 要求 invoke 額外開銷 < 10ms，重編一張 kb_query 規模的圖遠不只這個數）。
_deps: Any = None


def singleton_deps() -> Any:
    """自訂 skill 共用的依賴容器（延遲建立：測試可先以 fake 覆寫 _deps）。"""
    global _deps
    if _deps is None:
        _deps = _default_deps()
    return _deps


# 既有呼叫端沿用 custom.deps() 名稱（re-export 別名，避免與 load 的 deps 參數混淆）。
deps = singleton_deps


def _is_agentic(definition: str) -> bool:
    """definition 是否為 agentic canonical 投影（metadata.kind: agentic，§3 標準形狀）。
    壞 YAML → False，交回 flow 路徑的 validate_source 收斂處理（不在這裡拋未捕捉例外）。"""
    try:
        data = yaml.safe_load(definition)
    except yaml.YAMLError:
        return False
    if not isinstance(data, dict):
        return False
    metadata = data.get("metadata")
    return isinstance(metadata, dict) and metadata.get("kind") == "agentic"


async def _fetch(path: str, ctx: RequestContext) -> Any | None:
    """GET backend；404 → None（含跨租戶不可見），其餘失敗 → BackendUnavailable。

    走 backend_http 的共用 client（同一個 backend base_url）；skill 取回是輕請求，
    以 10s timeout 覆寫共用 client 的 30s 預設。
    """
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
    """租戶自訂 skill 的清單項目；backend 不可達 → 記 log 並回空清單（不讓整個端點 500）。"""
    try:
        infos = await _fetch("/api/skills", ctx) or []
    except BackendUnavailable as e:
        logger.warning("自訂 skill 清單取得失敗，只回內建 skill: %s", e)
        return []
    return list(await asyncio.gather(*(_entry(ctx, info) for info in infos)))


async def _entry(ctx: RequestContext, info: dict) -> dict:
    """單一清單項目。

    input_schema 只能從 YAML 得知，而 backend 的清單刻意不含 definition（單筆才有）→
    逐筆再取一次。取不到或解析不了時**不讓該筆消失**：清單欄位（name/description/
    required_role/revision）在 info 裡已經是完整的，只有 input_schema 退成 null
    （前端據此退回 JSON textarea，比整筆不見得好）。
    """
    schema: dict[str, InputField] | None = None
    kind = "flow"
    try:
        data = await _fetch(f"/api/skills/{info['name']}", ctx)
        if data:
            definition = data["definition"]
            if _is_agentic(definition):
                parsed = package.skill_from_agentic_meta(yaml.safe_load(definition))[0]
            else:
                parsed = skill_mod.parse_source(definition)
            schema = parsed.input_schema
            kind = parsed.kind
    except Exception as e:  # 取不到／壞 YAML／schema 不合 —— 一筆的問題不該炸整份清單
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
        # 只有 backend 明確證明存在正整數 current_revision 的 persisted custom Skill
        # 才能固定到 Agent revision；缺失/0/壞值一律 fail closed。
        "bindable": revision is not None,
        "kind": kind,
        "input_schema": schema,
    }


async def load(
    name: str, ctx: RequestContext, deps: Any = None
) -> LoadedSkill | None:
    """取回 + 驗證 + 編譯一個自訂 skill；本租戶查無（含軟刪）→ None（呼叫端回 404）。

    deps 可帶入 per-config 依賴容器（P4c apply-at-execution）：租戶有效設定覆寫時，
    以覆寫後的 deps 編圖（compiler 快取鍵含 id(deps) → 同組覆寫命中同一張圖）；
    None 時回退共用單例 deps()（無覆寫、行為與過去一致）。
    """
    data = await _fetch(f"/api/skills/{name}", ctx)
    if data is None or not data.get("enabled", True):
        return None

    definition = data.get("definition") or ""
    # agentic 的 definition 是 workflow 於 import 時產生的 canonical 投影（service-authored，
    # §3 標準形狀）：不走 flow-only 的 validate_source（那會以 AGENTIC_REQUIRES_IMPORT 拒絕投影），
    # 改以與 import 同一個 skill_from_agentic_meta 解回 flat Skill（compile 走 agentic 分派）。
    agentic = _is_agentic(definition)
    if agentic:
        try:
            skill = package.skill_from_agentic_meta(yaml.safe_load(definition))[0]
        except (yaml.YAMLError, package.PackageError) as e:
            raise InvalidCustomSkill(
                f"自訂 skill '{name}' 的 agentic canonical 定義解析失敗: {e}"
            )
    else:
        result = skill_mod.validate_source(definition)
        if not result.valid:
            # 存檔時是驗過的：走到這裡代表定義在事後失效（例如引用的節點被下架）。
            # 不是呼叫端的錯 → 受控的 500，而不是 422。
            raise InvalidCustomSkill(
                f"自訂 skill '{name}' 的定義未通過靜態驗證: "
                f"{[e.code for e in result.errors]}"
            )
        skill = skill_mod.parse_source(definition)

    try:
        # revision 取 backend 的 current_revision（YAML 裡的 revision 欄位作者不會維護）→
        # 快取鍵 (name, revision, 內容雜湊)：同 revision 命中、改版即重編。
        skill = skill.model_copy(
            update={"revision": int(data.get("current_revision") or 1)}
        )
        container = deps if deps is not None else singleton_deps()
        graph = compiler.compile(skill, container)
    except Exception as e:
        raise InvalidCustomSkill(f"自訂 skill '{name}' 編譯失敗: {e}") from e

    return LoadedSkill(
        skill=skill,
        graph=graph,
        input_model=skill_mod.build_input_model(skill),
        deps=container,
        recursion_limit=compiler.recursion_limit(skill),
        source="custom",
    )
