"""kb_query 族節點共用的依賴容器與正式環境組裝：Skill 引擎的 DI 組裝點。

KbQueryDeps 與 _default_deps 原本活在已退役的手寫圖（app/workflows/kb_query.py +
app/kbquery/graph.py）；Node-First 遷移後這裡是唯一的組裝點，節點不碰全域 settings
或單例，全部由這裡注入（app/skills/__init__.py、custom.py、config_apply.py 共用同一份）。
"""

from dataclasses import dataclass, field
from typing import Any, Callable

from app.engine.package_reader import AgentSkillPackageReader
from app.engine.script_policy import configured_script_runner
from app.llm import get_llm
from app.nodes.kbquery.adapters import (
    BackendVectorSearch,
    LangChainStructuredLLM,
    LoggingAuditRepository,
    ScoreReranker,
    StaticGlossary,
)
from app.nodes.kbquery.locators import (
    StructuredDataLocator,
    TableCellLocator,
    TextEvidenceLocator,
)
from app.nodes.kbquery.ports import (
    AuditRepositoryPort,
    EvidenceLocatorPort,
    GlossaryPort,
    RerankerPort,
    SearchPort,
    StructuredLLMPort,
)
from app.nodes.context_enrichment.adapters import (
    BackendContextPolicy,
    BackendContextRetrieval,
    BackendContextStore,
)
from app.nodes.context_enrichment.ports import (
    ContextPolicyPort,
    ContextRetrievalPort,
    ContextStorePort,
    TaskContextPort,
)
from app.settings import settings


@dataclass
class KbQueryDeps:
    """kb_query 族節點的所有外部依賴；節點不碰全域 settings 或單例，全部由這裡注入。"""

    llm: StructuredLLMPort | None
    glossary: GlossaryPort
    searchers: dict[str, SearchPort]
    reranker: RerankerPort
    locators: dict[str, EvidenceLocatorPort]
    audit_repo: AuditRepositoryPort
    default_top_k: int = 8
    max_retrieval_attempts: int = 2
    # intent_classification 的 LLM 補位信心門檻（P4c 促升，per-config 可覆寫；預設對齊節點內建 0.6）
    intent_confidence_threshold: float = 0.6
    # agentic runner 用（設計 §4.2）：package reader port 與 create_react_agent 的 chat model
    # factory。皆有預設，既有 flow 的 KbQueryDeps 建構（含測試 fake）不受影響；agentic 才用到。
    agent_package_reader: AgentSkillPackageReader | None = None
    agent_chat_model: Callable[[], Any] | None = None
    write_evidence_sink: Any | None = None
    # Context Enrichment reuses this service-level assembly point rather than
    # introducing a second container.  These ports are only consumed by the
    # server-owned context-enrichment skill.
    context_policy: ContextPolicyPort | None = None
    context_store: ContextStorePort | None = None
    context_retrieval: ContextRetrievalPort | None = None
    context_task_backend: TaskContextPort | None = None
    # Every dependency set receives an explicit deployment-policy runner.
    # Tests that need Development behavior inject RestrictedInProcessRunner.
    script_runner: Any = field(default_factory=configured_script_runner)


def _default_deps() -> KbQueryDeps:
    """組出正式環境的依賴組合。"""
    from app.skills.package_reader import BackendPackageReader
    from app.runtime.write_evidence import BackendWriteEvidenceSink
    from app.runtime.orchestrator_backend import OrchestratorBackendClient

    return KbQueryDeps(
        llm=LangChainStructuredLLM(),
        glossary=StaticGlossary(),
        # ponytail: 只串接 vector；keyword/metadata/table/structured 檢索來源
        # 後端尚未提供，等 API 就緒後在這個 dict 加上對應 adapter 即可
        searchers={"vector": BackendVectorSearch()},
        reranker=ScoreReranker(),
        locators={
            "text": TextEvidenceLocator(),
            "table": TableCellLocator(),
            "structured": StructuredDataLocator(),
        },
        audit_repo=LoggingAuditRepository(),
        default_top_k=settings.kb_query_top_k,
        max_retrieval_attempts=settings.kb_query_max_retrieval_attempts,
        agent_package_reader=BackendPackageReader(),
        agent_chat_model=get_llm,
        write_evidence_sink=BackendWriteEvidenceSink(),
        context_policy=BackendContextPolicy(),
        context_store=BackendContextStore(),
        context_retrieval=BackendContextRetrieval(settings.multi_agent_context_top_k),
        context_task_backend=OrchestratorBackendClient(),
    )
