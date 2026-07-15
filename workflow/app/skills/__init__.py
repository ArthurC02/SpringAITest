"""內建 Skill 的載入處：repo 內的 *.yaml 於啟動時解析、編譯成可執行圖。

內建 skill 不入 DB（設計文稿 §3），因此這裡就是它們的唯一事實來源。載入即編譯的
理由是 fail fast：YAML 打錯字（節點名、條件式）要在服務啟動當下就炸，而不是等到
第一個使用者呼叫 invoke 才 500。

依賴注入：節點不碰全域 settings，deps 由這裡按 skill 名字組好傳給 compiler。
P2 只有 kb_query 一個內建 skill，直接沿用 workflows/kb_query.py 已經組好的正式依賴
（同一份依賴組合不重寫第二遍）；自訂 skill（P4，來自 backend）走同一條路徑。
"""

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from langgraph.graph.state import CompiledStateGraph
from pydantic import BaseModel

from app.engine import compiler
from app.engine import skill as skill_mod
from app.engine.skill import Skill

# import 亦觸發初始 4 個 tool 的 @tool 註冊：skill 的 tool/script 步驟在編譯期就要查得到
from app import tools as _tools  # noqa: F401

# import 亦觸發 kb_query 十節點的 @node 註冊（build_input_model 之外，compile 需要它們）
from app.workflows.kb_query import _default_deps as _kb_query_deps

_SKILL_DIR = Path(__file__).parent

# skill 名 → 依賴組裝函式。沒有對應項目的 skill 以 None 建圖（節點若需要依賴會在編譯期炸）。
_DEPS_BUILDERS = {"kb_query": _kb_query_deps}


@dataclass(frozen=True)
class LoadedSkill:
    """一個可執行的 skill：定義 + 編譯後的圖 + 輸入驗證用的動態 model。"""

    skill: Skill
    graph: CompiledStateGraph
    input_model: type[BaseModel] | None
    deps: Any
    recursion_limit: int = 100  # 全圖護欄；由 compiler 依 flow 結構算出上界
    source: str = "builtin"


_SKILLS: dict[str, LoadedSkill] = {}


def _load_builtin() -> None:
    for path in sorted(_SKILL_DIR.glob("*.yaml")):
        skill = skill_mod.parse_source(path.read_text(encoding="utf-8"))
        deps_builder = _DEPS_BUILDERS.get(skill.name)
        deps = deps_builder() if deps_builder else None
        _SKILLS[skill.name] = LoadedSkill(
            skill=skill,
            graph=compiler.compile(skill, deps),
            input_model=skill_mod.build_input_model(skill),
            deps=deps,
            recursion_limit=compiler.recursion_limit(skill),
        )


def get(name: str) -> LoadedSkill | None:
    """依名稱查已載入的 skill；查無回 None。"""
    return _SKILLS.get(name)


def all_skills() -> list[LoadedSkill]:
    """所有已載入的 skill，依名稱排序以維持穩定輸出順序。"""
    return sorted(_SKILLS.values(), key=lambda s: s.skill.name)


_load_builtin()
