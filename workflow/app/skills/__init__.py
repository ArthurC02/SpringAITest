"""內建 Skill 的載入處：repo 內的 *.yaml 於啟動時解析、編譯成可執行圖。

內建 skill 不入 DB（設計文稿 §3），因此這裡就是它們的唯一事實來源。載入即編譯的
理由是 fail fast：YAML 打錯字（節點名、條件式）要在服務啟動當下就炸，而不是等到
第一個使用者呼叫 invoke 才 500。

依賴注入：節點不碰全域 settings。唯一例外是 `retrieve` 的 top_k 最終回落（優先序:
config seed > 建構參數 > 全域 settings）。deps 由這裡按 skill 名字組好傳給 compiler。
P2 只有 kb_query 一個內建 skill，直接沿用 deps.py 已經組好的正式依賴
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
from app.skills.deps import _default_deps as _kb_query_deps
from app.nodes.kbquery import nodes as _kbquery_nodes  # noqa: F401
from app.nodes.context_enrichment import nodes as _context_enrichment_nodes  # noqa: F401

# 內建骨架 template_* 引用 nl_logic / retrieve 節點：_load_builtin 在本模組匯入時就會編譯
# 這些骨架，因此節點的 @node 註冊必須先觸發。skills 匯入即自足（不倚賴呼叫端先匯入
# app.main），否則單獨匯入 app.skills（例如 test_skill_kbquery_parity_e2e 只 import skills）
# 會在冷啟動編譯期炸 unknown node。移除任一行 → 冷啟動編譯失敗、測試轉紅（縫⑥⑦ 護欄）。
from app.nodes import nl_logic as _nl_logic  # noqa: F401
from app.nodes import retrieve as _retrieve  # noqa: F401

# Node-First 遷移（Phase 1）：rag_qa/summarize/triage/analyze_report 四顆內建 skill
# 引用的節點，同樣必須在 _load_builtin 編譯前先觸發 @node 註冊（理由同上）。
from app.nodes import analyze_report as _analyze_report_nodes  # noqa: F401
from app.nodes import rag_answer as _rag_answer  # noqa: F401
from app.nodes import summarize_text as _summarize_text  # noqa: F401
from app.nodes import triage as _triage_nodes  # noqa: F401

# agentic runner 節點（@node agent_skill_runner）：custom.load 對 kind: agentic 的自訂 skill
# 走 compiler 的 agentic 分派，compile 需先能 resolve 此節點（縫：移除即 agentic 編譯期炸）。
from app.nodes import agent_skill_runner as _agent_skill_runner  # noqa: F401

_SKILL_DIR = Path(__file__).parent

# 五支 template_* 骨架:簡單模式 compose 的基底。與 kb_query 共用同一組正式依賴
# （KbQueryDeps 已含 llm/檢索/稽核各埠,同時滿足 nl_logic 的 deps=["llm"]、kb 族與
# query_intake 的各埠）。deps 映射必須與五支 yaml 同批存在:查無 → deps=None →
# 節點需要依賴時編譯期就炸（縫③,整服務起不來）。
_TEMPLATE_NAMES = (
    "template-retrieval",
    "template-compare",
    "template-stats",
    "template-infer",
    "template-inspire",
)

# Node-First 遷移（Phase 1）四顆新內建 skill：與 kb_query/template_* 共用同一組
# 正式依賴——rag_answer/summarize_text/triage_*/doc_insights/report_synthesize
# 只用得到其中的 llm 埠，其餘（檢索/稽核各埠）用不到但無妨（同一支 KbQueryDeps
# 也滿足編譯器強制附加的 audit_feedback 節點所需的 audit_repo）。
_NODE_FIRST_MIGRATION_NAMES = ("rag-qa", "summarize", "triage", "analyze-report")
# 內部 skill：Root runtime 自己用的組件，不是使用者資產。它們的 invoke 已在 main.py
# 前置 404，這裡同步從 catalog 隱藏——否則任何登入者仍讀得到完整 YAML 定義。
_INTERNAL_SKILL_NAMES = frozenset({"context-enrichment", "context-task-local"})

# skill 名 → 依賴組裝函式。沒有對應項目的 skill 以 None 建圖（節點若需要依賴會在編譯期炸）。
_DEPS_BUILDERS = {
    "kb-query": _kb_query_deps,
    "context-enrichment": _kb_query_deps,
    "context-task-local": _kb_query_deps,
    **{name: _kb_query_deps for name in _TEMPLATE_NAMES},
    **{name: _kb_query_deps for name in _NODE_FIRST_MIGRATION_NAMES},
}


@dataclass(frozen=True)
class LoadedSkill:
    """一個可執行的 skill：定義 + 編譯後的圖 + 輸入驗證用的動態 model。"""

    skill: Skill
    graph: CompiledStateGraph
    input_model: type[BaseModel] | None
    deps: Any
    recursion_limit: int = 100  # 全圖護欄；由 compiler 依 flow 結構算出上界
    source: str = "builtin"
    # 骨架原文:前端 compose 要拿內建 template_* 的 YAML 原文做定點 patch,而 parse 後的
    # Skill model 丟了原文。內建項在載入時就把 path.read_text 帶進來（縫②）;custom 不需要。
    definition: str = ""


_SKILLS: dict[str, LoadedSkill] = {}


def _load_builtin() -> None:
    for path in sorted(_SKILL_DIR.glob("*.yaml")):
        raw = path.read_text(encoding="utf-8")
        skill = skill_mod.parse_source(raw)
        deps_builder = _DEPS_BUILDERS.get(skill.name)
        deps = deps_builder() if deps_builder else None
        _SKILLS[skill.name] = LoadedSkill(
            skill=skill,
            graph=compiler.compile(skill, deps),
            input_model=skill_mod.build_input_model(skill),
            deps=deps,
            recursion_limit=compiler.recursion_limit(skill),
            definition=raw,
        )


def get(name: str) -> LoadedSkill | None:
    """依名稱查已載入的 skill；查無回 None。"""
    return _SKILLS.get(name)


def all_skills() -> list[LoadedSkill]:
    """所有已載入的 skill，依名稱排序以維持穩定輸出順序。"""
    return sorted(
        (skill for skill in _SKILLS.values() if skill.skill.name not in _INTERNAL_SKILL_NAMES),
        key=lambda skill: skill.skill.name,
    )


_load_builtin()
