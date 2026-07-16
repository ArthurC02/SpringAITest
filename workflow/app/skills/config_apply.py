"""Configuration Set apply-at-execution（P4c，設計 §7.4 / §9 / §10 縫⑤ / §11 點 3）。

為什麼在 invoke 期重建依賴而不是啟動時鎖死：租戶的 active Configuration Set 是「組織 Admin
那層」的執行參數（top_k、模型、溫度、逾時、意圖門檻），每個租戶不同、且可隨時改版。啟動預編的
全域圖看不到它。這裡在每次 invoke 取回本租戶 active set，疊上全域預設得到「有效設定」，據此建一組
per-config 依賴容器——同租戶+同版靠 (tenant_id, config_version) 快取回同一個 deps 物件，讓
compiler.compile 的圖快取（鍵含 id(deps)）命中、不重編；換版才給新 deps、重編一次。

租戶隔離（§11 點 3）：active set 一律帶 X-Tenant-Id 直取 backend（不經 platform）；per-config
deps 快取鍵含 tenant_id，A 租戶的設定絕不污染 B 租戶。取不到（404）或後端故障 → 全域預設回落
（不跨租戶 fallback、不寫快取、無副作用）——這是保守的安全預設，不是另一租戶的設定。
"""

import dataclasses
import hashlib
import json
import logging
from collections import OrderedDict
from typing import Any

from app.kbquery.adapters import LangChainStructuredLLM
from app.kbquery.graph import KbQueryDeps
from app.llm import DEFAULT_TEMPERATURE
from app.security import RequestContext
from app.settings import settings
from app.skills import custom

logger = logging.getLogger(__name__)

# 促升的兩個歷史寫死值的全域預設（節點內建 0.6 / llm.py 0.7）。settings 已承載其餘四個。
_INTENT_CONFIDENCE_DEFAULT = 0.6


def global_defaults() -> dict[str, Any]:
    """七個開放鍵的全域預設（未來系統 Admin 那層，v1 = 程式內建值）。設計 §9 開放鍵表。"""
    return {
        "retrieval.top_k": settings.retrieval_top_k,
        "kb_query.top_k": settings.kb_query_top_k,
        "kb_query.max_retrieval_attempts": settings.kb_query_max_retrieval_attempts,
        "workflow.timeout_seconds": settings.workflow_timeout_seconds,
        "llm.model": settings.llm_model,
        "intent.confidence_threshold": _INTENT_CONFIDENCE_DEFAULT,
        "llm.temperature": DEFAULT_TEMPERATURE,
    }


def resolve_effective(values: dict[str, Any] | None) -> dict[str, Any]:
    """有效設定 = 全域預設 ⊕ 租戶覆寫。只覆寫「有值」的鍵，其餘精確回落全域預設。

    逐鍵回落（不整組替換）：只覆寫一鍵時其餘六鍵仍是全域預設，不會被誤設成 null/0/空字串
    （SSR-P4-017）。未知鍵忽略（backend 寫入期已把關白名單，這裡只取認得的七鍵）。
    """
    effective = global_defaults()
    for key in effective:
        if values and key in values and values[key] is not None:
            effective[key] = values[key]
    return effective


def build_config_deps(base: KbQueryDeps, effective: dict[str, Any]) -> KbQueryDeps:
    """以有效設定新建 per-config 依賴容器：沿用 base 的各埠，只覆寫可調的四個欄位 + llm。

    base = 共用單例 deps（正式環境是 _default_deps()，含檢索/稽核/詞彙各埠；測試為 fake）。
    llm 一律新建 LangChainStructuredLLM(model, temperature)，繞開 get_llm 的 lru_cache 單例。
    retrieval.top_k 不在此套用——它是通用 retrieve@1.0 的執行參數，不經 deps，而是 invoke 期
    由 resolve() 抽出覆寫值、seed 進初始 state（縫⑦ runtime apply，見 retrieve.py 取值精度）。
    """
    return dataclasses.replace(
        base,
        default_top_k=int(effective["kb_query.top_k"]),
        max_retrieval_attempts=int(effective["kb_query.max_retrieval_attempts"]),
        intent_confidence_threshold=float(effective["intent.confidence_threshold"]),
        llm=LangChainStructuredLLM(
            model=str(effective["llm.model"]),
            temperature=float(effective["llm.temperature"]),
        ),
    )


def config_version(active: dict[str, Any]) -> str:
    """版本識別：優先用 backend 的 updated_at（改版即 bump），退回 values 內容雜湊。"""
    updated_at = active.get("updated_at")
    if updated_at:
        return str(updated_at)
    payload = json.dumps(active.get("values") or {}, sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


# per-config deps 快取：(tenant_id, config_version) → deps。FIFO 上限比照 compiler._CACHE_MAX。
# 鍵含 tenant_id → A 租戶設定絕不落到 B（§11 點 3）；同租戶同版命中同一物件 → compiler 圖快取命中。
_CACHE_MAX = 32
_config_deps_cache: "OrderedDict[tuple[str, str], KbQueryDeps]" = OrderedDict()


def get_config_deps(
    tenant_id: str, version: str, base: KbQueryDeps, effective: dict[str, Any]
) -> KbQueryDeps:
    """取/建 per-config deps；同 (tenant_id, version) 回同一物件（id 穩定 → 圖快取命中）。"""
    key = (tenant_id, version)
    deps = _config_deps_cache.get(key)
    if deps is None:
        deps = build_config_deps(base, effective)
        _config_deps_cache[key] = deps
        while len(_config_deps_cache) > _CACHE_MAX:
            _config_deps_cache.popitem(last=False)
    return deps


async def resolve(ctx: RequestContext) -> tuple[KbQueryDeps | None, int, int | None]:
    """取本租戶 active set → 有效設定 → per-config deps + retrieval.top_k 執行期 seed。

    回 (per_config_deps 或 None, timeout_seconds, retrieval_top_k 或 None)：
    - 無 active / 空 values → (None, 全域逾時, None)：呼叫端走全域路徑（builtin 沿用啟動圖、
      custom 走單例），retrieve 回落骨架 SLOT／全域，零行為變更。
    - 有覆寫 → (per_config, 有效逾時, seed)：呼叫端以 per_config 編圖；seed 只在租戶明確覆寫
      retrieval.top_k 時為該值（其餘為 None），main.py 據此 seed 進初始 state（縫⑦ runtime apply）。
    後端 404 → 空 values；不可達 / 5xx / 壞 JSON / 逾時 → 記 log 後回全域預設（不跨租戶、不寫快取）。
    """
    active: dict | None = None
    try:
        active = await custom._fetch("/api/configuration-sets/active", ctx)
    except Exception as e:  # noqa: BLE001 — 刻意的韌性邊界（比照 custom.catalog）：故障退回全域預設
        logger.warning("取 active Configuration Set 失敗，改用全域預設: %s", e)
        active = None

    values = (active or {}).get("values") or {}
    effective = resolve_effective(values)
    timeout = int(effective["workflow.timeout_seconds"])
    if not values:
        return None, timeout, None

    # retrieval.top_k 只在租戶明確覆寫時才 seed（用原始 values 而非 effective —— effective 會
    # 回落全域 4，若無條件 seed 4 反而蓋掉骨架 SLOT 的 8，破壞精度 per-config > SLOT > 全域）。
    raw_top_k = values.get("retrieval.top_k")
    retrieval_seed = int(raw_top_k) if raw_top_k is not None else None

    per_config = get_config_deps(
        ctx.tenant_id, config_version(active), custom.deps(), effective
    )
    return per_config, timeout, retrieval_seed
