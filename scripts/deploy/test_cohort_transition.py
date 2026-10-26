"""Real root-owned files, flock and process-death boundaries in disposable Actions.

No Docker, network, product runtime or participant data. The parent is a single
ordinary ops test; its child runs all transaction cases under /root and reports
every assertion. It never silently skips a missing privilege or missing API.
"""
import copy
import fcntl
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
from typing import Callable, cast
import unittest
from unittest.mock import patch

import install_executor as installer
import runtime_guard as guard
from test_runtime_guard import reviewed_profile

OLD = "1ccd1ada4777c80081c4f9bb2a64a500b1e221f794406319504d9e66cbb975cb"
NEW = "e9af03f08162ecf2bda094d38511b44b810043863d3104f60ea1046ae687f3b3"
COMMIT = "a" * 40
FIXTURES = Path(__file__).parent / "fixtures"


def file_identity(path: Path) -> tuple:
    info = path.lstat()
    return (info.st_mode, os.readlink(path) if path.is_symlink() else None,
            None if path.is_dir() else hashlib.sha256(path.read_bytes()).hexdigest())


def root_cases() -> type[unittest.TestCase]:
    class CohortRootCases(unittest.TestCase):
        def setUp(self) -> None:
            self.assertEqual(os.geteuid(), 0)
            temporary = tempfile.TemporaryDirectory(prefix="duora-cohort-test-", dir="/root")
            self.addCleanup(temporary.cleanup)
            self.root = Path(temporary.name)
            self.source = self.root / "source"
            self.source.mkdir(mode=0o700)
            self.entry = self.root / "duora-deploy"
            self.entry.write_bytes(b"# synthetic unchanged executor\n")
            self.entry.chmod(0o755)
            self.old_hash = hashlib.sha256(self.entry.read_bytes()).hexdigest()
            files = {}
            for name in installer.FILES:
                content = self.entry.read_bytes() if name == "duora_deploy.py" else ("# synthetic " + name + "\n").encode()
                target = self.source / name
                target.write_bytes(content)
                target.chmod(0o755 if name == "duora_deploy.py" else 0o600)
                files[name] = {"sha256": hashlib.sha256(content).hexdigest(),
                               "mode": 0o755 if name == "duora_deploy.py" else 0o600}
            self.manifest = {"schema": 1, "sourceCommit": COMMIT, "guardVersion": 1, "files": files,
                             "qualificationRun": "https://github.com/BipoXDXD/duora/actions/runs/12345678"}
            self.save_manifest()
            state, ingress = self.root / "state", self.root / "ingress"
            for directory in (state, ingress, ingress / "duora"):
                directory.mkdir(mode=0o700)
            for path in (state / ".deploy.lock", ingress / "host.lock", ingress / "duora/release.lock"):
                path.touch(mode=0o600)
            self.paths = installer.InstallPaths(self.entry, self.root / "executor-versions",
                                                self.root / "preimages", state, ingress)
            self.old_executor_target = self.paths.versions / ("c" * 40) / "duora_deploy.py"
            self.old_executor_target.parent.mkdir(parents=True, mode=0o700)
            old_files = {}
            for name in installer.FILES:
                content = (self.source / name).read_bytes()
                if name == "install_executor.py": content = b"# synthetic qualified legacy installer\n"
                target = self.old_executor_target.parent / name
                target.write_bytes(content)
                target.chmod(0o755 if name == "duora_deploy.py" else 0o600)
                old_files[name] = {"sha256": hashlib.sha256(content).hexdigest(),
                                   "mode": 0o755 if name == "duora_deploy.py" else 0o600}
            old_manifest = {"schema": 1, "sourceCommit": "c" * 40, "guardVersion": 1, "files": old_files,
                            "qualificationRun": "https://github.com/BipoXDXD/duora/actions/runs/12345677"}
            old_manifest_path = self.old_executor_target.parent / "bundle.json"
            old_manifest_path.write_text(json.dumps(old_manifest))
            old_manifest_path.chmod(0o600)
            self.entry.unlink()
            self.entry.symlink_to(self.old_executor_target)
            self.receiver = self.root / "coolify-release/receiver.py"
            self.old_target = self.receiver.parent / "versions" / ("2.0.2-" + OLD) / "receiver.py"
            self.old_target.parent.mkdir(parents=True, mode=0o700)
            self.old_target.write_bytes((FIXTURES / "receiver-2.0.2-1ccd.py.txt").read_bytes())
            self.old_target.chmod(0o755)
            self.receiver.symlink_to(self.old_target)
            self.new_target = self.receiver.parent / "versions" / ("2.1.1-" + NEW) / "receiver.py"
            self.receiver_source = self.root / "candidate-receiver.py"
            self.receiver_source.write_bytes((FIXTURES / "receiver-2.1.1-e9af.py.txt").read_bytes())
            self.receiver_source.chmod(0o600)
            binding = patch.object(installer, "RECEIVER_PATH", self.receiver)
            binding.start()
            self.addCleanup(binding.stop)
            self.sentinel = state / "current.json"
            self.sentinel.write_text('{"releaseId":"synthetic-current-F3"}')
            self.sentinel.chmod(0o600)
            self.previous = state / "previous.json"
            self.previous.write_text('{"releaseId":"synthetic-previous-A0A"}')
            self.previous.chmod(0o600)
            self.inventory = {"profile": reviewed_profile(), "audit": {"reviewed": "synthetic root transaction"}}
            self.inventory = self.inspect()
            self.old_baseline_raw = (json.dumps(self.inventory["profile"], sort_keys=True,
                                               separators=(",", ":")) + "\n").encode()
            baseline = state / guard.BASELINE_NAME
            baseline.write_bytes(self.old_baseline_raw)
            baseline.chmod(0o600)
            old_receipt = {"schema": 1, "status": "installed", "sourceCommit": "c" * 40,
                           "manifestSha256": hashlib.sha256(old_manifest_path.read_bytes()).hexdigest(),
                           "inventorySha256": self.inventory["inventorySha256"],
                           "baselineSha256": hashlib.sha256(self.old_baseline_raw).hexdigest(),
                           "executorSha256": self.old_hash, "files": old_files,
                           "qualificationRun": old_manifest["qualificationRun"],
                           "qualificationArtifactId": 122, "appliedAt": 1,
                           "preimage": str(self.root / "historical-preimage")}
            self.old_receipt_raw = (json.dumps(old_receipt, sort_keys=True, separators=(",", ":")) + "\n").encode()
            receipt = state / "executor-install-receipt.json"
            receipt.write_bytes(self.old_receipt_raw)
            receipt.chmod(0o600)

        def save_manifest(self) -> None:
            path = self.source / "bundle.json"
            path.write_text(json.dumps(self.manifest))
            path.chmod(0o600)
            self.manifest_hash = hashlib.sha256(path.read_bytes()).hexdigest()

        def idle(self) -> None:
            receiver = installer._module(self.receiver.resolve(), "cohort_test_installed_receiver")
            receiver.STATE = self.paths.ingress
            installer.require_ingress_idle(receiver, self.paths, expected_commit=COMMIT,
                                           manifest_sha256=self.manifest_hash)

        def inspect(self) -> dict:
            result = copy.deepcopy(self.inventory)
            result["audit"]["currentSha256"] = hashlib.sha256(self.sentinel.read_bytes()).hexdigest()
            result["audit"]["previousSha256"] = hashlib.sha256(self.previous.read_bytes()).hexdigest()
            result["inventorySha256"] = guard.digest({"profile": result["profile"], "audit": result["audit"]})
            return result

        def invoke(self, *, apply: bool = False, reviewed: str | None = None, inspect=None) -> dict:
            operation = getattr(installer, "transition_cohort", None)
            self.assertTrue(callable(operation), "Missing coordinated receiver/executor transaction")
            return cast(Callable[..., dict], operation)(self.source, self.receiver_source,
                expected_commit=COMMIT, manifest_sha256=self.manifest_hash,
                reviewed_inventory_sha256=self.inventory["inventorySha256"],
                reviewed_cohort_sha256=reviewed, expected_executor_sha256=self.old_hash,
                paths=self.paths, inspect_runtime=inspect or self.inspect,
                require_idle=self.idle, apply=apply, qualification_artifact=123)

        def snapshot(self) -> dict:
            return {str(p.relative_to(self.root)): file_identity(p) for p in self.root.rglob("*")}

        def apply(self, *, inspect=None) -> dict:
            preflight = self.invoke()
            return self.invoke(apply=True, reviewed=preflight["cohortSha256"], inspect=inspect)

        def pending(self) -> dict:
            return json.loads((self.paths.ingress / installer.FENCE_DIRECTORY / "pending.json").read_text())

        def recover(self, pending: dict) -> dict:
            return cast(Callable[..., dict], installer.recover_installation)(paths=self.paths, pending=pending,
                require_idle=self.idle, expected_bundle=(COMMIT, self.manifest_hash),
                inspect_runtime=self.inspect)

        def assert_old_pair(self) -> None:
            self.assertTrue(self.entry.is_symlink())
            self.assertEqual(os.readlink(self.entry), str(self.old_executor_target))
            self.assertEqual(self.entry.read_bytes(), b"# synthetic unchanged executor\n")
            self.assertEqual(os.readlink(self.receiver), str(self.old_target))
            self.assertEqual(self.sentinel.read_text(), '{"releaseId":"synthetic-current-F3"}')
            self.assertEqual(self.previous.read_text(), '{"releaseId":"synthetic-previous-A0A"}')
            self.assertEqual((self.paths.state / guard.BASELINE_NAME).read_bytes(), self.old_baseline_raw)
            self.assertEqual((self.paths.state / "executor-install-receipt.json").read_bytes(), self.old_receipt_raw)

        def killed_at_partial_pair(self, *, both_swapped: bool = False) -> dict:
            preflight = self.invoke()
            child = os.fork()
            if child == 0:
                original_sync = installer._sync
                def kill_after_first_swap(path: Path) -> None:
                    original_sync(path)
                    receiver_new = self.receiver.resolve() == self.new_target
                    executor_new = self.entry.is_symlink() and self.entry.resolve() == self.paths.versions / COMMIT / "duora_deploy.py"
                    reached = (receiver_new and executor_new) if both_swapped else (receiver_new != executor_new)
                    if reached:
                        fence = self.paths.ingress / installer.FENCE_DIRECTORY / "pending.json"
                        if not fence.is_file(): os._exit(71)
                        # A separate process must be denied all three locks while the pair is intermediate.
                        probe = "import fcntl,sys; blocked=0\nfor p in sys.argv[1:]:\n with open(p) as f:\n  try: fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)\n  except BlockingIOError: blocked+=1\nsys.exit(0 if blocked==3 else 72)"
                        paths = [self.paths.ingress / "host.lock", self.paths.ingress / "duora/release.lock", self.paths.state / ".deploy.lock"]
                        if subprocess.run([sys.executable, "-c", probe, *map(str, paths)], check=False).returncode:
                            os._exit(72)
                        os.kill(os.getpid(), signal.SIGKILL)
                with patch.object(installer, "_sync", kill_after_first_swap):
                    self.invoke(apply=True, reviewed=preflight["cohortSha256"])
                os._exit(73)
            _, status = os.waitpid(child, 0)
            self.assertTrue(os.WIFSIGNALED(status), "No actual process-death boundary observed")
            self.assertEqual(os.WTERMSIG(status), signal.SIGKILL)
            pending = self.pending()
            # Both real receiver generations must refuse ingress while recovery is outstanding.
            for target in (self.old_target, self.receiver_source):
                receiver = installer._module(target, "cohort_test_fenced_receiver")
                receiver.STATE = self.paths.ingress
                with self.assertRaises(ValueError): receiver.require_host_idle()
            return pending

        def test_preflight_does_not_stage_write_enroll_or_change_either_entry(self) -> None:
            before = self.snapshot()
            result = self.invoke()
            self.assertEqual(result["status"], "cohort-preflight")
            self.assertEqual(result["receiverBefore"]["sha256"], OLD)
            self.assertEqual(self.snapshot(), before)

        def test_one_lock_owner_installs_both_entries_and_preserves_current(self) -> None:
            result = self.apply()
            self.assertEqual(result["status"], "cohort-installed")
            self.assertEqual(installer.receiver_binding()["sha256"], NEW)
            self.assertEqual(self.entry.resolve(), self.paths.versions / COMMIT / "duora_deploy.py")
            self.assertEqual(hashlib.sha256(self.entry.read_bytes()).hexdigest(), self.old_hash)
            self.assertFalse((self.paths.ingress / installer.FENCE_DIRECTORY / "pending.json").exists())
            self.assertFalse((self.paths.state / "executor-install-pending.json").exists())
            self.assertEqual(self.sentinel.read_text(), '{"releaseId":"synthetic-current-F3"}')

        def test_wrong_reviewed_cohort_is_refused_without_staging_or_replacing(self) -> None:
            before = self.snapshot()
            with self.assertRaises(installer.InstallError): self.invoke(apply=True, reviewed="0" * 64)
            self.assertEqual(self.snapshot(), before)

        def test_changed_current_release_refuses_before_either_swap(self) -> None:
            preflight = self.invoke()
            self.sentinel.write_text('{"releaseId":"foreign-new-current"}')
            before = self.snapshot()
            with self.assertRaises(installer.InstallError):
                self.invoke(apply=True, reviewed=preflight["cohortSha256"])
            self.assertEqual(self.snapshot(), before)

        def test_changed_core_is_refused_before_either_swap(self) -> None:
            core = self.source / "duora_deploy.py"
            core.write_bytes(b"# forbidden concurrent core change\n")
            self.manifest["files"]["duora_deploy.py"]["sha256"] = hashlib.sha256(core.read_bytes()).hexdigest()
            self.save_manifest()
            with self.assertRaises(installer.InstallError): self.invoke()
            self.assert_old_pair()

        def test_candidate_symlink_is_refused_even_when_its_bytes_have_the_qualified_hash(self) -> None:
            self.receiver_source.unlink()
            self.receiver_source.symlink_to(FIXTURES / "receiver-2.1.1-e9af.py.txt")
            with self.assertRaises(installer.InstallError): self.invoke()
            self.assert_old_pair()

        def test_busy_lock_or_foreign_intent_refuses_without_replacing_pair(self) -> None:
            with (self.paths.ingress / "host.lock").open() as handle:
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                with self.assertRaises(installer.InstallError): self.invoke()
            foreign = self.paths.ingress / "goblinshift"
            foreign.mkdir(mode=0o700)
            (foreign / "pending.json").write_text('{"foreign":"untouched"}')
            with self.assertRaises(installer.InstallError): self.invoke()
            self.assert_old_pair()
            self.assertEqual((foreign / "pending.json").read_text(), '{"foreign":"untouched"}')

        def test_process_death_between_entries_keeps_fence_and_recovers_exact_old_pair(self) -> None:
            pending = self.killed_at_partial_pair()
            self.recover(pending)
            self.assert_old_pair()
            self.assertFalse((self.paths.ingress / installer.FENCE_DIRECTORY / "pending.json").exists())

        def test_process_death_after_both_swaps_still_has_guarded_static_return(self) -> None:
            pending = self.killed_at_partial_pair(both_swapped=True)
            self.recover(pending)
            self.assert_old_pair()

        def test_process_death_during_pair_recovery_keeps_fence_and_same_attempt_resumes(self) -> None:
            pending = self.killed_at_partial_pair(both_swapped=True)
            child = os.fork()
            if child == 0:
                original_sync = installer._sync
                def kill_during_return(path: Path) -> None:
                    original_sync(path)
                    receiver_old = self.receiver.resolve() == self.old_target
                    executor_old = self.entry.is_symlink() and self.entry.resolve() == self.old_executor_target
                    if receiver_old != executor_old:
                        if not (self.paths.ingress / installer.FENCE_DIRECTORY / "pending.json").is_file():
                            os._exit(74)
                        os.kill(os.getpid(), signal.SIGKILL)
                with patch.object(installer, "_sync", kill_during_return):
                    self.recover(pending)
                os._exit(75)
            _, status = os.waitpid(child, 0)
            self.assertTrue(os.WIFSIGNALED(status))
            self.assertEqual(os.WTERMSIG(status), signal.SIGKILL)
            self.assertEqual(self.pending(), pending)
            self.recover(pending)
            self.assert_old_pair()

        def test_recovery_refuses_foreign_receiver_before_overwriting_any_owned_entry(self) -> None:
            pending = self.killed_at_partial_pair()
            self.receiver.unlink()
            self.receiver.write_bytes(b"foreign root-owned receiver\n")
            before = (file_identity(self.entry), file_identity(self.receiver))
            with self.assertRaises(installer.InstallError): self.recover(pending)
            self.assertEqual((file_identity(self.entry), file_identity(self.receiver)), before)
            self.assertEqual(self.pending(), pending)

        def test_recovery_refuses_foreign_receiver_target_even_with_new_receiver_bytes(self) -> None:
            pending = self.killed_at_partial_pair(both_swapped=True)
            foreign = self.root / "foreign-receiver.py"
            foreign.write_bytes(self.receiver_source.read_bytes())
            foreign.chmod(0o755)
            self.receiver.unlink()
            self.receiver.symlink_to(foreign)
            before = (file_identity(self.entry), file_identity(self.receiver))
            with self.assertRaises(installer.InstallError): self.recover(pending)
            self.assertEqual((file_identity(self.entry), file_identity(self.receiver)), before)
            self.assertEqual(self.pending(), pending)

        def test_recovery_refuses_foreign_executor_target_with_identical_core_bytes(self) -> None:
            pending = self.killed_at_partial_pair(both_swapped=True)
            foreign = self.root / "foreign-executor.py"
            foreign.write_bytes(self.entry.read_bytes())
            foreign.chmod(0o755)
            self.entry.unlink()
            self.entry.symlink_to(foreign)
            before = (file_identity(self.entry), file_identity(self.receiver))
            with self.assertRaises(installer.InstallError): self.recover(pending)
            self.assertEqual((file_identity(self.entry), file_identity(self.receiver)), before)
            self.assertEqual(self.pending(), pending)

        def test_recovery_preserves_foreign_intent_and_rejects_changed_journal_hash(self) -> None:
            pending = self.killed_at_partial_pair()
            before = (file_identity(self.entry), file_identity(self.receiver))
            foreign = self.paths.ingress / "stillon"
            foreign.mkdir(mode=0o700)
            (foreign / "pending.json").write_text('{"foreign":"untouched"}')
            with self.assertRaises(installer.InstallError): self.recover(pending)
            self.assertEqual((file_identity(self.entry), file_identity(self.receiver)), before)
            (foreign / "pending.json").unlink()
            with self.assertRaises(installer.InstallError):
                self.recover({**pending, "attemptSha256": "0" * 64})
            self.assertEqual((file_identity(self.entry), file_identity(self.receiver)), before)
            self.assertEqual(self.pending(), pending)

        def test_changed_previous_release_refuses_recovery_before_replacing_either_entry(self) -> None:
            pending = self.killed_at_partial_pair(both_swapped=True)
            self.previous.write_text('{"releaseId":"foreign-new-previous"}')
            before = self.snapshot()
            with self.assertRaises(installer.InstallError): self.recover(pending)
            self.assertEqual(self.snapshot(), before)
            self.assertEqual(self.pending(), pending)

    return CohortRootCases


class CoordinatedTransitionActionsTests(unittest.TestCase):
    def test_root_filesystem_lock_and_process_death_boundaries(self) -> None:
        command = [sys.executable, str(Path(__file__).resolve()), "--root-child"]
        if os.geteuid() != 0: command = ["sudo", "-n", *command]
        result = subprocess.run(command, capture_output=True, text=True, timeout=90, check=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    if sys.argv[1:] == ["--root-child"]:
        outcome = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(root_cases()))
        raise SystemExit(0 if outcome.wasSuccessful() else 1)
    unittest.main()
