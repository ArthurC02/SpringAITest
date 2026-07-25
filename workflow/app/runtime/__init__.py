"""D3 direct-Agent execution runtime.

This package is deliberately separate from ``agent_skill_runner``. The latter
remains the legacy explicit Skill executor; this package owns durable,
revision-pinned Agent runs and their governance wrapper.
"""

from app.runtime.models import DirectAgentExecutionSnapshot

__all__ = ["DirectAgentExecutionSnapshot"]
