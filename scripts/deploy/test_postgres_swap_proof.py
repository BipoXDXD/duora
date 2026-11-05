"""Pure parts of the real-Docker swap proof; the proof itself runs in postgres_swap_bench.py and continuity_lab.py."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
from typing import Any
import unittest
from unittest.mock import patch

import continuity_lab as lab
import duora_deploy as executor
import postgres_swap as swap
import postgres_swap_bench as bench
import postgres_swap_driver as driver
import runtime_guard as guard
from test_postgres_swap import source_manifest as manifest

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
        lock = json.loads(bench.LOCK.read_text())
        self.assertEqual(fixture["origin"], lock["images"])
        for architecture in ("amd64", "arm64"):
            self.assertNotEqual(fixture["origin"][architecture], fixture["images"][architecture])

    def test_a_lock_at_the_target_never_reaches_the_identical_digest_refusal(self) -> None:
        fixture = json.loads(bench.FIXTURE.read_text())
        for architecture in ("amd64", "arm64"):
            self.assertEqual(bench.alternative_digest(fixture["images"][architecture], architecture),
                             fixture["origin"][architecture])

    def test_bench_main_has_no_skip_left(self) -> None:
        self.assertNotIn("skipped", Path(bench.__file__).read_text())


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

    def test_cleanup_needs_a_claim_and_a_disposable_host(self) -> None:
        files = self.production_files()
        self.mark_disposable()
        with patch.object(bench, "docker", self.docker):
            self.assertFalse(bench.cleanup_claimed())
        self.assertTrue(all(file.exists() for file in files))


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
                raise subprocess.TimeoutExpired("bench", timeout)
            def terminate(self): cleanup.append("terminate")
            def kill(self): cleanup.append("kill")
        def run(command, **_):
            if "cleanup" in command: cleanup.append("cleanup")
            return subprocess.CompletedProcess(command, 0, "", "")
        output = io.StringIO()
        report = self.report()
        with patch.object(drill.subprocess, "Popen", Slow), patch.object(drill.subprocess, "run", run), \
                patch.object(drill, "_can_sudo", return_value=True), patch.object(drill, "_host_is_disposable", return_value=True), \
                contextlib.redirect_stdout(output):
            self.assertFalse(drill._swap_stage(report))
        self.assertIn("stuck here", output.getvalue())
        self.assertEqual(cleanup, ["terminate", "kill", "cleanup"])

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
