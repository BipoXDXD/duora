"""Testes do gerador de segredos. Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v"""

import contextlib
import io
import os
import re
import stat
import subprocess
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock
from tempfile import TemporaryDirectory

import duora_backup
import duora_secrets as secrets_cli


def read(directory: Path, name: str) -> str:
    return (directory / name).read_text(encoding="utf-8")


class GeneratePasswordTests(unittest.TestCase):
    """Senha gerada nunca começa com "-": uma CLI (o mc do drill) a leria como opção ("flag provided but not
    defined")."""

    URLSAFE = re.compile(r"[A-Za-z0-9_-]+")

    def test_never_starts_with_a_dash_even_when_the_random_part_does(self) -> None:
        with mock.patch.object(secrets_cli.secrets, "token_urlsafe", return_value="-Awrz3x" + "a" * 36):
            password = secrets_cli.generate_password()

        self.assertRegex(password[0], r"[A-Za-z0-9]")
        self.assertTrue(password.endswith("-Awrz3x" + "a" * 36))

    def test_uses_only_the_url_safe_alphabet_with_the_expected_length(self) -> None:
        for _ in range(500):
            password = secrets_cli.generate_password()

            self.assertEqual(44, len(password))
            self.assertRegex(password, self.URLSAFE)
            self.assertNotEqual("-", password[0])

    def test_generated_passwords_are_different(self) -> None:
        self.assertEqual(100, len({secrets_cli.generate_password() for _ in range(100)}))

    def test_every_generated_secret_file_uses_it(self) -> None:
        with mock.patch.object(secrets_cli, "generate_password", return_value="PSENHA") as generator, \
                TemporaryDirectory() as tmp:
            target = Path(tmp) / "secrets"
            target.mkdir(mode=0o700)
            secrets_cli.init_secrets(target, change_owner=False)
            secrets_cli.init_backup(target, change_owner=False)

        # superusuário, migrador, aplicação e duora_backup.
        self.assertEqual(4, generator.call_count)


class InitSecretsTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.directory = Path(self._tmp.name) / "secrets"

    def test_creates_every_secret_file(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual(set(secrets_cli.SECRET_OWNERS) | set(secrets_cli.CERTIFICATE_OWNERS),
                         {p.name for p in self.directory.iterdir()})

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



NOW = datetime(2026, 10, 15, 12, 0, tzinfo=timezone.utc)
CERTIFICATE = "dataprotection_certificate"
PREVIOUS = "dataprotection_previous_certificates"


def pem_blocks(text: str) -> list[str]:
    return re.findall(r"-----BEGIN ([A-Z ]+)-----", text)


def fingerprint(pem: str) -> str:
    result = subprocess.run(["openssl", "x509", "-noout", "-fingerprint", "-sha256"], input=pem,
                            capture_output=True, text=True, check=True)
    return result.stdout.strip()


class KeyRingCertificateTests(unittest.TestCase):
    """Certificado que cifra o key ring do Data Protection em repouso (spec 11.2, docs/security/auth.md)."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.directory = Path(self._tmp.name) / "secrets"

    def test_certificate_is_one_certificate_followed_by_its_unencrypted_private_key(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        pem = read(self.directory, CERTIFICATE)
        self.assertEqual(["CERTIFICATE", "PRIVATE KEY"], pem_blocks(pem))
        modulus = ["openssl", "x509", "-noout", "-modulus"], ["openssl", "rsa", "-noout", "-modulus"]
        certificate_modulus, key_modulus = (
            subprocess.run(command, input=pem, capture_output=True, text=True, check=True).stdout
            for command in modulus)
        self.assertEqual(certificate_modulus, key_modulus)

    def test_previous_certificates_start_empty(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual("", read(self.directory, PREVIOUS))

    def test_certificate_is_added_to_a_directory_that_only_has_the_passwords(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        app_password = read(self.directory, "db_app_password")
        (self.directory / CERTIFICATE).unlink()
        (self.directory / PREVIOUS).unlink()

        self.assertTrue(secrets_cli.init_secrets(self.directory, change_owner=False))

        self.assertEqual(app_password, read(self.directory, "db_app_password"))
        self.assertEqual(["CERTIFICATE", "PRIVATE KEY"], pem_blocks(read(self.directory, CERTIFICATE)))

    def test_second_run_keeps_the_certificate(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        before = read(self.directory, CERTIFICATE)

        self.assertFalse(secrets_cli.init_secrets(self.directory, change_owner=False))

        self.assertEqual(before, read(self.directory, CERTIFICATE))

    def test_certificate_without_its_previous_file_is_refused(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        before = read(self.directory, CERTIFICATE)
        (self.directory / PREVIOUS).unlink()

        with self.assertRaisesRegex(secrets_cli.SecretsError, "incompleto"):
            secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual(before, read(self.directory, CERTIFICATE))

    def test_rotation_moves_the_current_certificate_to_the_previous_ones(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        first = read(self.directory, CERTIFICATE)

        secrets_cli.rotate_certificate(self.directory, change_owner=False)

        second = read(self.directory, CERTIFICATE)
        self.assertNotEqual(fingerprint(first), fingerprint(second))
        self.assertEqual(first, read(self.directory, PREVIOUS))

    def test_second_rotation_keeps_every_previous_certificate_newest_first(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        first = read(self.directory, CERTIFICATE)
        secrets_cli.rotate_certificate(self.directory, change_owner=False)
        second = read(self.directory, CERTIFICATE)

        secrets_cli.rotate_certificate(self.directory, change_owner=False)

        self.assertEqual(second + first, read(self.directory, PREVIOUS))

    def test_rotated_files_stay_0400(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        secrets_cli.rotate_certificate(self.directory, change_owner=False)

        for name in (CERTIFICATE, PREVIOUS):
            with self.subTest(file=name):
                self.assertEqual(0o400, stat.S_IMODE((self.directory / name).stat().st_mode))
        self.assertEqual({CERTIFICATE, PREVIOUS} | set(secrets_cli.SECRET_OWNERS),
                         {p.name for p in self.directory.iterdir()})

    def test_rotation_without_a_certificate_is_refused(self) -> None:
        self.directory.mkdir(mode=0o700)

        with self.assertRaisesRegex(secrets_cli.SecretsError, "init"):
            secrets_cli.rotate_certificate(self.directory, change_owner=False)

    def test_retiring_empties_the_previous_certificates_and_keeps_the_current(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        secrets_cli.rotate_certificate(self.directory, change_owner=False)
        current = read(self.directory, CERTIFICATE)
        keys = self.key_ring(("expirada", read(self.directory, PREVIOUS), -timedelta(days=2)),
                             ("nova", current, timedelta(days=90)))

        secrets_cli.retire_previous_certificates(self.directory, keys, change_owner=False, now=NOW)

        self.assertEqual("", read(self.directory, PREVIOUS))
        self.assertEqual(current, read(self.directory, CERTIFICATE))

    def test_retiring_is_refused_while_a_key_encrypted_by_a_previous_certificate_is_still_needed(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        secrets_cli.rotate_certificate(self.directory, change_owner=False)
        previous = read(self.directory, PREVIOUS)
        # Expirou há 1 h: ainda decifra tokens de até 24 h que ela protegeu.
        keys = self.key_ring(("recente", previous, -timedelta(hours=1)))

        with self.assertRaisesRegex(secrets_cli.SecretsError, "recente"):
            secrets_cli.retire_previous_certificates(self.directory, keys, change_owner=False, now=NOW)

        self.assertEqual(previous, read(self.directory, PREVIOUS))

    def test_retiring_without_key_files_is_refused(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)
        empty = Path(self._tmp.name) / "sem-chaves"
        empty.mkdir()

        with self.assertRaisesRegex(secrets_cli.SecretsError, "nenhuma chave"):
            secrets_cli.retire_previous_certificates(self.directory, empty, change_owner=False, now=NOW)

    def test_rotation_replaces_the_previous_file_before_the_certificate_and_syncs_to_disk(self) -> None:
        secrets_cli.init_secrets(self.directory, change_owner=False)

        with mock.patch.object(secrets_cli.os, "replace", wraps=os.replace) as replace, \
                mock.patch.object(secrets_cli.os, "fsync", wraps=os.fsync) as fsync:
            secrets_cli.rotate_certificate(self.directory, change_owner=False)

        self.assertEqual([self.directory / PREVIOUS, self.directory / CERTIFICATE],
                         [Path(call.args[1]) for call in replace.call_args_list])
        # Os dois arquivos novos e o diretório, depois das trocas.
        self.assertGreaterEqual(fsync.call_count, 3)

    def test_init_stages_the_certificate_pair_and_renames_the_certificate_last(self) -> None:
        with mock.patch.object(secrets_cli.os, "replace", wraps=os.replace) as replace:
            secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual([self.directory / PREVIOUS, self.directory / CERTIFICATE],
                         [Path(call.args[1]) for call in replace.call_args_list])
        self.assertFalse([p.name for p in self.directory.iterdir() if p.name.startswith(".")])

    def test_temporary_private_key_is_removed_even_when_openssl_fails(self) -> None:
        self.directory.mkdir(mode=0o700)
        failure = subprocess.CompletedProcess(["openssl"], 1, stdout="", stderr="falhou")

        with mock.patch.object(secrets_cli.subprocess, "run", return_value=failure), \
                self.assertRaisesRegex(secrets_cli.SecretsError, "openssl req falhou"):
            secrets_cli.init_secrets(self.directory, change_owner=False)

        self.assertEqual([], [p.name for p in self.directory.iterdir()])

    def key_ring(self, *keys: tuple[str, str, timedelta]) -> Path:
        """Arquivos key-*.xml como o Data Protection grava: expirationDate e o certificado que cifrou a chave."""
        directory = Path(self._tmp.name) / "key-ring"
        directory.mkdir(exist_ok=True)
        for name, pem, until_expiration in keys:
            certificate = "".join(pem.split("-----BEGIN CERTIFICATE-----")[1].split("-----END CERTIFICATE-----")[0].split())
            expiration = (NOW + until_expiration).isoformat().replace("+00:00", "Z")
            (directory / f"key-{name}.xml").write_text(
                f'<?xml version="1.0" encoding="utf-8"?><key id="{name}" version="1"><creationDate>2026-01-01T00:00:00Z'
                f'</creationDate><expirationDate>{expiration}</expirationDate><descriptor><descriptor>'
                f'<encryptedSecret><EncryptedData Type="http://www.w3.org/2001/04/xmlenc#Element" '
                f'xmlns="http://www.w3.org/2001/04/xmlenc#"><KeyInfo xmlns="http://www.w3.org/2000/09/xmldsig#">'
                f'<EncryptedKey xmlns="http://www.w3.org/2001/04/xmlenc#"><KeyInfo '
                f'xmlns="http://www.w3.org/2000/09/xmldsig#"><X509Data><X509Certificate>{certificate}'
                f'</X509Certificate></X509Data></KeyInfo></EncryptedKey></KeyInfo></EncryptedData>'
                f'</encryptedSecret></descriptor></descriptor></key>', encoding="utf-8")
        return directory


class WriteBackupRepositoryTests(unittest.TestCase):
    """pgbackrest_repo.conf com a chave do backup no B2, digitada no stdin (docs/operations/backblaze-setup.md)."""

    # Formato das application keys do B2: keyID de 25 caracteres e applicationKey de 31 (sintéticos).
    KEY = "005a1b2c3d4e5f60000000001"
    SECRET = "K005Sintetico/NaoReal+000000000"
    ENDPOINT = "s3.us-east-005.backblazeb2.com"

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.directory = Path(self._tmp.name) / "secrets"
        self.directory.mkdir(mode=0o700)
        self.path = self.directory / secrets_cli.BACKUP_REPOSITORY_FILE

    def repository(self, **overrides: str) -> secrets_cli.BackupRepository:
        values = {"bucket": "duora-backup-exemplo", "region": "", "endpoint": self.ENDPOINT,
                  "uri_style": "host", "credentials": f"{self.KEY}\n{self.SECRET}\n"}
        values.update(overrides)
        return secrets_cli.parse_backup_repository(**values)

    def test_writes_a_file_that_the_verify_reads_back_with_the_region_of_the_b2_endpoint(self) -> None:
        secrets_cli.write_backup_repository(self.directory, self.repository(), replace=False, change_owner=False)

        settings = duora_backup.parse_repo_settings(self.path.read_text(encoding="utf-8"))
        self.assertEqual((self.ENDPOINT, "duora-backup-exemplo", "us-east-005", self.KEY, self.SECRET, False, 443),
                         (settings.endpoint, settings.bucket, settings.region, settings.key, settings.secret,
                          settings.path_style, settings.port))
        self.assertEqual("duora-backup-exemplo.s3.us-east-005.backblazeb2.com", settings.host)
        self.assertTrue(settings.is_backblaze_b2)
        self.assertRegex(self.path.read_text(encoding="utf-8"), r"(?m)^repo1-s3-uri-style=host$")

    def test_another_region_number_comes_from_the_endpoint(self) -> None:
        repository = self.repository(endpoint="s3.us-east-004.backblazeb2.com")

        self.assertEqual("us-east-004", repository.region)

    def test_explicit_region_wins_for_endpoints_without_it_in_the_name(self) -> None:
        repository = self.repository(endpoint="storage.example.test", region="us-east-1")

        self.assertEqual(("storage.example.test", "us-east-1"), (repository.endpoint, repository.region))

    def test_file_is_0400(self) -> None:
        secrets_cli.write_backup_repository(self.directory, self.repository(), replace=False, change_owner=False)

        self.assertEqual(0o400, stat.S_IMODE(self.path.stat().st_mode))

    def test_existing_file_is_kept_without_replace(self) -> None:
        self.path.write_text("antigo", encoding="utf-8")

        with self.assertRaisesRegex(secrets_cli.SecretsError, "--replace"):
            secrets_cli.write_backup_repository(self.directory, self.repository(), replace=False, change_owner=False)
        self.assertEqual("antigo", self.path.read_text(encoding="utf-8"))

    def test_replace_swaps_the_file_atomically_and_leaves_no_staging_file(self) -> None:
        self.path.write_text("antigo", encoding="utf-8")

        secrets_cli.write_backup_repository(self.directory, self.repository(), replace=True, change_owner=False)

        self.assertIn(self.KEY, self.path.read_text(encoding="utf-8"))
        self.assertEqual([self.path.name], [p.name for p in self.directory.iterdir()])
        self.assertEqual(0o400, stat.S_IMODE(self.path.stat().st_mode))

    def test_explicit_endpoint_and_path_style_are_kept(self) -> None:
        repository = self.repository(uri_style="path")

        secrets_cli.write_backup_repository(self.directory, repository, replace=False, change_owner=False)

        settings = duora_backup.parse_repo_settings(self.path.read_text(encoding="utf-8"))
        self.assertTrue(settings.path_style)

    def test_refuses_invalid_input(self) -> None:
        cases = {
            "endpoint vazio": {"endpoint": ""},
            "endpoint com esquema": {"endpoint": "https://s3.us-east-005.backblazeb2.com"},
            "endpoint com caminho": {"endpoint": "s3.us-east-005.backblazeb2.com/x"},
            "endpoint sem região no nome e sem --region": {"endpoint": "storage.example.test"},
            "bucket com ponto": {"bucket": "duora.backup"},
            "bucket maiúsculo": {"bucket": "Duora-Backup"},
            "região inválida": {"region": "us_east_1"},
            "uri style": {"uri_style": "virtual"},
            "uma linha só": {"credentials": f"{self.KEY}\n"},
            "três linhas": {"credentials": f"{self.KEY}\n{self.SECRET}\nmais\n"},
            "chave com espaço": {"credentials": f"005a1b2c 3d4e5f60000000001\n{self.SECRET}\n"},
            "segredo injeta linha": {"credentials": f"{self.KEY}\n{self.SECRET}\\nrepo1-type=posix\n"},
            "segredo curto": {"credentials": f"{self.KEY}\ncurto\n"},
            "segredo vazio": {"credentials": f"{self.KEY}\n\n"},
        }
        for name, override in cases.items():
            with self.subTest(case=name):
                with self.assertRaises(secrets_cli.SecretsError):
                    self.repository(**override)

    def test_error_messages_never_carry_the_secret(self) -> None:
        with self.assertRaises(secrets_cli.SecretsError) as raised:
            self.repository(credentials=f"{self.KEY}\n{self.SECRET} \n")

        self.assertNotIn(self.SECRET, str(raised.exception))
        self.assertNotIn(self.SECRET, repr(self.repository()))

    def test_command_reads_the_credentials_from_stdin(self) -> None:
        with mock.patch.object(secrets_cli.sys, "stdin") as stdin:
            stdin.read.return_value = f"{self.KEY}\n{self.SECRET}\n"
            code = secrets_cli.main(["write-backup-repo", str(self.directory), "--bucket", "duora-backup-exemplo",
                                     "--endpoint", self.ENDPOINT, "--no-chown"])

        self.assertEqual(0, code)
        self.assertIn(f"repo1-s3-key-secret={self.SECRET}", self.path.read_text(encoding="utf-8"))


class WriteMediaBackupRepositoryTests(unittest.TestCase):
    """Os três arquivos do restic das fotos: destino, senha do repositório e chave B2 presa a media/."""

    KEY = "005a1b2c3d4e5f60000000002"
    SECRET = "K005SinteticoMidia/NaoReal+0000"
    PASSWORD = "Senha+Sintetica/DoRestic0000000000000000000000000000="
    ENDPOINT = "s3.us-east-005.backblazeb2.com"

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.directory = Path(self._tmp.name) / "secrets"
        self.directory.mkdir(mode=0o700)

    def write(self, *, stdin: str | None = None, bucket: str = "duora-backup-exemplo", endpoint: str | None = None,
              replace: bool = False) -> None:
        stdin = stdin if stdin is not None else f"{self.KEY}\n{self.SECRET}\n{self.PASSWORD}\n"
        repository = secrets_cli.parse_media_backup_repository(bucket=bucket, endpoint=endpoint or self.ENDPOINT,
                                                               lines=stdin)
        secrets_cli.write_media_backup_repository(self.directory, repository, replace=replace)

    def test_the_backup_wrapper_reads_back_what_was_written(self) -> None:
        self.write()

        media = duora_backup.read_media_settings(self.directory)

        self.assertEqual((self.ENDPOINT, "duora-backup-exemplo", "us-east-005", self.KEY, self.SECRET),
                         (media.settings.endpoint, media.settings.bucket, media.settings.region, media.settings.key,
                          media.settings.secret))
        self.assertEqual(self.PASSWORD, media.password_file.read_text(encoding="utf-8").strip())

    def test_every_file_is_0400(self) -> None:
        self.write()

        for name in secrets_cli.MEDIA_BACKUP_FILES:
            with self.subTest(name=name):
                self.assertEqual(0o400, stat.S_IMODE((self.directory / name).stat().st_mode))

    def test_existing_files_are_kept_without_replace(self) -> None:
        self.write()
        before = (self.directory / "restic_media_password").read_text(encoding="utf-8")

        with self.assertRaisesRegex(secrets_cli.SecretsError, "--replace"):
            self.write(stdin=f"{self.KEY}\n{self.SECRET}\n{'Outra' * 10}\n")
        self.assertEqual(before, (self.directory / "restic_media_password").read_text(encoding="utf-8"))

    def test_replace_rotates_the_key_and_leaves_no_staging_file(self) -> None:
        self.write()

        self.write(stdin=f"005a1b2c3d4e5f60000000003\n{self.SECRET}\n{self.PASSWORD}\n", replace=True)

        self.assertEqual("005a1b2c3d4e5f60000000003", duora_backup.read_media_settings(self.directory).settings.key)
        self.assertEqual(sorted(secrets_cli.MEDIA_BACKUP_FILES), sorted(p.name for p in self.directory.iterdir()))

    def test_refuses_invalid_input_without_echoing_secrets(self) -> None:
        cases = {
            "duas linhas": f"{self.KEY}\n{self.SECRET}\n",
            "quatro linhas": f"{self.KEY}\n{self.SECRET}\n{self.PASSWORD}\nmais\n",
            "senha curta": f"{self.KEY}\n{self.SECRET}\ncurta\n",
            "senha com espaço": f"{self.KEY}\n{self.SECRET}\n{self.PASSWORD} x\n",
            "chave com espaço": f"{self.KEY}\n{self.SECRET} x\n{self.PASSWORD}\n",
        }
        for name, stdin in cases.items():
            with self.subTest(case=name):
                with self.assertRaises(secrets_cli.SecretsError) as raised:
                    self.write(stdin=stdin)
                self.assertNotIn(self.SECRET, str(raised.exception))
                self.assertNotIn(self.PASSWORD, str(raised.exception))
        for bucket, endpoint in (("Duora", self.ENDPOINT), ("duora-ok", "https://x.test"), ("duora-ok", "x.test")):
            with self.subTest(bucket=bucket, endpoint=endpoint), self.assertRaises(secrets_cli.SecretsError):
                self.write(bucket=bucket, endpoint=endpoint)

    def test_missing_secrets_directory_is_a_clear_error_for_both_commands(self) -> None:
        missing = Path(self._tmp.name) / "nao-existe"
        stdin_by_command = {
            "write-media-backup-repo": f"{self.KEY}\n{self.SECRET}\n{self.PASSWORD}\n",
            "write-backup-repo": f"{self.KEY}\n{self.SECRET}\n",
        }
        for command, text in stdin_by_command.items():
            with self.subTest(command=command):
                with mock.patch.object(secrets_cli.sys, "stdin") as stdin, \
                        contextlib.redirect_stderr(io.StringIO()) as stderr:
                    stdin.read.return_value = text
                    code = secrets_cli.main([command, str(missing), "--bucket", "duora-backup-exemplo",
                                             "--endpoint", self.ENDPOINT, "--no-chown"])

                self.assertEqual(1, code)
                self.assertIn("não existe", stderr.getvalue())
                self.assertNotIn(self.SECRET, stderr.getvalue())
                self.assertFalse(missing.exists())

    def test_command_reads_the_three_lines_from_stdin(self) -> None:
        with mock.patch.object(secrets_cli.sys, "stdin") as stdin:
            stdin.read.return_value = f"{self.KEY}\n{self.SECRET}\n{self.PASSWORD}\n"
            code = secrets_cli.main(["write-media-backup-repo", str(self.directory), "--bucket",
                                     "duora-backup-exemplo", "--endpoint", self.ENDPOINT, "--no-chown"])

        self.assertEqual(0, code)
        self.assertEqual("duora-backup-exemplo", duora_backup.read_media_settings(self.directory).settings.bucket)

if __name__ == "__main__":
    unittest.main()
