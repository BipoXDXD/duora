"""Pure parts of the real-Docker swap proof; the proof itself runs in postgres_swap_bench.py and continuity_lab.py."""
import contextlib
import dataclasses
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import socket
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
from typing import Any
import unittest
from unittest.mock import patch

import continuity_lab as lab
import duora_deploy as executor
import postgres_swap as swap
import postgres_swap_bench as bench
import postgres_swap_driver as driver
import runtime_guard as guard
from test_postgres_swap import NEW_REF as swap_target, SwapFixture, source_manifest as manifest

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("restore_drill_under_test", REPO / "scripts/backup/restore_drill.py")
assert spec is not None and spec.loader is not None
drill = importlib.util.module_from_spec(spec)
sys.modules["restore_drill_under_test"] = drill
spec.loader.exec_module(drill)


class FixtureTests(unittest.TestCase):
    def test_target_is_the_qualified_digest_of_each_architecture(self) -> None:
        fixture = json.loads(bench.FIXTURE.read_text())
        self.assertEqual(fixture["runUrl"], "https://github.com/BipoXDXD/duora/actions/runs/42073873377")
        self.assertEqual(fixture["images"], {
            "amd64": "ghcr.io/bipoxdxd/duora-postgres@sha256:b5103bfd8ac7832915eed8170ffd0bcb94d9bca834cbd659114fa25606f0434e",
            "arm64": "ghcr.io/bipoxdxd/duora-postgres@sha256:fa94a32e8c70257f67505bad12a9da878a4df7f33cd52ba9e6319a4c91025d4f"})

    def test_v1_phase_keeps_the_legacy_digest(self) -> None:
        for architecture, reference in guard.LEGACY_DIGESTS.items():
            self.assertNotEqual(bench.target_for(architecture), reference)

    def test_source_comes_from_the_lock(self) -> None:
        lock = json.loads(bench.LOCK.read_text())
        self.assertEqual(bench.source_for("arm64"), lock["images"]["arm64"])


class AlternativeTargetTests(unittest.TestCase):
    OLD, NEW = "ghcr.io/x/duora-postgres@sha256:" + "a" * 64, "ghcr.io/x/duora-postgres@sha256:" + "b" * 64

    def fixture(self, images: str, origin: str) -> dict:
        return {"images": {"arm64": images}, "origin": {"arm64": origin}}

    def choose(self, running: str, images: str, origin: str) -> str:
        with patch.object(bench.Path, "read_text", return_value=json.dumps(self.fixture(images, origin))):
            return bench.alternative_digest(running, "arm64")

    def test_today_the_digest_in_production_swaps_to_the_target(self) -> None:
        self.assertEqual(self.choose(self.OLD, self.NEW, self.OLD), self.NEW)

    def test_with_the_lock_already_at_the_target_the_proof_swaps_back(self) -> None:
        self.assertEqual(self.choose(self.NEW, self.NEW, self.OLD), self.OLD)

    def test_a_third_digest_still_has_a_target(self) -> None:
        self.assertEqual(self.choose("ghcr.io/x/duora-postgres@sha256:" + "c" * 64, self.NEW, self.OLD), self.NEW)

    def test_no_different_digest_fails_instead_of_skipping(self) -> None:
        with self.assertRaises(bench.ProofFailure):
            self.choose(self.NEW, self.NEW, self.NEW)

    def test_real_fixture_has_origin_for_both_architectures(self) -> None:
        fixture = json.loads(bench.FIXTURE.read_text())
        self.assertEqual(fixture["origin"], {
            "amd64": "ghcr.io/bipoxdxd/duora-postgres@sha256:d32f8c4408e804bf8b57583c6fdfb8935380e6326bcc7ccb7397b1f386ada06c",
            "arm64": "ghcr.io/bipoxdxd/duora-postgres@sha256:a626e902dfc8165bf5b866ea03fd4b6c2eec739ec7b76872154504bee99dd930"})
        for architecture in ("amd64", "arm64"):
            self.assertNotEqual(fixture["origin"][architecture], fixture["images"][architecture])

    def test_a_lock_at_the_target_never_reaches_the_identical_digest_refusal(self) -> None:
        fixture = json.loads(bench.FIXTURE.read_text())
        for architecture in ("amd64", "arm64"):
            self.assertEqual(bench.alternative_digest(fixture["images"][architecture], architecture),
                             fixture["origin"][architecture])

    def test_bench_main_has_no_skip_left(self) -> None:
        self.assertNotIn("skipped", Path(bench.__file__).read_text())


class LockAtTargetTests(AlternativeTargetTests):
    """The lock pull request changes only the lock: nothing about the fixture may depend on what the lock says."""

    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        moved = Path(directory.name) / "lock.json"
        moved.write_text(json.dumps({"images": json.loads(bench.FIXTURE.read_text())["images"]}))
        patcher = patch.object(bench, "LOCK", moved)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_the_drill_pair_is_a_real_reverse_swap(self) -> None:
        fixture = json.loads(bench.FIXTURE.read_text())
        for architecture in ("amd64", "arm64"):
            source = bench.source_for(architecture)
            self.assertEqual(source, fixture["images"][architecture])
            self.assertEqual(bench.alternative_digest(source, architecture), fixture["origin"][architecture])


def finished_pid() -> int:
    """A real process that existed and is gone, as a proof killed by SIGKILL is once its parent reaps it."""
    process = subprocess.Popen([sys.executable, "-c", ""])
    process.wait()
    return process.pid


class HostGuardTests(unittest.TestCase):
    ACTIONS = {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted", "GITHUB_REPOSITORY": "BipoXDXD/duora"}

    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.base = Path(directory.name)
        self.paths = dict(PIN=self.base / "etc/duora/coolify-deploy.json", RELEASES=self.base / "opt/duora/releases",
                          INGRESS=self.base / "var/lib/coolify-release", BACKUP_COMMAND=self.base / "sbin/duora-backup",
                          DISPOSABLE_MARKER=self.base / "marker", CLAIM=self.base / "claim")
        for name, value in self.paths.items():
            patcher = patch.object(bench, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        ancestors = patch.object(bench, "unprotected_ancestors", return_value=[])
        ancestors.start()
        self.addCleanup(ancestors.stop)
        self.docker_calls: list[tuple] = []
        self.volumes = ""

    def docker(self, *arguments: str, **_: object) -> str:
        self.docker_calls.append(arguments)
        if arguments[:1] == ("version",): return "amd64"
        if arguments[:2] == ("volume", "ls"): return self.volumes
        return ""

    def production_files(self) -> list[Path]:
        files = [self.paths["PIN"], self.paths["PIN"].parent / "duora.env", self.paths["RELEASES"].parent / "compose.prod.yml",
                 self.paths["INGRESS"] / "host.lock", self.paths["BACKUP_COMMAND"]]
        for file in files:
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("production\n")
        return files

    def mark_disposable(self) -> None:
        self.paths["DISPOSABLE_MARKER"].write_text(socket.gethostname() + "\n")

    def run_main(self, environment: dict | None = None) -> None:
        with patch.object(bench, "docker", self.docker), patch.object(bench.os, "geteuid", return_value=0), \
                patch.dict(bench.os.environ, environment or {}, clear=True), \
                patch.object(sys, "argv", ["bench", "--mode", "pitr"]), \
                patch.object(bench.Bench, "__init__", side_effect=AssertionError("must refuse before building anything")):
            bench.main()

    def test_refusal_on_a_host_with_state_removes_nothing(self) -> None:
        files = self.production_files()
        self.mark_disposable()
        with self.assertRaises(bench.ProofFailure):
            self.run_main()
        for file in files:
            self.assertEqual(file.read_text(), "production\n")
        self.assertFalse(self.paths["CLAIM"].exists())

    def test_a_claim_left_by_a_killed_run_refuses_and_asks_for_cleanup(self) -> None:
        self.mark_disposable()
        original = '{"root": "/root/old", "registry": "r", "ancestors": [{"path": "/opt", "mode": 511, "uid": 0, "gid": 0}]}'
        self.paths["CLAIM"].write_text(original)
        with self.assertRaisesRegex(bench.ProofFailure, "--mode cleanup"):
            self.run_main()
        self.assertEqual(self.paths["CLAIM"].read_text(), original)
        self.assertEqual(self.docker_calls, [])

    def test_a_second_claim_in_a_race_never_overwrites_the_first(self) -> None:
        self.paths["CLAIM"].write_text("{}")
        with patch.object(bench, "require_host_free"), self.assertRaises(FileExistsError):
            bench.FixedHost().claim(root=self.base / "work", registry="registry")
        self.assertEqual(self.paths["CLAIM"].read_text(), "{}")

    def claim_text(self, pid: int) -> str:
        return json.dumps({"root": str(self.base / "work"), "registry": "registry", "pid": pid,
                           "ancestors": [{"path": "/opt", "mode": 511, "uid": 0, "gid": 0}]})

    def run_cleanup(self) -> bool:
        """Docker and the ancestors are doubles that record into docker_calls; the files and the claim are real."""
        with patch.object(bench, "docker", self.docker), patch.object(bench, "remove_holds") as holds, \
                patch.object(bench.subprocess, "run") as run, patch.object(bench, "restore_ancestors") as restore:
            try:
                return bench.cleanup_claimed()
            finally:
                self.docker_calls += [("holds",)] * holds.call_count + [("rm",)] * run.call_count
                self.docker_calls += [("restore", tuple(call.args)) for call in restore.call_args_list]

    def assert_host_untouched(self, files: list[Path]) -> None:
        for file in files:
            self.assertEqual(file.read_text(), "production\n")
        self.assertEqual(self.docker_calls, [])

    def test_claim_records_the_pid_of_the_proof(self) -> None:
        with patch.object(bench.os, "getpid", return_value=4321):
            bench.FixedHost().claim(root=Path("/root/work"), registry="registry")
        self.assertEqual(json.loads(self.paths["CLAIM"].read_text()),
                         {"root": "/root/work", "registry": "registry", "ancestors": [], "pid": 4321})

    def test_claim_is_complete_before_it_becomes_visible(self) -> None:
        link = os.link
        published = []
        def spying(source: str, destination: Path) -> None:
            self.assertFalse(self.paths["CLAIM"].exists())
            self.assertEqual(Path(source).parent, self.paths["CLAIM"].parent)
            published.append(json.loads(Path(source).read_text()))
            link(source, destination)
        with patch.object(bench.os, "getpid", return_value=4321), patch.object(bench.os, "link", spying):
            bench.FixedHost().claim(root=Path("/root/work"), registry="registry")
        self.assertEqual(published, [{"root": "/root/work", "registry": "registry", "ancestors": [], "pid": 4321}])
        self.assertEqual([path.name for path in self.base.iterdir()], ["claim"])

    def test_a_lost_race_leaves_no_temporary_and_protects_nothing(self) -> None:
        self.paths["CLAIM"].write_text("{}")
        host = bench.FixedHost()
        with patch.object(bench, "require_host_free"), patch.object(bench, "protect_ancestors") as protect, \
                self.assertRaises(FileExistsError):
            host.claim(root=self.base / "work", registry="registry")
        protect.assert_not_called()
        self.assertFalse(host.claimed)
        self.assertEqual([path.name for path in self.base.iterdir()], ["claim"])

    def test_process_alive_tells_a_running_pid_from_a_finished_one(self) -> None:
        self.assertTrue(bench.process_alive(os.getpid()))
        self.assertFalse(bench.process_alive(finished_pid()))

    def test_a_process_of_another_user_counts_as_alive(self) -> None:
        with patch.object(bench.os, "kill", side_effect=PermissionError(1, "Operation not permitted")):
            self.assertTrue(bench.process_alive(4321))

    def test_a_live_proof_refuses_a_new_one_without_asking_for_cleanup(self) -> None:
        self.mark_disposable()
        original = self.claim_text(os.getpid())
        self.paths["CLAIM"].write_text(original)
        with self.assertRaisesRegex(bench.ProofFailure, "PID %d is still running" % os.getpid()) as refused:
            self.run_main()
        self.assertNotIn("--mode cleanup", str(refused.exception))
        self.assertEqual(self.paths["CLAIM"].read_text(), original)
        self.assertEqual(self.docker_calls, [])

    def test_a_dead_proof_refuses_a_new_one_and_asks_for_cleanup(self) -> None:
        self.mark_disposable()
        self.paths["CLAIM"].write_text(self.claim_text(finished_pid()))
        with self.assertRaisesRegex(bench.ProofFailure, "--mode cleanup"):
            self.run_main()

    def test_a_truncated_claim_refuses_a_new_proof_and_asks_for_cleanup(self) -> None:
        self.mark_disposable()
        self.paths["CLAIM"].write_text('{"root": "/ro')
        with self.assertRaisesRegex(bench.ProofFailure, "--mode cleanup"):
            self.run_main()

    def test_cleanup_refuses_a_live_proof_and_touches_nothing(self) -> None:
        files = self.production_files()
        self.mark_disposable()
        original = self.claim_text(os.getpid())
        self.paths["CLAIM"].write_text(original)
        with self.assertRaisesRegex(bench.ProofFailure, "PID %d is still running" % os.getpid()):
            self.run_cleanup()
        self.assert_host_untouched(files)
        self.assertEqual(self.paths["CLAIM"].read_text(), original)

    def test_cleanup_of_a_dead_proof_removes_what_it_claimed(self) -> None:
        files = self.production_files()
        self.mark_disposable()
        (self.base / "work").mkdir()
        self.paths["CLAIM"].write_text(self.claim_text(finished_pid()))
        self.assertTrue(self.run_cleanup())
        self.assertIn(("restore", ([{"path": "/opt", "mode": 511, "uid": 0, "gid": 0}],)), self.docker_calls)
        self.assertFalse(any(file.exists() for file in files))
        self.assertFalse((self.base / "work").exists())
        self.assertFalse(self.paths["CLAIM"].exists())

    def test_cleanup_of_a_truncated_claim_removes_only_the_claim(self) -> None:
        """Every cut the old writer could leave: it died before protecting anything, so only the claim is the proof's."""
        files = self.production_files()
        self.mark_disposable()
        complete = '{"root": "/root/work", "registry": "registry", "ancestors": [{"path": "/opt", "mode": 511, "uid": 0, "gid": 0}]}'
        for cut in range(len(complete)):
            with self.subTest(cut=cut):
                self.paths["CLAIM"].write_text(complete[:cut])
                self.assertTrue(self.run_cleanup())
                self.assertFalse(self.paths["CLAIM"].exists())
                self.assert_host_untouched(files)

    def test_cleanup_off_a_disposable_host_keeps_even_a_truncated_claim(self) -> None:
        self.paths["CLAIM"].write_text('{"root": "/ro')
        self.assertFalse(self.run_cleanup())
        self.assertEqual(self.paths["CLAIM"].read_text(), '{"root": "/ro')

    def test_a_claim_that_is_not_a_cut_of_ours_is_refused_and_kept(self) -> None:
        files = self.production_files()
        self.mark_disposable()
        known = '"root": "/x", "registry": "r", "ancestors": []'
        for content in (b"garbage", b"\xff\xfe", b"{\xff", b"[]", b"null", b"1", b"{}", b'{"root": "/x"}',
                        ('{' + known + ', "extra": 1}').encode(), ('{' + known + ', "pid": 0}').encode(),
                        ('{' + known + ', "pid": -1}').encode(), ('{' + known + ', "pid": true}').encode(),
                        ('{' + known + ', "pid": "4321"}').encode()):
            with self.subTest(content=content):
                self.paths["CLAIM"].write_bytes(content)
                with self.assertRaisesRegex(bench.ProofFailure, "Unknown claim"):
                    self.run_cleanup()
                with self.assertRaisesRegex(bench.ProofFailure, "Unknown claim"):
                    self.run_main()
                self.assert_host_untouched(files)
                self.assertEqual(self.paths["CLAIM"].read_bytes(), content)

    def test_cleanup_refuses_a_claim_with_no_owner_to_check(self) -> None:
        files = self.production_files()
        self.mark_disposable()
        original = '{"root": "/root/work", "registry": "r", "ancestors": [{"path": "/opt", "mode": 511, "uid": 0, "gid": 0}]}'
        self.paths["CLAIM"].write_text(original)
        with self.assertRaisesRegex(bench.ProofFailure, "no owner PID"):
            self.run_cleanup()
        self.assert_host_untouched(files)
        self.assertEqual(self.paths["CLAIM"].read_text(), original)

    def test_each_fixed_location_alone_refuses(self) -> None:
        self.mark_disposable()
        for path in (self.paths["PIN"].parent, self.paths["RELEASES"].parent, self.paths["INGRESS"]):
            with self.subTest(path=path.name):
                path.mkdir(parents=True)
                with self.assertRaises(bench.ProofFailure):
                    self.run_main()
                self.assertTrue(path.exists())
                path.rmdir()
        self.paths["BACKUP_COMMAND"].parent.mkdir()
        self.paths["BACKUP_COMMAND"].write_text("x")
        with self.assertRaises(bench.ProofFailure):
            self.run_main()
        self.assertTrue(self.paths["BACKUP_COMMAND"].exists())

    def test_host_that_is_not_disposable_is_refused_before_docker(self) -> None:
        with self.assertRaises(bench.ProofFailure):
            self.run_main()
        self.assertEqual(self.docker_calls, [])

    def test_only_a_hosted_runner_or_this_hosts_marker_is_disposable(self) -> None:
        self.assertTrue(bench.host_is_disposable(self.ACTIONS))
        for changed in (dict(self.ACTIONS, RUNNER_ENVIRONMENT="self-hosted"), dict(self.ACTIONS, GITHUB_REPOSITORY="fork/duora"), {}):
            self.assertFalse(bench.host_is_disposable(changed))
        self.paths["DISPOSABLE_MARKER"].write_text("another-host\n")
        self.assertFalse(bench.host_is_disposable({}))
        self.mark_disposable()
        self.assertTrue(bench.host_is_disposable({}))

    def test_existing_data_volume_is_refused(self) -> None:
        self.mark_disposable()
        self.volumes = "duora_pgdata"
        with self.assertRaises(bench.ProofFailure):
            self.run_main()

    def test_existing_stack_is_refused(self) -> None:
        self.mark_disposable()
        with patch.object(self, "docker", lambda *a, **k: "abc" if a[:1] == ("ps",) else ""), self.assertRaises(bench.ProofFailure):
            self.run_main()

    def test_release_removes_only_what_this_run_claimed(self) -> None:
        unrelated = self.paths["PIN"].parent.parent / "keep.conf"
        unrelated.parent.mkdir(parents=True)
        unrelated.write_text("x")
        host = bench.FixedHost()
        host.claim(root=self.base / "work", registry="registry")
        for directory in (self.paths["PIN"].parent, self.paths["RELEASES"].parent, self.paths["INGRESS"]):
            directory.mkdir(parents=True)
        host.release()
        self.assertFalse(self.paths["PIN"].parent.exists())
        self.assertTrue(unrelated.exists())
        self.assertFalse(self.paths["CLAIM"].exists())

    def test_release_without_claim_removes_nothing(self) -> None:
        files = self.production_files()
        bench.FixedHost().release()
        self.assertTrue(all(file.exists() for file in files))

    def bench_stopping(self, result: Any) -> Any:
        stack = object.__new__(bench.Bench)
        stack.compose_command = ["docker", "compose"]
        stack.registry_name = "registry"
        stack.root = self.base / "work"
        stack.host = bench.FixedHost()
        stack.host.claim(root=stack.root, registry="registry")
        self.paths["PIN"].parent.mkdir(parents=True)
        def run(command, **_):
            if "down" in command:
                if isinstance(result, Exception): raise result
                return subprocess.CompletedProcess(command, result, "", "")
            return subprocess.CompletedProcess(command, 0, "", "")
        with patch.object(bench, "docker", self.docker), patch.object(bench.subprocess, "run", run):
            stack.stop()
        return stack

    def test_failed_compose_down_keeps_the_claim_for_cleanup(self) -> None:
        for result in (1, subprocess.TimeoutExpired("down", 180)):
            with self.subTest(result=str(result)):
                self.setUp()
                with patch.object(bench, "docker", self.docker):
                    self.bench_stopping(result)
                self.assertTrue(self.paths["CLAIM"].exists())
                self.assertTrue(self.paths["PIN"].parent.exists())

    def test_clean_compose_down_releases_the_claim(self) -> None:
        with patch.object(bench, "docker", self.docker):
            self.bench_stopping(0)
        self.assertFalse(self.paths["CLAIM"].exists())
        self.assertFalse(self.paths["PIN"].parent.exists())

    def test_cleanup_needs_a_claim_and_a_disposable_host(self) -> None:
        files = self.production_files()
        self.mark_disposable()
        with patch.object(bench, "docker", self.docker):
            self.assertFalse(bench.cleanup_claimed())
        self.assertTrue(all(file.exists() for file in files))


def directory(mode: int, uid: int = 0, gid: int = 0) -> Any:
    return SimpleNamespace(st_mode=stat.S_IFDIR | mode, st_uid=uid, st_gid=gid)


class ProtectedAncestorTests(unittest.TestCase):
    """The hosted image runs chmod -R 777 /opt; the executor rightly refuses to read under that."""
    RUNNER = {"/opt": directory(0o777), "/etc": directory(0o755), "/etc/duora": None, "/var": directory(0o755),
              "/var/lib": directory(0o755), "/root": directory(0o700)}

    def lstat(self, tree: dict) -> Any:
        def read(path):
            value = tree.get(str(path))
            if value is None:
                raise FileNotFoundError(path)
            return value
        return read

    def found(self, tree: dict, targets: list[Path]) -> list[dict]:
        with patch.object(bench.os, "lstat", self.lstat(tree)):
            return bench.unprotected_ancestors(targets)

    def test_the_runner_image_needs_only_opt_changed(self) -> None:
        targets = [Path("/etc/duora"), Path("/opt/duora/releases"), Path("/var/lib/coolify-release"), Path("/root/work")]
        self.assertEqual(self.found(self.RUNNER, targets), [{"path": "/opt", "mode": 0o777, "uid": 0, "gid": 0}])

    def test_group_owner_and_group_or_world_write_all_count(self) -> None:
        for name, entry in (("group", directory(0o755, gid=1001)), ("owner", directory(0o755, uid=1001)),
                            ("group-write", directory(0o775)), ("world-write", directory(0o757))):
            with self.subTest(name):
                self.assertEqual(len(self.found({"/data": entry}, [Path("/data/x")])), 1)

    def test_protected_directories_and_absent_ones_are_left_alone(self) -> None:
        self.assertEqual(self.found({"/data": directory(0o755)}, [Path("/data/x/y")]), [])

    def test_a_directory_is_listed_once_and_the_root_is_not_inspected(self) -> None:
        tree = {"/opt": directory(0o777), "/": directory(0o777)}
        self.assertEqual(len(self.found(tree, [Path("/opt/a"), Path("/opt/b")])), 1)

    def test_symlink_in_the_ancestry_is_refused_not_followed(self) -> None:
        with self.assertRaises(bench.ProofFailure):
            self.found({"/link": SimpleNamespace(st_mode=stat.S_IFLNK | 0o777, st_uid=0, st_gid=0)}, [Path("/link/x")])

    def changes(self) -> tuple[list, Any, Any]:
        calls: list = []
        return (calls, lambda path, uid, gid: calls.append(("chown", path, uid, gid)),
                lambda path, mode: calls.append(("chmod", path, mode)))

    def test_protection_removes_only_group_and_world_write_and_restores_exactly(self) -> None:
        entry = {"path": "/opt", "mode": 0o1777, "uid": 1001, "gid": 1001}
        calls, chown, chmod = self.changes()
        with patch.object(bench.os, "chown", chown), patch.object(bench.os, "chmod", chmod):
            bench.protect_ancestors([entry])
            bench.restore_ancestors([entry])
        self.assertEqual(calls, [("chown", "/opt", 0, 0), ("chmod", "/opt", 0o1755),
                                 ("chown", "/opt", 1001, 1001), ("chmod", "/opt", 0o1777)])

    def test_claim_protects_and_release_restores_with_the_recorded_state(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            entry = {"path": "/opt", "mode": 0o777, "uid": 0, "gid": 0}
            with patch.object(bench, "CLAIM", base / "claim"), patch.object(bench, "require_host_free"), \
                    patch.object(bench, "unprotected_ancestors", return_value=[entry]), \
                    patch.object(bench, "protect_ancestors") as protect, patch.object(bench, "restore_ancestors") as restore, \
                    patch.object(bench, "_remove_fixed_paths"):
                host = bench.FixedHost()
                host.claim(root=base / "work", registry="registry")
                protect.assert_called_once_with([entry])
                self.assertEqual(json.loads((base / "claim").read_text())["ancestors"], [entry])
                host.release()
                restore.assert_called_once_with([entry])

    def test_claim_on_the_runner_image_protects_opt_through_the_real_fixed_paths(self) -> None:
        with tempfile.TemporaryDirectory() as temporary, patch.object(bench, "CLAIM", Path(temporary) / "claim"), \
                patch.object(bench, "require_host_free"), patch.object(bench, "protect_ancestors") as protect, \
                patch.object(bench.os, "lstat", self.lstat(self.RUNNER)):
            bench.FixedHost().claim(root=Path("/root/work"), registry="registry")
        protect.assert_called_once_with([{"path": "/opt", "mode": 0o777, "uid": 0, "gid": 0}])

    def test_cleanup_of_a_killed_run_restores_the_recorded_ancestors(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            claim = Path(temporary) / "claim"
            entry = {"path": "/opt", "mode": 0o777, "uid": 0, "gid": 0}
            claim.write_text(json.dumps({"root": temporary + "/none", "registry": "r", "ancestors": [entry], "pid": finished_pid()}))
            with patch.object(bench, "CLAIM", claim), patch.object(bench, "host_is_disposable", return_value=True), \
                    patch.object(bench, "docker", return_value=""), patch.object(bench, "remove_holds"), \
                    patch.object(bench, "_remove_fixed_paths"), patch.object(bench.subprocess, "run"), \
                    patch.object(bench, "restore_ancestors") as restore:
                self.assertTrue(bench.cleanup_claimed())
            restore.assert_called_once_with([entry])


class ExplicitDirectoryTests(unittest.TestCase):
    """Created by the proof with an explicit mode at every level, whatever the umask or the parent's ACL says."""

    def setUp(self) -> None:
        directory_object = tempfile.TemporaryDirectory()
        self.addCleanup(directory_object.cleanup)
        self.base = Path(directory_object.name)
        previous = os.umask(0)
        self.addCleanup(os.umask, previous)
        chown = patch.object(bench.os, "chown")
        self.chown = chown.start()
        self.addCleanup(chown.stop)

    def mode(self, path: Path) -> int:
        return stat.S_IMODE(path.stat().st_mode)

    def test_every_new_level_gets_the_mode_with_umask_zero(self) -> None:
        target = self.base / "a/b/c"
        bench.ensure_root_directory(target)
        for level in (self.base / "a", self.base / "a/b", target):
            self.assertEqual(self.mode(level), 0o755)
            self.chown.assert_any_call(level, 0, 0)

    def test_an_existing_level_is_not_touched(self) -> None:
        existing = self.base / "a"
        existing.mkdir()
        existing.chmod(0o777)
        bench.ensure_root_directory(existing / "b")
        self.assertEqual(self.mode(existing), 0o777)
        self.assertEqual(self.mode(existing / "b"), 0o755)

    def test_write_root_creates_parents_with_explicit_mode(self) -> None:
        bench.write_root(self.base / "x/y/file", b"data", mode=0o644)
        self.assertEqual(self.mode(self.base / "x"), 0o755)
        self.assertEqual(self.mode(self.base / "x/y"), 0o755)
        self.assertEqual(self.mode(self.base / "x/y/file"), 0o644)

    def test_ingress_has_no_group_or_world_write_with_umask_zero(self) -> None:
        with patch.object(bench, "INGRESS", self.base / "lib/ingress"):
            bench.prepare_ingress()
        for path, expected in ((self.base / "lib", 0o755), (self.base / "lib/ingress", 0o755), (self.base / "lib/ingress/duora", 0o700)):
            self.assertEqual(self.mode(path), expected)
        for lock in (self.base / "lib/ingress/host.lock", self.base / "lib/ingress/duora/release.lock"):
            self.assertEqual(self.mode(lock), 0o600)

    def test_claim_closes_the_umask_for_the_proof_and_release_restores_it(self) -> None:
        with patch.object(bench, "CLAIM", self.base / "claim"), patch.object(bench, "require_host_free"), \
                patch.object(bench, "unprotected_ancestors", return_value=[]), patch.object(bench, "_remove_fixed_paths"):
            host = bench.FixedHost()
            host.claim(root=self.base / "work", registry="registry")
            self.assertEqual(os.umask(0o022), 0o022)
            host.release()
            self.assertEqual(os.umask(0), 0)


class ReauditAfterCreationTests(SwapFixture):
    def test_what_appears_after_the_claim_is_protected_by_prepare_fixed_paths(self) -> None:
        late = {"path": "/opt/duora", "mode": 0o775, "uid": 0, "gid": 0}
        with patch.multiple(bench, PIN=self.root / "fixed/pin.json", RELEASES=self.root / "fixed/releases",
                            INGRESS=self.ingress), patch.object(bench.os, "chown"), \
                patch.object(bench, "unprotected_ancestors", return_value=[late]) as found, \
                patch.object(bench, "protect_ancestors") as protect:
            bench.prepare_fixed_paths(self.release, self.paths)
            audited = found.call_args.args[0]
            protect.assert_called_once_with([late])
            self.assertIn(self.root / "fixed/releases" / (self.release.release_id + ".json"), audited)
            self.assertIn(self.paths.compose_file, audited)
            self.assertIn(self.paths.host_env, audited)

    def test_real_reaudit_sees_an_open_directory_created_after_the_claim(self) -> None:
        tree = {"/opt": directory(0o755), "/opt/duora": directory(0o777), "/opt/duora/releases": directory(0o755)}
        def lstat(path):
            if str(path) not in tree:
                raise FileNotFoundError(path)
            return tree[str(path)]
        with patch.object(bench.os, "lstat", lstat):
            found = bench.unprotected_ancestors([Path("/opt/duora/releases/release.json")])
        self.assertEqual([entry["path"] for entry in found], ["/opt/duora"])

    def test_a_file_target_with_open_mode_is_found_too(self) -> None:
        tree = {"/root": directory(0o700), "/root/compose.yml": SimpleNamespace(st_mode=stat.S_IFREG | 0o666, st_uid=0, st_gid=0)}
        def lstat(path):
            if str(path) not in tree:
                raise FileNotFoundError(path)
            return tree[str(path)]
        with patch.object(bench.os, "lstat", lstat):
            found = bench.unprotected_ancestors([Path("/root/compose.yml")])
        self.assertEqual(found, [{"path": "/root/compose.yml", "mode": 0o666, "uid": 0, "gid": 0}])


class AncestryReportTests(unittest.TestCase):
    TREE = {"/opt": directory(0o777), "/opt/duora": directory(0o775, gid=1001), "/opt/duora/releases": directory(0o755)}

    def report(self) -> str:
        def lstat(path):
            if str(path) not in self.TREE:
                raise FileNotFoundError(path)
            return self.TREE[str(path)]
        with patch.object(bench.os, "lstat", lstat), patch.object(bench.os, "listxattr", lambda *_a, **_k: ["system.posix_acl_default"], create=True):
            return bench.ancestry_report(Path("/opt/duora/releases/release.json"))

    def test_report_gives_mode_owner_and_acl_of_each_ancestor(self) -> None:
        report = self.report()
        self.assertIn("/opt drwxrwxrwx 0:0", report)
        self.assertIn("/opt/duora drwxrwxr-x 0:1001", report)
        self.assertIn("/opt/duora/releases drwxr-xr-x 0:0", report)
        self.assertIn("system.posix_acl_default", report)

    def test_report_survives_a_missing_level(self) -> None:
        with patch.object(bench.os, "lstat", side_effect=FileNotFoundError):
            self.assertIn("/opt missing", bench.ancestry_report(Path("/opt/file")))


class RunnerReproductionTests(unittest.TestCase):
    """The executor's real reader over a simulated runner filesystem; the executor itself stays untouched."""

    def read(self, opt_mode: int) -> str:
        tree = {"opt": directory(opt_mode), "duora": directory(0o755), "releases": directory(0o755)}
        descriptors: dict[int, str] = {3: "/"}
        def fake_open(name, flags, mode=0o777, *, dir_fd=None):
            if name == "/":
                return 3
            if name not in tree:
                raise FileNotFoundError(name)
            descriptors[len(descriptors) + 3] = name
            return len(descriptors) + 2
        with patch.object(executor.os, "open", fake_open), patch.object(executor.os, "close"), \
                patch.object(executor.os, "fstat", lambda descriptor: tree[descriptors[descriptor]]):
            with self.assertRaises(executor.DeployError) as refused:
                executor._recovery_root_read(Path("/opt/duora/releases/release.json"))
        return str(refused.exception)

    def test_opt_as_the_runner_image_ships_it_reproduces_the_ci_refusal(self) -> None:
        self.assertEqual(self.read(0o777), "Recovery ancestry is not protected root state")

    def test_opt_after_the_proof_protects_it_passes_the_ancestry(self) -> None:
        self.assertNotIn("ancestry", self.read(0o777 & ~0o022))


class ProtectedReadAuditTests(SwapFixture):
    def lab_object(self) -> Any:
        lab_object = object.__new__(bench.SwapLab)
        lab_object.paths = self.paths
        return lab_object

    def patched(self):
        return patch.multiple(bench, PIN=self.pin, RELEASES=self.opt, INGRESS=self.ingress)

    def test_audit_names_the_exact_path_the_executor_would_refuse(self) -> None:
        def read(path):
            if path == self.opt / (self.release.release_id + ".json"):
                raise executor.DeployError("Recovery ancestry is not protected root state")
            return b""
        with self.patched(), patch.object(bench.executor, "_recovery_root_read", read), \
                self.assertRaisesRegex(bench.ProofFailure, str(self.opt)):
            self.lab_object().require_protected_reads(self.release)

    def test_refusal_carries_each_ancestor_in_the_exception_and_in_a_last_stderr_line(self) -> None:
        def read(_path):
            raise executor.DeployError("Recovery ancestry is not protected root state")
        stderr = io.StringIO()
        with self.patched(), patch.object(bench.executor, "_recovery_root_read", read), \
                patch.object(bench, "ancestry_report", return_value="/opt drwxrwxrwx 0:0"), \
                contextlib.redirect_stderr(stderr), self.assertRaises(bench.ProofFailure) as refused:
            self.lab_object().require_protected_reads(self.release)
        self.assertIn("/opt drwxrwxrwx 0:0", str(refused.exception))
        self.assertIn("/opt drwxrwxrwx 0:0", stderr.getvalue())

    def test_every_refused_path_is_named_not_only_the_first(self) -> None:
        refused_paths = {self.opt / (self.release.release_id + ".json"), self.paths.compose_file}
        def read(path):
            if path in refused_paths:
                raise executor.DeployError("refused")
            return b""
        with self.patched(), patch.object(bench.executor, "_recovery_root_read", read), contextlib.redirect_stderr(io.StringIO()), \
                self.assertRaises(bench.ProofFailure) as refused:
            self.lab_object().require_protected_reads(self.release)
        for path in refused_paths:
            self.assertIn(str(path), str(refused.exception))

    def test_matrix_and_unhealthy_are_audited_before_the_first_command(self) -> None:
        source = Path(bench.__file__).read_text()
        for start in ("    def matrix(self", "    def unhealthy(self"):
            body = source[source.index(start):]
            self.assertLess(body.index("self.require_protected_reads("), body.index("self.swap("))

    def test_audit_uses_the_executors_own_reader(self) -> None:
        seen = []
        with self.patched(), patch.object(bench.executor, "_recovery_root_read", side_effect=lambda path: seen.append(path) or b""):
            self.lab_object().require_protected_reads(self.release)
        self.assertIn(self.pin, seen)

    def test_audit_covers_every_path_the_real_plan_reads(self) -> None:
        reads: list[Path] = []
        def recording(path: Path) -> bytes:
            reads.append(path)
            return path.read_bytes()
        (self.state / "executor-install-receipt.json").write_text("{}")
        host = dataclasses.replace(self.host(), read_protected=recording)
        swap.postgres_swap_entry(self.release, swap_target, self.paths, self.docker, action="plan",
                                 reviewed_plan_sha256=None, host=host)
        self.assertTrue(reads)
        with self.patched():
            audited = bench.protected_reads(self.paths, self.release)
        self.assertEqual(set(reads) - set(audited), set())

    def test_audit_covers_locks_pin_and_receipt_the_entry_reads_before_the_plan(self) -> None:
        with self.patched():
            audited = bench.protected_reads(self.paths, self.release)
        for path in (self.ingress / "host.lock", self.ingress / "duora/release.lock", self.state / ".deploy.lock",
                     self.state / "executor-install-receipt.json", self.pin):
            self.assertIn(path, audited)

    def test_every_swap_through_the_lab_is_audited_first(self) -> None:
        source = Path(bench.__file__).read_text()
        body = source[source.index("    def sealed_swap"):source.index('plan = self.swap(release, target, "--preflight-only")\n        facts')]
        self.assertIn("self.require_protected_reads(release)", body)


class DriverEnvironmentTests(unittest.TestCase):
    def test_sudo_family_is_removed_only_from_the_driver_environment(self) -> None:
        original = {"SUDO_UID": "1001", "SUDO_GID": "1001", "SUDO_USER": "runner", "SUDO_COMMAND": "x", "PATH": "/bin", "HOME": "/root"}
        cleaned = bench.driver_environment(original)
        self.assertEqual(cleaned, {"PATH": "/bin", "HOME": "/root"})
        self.assertEqual(original["SUDO_UID"], "1001")

    def test_the_real_administrator_check_accepts_that_environment_and_still_refuses_sudo(self) -> None:
        with patch.object(executor.os, "getuid", return_value=0), patch.object(executor.os, "geteuid", return_value=0):
            with patch.dict(executor.os.environ, bench.driver_environment({"SUDO_UID": "1001", "PATH": "/bin"}), clear=True):
                executor._require_recovery_administrator()
            with patch.dict(executor.os.environ, {"SUDO_UID": "1001"}, clear=True), self.assertRaises(executor.DeployError):
                executor._require_recovery_administrator()

    def test_every_driver_call_uses_that_environment(self) -> None:
        lab_object = object.__new__(bench.SwapLab)
        lab_object.driver = lambda *a, **k: ["driver"]
        seen = []
        def run(command, **arguments):
            seen.append(arguments["env"])
            return subprocess.CompletedProcess(command, -9 if len(seen) > 1 else 0, "{}", "")
        release = executor.parse_manifest(json.dumps(manifest()), image_prefix="")
        with patch.object(bench.subprocess, "run", run), patch.dict(bench.os.environ, {"SUDO_UID": "1001"}):
            lab_object.swap(release, "target", "--resume")
            lab_object.killed(release, "target", "quiesce", "--resume")
        self.assertEqual(len(seen), 2)
        self.assertTrue(all("SUDO_UID" not in environment for environment in seen))


class SpecificRefusalTests(unittest.TestCase):
    def lab(self, error: Exception) -> tuple[Any, Any]:
        lab_object = object.__new__(bench.SwapLab)
        def deploy(_release):
            raise error
        lab_object.deploy = deploy
        return lab_object, executor.parse_manifest(json.dumps(manifest()), image_prefix="")

    def test_refused_deploy_needs_the_expected_reason(self) -> None:
        lab_object, release = self.lab(executor.DeployError("espaço em disco insuficiente"))
        with patch.object(bench, "docker", return_value=""), self.assertRaisesRegex(bench.ProofFailure, "espaço"):
            lab_object.refused_deploy(release, bench.REFUSED_BY_FENCE)

    def test_refused_deploy_accepts_the_expected_reason(self) -> None:
        lab_object, release = self.lab(executor.DeployError("PostgreSQL swap aberto (journal ou fence): resolva"))
        with patch.object(bench, "docker", return_value=""):
            lab_object.refused_deploy(release, bench.REFUSED_BY_FENCE)

    def digest_refusal(self, cause: Exception) -> Exception:
        """The chain of the real path: GuardError, re-raised by collect_runtime, wrapped by _collect_runtime."""
        try:
            try:
                try:
                    raise cause
                except ValueError as inner:
                    raise guard.GuardError("Cannot establish the reviewed healthy runtime") from inner
            except ValueError as outer:
                raise executor.DeployError(bench.REFUSED_BY_DIGEST) from outer
        except executor.DeployError as error:
            return error

    def test_digest_refusal_needs_the_digest_cause(self) -> None:
        lab_object, release = self.lab(self.digest_refusal(guard.GuardError(bench.REFUSED_DIGEST_CAUSE)))
        with patch.object(bench, "docker", return_value=""):
            lab_object.refused_deploy(release, bench.REFUSED_BY_DIGEST, cause=bench.REFUSED_DIGEST_CAUSE)

    def test_any_other_runtime_failure_under_the_same_message_fails_the_proof(self) -> None:
        for cause in (guard.GuardError("Runtime is not stably healthy"), guard.GuardError("Unexpected data volume mount"),
                      ValueError("SQL capture failed")):
            lab_object, release = self.lab(self.digest_refusal(cause))
            with self.subTest(cause=str(cause)), patch.object(bench, "docker", return_value=""), \
                    self.assertRaisesRegex(bench.ProofFailure, str(cause)):
                lab_object.refused_deploy(release, bench.REFUSED_BY_DIGEST, cause=bench.REFUSED_DIGEST_CAUSE)

    def test_digest_cause_is_the_guards_real_message(self) -> None:
        self.assertIn(bench.REFUSED_DIGEST_CAUSE, Path(guard.__file__).read_text())

    def test_digest_call_sites_all_demand_the_cause(self) -> None:
        source = Path(bench.__file__).read_text()
        self.assertEqual(len(re.findall(r"REFUSED_BY_DIGEST,\s+cause=REFUSED_DIGEST_CAUSE", source)), source.count("REFUSED_BY_DIGEST") - 1)

    def test_expected_reasons_are_the_messages_the_executor_really_emits(self) -> None:
        source = (Path(bench.__file__).parent / "postgres_swap.py").read_text()
        deployer = (Path(bench.__file__).parent / "duora_deploy.py").read_text()
        self.assertIn(bench.REFUSED_BY_FENCE, source)
        self.assertIn(bench.REFUSED_BY_COMPOSE, source)
        self.assertIn(bench.REFUSED_BY_DIGEST, source + deployer)
        self.assertIn(bench.REFUSED_BY_RESEAL, source)
        self.assertIn(bench.REFUSED_RESUME_OF_ABORT, source)


class KillPointTests(unittest.TestCase):
    def armed(self, phase: str, directory: Path) -> Any:
        journal = directory / "postgres-swap"
        journal.mkdir(exist_ok=True)
        (journal / "phase.json").write_text(json.dumps({"phase": phase}))
        class Swap:
            run_in_own_group = staticmethod(lambda command, **_: "ran")
            _swap_write_atomic = staticmethod(lambda *_a, **_k: None)
        driver.arm_kill(Swap, directory, phase)
        return Swap

    def outcome(self, phase: str, command: list[str]) -> str:
        with tempfile.TemporaryDirectory() as directory:
            armed = self.armed(phase, Path(directory))
            with patch.object(driver.subprocess, "Popen"), patch.object(driver.time, "sleep"), \
                    patch.object(driver.os, "kill", side_effect=KeyboardInterrupt):
                try:
                    return armed.run_in_own_group(command)
                except KeyboardInterrupt:
                    return "killed"

    def test_close_phases_die_inside_the_close_not_at_the_operator_check(self) -> None:
        for phase in ("awaiting-reseal", "aborted-awaiting-reseal"):
            self.assertEqual(self.outcome(phase, ["docker", "ps", "--all"]), "ran")
            self.assertEqual(self.outcome(phase, ["/usr/local/sbin/duora-backup", "verify-repo"]), "killed")

    def test_closing_dies_while_removing_its_holds(self) -> None:
        self.assertEqual(self.outcome("closing", ["docker", "ps", "--all"]), "ran")
        self.assertEqual(self.outcome("closing", ["docker", "container", "inspect", "--format", "x", "hold"]), "killed")

    def test_forward_phases_die_at_their_first_command(self) -> None:
        self.assertEqual(self.outcome("quiesce", ["docker", "compose", "stop", "worker"]), "killed")

    def test_matrix_crosses_the_close_phases(self) -> None:
        self.assertEqual(set(bench.CLOSE_KILL_STATES), {"awaiting-reseal", "closing", "aborted-awaiting-reseal"})


class OutageTests(unittest.TestCase):
    def test_outage_sums_the_unavailable_intervals_between_samples(self) -> None:
        samples = [(0.0, True), (1.0, False), (2.0, False), (3.0, True), (4.0, False), (5.0, True)]
        self.assertEqual(bench.outage_seconds(samples), 3.0)

    def test_no_failure_is_no_outage_and_a_final_failure_counts_until_the_end(self) -> None:
        self.assertEqual(bench.outage_seconds([(0.0, True), (1.0, True)]), 0.0)
        self.assertEqual(bench.outage_seconds([(0.0, True), (1.0, False), (2.5, False)]), 1.5)
        self.assertEqual(bench.outage_seconds([]), 0.0)


class MatrixTests(unittest.TestCase):
    def test_every_forward_phase_is_crossed_by_sigkill(self) -> None:
        self.assertEqual(bench.KILL_STATES, swap.SWAP_FORWARD_PHASES)
        self.assertTrue(set(bench.CLOSE_KILL_STATES) <= set(swap.SWAP_OTHER_PHASES))
        self.assertIn(bench.ABORT_ONLY_STATE, swap.SWAP_OTHER_PHASES)


class ComposeDoubleTests(unittest.TestCase):
    def test_s3_double_keeps_the_production_file_and_mounts_the_ca_only_in_backup(self) -> None:
        raw = (REPO / "infra/compose/compose.prod.yml").read_text()
        changed = lab._with_s3_double(raw, key="k", secret="s", certificates=Path("/certs"))
        self.assertEqual(changed.count("\n  s3:\n"), 1)
        self.assertEqual(changed.count("/certs/public.crt:/etc/pgbackrest/drill-ca.crt:ro"), 1)
        backup = changed[changed.index("\n  backup:\n"):changed.index("\n  restore:\n")]
        self.assertIn("drill-ca.crt", backup)
        self.assertEqual(changed.count("\nservices:\n"), 1)
        for line in raw.splitlines():
            self.assertIn(line, changed)

    def test_repository_config_is_the_double_never_a_real_bucket(self) -> None:
        config = bench.repository_config("key", "secret")
        self.assertIn("repo1-s3-endpoint=s3\n", config)
        self.assertIn("repo1-storage-ca-file=/etc/pgbackrest/drill-ca.crt", config)


class DrillStageTests(unittest.TestCase):
    def test_root_runs_directly_and_sudo_is_noninteractive(self) -> None:
        self.assertEqual(drill.swap_command(euid=0, can_sudo=False, required=True)[1:], [str(drill.SWAP_PROOF), "--mode", "pitr"])
        self.assertEqual(drill.swap_command(euid=1000, can_sudo=True, required=True)[:2], ["sudo", "--non-interactive"])

    def test_actions_cannot_skip_and_a_workstation_says_it_skipped(self) -> None:
        with self.assertRaises(drill.DrillFailure):
            drill.swap_command(euid=1000, can_sudo=False, required=True)
        self.assertIsNone(drill.swap_command(euid=1000, can_sudo=False, required=False))

    def test_root_on_a_host_that_is_not_disposable_never_reaches_the_bench(self) -> None:
        self.assertIsNone(drill.swap_command(euid=0, can_sudo=True, required=False, disposable=False))
        with self.assertRaises(drill.DrillFailure):
            drill.swap_command(euid=0, can_sudo=True, required=True, disposable=False)

    def test_sudo_keeps_the_variables_that_prove_the_host_is_disposable(self) -> None:
        command = drill.swap_command(euid=1000, can_sudo=True, required=True)
        self.assertIn("--preserve-env=GITHUB_ACTIONS,RUNNER_ENVIRONMENT,GITHUB_REPOSITORY", command)

    def test_timeout_publishes_the_log_stops_the_bench_and_cleans_what_is_left(self) -> None:
        cleanup = []
        class Slow:
            pid = 4321
            returncode = None
            def __init__(self, command, **arguments):
                self.log = arguments["stdout"]
                self.log.write(b"[swap-proof] stuck here\n")
                self.log.flush()
            def wait(self, timeout=None):
                raise subprocess.TimeoutExpired("bench", timeout or 0)
            def terminate(self): raise PermissionError(1, "Operation not permitted")
            def kill(self): raise PermissionError(1, "Operation not permitted")
        def run(command, **_):
            if "cleanup" in command: cleanup.append("cleanup")
            if command[:3] == ["sudo", "--non-interactive", "kill"]: cleanup.append(command[3])
            return subprocess.CompletedProcess(command, 0, "", "")
        output = io.StringIO()
        report = self.report()
        with patch.object(drill.subprocess, "Popen", Slow), patch.object(drill.subprocess, "run", run), \
                patch.object(drill, "_can_sudo", return_value=True), patch.object(drill, "_host_is_disposable", return_value=True), \
                contextlib.redirect_stdout(output):
            self.assertFalse(drill._swap_stage(report))
        self.assertIn("stuck here", output.getvalue())
        self.assertEqual(cleanup, ["-TERM", "-KILL", "cleanup"])

    def stop(self, command: list[str], *, signal_result: Any = 0) -> list[str]:
        calls = []
        class Process:
            pid = 4321
            def wait(self, timeout=None): return 0
            def terminate(self): calls.append("terminate")
            def kill(self): calls.append("kill")
        def run(arguments, **_):
            if "cleanup" in arguments:
                calls.append("cleanup")
            else:
                calls.append(arguments[3])
                if isinstance(signal_result, Exception): raise signal_result
            return subprocess.CompletedProcess(arguments, 0 if isinstance(signal_result, Exception) else signal_result, "", "")
        with patch.object(drill.subprocess, "run", run), contextlib.redirect_stdout(io.StringIO()):
            drill._stop_swap_proof(Process(), command)  # type: ignore[arg-type]
        return calls

    def test_direct_root_signals_its_own_child(self) -> None:
        self.assertEqual(self.stop(["python3", "bench", "--mode", "pitr"]), ["terminate", "cleanup"])

    def test_failed_sudo_kill_still_reaches_the_cleanup(self) -> None:
        for result in (1, subprocess.TimeoutExpired("sudo", 30), OSError("no sudo")):
            with self.subTest(result=str(result)):
                self.assertEqual(self.stop(["sudo", "--non-interactive", "python3", "bench", "--mode", "pitr"], signal_result=result)[-1],
                                 "cleanup")

    def test_failed_cleanup_does_not_lose_the_log(self) -> None:
        class Process:
            pid = 1
            def wait(self, timeout=None): return 0
            def terminate(self): pass
        with patch.object(drill.subprocess, "run", side_effect=subprocess.TimeoutExpired("cleanup", 180)), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            drill._stop_swap_proof(Process(), ["python3", "bench", "--mode", "pitr"])  # type: ignore[arg-type]
        self.assertIn("cleanup", output.getvalue())

    def report(self):
        return drill.DrillReport(image="image")

    def stage(self, result) -> tuple[bool, Any]:
        report = self.report()
        class Finished:
            pid = 1
            returncode = result.returncode
            def __init__(self, command, **arguments):
                arguments["stdout"].write((result.stdout + result.stderr).encode())
                arguments["stdout"].flush()
            def wait(self, timeout=None): return result.returncode
        with patch.object(drill.subprocess, "Popen", Finished), patch.object(drill, "_can_sudo", return_value=True), \
                patch.object(drill, "_host_is_disposable", return_value=True), patch.object(drill, "print", create=True):
            return drill._swap_stage(report), report

    def test_failure_or_missing_result_fails_the_drill(self) -> None:
        from subprocess import CompletedProcess
        self.assertFalse(self.stage(CompletedProcess([], 1, "SWAP_RESULT={}\n", "boom"))[0])
        self.assertFalse(self.stage(CompletedProcess([], 0, "no result\n", ""))[0])

    def test_success_is_recorded_in_the_report(self) -> None:
        from subprocess import CompletedProcess
        passed, report = self.stage(CompletedProcess([], 0, 'noise\nSWAP_RESULT={"timeline":2}\n', ""))
        self.assertTrue(passed)
        self.assertEqual(report.swap, {"timeline": 2})


class DriverTests(unittest.TestCase):
    def test_phase_reader_tolerates_absent_journal(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            self.assertIsNone(driver.current_phase(Path(directory)))
            journal = Path(directory) / "postgres-swap"
            journal.mkdir()
            (journal / "phase.json").write_text('{"phase":"quiesce"}')
            self.assertEqual(driver.current_phase(Path(directory)), "quiesce")

    def test_clock_is_inside_the_real_window(self) -> None:
        swap._require_swap_window(driver.WINDOW)


if __name__ == "__main__":
    unittest.main()
