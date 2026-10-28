"""Testes do comando de deploy. Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v"""

import contextlib
import io
import json
import platform
import re
import subprocess
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

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


def make_media_dir(root: Path, *, without_marker_in: str | None = None) -> Path:
    """Diretório de fotos como o role app_dirs o deixa: as duas áreas, cada uma com o marcador da montagem."""
    media_dir = root / "media"
    for area in ("quarantine", "derivatives"):
        (media_dir / area).mkdir(parents=True)
        if area != without_marker_in:
            (media_dir / area / ".duora-media-area").write_text("app_dirs\n", encoding="utf-8")
    return media_dir


class PreflightTests(unittest.TestCase):
    def setUp(self) -> None:
        manifest = valid_manifest()
        manifest["architecture"] = deploy.docker_architecture(platform.machine())
        self.release = parse(manifest)
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.host = deploy.HostConfig(image_prefix=PREFIX, min_free_gib=0, disk_check_path=Path("."),
                                      media_dir=make_media_dir(Path(self._tmp.name)))

    def test_matching_architecture_and_enough_disk_pass(self) -> None:
        deploy.preflight(self.release, self.host)

    def test_release_for_other_architecture_is_refused(self) -> None:
        manifest = valid_manifest()
        manifest["architecture"] = next(a for a in deploy.SUPPORTED_ARCHITECTURES if a != self.release.architecture)

        with self.assertRaisesRegex(deploy.DeployError, "host"):
            deploy.preflight(parse(manifest), self.host)


class MediaStoragePreflightTests(unittest.TestCase):
    """O Compose sempre configura o storage FileSystem, e o web o exige no boot mesmo com o upload desligado
    (adendo de 2026-10-19 do ADR-0025): sem as áreas montadas, o deploy recusa antes de baixar ou parar algo."""

    def setUp(self) -> None:
        manifest = valid_manifest()
        manifest["architecture"] = deploy.docker_architecture(platform.machine())
        self.release = parse(manifest)
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)

    def host(self, media_dir: Path | None) -> deploy.HostConfig:
        return deploy.HostConfig(image_prefix=PREFIX, min_free_gib=0, disk_check_path=Path("."), media_dir=media_dir)

    def test_host_config_reads_the_media_directory(self) -> None:
        config = deploy.parse_host_config("DUORA_IMAGE_PREFIX=x\nDUORA_MEDIA_DIR=/var/lib/duora-media\n")

        self.assertEqual(Path("/var/lib/duora-media"), config.media_dir)

    def test_both_areas_with_their_marker_pass(self) -> None:
        deploy.preflight(self.release, self.host(make_media_dir(self.root)))

    def test_missing_media_directory_setting_is_refused(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "DUORA_MEDIA_DIR ausente"):
            deploy.preflight(self.release, self.host(None))

    def test_relative_media_directory_is_refused(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "DUORA_MEDIA_DIR deve ser absoluto"):
            deploy.preflight(self.release, self.host(Path("media")))

    def test_area_without_marker_is_refused_naming_the_marker_and_the_fix(self) -> None:
        for area in ("quarantine", "derivatives"):
            with self.subTest(area=area), TemporaryDirectory() as tmp:
                media_dir = make_media_dir(Path(tmp), without_marker_in=area)
                with self.assertRaises(deploy.DeployError) as raised:
                    deploy.preflight(self.release, self.host(media_dir))
                message = str(raised.exception)
                self.assertIn(str(media_dir / area / ".duora-media-area"), message)
                self.assertIn("app_dirs", message)

    def test_marker_unreadable_for_lack_of_permission_asks_for_root(self) -> None:
        media_dir = make_media_dir(self.root)

        with patch.object(Path, "stat", side_effect=PermissionError(13, "Permission denied")):
            with self.assertRaisesRegex(deploy.DeployError, r"rode como root \(sudo\)"):
                deploy.require_media_areas(media_dir)

    def test_marker_that_is_not_a_regular_file_is_refused(self) -> None:
        media_dir = make_media_dir(self.root, without_marker_in="derivatives")
        (media_dir / "derivatives" / ".duora-media-area").mkdir()

        with self.assertRaisesRegex(deploy.DeployError, "sem o marcador"):
            deploy.require_media_areas(media_dir)

    def test_warning_about_running_without_root_comes_before_the_media_refusal(self) -> None:
        stdout = io.StringIO()

        with patch.object(deploy.os, "geteuid", return_value=1000), contextlib.redirect_stdout(stdout):
            with self.assertRaises(deploy.DeployError):
                deploy.preflight(self.release, self.host(None))

        self.assertIn("rodando sem root", stdout.getvalue())

    def test_missing_area_directory_is_refused(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "quarantine"):
            deploy.preflight(self.release, self.host(self.root / "nao-existe"))

    def test_marker_and_areas_match_the_store_the_web_boots_with(self) -> None:
        store = (Path(__file__).resolve().parents[2] / "backend" / "src" / "Duora.Infrastructure" / "Media"
                 / "FileSystemObjectStore.cs").read_text(encoding="utf-8")

        self.assertIn(f'AreaMarkerFileName = "{deploy.MEDIA_AREA_MARKER}"', store)
        self.assertIn("[" + ", ".join(f'"{area}"' for area in deploy.MEDIA_AREAS) + "]", store)

    def test_main_refuses_without_marker_before_running_any_command(self) -> None:
        manifest = self.root / "manifest.json"
        manifest.write_text(json.dumps(self.release.raw), encoding="utf-8")
        media_dir = make_media_dir(self.root, without_marker_in="derivatives")
        host_env = self.root / "duora.env"
        host_env.write_text(f"DUORA_IMAGE_PREFIX={PREFIX}\nDUORA_MIN_FREE_GIB=0\n"
                            f"DUORA_DISK_CHECK_PATH={self.root}\nDUORA_MEDIA_DIR={media_dir}\n", encoding="utf-8")
        runner = FakeRunner(architecture=self.release.architecture)
        stderr = io.StringIO()

        with contextlib.redirect_stderr(stderr), contextlib.redirect_stdout(io.StringIO()):
            original = deploy.run_command
            deploy.run_command = runner
            try:
                code = deploy.main(["--manifest", str(manifest), "--host-env", str(host_env),
                                    "--state-dir", str(self.root / "state"),
                                    "--compose-file", str(self.root / "compose.prod.yml")])
            finally:
                deploy.run_command = original

        self.assertEqual(1, code)
        self.assertIn(".duora-media-area", stderr.getvalue())
        self.assertEqual([], runner.commands)


class HostConfigTests(unittest.TestCase):
    def test_reads_key_value_lines_ignoring_comments_and_blanks(self) -> None:
        text = "# comentário\n\nDUORA_IMAGE_PREFIX=ghcr.io/x/duora-\nDUORA_MIN_FREE_GIB=7\n"

        config = deploy.parse_host_config(text)

        self.assertEqual("ghcr.io/x/duora-", config.image_prefix)
        self.assertEqual(7, config.min_free_gib)

    def test_missing_image_prefix_is_an_error(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "DUORA_IMAGE_PREFIX"):
            deploy.parse_host_config("DUORA_MIN_FREE_GIB=7\n")


class MediaUploadGateTests(unittest.TestCase):
    """Upload de fotos ligado só com o backup das fotos verificado (adendo do ADR-0010; I5 da revisão do #61)."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        root = Path(self._tmp.name)
        self.paths = deploy.Paths(compose_file=root / "compose.prod.yml", host_env=root / "duora.env",
                                  state_dir=root / "state", backup_command=root / "duora-backup")
        self.paths.state_dir.mkdir()
        self.release = parse(valid_manifest())
        self.now = 1_760_000_000.0

    def run_deploy(self, runner: "FakeRunner", *, enabled: bool = True) -> None:
        with patch.object(deploy, "runtime_recheck"), patch.object(deploy, "runtime_preflight", return_value=None):
            deploy.deploy(self.release, self.paths, runner, readiness_timeout_seconds=5, sleep=lambda _: None,
                          media_uploads_enabled=enabled, clock=lambda: self.now)

    def record_media_verify(self, age_seconds: float) -> None:
        (self.paths.state_dir / deploy.MEDIA_VERIFY_OK_FILE).write_text(f"{self.now - age_seconds}\n",
                                                                         encoding="utf-8")

    def test_host_env_switches_the_upload_on_with_a_policy_accepted_in_production(self) -> None:
        # "Editorial" é sintético: simula a política que ainda não existe entrando na allowlist.
        with patch.object(deploy, "PRODUCTION_MEDIA_REVIEW_POLICIES", frozenset({"Editorial"})), \
                patch.object(deploy, "PRODUCTION_DELETION_LEDGER_READY", True):
            self.assertTrue(deploy.parse_host_config("DUORA_IMAGE_PREFIX=x\nDUORA_MEDIA_REVIEW=Editorial\n")
                            .media_uploads_enabled)
        for text in ("DUORA_IMAGE_PREFIX=x\n", "DUORA_IMAGE_PREFIX=x\nDUORA_MEDIA_REVIEW=\n"):
            with self.subTest(text=text):
                self.assertFalse(deploy.parse_host_config(text).media_uploads_enabled)

    def test_human_review_is_the_production_policy_but_the_inventory_keeps_the_upload_off(self) -> None:
        # Decisão do dono de 2026-10-28: HumanReview é aceita em Production. Ligar o upload é da #69 (inventário).
        self.assertEqual(frozenset({"HumanReview"}), deploy.PRODUCTION_MEDIA_REVIEW_POLICIES)
        self.assertFalse(deploy.parse_host_config("DUORA_IMAGE_PREFIX=x\nDUORA_MEDIA_REVIEW=\n").media_uploads_enabled)
        with patch.object(deploy, "PRODUCTION_DELETION_LEDGER_READY", True):
            self.assertTrue(deploy.parse_host_config("DUORA_IMAGE_PREFIX=x\nDUORA_MEDIA_REVIEW=HumanReview\n")
                            .media_uploads_enabled)

    def test_an_accepted_policy_is_refused_while_the_deletion_ledger_does_not_exist(self) -> None:
        # Sem o ledger externo de exclusões em B2 (#69), um restore desfaria exclusões de fotos reais: falha fechada.
        self.assertFalse(deploy.PRODUCTION_DELETION_LEDGER_READY)
        with self.assertRaisesRegex(deploy.DeployError, "ledger .*#69"):
            deploy.parse_host_config("DUORA_IMAGE_PREFIX=x\nDUORA_MEDIA_REVIEW=HumanReview\n")

    def test_review_outside_the_production_allowlist_is_refused(self) -> None:
        # TechnicalOnly existe no backend, mas o boot de Production a recusa: aceitá-la aqui derrubaria o web
        # depois da migração. Nomes fora da allowlist, inclusive com outra caixa, são recusados.
        for value in ("TechnicalOnly", "technicalonly", "Editorial", "humanreview"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(deploy.DeployError, f"DUORA_MEDIA_REVIEW={value} .*Production"):
                    deploy.parse_host_config(f"DUORA_IMAGE_PREFIX=x\nDUORA_MEDIA_REVIEW={value}\n")

    def test_production_allowlist_names_only_backend_policies_and_never_technical_only(self) -> None:
        source = (Path(__file__).resolve().parents[2] / "backend" / "src" / "Duora.Core" / "Media"
                  / "MediaReview.cs").read_text(encoding="utf-8")
        body = re.search(r"public enum MediaReviewPolicy\s*\{(?P<body>.*?)\}", source, re.DOTALL)
        assert body is not None
        policies = set(re.findall(r"^\s*([A-Z][A-Za-z0-9]*),", body["body"], re.MULTILINE))

        self.assertIn("TechnicalOnly", policies)
        self.assertNotIn("TechnicalOnly", deploy.PRODUCTION_MEDIA_REVIEW_POLICIES)
        self.assertLessEqual(deploy.PRODUCTION_MEDIA_REVIEW_POLICIES, policies)

    def test_upload_off_does_not_look_at_the_media_backup(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner, enabled=False)

        self.assertFalse(runner.ran("systemctl"))

    def test_upload_on_with_active_timer_and_recent_verify_deploys_after_checking_first(self) -> None:
        self.record_media_verify(deploy.MAX_MEDIA_VERIFY_AGE_HOURS * 3600)
        runner = FakeRunner()

        self.run_deploy(runner)

        self.assertLess(runner.index_of(f"systemctl is-active --quiet {deploy.MEDIA_VERIFY_TIMER}"),
                        runner.index_of("pull"))
        self.assertTrue(runner.ran("up --detach --wait --no-deps web"))

    def test_upload_on_refuses_before_touching_anything(self) -> None:
        cases = {
            "timer inativo": (FakeRunner(fail_on="is-active"), 3600),
            "verify velho": (FakeRunner(), deploy.MAX_MEDIA_VERIFY_AGE_HOURS * 3600 + 1),
            "verify no futuro": (FakeRunner(), -3600),
        }
        for name, (runner, age) in cases.items():
            with self.subTest(case=name):
                self.record_media_verify(age)
                with self.assertRaisesRegex(deploy.DeployError, "fotos"):
                    self.run_deploy(runner)
                self.assertFalse(runner.ran("pull"))
                self.assertFalse(runner.ran("stop"))

    def test_upload_on_without_or_with_a_corrupt_marker_refuses(self) -> None:
        for content in (None, "ontem", "", "nan", "inf", "-inf"):
            with self.subTest(content=content):
                marker = self.paths.state_dir / deploy.MEDIA_VERIFY_OK_FILE
                marker.unlink(missing_ok=True)
                if content is not None:
                    marker.write_text(content, encoding="utf-8")
                runner = FakeRunner()
                with self.assertRaisesRegex(deploy.DeployError, "fotos"):
                    self.run_deploy(runner)
                self.assertFalse(runner.ran("pull"))

    def test_skipping_database_backup_does_not_skip_media_gate(self) -> None:
        runner = FakeRunner()
        with self.assertRaisesRegex(deploy.DeployError, "fotos"):
            deploy.deploy(self.release, self.paths, runner, verify_backup=False,
                          media_uploads_enabled=True, clock=lambda: self.now)
        self.assertFalse(runner.ran("pull"))

    def test_names_match_the_backup_wrapper_and_the_role(self) -> None:
        import duora_backup
        timer = (Path(__file__).resolve().parents[2] / "infra" / "ansible" / "roles" / "backup" / "files"
                 / deploy.MEDIA_VERIFY_TIMER)

        self.assertEqual(duora_backup.MEDIA_VERIFY_OK_FILE, deploy.MEDIA_VERIFY_OK_FILE)
        self.assertTrue(timer.is_file())
        self.assertGreater(deploy.MAX_MEDIA_VERIFY_AGE_HOURS, duora_backup.MAX_MEDIA_SNAPSHOT_AGE_HOURS)


class FakeRunner:
    """Registra os comandos e responde como o Docker responderia; falha no comando indicado.

    O banco começa em pre_head (None = sem histórico, banco vazio) e, depois da primeira leitura, responde
    migration_head, como se o migrador tivesse rodado.
    """

    def __init__(self, architecture: str = "arm64", fail_on: str | None = None,
                 pre_head: str | None = None, migration_head: str = "20261015175752_Baseline",
                 ready_after: int = 1,
                 service_states: str = "web=running\nworker=running\nedge=running\n",
                 privileged_without_mfa: str = "0") -> None:
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
        self._privileged_without_mfa = privileged_without_mfa

    def __call__(self, command: list[str], *, capture: bool = False,
                 input_text: str | None = None) -> subprocess.CompletedProcess[str]:
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
        if "not u.two_factor_enabled" in joined:
            return subprocess.CompletedProcess(command, 0, self._privileged_without_mfa + "\n", "")
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
            backup_command=root / "duora-backup",
        )
        self.release = parse(valid_manifest())

    def run_deploy(self, runner: FakeRunner, **overrides) -> None:
        options = {"readiness_timeout_seconds": 5, "sleep": lambda _: None}
        options.update(overrides)
        # Legacy pipeline fixtures isolate policy; dedicated collector/contract/native tests exercise the real guard.
        with patch.object(deploy, "runtime_recheck"), patch.object(deploy, "runtime_preflight", return_value=None):
            deploy.deploy(self.release, self.paths, runner, **options)

    def test_happy_path_runs_steps_in_order(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner)

        order = [runner.index_of(step) for step in [
            "pull",
            "to_regclass",
            "stop worker",
            "run --rm --no-deps migrator",
            "reconcile-privileges.sql",
            "up --detach --wait --no-deps web worker edge",
            "/health/ready",
        ]]
        self.assertEqual(sorted(order), order)

    def test_backup_is_verified_with_postgres_up_before_anything_stops(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner)

        verify = runner.index_of("duora-backup verify")
        self.assertEqual([str(self.paths.backup_command), "verify"], runner.commands[verify])
        self.assertFalse(runner.ran("up --detach --wait postgres backup"))
        self.assertLess(verify, runner.index_of("stop worker"))

    def test_readiness_ignores_foreign_web_alias_on_shared_ingress_network(self) -> None:
        # Another application's web alias returns 401 on the shared ingress network.
        # The private Duora backend is healthy; reaching the foreign alias must not
        # prevent the real deployment from recording its completed release.
        runner = FakeRunner(fail_on="http://web:8080/health/ready")

        self.run_deploy(runner)

        current = json.loads((self.paths.state_dir / "current.json").read_text())
        self.assertEqual(self.release.release_id, current["releaseId"])

    def test_unhealthy_backup_stops_deploy_before_stopping_anything(self) -> None:
        runner = FakeRunner(fail_on="duora-backup verify")

        with self.assertRaisesRegex(deploy.DeployError, "backup"):
            self.run_deploy(runner)
        self.assertFalse(runner.ran("stop worker"))
        self.assertFalse(runner.ran("run --rm --no-deps migrator"))
        self.assertFalse((self.paths.state_dir / "current.json").exists())

    def test_missing_backup_command_stops_deploy_before_stopping_anything(self) -> None:
        fake = FakeRunner()

        def runner(command: list[str], *, capture: bool = False, input_text: str | None = None) -> subprocess.CompletedProcess[str]:
            if command[0].endswith("duora-backup"):
                raise FileNotFoundError(command[0])
            return fake(command, capture=capture)

        with patch.object(deploy, "runtime_recheck"), patch.object(deploy, "runtime_preflight", return_value=None), self.assertRaises(FileNotFoundError):
            deploy.deploy(self.release, self.paths, runner, readiness_timeout_seconds=5, sleep=lambda _: None)
        self.assertFalse(fake.ran("stop worker"))

    def test_main_reports_failure_when_backup_command_cannot_run(self) -> None:
        manifest = self.paths.state_dir.parent / "manifest.json"
        release = valid_manifest()
        release["architecture"] = deploy.docker_architecture(platform.machine())
        manifest.write_text(json.dumps(release), encoding="utf-8")
        media_dir = make_media_dir(self.paths.state_dir.parent)
        self.paths.host_env.write_text(f"DUORA_IMAGE_PREFIX={PREFIX}\nDUORA_MIN_FREE_GIB=0\n"
                                       f"DUORA_DISK_CHECK_PATH={self.paths.state_dir.parent}\n"
                                       f"DUORA_MEDIA_DIR={media_dir}\n", encoding="utf-8")

        def failing_command(command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
            raise FileNotFoundError(command[0])

        with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
            original = deploy.run_command
            deploy.run_command = failing_command
            try:
                code = deploy.main(["--manifest", str(manifest), "--host-env", str(self.paths.host_env),
                                    "--state-dir", str(self.paths.state_dir),
                                    "--compose-file", str(self.paths.compose_file)])
            finally:
                deploy.run_command = original

        self.assertEqual(1, code)

    def test_backup_check_skipped_only_when_explicitly_accepted(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner, verify_backup=False)

        self.assertFalse(runner.ran("duora-backup"))
        self.assertTrue(runner.ran("stop worker"))

    def test_rollback_also_requires_healthy_backup(self) -> None:
        runner = FakeRunner(fail_on="duora-backup verify", pre_head="20261020000000_Newer")

        with self.assertRaisesRegex(deploy.DeployError, "backup"):
            self.run_deploy(runner)

    def test_compose_receives_host_and_release_env_files(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner)

        pull = runner.commands[runner.index_of("pull")]
        self.assertIn(str(self.paths.host_env), pull)
        self.assertIn(str(self.paths.state_dir / "staging" / "2026.10.15-1.env"), pull)
        recorded_env = self.paths.state_dir / "releases" / "2026.10.15-1.env"
        self.assertIn(f"DUORA_WEB_IMAGE={PREFIX}web@{DIGEST}", recorded_env.read_text().splitlines())

    def test_marks_release_as_current_after_success(self) -> None:
        self.run_deploy(FakeRunner())

        current = json.loads((self.paths.state_dir / "current.json").read_text())
        self.assertEqual("2026.10.15-1", current["releaseId"])

    def test_pulled_image_with_wrong_architecture_stops_before_migration(self) -> None:
        runner = FakeRunner(architecture="amd64")

        with self.assertRaisesRegex(deploy.DeployError, "arquitetura"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("run --rm --no-deps migrator"))

    def test_failed_migration_does_not_start_new_version(self) -> None:
        runner = FakeRunner(fail_on="run --rm --no-deps migrator")

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

    def test_readiness_uses_private_duora_alias(self) -> None:
        runner = FakeRunner()

        self.run_deploy(runner)

        probes = [c for c in runner.commands if "/health/ready" in " ".join(c)]
        self.assertEqual(1, len(probes))
        self.assertEqual("http://duora-web:8080/health/ready", probes[0][-1])

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
        runner = FakeRunner(fail_on="run --rm --no-deps migrator")

        with self.assertRaises(deploy.DeployError):
            self.run_deploy(runner)

        current = json.loads((self.paths.state_dir / "current.json").read_text())
        self.assertEqual("anterior", current["releaseId"])

    def test_forward_deploy_on_database_at_older_migration_runs_migrator(self) -> None:
        runner = FakeRunner(pre_head="20261001000000_Older")

        self.run_deploy(runner)

        self.assertTrue(runner.ran("run --rm --no-deps migrator"))

    def test_eligible_rollback_skips_migrator_and_keeps_newer_schema(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.15-1")
        runner = FakeRunner(pre_head="20261020000000_Newer", migration_head="20261020000000_Newer")

        self.run_deploy(runner)

        self.assertFalse(runner.ran("run --rm --no-deps migrator"))
        self.assertTrue(runner.ran("up --detach --wait --no-deps web worker edge"))
        current = json.loads((self.paths.state_dir / "current.json").read_text())
        self.assertEqual("2026.10.15-1", current["releaseId"])

    def test_rollback_across_the_bootstrap_with_privileged_account_without_mfa_is_refused_before_stopping_anything(
            self) -> None:
        # A release anterior ao bootstrap não tem o I5: o reset por e-mail tomaria a conta sem segundo fator.
        self.write_current("2026.10.16-1", previous="2026.10.15-1")
        runner = FakeRunner(pre_head="20261020000000_Newer", privileged_without_mfa="2")

        with self.assertRaisesRegex(deploy.DeployError, "sem MFA"):
            self.run_deploy(runner)

        self.assertTrue(runner.ran("not u.two_factor_enabled"))
        self.assertFalse(runner.ran("stop worker"))
        self.assertFalse(runner.ran("up --detach --wait --no-deps"))

    def test_rollback_across_the_bootstrap_checks_privileged_accounts_and_proceeds_without_any(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.15-1")
        runner = FakeRunner(pre_head="20261020000000_Newer", privileged_without_mfa="0")

        self.run_deploy(runner)

        self.assertTrue(runner.ran("not u.two_factor_enabled"))
        self.assertTrue(runner.ran("up --detach --wait --no-deps web worker edge"))

    def test_forward_deploy_does_not_check_privileged_accounts(self) -> None:
        runner = FakeRunner(pre_head="20261001000000_Older", privileged_without_mfa="2")

        self.run_deploy(runner)

        self.assertFalse(runner.ran("not u.two_factor_enabled"))

    def test_bootstrap_migration_named_by_the_rollback_guard_exists(self) -> None:
        migrations = (Path(__file__).resolve().parents[2] / "backend" / "src" / "Duora.Infrastructure" / "Persistence"
                      / "Migrations")

        self.assertTrue((migrations / f"{deploy.ADMIN_BOOTSTRAP_MIGRATION}.cs").is_file())

    def test_rollback_to_release_not_declared_previous_is_refused_before_stopping_anything(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.14-9")
        runner = FakeRunner(pre_head="20261020000000_Newer")

        with self.assertRaisesRegex(deploy.DeployError, "elegível"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("stop worker"))
        self.assertFalse(runner.ran("run --rm --no-deps migrator"))
        self.assertFalse(runner.ran("up --detach --wait --no-deps"))

    def test_database_ahead_without_current_release_is_refused_before_stopping_anything(self) -> None:
        runner = FakeRunner(pre_head="20261020000000_Newer")

        with self.assertRaisesRegex(deploy.DeployError, "elegível"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("stop worker"))

    def write_current(self, release_id: str, *, previous: str | None,
                      schema_max: str = "20261020000000_Newer") -> None:
        """Estado como o próprio script grava depois de dois deploys bem-sucedidos: a release anterior (a deste
        teste, self.release) e a corrente, que declara a anterior como previousReleaseId."""
        releases = self.paths.state_dir / "releases"
        releases.mkdir(parents=True, exist_ok=True)
        (releases / f"{self.release.release_id}.json").write_text(
            json.dumps(self.release.raw, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        manifest = valid_manifest()
        manifest["releaseId"] = release_id
        manifest["previousReleaseId"] = previous
        manifest["migrations"] = ["20261015175752_Baseline", schema_max]
        manifest["schema"]["max"] = schema_max
        (releases / f"{release_id}.json").write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n",
                                                    encoding="utf-8")
        (self.paths.state_dir / "current.json").write_text(json.dumps({"releaseId": release_id}), encoding="utf-8")

    # --- Adversariais: o manifesto do CI não pode mexer na elegibilidade nem no estado ---------------------------

    def test_manifest_claiming_eligible_id_with_other_images_is_refused(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.15-1")
        manifest = valid_manifest()
        manifest["images"]["web"] = f"{PREFIX}web@sha256:{'b' * 64}"
        self.release = parse(manifest)
        runner = FakeRunner(pre_head="20261020000000_Newer")

        # O id elegível já está registrado com outras imagens: releases são imutáveis.
        with self.assertRaisesRegex(deploy.DeployError, "já registrada"):
            self.run_deploy(runner)

        self.assertEqual([], runner.commands)

    def test_failed_deploy_cannot_poison_eligibility_for_a_later_rollback(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.15-1")
        # 1o envio: reusa o id da release corrente, declara um "anterior" falso e falha antes do migrador.
        poison = valid_manifest()
        poison["releaseId"] = "2026.10.16-1"
        poison["previousReleaseId"] = "2026.10.01-evil"
        self.release = parse(poison)
        with self.assertRaises(deploy.DeployError):
            self.run_deploy(FakeRunner(architecture="amd64"))
        # 2o envio: a release "anterior" falsa, com o banco à frente.
        evil = valid_manifest()
        evil["releaseId"] = "2026.10.01-evil"
        self.release = parse(evil)
        runner = FakeRunner(pre_head="20261020000000_Newer")

        with self.assertRaisesRegex(deploy.DeployError, "elegível"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("stop worker"))

    def test_release_id_already_recorded_with_other_content_is_refused_before_anything(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.15-1")
        reused = valid_manifest()
        reused["releaseId"] = "2026.10.16-1"
        self.release = parse(reused)
        runner = FakeRunner(pre_head="20261020000000_Newer")

        with self.assertRaisesRegex(deploy.DeployError, "já registrada"):
            self.run_deploy(runner)

        self.assertEqual([], runner.commands)

    def test_rollback_to_declared_previous_that_never_ran_here_is_refused(self) -> None:
        self.write_current("2026.10.16-1", previous="2026.10.15-1")
        (self.paths.state_dir / "releases" / "2026.10.15-1.json").unlink()
        runner = FakeRunner(pre_head="20261020000000_Newer")

        with self.assertRaisesRegex(deploy.DeployError, "nunca foi implantada"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("stop worker"))

    def test_failed_deploy_records_nothing(self) -> None:
        runner = FakeRunner(fail_on="run --rm --no-deps migrator")

        with self.assertRaises(deploy.DeployError):
            self.run_deploy(runner)

        self.assertFalse((self.paths.state_dir / "releases" / "2026.10.15-1.json").exists())
        self.assertFalse((self.paths.state_dir / "releases" / "2026.10.15-1.env").exists())
        self.assertFalse((self.paths.state_dir / "current.json").exists())

    def test_successful_deploy_records_release_for_manual_operation(self) -> None:
        self.run_deploy(FakeRunner())

        recorded = json.loads((self.paths.state_dir / "releases" / "2026.10.15-1.json").read_text())
        self.assertEqual(self.release.raw, recorded)
        self.assertTrue((self.paths.state_dir / "releases" / "2026.10.15-1.env").exists())

    def test_rollback_with_database_beyond_current_release_schema_is_refused(self) -> None:
        # Teto: o banco não pode estar além do schema.max da release corrente registrada.
        self.write_current("2026.10.16-1", previous="2026.10.15-1", schema_max="20261020000000_Newer")
        runner = FakeRunner(pre_head="20261101000000_Unknown")

        with self.assertRaisesRegex(deploy.DeployError, "20261101000000_Unknown"):
            self.run_deploy(runner)

        self.assertFalse(runner.ran("stop worker"))


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
