"""Projeto sombra duora-c2 (issue #86): env, configurações, dados sintéticos, overlay e restore com dublês."""
from __future__ import annotations

import base64
import dataclasses
import hashlib
import io
import json
import os
import re
import shutil
import signal
import subprocess
import tempfile
import time
import unittest
from contextlib import redirect_stderr, redirect_stdout
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from unittest import mock

import c2_probe
import c2_shadow as shadow

CANARY = "CANARY-6a1f-production-secret"
DIGEST = "@sha256:" + "c" * 64
RELEASE_ENV = "\n".join([f"{key}=registry.example/{key.lower()}{DIGEST}" for key in shadow.IMAGE_KEYS]
                        + [f"DUORA_RESEND_API_KEY={CANARY}", f"POSTGRES_PASSWORD={CANARY}", "# comentário"]) + "\n"


class ReleaseEnvTests(unittest.TestCase):
    def test_only_image_digests_are_read(self) -> None:
        images = shadow.read_release_images(RELEASE_ENV)
        self.assertEqual(set(images), set(shadow.IMAGE_KEYS))
        self.assertNotIn(CANARY, json.dumps(images))

    def test_missing_image_or_tag_without_digest_is_refused(self) -> None:
        without_web = "\n".join(line for line in RELEASE_ENV.splitlines() if not line.startswith("DUORA_WEB_IMAGE"))
        with self.assertRaises(shadow.ShadowError):
            shadow.read_release_images(without_web)
        with self.assertRaises(shadow.ShadowError):
            shadow.read_release_images(RELEASE_ENV.replace(DIGEST, ":latest", 1))

    def test_env_publishes_only_on_loopback_and_never_leaks_release_secrets(self) -> None:
        layout = shadow.Layout(Path("/var/lib/duora-c2"))
        env = shadow.render_env(layout, shadow.read_release_images(RELEASE_ENV), "c2key", "c2secret")
        values = dict(line.split("=", 1) for line in env.splitlines())
        self.assertEqual(values["DUORA_HTTPS_PORT"], "127.0.0.1:18443")
        self.assertEqual(values["DUORA_HTTP_PORT"], "127.0.0.1:18080")
        self.assertEqual(values["DUORA_TLS"], "internal")
        self.assertEqual(values["DUORA_DOMAIN"], "c2.duora.test")
        self.assertEqual(values["DUORA_SECRETS_DIR"], "/var/lib/duora-c2/secrets")
        self.assertNotIn(CANARY, env)


class ConfTests(unittest.TestCase):
    def test_image_confs_get_only_the_backup_ip_replaced(self) -> None:
        for source in (shadow.HBA_SOURCE, shadow.REPO_CONF_SOURCE):
            with self.subTest(source=source.name):
                original = source.read_text(encoding="utf-8")
                converted = shadow.shadow_conf(original)
                self.assertNotIn(shadow.PRODUCTION_BACKUP_IP, converted)
                self.assertEqual(converted.replace(shadow.SHADOW_BACKUP_IP, shadow.PRODUCTION_BACKUP_IP), original)

    def test_unexpected_number_of_ips_is_refused(self) -> None:
        with self.assertRaises(shadow.ShadowError):
            shadow.shadow_conf("nada aqui")
        with self.assertRaises(shadow.ShadowError):
            shadow.shadow_conf(f"{shadow.PRODUCTION_BACKUP_IP} {shadow.PRODUCTION_BACKUP_IP}")

    def test_overlay_and_compose_overlay_ip_agree(self) -> None:
        overlay = shadow.OVERLAY_FILE.read_text(encoding="utf-8")
        self.assertIn(f"ipv4_address: {shadow.SHADOW_BACKUP_IP}", overlay)
        for subnet in shadow.SHADOW_SUBNETS:
            self.assertIn(f"subnet: {subnet}", overlay)

    def test_production_project_is_refused(self) -> None:
        with self.assertRaises(c2_probe.CommandRefused):
            shadow.compose_command(shadow.Layout(Path("/tmp/x")), project="duora")


@unittest.skipUnless(shutil.which("docker"), "docker CLI ausente: render do overlay não conferido")
class OverlayRenderTests(unittest.TestCase):
    """`docker compose config` não precisa de daemon: confere o merge real (incluindo !override)."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.directory = tempfile.TemporaryDirectory()
        root = Path(cls.directory.name)
        layout = shadow.Layout(root)
        for path in (layout.secrets, layout.certs, layout.conf, layout.media / "quarantine",
                     layout.media / "derivatives"):
            path.mkdir(parents=True)
        layout.env_file.write_text(shadow.render_env(layout, shadow.read_release_images(RELEASE_ENV), "k", "s"))
        result = subprocess.run([*shadow.compose_command(layout), "--profile", "restore", "--profile", "migrate",
                                 "config", "--format", "json"], capture_output=True, text=True, timeout=60,
                                check=False)
        if result.returncode != 0:
            raise AssertionError(result.stderr)
        cls.config = json.loads(result.stdout)
        production = subprocess.run(
            ["docker", "compose", "--file", str(shadow.COMPOSE_FILE), "--env-file", str(layout.env_file),
             "--profile", "restore", "--profile", "migrate", "config", "--format", "json"],
            capture_output=True, text=True, timeout=60, check=False)
        if production.returncode != 0:
            raise AssertionError(production.stderr)
        cls.production = json.loads(production.stdout)

    @classmethod
    def tearDownClass(cls) -> None:
        cls.directory.cleanup()

    def test_networks_use_shadow_subnets_only(self) -> None:
        subnets = {name: [c["subnet"] for c in (network.get("ipam") or {}).get("config", [])]
                   for name, network in self.config["networks"].items()}
        self.assertEqual(subnets["edge"], ["10.231.10.0/24"])
        self.assertEqual(subnets["pgbackrest"], ["10.231.21.0/24"])
        self.assertEqual(subnets["backup-egress"], ["10.231.20.0/24"])
        self.assertEqual(self.config["name"], "duora-c2")
        backup = self.config["services"]["backup"]["networks"]["pgbackrest"]
        self.assertEqual(backup["ipv4_address"], "10.231.21.10")

    def test_edge_is_published_on_loopback_only(self) -> None:
        ports = self.config["services"]["edge"]["ports"]
        self.assertTrue(ports)
        self.assertEqual({port["host_ip"] for port in ports}, {"127.0.0.1"})
        for name, service in self.config["services"].items():
            if name != "edge":
                self.assertFalse(service.get("ports"), name)

    def test_resource_limits_and_hardening_match_production(self) -> None:
        for name, service in self.production["services"].items():
            shadow_service = self.config["services"][name]
            with self.subTest(service=name):
                self.assertEqual(shadow_service.get("deploy"), service.get("deploy"))
                self.assertEqual(shadow_service.get("cpu_shares"), service.get("cpu_shares"))
                self.assertEqual(shadow_service.get("read_only"), service.get("read_only"))
                self.assertEqual(shadow_service.get("cap_drop"), service.get("cap_drop"))
                self.assertEqual(shadow_service.get("image"), service.get("image"))

    def test_shadow_confs_are_mounted_read_only(self) -> None:
        def mounts(service: str) -> dict[str, bool]:
            return {v["target"]: v.get("read_only", False) for v in self.config["services"][service]["volumes"]}

        self.assertTrue(mounts("postgres")["/etc/postgresql/pg_hba.duora.conf"])
        self.assertTrue(mounts("backup")["/etc/pgbackrest/pgbackrest-repo.conf"])
        self.assertTrue(mounts("restore")["/etc/pgbackrest/pgbackrest-repo.conf"])
        environment = self.config["services"]["web"]["environment"]
        self.assertEqual(environment["ReverseProxy__KnownNetworks__0"], "10.231.10.0/24")


class SeedTests(unittest.TestCase):
    NOW = datetime(2026, 10, 28, 12, tzinfo=UTC)

    def test_identity_v3_hash_layout(self) -> None:
        salt = bytes(range(16))
        decoded = base64.b64decode(shadow.identity_password_hash("frase sintética", salt))
        self.assertEqual(len(decoded), 61)
        self.assertEqual(decoded[0], 1)
        self.assertEqual(int.from_bytes(decoded[1:5], "big"), 2)  # HMACSHA512
        self.assertEqual(int.from_bytes(decoded[5:9], "big"), 100_000)
        self.assertEqual(int.from_bytes(decoded[9:13], "big"), 16)
        self.assertEqual(decoded[13:29], salt)
        expected = hashlib.pbkdf2_hmac("sha512", "frase sintética".encode(), salt, 100_000, dklen=32)
        self.assertEqual(decoded[29:], expected)
        with self.assertRaises(ValueError):
            shadow.identity_password_hash("x", b"short")

    def test_seed_is_one_transaction_with_synthetic_accounts_and_future_events(self) -> None:
        sql, emails = shadow.seed_sql(3, self.NOW, "HASH'Q", events=2, interval_minutes=15)
        lines = sql.splitlines()
        self.assertEqual(lines[:2], ["\\set ON_ERROR_STOP on", "begin;"])
        self.assertEqual(lines[-1], "commit;")
        self.assertEqual(emails, ["p0001@c2.invalid", "p0002@c2.invalid", "p0003@c2.invalid"])
        self.assertEqual(sql.count("insert into identity.users("), 3)
        self.assertEqual(sql.count("'Passed'"), 3)
        self.assertEqual(sql.count("insert into events.events("), 2)
        self.assertEqual(sql.count("'build-round'"), 2)
        self.assertEqual(sql.count("'close-round'"), 2)
        self.assertIn("'2026-10-28T12:15:00+00:00'", sql)  # primeiro evento em 15 min
        self.assertIn("'2026-10-28T12:30:00+00:00'", sql)  # intervalo de 15 min
        self.assertIn("'HASH''Q'", sql)  # aspas escapadas
        self.assertIn("create table if not exists c2.markers", sql)
        self.assertNotIn("@example.com", sql)

    def test_seed_requires_accounts_and_events(self) -> None:
        for accounts, events in ((0, 1), (1, 0)):
            with self.subTest(accounts=accounts, events=events), self.assertRaises(ValueError):
                shadow.seed_sql(accounts, self.NOW, "h", events=events, interval_minutes=15)


class FakeRunner:
    def __init__(self, responses: dict[str, list[str]] | None = None) -> None:
        self.commands: list[list[str]] = []
        self.responses = responses or {}

    def __call__(self, argv, *, stdin=None, check=True, timeout=0) -> str:
        self.commands.append(list(argv))
        for marker, outputs in self.responses.items():
            if marker in " ".join(argv) and outputs:
                return outputs.pop(0) if len(outputs) > 1 else outputs[0]
        return ""


class RestoreRehearsalTests(unittest.TestCase):
    def test_measures_rpo_and_rto_after_host_loss(self) -> None:
        now = [1000.0]

        def clock() -> float:
            return now[0]

        def sleep(seconds: float) -> None:
            now[0] += seconds

        acks = [str(1000.0 + i) for i in range(200)]
        runner = FakeRunner({"returning extract": acks, "pg_is_in_recovery": ["t", "f"],
                             "max(at)": ["1030.0"]})
        with tempfile.TemporaryDirectory() as directory:
            layout = shadow.Layout(Path(directory))
            layout.state.mkdir()
            result = shadow.restore_rehearsal(layout, runner, clock, sleep)
            saved = json.loads((layout.state / "restore-rehearsal.json").read_text())
        self.assertEqual(result["last_ack_at"], 1089.0)
        self.assertEqual(result["restored_max_at"], 1030.0)
        self.assertEqual(result["rpo_seconds"], 59.0)
        self.assertEqual(result["outage_at"], 1090.0)
        self.assertEqual(saved, result)
        joined = [" ".join(c) for c in runner.commands]
        kill = next(i for i, c in enumerate(joined) if c.startswith("docker kill duora-c2-postgres-1"))
        volume = next(i for i, c in enumerate(joined) if c.startswith("docker volume rm duora-c2_pgdata"))
        restore = next(i for i, c in enumerate(joined) if "--profile restore run" in c)
        self.assertLess(kill, volume)
        self.assertLess(volume, restore)
        self.assertTrue(joined[-1].endswith("full --compose-project duora-c2 --state-dir "
                                            f"{layout.state} --skip-egress-check"))
        self.assertFalse(any(" duora_pgdata" in c or "--project-name duora " in c for c in joined))


class RehearsalSqlTests(unittest.TestCase):
    def test_every_psql_value_read_suppresses_the_command_tag(self) -> None:
        # Sem --quiet, "insert ... returning" imprime "INSERT 0 1" depois do valor e o float() quebra.
        now = [1000.0]

        def sleep(seconds: float) -> None:
            now[0] += seconds

        runner = FakeRunner({"returning extract": ["1000.0"], "pg_is_in_recovery": ["f"], "max(at)": ["1000.0"]})
        with tempfile.TemporaryDirectory() as directory:
            layout = shadow.Layout(Path(directory))
            layout.state.mkdir()
            shadow.restore_rehearsal(layout, runner, lambda: now[0], sleep, final_full=False)
        reads = [c for c in runner.commands if "psql" in c]
        self.assertTrue(reads)
        self.assertTrue(all("--quiet" in c for c in reads))


class RestoreWithoutFinalFullTests(unittest.TestCase):
    def test_window_skips_the_new_full_when_the_shadow_is_torn_down_next(self) -> None:
        now = [1000.0]

        def sleep(seconds: float) -> None:
            now[0] += seconds

        runner = FakeRunner({"returning extract": [str(1000.0 + i) for i in range(200)],
                             "pg_is_in_recovery": ["f"], "max(at)": ["1060.0"]})
        with tempfile.TemporaryDirectory() as directory:
            layout = shadow.Layout(Path(directory))
            layout.state.mkdir()
            result = shadow.restore_rehearsal(layout, runner, lambda: now[0], sleep, final_full=False)
        joined = [" ".join(c) for c in runner.commands]
        self.assertFalse(any("duora_backup.py full" in c for c in joined))
        self.assertIn("up --detach --wait web worker edge", joined[-1])
        self.assertEqual(result["rpo_seconds"], 29.0)

    def test_cli_flag_reaches_the_rehearsal(self) -> None:
        calls: list[bool] = []
        original = shadow.restore_rehearsal
        shadow.restore_rehearsal = lambda layout, **kwargs: calls.append(kwargs["final_full"]) or {}  # type: ignore[assignment]
        try:
            with tempfile.TemporaryDirectory() as directory, mock.patch("os.geteuid", return_value=0), redirect_stdout(io.StringIO()):
                shadow.Layout(Path(directory)).state.mkdir()
                self.assertEqual(shadow.main(["--root", directory, "restore-rehearsal", "--no-final-full"]), 0)
                self.assertEqual(shadow.main(["--root", directory, "restore-rehearsal"]), 0)
        finally:
            shadow.restore_rehearsal = original
        self.assertEqual(calls, [False, True])


class PreflightAndDownTests(unittest.TestCase):
    def test_clashing_subnet_is_refused(self) -> None:
        runner = FakeRunner({"network ls": ["abc\n"], "network inspect": ["172.30.10.0/24 10.231.21.0/24 \n"]})
        with self.assertRaises(shadow.ShadowError):
            shadow.preflight(runner)
        free = FakeRunner({"network ls": ["abc\n"], "network inspect": ["172.30.10.0/24 \n"]})
        shadow.preflight(free)

    def test_down_only_removes_shadow_roots(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            odd = Path(directory) / "elsewhere"
            odd.mkdir()
            with self.assertRaises(shadow.ShadowError):
                shadow.down(shadow.Layout(odd), FakeRunner())
            self.assertTrue(odd.exists())
            root = Path(directory) / "duora-c2-test"
            root.mkdir()
            runner = FakeRunner()
            shadow.down(shadow.Layout(root), runner)
            self.assertFalse(root.exists())
            self.assertIn("--volumes", runner.commands[0])

    def test_prepare_refuses_existing_root(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "leftover").write_text("x")
            release = root / "release.env"
            release.write_text(RELEASE_ENV)
            with self.assertRaises(shadow.ShadowError):
                shadow.prepare(shadow.Layout(root), release, FakeRunner())

    def test_restic_command_uses_its_own_slice_with_media_limits(self) -> None:
        command = shadow.restic_backup_command(shadow.Layout(Path("/var/lib/duora-c2")))
        self.assertIn("--slice=duora-c2-media.slice", command)
        self.assertNotIn("--slice=duora-media.slice", command)
        for limit in ("MemoryMax=1024M", "MemoryHigh=768M", "MemorySwapMax=0", "CPUQuota=50%"):
            self.assertIn(limit, command)
        self.assertIn("duora-c2", command)


class ShadowSecretsTests(unittest.TestCase):
    def test_every_selfie_secret_file_of_the_production_compose_is_synthesized(self) -> None:
        compose = (Path(__file__).resolve().parents[2] / "infra/compose/compose.prod.yml").read_text(encoding="utf-8")
        mounted = set(re.findall(r"\}/(selfie-[a-z0-9-]+)", compose))
        self.assertTrue(mounted)
        self.assertEqual(mounted, set(shadow.SYNTHETIC_SELFIE_SECRETS))


SOURCE, TARGET = shadow.SWAP_SOURCE_IMAGE, shadow.SWAP_TARGET_IMAGE
HEALTHY = shadow.SwapResources(available_mib=lambda: 4000, pressure=lambda: 1.0, disk_mib=lambda _path: (100_000, 60_000),
                               device=lambda _path: 7)
PHOTO = {"systemIdentifier": "7000", "catalogVersion": 202506291, "controlVersion": 1800, "timeline": 1,
         "head": "20261105120000_RegistrationReconciliationManifest", "acl": "d41d8",
         "collation": {"provider": "c", "collate": "en_US.utf8", "ctype": "en_US.utf8", "version": "2.41"}}


class SwapDocker:
    """Runner double: answers like Docker for the shadow, tracks the digest and advances a clock per command."""

    def __init__(self) -> None:
        self.commands: list[str] = []
        self.image = SOURCE
        self.now = 1000.0
        self.fail_on: str | None = None
        self.photo_after_swap: dict | None = None
        self.config_project = "duora-c2"
        self.config_volume = "duora-c2_pgdata"
        self.config_root = "/ROOT"
        self.config_image = SOURCE
        self.config_port_ip = "127.0.0.1"
        self.config_bind: str | None = None
        self.config_secret: str | None = None
        self.running_images: tuple[str, str] | None = None
        self.ballast = "0:"
        self.updates: list[str] = []
        self.archived = "000000010000000000000005"
        self.volume_created = "2026-11-06T00:00:00Z"
        self.compatibility = {"serverVersionNum": "180006", "controlVersion": 1800, "catalogVersion": 202506291, "encoding": "UTF8",
                              "provider": "c", "collate": "C.UTF-8", "ctype": "C.UTF-8", "locale": None, "recorded": "2.41", "actual": "2.41"}
        self.compatibility_after_swap: dict | None = None

    def clock(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.now += seconds

    def __call__(self, argv, *, stdin=None, check=True, timeout=0) -> str:
        joined = " ".join(argv)
        self.commands.append(joined)
        self.now += 1
        if self.fail_on and self.fail_on in joined:
            raise shadow.ShadowError("falha simulada: " + self.fail_on)
        if "config --format json" in joined:
            root = self.config_root
            bind = self.config_bind or root + "/conf/pg_hba.conf"
            return json.dumps({"name": self.config_project, "volumes": {"pgdata": {"name": self.config_volume}},
                "services": {"postgres": {"image": self.config_image, "volumes": [{"type": "bind", "source": bind}, {"type": "volume", "source": "pgdata"}]},
                             "backup": {"image": self.config_image, "volumes": [{"type": "bind", "source": root + "/certs/public.crt"}]},
                             "edge": {"image": "edge", "ports": [{"host_ip": self.config_port_ip, "published": "18443", "target": 443}]}},
                "secrets": {"db": {"file": self.config_secret or root + "/secrets/db"}}})
        if argv[:3] == ["docker", "image", "inspect"]:
            return "sha256:" + argv[-1][-64:] + "\n"
        if argv[:2] == ["docker", "update"]:
            self.updates.append(joined)
            return ""
        if argv[:2] == ["docker", "info"]:
            return "/var/lib/docker\n"
        if argv[:2] == ["docker", "inspect"]:
            if "HostConfig" in joined:
                return "1500000000 2048\n"
            images = self.running_images or (self.image, self.image)
            return "".join(f"{item} sha256:{item[-64:]}\n" for item in images)
        if argv[:3] == ["docker", "volume", "inspect"]:
            return self.volume_created + "\n"
        if " up " in joined and "--no-deps postgres" in joined:
            self.image = dict(line.split("=", 1) for line in Path(argv[argv.index("--env-file", argv.index("--env-file") + 1) + 1]).read_text().splitlines())["DUORA_POSTGRES_IMAGE"]
        if "'systemIdentifier'" in joined:
            changed = self.photo_after_swap is not None and self.image == TARGET
            return json.dumps(self.photo_after_swap if changed else PHOTO) + "\n"
        if "'serverVersionNum'" in joined:
            changed = self.compatibility_after_swap is not None and self.image == TARGET
            return json.dumps(self.compatibility_after_swap if changed else self.compatibility) + "\n"
        if "swap_ballast order by id" in joined:
            return self.ballast + "\n"
        if "pg_switch_wal" in joined:
            return "000000010000000000000005\n"
        if "last_archived_wal" in joined:
            return self.archived + "\n"
        if "pg_database_size" in joined:
            return "1100\n"
        return ""


class SwapRehearsalFixture(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.layout = shadow.Layout(Path(directory.name) / "duora-c2")
        self.layout.state.mkdir(parents=True)
        self.layout.env_file.write_text("DUORA_POSTGRES_IMAGE=" + SOURCE + "\nDUORA_WEB_IMAGE=web@sha256:" + "c" * 64 + "\n")
        self.docker = SwapDocker()
        self.docker.config_root = str(self.layout.root.resolve())

    def rehearse(self, *, target: str = TARGET, fill_mib: int = 0, resources: Any = HEALTHY, shield: Any = None) -> dict:
        return shadow.swap_rehearsal(self.layout, target, fill_mib=fill_mib, run=self.docker, clock=self.docker.clock,
                                     sleep=self.docker.sleep, resources=resources, shield=shield or (lambda: None))

    def position(self, text: str, start: int = 0) -> int:
        return next(i for i in range(start, len(self.docker.commands)) if text in self.docker.commands[i])

    def stops(self) -> list[str]:
        return [c for c in self.docker.commands if " stop " in c or " up " in c]


class SwapRehearsalTests(SwapRehearsalFixture):
    def test_swaps_to_the_target_and_back_with_the_same_photo(self) -> None:
        result = self.rehearse()
        self.assertEqual(self.docker.image, SOURCE)
        self.assertTrue(result["photoStable"])
        self.assertEqual((result["source"], result["target"]), (SOURCE, TARGET))
        saved = json.loads((self.layout.state / "swap-rehearsal.json").read_text())
        self.assertEqual(saved, result)

    def test_order_stops_application_then_postgres_then_backup_and_raises_backup_first(self) -> None:
        self.rehearse()
        pull = self.position("docker pull")
        worker = self.position(" stop worker")
        web = self.position(" stop web")
        photo = next(i for i, c in enumerate(self.docker.commands) if "'systemIdentifier'" in c)
        check = self.position("pgbackrest --stanza=duora check")
        postgres = self.position(" stop postgres")
        backup = self.position(" stop backup")
        up_backup = self.position("--no-deps backup")
        up_postgres = self.position("--no-deps postgres")
        self.assertEqual(sorted((pull, worker, web, photo, check, postgres, backup, up_backup, up_postgres)),
                         [pull, worker, web, photo, check, postgres, backup, up_backup, up_postgres])
        self.assertIn("--wait", self.docker.commands[up_postgres])
        self.assertTrue(self.docker.commands[-1].endswith("web worker edge") or "--no-deps web worker edge" in " ".join(self.docker.commands))

    def test_each_direction_runs_the_check_and_is_timed(self) -> None:
        result = self.rehearse()
        self.assertEqual(sum("pgbackrest --stanza=duora check" in c for c in self.docker.commands), 3)
        for direction in ("forward", "back"):
            timing = result[direction]
            self.assertEqual(set(timing), {"stopPostgresSeconds", "stopBackupSeconds", "upBackupSeconds",
                                           "upPostgresSeconds", "databaseDownSeconds", "checkSeconds"})
            self.assertGreater(timing["databaseDownSeconds"], 0)
        self.assertGreaterEqual(result["pullSeconds"], 1)

    def test_real_digest_of_both_containers_is_checked_before_and_after(self) -> None:
        self.rehearse()
        inspected = [c for c in self.docker.commands if "{{.Config.Image}} {{.Image}}" in c]
        self.assertGreaterEqual(len(inspected), 3)
        self.assertTrue(all("duora-c2-postgres-1" in c and "duora-c2-backup-1" in c for c in inspected))

    def test_application_outage_is_reported_apart_from_the_database(self) -> None:
        result = self.rehearse()
        self.assertGreater(result["applicationDownSeconds"], result["forward"]["databaseDownSeconds"])
        for key in ("stopApplicationSeconds", "reopenApplicationSeconds"):
            self.assertGreater(result[key], 0)

    def test_compatibility_and_ballast_checksum_are_compared(self) -> None:
        self.docker.ballast = "250000:abc"
        result = self.rehearse()
        self.assertEqual(result["ballast"], "250000:abc")
        import postgres_swap
        self.assertIn(postgres_swap.SWAP_COMPATIBILITY_SQL, "\n".join(self.docker.commands))

    def test_target_with_another_collation_library_returns_to_the_origin(self) -> None:
        self.docker.compatibility_after_swap = dict(self.docker.compatibility, actual="2.42")
        with self.assertRaisesRegex(shadow.ShadowError, "colação"):
            self.rehearse()
        self.assertEqual(self.docker.image, SOURCE)

    def test_a_minor_upgrade_of_the_server_is_not_a_failure(self) -> None:
        self.docker.compatibility_after_swap = dict(self.docker.compatibility, serverVersionNum="180007")
        self.assertTrue(self.rehearse()["photoStable"])

    def test_a_major_change_of_the_server_returns_to_the_origin(self) -> None:
        self.docker.compatibility_after_swap = dict(self.docker.compatibility, serverVersionNum="190000")
        with self.assertRaisesRegex(shadow.ShadowError, "foto"):
            self.rehearse()

    def test_ballast_that_lost_rows_returns_to_the_origin(self) -> None:
        real = shadow._sql
        def sql(run: Any, compose: list[str], statement: str) -> str:
            lost = "swap_ballast order by id" in statement and self.docker.image == TARGET
            return "1:lost" if lost else real(run, compose, statement)
        with mock.patch.object(shadow, "_sql", sql), self.assertRaisesRegex(shadow.ShadowError, "foto"):
            self.rehearse()
        self.assertEqual(self.docker.image, SOURCE)

    def test_repeated_fill_replaces_the_ballast_instead_of_growing_it(self) -> None:
        self.rehearse(fill_mib=64)
        self.assertTrue(any("truncate c2.swap_ballast" in c for c in self.docker.commands))

    def test_reports_the_data_volume_it_really_had(self) -> None:
        self.assertEqual(self.rehearse()["databaseSizeMib"], 1100)

    def test_never_reaches_production_paths_or_project(self) -> None:
        self.rehearse()
        joined = "\n".join(self.docker.commands)
        for forbidden in ("duora_pgdata", "/etc/duora", "/var/lib/duora/", "--project-name duora ", "coolify", " down", " rm "):
            self.assertNotIn(forbidden, joined)
        self.assertNotIn("duora-deploy", joined)

    def test_postgres_runs_the_same_photo_sql_as_the_executor(self) -> None:
        self.rehearse()
        import postgres_swap
        self.assertIn(postgres_swap.SWAP_PHOTO_SQL, "\n".join(self.docker.commands))


class SwapRehearsalGuardTests(SwapRehearsalFixture):
    def refused(self, **arguments: Any) -> None:
        with self.assertRaises(shadow.ShadowError):
            self.rehearse(**arguments)
        self.assertEqual(self.stops(), [])
        self.assertFalse(any("docker pull" in c for c in self.docker.commands))

    def test_only_the_allowlisted_target(self) -> None:
        for target in (SOURCE, "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "b" * 64, "postgres:18", ""):
            with self.subTest(target=target):
                self.refused(target=target)

    def test_digests_are_the_reviewed_ones(self) -> None:
        self.assertEqual(TARGET, "ghcr.io/bipoxdxd/duora-postgres@sha256:b5103bfd8ac7832915eed8170ffd0bcb94d9bca834cbd659114fa25606f0434e")
        self.assertEqual(SOURCE, "ghcr.io/bipoxdxd/duora-postgres@sha256:d32f8c4408e804bf8b57583c6fdfb8935380e6326bcc7ccb7397b1f386ada06c")

    def test_shadow_on_another_digest_is_refused(self) -> None:
        self.layout.env_file.write_text("DUORA_POSTGRES_IMAGE=ghcr.io/x/y@sha256:" + "a" * 64 + "\n")
        self.refused()

    def test_compose_that_resolves_to_production_is_refused(self) -> None:
        self.docker.config_volume = "duora_pgdata"
        self.refused()
        self.docker.config_volume, self.docker.config_project = "duora-c2_pgdata", "duora"
        self.refused()

    def test_binds_secrets_ports_and_image_must_stay_in_the_shadow(self) -> None:
        for name, value in (("config_bind", "/var/lib/duora/secrets"), ("config_bind", "/etc/duora/duora.env"),
                            ("config_secret", "/etc/duora/secret"), ("config_port_ip", "0.0.0.0"), ("config_port_ip", ""),
                            ("config_image", "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "e" * 64)):
            with self.subTest(name=name, value=value):
                self.setUp()
                setattr(self.docker, name, value)
                self.refused()

    def test_sibling_directory_with_the_same_prefix_is_not_inside_the_root(self) -> None:
        self.docker.config_bind = self.docker.config_root + "-evil/file"
        self.refused()

    def test_memory_below_the_floor_refuses_before_any_effect(self) -> None:
        self.refused(resources=dataclasses.replace(HEALTHY, available_mib=lambda: shadow.SWAP_MIN_AVAILABLE_MIB - 1))

    def test_disk_that_would_pass_85_percent_refuses(self) -> None:
        self.refused(fill_mib=1024, resources=dataclasses.replace(HEALTHY, disk_mib=lambda _path: (100_000, 22_000)))
        self.assertEqual(self.docker.updates, [])

    def test_docker_on_another_filesystem_refuses(self) -> None:
        devices = {str(self.layout.root): 7, "/var/lib/docker": 8}
        self.refused(resources=dataclasses.replace(HEALTHY, device=lambda path: devices.get(str(path), 7)))

    def test_pressure_at_the_start_refuses(self) -> None:
        self.refused(resources=dataclasses.replace(HEALTHY, pressure=lambda: shadow.SWAP_MAX_PRESSURE + 1))

    def test_running_outside_the_origin_refuses_a_repeat(self) -> None:
        self.docker.image = TARGET
        with self.assertRaisesRegex(shadow.ShadowError, "swap-abort"):
            self.rehearse()
        self.assertEqual(self.stops(), [])

    def test_a_container_with_another_image_id_refuses(self) -> None:
        self.docker.running_images = (SOURCE, TARGET)
        self.refused()

    def test_root_outside_the_shadow_is_refused(self) -> None:
        for root in (Path("/var/lib/duora"), Path("/etc/duora"), Path("/tmp/other")):
            with self.subTest(root=root), self.assertRaises(shadow.ShadowError):
                shadow.swap_rehearsal(shadow.Layout(root), TARGET, fill_mib=0, run=self.docker, clock=self.docker.clock,
                                      sleep=self.docker.sleep, resources=HEALTHY)
        self.assertEqual(self.docker.commands, [])

    def test_project_goes_through_the_probe_validation(self) -> None:
        with mock.patch.object(shadow.c2_probe, "validate_project", side_effect=c2_probe.CommandRefused("x")) as validate, \
                self.assertRaises(c2_probe.CommandRefused):
            self.rehearse()
        validate.assert_called()

    def test_fill_is_bounded_and_needs_disk(self) -> None:
        self.refused(fill_mib=shadow.SWAP_MAX_FILL_MIB + 1)
        self.refused(fill_mib=-1)
        self.assertEqual(shadow.SWAP_MAX_FILL_MIB, 2048)

    def test_pull_failure_stops_nothing(self) -> None:
        self.docker.fail_on = "docker pull"
        with self.assertRaises(shadow.ShadowError):
            self.rehearse()
        self.assertEqual(self.stops(), [])

    def test_archive_backlog_stops_nothing(self) -> None:
        self.docker.archived = "000000010000000000000001"
        with self.assertRaisesRegex(shadow.ShadowError, "arquiv"):
            self.rehearse()
        self.assertEqual(self.stops(), [])


class SwapRehearsalResourceTests(SwapRehearsalFixture):
    def test_memory_drop_between_batches_stops_cleanly_without_touching_the_database(self) -> None:
        readings = iter([4000, 4000, 400])
        resources = dataclasses.replace(HEALTHY, available_mib=lambda: next(readings, 400))
        with self.assertRaisesRegex(shadow.ShadowError, "memória"):
            self.rehearse(fill_mib=256, resources=resources)
        self.assertEqual(self.stops(), [])
        self.assertLess(sum("insert into c2.swap_ballast" in c for c in self.docker.commands), 4)
        self.assertTrue(json.loads((self.layout.state / "swap-rehearsal.json").read_text())["aborted"])

    def test_pressure_between_batches_stops_too(self) -> None:
        readings = iter([1.0, 1.0, 90.0])
        with self.assertRaisesRegex(shadow.ShadowError, "PSI"):
            self.rehearse(fill_mib=256, resources=dataclasses.replace(HEALTHY, pressure=lambda: next(readings, 90.0)))

    def test_fill_runs_throttled_and_gives_the_cpu_back_even_on_failure(self) -> None:
        readings = iter([4000, 4000, 400])
        with self.assertRaises(shadow.ShadowError):
            self.rehearse(fill_mib=256, resources=dataclasses.replace(HEALTHY, available_mib=lambda: next(readings, 400)))
        self.assertIn("--cpus 0.5", self.docker.updates[0])
        self.assertIn("--cpu-shares 256", self.docker.updates[0])
        self.assertIn("--cpus 1.5", self.docker.updates[-1])
        self.assertIn("--cpu-shares 2048", self.docker.updates[-1])

    def test_memory_drop_before_the_way_back_returns_to_the_origin(self) -> None:
        seen = {"count": 0}
        def available() -> int:
            seen["count"] += 1
            return 4000 if self.docker.image == SOURCE else 400
        with self.assertRaisesRegex(shadow.ShadowError, "memória"):
            self.rehearse(resources=dataclasses.replace(HEALTHY, available_mib=available))
        self.assertEqual(self.docker.image, SOURCE)

    def test_host_readers_parse_the_real_formats(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            (base / "meminfo").write_text("MemTotal: 8000000 kB\nMemAvailable:  2097152 kB\n")
            self.assertEqual(shadow._memory_available_mib(base / "meminfo"), 2048)
            for name, value in (("cpu", "3.50"), ("memory", "41.25"), ("io", "0.00")):
                (base / name).write_text(f"some avg10={value} avg60=0.00 avg300=0.00 total=1\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=1\n")
            self.assertEqual(shadow._pressure_avg10(base), 41.25)

    def test_minio_has_cpu_and_memory_limits_in_the_overlay(self) -> None:
        overlay = shadow.OVERLAY_FILE.read_text(encoding="utf-8")
        block = overlay[overlay.index("\n  s3:\n"):overlay.index("\n  postgres:\n") if overlay.index("\n  s3:\n") < overlay.index("\n  postgres:\n") else overlay.index("\nnetworks:")]
        self.assertIn("memory:", block)
        self.assertIn("cpus:", block)


@unittest.skipUnless(shutil.which("docker"), "docker CLI ausente: render real do guarda não conferido")
class SwapGuardRealRenderTests(unittest.TestCase):
    """`docker compose config` real, com o ambiente hostil que um root poderia ter herdado: precisa cair no sombra."""

    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.layout = shadow.Layout(Path(directory.name).resolve() / "duora-c2")
        for path in (self.layout.secrets, self.layout.certs, self.layout.conf, self.layout.media / "quarantine",
                     self.layout.media / "derivatives"):
            path.mkdir(parents=True)
        images = dict(shadow.read_release_images(RELEASE_ENV), DUORA_POSTGRES_IMAGE=SOURCE)
        self.layout.env_file.write_text(shadow.render_env(self.layout, images, "k", "s"))

    def test_guard_accepts_the_real_render_under_a_hostile_environment(self) -> None:
        path = os.path.dirname(shutil.which("docker") or "") + ":" + shadow.RUNNER_ENVIRONMENT["PATH"]
        hostile = {"DUORA_MEDIA_DIR": "/var/lib/duora/media", "DUORA_SECRETS_DIR": "/etc/duora/secrets",
                   "DOCKER_HOST": "tcp://192.0.2.1:2375", "DOCKER_CONTEXT": "production", "COMPOSE_PROJECT_NAME": "duora"}
        with mock.patch.dict(shadow.os.environ, hostile), mock.patch.dict(shadow.RUNNER_ENVIRONMENT, PATH=path), \
                redirect_stdout(io.StringIO()):
            self.assertEqual(shadow._swap_guard(self.layout, shadow.default_runner)[:2], ["docker", "compose"])

    def test_inherited_environment_would_escape_the_shadow_without_the_minimal_runner(self) -> None:
        def inherited(argv: Any, **_arguments: Any) -> str:
            environment = dict(os.environ, DUORA_MEDIA_DIR="/var/lib/duora/media", DUORA_SECRETS_DIR="/etc/duora/secrets")
            return subprocess.run(list(argv), capture_output=True, text=True, env=environment, timeout=60, check=True).stdout
        with self.assertRaisesRegex(shadow.ShadowError, "fora de"):
            shadow._swap_guard(self.layout, inherited)

    def test_minio_limits_reach_the_rendered_service(self) -> None:
        result = subprocess.run([*shadow.compose_command(self.layout), "config", "--format", "json"], capture_output=True,
                                text=True, timeout=60, check=True)
        s3 = json.loads(result.stdout)["services"]["s3"]
        self.assertEqual(s3["cpu_shares"], 256)
        self.assertEqual(s3["deploy"]["resources"]["limits"]["memory"], "805306368")
        self.assertEqual(s3["deploy"]["resources"]["limits"]["cpus"], 0.5)


class DefaultRunnerEnvironmentTests(unittest.TestCase):
    def test_commands_never_inherit_the_callers_environment(self) -> None:
        seen = {}
        def run(argv, **arguments):
            seen.update(arguments["env"])
            return subprocess.CompletedProcess(argv, 0, "", "")
        hostile = {"DUORA_MEDIA_DIR": "/var/lib/duora", "DUORA_SECRETS_DIR": "/etc/duora", "DOCKER_HOST": "tcp://other",
                   "DOCKER_CONTEXT": "production", "COMPOSE_PROJECT_NAME": "duora", "COMPOSE_FILE": "/opt/duora/compose.json"}
        with mock.patch.dict(shadow.os.environ, hostile), mock.patch.object(shadow.subprocess, "run", run), redirect_stdout(io.StringIO()):
            shadow.default_runner(["docker", "ps"])
        self.assertEqual(seen, {"PATH": "/usr/bin:/bin", "LC_ALL": "C"})


class SwapRehearsalFillTests(SwapRehearsalFixture):
    def test_fill_goes_in_bounded_batches_of_synthetic_data_only(self) -> None:
        self.rehearse(fill_mib=256)
        batches = [c for c in self.docker.commands if "insert into c2.swap_ballast" in c]
        self.assertEqual(len(batches), 4)
        self.assertTrue(any("set storage external" in c for c in self.docker.commands))

    def test_fill_happens_before_anything_stops(self) -> None:
        self.rehearse(fill_mib=64)
        self.assertLess(self.position("insert into c2.swap_ballast"), self.position(" stop worker"))


class SwapRehearsalAbortTests(SwapRehearsalFixture):
    def assert_back_on_source(self) -> None:
        self.assertEqual(self.docker.image, SOURCE)
        self.assertTrue(any("--no-deps web worker edge" in c for c in self.docker.commands))

    def test_unhealthy_target_returns_to_the_old_digest(self) -> None:
        real = self.docker.__call__
        calls = {"up": 0}
        def failing(argv, **arguments):
            if "--no-deps postgres" in " ".join(argv):
                calls["up"] += 1
                if calls["up"] == 1:
                    self.docker.commands.append(" ".join(argv))
                    raise shadow.ShadowError("container unhealthy")
            return real(argv, **arguments)
        with self.assertRaisesRegex(shadow.ShadowError, "unhealthy"):
            shadow.swap_rehearsal(self.layout, TARGET, fill_mib=0, run=failing, clock=self.docker.clock,
                                  sleep=self.docker.sleep, resources=HEALTHY)
        self.assert_back_on_source()
        saved = json.loads((self.layout.state / "swap-rehearsal.json").read_text())
        self.assertTrue(saved["aborted"])
        self.assertIn("unhealthy", saved["error"])

    def test_failed_check_with_the_application_stopped_reopens_it(self) -> None:
        self.docker.fail_on = "pgbackrest --stanza=duora check"
        real = self.docker.__call__
        def once(argv, **arguments):
            try:
                return real(argv, **arguments)
            except shadow.ShadowError:
                self.docker.fail_on = None
                raise
        with self.assertRaisesRegex(shadow.ShadowError, "abortado"):
            shadow.swap_rehearsal(self.layout, TARGET, fill_mib=0, run=once, clock=self.docker.clock,
                                  sleep=self.docker.sleep, resources=HEALTHY)
        self.assert_back_on_source()

    def test_changed_photo_returns_to_the_old_digest(self) -> None:
        self.docker.photo_after_swap = dict(PHOTO, timeline=2)
        with self.assertRaisesRegex(shadow.ShadowError, "foto"):
            self.rehearse()
        self.assert_back_on_source()

    def test_wrong_image_after_up_returns_to_the_old_digest(self) -> None:
        real = self.docker.__call__
        def lying(argv, **arguments):
            if argv[:2] == ["docker", "inspect"] and self.docker.image == TARGET:
                self.docker.commands.append(" ".join(argv))
                return SOURCE + "\n"
            return real(argv, **arguments)
        with self.assertRaisesRegex(shadow.ShadowError, "digest"):
            shadow.swap_rehearsal(self.layout, TARGET, fill_mib=0, run=lying, clock=self.docker.clock,
                                  sleep=self.docker.sleep, resources=HEALTHY)
        self.assertEqual(self.docker.image, SOURCE)

    def test_volume_identity_change_is_a_failure(self) -> None:
        real = self.docker.__call__
        def replaced(argv, **arguments):
            if argv[:3] == ["docker", "volume", "inspect"] and self.docker.image == TARGET:
                self.docker.volume_created = "2026-11-07T00:00:00Z"
            return real(argv, **arguments)
        with self.assertRaisesRegex(shadow.ShadowError, "volume"):
            shadow.swap_rehearsal(self.layout, TARGET, fill_mib=0, run=replaced, clock=self.docker.clock,
                                  sleep=self.docker.sleep, resources=HEALTHY)

    def test_explicit_abort_returns_a_killed_rehearsal_to_the_old_digest(self) -> None:
        self.docker.image = TARGET
        shadow.swap_abort(self.layout, run=self.docker)
        self.assert_back_on_source()
        self.assertFalse(any(" down" in c or " rm " in c for c in self.docker.commands))

    def killed_run_baseline(self) -> None:
        with mock.patch.object(shadow, "_swap_direction", side_effect=KeyboardInterrupt), self.assertRaises(KeyboardInterrupt):
            self.rehearse()

    def test_abort_confirms_digest_photo_and_check_and_records_them(self) -> None:
        self.killed_run_baseline()
        self.docker.image = TARGET
        self.docker.commands.clear()
        report = shadow.swap_abort(self.layout, run=self.docker)
        self.assertEqual(report, {"digest": SOURCE, "baselineCompared": True, "check": True})
        self.assertTrue(any("pgbackrest --stanza=duora check" in c for c in self.docker.commands))
        self.assertEqual(json.loads((self.layout.state / "swap-abort.json").read_text()), report)

    def test_abort_with_a_changed_photo_fails_and_leaves_the_application_stopped(self) -> None:
        self.killed_run_baseline()
        self.docker.image = TARGET
        self.docker.commands.clear()
        with mock.patch.object(shadow, "_swap_snapshot", return_value={"photo": {"timeline": 9}}), \
                self.assertRaisesRegex(shadow.ShadowError, "foto"):
            shadow.swap_abort(self.layout, run=self.docker)
        self.assertFalse(any("--no-deps web worker edge" in c for c in self.docker.commands))
        self.assertIn("error", json.loads((self.layout.state / "swap-abort.json").read_text()))

    def test_abort_that_does_not_reach_the_origin_leaves_the_application_stopped(self) -> None:
        self.docker.running_images = (TARGET, TARGET)
        with self.assertRaisesRegex(shadow.ShadowError, "aplicação parada"):
            shadow.swap_abort(self.layout, run=self.docker)
        self.assertFalse(any("--no-deps web worker edge" in c for c in self.docker.commands))

    def test_a_new_rehearsal_discards_the_baseline_of_the_previous_one(self) -> None:
        baseline = self.layout.state / "swap-baseline.json"
        baseline.write_text("{}")
        self.docker.fail_on = "docker pull"
        with self.assertRaises(shadow.ShadowError):
            self.rehearse()
        self.assertFalse(baseline.exists())

    def test_abort_without_baseline_says_it_could_not_compare(self) -> None:
        self.docker.image = TARGET
        self.assertFalse(shadow.swap_abort(self.layout, run=self.docker)["baselineCompared"])

    def test_failed_rehearsal_records_the_abort_result(self) -> None:
        self.docker.photo_after_swap = dict(PHOTO, timeline=2)
        with self.assertRaises(shadow.ShadowError):
            self.rehearse()
        saved = json.loads((self.layout.state / "swap-rehearsal.json").read_text())
        self.assertEqual(saved["abort"]["digest"], SOURCE)

    def test_oserror_during_the_swap_also_rolls_back(self) -> None:
        with mock.patch.object(shadow, "_swap_recreate", side_effect=[OSError("disk"), {}]), self.assertRaisesRegex(shadow.ShadowError, "disk"):
            self.rehearse()

    def test_rollback_shields_itself_from_further_signals(self) -> None:
        self.docker.photo_after_swap = dict(PHOTO, timeline=2)
        shielded = []
        with self.assertRaises(shadow.ShadowError):
            self.rehearse(shield=lambda: shielded.append(True))
        self.assertEqual(shielded, [True])

    def test_abort_keeps_the_guards(self) -> None:
        with self.assertRaises(shadow.ShadowError):
            shadow.swap_abort(shadow.Layout(Path("/var/lib/duora")), run=self.docker)
        self.assertEqual(self.docker.commands, [])


class SwapRehearsalCliTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name) / "duora-c2"
        (self.root / "state").mkdir(parents=True)

    def main(self, *arguments: str) -> int:
        with mock.patch.object(shadow.os, "geteuid", return_value=0), redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            return shadow.main(["--root", str(self.root), *arguments])

    def test_main_exposes_the_rehearsal_and_the_abort(self) -> None:
        with mock.patch.object(shadow, "swap_rehearsal", return_value={"ok": True}) as rehearsal:
            self.assertEqual(self.main("swap-rehearsal", "--target", TARGET, "--fill-mib", "1024"), 0)
        self.assertEqual(rehearsal.call_args.args[1], TARGET)
        self.assertEqual(rehearsal.call_args.kwargs["fill_mib"], 1024)
        with mock.patch.object(shadow, "swap_abort", return_value={"digest": SOURCE}) as abort:
            self.assertEqual(self.main("swap-abort"), 0)
        abort.assert_called_once()

    def test_commands_exclude_each_other_through_a_real_lock(self) -> None:
        for command, target in ((["swap-rehearsal", "--target", TARGET], "swap_rehearsal"), (["swap-abort"], "swap_abort"),
                                (["restore-rehearsal"], "restore_rehearsal"), (["down"], "down")):
            with self.subTest(command=command[0]), shadow._swap_lock(shadow.Layout(self.root)), \
                    mock.patch.object(shadow, target) as called:
                self.assertEqual(self.main(*command), 1)
                called.assert_not_called()

    def test_sigterm_and_sighup_become_a_rollback_and_the_handlers_are_restored(self) -> None:
        before = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGHUP)}
        for number in (signal.SIGTERM, signal.SIGHUP):
            with self.subTest(number=number), self.assertRaises(shadow.ShadowError):
                with shadow._swap_signals():
                    os.kill(os.getpid(), number)
                    time.sleep(1)
        self.assertEqual(before, {number: signal.getsignal(number) for number in before})

    def test_after_the_shield_a_signal_no_longer_interrupts_the_rollback(self) -> None:
        with shadow._swap_signals() as shield:
            shield()
            os.kill(os.getpid(), signal.SIGTERM)
            time.sleep(0.05)

    def test_refused_host_failures_exit_with_a_message_not_a_traceback(self) -> None:
        for error in (subprocess.TimeoutExpired("docker", 1), KeyError("services")):
            with self.subTest(error=type(error).__name__), mock.patch.object(shadow, "swap_abort", side_effect=error):
                self.assertEqual(self.main("swap-abort"), 1)


if __name__ == "__main__":
    unittest.main()
