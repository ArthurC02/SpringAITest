from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Sequence
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "scripts" / "resource_calibration.py"
SPEC = importlib.util.spec_from_file_location("resource_calibration", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
calibration = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(calibration)


class FakeRunner:
    def __init__(self, responses: dict[tuple[str, ...], str]) -> None:
        self.responses = responses

    def __call__(self, command: Sequence[str], label: str) -> str:
        try:
            return self.responses[tuple(command)]
        except KeyError as error:
            raise calibration.CalibrationError(f"unexpected test command: {label}") from error


def service(name: str, scopes: list[str] | None = None, scope: str = "shared_workload", counterpart: str | None = None) -> dict[str, Any]:
    return {
        "service": name,
        "service_class": "application",
        "execution_scopes": scopes or ["default", "full", "evidence", "evidence-real"],
        "comparison_scope": scope,
        "counterpart": counterpart,
        "calibration_status": "calibration_pending",
    }


def inventory(services: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "schema_version": 1,
        "phase": "exploratory_no_ceilings",
        "measurement_contract": {
            "baseline_only": True,
            "ceiling_approving": False,
        },
        "compose_snapshots": {
            "base-production": ["default", "full"],
            "evidence-production": ["default", "full", "evidence", "evidence-real"],
            "base-development": ["default", "full"],
            "evidence-development": ["default", "full", "evidence", "evidence-real"],
        },
        "workloads": [
            {"id": "steady-running-default-v1", "version": 1, "lane": "default", "kind": "steady_running", "sample_window_seconds": 60, "samples": 60, "ceiling_approving": False},
            {"id": "steady-running-full-v1", "version": 1, "lane": "full", "kind": "steady_running", "sample_window_seconds": 60, "samples": 60, "ceiling_approving": False},
            {"id": "steady-running-evidence-v1", "version": 1, "lane": "evidence", "kind": "steady_running", "sample_window_seconds": 60, "samples": 60, "ceiling_approving": False},
            {"id": "steady-running-evidence-real-v1", "version": 1, "lane": "evidence-real", "kind": "steady_running", "sample_window_seconds": 60, "samples": 60, "ceiling_approving": False},
        ],
        "services": services,
    }


class ResourceCalibrationTests(unittest.TestCase):
    def test_checked_in_inventory_lists_every_compose_service_and_lane(self) -> None:
        loaded = calibration.load_inventory(ROOT / "infra" / "resource-calibration-inventory.json")
        expected = {
            "litellm", "mem0", "frontend", "platform", "rabbitmq", "backend", "workflow", "appdb",
            "langfuse-worker", "langfuse-web", "postgres", "clickhouse", "redis", "minio",
            "rabbitmq-evidence", "backend-evidence", "workflow-evidence", "evidence-model",
            "workflow-capture-proxy", "platform-evidence", "evidence-nginx", "frontend-evidence",
        }
        self.assertEqual(expected, {item["service"] for item in loaded["services"]})
        self.assertEqual(22, len(loaded["services"]))
        self.assertTrue(all(item["calibration_status"] == "calibration_pending" for item in loaded["services"]))
        self.assertEqual("exploratory_no_ceilings", loaded["phase"])
        self.assertEqual(12, len(calibration.services_for_lane(loaded, "default")))
        self.assertEqual(14, len(calibration.services_for_lane(loaded, "full")))
        self.assertEqual(17, len(calibration.services_for_lane(loaded, "evidence")))
        self.assertEqual(20, len(calibration.services_for_lane(loaded, "evidence-real")))
        self.assertEqual(14, len(calibration.services_for_scopes(loaded, loaded["compose_snapshots"]["base-production"])))
        self.assertEqual(22, len(calibration.services_for_scopes(loaded, loaded["compose_snapshots"]["evidence-production"])))
        self.assertEqual({"steady-running-default-v1", "steady-running-full-v1", "steady-running-evidence-v1", "steady-running-evidence-real-v1"}, {workload["id"] for workload in loaded["workloads"]})

    def test_inventory_rejects_ceilings_invalid_enum_and_asymmetric_counterpart(self) -> None:
        data = inventory([service("backend")])
        data["services"][0]["approved_ceilings"] = {"memory": "1g"}
        with self.assertRaisesRegex(calibration.CalibrationError, "unsupported"):
            calibration.validate_inventory_data(data)
        data = inventory([service("backend")])
        data["services"][0]["service_class"] = "unbounded"
        with self.assertRaisesRegex(calibration.CalibrationError, "class"):
            calibration.validate_inventory_data(data)
        data = inventory([service("backend")])
        data["services"][0]["execution_scopes"] = ["unknown"]
        with self.assertRaisesRegex(calibration.CalibrationError, "scopes"):
            calibration.validate_inventory_data(data)
        data = inventory([service("backend", scope="counterpart_candidate", counterpart="backend-evidence")])
        with self.assertRaisesRegex(calibration.CalibrationError, "not inventoried"):
            calibration.validate_inventory_data(data)

    def test_parsers_reject_locale_and_malformed_stats(self) -> None:
        self.assertEqual(12_886_999, calibration.parse_memory_bytes("12.29MiB / 1GiB"))
        self.assertEqual(1.5, calibration.parse_cpu_percent("1.5%"))
        self.assertEqual(4, calibration.parse_pids("4"))
        for invalid in ("1,5%", "-1%", "secret"):
            with self.assertRaises(calibration.CalibrationError):
                calibration.parse_cpu_percent(invalid)
        for invalid in ("12 MB", "12MB/1GB", "not-a-value"):
            with self.assertRaises(calibration.CalibrationError):
                calibration.parse_memory_bytes(invalid)

    def test_collection_uses_selected_lane_and_keeps_real_docker_identifiers_out(self) -> None:
        loaded = calibration.validate_inventory_data(inventory([service("backend")]))
        container_id = "a" * 64
        compose_file = ROOT / "infra" / "docker-compose.yml"
        compose = ["docker", "compose", "-f", str(compose_file)]
        responses = {
            ("docker", "version", "--format", "{{.Server.Version}} "): "should-not-be-called",
            ("docker", "version", "--format", "{{.Server.Version}}") : "28.0.1",
            ("docker", "compose", "version", "--short"): "v2.35.1",
            ("git", "rev-parse", "HEAD"): "b" * 40,
            tuple([*compose, "config", "--services"]): "backend\n",
            tuple([*compose, "ps", "--status", "running", "--quiet", "backend"]): container_id,
            ("docker", "stats", "--no-stream", "--format", "{{json .}}", container_id): json.dumps({
                "Name": "tenant-secret-container-name", "ID": container_id, "CPUPerc": "2.5%",
                "MemUsage": "12MiB / 1GiB", "PIDs": "3", "Labels": "token=secret",
            }),
        }
        times = iter((datetime(2026, 8, 6, 1, 2, 3, tzinfo=timezone.utc), datetime(2026, 8, 6, 1, 2, 4, tzinfo=timezone.utc)))
        bundle = calibration.collect_bundle(loaded, "steady-running-default-v1", [compose_file], FakeRunner(responses), lambda _: None, lambda: next(times))
        rendered = json.dumps(bundle)
        for forbidden in (container_id, "tenant-secret", "token=secret", "{{json .}}", "command"):
            self.assertNotIn(forbidden, rendered)
        self.assertEqual("exploratory_no_ceilings", bundle["phase"])
        self.assertEqual("exploratory", bundle["result"])
        self.assertEqual("default", bundle["selected_lane"])
        self.assertEqual(1, bundle["workload_version"])
        self.assertEqual("steady_running", bundle["workload_kind"])
        self.assertEqual("2026-08-06T01:02:03Z", bundle["started_at_utc"])
        self.assertEqual("2026-08-06T01:02:04Z", bundle["ended_at_utc"])
        self.assertEqual({"service", "peak_cpu_percent", "peak_memory_bytes", "peak_pids"}, set(bundle["services"][0]))
        self.assertEqual(12 * 1_048_576, bundle["services"][0]["peak_memory_bytes"])

    def test_missing_service_and_malformed_stats_fail_without_replacing_bundle(self) -> None:
        loaded = calibration.validate_inventory_data(inventory([service("backend")]))
        compose_file = ROOT / "infra" / "docker-compose.yml"
        compose = ["docker", "compose", "-f", str(compose_file)]
        common = {
            ("docker", "version", "--format", "{{.Server.Version}}") : "28.0.1",
            ("docker", "compose", "version", "--short"): "v2.35.1",
            ("git", "rev-parse", "HEAD"): "b" * 40,
            tuple([*compose, "config", "--services"]): "backend\n",
        }
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "bundle.json"
            output.write_text('{"preserve": true}\n', encoding="utf-8")
            missing = dict(common)
            missing[tuple([*compose, "ps", "--status", "running", "--quiet", "backend"])] = ""
            with self.assertRaisesRegex(calibration.CalibrationError, "not running"):
                calibration.collect_bundle(loaded, "steady-running-default-v1", [compose_file], FakeRunner(missing), lambda _: None)
            self.assertEqual('{"preserve": true}\n', output.read_text(encoding="utf-8"))
            self.assertEqual([], list(Path(directory).glob(".bundle.json.*.tmp")))
            extra = dict(common)
            extra[tuple([*compose, "config", "--services"])] = "backend\nunexpected-service\n"
            with self.assertRaisesRegex(calibration.CalibrationError, "selected lane"):
                calibration.collect_bundle(loaded, "steady-running-default-v1", [compose_file], FakeRunner(extra), lambda _: None)
            malformed = dict(common)
            container_id = "a" * 64
            malformed[tuple([*compose, "ps", "--status", "running", "--quiet", "backend"])] = container_id
            malformed[("docker", "stats", "--no-stream", "--format", "{{json .}}", container_id)] = "{bad json"
            with self.assertRaisesRegex(calibration.CalibrationError, "statistics"):
                calibration.collect_bundle(loaded, "steady-running-default-v1", [compose_file], FakeRunner(malformed), lambda _: None)
            self.assertEqual('{"preserve": true}\n', output.read_text(encoding="utf-8"))

    def test_docker_blocked_exit_is_distinct_and_preserves_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "bundle.json"
            output.write_text('{"preserve": true}\n', encoding="utf-8")
            with mock.patch.object(calibration, "collect_bundle", side_effect=calibration.CalibrationBlocked("Docker daemon unavailable")):
                code = calibration.main([
                    "collect", "--inventory", str(ROOT / "infra" / "resource-calibration-inventory.json"),
                    "--output", str(output), "--workload-id", "steady-running-default-v1",
                    "--compose-file", str(ROOT / "infra" / "docker-compose.yml"),
                ])
            self.assertEqual(3, code)
            self.assertEqual('{"preserve": true}\n', output.read_text(encoding="utf-8"))

    def test_docker_daemon_failure_is_sanitized_blocked_condition(self) -> None:
        failed = mock.Mock(returncode=1, stdout="", stderr="token=not-for-output")
        with mock.patch.object(calibration.subprocess, "run", return_value=failed):
            with self.assertRaisesRegex(calibration.CalibrationBlocked, "^Docker daemon unavailable$"):
                calibration._run_command(["docker", "version"], "Docker engine")
        with mock.patch.object(calibration.subprocess, "run", side_effect=[failed, failed]):
            with self.assertRaisesRegex(calibration.CalibrationBlocked, "^Docker daemon unavailable$"):
                calibration._run_command(["docker", "stats"], "Docker statistics for service 'backend'")

    def test_second_stats_failure_returns_blocked_without_creating_bundle(self) -> None:
        loaded = calibration.validate_inventory_data(inventory([service("backend")]))
        compose_file = ROOT / "infra" / "docker-compose.yml"
        compose = ["docker", "compose", "-f", str(compose_file)]
        container_id = "a" * 64

        def command(command: Sequence[str], label: str) -> str:
            responses = {
                ("docker", "version", "--format", "{{.Server.Version}}") : "28.0.1",
                ("docker", "compose", "version", "--short"): "v2.35.1",
                ("git", "rev-parse", "HEAD"): "b" * 40,
                tuple([*compose, "config", "--services"]): "backend\n",
                tuple([*compose, "ps", "--status", "running", "--quiet", "backend"]): container_id,
            }
            return responses[tuple(command)]

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "bundle.json"
            output.write_text('{"preserve": true}\n', encoding="utf-8")
            with mock.patch.object(calibration, "load_inventory", return_value=loaded), \
                 mock.patch.object(calibration, "_run_command", side_effect=command), \
                 mock.patch.object(calibration, "_sample_service", side_effect=[(1.0, 10, 1), calibration.CalibrationBlocked("Docker daemon unavailable")]), \
                 mock.patch.object(calibration.time, "sleep"):
                code = calibration.main([
                    "collect", "--inventory", "ignored.json", "--output", str(output),
                    "--workload-id", "steady-running-default-v1", "--compose-file", str(compose_file),
                ])
            self.assertEqual(3, code)
            self.assertEqual('{"preserve": true}\n', output.read_text(encoding="utf-8"))
            self.assertEqual([], list(Path(directory).glob(".bundle.json.*.tmp")))

    def test_workload_manifest_rejects_free_or_ceiling_approving_work(self) -> None:
        data = inventory([service("backend")])
        data["workloads"][0]["samples"] = 0
        with self.assertRaisesRegex(calibration.CalibrationError, "sampling"):
            calibration.validate_inventory_data(data)
        data = inventory([service("backend")])
        data["workloads"][0]["ceiling_approving"] = True
        with self.assertRaisesRegex(calibration.CalibrationError, "must not approve"):
            calibration.validate_inventory_data(data)
        loaded = calibration.validate_inventory_data(inventory([service("backend")]))
        with self.assertRaisesRegex(calibration.CalibrationError, "not in the reviewed manifest"):
            calibration.collect_bundle(loaded, "free-form-workload", [ROOT / "infra" / "docker-compose.yml"])

    def test_compose_snapshot_validator_uses_inventory_and_four_named_inputs(self) -> None:
        loaded = calibration.load_inventory(ROOT / "infra" / "resource-calibration-inventory.json")
        snapshots = loaded["compose_snapshots"]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths: dict[str, Path] = {}
            for snapshot in calibration.SNAPSHOTS:
                services = {item["service"]: {"image": "example"} for item in calibration.services_for_scopes(loaded, snapshots[snapshot])}
                path = root / f"{snapshot}.json"
                path.write_text(json.dumps({"services": services}), encoding="utf-8")
                paths[snapshot] = path
            calibration.validate_compose_snapshots(loaded, paths)
            self.assertEqual(0, calibration.main([
                "validate-compose", "--inventory", str(ROOT / "infra" / "resource-calibration-inventory.json"),
                "--base-production-json", str(paths["base-production"]),
                "--evidence-production-json", str(paths["evidence-production"]),
                "--base-development-json", str(paths["base-development"]),
                "--evidence-development-json", str(paths["evidence-development"]),
            ]))
            paths["base-development"].write_text(json.dumps({"services": {"backend": {"image": "example"}}}), encoding="utf-8")
            with self.assertRaisesRegex(calibration.CalibrationError, "service sets differ"):
                calibration.validate_compose_snapshots(loaded, paths)
            paths["base-development"].write_text(paths["base-production"].read_text(encoding="utf-8"), encoding="utf-8")
            paths["evidence-development"].write_text(json.dumps({"services": {"backend": {"mem_limit": None}}}), encoding="utf-8")
            with self.assertRaisesRegex(calibration.CalibrationError, "empty resource"):
                calibration.validate_compose_snapshots(loaded, paths)


if __name__ == "__main__":
    unittest.main()
