"""Projeto sombra duora-c2 (issue #86): env, configurações, dados sintéticos, overlay e restore com dublês."""
from __future__ import annotations

import base64
import hashlib
import io
import json
import re
import shutil
import subprocess
import tempfile
import unittest
from contextlib import redirect_stdout
from datetime import UTC, datetime
from pathlib import Path
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
            with mock.patch("os.geteuid", return_value=0), redirect_stdout(io.StringIO()):
                self.assertEqual(shadow.main(["restore-rehearsal", "--no-final-full"]), 0)
                self.assertEqual(shadow.main(["restore-rehearsal"]), 0)
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


if __name__ == "__main__":
    unittest.main()
