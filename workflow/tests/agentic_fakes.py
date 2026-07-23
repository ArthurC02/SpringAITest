"""agentic runner 測試用的手寫 fake（無 mocking library、確定性）。

- ScriptedChatModel：確定性 LiteLLM 替身。以一串 step 腳本決定每次 _generate 回 tool call
  還是 final answer；create_react_agent 一律走這顆，不打 LiteLLM、不耗 quota。
- make_package / make_agentic_skill：直接組出 AgentSkillPackage 與 kind=agentic 的 Skill。
- make_agentic_deps：組出帶 MemoryPackageReader + model factory + 可回收 audit_repo 的 deps。
"""

import asyncio
from typing import Any

from langchain_core.language_models.chat_models import BaseChatModel
from langchain_core.messages import AIMessage
from langchain_core.outputs import ChatGeneration, ChatResult

from app.engine import compiler
from app.engine.package import AgentSkillPackage
from app.engine.package_reader import MemoryPackageReader
from app.engine.skill import Skill
from app.nodes.kbquery.adapters import ScoreReranker, StaticGlossary
from app.nodes.kbquery.locators import (
    StructuredDataLocator,
    TableCellLocator,
    TextEvidenceLocator,
)
from app.skills.deps import KbQueryDeps
from tests.kbquery_fakes import RecordingAuditRepo


# ---------------------------------------------------------------------------
# 確定性 chat model
# ---------------------------------------------------------------------------

# step 腳本語法：("tool", registry_or_lc_name, args_dict) 或 ("final", text)
ToolStep = tuple[str, str, dict]
FinalStep = tuple[str, str]


class ScriptedChatModel(BaseChatModel):
    """確定性 chat model：第 i 次呼叫回 steps[i]（超出則沿用最後一個 step）。

    - steps 內每一項是 ('tool', name, args) → 回一則帶該 tool_call 的 AIMessage；
      ('final', text) → 回一則純文字 AIMessage（結束 loop）。
    - counter：以 mutable dict 記呼叫次數（避免重指定 pydantic field）。
    - LangChain/OpenAI 的 function name 不含 '.'；runner 把 registry 名的 '.' 轉底線，
      故腳本裡的 tool name 用底線版（例：'test_echo'）。
    """

    steps: list[Any]
    counter: dict[str, int]

    @property
    def _llm_type(self) -> str:
        return "scripted"

    def bind_tools(self, tools: Any, **kwargs: Any) -> "ScriptedChatModel":
        return self

    def _generate(self, messages, stop=None, run_manager=None, **kwargs) -> ChatResult:
        i = self.counter["calls"]
        self.counter["calls"] = i + 1
        step = self.steps[i] if i < len(self.steps) else self.steps[-1]
        if step[0] == "tool":
            _, name, args = step
            msg = AIMessage(
                content="",
                tool_calls=[{"name": name, "args": args, "id": f"call_{i}"}],
            )
        else:
            msg = AIMessage(content=step[1])
        return ChatResult(generations=[ChatGeneration(message=msg)])


def scripted_model(*steps: Any) -> ScriptedChatModel:
    return ScriptedChatModel(steps=list(steps), counter={"calls": 0})


def model_factory(*steps: Any):
    """回傳一個 () -> ScriptedChatModel 的 factory（runner 的 chat_model_factory）。"""
    model = scripted_model(*steps)
    return lambda: model, model


# ---------------------------------------------------------------------------
# package / skill / deps
# ---------------------------------------------------------------------------


def make_agentic_skill(
    *,
    name: str = "sales-helper",
    description: str = "銷售小幫手",
    uses_tools: list[str] | None = None,
    input_schema: dict | None = None,
    timeout_seconds: int | None = None,
) -> Skill:
    return Skill.model_validate(
        {
            "name": name,
            "description": description,
            "kind": "agentic",
            "input_schema": input_schema
            or {"query": {"type": "str", "required": True, "min_length": 1}},
            "uses_tools": uses_tools or [],
            "timeout_seconds": timeout_seconds,
            "flow": [],
        }
    )


def make_package(
    skill: Skill,
    *,
    instruction: str = "You are a helpful assistant.",
    resources: dict[str, bytes] | None = None,
    scripts: dict[str, str] | None = None,
    sha256: str = "deadbeef",
) -> AgentSkillPackage:
    return AgentSkillPackage(
        skill=skill,
        instruction=instruction,
        resources=dict(resources or {}),
        scripts=dict(scripts or {}),
        sha256=sha256,
    )


def make_agentic_deps(
    reader: MemoryPackageReader,
    chat_model_factory,
    audit_repo: RecordingAuditRepo | None = None,
) -> KbQueryDeps:
    """組出帶 agentic port 的 deps；audit_repo 可從 deps.audit_repo 取回斷言。"""
    return KbQueryDeps(
        llm=None,
        glossary=StaticGlossary(),
        searchers={},
        reranker=ScoreReranker(),
        locators={
            "text": TextEvidenceLocator(),
            "table": TableCellLocator(),
            "structured": StructuredDataLocator(),
        },
        audit_repo=audit_repo or RecordingAuditRepo(),
        agent_package_reader=reader,
        agent_chat_model=chat_model_factory,
    )


def run_agentic(skill: Skill, deps: KbQueryDeps, **state) -> dict:
    """編譯 agentic skill 的圖、跑一次、回 public_output（同 invoke 的身分注入慣例）。"""
    graph = compiler.compile(skill, deps)
    base = {"tenant_id": "demo-a", "user_id": "alice", "role": "USER"}
    base.update(state)
    out = asyncio.run(graph.ainvoke(base, config={"recursion_limit": 25}))
    return compiler.public_output(out)
