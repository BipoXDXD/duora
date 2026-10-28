"""Pure tests for new hosted qualification boundaries; no Docker or .NET."""
import importlib.util
import io
import json
import hashlib
import tarfile
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT = Path(__file__).parent


class QualificationTests(unittest.TestCase):
    def load(self):
        self.assertTrue((ROOT / "qualify.py").exists(), "finite hosted qualification implementation missing")
        spec = importlib.util.spec_from_file_location("hosted_qualification", ROOT / "qualify.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def test_missing_or_unbound_license_admission_refuses_before_runtime_build(self):
        module = self.load()
        self.assertTrue(hasattr(module, "require_license_admission"), "pre-build license admission gate missing")
        for lock in [{}, {"python": {"license_admission": {"status": "NOT_ADMITTED", "receipt_sha256": None}}}]:
            with self.subTest(lock=lock), self.assertRaisesRegex(module.Refusal, "LICENSE_NOT_ADMITTED"):
                module.require_license_admission(lock)

    def test_unsigned_profile_bindings_are_exact_public_inputs_without_admission(self):
        module = self.load()
        self.assertTrue(hasattr(module, "profile_license_bindings"), "unsigned Hub binding plan missing")
        lock = json.loads((ROOT / "profile-lock.json").read_text())
        original = json.loads(json.dumps(lock))
        bindings = module.profile_license_bindings(lock)
        self.assertEqual(lock, original)
        self.assertEqual(bindings["selected_python_closure_sha256"], "d7749183848fca298b7404d5849d0c166733192c04ddde7975732fe303b1b617")
        self.assertEqual(bindings["policy_sha256"], "f2880c90e41e77c402703be4420ced16d3ed2ecc1ae91ade693999ec2774df7c")
        self.assertEqual(bindings["upstream_python_sha256"], "6912dc06b4bad2af7602fe3366e22c3885defbc3a2d567f909d4c8528bcee69b")
        self.assertNotIn("status", bindings)
        for status in ("NOT_ADMITTED", "PROFILE_ONLY_ADMITTED"):
            fixture = json.loads(json.dumps(original))
            fixture["python"]["license_admission"]["status"] = status
            before = json.loads(json.dumps(fixture))
            with self.subTest(status=status):
                self.assertEqual(module.profile_license_bindings(fixture), bindings)
                self.assertEqual(fixture, before)

    def test_scoped_case_admission_rejects_stale_bindings_and_generic_approval(self):
        module = self.load()
        self.assertTrue(hasattr(module, "validate_profile_license_receipt"), "digest-scoped admission schema missing")
        expected = {"inventory_sha256": "a" * 64, "source_hashes": {"profile.py": "b" * 64},
                    "core_binaries": {"python/bin/python3.13": "c" * 64}, "policy_sha256": "d" * 64}
        receipt = {"schema_version": 2, "status": "PROFILE_ONLY_ADMITTED", "scope": "synthetic_support_contact_profile",
                   "bindings": expected, "global_policy_effect": "UNCHANGED", "operational_effect": "NONE",
                   "case_decisions": [{"case_id": name, "decision": "PROFILE_ONLY_APPROVED",
                       "bindings_sha256": hashlib.sha256(module.packager.canonical(expected)).hexdigest(),
                       "decision_receipt_sha256": "e" * 64} for name in module.PROFILE_CASES]}
        module.validate_profile_license_receipt(receipt, expected)
        for field in ("inventory_sha256", "source_hashes", "core_binaries", "policy_sha256"):
            changed = json.loads(json.dumps(expected))
            changed[field] = "f" * 64
            with self.subTest(field=field), self.assertRaises(module.Refusal):
                module.validate_profile_license_receipt(receipt, changed)
        for field, value in [("case_decisions", []), ("case_decisions", [True] * 5), ("status", "ADMITTED"), ("global_policy_effect", "WAIVER")]:
            changed = dict(receipt, **{field: value})
            with self.subTest(field=field), self.assertRaises(module.Refusal):
                module.validate_profile_license_receipt(changed, expected)

    def test_image_metadata_reply_requires_observed_hashes_and_complete_file_set(self):
        module = self.load()
        self.assertTrue(hasattr(module, "verify_image_public_files"), "image metadata verification missing")
        expected = {"/opt/probe/notices/LICENSE.txt": {"sha256": "a" * 64, "bytes": 12}}
        module.verify_image_public_files(json.dumps(expected).encode(), expected)
        with self.assertRaises(module.Refusal):
            module.verify_image_public_files(b"{}", expected)
        changed = {"/opt/probe/notices/LICENSE.txt": {"sha256": "b" * 64, "bytes": 12}}
        with self.assertRaises(module.Refusal):
            module.verify_image_public_files(json.dumps(changed).encode(), expected)

    def test_constructed_closure_or_base_change_blocks_before_docker_build(self):
        module = self.load()
        self.assertTrue(hasattr(module, "verify_constructed_admission"), "constructed closure admission binding missing")
        receipt = {"selected_python_closure_sha256": "a" * 64, "base_reference": "public-base",
                   "archive_sha256": "b" * 64, "helper_files": {"helper": "c" * 64}, "runner_sha256": "d" * 64, "notice_manifest_sha256": "f" * 64}
        manifest = {"python_closure_sha256": "a" * 64, "base": {"reference": "public-base"},
                    "python_archive_sha256": "b" * 64, "helper": {"helper": "c" * 64}, "runner_sha256": "d" * 64, "notice_manifest_sha256": "f" * 64}
        receipt.update(selection_sha256="1" * 64, core_binaries={}, public_metadata={},
                       source_hashes={"profile.py": "2" * 64, "Dockerfile": "3" * 64})
        manifest.update(python_selection_sha256="1" * 64, core_binaries={}, public_metadata={},
                        packager_sha256="2" * 64, dockerfile_sha256="3" * 64)
        receipt = {"bindings": receipt}
        module.verify_constructed_admission(receipt, manifest)
        for name in ("python_closure_sha256", "python_archive_sha256", "runner_sha256", "notice_manifest_sha256"):
            changed = dict(manifest, **{name: "e" * 64})
            with self.subTest(name=name), self.assertRaisesRegex(module.Refusal, "LICENSE_NOT_ADMITTED"):
                module.verify_constructed_admission(receipt, changed)
        changed = dict(manifest, base={"reference": "different-base"})
        with self.assertRaises(module.Refusal):
            module.verify_constructed_admission(receipt, changed)

    def test_public_command_failure_keeps_only_closed_reason_phase_and_library(self):
        module = self.load()
        with tempfile.TemporaryDirectory() as tmp:
            code = "import sys;sys.stderr.write('error while loading shared libraries: libutil.so.1: cannot open shared object file: TOKEN_SYNTHETIC_123 PRIVATE_SENTINEL');sys.exit(7)"
            try:
                module.command(["/usr/bin/python3", "-c", code], Path(tmp), strict_stderr=True)
            except module.Refusal as error:
                self.assertTrue(hasattr(module, "public_failure_details"), "closed command diagnosis missing")
                public = module.public_failure_details("python_smoke_start", error)
            else:
                self.fail("nonzero synthetic command accepted")
            self.assertEqual(public, {"failure_phase": "python_smoke_start", "command_exit_code": 7,
                                      "command_error_category": "runtime_library_missing", "missing_library": "libutil.so.1"})
            serialized = json.dumps(public)
            self.assertNotIn("TOKEN_SYNTHETIC_123", serialized)
            self.assertNotIn("PRIVATE_SENTINEL", serialized)
            self.assertNotIn("stderr", serialized)

    def test_command_diagnostic_does_not_export_unknown_library_or_change_output_limit(self):
        module = self.load()
        self.assertTrue(hasattr(module, "public_failure_details"), "closed bounded diagnosis missing")
        for code, limit, category in [
            ("import sys;sys.stderr.write('error while loading shared libraries: PRIVATE_SENTINEL.so: cannot open shared object file');sys.exit(9)", 16384, "runtime_library_missing"),
            ("import sys;sys.stdout.write('x'*17000)", 16384, "output_limit"),
        ]:
            with self.subTest(category=category), tempfile.TemporaryDirectory() as tmp:
                with self.assertRaises(module.Refusal) as caught:
                    module.command(["/usr/bin/python3", "-c", code], Path(tmp), output_limit=limit)
                public = module.public_failure_details("python_smoke_start", caught.exception)
                self.assertEqual(public["command_error_category"], category)
                self.assertNotIn("PRIVATE_SENTINEL", json.dumps(public))
                self.assertNotIn("missing_library", public)

    def test_bounded_download_rejects_extra_byte_and_digest_mismatch(self):
        module = self.load()
        for payload, size, digest in [(b"abcd", 3, "0" * 64), (b"abc", 3, "0" * 64)]:
            with self.subTest(payload=payload), self.assertRaises(module.Refusal):
                module.bounded_download(io.BytesIO(payload), size, digest)

    def test_preserved_helper_unpacks_exact_original_hashes_and_rejects_tampering(self):
        module = self.load()
        record = json.loads((ROOT / "preserved-helper.json").read_text())
        files = module.helper_bytes(record)
        self.assertEqual(set(files), {"Duora.IntegrationTests.dll", "Duora.IntegrationTests.runtimeconfig.json", "SHA256SUMS"})
        record["archive"] = "AAAA"
        with self.assertRaises(module.Refusal):
            module.helper_bytes(record)

    def test_effective_isolation_rejects_swapped_limits_privilege_and_host_network(self):
        module = self.load()
        host = {"NetworkMode": "none", "ReadonlyRootfs": True, "CapDrop": ["ALL"],
                "SecurityOpt": ["no-new-privileges:true"], "Memory": 536870912,
                "MemorySwap": 536870912, "NanoCpus": 500000000, "PidsLimit": 64,
                "Privileged": False, "PidMode": "", "IpcMode": "private",
                "LogConfig": {"Type": "none"}, "Tmpfs": {"/tmp": "rw,noexec,nosuid,nodev,size=16777216,mode=1777"},
                "Ulimits": [{"Name": "core", "Soft": 0, "Hard": 0}]}
        value = {"Config": {"User": "1654:1654"}, "HostConfig": host, "Mounts": [{"Type": "tmpfs", "Destination": "/tmp", "RW": True}]}
        self.assertTrue(module.verify_isolation(value))
        for key, bad in [("MemorySwap", 1073741824), ("Privileged", True), ("NetworkMode", "host"), ("CapAdd", ["SYS_ADMIN"])]:
            with self.subTest(key=key):
                changed = json.loads(json.dumps(value))
                changed["HostConfig"][key] = bad
                with self.assertRaises(module.Refusal):
                    module.verify_isolation(changed)

    def test_oci_export_binds_config_and_layer_bytes_to_leaf(self):
        module = self.load()
        self.assertTrue(hasattr(module, "oci_archive"), "OCI byte/config binding is missing")
        layer = b"synthetic-layer"
        layer_digest = "sha256:" + hashlib.sha256(layer).hexdigest()
        config = json.dumps({"rootfs": {"diff_ids": [layer_digest]}, "architecture": "amd64", "os": "linux"}).encode()
        config_digest = "sha256:" + hashlib.sha256(config).hexdigest()
        docker_manifest = json.dumps([{"Config": "config.json", "Layers": ["layer.tar"]}]).encode()
        with tempfile.TemporaryDirectory() as tmp:
            source, target = Path(tmp) / "docker.tar", Path(tmp) / "oci.tar"
            with tarfile.open(source, "w") as archive:
                for name, data in [("manifest.json", docker_manifest), ("config.json", config), ("layer.tar", layer)]:
                    entry = tarfile.TarInfo(name)
                    entry.size = len(data)
                    archive.addfile(entry, io.BytesIO(data))
            leaf = module.oci_archive(source, target, config_digest)
            with tarfile.open(target) as archive:
                index = json.load(archive.extractfile("index.json"))
                self.assertEqual(index["manifests"][0]["digest"], leaf)
                manifest = json.load(archive.extractfile("blobs/sha256/" + leaf[7:]))
                self.assertEqual(manifest["config"]["digest"], config_digest)
                self.assertEqual(manifest["layers"][0]["digest"], layer_digest)
            with self.assertRaises(module.Refusal):
                module.oci_archive(source, Path(tmp) / "bad.tar", "sha256:" + "0" * 64)

    def test_mechanical_reply_requires_flags_and_keeps_runtime_cache_unproven(self):
        module = self.load()
        result = {"status": "replay_verified", "configuration_matches_approved": True,
                  "ioptions_matches_approved": True, "cached_runtime_options_observed": False,
                  "runtime_effective_proven": False, "application_readiness_observed": False}
        self.assertTrue(module.mechanical_reply(json.dumps(result).encode()))
        result["cached_runtime_options_observed"] = True
        with self.assertRaises(module.Refusal):
            module.mechanical_reply(json.dumps(result).encode())


if __name__ == "__main__":
    unittest.main()
