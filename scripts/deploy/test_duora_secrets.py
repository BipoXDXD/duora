"""Testes do gerador de segredos. Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v"""

import stat
import subprocess
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import duora_secrets as secrets_cli


def read(directory: Path, name: str) -> str:
    return (directory / name).read_text(encoding="utf-8")


class InitSecretsTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.directory = Path(self._tmp.name) / "secrets"

    def test_creates_every_secret_file(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual(set(secrets_cli.SECRET_OWNERS), {p.name for p in self.directory.iterdir()})

    def test_directory_is_0700_and_files_are_0400(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual(0o700, stat.S_IMODE(self.directory.stat().st_mode))
        for path in self.directory.iterdir():
            with self.subTest(file=path.name):
                self.assertEqual(0o400, stat.S_IMODE(path.stat().st_mode))

    def test_connection_strings_carry_the_role_passwords(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        app_password = read(self.directory, "db_app_password")
        migrator_password = read(self.directory, "db_migrator_password")
        self.assertIn(f"Username=duora_app;Password={app_password};",
                      read(self.directory, "duora_app_connection"))
        self.assertIn(f"Username=duora_migrator;Password={migrator_password};",
                      read(self.directory, "duora_migrator_connection"))

    def test_passwords_are_distinct_and_long(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        passwords = [read(self.directory, name) for name in
                     ("postgres_superuser_password", "db_migrator_password", "db_app_password")]
        self.assertEqual(3, len(set(passwords)))
        self.assertTrue(all(len(password) >= 32 for password in passwords))

    def test_second_run_keeps_existing_secrets(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        before = read(self.directory, "db_app_password")

        secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual(before, read(self.directory, "db_app_password"))

    def test_partial_directory_is_refused(self) -> None:
        self.directory.mkdir(mode=0o700)
        (self.directory / "db_app_password").write_text("antiga", encoding="utf-8")

        with self.assertRaisesRegex(secrets_cli.SecretsError, "incompleto"):
            secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual("antiga", read(self.directory, "db_app_password"))


def openssl(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["openssl", *args], capture_output=True, text=True, check=False)


class InitBackupTests(unittest.TestCase):
    """Certificados do pgBackRest (postgres → backup por TLS). Precisa do openssl, como na VPS."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.directory = Path(self._tmp.name) / "secrets"

    def test_creates_ca_server_and_client_files_0400(self) -> None:
        secrets_cli.init_backup(self.directory, change_owner=False)

        self.assertEqual(set(secrets_cli.BACKUP_FILES), {p.name for p in self.directory.iterdir()})
        for path in self.directory.iterdir():
            with self.subTest(file=path.name):
                self.assertEqual(0o400, stat.S_IMODE(path.stat().st_mode))

    def test_ca_private_key_is_not_kept(self) -> None:
        secrets_cli.init_backup(self.directory, change_owner=False)

        keys = [p.name for p in self.directory.iterdir() if "PRIVATE KEY" in p.read_text(encoding="utf-8")]
        self.assertEqual(["pgbackrest_tls_client.key", "pgbackrest_tls_server.key"], sorted(keys))

    def test_server_certificate_is_for_the_backup_host_and_signed_by_the_ca(self) -> None:
        secrets_cli.init_backup(self.directory, change_owner=False)

        server = str(self.directory / "pgbackrest_tls_server.crt")
        verify = openssl("verify", "-CAfile", str(self.directory / "pgbackrest_tls_ca.crt"), "-purpose", "sslserver",
                         "-verify_hostname", "backup", server)
        self.assertEqual(0, verify.returncode, verify.stdout + verify.stderr)

    def test_server_certificate_is_not_valid_for_another_host(self) -> None:
        secrets_cli.init_backup(self.directory, change_owner=False)

        verify = openssl("verify", "-CAfile", str(self.directory / "pgbackrest_tls_ca.crt"), "-verify_hostname",
                         "postgres", str(self.directory / "pgbackrest_tls_server.crt"))
        self.assertNotEqual(0, verify.returncode)

    def test_client_certificate_carries_the_cn_authorized_on_the_backup_server(self) -> None:
        secrets_cli.init_backup(self.directory, change_owner=False)

        client = str(self.directory / "pgbackrest_tls_client.crt")
        verify = openssl("verify", "-CAfile", str(self.directory / "pgbackrest_tls_ca.crt"), "-purpose", "sslclient",
                         client)
        subject = openssl("x509", "-in", client, "-noout", "-subject", "-nameopt", "RFC2253")
        self.assertEqual(0, verify.returncode, verify.stdout + verify.stderr)
        self.assertEqual("subject=CN=duora-postgres", subject.stdout.strip())

    def test_backup_role_password_is_long_and_matches_the_pgpass_of_the_backup_container(self) -> None:
        secrets_cli.init_backup(self.directory, change_owner=False)

        password = read(self.directory, "db_backup_password")
        self.assertGreaterEqual(len(password), 32)
        # .pgpass do libpq: host:porta:banco:usuário:senha, só a origem e o papel do pgBackRest.
        self.assertEqual(f"postgres:5432:postgres:duora_backup:{password}\n",
                         read(self.directory, "pgbackrest_db_pgpass"))

    def test_second_run_keeps_existing_certificates(self) -> None:
        secrets_cli.init_backup(self.directory, change_owner=False)
        before = read(self.directory, "pgbackrest_tls_server.crt")

        created = secrets_cli.init_backup(self.directory, change_owner=False)

        self.assertFalse(created)
        self.assertEqual(before, read(self.directory, "pgbackrest_tls_server.crt"))

    def test_partial_tls_set_is_refused(self) -> None:
        self.directory.mkdir(mode=0o700)
        (self.directory / "pgbackrest_tls_ca.crt").write_text("antiga", encoding="utf-8")

        with self.assertRaisesRegex(secrets_cli.SecretsError, "incompleto"):
            secrets_cli.init_backup(self.directory, change_owner=False)

        self.assertEqual("antiga", read(self.directory, "pgbackrest_tls_ca.crt"))

    def test_database_secrets_and_tls_coexist_in_the_same_directory(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        secrets_cli.init_backup(self.directory, change_owner=False)

        self.assertFalse(secrets_cli.init_secrets(self.directory, change_owner=False))


if __name__ == "__main__":
    unittest.main()
