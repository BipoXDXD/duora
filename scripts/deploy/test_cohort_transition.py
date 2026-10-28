"""Real root-owned files, flock and process-death boundaries in disposable Actions.

No Docker, network, product runtime or participant data. The parent is a single
ordinary ops test; its child runs all transaction cases under /root and reports
every assertion. It never silently skips a missing privilege or missing API.
"""
import copy
import ctypes
import contextlib
import fcntl
import hashlib
import io
import json
import os
from pathlib import Path
import signal
import select
import subprocess
import sys
import tempfile
import time
from typing import Callable, cast
from types import SimpleNamespace
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
    return (info.st_mode, info.st_uid, info.st_gid, info.st_nlink,
            os.readlink(path) if path.is_symlink() else None,
            None if path.is_dir() else hashlib.sha256(path.read_bytes()).hexdigest())


def supervise_root_group(worker: Callable[[], int], *, timeout: float) -> int:
    """Own one isolated worker group; keep its leader unreaped until group cleanup."""
    libc = ctypes.CDLL(None, use_errno=True)
    previous = ctypes.c_int()
    if libc.prctl(37, ctypes.byref(previous), 0, 0, 0) != 0 or libc.prctl(36, 1, 0, 0, 0) != 0:
        raise OSError(ctypes.get_errno(), "Cannot establish the test-only Linux subreaper")
    reader, writer = os.pipe()
    acknowledgment_reader, acknowledgment_writer = os.pipe()
    child = os.fork()
    if child == 0:
        os.close(reader)
        os.close(acknowledgment_writer)
        try:
            os.setsid()
            os.umask(0o077)
            os.write(writer, b"1")
            os.close(writer)
            if os.read(acknowledgment_reader, 1) != b"1": os._exit(126)
            os.close(acknowledgment_reader)
            result = worker()
        except BaseException:
            import traceback
            traceback.print_exc()
            result = 126
        finally:
            sys.stdout.flush()
            sys.stderr.flush()
        os._exit(result)
    os.close(writer)
    os.close(acknowledgment_reader)
    try:
        readable, _, _ = select.select([reader], [], [], 2)
        if not readable or os.read(reader, 1) != b"1":
            os.kill(child, signal.SIGKILL)
            os.waitpid(child, 0)
            return 126
        os.write(acknowledgment_writer, b"1")
        os.close(acknowledgment_writer)
        acknowledgment_writer = -1
        deadline = time.monotonic() + timeout
        timed_out = False
        while os.waitid(os.P_PID, child, os.WEXITED | os.WNOHANG | os.WNOWAIT) is None:
            if time.monotonic() >= deadline:
                timed_out = True
                break
            time.sleep(0.02)
        # WNOWAIT pins the leader PID, preventing group-ID reuse during termination.
        for termination in (signal.SIGTERM, signal.SIGKILL):
            try: os.killpg(child, termination)
            except ProcessLookupError: pass
            if termination == signal.SIGTERM: time.sleep(0.1)
        _, status = os.waitpid(child, 0)
        drain_deadline = time.monotonic() + 2
        while True:
            # A nested test supervisor can own another session. Subreaping gives
            # us only descendants of this worker; never enumerate global PIDs.
            owned = Path(f"/proc/self/task/{os.getpid()}/children").read_text().split()
            for value in owned:
                orphan = int(value)
                if orphan <= 0 or orphan == os.getpid(): raise RuntimeError("Invalid owned test child")
                try: os.kill(orphan, signal.SIGKILL)
                except ProcessLookupError: pass
            try:
                adopted, _ = os.waitpid(-1, os.WNOHANG)
            except ChildProcessError:
                break
            if adopted == 0:
                if time.monotonic() >= drain_deadline: return 125
                time.sleep(0.02)
        return 124 if timed_out else os.waitstatus_to_exitcode(status)
    finally:
        os.close(reader)
        if acknowledgment_writer >= 0: os.close(acknowledgment_writer)
        if libc.prctl(36, previous.value, 0, 0, 0) != 0:
            raise OSError(ctypes.get_errno(), "Cannot restore the test-only subreaper state")


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
            setattr(receiver, "STATE", self.paths.ingress)
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

        def historical_installer(self):
            raw = (FIXTURES / "install-executor-cohort-55d2-before-review.py.txt").read_bytes()
            self.assertEqual(hashlib.sha256(raw).hexdigest(),
                             "55d2ae628262c78885787d0d3abab9a37699b62f9f626972cd0e7166b5308101")
            source = self.root / "historical-installer-55d2.py"
            source.write_bytes(raw)
            source.chmod(0o600)
            return installer._module(source, "cohort_historical_installer_55d2")

        def with_module(self, module, action: Callable) -> dict:
            # Each side of the regression gets fresh actual files and an isolated module binding.
            with patch.dict(globals(), {"installer": module}):
                case = type(self)("test_preflight_does_not_stage_write_enroll_or_change_either_entry")
                case.setUp()
                try: return action(case)
                finally: case.doCleanups()

        def cli_refusal(self, *, foreign: bool) -> dict:
            root = self.root
            compose, env, caddy, manifest_file = [root / n for n in ("compose.json", "host.env", "caddy", "release.json")]
            for item in (compose, env, caddy, manifest_file):
                item.write_text("synthetic protected input")
                item.chmod(0o600)
            pin = root / "coolify-deploy.json"
            pin.write_text(json.dumps({"compose": str(compose), "compose_sha256": hashlib.sha256(compose.read_bytes()).hexdigest(),
                                       "caddy": str(caddy), "manifest": str(manifest_file)}))
            pin.chmod(0o600)
            releases = self.paths.state / "releases"
            releases.mkdir(mode=0o700)
            for name in ("synthetic-current-F3.json", "synthetic-current-F3.env"):
                (releases / name).write_text("synthetic protected release")
                (releases / name).chmod(0o600)
            ingress_current = self.paths.ingress / "duora/current.json"
            ingress_current.write_text('{"synthetic":"receiver current"}')
            ingress_current.chmod(0o600)
            imported, runtime = root / "foreign-imported", root / "runtime-called"
            if foreign:
                active = root / "foreign" / COMMIT / "duora_deploy.py"
                active.parent.mkdir(parents=True, mode=0o700)
                active.write_text("from pathlib import Path\nPath(" + repr(str(imported)) + ").touch()\n")
                active.chmod(0o755)
                self.entry.unlink()
                self.entry.symlink_to(active)
            original_module, original_read, original_sha = installer._module, installer._protected_bytes, installer._sha
            def runtime_provider(*_args, **_kwargs):
                runtime.touch()
                raise RuntimeError("Controlled provider must not run for a refused preflight")
            candidate = SimpleNamespace(
                Paths=lambda compose_path, env_path, state: SimpleNamespace(compose_file=compose_path, host_env=env_path, state_dir=state),
                parse_manifest=lambda *_args, **_kwargs: SimpleNamespace(contract_version="v2", raw={}),
                runtime_inventory=runtime_provider)
            def module_loader(path: Path, name: str):
                return candidate if path == self.source / "duora_deploy.py" else original_module(path, name)
            def read_input(path: Path) -> bytes:
                mapped = {Path("/etc/duora/coolify-deploy.json"): pin, Path("/etc/duora/duora.env"): env}
                return original_read(mapped.get(path, path))
            def script_sha(path: Path) -> str:
                return self.manifest["files"]["install_executor.py"]["sha256"] if path == Path(installer.__file__) else original_sha(path)
            def idle(paths, *, expected_commit, manifest_sha256, **_kwargs):
                receiver = original_module(self.receiver.resolve(), "cohort_cli_verified_receiver")
                setattr(receiver, "STATE", paths.ingress)
                installer.require_ingress_idle(receiver, paths, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
            args = ["install_executor.py", "--bundle", str(self.source), "--source-commit", COMMIT,
                    "--manifest-sha256", self.manifest_hash, "--artifact-id", "123",
                    "--cohort-receiver", str(self.receiver_source), "--expected-executor-sha256", self.old_hash]
            before = self.snapshot()
            output, errors = io.StringIO(), io.StringIO()
            previous_mask = os.umask(0o077)
            try:
                with contextlib.ExitStack() as stack:
                    if not foreign:
                        handle = stack.enter_context((self.paths.ingress / "host.lock").open())
                        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    stack.enter_context(patch.object(sys, "argv", args))
                    stack.enter_context(patch.object(installer, "InstallPaths", return_value=self.paths))
                    stack.enter_context(patch.object(installer, "_module", side_effect=module_loader))
                    stack.enter_context(patch.object(installer, "_protected_bytes", side_effect=read_input))
                    stack.enter_context(patch.object(installer, "_sha", side_effect=script_sha))
                    stack.enter_context(patch.object(installer, "_verify_provenance", return_value=None))
                    stack.enter_context(patch.object(installer, "_production_idle", side_effect=idle))
                    stack.enter_context(contextlib.redirect_stdout(output))
                    stack.enter_context(contextlib.redirect_stderr(errors))
                    try: status = installer.main()
                    except Exception as error:
                        status = 1
                        errors.write(type(error).__name__ + ": " + str(error))
                return {"status": status, "foreignImported": imported.exists(), "runtimeCalled": runtime.exists(),
                        "unchanged": self.snapshot() == before, "stderr": errors.getvalue()}
            finally:
                os.umask(previous_mask)

        def metadata_recovery(self, *, saved: bool, hardlink: bool) -> dict:
            pending = self.killed_at_partial_pair(both_swapped=True)
            path = self.paths.preimages / pending["attempt"] / "duora-deploy" if saved else self.entry
            if hardlink:
                os.link(path, self.root / "foreign-hardlink", follow_symlinks=False)
                self.assertEqual(path.lstat().st_nlink, 2)
            else:
                os.lchown(path, 65534, 65534)
                self.assertEqual(path.lstat().st_uid, 65534)
            before = self.snapshot()
            refused = False
            marker = self.root / "recovery-callback-reached"
            def monitored_idle() -> None:
                marker.touch()
                self.idle()
            def monitored_inspect() -> dict:
                marker.touch()
                return self.inspect()
            try:
                cast(Callable[..., dict], installer.recover_installation)(paths=self.paths, pending=pending,
                    require_idle=monitored_idle, expected_bundle=(COMMIT, self.manifest_hash),
                    inspect_runtime=monitored_inspect)
            except installer.InstallError: refused = True
            return {"refused": refused, "unchanged": self.snapshot() == before,
                    "fenced": (self.paths.ingress / installer.FENCE_DIRECTORY / "pending.json").exists(),
                    "callbackReached": marker.exists()}

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
                setattr(receiver, "STATE", self.paths.ingress)
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

        def test_cli_refuses_foreign_homonym_before_import_runtime_or_write(self) -> None:
            historical, current = self.historical_installer(), installer
            old = self.with_module(historical, lambda case: case.cli_refusal(foreign=True))
            self.assertTrue(old["foreignImported"], "Historical P1 control did not execute the actual foreign module")
            new = self.with_module(current, lambda case: case.cli_refusal(foreign=True))
            self.assertFalse(new["foreignImported"])
            self.assertFalse(new["runtimeCalled"])
            self.assertTrue(new["unchanged"])
            self.assertEqual(new["status"], 1)
            self.assertIn("Static installation/preflight refused", new["stderr"])
            print('[cohort-review-regression] {"P1_foreign_control_reproduced":true,"candidate_refused_before_import":true}')

        def test_cli_busy_lock_refuses_before_runtime_callback_or_write(self) -> None:
            historical, current = self.historical_installer(), installer
            old = self.with_module(historical, lambda case: case.cli_refusal(foreign=False))
            self.assertTrue(old["runtimeCalled"], "Historical P1 control did not perform the actual early runtime callback")
            new = self.with_module(current, lambda case: case.cli_refusal(foreign=False))
            self.assertFalse(new["foreignImported"])
            self.assertFalse(new["runtimeCalled"])
            self.assertTrue(new["unchanged"])
            self.assertEqual(new["status"], 1)
            self.assertIn("Static installation/preflight refused", new["stderr"])
            print('[cohort-review-regression] {"P1_busy_lock_control_reproduced":true,"candidate_zero_runtime":true}')

        def test_initial_unreviewed_inventory_is_captured_under_locks_and_cannot_authorize_apply(self) -> None:
            operation = getattr(installer, "transition_cohort", None)
            self.assertTrue(callable(operation))
            probe = "import fcntl,sys; blocked=0\nfor p in sys.argv[1:]:\n with open(p) as f:\n  try: fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)\n  except BlockingIOError: blocked+=1\nsys.exit(0 if blocked==3 else 72)"
            def inspect_locked() -> dict:
                paths = [self.paths.ingress / "host.lock", self.paths.ingress / "duora/release.lock", self.paths.state / ".deploy.lock"]
                result = subprocess.run([sys.executable, "-c", probe, *map(str, paths)], check=False, timeout=5)
                self.assertEqual(result.returncode, 0, "Initial runtime inspection ran outside the three locks")
                return self.inspect()
            arguments = {"expected_commit": COMMIT, "manifest_sha256": self.manifest_hash,
                         "reviewed_inventory_sha256": None, "expected_executor_sha256": self.old_hash,
                         "paths": self.paths, "inspect_runtime": inspect_locked,
                         "require_idle": self.idle, "qualification_artifact": 123}
            before = self.snapshot()
            api = cast(Callable[..., dict], operation)
            preflight = api(self.source, self.receiver_source, **arguments)
            self.assertEqual(preflight["status"], "cohort-preflight")
            self.assertEqual(self.snapshot(), before)
            with self.assertRaises(installer.InstallError):
                api(self.source, self.receiver_source, **arguments,
                    reviewed_cohort_sha256=preflight["cohortSha256"], apply=True)
            self.assertEqual(self.snapshot(), before)

        def test_active_and_saved_executor_link_metadata_refuses_without_overwrite(self) -> None:
            historical, current = self.historical_installer(), installer
            for saved in (False, True):
                for hardlink in (False, True):
                    with self.subTest(saved=saved, hardlink=hardlink):
                        old = self.with_module(historical, lambda case: case.metadata_recovery(saved=saved, hardlink=hardlink))
                        self.assertFalse(old["refused"], "Historical P2 control did not reach the unguarded recovery")
                        self.assertFalse(old["unchanged"])
                        self.assertTrue(old["callbackReached"])
                        new = self.with_module(current, lambda case: case.metadata_recovery(saved=saved, hardlink=hardlink))
                        self.assertTrue(new["refused"])
                        self.assertTrue(new["unchanged"])
                        self.assertTrue(new["fenced"])
                        self.assertFalse(new["callbackReached"])
            print('[cohort-review-regression] {"P2_four_historical_controls_reproduced":true,"candidate_four_metadata_refusals":true}')

        def test_test_harness_timeout_kills_and_reaps_its_worker_and_fork_descendant(self) -> None:
            pids = self.root / "timeout-owned-pids.json"
            def blocked() -> int:
                descendant = os.fork()
                if descendant == 0:
                    time.sleep(30)
                    os._exit(0)
                pids.write_text(json.dumps([os.getpid(), descendant]))
                time.sleep(30)
                return 0
            self.assertEqual(supervise_root_group(blocked, timeout=2), 124)
            self.assertTrue(pids.is_file(), "The timeout probe never created its actual fork descendant")
            for owned in json.loads(pids.read_text()):
                with self.assertRaises(ProcessLookupError): os.kill(owned, 0)

    return CohortRootCases


class CoordinatedTransitionActionsTests(unittest.TestCase):
    def test_root_filesystem_lock_and_process_death_boundaries(self) -> None:
        command = [sys.executable, str(Path(__file__).resolve()), "--root-child"]
        if os.geteuid() != 0: command = ["sudo", "-n", *command]
        result = subprocess.run(command, capture_output=True, text=True, timeout=90, check=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    if sys.argv[1:] == ["--root-child"]:
        def run_cases() -> int:
            outcome = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(root_cases()))
            return 0 if outcome.wasSuccessful() else 1
        raise SystemExit(supervise_root_group(run_cases, timeout=60))
    unittest.main()
