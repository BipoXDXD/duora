"""Real temporary files/locks; no Docker, services or installation outside this directory."""
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import install_executor as installer
import runtime_guard as guard
from test_runtime_guard import reviewed_profile

COMMIT = "a" * 40


class InstallationTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        files = {}
        for name in installer.FILES:
            content = ("# synthetic " + name + "\n").encode()
            (self.source / name).write_bytes(content)
            files[name] = {"sha256": hashlib.sha256(content).hexdigest(), "mode": 493 if name == "duora_deploy.py" else 384}
        manifest = {"schema": 1, "sourceCommit": COMMIT, "guardVersion": 1, "files": files,
                    "qualificationRun": "https://github.com/BipoXDXD/duora/actions/runs/12345678"}
        (self.source / "bundle.json").write_text(json.dumps(manifest))
        self.manifest_hash = hashlib.sha256((self.source / "bundle.json").read_bytes()).hexdigest()
        state, ingress = self.root / "state", self.root / "ingress"
        for directory in [state, ingress, ingress / "duora"]: directory.mkdir()
        for lock in [state / ".deploy.lock", ingress / "host.lock", ingress / "duora/release.lock"]: lock.touch()
        self.entry = self.root / "duora-deploy"
        self.entry.write_text("original executor\n")
        self.paths = installer.InstallPaths(self.entry, self.root / "versions", self.root / "preimages", state, ingress)
        self.inventory = {"profile": reviewed_profile(), "audit": {"reviewed": "synthetic reference"}}
        self.inventory["inventorySha256"] = guard.digest(self.inventory)
        self.old_hash = hashlib.sha256(self.entry.read_bytes()).hexdigest()

    def invoke(self, *, apply: bool = False, inspect=None, idle=None, reviewed=None) -> dict:
        return installer.install_bundle(self.source, expected_commit=COMMIT, manifest_sha256=self.manifest_hash,
            reviewed_inventory_sha256=self.inventory["inventorySha256"] if reviewed is None else reviewed,
            expected_executor_sha256=self.old_hash, paths=self.paths,
            inspect_runtime=inspect or (lambda: self.inventory), require_idle=idle or (lambda: None), apply=apply)

    def test_closed_bundle_requires_every_exact_member_and_hash(self) -> None:
        self.assertEqual(installer.validate_bundle(self.source, expected_commit=COMMIT,
                         manifest_sha256=self.manifest_hash)["sourceCommit"], COMMIT)
        (self.source / "extra.py").write_text("unexpected")
        with self.assertRaises(installer.InstallError): self.invoke()
        (self.source / "extra.py").unlink()
        (self.source / "runtime_guard.py").write_text("changed")
        with self.assertRaises(installer.InstallError): self.invoke()

    def test_symlink_member_and_wrong_source_or_manifest_are_refused(self) -> None:
        (self.source / "runtime_guard.py").unlink()
        (self.source / "runtime_guard.py").symlink_to(self.entry)
        with self.assertRaises(installer.InstallError): self.invoke()
        with self.assertRaises(installer.InstallError):
            installer.validate_bundle(self.source, expected_commit="b" * 40, manifest_sha256=self.manifest_hash)

    def test_readonly_preflight_never_creates_versions_baseline_receipts_or_changes_entry(self) -> None:
        self.assertEqual(self.invoke()["status"], "preflight")
        self.assertEqual(hashlib.sha256(self.entry.read_bytes()).hexdigest(), self.old_hash)
        self.assertFalse(self.paths.versions.exists())
        self.assertFalse((self.paths.state / guard.BASELINE_NAME).exists())
        self.assertFalse((self.paths.state / "executor-install-receipt.json").exists())

    def test_apply_installs_one_complete_version_and_records_exact_source_and_baseline(self) -> None:
        result = self.invoke(apply=True)
        self.assertEqual(result["status"], "installed")
        self.assertTrue(self.entry.is_symlink())
        self.assertEqual(self.entry.resolve(), self.paths.versions / COMMIT / "duora_deploy.py")
        self.assertEqual(json.loads((self.paths.state / guard.BASELINE_NAME).read_text()), self.inventory["profile"])
        self.assertEqual(result["sourceCommit"], COMMIT)
        self.assertEqual(result["executorSha256"], hashlib.sha256(self.entry.read_bytes()).hexdigest())

    def test_no_automatic_enrollment_of_changed_inventory_active_queue_or_lock_contention(self) -> None:
        with self.assertRaises(installer.InstallError): self.invoke(apply=True, reviewed="0" * 64)
        def busy() -> None: raise installer.InstallError("synthetic active queue")
        with self.assertRaises(installer.InstallError): self.invoke(apply=True, idle=busy)
        import fcntl
        with (self.paths.ingress / "host.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(installer.InstallError): self.invoke(apply=True)
        self.assertFalse(self.entry.is_symlink())

    def test_failed_postcheck_recovers_only_own_files_and_keeps_original_executor(self) -> None:
        calls = 0
        def inspect() -> dict:
            nonlocal calls
            calls += 1
            if calls > 1: raise installer.InstallError("synthetic post-activation failure")
            return self.inventory
        with self.assertRaises(installer.InstallError): self.invoke(apply=True, inspect=inspect)
        self.assertFalse(self.entry.is_symlink())
        self.assertEqual(hashlib.sha256(self.entry.read_bytes()).hexdigest(), self.old_hash)
        self.assertFalse((self.paths.state / guard.BASELINE_NAME).exists())

    def test_executor_preimage_drift_refuses_without_overwrite(self) -> None:
        self.entry.write_text("changed by someone else\n")
        with self.assertRaises(installer.InstallError): self.invoke(apply=True)
        self.assertEqual(self.entry.read_text(), "changed by someone else\n")
