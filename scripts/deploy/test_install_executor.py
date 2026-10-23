"""Real temporary files/locks; no Docker, services or installation outside this directory."""
import hashlib
import json
import tempfile
import unittest
import os
import signal
from unittest.mock import patch
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
        def inspect() -> dict:
            if self.entry.is_symlink(): raise installer.InstallError("synthetic post-activation failure")
            return self.inventory
        with self.assertRaises(installer.InstallError): self.invoke(apply=True, inspect=inspect)
        self.assertFalse(self.entry.is_symlink())
        self.assertEqual(hashlib.sha256(self.entry.read_bytes()).hexdigest(), self.old_hash)
        self.assertFalse((self.paths.state / guard.BASELINE_NAME).exists())

    def test_executor_preimage_drift_refuses_without_overwrite(self) -> None:
        self.entry.write_text("changed by someone else\n")
        with self.assertRaises(installer.InstallError): self.invoke(apply=True)
        self.assertEqual(self.entry.read_text(), "changed by someone else\n")

    def test_release_and_preservation_files_are_rechecked_against_the_reviewed_snapshot(self) -> None:
        state = self.paths.state
        (state / "releases").mkdir()
        (state / "current.json").write_text('{"releaseId":"current"}')
        for name in ["current.json", "current.env"]:
            (state / "releases" / name).write_text("synthetic current identity")
        pin = self.root / "coolify-deploy.json"
        pin.write_text("synthetic pinned manifest and Compose/Caddy hashes")
        baseline = installer.installation_context(self.paths, (pin,))
        for changed in [state / "current.json", state / "releases/current.json", state / "releases/current.env", pin]:
            original = changed.read_bytes()
            changed.write_bytes(original + b" changed")
            with self.subTest(changed=changed.name), self.assertRaises(installer.InstallError):
                installer.require_installation_context(baseline, self.paths, (pin,))
            changed.write_bytes(original)

    def test_actual_pinned_compose_json_is_selected_and_its_pinned_hash_is_required(self) -> None:
        actual = self.root / "compose.coolify.json"
        actual.write_text('{"name":"duora","services":{},"syntheticRegionalCut":"preserved"}')
        pin = {"compose": str(actual), "compose_sha256": hashlib.sha256(actual.read_bytes()).hexdigest()}
        # Ops runs unprivileged: model root-only filesystem metadata at the syscall boundary.
        # The native Actions lab separately exercises real root ownership/ancestry and unsafe parents/links.
        real_stat = Path.lstat
        def protected_metadata(path):
            metadata = list(real_stat(path))
            metadata[4] = 0
            metadata[0] = int(metadata[0]) & ~0o022
            return os.stat_result(metadata)
        with patch.object(Path, "lstat", protected_metadata), patch.object(os, "geteuid", return_value=0):
            self.assertEqual(installer.pinned_compose(pin), actual)
            actual.write_text("unreviewed changed configuration")
            with self.assertRaises(installer.InstallError): installer.pinned_compose(pin)

    def test_interrupted_activation_has_explicit_hash_guarded_recovery_under_real_locks(self) -> None:
        receiver = installer._module(Path(__file__).resolve().parents[1] / "release/coolify/receiver.py", "installer_crash_receiver")
        for boundary in ["fence", "pending", "baseline", "entry", "receipt"]:
            with self.subTest(boundary=boundary):
                if boundary != "fence": self.setUp()
                setattr(receiver, "STATE", self.paths.ingress)
                original_write, original_sync = installer._write, installer._sync
                def interrupted_write(path, raw, **kwargs):
                    original_write(path, raw, **kwargs)
                    if path.name == {"pending": "executor-install-pending.json", "baseline": guard.BASELINE_NAME,
                                      "receipt": "executor-install-receipt.json"}.get(boundary):
                        os.kill(os.getpid(), signal.SIGKILL)
                    if boundary == "fence" and path == self.paths.ingress / "duora-executor-install/pending.json":
                        os.kill(os.getpid(), signal.SIGKILL)
                def interrupted_sync(path):
                    original_sync(path)
                    if boundary == "entry" and path == self.entry.parent and self.entry.is_symlink():
                        os.kill(os.getpid(), signal.SIGKILL)
                child = os.fork()
                if child == 0:
                    try:
                        with patch.object(installer, "_write", side_effect=interrupted_write), \
                                patch.object(installer, "_sync", side_effect=interrupted_sync):
                            self.invoke(apply=True)
                    finally: os._exit(91)
                _pid, status = os.waitpid(child, 0)
                self.assertTrue(os.WIFSIGNALED(status), "Expected the actual child to die at " + boundary)
                fence = self.paths.ingress / "duora-executor-install/pending.json"
                pending = json.loads(fence.read_text())
                with self.assertRaises(ValueError): receiver.require_host_idle()
                with self.assertRaises(installer.InstallError): self.invoke(apply=True)
                def idle():
                    installer.require_ingress_idle(receiver, self.paths, expected_commit=COMMIT, manifest_sha256=self.manifest_hash)
                if boundary == "fence":
                    original_fence = fence.read_bytes()
                    fence.write_text(json.dumps(dict(pending, attemptSha256="0" * 64)))
                    with self.assertRaises(installer.InstallError):
                        installer.recover_installation(paths=self.paths, pending=pending, require_idle=idle)
                    self.assertNotEqual(fence.read_bytes(), original_fence, "Never rewrite an unowned fence")
                    fence.write_bytes(original_fence)
                foreign = self.paths.ingress / "duora/pending.json"
                foreign.write_text('{"release":"foreign-owned-intent"}')
                with self.assertRaises(installer.InstallError):
                    installer.recover_installation(paths=self.paths, pending=pending, require_idle=idle)
                self.assertTrue(foreign.exists(), "Recovery cannot remove a foreign pending release")
                foreign.unlink()  # Test-owned synthetic intent, never a production recovery operation.
                # Foreign baseline means recovery must refuse before replacing either file.
                if boundary == "entry":
                    baseline_path = self.paths.state / guard.BASELINE_NAME
                    original = baseline_path.read_bytes()
                    baseline_path.write_text("foreign intervention")
                    with self.assertRaises(installer.InstallError):
                        installer.recover_installation(paths=self.paths, pending=pending, require_idle=idle)
                    self.assertTrue(self.entry.is_symlink())
                    baseline_path.write_bytes(original)
                installer.recover_installation(paths=self.paths, pending=pending, require_idle=idle)
                self.assertFalse(self.entry.is_symlink())
                self.assertEqual(installer._sha(self.entry), self.old_hash)
                self.assertFalse((self.paths.state / guard.BASELINE_NAME).exists())
                self.assertFalse((self.paths.state / "executor-install-pending.json").exists())
                self.assertFalse(fence.exists())
                receiver.require_host_idle()

    def test_sigkill_during_own_recovery_is_resumable_without_unfencing_receiver(self) -> None:
        original_sync = installer._sync
        def kill_after_swap(path):
            original_sync(path)
            if path == self.entry.parent and self.entry.is_symlink(): os.kill(os.getpid(), signal.SIGKILL)
        child = os.fork()
        if child == 0:
            try:
                with patch.object(installer, "_sync", side_effect=kill_after_swap): self.invoke(apply=True)
            finally: os._exit(91)
        _pid, status = os.waitpid(child, 0)
        self.assertTrue(os.WIFSIGNALED(status))
        fence = self.paths.ingress / "duora-executor-install/pending.json"
        pending = json.loads(fence.read_text())
        original_write = installer._write
        def kill_after_restoration(path, raw, **kwargs):
            original_write(path, raw, **kwargs)
            if path == self.entry: os.kill(os.getpid(), signal.SIGKILL)
        child = os.fork()
        if child == 0:
            try:
                with patch.object(installer, "_write", side_effect=kill_after_restoration):
                    installer.recover_installation(paths=self.paths, pending=pending, require_idle=lambda: None)
            finally: os._exit(92)
        _pid, status = os.waitpid(child, 0)
        self.assertTrue(os.WIFSIGNALED(status))
        self.assertTrue(fence.exists())
        self.assertEqual(installer._sha(self.entry), self.old_hash)
        installer.recover_installation(paths=self.paths, pending=pending, require_idle=lambda: None)
        self.assertFalse(fence.exists())
