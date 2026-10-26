"""Preserve complete standalone build outputs without accepting the database assembly."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "media"))
import instrumentation_bundle as bundle


class InstrumentationBundleTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name) / "checkout"
        self.root.mkdir()
        self.output = Path(self.directory.name) / "kit"
        (self.root / "global.json").write_text('{"sdk":{"version":"10.0.402"}}\n')
        for project in ("Duora.MediaCorpusTests", "Duora.MediaBaselineHost"):
            path = self.root / "backend" / "tests" / project / "bin" / "Debug" / "net10.0"
            path.mkdir(parents=True)
            for suffix in (".dll", ".deps.json", ".runtimeconfig.json"):
                (path / (project + suffix)).write_bytes(b"synthetic build output")
        self.metadata = {"commit": "a" * 40, "tree": "b" * 40,
                         "run_id": "123", "run_attempt": "2", "architecture": "x86_64"}

    def package(self):
        return bundle.package(self.root, self.output, **self.metadata)

    def test_package_preserves_outputs_and_hashes_with_separate_source_identity(self):
        dependency = self.root / bundle.OUTPUTS[0] / "runtimes" / "linux-arm64" / "native.so"
        dependency.parent.mkdir(parents=True)
        dependency.write_bytes(b"retained dependency")
        manifest = self.package()
        self.assertEqual("standalone-instrumentation", manifest["kind"])
        self.assertEqual({"commit": "a" * 40, "tree": "b" * 40}, manifest["source"])
        self.assertEqual({"id": 123, "attempt": 2, "job": "backend",
                          "architecture": "amd64"}, manifest["ci"])
        self.assertEqual("Debug", manifest["configuration"])
        self.assertEqual("net10.0", manifest["targetFramework"])
        self.assertFalse(manifest["hostQualified"])
        self.assertNotIn("decoder", manifest)
        records = manifest["files"]
        self.assertEqual(8, len(records))
        for item in records:
            original = (self.root / item["path"]).read_bytes()
            self.assertEqual(original, (self.output / item["path"]).read_bytes())
            self.assertEqual(hashlib.sha256(original).hexdigest(), item["sha256"])
            self.assertEqual(len(original), item["bytes"])
        stored = json.loads((self.output / "instrumentation.json").read_text())
        self.assertEqual(manifest, stored)

    def test_missing_runtime_dependency_description_refuses_without_partial_output(self):
        (self.root / bundle.OUTPUTS[1] / "Duora.MediaBaselineHost.runtimeconfig.json").unlink()
        with self.assertRaisesRegex(ValueError, "required output"):
            self.package()
        self.assertFalse(self.output.exists())

    def test_integration_test_assembly_cannot_enter_the_restricted_kit(self):
        (self.root / bundle.OUTPUTS[0] / "Duora.IntegrationTests.dll").write_bytes(b"fixture")
        with self.assertRaisesRegex(ValueError, "integration assembly"):
            self.package()
        self.assertFalse(self.output.exists())

    def test_symlink_payload_or_output_root_cannot_escape_selection(self):
        outside = Path(self.directory.name) / "outside"
        outside.mkdir()
        (outside / "private.txt").write_text("must not be copied")
        link = self.root / bundle.OUTPUTS[0] / "escape"
        link.symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "symbolic link"):
            self.package()
        link.unlink()
        original = self.root / bundle.OUTPUTS[1]
        original.rename(original.with_name("saved"))
        original.symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "symbolic link"):
            self.package()
        self.assertFalse(self.output.exists())

    def test_missing_or_untrusted_source_metadata_cannot_be_attested(self):
        for key, value in (("commit", "main"), ("tree", ""), ("run_id", "0"),
                           ("run_attempt", "-1"), ("architecture", "unknown")):
            original = self.metadata[key]
            self.metadata[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.package()
            self.metadata[key] = original
        self.assertFalse(self.output.exists())

    def test_existing_output_is_not_overwritten_and_output_cannot_overlap_checkout(self):
        self.output.mkdir()
        sentinel = self.output / "preserved"
        sentinel.write_text("keep")
        with self.assertRaises(FileExistsError):
            self.package()
        self.assertEqual("keep", sentinel.read_text())
        self.output = self.root / "kit"
        with self.assertRaisesRegex(ValueError, "outside checkout"):
            self.package()

    def test_fifo_is_rejected_without_reading_it(self):
        import os
        os.mkfifo(self.root / bundle.OUTPUTS[0] / "socket-like-input")
        with self.assertRaisesRegex(ValueError, "regular file"):
            self.package()
        self.assertFalse(self.output.exists())
