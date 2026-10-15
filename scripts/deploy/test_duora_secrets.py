"""Testes do gerador de segredos. Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v"""

import stat
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


if __name__ == "__main__":
    unittest.main()
