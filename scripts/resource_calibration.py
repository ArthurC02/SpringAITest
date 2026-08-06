#!/usr/bin/env python3
"""Fail-closed resource-calibration inventory validation and collection.

The collector intentionally writes only aggregate resource measurements.  Docker
container identifiers are used only while querying Docker and never enter the
bundle, errors, or logs.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import math
import os
import platform as host_platform
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Callable, Iterable, Sequence


SCHEMA_VERSION = 1
LANES = frozenset({"default", "full", "evidence", "evidence-real"})
SNAPSHOTS = ("base-production", "evidence-production", "base-development", "evidence-development")
SCOPES = frozenset({"counterpart_candidate", "shared_workload", "distinct_workload"})
SERVICE_CLASSES = frozenset({"application", "broker", "datastore", "evidence_support", "frontend", "memory_service", "model_gateway", "observability"})
SERVICE_NAME = re.compile(r"^[a-z][a-z0-9-]{0,62}$")
WORKLOAD_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
CONTAINER_ID = re.compile(r"^[0-9a-f]{12,64}$")
MEMORY_VALUE = re.compile(r"^([0-9]+(?:\.[0-9]+)?)(B|kB|KB|KiB|MB|MiB|GB|GiB|TB|TiB)$")
MEMORY_MULTIPLIERS = {
    "B": 1,
    "kB": 1_000,
    "KB": 1_000,
    "KiB": 1_024,
    "MB": 1_000_000,
    "MiB": 1_048_576,
    "GB": 1_000_000_000,
    "GiB": 1_073_741_824,
    "TB": 1_000_000_000_000,
    "TiB": 1_099_511_627_776,
}
RESOURCE_FIELDS = frozenset({"mem_limit", "cpus", "pids_limit"})


class CalibrationError(RuntimeError):
    """A deterministic, safe-to-report calibration failure."""


class CalibrationBlocked(CalibrationError):
    """A safe stop condition that must not be mistaken for a complete bundle."""


Runner = Callable[[Sequence[str], str], str]


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise CalibrationError(message)


def _exact_keys(value: dict[str, Any], required: set[str], label: str) -> None:
    _require(set(value) == required, f"{label} has an unsupported or missing field")


def validate_inventory_data(data: Any) -> dict[str, Any]:
    """Validate the checked-in contract without accepting a partial inventory."""
    _require(isinstance(data, dict), "inventory must be a JSON object")
    _exact_keys(data, {"schema_version", "phase", "measurement_contract", "compose_snapshots", "workloads", "services"}, "inventory")
    _require(data["schema_version"] == SCHEMA_VERSION, "unsupported inventory schema version")
    _require(data["phase"] == "exploratory_no_ceilings", "inventory must remain exploratory without ceilings")
    contract = data["measurement_contract"]
    _require(isinstance(contract, dict), "measurement contract must be an object")
    _exact_keys(contract, {"baseline_only", "ceiling_approving"}, "measurement contract")
    _require(contract == {"baseline_only": True, "ceiling_approving": False}, "measurement contract is invalid")
    snapshots = data["compose_snapshots"]
    _require(isinstance(snapshots, dict), "Compose snapshot contract must be an object")
    _exact_keys(snapshots, set(SNAPSHOTS), "Compose snapshot contract")
    for lanes in snapshots.values():
        _require(isinstance(lanes, list) and lanes and all(lane in LANES for lane in lanes) and len(set(lanes)) == len(lanes), "Compose snapshot lanes are invalid")
    _require(snapshots["base-production"] == snapshots["base-development"], "base Compose snapshots must select the same lane")
    _require(snapshots["evidence-production"] == snapshots["evidence-development"], "evidence Compose snapshots must select the same lane")
    workloads = data["workloads"]
    _require(isinstance(workloads, list) and len(workloads) == len(LANES), "workload manifest is incomplete")
    workload_keys = {"id", "version", "lane", "kind", "sample_window_seconds", "samples", "ceiling_approving"}
    workload_lanes: set[str] = set()
    workload_ids: set[str] = set()
    for workload in workloads:
        _require(isinstance(workload, dict), "workload manifest entry must be an object")
        _exact_keys(workload, workload_keys, "workload manifest entry")
        identifier = workload["id"]
        lane = workload["lane"]
        _require(isinstance(identifier, str) and WORKLOAD_ID.fullmatch(identifier) is not None, "workload ID is invalid")
        _require(lane in LANES and lane not in workload_lanes and identifier not in workload_ids, "workload manifest lane is invalid")
        _require(identifier == f"steady-running-{lane}-v1", "workload ID does not match the reviewed baseline")
        _require(workload["version"] == 1 and workload["kind"] == "steady_running", "workload manifest is invalid")
        _require(workload["sample_window_seconds"] == 60 and workload["samples"] == 60, "workload sampling contract is invalid")
        _require(workload["ceiling_approving"] is False, "workload must not approve ceilings")
        workload_lanes.add(lane)
        workload_ids.add(identifier)
    _require(workload_lanes == LANES, "workload manifest does not cover every execution lane")
    services = data["services"]
    _require(isinstance(services, list) and services, "inventory must contain services")

    expected_keys = {
        "service",
        "service_class",
        "execution_scopes",
        "comparison_scope",
        "counterpart",
        "calibration_status",
    }
    seen: set[str] = set()
    by_service: dict[str, dict[str, Any]] = {}
    for item in services:
        _require(isinstance(item, dict), "inventory service entry must be an object")
        _exact_keys(item, expected_keys, "inventory service entry")
        service = item["service"]
        scope = item["comparison_scope"]
        _require(isinstance(service, str) and SERVICE_NAME.fullmatch(service) is not None, "inventory service name is invalid")
        _require(item["service_class"] in SERVICE_CLASSES, "inventory service class is invalid")
        execution_scopes = item["execution_scopes"]
        _require(isinstance(execution_scopes, list) and execution_scopes, "inventory execution scopes are invalid")
        _require(all(lane in LANES for lane in execution_scopes) and len(set(execution_scopes)) == len(execution_scopes), "inventory execution scopes are invalid")
        _require(scope in SCOPES, "inventory comparison scope is invalid")
        _require(item["calibration_status"] == "calibration_pending", "only calibration_pending entries are valid before approval")
        _require(service not in seen, "inventory service names must be globally unique")
        seen.add(service)
        by_service[service] = item
        counterpart = item["counterpart"]
        if scope == "counterpart_candidate":
            _require(isinstance(counterpart, str) and SERVICE_NAME.fullmatch(counterpart) is not None, "counterpart candidate entry is missing counterpart")
        else:
            _require(counterpart is None, "shared or distinct workload must not declare counterpart")

    for service, item in by_service.items():
        if item["comparison_scope"] != "counterpart_candidate":
            continue
        counterpart = by_service.get(item["counterpart"])
        _require(counterpart is not None, "counterpart candidate is not inventoried")
        _require(counterpart["comparison_scope"] == "counterpart_candidate", "counterpart does not have candidate scope")
        _require(counterpart["counterpart"] == service, "counterpart relationship is not symmetric")
        _require(counterpart["service_class"] == item["service_class"], "counterpart service class differs")
    _require(set().union(*(set(item["execution_scopes"]) for item in services)) == LANES, "inventory does not cover every execution lane")
    return data


def load_inventory(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise CalibrationError("inventory cannot be read as JSON") from exc
    return validate_inventory_data(data)


def services_for_lane(inventory: dict[str, Any], lane: str) -> list[dict[str, Any]]:
    _require(lane in LANES, "collection lane is invalid")
    selected = [item for item in inventory["services"] if lane in item["execution_scopes"]]
    _require(selected, "collection lane has no inventoried services")
    return selected


def services_for_scopes(inventory: dict[str, Any], scopes: Iterable[str]) -> list[dict[str, Any]]:
    scope_set = set(scopes)
    _require(scope_set and scope_set.issubset(LANES), "inventory execution scopes are invalid")
    return [item for item in inventory["services"] if scope_set.intersection(item["execution_scopes"])]


def workload_for_id(inventory: dict[str, Any], workload_id: str) -> dict[str, Any]:
    _require(isinstance(workload_id, str) and WORKLOAD_ID.fullmatch(workload_id) is not None, "workload ID is invalid")
    for workload in inventory["workloads"]:
        if workload["id"] == workload_id:
            return workload
    raise CalibrationError("workload ID is not in the reviewed manifest")


def _reject_resource_fields(value: Any, snapshot: str, path: str = "") -> None:
    if not isinstance(value, dict):
        return
    for key, child in value.items():
        child_path = f"{path}.{key}" if path else key
        if key in RESOURCE_FIELDS:
            if child in (None, "", {}, []):
                raise CalibrationError(f"{snapshot} has an empty resource field")
            raise CalibrationError(f"{snapshot} declares a resource field before calibration")
        if key == "resources" and path.endswith("deploy"):
            if child in (None, "", {}, []):
                raise CalibrationError(f"{snapshot} has an empty deploy resource field")
            raise CalibrationError(f"{snapshot} declares deploy resources before calibration")
        _reject_resource_fields(child, snapshot, child_path)


def compose_services(path: Path, snapshot: str) -> set[str]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise CalibrationError(f"{snapshot} Compose JSON cannot be read") from exc
    _require(isinstance(data, dict), f"{snapshot} Compose JSON must be an object")
    services = data.get("services")
    _require(isinstance(services, dict) and services, f"{snapshot} Compose JSON has no services")
    for service, definition in services.items():
        _require(isinstance(service, str) and SERVICE_NAME.fullmatch(service) is not None, f"{snapshot} Compose service name is invalid")
        _require(isinstance(definition, dict), f"{snapshot} Compose service definition is invalid")
        _reject_resource_fields(definition, snapshot)
    return set(services)


def validate_compose_snapshots(inventory: dict[str, Any], paths: dict[str, Path]) -> None:
    _require(set(paths) == set(SNAPSHOTS), "Compose snapshot paths are incomplete")
    actual = {snapshot: compose_services(paths[snapshot], snapshot) for snapshot in SNAPSHOTS}
    _require(actual["base-production"] == actual["base-development"], "base production/development service sets differ")
    _require(actual["evidence-production"] == actual["evidence-development"], "evidence production/development service sets differ")
    snapshots = inventory["compose_snapshots"]
    for snapshot in SNAPSHOTS:
        expected = {item["service"] for item in services_for_scopes(inventory, snapshots[snapshot])}
        _require(actual[snapshot] == expected, f"{snapshot} Compose service set does not match inventory lane")
    _require(set().union(*actual.values()) == {item["service"] for item in inventory["services"]}, "Compose snapshot union does not match the complete inventory")


def parse_cpu_percent(value: Any) -> float:
    _require(isinstance(value, str) and value.endswith("%"), "Docker CPU sample is invalid")
    number = value[:-1]
    _require("," not in number and re.fullmatch(r"[0-9]+(?:\.[0-9]+)?", number) is not None, "Docker CPU sample is invalid")
    parsed = float(number)
    _require(math.isfinite(parsed) and parsed >= 0, "Docker CPU sample is invalid")
    return parsed


def parse_memory_bytes(value: Any) -> int:
    _require(isinstance(value, str), "Docker memory sample is invalid")
    current = value.split(" / ", 1)[0].strip()
    match = MEMORY_VALUE.fullmatch(current)
    _require(match is not None, "Docker memory sample is invalid")
    parsed = float(match.group(1)) * MEMORY_MULTIPLIERS[match.group(2)]
    _require(math.isfinite(parsed) and parsed >= 0, "Docker memory sample is invalid")
    return int(parsed)


def parse_pids(value: Any) -> int:
    _require(isinstance(value, str) and re.fullmatch(r"[0-9]+", value) is not None, "Docker PID sample is invalid")
    return int(value)


def _run_command(command: Sequence[str], label: str) -> str:
    try:
        result = subprocess.run(command, check=False, capture_output=True, text=True, encoding="utf-8", errors="replace")
    except OSError as exc:
        if label == "Docker engine":
            raise CalibrationBlocked("Docker daemon unavailable") from exc
        raise CalibrationError(f"{label} is unavailable") from exc
    if result.returncode != 0:
        if label == "Docker engine":
            raise CalibrationBlocked("Docker daemon unavailable")
        if label.startswith("Docker statistics") and not _docker_daemon_available():
            raise CalibrationBlocked("Docker daemon unavailable")
        raise CalibrationError(f"{label} failed")
    return result.stdout.strip()


def _docker_daemon_available() -> bool:
    try:
        result = subprocess.run(
            ["docker", "info", "--format", "{{.ServerVersion}}"],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except OSError:
        return False
    return result.returncode == 0


def _compose_command(compose_files: Sequence[Path], lane: str) -> list[str]:
    command = ["docker", "compose"]
    for compose_file in compose_files:
        command.extend(["-f", str(compose_file)])
    if lane == "full":
        command.extend(["--profile", "full"])
    elif lane == "evidence":
        command.extend(["--profile", "evidence"])
    elif lane == "evidence-real":
        command.extend(["--profile", "evidence", "--profile", "evidence-real"])
    return command


def _metadata(runner: Runner) -> dict[str, Any]:
    engine_version = runner(["docker", "version", "--format", "{{.Server.Version}}"], "Docker engine")
    compose_version = runner(["docker", "compose", "version", "--short"], "Docker Compose")
    commit = runner(["git", "rev-parse", "HEAD"], "Git metadata")
    _require(re.fullmatch(r"[0-9]+(?:\.[0-9]+)+(?:[-+][A-Za-z0-9._-]+)?", engine_version) is not None, "Docker engine metadata is invalid")
    _require(re.fullmatch(r"v?[0-9]+(?:\.[0-9]+)+(?:[-+][A-Za-z0-9._-]+)?", compose_version) is not None, "Docker Compose metadata is invalid")
    _require(re.fullmatch(r"[0-9a-f]{40}", commit) is not None, "Git metadata is invalid")
    return {
        "host": {
            "architecture": host_platform.machine() or "unknown",
            "os": sys.platform,
            "system": host_platform.system() or "unknown",
        },
        "docker": {"engine_version": engine_version},
        "compose": {"version": compose_version},
        "git": {"commit": commit},
    }


def _require_compose_services(selected: Iterable[dict[str, Any]], compose_command: Sequence[str], runner: Runner) -> dict[str, str]:
    configured = runner([*compose_command, "config", "--services"], "Docker Compose configuration")
    configured_services = {line for line in configured.splitlines() if SERVICE_NAME.fullmatch(line)}
    expected_services = {item["service"] for item in selected}
    _require(configured_services == expected_services, "Docker Compose services do not match the selected lane")
    mapping: dict[str, str] = {}
    for item in selected:
        service = item["service"]
        _require(service in configured_services, f"inventory service '{service}' is absent from Docker Compose")
        container_id = runner([*compose_command, "ps", "--status", "running", "--quiet", service], f"Docker service '{service}'")
        identifiers = [line for line in container_id.splitlines() if line]
        _require(len(identifiers) == 1 and CONTAINER_ID.fullmatch(identifiers[0]) is not None, f"required service '{service}' is not running")
        mapping[service] = identifiers[0]
    return mapping


def _sample_service(service: str, container_id: str, runner: Runner) -> tuple[float, int, int]:
    sample_text = runner(["docker", "stats", "--no-stream", "--format", "{{json .}}", container_id], f"Docker statistics for service '{service}'")
    try:
        sample = json.loads(sample_text)
    except json.JSONDecodeError as exc:
        raise CalibrationError(f"Docker statistics for service '{service}' are invalid") from exc
    _require(isinstance(sample, dict), f"Docker statistics for service '{service}' are invalid")
    _require({"CPUPerc", "MemUsage", "PIDs"}.issubset(sample), f"Docker statistics for service '{service}' are invalid")
    return parse_cpu_percent(sample["CPUPerc"]), parse_memory_bytes(sample["MemUsage"]), parse_pids(sample["PIDs"])


def collect_bundle(
    inventory: dict[str, Any],
    workload_id: str,
    compose_files: Sequence[Path],
    runner: Runner | None = None,
    sleep: Callable[[float], None] | None = None,
    now: Callable[[], datetime] = lambda: datetime.now(timezone.utc),
) -> dict[str, Any]:
    runner = runner or _run_command
    sleep = sleep or time.sleep
    workload = workload_for_id(inventory, workload_id)
    lane = workload["lane"]
    sample_window_seconds = workload["sample_window_seconds"]
    samples = workload["samples"]
    _require(compose_files and all(path.is_file() for path in compose_files), "Compose file is unavailable")
    selected = services_for_lane(inventory, lane)
    metadata = _metadata(runner)
    compose_command = _compose_command(compose_files, lane)
    containers = _require_compose_services(selected, compose_command, runner)
    peaks = {item["service"]: {"cpu": 0.0, "memory": 0, "pids": 0, "samples": 0} for item in selected}
    interval = sample_window_seconds / (samples - 1)
    started_at = now().astimezone(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    for index in range(samples):
        for item in selected:
            service = item["service"]
            cpu, memory, pids = _sample_service(service, containers[service], runner)
            peak = peaks[service]
            peak["cpu"] = max(peak["cpu"], cpu)
            peak["memory"] = max(peak["memory"], memory)
            peak["pids"] = max(peak["pids"], pids)
            peak["samples"] += 1
        if index + 1 < samples:
            sleep(interval)
    services = []
    for item in selected:
        service = item["service"]
        peak = peaks[service]
        _require(peak["samples"] > 0, f"service '{service}' produced zero samples")
        services.append(
            {
                "service": service,
                "peak_cpu_percent": peak["cpu"],
                "peak_memory_bytes": peak["memory"],
                "peak_pids": peak["pids"],
            }
        )
    ended_at = now().astimezone(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    return {
        "schema_version": SCHEMA_VERSION,
        "phase": "exploratory_no_ceilings",
        "result": "exploratory",
        "metadata": metadata,
        "workload_id": workload_id,
        "workload_version": workload["version"],
        "workload_kind": workload["kind"],
        "selected_lane": lane,
        "sample_window_seconds": sample_window_seconds,
        "started_at_utc": started_at,
        "ended_at_utc": ended_at,
        "services": services,
    }


def write_bundle_atomically(bundle: dict[str, Any], output: Path) -> None:
    _require(output.parent.is_dir(), "output directory is unavailable")
    temporary_name: str | None = None
    try:
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=output.parent, prefix=f".{output.name}.", suffix=".tmp", delete=False) as handle:
            temporary_name = handle.name
            json.dump(bundle, handle, indent=2, sort_keys=True)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary_name, output)
        temporary_name = None
    finally:
        if temporary_name is not None:
            try:
                os.unlink(temporary_name)
            except FileNotFoundError:
                pass


def _parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate or collect resource-calibration evidence.")
    subcommands = parser.add_subparsers(dest="command", required=True)
    validate = subcommands.add_parser("validate", help="validate a calibration-pending inventory")
    validate.add_argument("--inventory", type=Path, required=True)
    compose = subcommands.add_parser("validate-compose", help="reject pre-calibration Compose resource declarations")
    compose.add_argument("--inventory", type=Path, required=True)
    compose.add_argument("--base-production-json", type=Path, required=True)
    compose.add_argument("--evidence-production-json", type=Path, required=True)
    compose.add_argument("--base-development-json", type=Path, required=True)
    compose.add_argument("--evidence-development-json", type=Path, required=True)
    collect = subcommands.add_parser("collect", help="collect aggregate Docker resource peaks")
    collect.add_argument("--inventory", type=Path, required=True)
    collect.add_argument("--output", type=Path, required=True)
    collect.add_argument("--workload-id", required=True)
    collect.add_argument("--compose-file", type=Path, action="append", required=True)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    arguments = _parse_arguments(argv if argv is not None else sys.argv[1:])
    try:
        if arguments.command == "validate-compose":
            inventory = load_inventory(arguments.inventory)
            validate_compose_snapshots(inventory, {
                "base-production": arguments.base_production_json,
                "evidence-production": arguments.evidence_production_json,
                "base-development": arguments.base_development_json,
                "evidence-development": arguments.evidence_development_json,
            })
            print("pre-calibration Compose JSON is valid")
            return 0
        inventory = load_inventory(arguments.inventory)
        if arguments.command == "validate":
            print("resource calibration inventory is valid")
            return 0
        bundle = collect_bundle(
            inventory=inventory,
            workload_id=arguments.workload_id,
            compose_files=arguments.compose_file,
        )
        write_bundle_atomically(bundle, arguments.output)
        print("resource calibration bundle collected")
        return 0
    except CalibrationBlocked as error:
        print(f"resource calibration BLOCKED: {error}", file=sys.stderr)
        return 3
    except CalibrationError as error:
        print(f"resource calibration failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
