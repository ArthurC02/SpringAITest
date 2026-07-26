"""Imports that register every context-enrichment node."""

from app.nodes.context_enrichment.nodes.assess_coverage import make_assess_coverage_node
from app.nodes.context_enrichment.nodes.build_facts import make_build_facts_node
from app.nodes.context_enrichment.nodes.build_requirements import make_build_requirements_node
from app.nodes.context_enrichment.nodes.build_views import make_build_views_node
from app.nodes.context_enrichment.nodes.deduplicate_evidence import make_deduplicate_evidence_node
from app.nodes.context_enrichment.nodes.detect_conflicts import make_detect_conflicts_node
from app.nodes.context_enrichment.nodes.discover_sources import make_discover_sources_node
from app.nodes.context_enrichment.nodes.expand_requirements import make_expand_requirements_node
from app.nodes.context_enrichment.nodes.normalize_evidence import make_normalize_evidence_node
from app.nodes.context_enrichment.nodes.normalize_request import make_normalize_request_node
from app.nodes.context_enrichment.nodes.resolve_security_scope import make_resolve_security_scope_node
from app.nodes.context_enrichment.nodes.resolve_time import make_resolve_time_node
from app.nodes.context_enrichment.nodes.retrieve_documents import make_retrieve_documents_node
from app.nodes.context_enrichment.nodes.submit_revision import make_submit_revision_node
from app.nodes.context_enrichment.nodes.task_local_context import make_task_local_context_node
from app.nodes.context_enrichment.nodes.validate_job import make_validate_job_node

__all__ = [name for name in globals() if name.startswith("make_")]
