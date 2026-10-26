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

    def test_stale_inventory_or_source_license_receipt_is_refused_before_download(self):
        module = self.load()
        names = ("python-license-inventory.json", "profile.py", "Dockerfile", "reviewed-python-aliases.json")
        with tempfile.TemporaryDirectory() as tmp, patch.object(module, "ROOT", Path(tmp)):
            root = Path(tmp)
            for name in names:
                (root / name).write_bytes(b"reviewed-source")
            (root / "notices").mkdir()
            (root / "notices/LICENSE.fixture.txt").write_bytes(b"verbatim")
            note_hash = hashlib.sha256(b"verbatim").hexdigest()
            inventory = json.dumps({"supplemental_notice_files": [{"path": "tools/support-contact-runtime/notices/LICENSE.fixture.txt", "bytes": 8, "sha256": note_hash, "verbatim": True}]}).encode()
            (root / "python-license-inventory.json").write_bytes(inventory)
            notice_manifest = {"inventory_sha256": hashlib.sha256(inventory).hexdigest(), "notices": {"LICENSE.fixture.txt": {"sha256": note_hash, "bytes": 8}}}
            receipt = {"status": "ADMITTED", "archive_sha256": "a" * 64,
                       "static_native_dependencies_reviewed": True, "required_notices_complete": True,
                       "license_policy_exceptions": [], "base_reference": "public-base@sha256:" + "b" * 64,
                       "selected_python_closure_sha256": "c" * 64,
                       "runner_sha256": module.packager.RUNNER_SHA256,
                       "helper_files": module.packager.HELPER_FILES,
                       "source_hashes": {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in names},
                       "notice_manifest_sha256": hashlib.sha256(module.packager.canonical(notice_manifest)).hexdigest()}
            raw = json.dumps(receipt).encode()
            (root / "license-admission.json").write_bytes(raw)
            lock = {"base": {"reference": receipt["base_reference"]}, "python": {"sha256": "a" * 64,
                    "license_admission": {"status": "ADMITTED", "receipt_sha256": hashlib.sha256(raw).hexdigest()}}}
            module.require_license_admission(lock)
            for name in names:
                with self.subTest(name=name):
                    original = (root / name).read_bytes()
                    (root / name).write_bytes(b"changed-source")
                    with self.assertRaisesRegex(module.Refusal, "LICENSE_NOT_ADMITTED"):
                        module.require_license_admission(lock)
                    (root / name).write_bytes(original)
            (root / "notices/LICENSE.fixture.txt").write_bytes(b"modified")
            with self.assertRaisesRegex(module.Refusal, "LICENSE_NOT_ADMITTED"):
                module.require_license_admission(lock)

    def test_constructed_closure_or_base_change_blocks_before_docker_build(self):
        module = self.load()
        self.assertTrue(hasattr(module, "verify_constructed_admission"), "constructed closure admission binding missing")
        receipt = {"selected_python_closure_sha256": "a" * 64, "base_reference": "public-base",
                   "archive_sha256": "b" * 64, "helper_files": {"helper": "c" * 64}, "runner_sha256": "d" * 64, "notice_manifest_sha256": "f" * 64}
        manifest = {"python_closure_sha256": "a" * 64, "base": {"reference": "public-base"},
                    "python_archive_sha256": "b" * 64, "helper": {"helper": "c" * 64}, "runner_sha256": "d" * 64, "notice_manifest_sha256": "f" * 64}
        module.verify_constructed_admission(receipt, manifest)
        for name in ("python_closure_sha256", "python_archive_sha256", "runner_sha256", "notice_manifest_sha256"):
            changed = dict(manifest, **{name: "e" * 64})
            with self.subTest(name=name), self.assertRaisesRegex(module.Refusal, "LICENSE_NOT_ADMITTED"):
                module.verify_constructed_admission(receipt, changed)
        changed = dict(manifest, base={"reference": "different-base"})
        with self.assertRaises(module.Refusal):
            module.verify_constructed_admission(receipt, changed)

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
