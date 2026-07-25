from __future__ import annotations

import base64
import binascii
import hashlib
from dataclasses import dataclass
from typing import Any, Mapping
from urllib.parse import quote

import httpx
from pydantic import ConfigDict, BaseModel, Field

from app.backend_http import get_client
from app.engine import package
from app.engine.package import (
    MAX_PACKAGE_BASE64_CHARS,
    MAX_PACKAGE_RAW_BYTES,
    AgentSkillPackage,
)
from app.engine.skill import Skill, parse_source
from app.runtime.models import PinnedSkillSummary
from app.security import RequestContext
from app.settings import settings

MAX_RESOURCE_BYTES = 16_384


class ArtifactError(RuntimeError):
    """A pinned artifact was unavailable, corrupt, or did not match its pin."""


class SkillExecutionArtifact(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    name: str
    revision: int = Field(ge=1)
    kind: str
    definition: str
    definition_sha256: str
    package_sha256: str | None = None
    package_base64: str | None = None


@dataclass(frozen=True)
class LoadedSkillArtifact:
    name: str
    revision: int
    kind: str
    definition_sha256: str
    package_sha256: str | None
    skill: Skill
    instruction: str
    instruction_sha256: str
    resources: Mapping[str, bytes]
    scripts_present: bool

    @property
    def uses_tools(self) -> frozenset[str]:
        return frozenset(self.skill.uses_tools)


def _sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def _decode_package(value: str) -> bytes:
    if len(value) > MAX_PACKAGE_BASE64_CHARS:
        raise ArtifactError("skill package exceeds the runtime artifact limit")
    try:
        raw = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise ArtifactError("skill package is not valid base64") from exc
    if (
        len(raw) > MAX_PACKAGE_RAW_BYTES
        or len(base64.b64encode(raw)) > MAX_PACKAGE_BASE64_CHARS
    ):
        raise ArtifactError("skill package exceeds the runtime artifact limit")
    return raw


def verify_and_load_artifact(
    raw: dict[str, Any], pin: PinnedSkillSummary
) -> LoadedSkillArtifact:
    """Validate both Backend envelope hashes and Workflow package semantics."""

    try:
        artifact = SkillExecutionArtifact.model_validate(raw)
    except Exception as exc:
        raise ArtifactError("skill execution artifact has an invalid shape") from exc
    if (
        artifact.name != pin.name
        or artifact.revision != pin.revision
        or artifact.kind != pin.kind
    ):
        raise ArtifactError("skill execution artifact does not match the pinned revision")
    definition_hash = _sha256_text(artifact.definition)
    if definition_hash != artifact.definition_sha256 or definition_hash != pin.definition_sha256:
        raise ArtifactError("skill definition hash does not match the pinned revision")

    if artifact.kind == "agentic":
        if (
            artifact.package_base64 is None
            or artifact.package_sha256 is None
            or pin.package_sha256 is None
        ):
            raise ArtifactError("pinned agentic skill has no immutable package")
        raw_package = _decode_package(artifact.package_base64)
        package_hash = hashlib.sha256(raw_package).hexdigest()
        if (
            package_hash != artifact.package_sha256
            or package_hash != pin.package_sha256
        ):
            raise ArtifactError("skill package hash does not match the pinned revision")
        try:
            parsed = package.parse_package(raw_package, pin.name)
        except package.PackageError as exc:
            raise ArtifactError("pinned skill package failed semantic validation") from exc
        if parsed.kind != "agentic" or parsed.agentic is None:
            raise ArtifactError("pinned artifact kind does not match its package")
        agentic: AgentSkillPackage = parsed.agentic
        # Backend definition and package frontmatter are two projections of the
        # same immutable revision. Pin both; neither is allowed to drift.
        if agentic.skill.name != pin.name:
            raise ArtifactError("skill package name does not match its pin")
        instruction = agentic.instruction
        return LoadedSkillArtifact(
            name=pin.name,
            revision=pin.revision,
            kind=pin.kind,
            definition_sha256=definition_hash,
            package_sha256=package_hash,
            skill=agentic.skill.model_copy(update={"revision": pin.revision}),
            instruction=instruction,
            instruction_sha256=_sha256_text(instruction),
            resources=agentic.resources,
            # D3 never executes package scripts. Presence is retained only for
            # a sanitized audit fact.
            scripts_present=bool(agentic.scripts),
        )

    package_hash: str | None = None
    package_scripts_present = False
    if artifact.package_base64 is not None or artifact.package_sha256 is not None:
        if (
            artifact.package_base64 is None
            or artifact.package_sha256 is None
            or pin.package_sha256 is None
        ):
            raise ArtifactError("pinned flow package metadata is incomplete")
        raw_package = _decode_package(artifact.package_base64)
        package_hash = hashlib.sha256(raw_package).hexdigest()
        if (
            package_hash != artifact.package_sha256
            or package_hash != pin.package_sha256
        ):
            raise ArtifactError("flow package hash does not match the pinned revision")
        try:
            parsed_flow = package.parse_package(raw_package, pin.name)
        except package.PackageError as exc:
            raise ArtifactError("pinned flow package failed semantic validation") from exc
        if parsed_flow.kind != "flow" or parsed_flow.agentic is not None:
            raise ArtifactError("pinned flow artifact kind does not match its package")
        if parsed_flow.canonical_definition != artifact.definition:
            raise ArtifactError(
                "flow package definition does not match the immutable artifact"
            )
        skill = parsed_flow.skill
        package_scripts_present = any(
            entry.startswith("scripts/") for entry in parsed_flow.entries
        )
    else:
        if pin.package_sha256 is not None:
            raise ArtifactError("pinned flow package is unavailable")
        try:
            skill = parse_source(artifact.definition)
        except Exception as exc:
            raise ArtifactError("pinned flow definition failed semantic validation") from exc
    if skill.name != pin.name or skill.kind != "flow":
        raise ArtifactError("pinned flow definition does not match its pin")
    skill = skill.model_copy(update={"revision": pin.revision})
    return LoadedSkillArtifact(
        name=pin.name,
        revision=pin.revision,
        kind=pin.kind,
        definition_sha256=definition_hash,
        package_sha256=package_hash,
        skill=skill,
        instruction="",
        instruction_sha256=_sha256_text(""),
        resources={},
        scripts_present=package_scripts_present or _flow_contains_script(skill.flow),
    )


def _flow_contains_script(steps: list[dict[str, Any]]) -> bool:
    for step in steps:
        if "script" in step:
            return True
        if isinstance(step.get("sequence"), list) and _flow_contains_script(step["sequence"]):
            return True
        branch = step.get("branch")
        if isinstance(branch, dict):
            if _flow_contains_script(branch.get("then") or []):
                return True
            if _flow_contains_script(branch.get("else") or []):
                return True
        loop = step.get("loop")
        if isinstance(loop, dict) and _flow_contains_script(loop.get("body") or []):
            return True
    return False


class RevisionArtifactReader:
    """Read an exact immutable Skill revision from Backend."""

    async def read(
        self, pin: PinnedSkillSummary, ctx: RequestContext
    ) -> LoadedSkillArtifact:
        headers = {
            "X-Internal-Token": settings.internal_api_token,
            "X-Tenant-Id": ctx.tenant_id,
            "X-User-Id": ctx.user_id,
            "X-User-Role": ctx.role,
        }
        name = quote(pin.name, safe="")
        try:
            response = await get_client().get(
                f"/api/skills/{name}/revisions/{pin.revision}/execution-artifact",
                headers=headers,
                timeout=httpx.Timeout(10.0),
            )
            if response.status_code == 404:
                raise ArtifactError("pinned skill revision is unavailable")
            response.raise_for_status()
            body = response.json()
        except ArtifactError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise ArtifactError("Backend skill artifact request failed") from exc
        return verify_and_load_artifact(body, pin)


def read_artifact_resource(artifact: LoadedSkillArtifact, path: str) -> str:
    if artifact.kind != "agentic":
        raise ArtifactError("legacy flow artifacts have no readable package resources")
    if not isinstance(path, str) or not path.strip():
        raise ArtifactError("resource path must not be blank")
    normalized = path.strip().replace("\\", "/")
    if (
        normalized.startswith("/")
        or ":" in normalized.split("/", 1)[0]
        or any(part in {".", "..", ""} for part in normalized.split("/"))
        or not normalized.startswith(("references/", "assets/"))
    ):
        raise ArtifactError("resource path is outside the active Skill package")
    value = artifact.resources.get(normalized)
    if value is None:
        raise ArtifactError("resource is not present in the active Skill package")
    return value[:MAX_RESOURCE_BYTES].decode("utf-8", errors="replace")
