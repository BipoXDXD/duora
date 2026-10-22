"""Closed release-bundle trust boundary; no Docker or registry access in these tests."""
from __future__ import annotations

import hashlib
import importlib
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "media"))
from launcher import Profile

ROOT = Path(__file__).resolve().parents[2]
COMMIT = "a" * 40
IMAGE = "ghcr.io/bipoxdxd/duora-media-sanitizer@sha256:" + "b" * 64
CONFIG = "sha256:" + "c" * 64
CASE_NAMES = [
    "writable-root-negative-control-rejects-dac-false-positive",
    "effective-cgroups-network-dns-storage-secrets-readonly-tmpfs-seccomp",
    "unauthorized-kernel-peer-rejected-before-header", "stdout-withholds-all-bytes",
    "stderr-withholds-all-bytes", "crash-withholds-all-bytes", "hard-deadline-withholds-bytes",
    "descendants-terminated-at-deadline", "kernel-oom-is-not-success", "one-slot-and-explicit-cancel",
    "sigkill-cannot-release-a-result", "launcher-sigkill-orphan-cleanup-and-durable-dedupe",
    "actual-unmodified-dotnet-sanitizer-produces-three-variants",
    "supplied-restricted-systemd-template-starts-and-runs-sanitizer",
]
BOOLEAN_CONTROLS = ["nonRoot", "noCapabilities", "noPrivileges", "seccomp", "noSecrets", "noStorage",
                    "noDockerSocket", "rootReadOnly", "rootMountReadOnly", "tmpfsBounded", "noExec",
                    "noNetwork", "noDns"]


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value), encoding="utf-8")


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class MediaReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.bundle = Path(self.temporary.name) / "release"
        self.bundle.mkdir()
        self.profile: dict[str, Any] = {"schema": 1, "image": IMAGE, "runtime_socket": "/run/user/1002/docker.sock",
                        "control_socket": "/run/duora-media/socket", "journal_directory": "/var/lib/duora-media",
                        "worker_uid": 1654, "control_gid": 1654}
        for name in ["launcher.py", "docker_runtime.py", "run_launcher.py"]:
            (self.bundle / name).write_bytes((ROOT / "scripts/media" / name).read_bytes())
        name = "duora-media-launcher.service"
        (self.bundle / name).write_bytes((ROOT / "infra/media/systemd" / name).read_bytes())
        write_json(self.bundle / "profile.json", self.profile)
        write_json(self.bundle / "sbom.cdx.json", {"bomFormat": "CycloneDX", "specVersion": "1.6",
                                                  "version": 1, "components": []})
        self.proof: dict[str, Any] = {
            "architecture": "x86_64", "candidateOnly": True, "complete": True,
            "cases": [{"name": name, "passed": True, "milliseconds": 1.0} for name in CASE_NAMES],
            "images": {"sanitizer": CONFIG, "isolation-probe": "sha256:" + "d" * 64},
            "runtime": {"ServerVersion": "synthetic-test", "CgroupDriver": "systemd", "CgroupVersion": "2"},
            "effectiveControls": {**dict.fromkeys(BOOLEAN_CONTROLS, True), "memory": "536870912",
                                  "swap": "0", "cpu": "100000 100000", "pids": "64"},
            "kernelOomObserved": True, "writableRootNegativeControl": True,
            "sanitizerResponseBytes": 2048, "sanitizerResponseHash": "e" * 64,
        }
        write_json(self.bundle / "isolation.json", self.proof)
        self.manifest: dict[str, Any] = {
            "schema": 1, "scope": "synthetic-candidate", "commit": COMMIT, "architecture": "amd64",
            "image": IMAGE, "imageConfig": CONFIG, "launcherVersion": "s2-control-v1",
            "protocol": {"control": 1, "stream": 1}, "launcherUid": 1002,
            "profileSha256": Profile.from_dict(self.profile, 1002).digest,
            "files": {path.name: digest(path) for path in self.bundle.iterdir()},
            "ci": {"run": 101, "attempt": 1}, "isolation": {"run": 102, "attempt": 1},
            "publication": {"run": 103, "attempt": 1},
        }
        write_json(self.bundle / "manifest.json", self.manifest)

    def verify(self, *, pin: str | None = None, commit: str = COMMIT, architecture: str = "amd64",
               scope: str = "synthetic-candidate"):
        self.assertTrue((ROOT / "scripts/media/release_contract.py").is_file(),
                        "S4 release bundle validation is not implemented")
        module = importlib.import_module("release_contract")
        return module.verify_bundle(self.bundle, pin or digest(self.bundle / "manifest.json"),
                                    commit, architecture, scope)

    def reseal(self, file_name: str | None = None):
        if file_name:
            self.manifest["files"][file_name] = digest(self.bundle / file_name)
        write_json(self.bundle / "manifest.json", self.manifest)

    def test_valid_bundle_returns_the_exact_worker_profile_pin(self):
        verified = self.verify()
        self.assertEqual(COMMIT, verified["commit"])
        self.assertEqual(Profile.from_dict(self.profile, 1002).digest, verified["profileSha256"])

    def test_arm64_proof_is_required_for_an_arm64_release(self):
        self.manifest["architecture"] = "arm64"
        self.proof["architecture"] = "aarch64"
        write_json(self.bundle / "isolation.json", self.proof)
        self.reseal("isolation.json")
        self.assertEqual("arm64", self.verify(architecture="arm64")["architecture"])
        with self.assertRaises(ValueError):
            self.verify(architecture="amd64")

    def test_payload_and_manifest_rewrite_cannot_replace_the_external_trust_pin(self):
        original_pin = digest(self.bundle / "manifest.json")
        (self.bundle / "launcher.py").write_text("malicious replacement", encoding="utf-8")
        self.reseal("launcher.py")
        with self.assertRaises(ValueError):
            self.verify(pin=original_pin)

    def test_mutable_foreign_or_different_profile_image_is_refused(self):
        for image in ["duora:latest", "ghcr.io/attacker/duora-media-sanitizer@sha256:" + "b" * 64,
                      "ghcr.io/bipoxdxd/duora-media-sanitizer@sha256:" + "c" * 64]:
            with self.subTest(image=image), self.assertRaises(ValueError):
                self.manifest["image"] = image
                self.reseal()
                self.verify()

    def test_unexpected_commit_scope_protocol_and_identity_are_refused(self):
        for field, value in [("commit", "d" * 40), ("scope", "production-approved"),
                             ("architecture", "ppc64le"), ("protocol", {"control": 2, "stream": 1}),
                             ("launcherVersion", "unknown"), ("launcherUid", 1654), ("schema", True)]:
            original = self.manifest[field]
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.manifest[field] = value
                self.reseal()
                self.verify()
            self.manifest[field] = original
        with self.assertRaises(ValueError):
            self.reseal()
            self.verify(pin="not-a-sha256")

    def test_profile_file_hash_and_canonical_control_pin_are_both_required(self):
        self.profile["control_socket"] = "/run/duora-media/different.sock"
        write_json(self.bundle / "profile.json", self.profile)
        with self.assertRaises(ValueError):
            self.verify()
        self.reseal("profile.json")
        with self.assertRaises(ValueError):
            self.verify()

    def test_missing_extra_altered_or_symlinked_file_is_refused(self):
        target = self.bundle / "run_launcher.py"
        original = target.read_bytes()
        target.unlink()
        with self.assertRaises(ValueError):
            self.verify()
        target.write_bytes(original + b"\n# changed\n")
        with self.assertRaises(ValueError):
            self.verify()
        target.write_bytes(original)
        extra = self.bundle / "unexpected.txt"
        extra.write_text("unreviewed", encoding="utf-8")
        with self.assertRaises(ValueError):
            self.verify()
        extra.unlink()
        outside = self.bundle.parent / "outside.py"
        outside.write_bytes(original)
        target.unlink()
        os.symlink(outside, target)
        with self.assertRaises(ValueError):
            self.verify()

    def test_file_path_escape_unknown_fields_and_duplicate_json_keys_are_refused(self):
        self.manifest["files"]["../outside.py"] = "a" * 64
        self.reseal()
        with self.assertRaises(ValueError):
            self.verify()
        del self.manifest["files"]["../outside.py"]
        self.manifest["environment"] = {"secret": "must-never-be-forwarded"}
        self.reseal()
        with self.assertRaises(ValueError):
            self.verify()
        del self.manifest["environment"]
        self.reseal()
        path = self.bundle / "manifest.json"
        path.write_text(path.read_text(encoding="utf-8").replace('"schema": 1', '"schema": 1, "schema": 1'),
                        encoding="utf-8")
        with self.assertRaises(ValueError):
            self.verify()

    def test_large_manifest_profile_proof_or_sbom_is_refused_before_parsing(self):
        for name, size in [("manifest.json", 16385), ("profile.json", 4097),
                           ("isolation.json", 65537), ("sbom.cdx.json", 8 * 1024 * 1024 + 1)]:
            path = self.bundle / name
            original = path.read_bytes()
            with self.subTest(name=name), self.assertRaises(ValueError):
                with path.open("wb") as stream:
                    stream.truncate(size)
                if name != "manifest.json":
                    self.reseal(name)
                self.verify()
            path.write_bytes(original)
            if name != "manifest.json":
                self.reseal(name)

    def test_failed_missing_duplicate_or_non_native_case_cannot_qualify(self):
        for change in [lambda proof: proof.update(complete=False),
                       lambda proof: proof.update(candidateOnly=False),
                       lambda proof: proof.update(architecture="aarch64"),
                       lambda proof: proof["cases"].pop(),
                       lambda proof: proof["cases"].append(proof["cases"][0]),
                       lambda proof: proof["cases"][0].update(passed=False),
                       lambda proof: proof["images"].update(sanitizer="sha256:" + "f" * 64),
                       lambda proof: proof.update(kernelOomObserved=False),
                       lambda proof: proof.update(writableRootNegativeControl=False),
                       lambda proof: proof["effectiveControls"].update(memory="max"),
                       lambda proof: proof["effectiveControls"].update(seccomp=False),
                       lambda proof: proof["runtime"].update(CgroupDriver="cgroupfs")]:
            proof = json.loads(json.dumps(self.proof))
            change(proof)
            write_json(self.bundle / "isolation.json", proof)
            self.reseal("isolation.json")
            with self.subTest(proof=proof), self.assertRaises(ValueError):
                self.verify()

    def test_valid_bom_and_exact_positive_run_attempt_references_are_required(self):
        for value in [{"run": True, "attempt": 1}, {"run": 101, "attempt": 0},
                      {"run": 101, "attempt": 1, "url": "https://attacker.invalid"}]:
            self.manifest["ci"] = value
            self.reseal()
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.verify()
        self.manifest["ci"] = {"run": 101, "attempt": 1}
        write_json(self.bundle / "sbom.cdx.json", {"bomFormat": "not-a-bom"})
        self.reseal("sbom.cdx.json")
        with self.assertRaises(ValueError):
            self.verify()
