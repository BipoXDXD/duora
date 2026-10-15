"""Testes do comando de deploy. Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v"""

import contextlib
import io
import json
import platform
import subprocess
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import duora_deploy as deploy

PREFIX = "ghcr.io/bipoxdxd/duora-"
DIGEST = "sha256:" + "a" * 64


def valid_manifest() -> dict:
    return {
        "schemaVersion": 1,
        "releaseId": "2026.10.15-1",
        "commit": "8d4ab45" + "0" * 33,
        "createdAt": "2026-10-15T18:00:00Z",
        "architecture": "arm64",
        "images": {name: f"{PREFIX}{name}@{DIGEST}" for name in deploy.IMAGE_NAMES},
        "contractVersion": "v1",
        "schema": {"min": "20261015175752_Baseline", "max": "20261015175752_Baseline"},
        "migrations": ["20261015175752_Baseline"],
        "sbom": {name: f"{PREFIX}{name}-sbom@{DIGEST}" for name in deploy.IMAGE_NAMES},
        "gates": {"status": "passed", "runUrl": "https://github.com/BipoXDXD/duora/actions/runs/1"},
        "previousReleaseId": None,
    }


def parse(manifest: dict, prefix: str = PREFIX) -> deploy.Release:
    return deploy.parse_manifest(json.dumps(manifest), image_prefix=prefix)


class ParseManifestTests(unittest.TestCase):
    def test_valid_manifest_becomes_release(self) -> None:
        release = parse(valid_manifest())

        self.assertEqual("2026.10.15-1", release.release_id)
        self.assertEqual("arm64", release.architecture)
        self.assertEqual(f"{PREFIX}web@{DIGEST}", release.images["web"])
        self.assertEqual("20261015175752_Baseline", release.latest_migration)
        self.assertIsNone(release.previous_release_id)

    def test_rejects_invalid_json(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "JSON"):
            deploy.parse_manifest("{not json", image_prefix=PREFIX)

    def test_rejects_unknown_field(self) -> None:
        manifest = valid_manifest()
        manifest["skipGates"] = True

        with self.assertRaisesRegex(deploy.DeployError, "skipGates"):
            parse(manifest)

    def test_rejects_missing_field(self) -> None:
        manifest = valid_manifest()
        del manifest["commit"]

        with self.assertRaisesRegex(deploy.DeployError, "commit"):
            parse(manifest)

    def test_rejects_unsupported_schema_version(self) -> None:
        manifest = valid_manifest()
        manifest["schemaVersion"] = 2

        with self.assertRaisesRegex(deploy.DeployError, "schemaVersion"):
            parse(manifest)

    def test_rejects_release_id_with_path_traversal(self) -> None:
        for release_id in ["../etc", "a/b", "", ".hidden", "x" * 65]:
            with self.subTest(release_id=release_id):
                manifest = valid_manifest()
                manifest["releaseId"] = release_id

                with self.assertRaisesRegex(deploy.DeployError, "releaseId"):
                    parse(manifest)

    def test_rejects_commit_that_is_not_full_sha(self) -> None:
        for commit in ["8d4ab45", "Z" * 40, "a" * 41]:
            with self.subTest(commit=commit):
                manifest = valid_manifest()
                manifest["commit"] = commit

                with self.assertRaisesRegex(deploy.DeployError, "commit"):
                    parse(manifest)

    def test_rejects_image_referenced_by_tag(self) -> None:
        manifest = valid_manifest()
        manifest["images"]["web"] = f"{PREFIX}web:latest"

        with self.assertRaisesRegex(deploy.DeployError, "images.web"):
            parse(manifest)

    def test_rejects_image_with_tag_and_digest(self) -> None:
        manifest = valid_manifest()
        manifest["images"]["web"] = f"{PREFIX}web:1.0@{DIGEST}"

        with self.assertRaisesRegex(deploy.DeployError, "images.web"):
            parse(manifest)

    def test_rejects_malformed_digest(self) -> None:
        for digest in ["sha256:" + "a" * 63, "sha256:" + "A" * 64, "sha512:" + "a" * 64]:
            with self.subTest(digest=digest):
                manifest = valid_manifest()
                manifest["images"]["worker"] = f"{PREFIX}worker@{digest}"

                with self.assertRaisesRegex(deploy.DeployError, "images.worker"):
                    parse(manifest)

    def test_rejects_image_outside_allowed_registry_prefix(self) -> None:
        manifest = valid_manifest()
        manifest["images"]["edge"] = f"docker.io/attacker/duora-edge@{DIGEST}"

        with self.assertRaisesRegex(deploy.DeployError, "images.edge"):
            parse(manifest)

    def test_rejects_missing_image(self) -> None:
        manifest = valid_manifest()
        del manifest["images"]["migrator"]

        with self.assertRaisesRegex(deploy.DeployError, "migrator"):
            parse(manifest)

    def test_rejects_unknown_architecture(self) -> None:
        manifest = valid_manifest()
        manifest["architecture"] = "riscv64"

        with self.assertRaisesRegex(deploy.DeployError, "architecture"):
            parse(manifest)

    def test_rejects_release_whose_gates_did_not_pass(self) -> None:
        manifest = valid_manifest()
        manifest["gates"]["status"] = "failed"

        with self.assertRaisesRegex(deploy.DeployError, "gates"):
            parse(manifest)

    def test_rejects_schema_max_different_from_last_migration(self) -> None:
        manifest = valid_manifest()
        manifest["migrations"] = ["20261015175752_Baseline", "20261101000000_Rooms"]

        with self.assertRaisesRegex(deploy.DeployError, "schema.max"):
            parse(manifest)

    def test_rejects_migrations_out_of_order(self) -> None:
        manifest = valid_manifest()
        manifest["migrations"] = ["20261101000000_Rooms", "20261015175752_Baseline"]
        manifest["schema"]["max"] = "20261015175752_Baseline"

        with self.assertRaisesRegex(deploy.DeployError, "migrations"):
            parse(manifest)

    def test_rejects_previous_release_equal_to_current(self) -> None:
        manifest = valid_manifest()
        manifest["previousReleaseId"] = manifest["releaseId"]

        with self.assertRaisesRegex(deploy.DeployError, "previousReleaseId"):
            parse(manifest)


class HostChecksTests(unittest.TestCase):
    def test_host_architecture_maps_kernel_names(self) -> None:
        self.assertEqual("amd64", deploy.docker_architecture("x86_64"))
        self.assertEqual("arm64", deploy.docker_architecture("aarch64"))
        self.assertEqual("arm64", deploy.docker_architecture("arm64"))

    def test_unknown_host_architecture_is_an_error(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "s390x"):
            deploy.docker_architecture("s390x")

    def test_disk_check_fails_below_minimum(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "disco"):
            deploy.require_free_space(free_bytes=4 * deploy.GIB, minimum_gib=5)

    def test_disk_check_passes_at_minimum(self) -> None:
        deploy.require_free_space(free_bytes=5 * deploy.GIB, minimum_gib=5)


class PreflightTests(unittest.TestCase):
    def setUp(self) -> None:
        manifest = valid_manifest()
        manifest["architecture"] = deploy.docker_architecture(platform.machine())
        self.release = parse(manifest)
        self.host = deploy.HostConfig(image_prefix=PREFIX, min_free_gib=0, disk_check_path=Path("."))

    def test_production_does_not_proceed_without_backup_check(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "backup"):
            deploy.preflight(self.release, self.host, accept_missing_backup_check=False)

    def test_explicit_acceptance_lets_staging_proceed_without_backup_check(self) -> None:
        deploy.preflight(self.release, self.host, accept_missing_backup_check=True)

    def test_release_for_other_architecture_is_refused(self) -> None:
        manifest = valid_manifest()
        manifest["architecture"] = next(a for a in deploy.SUPPORTED_ARCHITECTURES if a != self.release.architecture)

        with self.assertRaisesRegex(deploy.DeployError, "host"):
            deploy.preflight(parse(manifest), self.host, accept_missing_backup_check=True)


class HostConfigTests(unittest.TestCase):
    def test_reads_key_value_lines_ignoring_comments_and_blanks(self) -> None:
        text = "# comentário\n\nDUORA_IMAGE_PREFIX=ghcr.io/x/duora-\nDUORA_MIN_FREE_GIB=7\n"

        config = deploy.parse_host_config(text)

        self.assertEqual("ghcr.io/x/duora-", config.image_prefix)
        self.assertEqual(7, config.min_free_gib)

    def test_missing_image_prefix_is_an_error(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "DUORA_IMAGE_PREFIX"):
            deploy.parse_host_config("DUORA_MIN_FREE_GIB=7\n")


class FakeRunner:
    """Registra os comandos e responde como o Docker responderia; falha no comando indicado.

    O banco começa em pre_head (None = sem histórico, banco vazio) e, depois da primeira leitura, responde
    migration_head, como se o migrador tivesse rodado.
    """

    def __init__(self, architecture: str = "arm64", fail_on: str | None = None,
                 pre_head: str | None = None, migration_head: str = "20261015175752_Baseline",
                 ready_after: int = 1,
                 service_states: str = "web=running\nworker=running\nedge=running\n") -> None:
        self.commands: list[list[str]] = []
        self.captured: list[bool] = []
        self._architecture = architecture
        self._fail_on = fail_on
        self._pre_head = pre_head
        self._migration_head = migration_head
        self._head_reads = 0
        self._ready_after = ready_after
        self._readiness_calls = 0
        self._service_states = service_states

    def __call__(self, command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
        self.commands.append(command)
        self.captured.append(capture)
        joined = " ".join(command)
        if self._fail_on is not None and self._fail_on in joined:
            return subprocess.CompletedProcess(command, 1, "", "falha simulada")
        if command[:3] == ["docker", "image", "inspect"]:
            return subprocess.CompletedProcess(command, 0, self._architecture + "\n", "")
        if "to_regclass" in joined:
            self._head_reads += 1
            exists = self._head_reads > 1 or self._pre_head is not None
            return subprocess.CompletedProcess(command, 0, "t\n" if exists else "f\n", "")
        if 'max("MigrationId")' in joined:
            head = self._pre_head if self._head_reads == 1 else self._migration_head
            return subprocess.CompletedProcess(command, 0, (head or "") + "\n", "")
        if "ps" in command and "--format" in command:
            return subprocess.CompletedProcess(command, 0, self._service_states, "")
        if "/health/ready" in joined:
            self._readiness_calls += 1
            code = 0 if self._readiness_calls >= self._ready_after else 1
            return subprocess.CompletedProcess(command, code, "", "")
        return subprocess.CompletedProcess(command, 0, "", "")

    def index_of(self, fragment: str) -> int:
        for index, command in enumerate(self.commands):
            if fragment in " ".join(command):
                return index
        raise AssertionError(f"comando com '{fragment}' não executado: {self.commands}")

    def ran(self, fragment: str) -> bool:
        return any(fragment in " ".join(command) for command in self.commands)


class DeployTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        root = Path(self._tmp.name)
        self.paths = deploy.Paths(
            compose_file=root / "compose.prod.yml",
            host_env=root / "duora.env",
            state_dir=root / "releases",
        )
        self.release = parse(valid_manifest())

    def run_deploy(self, runner: FakeRunner, **overrides) -> None:
        options = {"readiness_timeout_seconds": 5, "sleep": lambda _: None}
        options.update(overrides)
        deploy.deploy(self.release, self.paths, runner, **options)

    def test_happy_path_runs_steps_in_order(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner)

        order = [runner.index_of(step) for step in [
            "pull",
            "up --detach --wait postgres",
            "to_regclass",
            "stop worker",
            "run --rm migrator",
            "reconcile-privileges.sql",
            "up --detach --wait --no-deps web worker edge",
            "/health/ready",
        ]]
        self.assertEqual(sorted(order), order)

    def test_compose_receives_host_and_release_env_files(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner)

        pull = runner.commands[runner.index_of("pull")]
        release_env = self.paths.state_dir / "releases" / "2026.10.15-1.env"
        self.assertIn(str(self.paths.host_env), pull)
        self.assertIn(str(release_env), pull)
        self.assertIn(f"DUORA_WEB_IMAGE={PREFIX}web@{DIGEST}", release_env.read_text().splitlines())

    def test_marks_release_as_current_after_success(self) -> None:
        self.run_deploy(FakeRunner())

        current = json.loads((self.paths.state_dir / "current.json").read_text())
        self.assertEqual("2026.10.15-1", current["releaseId"])

    def test_pulled_image_with_wrong_architecture_stops_before_migration(self) -> None:
        runner = FakeRunner(architecture="amd64")

        with self.assertRaisesRegex(deploy.DeployError, "arquitetura"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("run --rm migrator"))

    def test_failed_migration_does_not_start_new_version(self) -> None:
        runner = FakeRunner(fail_on="run --rm migrator")

        with self.assertRaisesRegex(deploy.DeployError, "migrator"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("up --detach --wait --no-deps web worker edge"))
        self.assertFalse((self.paths.state_dir / "current.json").exists())

    def test_failed_privilege_reconciliation_does_not_start_new_version(self) -> None:
        runner = FakeRunner(fail_on="reconcile-privileges.sql")

        with self.assertRaisesRegex(deploy.DeployError, "privilégios"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("up --detach --wait --no-deps web worker edge"))

    def test_database_head_different_from_manifest_stops_deploy(self) -> None:
        runner = FakeRunner(migration_head="20250101000000_Old")

        with self.assertRaisesRegex(deploy.DeployError, "20250101000000_Old"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("up --detach --wait --no-deps web worker edge"))

    def test_service_in_crash_loop_fails_deploy(self) -> None:
        runner = FakeRunner(service_states="web=running\nworker=running\nedge=restarting\n")

        with self.assertRaisesRegex(deploy.DeployError, "edge"):
            self.run_deploy(runner)

        self.assertFalse((self.paths.state_dir / "current.json").exists())

    def test_readiness_is_retried_until_it_passes(self) -> None:
        runner = FakeRunner(ready_after=3)

        self.run_deploy(runner)

        self.assertEqual(3, sum("/health/ready" in " ".join(c) for c in runner.commands))

    def test_readiness_timeout_fails_and_keeps_previous_release_current(self) -> None:
        self.paths.state_dir.mkdir(parents=True)
        (self.paths.state_dir / "current.json").write_text('{"releaseId": "anterior"}')
        runner = FakeRunner(ready_after=10_000)

        with self.assertRaisesRegex(deploy.DeployError, "health/ready"):
            self.run_deploy(runner, readiness_timeout_seconds=0)

        current = json.loads((self.paths.state_dir / "current.json").read_text())
        self.assertEqual("anterior", current["releaseId"])


    def test_only_commands_whose_output_is_read_are_captured(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner)

        for command, captured in zip(runner.commands, runner.captured):
            reads_output = (command[:3] == ["docker", "image", "inspect"]
                            or ("psql" in command and "--command" in command)
                            or ("ps" in command and "--format" in command))
            with self.subTest(command=" ".join(command)):
                self.assertEqual(reads_output, captured)

    def test_release_named_like_state_file_does_not_overwrite_current_state(self) -> None:
        self.write_current("anterior", previous=None)
        manifest = valid_manifest()
        manifest["releaseId"] = "current"
        self.release = parse(manifest)
        runner = FakeRunner(fail_on="run --rm migrator")

        with self.assertRaises(deploy.DeployError):
            self.run_deploy(runner)

        current = json.loads((self.paths.state_dir / "current.json").read_text())
        self.assertEqual("anterior", current["releaseId"])

    def test_forward_deploy_on_database_at_older_migration_runs_migrator(self) -> None:
        runner = FakeRunner(pre_head="20261001000000_Older")

        self.run_deploy(runner)

        self.assertTrue(runner.ran("run --rm migrator"))

    def test_eligible_rollback_skips_migrator_and_keeps_newer_schema(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.15-1")
        runner = FakeRunner(pre_head="20261020000000_Newer", migration_head="20261020000000_Newer")

        self.run_deploy(runner)

        self.assertFalse(runner.ran("run --rm migrator"))
        self.assertTrue(runner.ran("up --detach --wait --no-deps web worker edge"))
        current = json.loads((self.paths.state_dir / "current.json").read_text())
        self.assertEqual("2026.10.15-1", current["releaseId"])

    def test_rollback_to_release_not_declared_previous_is_refused_before_stopping_anything(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.14-9")
        runner = FakeRunner(pre_head="20261020000000_Newer")

        with self.assertRaisesRegex(deploy.DeployError, "elegível"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("stop worker"))
        self.assertFalse(runner.ran("run --rm migrator"))
        self.assertFalse(runner.ran("up --detach --wait --no-deps"))

    def test_database_ahead_without_current_release_is_refused_before_stopping_anything(self) -> None:
        runner = FakeRunner(pre_head="20261020000000_Newer")

        with self.assertRaisesRegex(deploy.DeployError, "elegível"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("stop worker"))

    def write_current(self, release_id: str, *, previous: str | None) -> None:
        releases = self.paths.state_dir / "releases"
        releases.mkdir(parents=True, exist_ok=True)
        manifest = valid_manifest()
        manifest["releaseId"] = release_id
        manifest["previousReleaseId"] = previous
        (releases / f"{release_id}.json").write_text(json.dumps(manifest), encoding="utf-8")
        (self.paths.state_dir / "current.json").write_text(json.dumps({"releaseId": release_id}), encoding="utf-8")


class ManifestInputTests(unittest.TestCase):
    def test_manifest_with_invalid_utf8_is_a_deploy_error(self) -> None:
        with TemporaryDirectory() as tmp:
            path = Path(tmp) / "manifest.json"
            path.write_bytes(b'{"releaseId": "\xff\xfe"}')

            with self.assertRaisesRegex(deploy.DeployError, "UTF-8"):
                deploy.read_manifest(str(path))


class DeployLockTests(unittest.TestCase):
    def start_holder(self, state_dir: str) -> subprocess.Popen[str]:
        holder = subprocess.Popen(
            [sys.executable, "-c", HOLD_LOCK_SCRIPT, str(Path(deploy.__file__).parent), state_dir],
            stdout=subprocess.PIPE, text=True)
        self.addCleanup(holder.wait)
        self.addCleanup(holder.kill)
        assert holder.stdout is not None
        self.assertEqual("locked", holder.stdout.readline().strip())
        return holder

    def test_second_process_cannot_take_the_lock(self) -> None:
        with TemporaryDirectory() as tmp:
            holder = self.start_holder(tmp)

            with self.assertRaisesRegex(deploy.DeployError, "outro deploy"):
                with deploy.deploy_lock(Path(tmp)):
                    pass

            holder.kill()
            holder.wait()
            with deploy.deploy_lock(Path(tmp)):
                pass

    def test_main_refuses_while_another_deploy_holds_the_lock(self) -> None:
        with TemporaryDirectory() as tmp:
            self.start_holder(tmp)
            errors = io.StringIO()

            with contextlib.redirect_stderr(errors):
                code = deploy.main(["--manifest", "-", "--state-dir", tmp, "--host-env", str(Path(tmp) / "x")])

            self.assertEqual(1, code)
            self.assertIn("outro deploy", errors.getvalue())


HOLD_LOCK_SCRIPT = """
import sys, time
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import duora_deploy
with duora_deploy.deploy_lock(Path(sys.argv[2])):
    print("locked", flush=True)
    time.sleep(60)
"""


if __name__ == "__main__":
    unittest.main()
