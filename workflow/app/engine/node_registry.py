"""Node Registry：節點以 @node 註冊為一等公民（具名、版本化、宣告 I/O 契約）。

為什麼是「依賴欄位名宣告」而不是規格書 §2.1 的統一簽名 `make_x(tools) -> NodeFn`：
現有 10 個 factory 的參數各異（`make_query_rewrite_node(llm, glossary)`…），且既有
節點測試直接以位置參數呼叫它們；改簽名等於改既有測試（P1 的不可退讓底線是既有測試
一行都不動）。因此 factory 本體與簽名一律不動，改用 `deps=["llm", "glossary"]` 宣告
「依序從 deps 物件取哪幾個欄位當位置參數」，讓 `spec.build(deps)` 成為統一建構介面 ——
編譯器（P2）只靠節點名字就能建出節點函式，不必認得每個 factory 的參數表。

`retrieve` 這種參數不在 deps 物件上的節點（query_key/top_k 是呼叫端決定的靜態參數），
宣告 `deps=[]`，由呼叫端在 `spec.build(deps, query_key=..., top_k=...)` 以 kwargs 傳入。
"""

from dataclasses import dataclass
from typing import Any, Awaitable, Callable, Iterable

# 節點函式簽名：吃整個 state，回傳要合併進 state 的部分鍵（或 None）
NodeFn = Callable[[dict], Awaitable[dict | None]]


@dataclass(frozen=True)
class NodeSpec:
    """已註冊節點的完整契約。

    - reads/writes：節點讀取／寫入的 state 鍵。Harness 於執行後剝除未宣告的 writes 鍵
      （節點無法偷寫 state）；reads 供 Skill 存檔時的資料流靜態檢查（P2）。
    - deps：對應注入用依賴物件（如 KbQueryDeps）的欄位名，依序當位置參數傳給 factory。
    - requires_tools：Tool Registry（P3）注入用，P1 一律空。
    - run_on_fatal：fatal 短路後仍需執行（answer_composer / audit_feedback）。
    - appends：writes 中屬「累加」語意的鍵（compiler 據此掛 operator.add reducer）。
      不宣告 → 覆寫語意。累加語意是節點自己的事實（retrieval_planner 每輪各留一份計畫
      供稽核），寫死在引擎裡就變成領域知識外洩，且漏掉時稽核軌跡只剩最後一次。
    - dynamic_reads：值為「建構期參數名」而非 state 鍵名（retrieve 讀 state[query_key]，
      query_key 由 Skill 的 params: 決定）。資料流檢查時用該步驟的 params 解析成實際鍵。
    """

    name: str
    version: str
    description: str
    reads: tuple[str, ...]
    writes: tuple[str, ...]
    deps: tuple[str, ...]
    requires_tools: tuple[str, ...]
    run_on_fatal: bool
    factory: Callable[..., NodeFn]
    appends: tuple[str, ...] = ()
    dynamic_reads: tuple[str, ...] = ()

    def build(self, deps: Any = None, **params: Any) -> NodeFn:
        """統一建構法：`factory(*[getattr(deps, d) for d in self.deps], **params)`。"""
        return self.factory(*(getattr(deps, d) for d in self.deps), **params)


_REGISTRY: dict[tuple[str, str], NodeSpec] = {}

# 引擎鍵：由 Harness 自己寫入（trace 累加、例外轉 fatal_error）。節點不得宣告、不得寫入 ——
# 節點若宣告了它們，Harness 的 writes 剝除就會放行節點偽造的 fatal_error / errors，
# 稽核紀錄（AuditTrail 讀 errors）與 fatal 短路都能被繞過。
ENGINE_KEYS = frozenset({"trace", "errors", "fatal_error"})


def _version_key(version: str) -> tuple[int, ...]:
    """語意化版本排序鍵；非數字段落視為 0（版本字串目前只用 "1.0" 這種形式）。"""
    return tuple(int(p) if p.isdigit() else 0 for p in version.split("."))


def node(
    *,
    name: str,
    writes: Iterable[str],
    version: str = "1.0",
    description: str = "",
    reads: Iterable[str] = (),
    deps: Iterable[str] = (),
    requires_tools: Iterable[str] = (),
    run_on_fatal: bool = False,
    appends: Iterable[str] = (),
    dynamic_reads: Iterable[str] = (),
):
    """裝飾器：把節點 factory 登記進註冊表，factory 本身原樣回傳（簽名不變）。

    writes 刻意沒有預設值：Harness 會剝除未宣告的輸出鍵，漏寫 writes 等於「該節點的
    輸出全部被靜默丟棄」（圖照跑、trace 照記 ok，下游卻永遠讀不到鍵）。改成必填 kwarg，
    漏寫就在 import 期 TypeError，失敗得大聲。

    同一個 name@version 重複註冊 → 立即 ValueError（對齊 workflows/registry.py 的行為，
    在 import 期就炸掉，而不是讓後註冊者悄悄覆蓋先註冊者）。
    """

    def decorator(factory: Callable[..., NodeFn]) -> Callable[..., NodeFn]:
        write_keys = tuple(writes)  # writes 可能是 generator，只走訪一次
        forbidden = ENGINE_KEYS & set(write_keys)
        if forbidden:
            raise ValueError(
                f"node {name}@{version} 不得宣告引擎鍵為 writes: "
                f"{sorted(forbidden)}（trace/errors/fatal_error 由 Harness 寫入）"
            )
        append_keys = tuple(appends)
        unknown_appends = set(append_keys) - set(write_keys)
        if unknown_appends:
            raise ValueError(
                f"node {name}@{version} 的 appends 必須是 writes 的子集，"
                f"未宣告為 writes: {sorted(unknown_appends)}"
            )
        key = (name, version)
        if key in _REGISTRY:
            raise ValueError(f"duplicate node: name={name} version={version}")
        _REGISTRY[key] = NodeSpec(
            name=name,
            version=version,
            description=description,
            reads=tuple(reads),
            writes=write_keys,
            deps=tuple(deps),
            requires_tools=tuple(requires_tools),
            run_on_fatal=run_on_fatal,
            factory=factory,
            appends=append_keys,
            dynamic_reads=tuple(dynamic_reads),
        )
        return factory

    return decorator


def get(name: str, version: str | None = None) -> NodeSpec | None:
    """依名稱（可選版本）查節點；未指定版本 → 解析到最新版。查無回 None。"""
    if version is not None:
        return _REGISTRY.get((name, version))
    candidates = [spec for (n, _), spec in _REGISTRY.items() if n == name]
    if not candidates:
        return None
    return max(candidates, key=lambda s: _version_key(s.version))


def all_specs() -> list[NodeSpec]:
    """所有已註冊節點，依 (名稱, 版本) 排序以維持穩定輸出順序。"""
    return sorted(_REGISTRY.values(), key=lambda s: (s.name, _version_key(s.version)))
