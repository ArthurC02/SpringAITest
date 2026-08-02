"""跨測試檔共用的假物件與 helper（各測試檔原本各有一份，上移去重）。"""

import asyncio
import dataclasses
from contextlib import contextmanager
from types import SimpleNamespace

import httpx

from app import skills
from app.engine import compiler
from app.engine import skill as skill_mod
from app.engine.skill import Skill
from app.nodes.kbquery.adapters import StaticGlossary
from tests.kbquery_fakes import RecordingAuditRepo

# backend 內部信任邊界的預設 token（＝settings.internal_api_token 的預設值）；多檔散布 import。
INTERNAL_TOKEN = "internal-dev-token"


def auth_headers(
    tenant_id="demo-a", user_id="alice", role="USER", token=INTERNAL_TOKEN
) -> dict:
    """組出符合服務間契約的標頭；預設是 demo-a 租戶的一般使用者。

    任一參數傳 None 即拔掉對應標頭（token 拔除是「缺 internal token → 401」等測試要的擴充版，
    test_config_apply / test_skills_api / test_skills_custom 原本各自帶一份逐字相同的 `_headers`，
    收斂於此）。四參數皆用預設時輸出與舊版固定四鍵完全一致。
    """
    headers = {}
    if token is not None:
        headers["X-Internal-Token"] = token
    if tenant_id is not None:
        headers["X-Tenant-Id"] = tenant_id
    if user_id is not None:
        headers["X-User-Id"] = user_id
    if role is not None:
        headers["X-User-Role"] = role
    return headers


class FakeBackendResponse:
    """假的 httpx.Response：只提供 retrieve 節點用得到的兩個方法。"""

    def __init__(self, chunks):
        self._chunks = chunks

    def raise_for_status(self) -> None:
        return None

    def json(self) -> dict:
        return {"chunks": self._chunks}


def patch_retrieve(monkeypatch, chunks) -> None:
    """monkeypatch httpx.AsyncClient.post → retrieve 節點收到 chunks（無額外斷言/記錄）。

    fake_post 內另有斷言或記錄行為（不只回 chunks）的測試各自保留 inline 版，不硬換。
    """

    async def fake_post(self, url, json=None, headers=None, **kwargs):
        return FakeBackendResponse(chunks)

    monkeypatch.setattr(httpx.AsyncClient, "post", fake_post)


def install_fake_get(monkeypatch, handler) -> None:
    """monkeypatch httpx.AsyncClient.get → handler(url, headers)；各 Fake 只留自己的 handle。"""

    async def fake_get(self, url, headers=None, **kwargs):  # noqa: ANN001
        return handler(url, headers or {})

    monkeypatch.setattr(httpx.AsyncClient, "get", fake_get)


def invoke_builtin(name: str, deps, **state) -> dict:
    """編譯指定 builtin skill 的圖、執行一次、回 public_output（三個逐字 `_invoke` 共用）。"""
    skill = skills.get(name).skill
    graph = compiler.compile(skill, deps)
    out = asyncio.run(graph.ainvoke({"tenant_id": "t", **state}))
    return compiler.public_output(out)


@contextmanager
def swap_skill(name: str, *, base: skills.LoadedSkill | None = None, **overrides):
    """暫時替換已載入的 Skill，並在離開時還原 registry。"""
    original = skills.get(name)
    source = base if base is not None else original
    skills._SKILLS[name] = dataclasses.replace(source, **overrides)
    try:
        yield skills._SKILLS[name]
    finally:
        if original is None:
            skills._SKILLS.pop(name, None)
        else:
            skills._SKILLS[name] = original


def register_builtin_skill(name: str, template: str, base_deps):
    """註冊測試用的內建 Skill，並回傳清理函式。"""
    skill = skill_mod.parse_source(template.format(name=name))
    loaded = skills.LoadedSkill(
        skill=skill,
        graph=compiler.compile(skill, base_deps),
        input_model=skill_mod.build_input_model(skill),
        deps=base_deps,
        recursion_limit=compiler.recursion_limit(skill),
        source="builtin",
    )
    skills._SKILLS[name] = loaded
    return lambda: skills._SKILLS.pop(name, None)


def default_engine_deps(**extra):
    """建立 engine 測試共用的依賴物件。"""
    fields = {
        "audit_repo": RecordingAuditRepo(),
        "llm": None,
        "glossary": StaticGlossary(),
        "max_retrieval_attempts": 2,
    }
    return SimpleNamespace(**{**fields, **extra})


def run_flow(
    flow: list[dict],
    state: dict | None = None,
    deps=None,
    input_schema: dict | None = None,
    seed_tenant: bool = True,
    **skill_extra,
) -> dict:
    """編譯並執行測試用 flow，回傳公開輸出。"""
    skill = Skill.model_validate(
        {
            "name": "probe-skill",
            "input_schema": input_schema or {},
            "flow": flow,
            **skill_extra,
        }
    )
    graph = compiler.compile(skill, deps or default_engine_deps())
    initial_state = (
        {"tenant_id": "t-test", **(state or {})} if seed_tenant else state or {}
    )
    return compiler.public_output(
        asyncio.run(graph.ainvoke(initial_state))
    )
