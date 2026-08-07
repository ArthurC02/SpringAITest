from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "export-artifact-compatibility-usage-v1.py"
SPEC = importlib.util.spec_from_file_location("artifact_usage_exporter", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
exporter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(exporter)


class ExporterTests(unittest.TestCase):
    def fixture(self, root: Path) -> tuple[Path, dict]:
        sources = []
        for service in ("backend", "platform", "workflow"):
            path = root / f"{service}.jsonl"
            path.write_text("", encoding="utf-8")
            sources.append({
                "service": service, "instance": f"{service}-1", "path": path.name,
                "coverageStartUtc": "2026-08-01T00:00:00Z",
                "coverageEndUtc": "2026-08-02T00:00:00Z",
            })
        manifest = {
            "schemaVersion": 1, "deploymentVersion": "release-1",
            "window": {"startUtc": "2026-08-01T00:00:00Z", "endUtc": "2026-08-02T00:00:00Z"},
            "sources": sources,
            "retryInflationDisclosure": "Counts may include retries where no logical-attempt identity exists.",
        }
        path = root / "manifest.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        return path, manifest

    @staticmethod
    def event(**changes: object) -> dict:
        value = {
            "schemaVersion": 1, "event": "artifact_compatibility_usage_total",
            "timestampUtc": "2026-08-01T12:00:00Z", "deploymentVersion": "release-1",
            "service": "backend", "surface": "public_skills", "operation": "update",
            "resolvedArtifactType": "business_workflow", "outcome": "success", "count": 2,
        }
        value.update(changes)
        return value

    def test_aggregates_and_emits_complete_authoritative_zero_matrix(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path, _ = self.fixture(root)
            events = [self.event(count=2), self.event(count=3)]
            (root / "backend.jsonl").write_text("\n".join(map(json.dumps, events)), encoding="utf-8")
            bundle = exporter.export(manifest_path)
            row = next(row for row in bundle["counts"] if row["service"] == "backend" and row["surface"] == "public_skills" and row["operation"] == "update" and row["resolvedArtifactType"] == "business_workflow" and row["outcome"] == "success")
            zero = next(row for row in bundle["counts"] if row["service"] == "workflow" and row["surface"] == "workflow_unified_invoke" and row["operation"] == "invoke" and row["resolvedArtifactType"] == "unknown" and row["outcome"] == "error")
            self.assertEqual(5, row["count"])
            self.assertEqual(0, zero["count"])
            expected_series = sum(len(operations) for surfaces in exporter.AUTHORITY.values() for operations in surfaces.values()) * len(exporter.ARTIFACT_TYPES) * len(exporter.OUTCOMES)
            self.assertEqual(expected_series, len(bundle["counts"]))
            self.assertNotIn("path", json.dumps(bundle["sources"]))

    def test_accepts_platform_pre_controller_invoke_rejection(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path, _ = self.fixture(root)
            event = self.event(
                service="platform", operation="invoke", resolvedArtifactType="unknown",
                outcome="rejected", count=1,
            )
            (root / "platform.jsonl").write_text(json.dumps(event), encoding="utf-8")

            bundle = exporter.export(manifest_path)

            row = next(
                row for row in bundle["counts"]
                if row["service"] == "platform"
                and row["surface"] == "public_skills"
                and row["operation"] == "invoke"
                and row["resolvedArtifactType"] == "unknown"
                and row["outcome"] == "rejected"
            )
            self.assertEqual(1, row["count"])

    def test_rejects_unknown_origin_non_authority_and_mixed_version(self) -> None:
        cases = (
            self.event(service="workflow", surface="unknown_origin", operation="invoke"),
            self.event(service="platform", surface="public_skills", operation="update"),
            self.event(deploymentVersion="release-2"),
        )
        for event in cases:
            with self.subTest(event=event):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    manifest_path, _ = self.fixture(root)
                    target = root / f"{event['service']}.jsonl"
                    target.write_text(json.dumps(event), encoding="utf-8")
                    with self.assertRaises(exporter.ExportError):
                        exporter.export(manifest_path)

    def test_rejects_malformed_event_and_incomplete_or_missing_coverage(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path, manifest = self.fixture(root)
            (root / "backend.jsonl").write_text("{bad json", encoding="utf-8")
            with self.assertRaisesRegex(exporter.ExportError, "malformed JSON"):
                exporter.export(manifest_path)
            (root / "backend.jsonl").write_text("", encoding="utf-8")
            manifest["sources"][0]["coverageStartUtc"] = "2026-08-01T00:00:01Z"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaisesRegex(exporter.ExportError, "complete window"):
                exporter.export(manifest_path)
            manifest["sources"] = manifest["sources"][:-1]
            manifest["sources"][0]["coverageStartUtc"] = "2026-08-01T00:00:00Z"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaisesRegex(exporter.ExportError, "include backend, platform, and workflow"):
                exporter.export(manifest_path)

    def test_rejects_placeholder_deployment_version(self) -> None:
        for deployment in ("unknown", "development"):
            with self.subTest(deployment=deployment):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    manifest_path, manifest = self.fixture(root)
                    manifest["deploymentVersion"] = deployment
                    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

                    with self.assertRaisesRegex(exporter.ExportError, "identify one deployed build"):
                        exporter.export(manifest_path)

    def test_failed_cli_preserves_existing_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path, _ = self.fixture(root)
            (root / "workflow.jsonl").write_text(json.dumps(self.event(service="workflow", surface="unknown_origin", operation="invoke")), encoding="utf-8")
            output = root / "bundle.json"
            output.write_text('{"preserve":true}\n', encoding="utf-8")
            self.assertEqual(2, exporter.main([str(manifest_path), "--output", str(output)]))
            self.assertEqual('{"preserve":true}\n', output.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
