#!/usr/bin/env python3
"""Export validated P3-R2 artifact compatibility usage evidence."""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


EVENT_NAME = "artifact_compatibility_usage_total"
SERVICES = {"backend", "platform", "workflow"}
ARTIFACT_TYPES = ("agent_skill", "business_workflow", "unknown")
OUTCOMES = ("success", "rejected", "not_found", "error")
AUTHORITY = {
    "backend": {
        "public_skills": (
            "list", "read", "create", "update", "delete", "import", "export",
            "package", "revision_read", "revision_restore", "execution_artifact",
        ),
        "public_business_workflows": ("list", "read", "create", "update", "delete", "export"),
    },
    "platform": {
        "public_skills": ("validate", "invoke"),
        "public_business_workflows": ("validate",),
    },
    "workflow": {
        "workflow_validate_alias": ("validate",),
        "workflow_business_workflows_validate": ("validate",),
        "public_skills": ("invoke",),
        "workflow_unified_invoke": ("invoke",),
    },
}
MANIFEST_KEYS = {"schemaVersion", "deploymentVersion", "window", "sources", "retryInflationDisclosure"}
SOURCE_KEYS = {"service", "instance", "path", "coverageStartUtc", "coverageEndUtc"}
EVENT_KEYS = {
    "schemaVersion", "event", "timestampUtc", "deploymentVersion", "service", "surface",
    "operation", "resolvedArtifactType", "outcome", "count",
}


class ExportError(ValueError):
    pass


def _object(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ExportError(f"{label} must be a JSON object")
    return value


def _exact_keys(value: dict[str, Any], expected: set[str], label: str) -> None:
    if set(value) != expected:
        raise ExportError(f"{label} fields must be exactly: {', '.join(sorted(expected))}")


def _text(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ExportError(f"{label} must be a non-empty string")
    return value


def _utc(value: Any, label: str) -> datetime:
    text = _text(value, label)
    if not text.endswith("Z"):
        raise ExportError(f"{label} must be UTC with a Z suffix")
    try:
        parsed = datetime.fromisoformat(text[:-1] + "+00:00")
    except ValueError as error:
        raise ExportError(f"{label} is not a valid UTC timestamp") from error
    if parsed.tzinfo != timezone.utc:
        raise ExportError(f"{label} must be UTC")
    return parsed


def load_manifest(path: Path) -> dict[str, Any]:
    try:
        manifest = _object(json.loads(path.read_text(encoding="utf-8")), "manifest")
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ExportError(f"cannot read source manifest: {error}") from error
    _exact_keys(manifest, MANIFEST_KEYS, "manifest")
    if manifest["schemaVersion"] != 1:
        raise ExportError("manifest schemaVersion must be 1")
    deployment = _text(manifest["deploymentVersion"], "deploymentVersion")
    if deployment in {"unknown", "development"}:
        raise ExportError("deploymentVersion must identify one deployed build")
    disclosure = _text(manifest["retryInflationDisclosure"], "retryInflationDisclosure")
    window = _object(manifest["window"], "window")
    _exact_keys(window, {"startUtc", "endUtc"}, "window")
    start, end = _utc(window["startUtc"], "window.startUtc"), _utc(window["endUtc"], "window.endUtc")
    if start >= end:
        raise ExportError("window.startUtc must be before window.endUtc")
    sources = manifest["sources"]
    if not isinstance(sources, list) or not sources:
        raise ExportError("sources must be a non-empty array")
    identities: set[tuple[str, str]] = set()
    seen_services: set[str] = set()
    for index, raw in enumerate(sources):
        source = _object(raw, f"sources[{index}]")
        _exact_keys(source, SOURCE_KEYS, f"sources[{index}]")
        service = _text(source["service"], f"sources[{index}].service")
        instance = _text(source["instance"], f"sources[{index}].instance")
        _text(source["path"], f"sources[{index}].path")
        if service not in SERVICES:
            raise ExportError(f"sources[{index}].service is unsupported")
        if (service, instance) in identities:
            raise ExportError(f"duplicate source identity: {service}/{instance}")
        identities.add((service, instance))
        seen_services.add(service)
        coverage_start = _utc(source["coverageStartUtc"], f"sources[{index}].coverageStartUtc")
        coverage_end = _utc(source["coverageEndUtc"], f"sources[{index}].coverageEndUtc")
        if coverage_start > start or coverage_end < end or coverage_start >= coverage_end:
            raise ExportError(f"source {service}/{instance} does not cover the complete window")
    if seen_services != SERVICES:
        raise ExportError("sources must include backend, platform, and workflow")
    return {**manifest, "_deployment": deployment, "_disclosure": disclosure, "_start": start, "_end": end}


def _read_events(manifest_path: Path, manifest: dict[str, Any]) -> Counter[tuple[str, str, str, str, str]]:
    counts: Counter[tuple[str, str, str, str, str]] = Counter()
    for source_index, source in enumerate(manifest["sources"]):
        event_path = Path(source["path"])
        if not event_path.is_absolute():
            event_path = manifest_path.parent / event_path
        try:
            lines = event_path.read_text(encoding="utf-8").splitlines()
        except (OSError, UnicodeError) as error:
            raise ExportError(f"cannot read source {source['service']}/{source['instance']}: {error}") from error
        for line_number, line in enumerate(lines, 1):
            if not line.strip():
                raise ExportError(f"{event_path}:{line_number}: blank JSONL record")
            label = f"{event_path}:{line_number}"
            try:
                event = _object(json.loads(line), label)
            except json.JSONDecodeError as error:
                raise ExportError(f"{label}: malformed JSON") from error
            _exact_keys(event, EVENT_KEYS, label)
            if event["schemaVersion"] != 1 or event["event"] != EVENT_NAME:
                raise ExportError(f"{label}: unsupported event schema or name")
            if event["deploymentVersion"] != manifest["_deployment"]:
                raise ExportError(f"{label}: deployment version does not match manifest")
            timestamp = _utc(event["timestampUtc"], f"{label}.timestampUtc")
            if not manifest["_start"] <= timestamp < manifest["_end"]:
                raise ExportError(f"{label}: event timestamp is outside the window")
            service = event["service"]
            surface = event["surface"]
            operation = event["operation"]
            artifact_type = event["resolvedArtifactType"]
            outcome = event["outcome"]
            if service != source["service"]:
                raise ExportError(f"{label}: event service does not match source")
            if surface == "unknown_origin":
                raise ExportError(f"{label}: unknown_origin blocks export")
            if service not in AUTHORITY or operation not in AUTHORITY[service].get(surface, ()):
                raise ExportError(f"{label}: non-authoritative service/surface/operation")
            if artifact_type not in ARTIFACT_TYPES or outcome not in OUTCOMES:
                raise ExportError(f"{label}: unbounded dimension value")
            count = event["count"]
            if isinstance(count, bool) or not isinstance(count, int) or count <= 0:
                raise ExportError(f"{label}: count must be a positive integer")
            counts[(service, surface, operation, artifact_type, outcome)] += count
    return counts


def export(manifest_path: Path) -> dict[str, Any]:
    manifest = load_manifest(manifest_path)
    observed = _read_events(manifest_path, manifest)
    rows = []
    for service, surfaces in AUTHORITY.items():
        for surface, operations in surfaces.items():
            for operation in operations:
                for artifact_type in ARTIFACT_TYPES:
                    for outcome in OUTCOMES:
                        key = service, surface, operation, artifact_type, outcome
                        rows.append({
                            "service": service, "surface": surface, "operation": operation,
                            "resolvedArtifactType": artifact_type, "outcome": outcome,
                            "count": observed[key],
                        })
    return {
        "schemaVersion": 1,
        "deploymentVersion": manifest["_deployment"],
        "window": manifest["window"],
        "sources": [
            {
                "service": source["service"], "instance": source["instance"],
                "coverageStartUtc": source["coverageStartUtc"],
                "coverageEndUtc": source["coverageEndUtc"],
            }
            for source in manifest["sources"]
        ],
        "retryInflationDisclosure": manifest["_disclosure"],
        "counts": rows,
    }


def _write_atomic(path: Path, bundle: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(bundle, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_manifest", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        bundle = export(args.source_manifest)
        _write_atomic(args.output, bundle)
    except ExportError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
