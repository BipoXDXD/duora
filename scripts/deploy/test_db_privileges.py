"""Privilégios de duora_app no PostgreSQL real da imagem derivada (infra/images/postgres).

Precisa de Docker e da imagem já construída; sem DUORA_POSTGRES_IMAGE o teste é pulado:

    docker buildx build -f infra/images/postgres/Dockerfile -t duora-postgres:test --load .
    DUORA_POSTGRES_IMAGE=duora-postgres:test python3 -m unittest discover -s scripts/deploy -p 'test_db_privileges.py' -v

Simula o que o migrador faz (objetos criados como duora_migrator, incluindo o histórico do EF Core e uma tabela
append-only com o REVOKE da convenção) e roda reconcile-privileges.sql como o deploy roda.
"""

from __future__ import annotations

import os
import subprocess
import tempfile
import time
import unittest
import uuid
from pathlib import Path

IMAGE = os.environ.get("DUORA_POSTGRES_IMAGE", "")
READY_TIMEOUT_SECONDS = 60
SECRETS = {
    "postgres_superuser_password": "superuser-synthetic",
    "db_migrator_password": "migrator-synthetic",
    "db_app_password": "app-synthetic",
}

# O que uma migration faz, como duora_migrator. audit_events segue a convenção de tabela append-only.
MIGRATION_SQL = """
SET ROLE duora_migrator;
CREATE SCHEMA ops;
CREATE TABLE ops.__ef_migrations_history (
  "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
INSERT INTO ops.__ef_migrations_history VALUES ('20261015175752_Baseline', '10.0.13');
CREATE SCHEMA rooms;
CREATE TABLE rooms.rooms (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);
CREATE TABLE ops.audit_events (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, action text NOT NULL);
REVOKE UPDATE, DELETE, TRUNCATE ON ops.audit_events FROM duora_app;
CREATE FUNCTION rooms.room_count() RETURNS bigint LANGUAGE sql AS 'SELECT count(*) FROM rooms.rooms';
"""


@unittest.skipUnless(IMAGE, "defina DUORA_POSTGRES_IMAGE com a imagem derivada (docker)")
class AppRolePrivilegesTests(unittest.TestCase):
    container = ""
    _secrets_dir: tempfile.TemporaryDirectory[str]

    @classmethod
    def setUpClass(cls) -> None:
        cls._secrets_dir = tempfile.TemporaryDirectory(prefix="duora-pg-test-")
        for name, value in SECRETS.items():
            path = Path(cls._secrets_dir.name) / name
            path.write_text(value, encoding="utf-8")
            path.chmod(0o644)  # dados sintéticos; o container (uid 999) precisa ler
        cls.container = f"duora-pg-privileges-{uuid.uuid4().hex[:8]}"
        _run(["docker", "run", "--detach", "--rm", "--name", cls.container, "--user", "999:999",
              "--volume", f"{cls._secrets_dir.name}:/run/secrets:ro",
              "--env", "POSTGRES_PASSWORD_FILE=/run/secrets/postgres_superuser_password",
              "--env", "DUORA_MIGRATOR_PASSWORD_FILE=/run/secrets/db_migrator_password",
              "--env", "DUORA_APP_PASSWORD_FILE=/run/secrets/db_app_password",
              IMAGE])
        cls._wait_ready()
        _run(cls._psql_superuser() + ["--command", MIGRATION_SQL])
        _run(cls._psql_superuser() + ["--file", "/usr/local/share/duora/reconcile-privileges.sql"])

    @classmethod
    def tearDownClass(cls) -> None:
        if cls.container:
            subprocess.run(["docker", "rm", "--force", cls.container], capture_output=True, check=False)
        cls._secrets_dir.cleanup()

    @classmethod
    def _wait_ready(cls) -> None:
        deadline = time.monotonic() + READY_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            probe = subprocess.run(["docker", "exec", cls.container, "pg_isready", "-h", "127.0.0.1", "-U", "postgres"],
                                   capture_output=True, check=False)
            if probe.returncode == 0:
                return
            time.sleep(1)
        raise AssertionError("PostgreSQL não ficou pronto")

    @classmethod
    def _psql_superuser(cls) -> list[str]:
        return ["docker", "exec", cls.container, "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1",
                "--username", "postgres", "--dbname", "duora"]

    def as_app(self, sql: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["docker", "exec", "--env", f"PGPASSWORD={SECRETS['db_app_password']}", self.container, "psql",
             "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "--host", "127.0.0.1", "--username", "duora_app",
             "--dbname", "duora", "--tuples-only", "--no-align", "--command", sql],
            capture_output=True, text=True, check=False)

    def assert_denied(self, sql: str) -> None:
        result = self.as_app(sql)
        self.assertNotEqual(0, result.returncode, f"devia ser negado: {sql}")
        self.assertIn("permission denied", result.stderr, f"{sql}: {result.stderr}")

    def assert_allowed(self, sql: str) -> str:
        result = self.as_app(sql)
        self.assertEqual(0, result.returncode, f"devia ser permitido: {sql}: {result.stderr}")
        return result.stdout.strip()

    def test_app_cannot_touch_migration_history(self) -> None:
        for sql in [
            "UPDATE ops.__ef_migrations_history SET \"ProductVersion\" = 'x'",
            "DELETE FROM ops.__ef_migrations_history",
            "INSERT INTO ops.__ef_migrations_history VALUES ('29990101000000_Fake', 'x')",
            "SELECT * FROM ops.__ef_migrations_history",
        ]:
            with self.subTest(sql=sql):
                self.assert_denied(sql)

    def test_app_cannot_run_ddl(self) -> None:
        for sql in [
            "CREATE TABLE rooms.intruder (id int)",
            "CREATE TABLE ops.intruder (id int)",
            "CREATE TABLE public.intruder (id int)",
            "CREATE SCHEMA intruder",
            "CREATE TEMP TABLE intruder (id int)",
            "TRUNCATE rooms.rooms",
        ]:
            with self.subTest(sql=sql):
                self.assert_denied(sql)

    def test_append_only_table_accepts_only_insert_and_select(self) -> None:
        self.assert_allowed("INSERT INTO ops.audit_events (action) VALUES ('auth.login_succeeded')")
        self.assertNotEqual("0", self.assert_allowed("SELECT count(*) FROM ops.audit_events"))
        for sql in ["UPDATE ops.audit_events SET action = 'x'", "DELETE FROM ops.audit_events",
                    "TRUNCATE ops.audit_events"]:
            with self.subTest(sql=sql):
                self.assert_denied(sql)

    def test_app_has_full_dml_on_business_tables(self) -> None:
        self.assert_allowed("INSERT INTO rooms.rooms (name) VALUES ('sala 1')")
        self.assert_allowed("UPDATE rooms.rooms SET name = 'sala 2' WHERE name = 'sala 1'")
        self.assertEqual("sala 2", self.assert_allowed("SELECT name FROM rooms.rooms WHERE name = 'sala 2'"))
        self.assert_allowed("DELETE FROM rooms.rooms WHERE name = 'sala 2'")

    def test_functions_are_executable_by_app_but_not_by_public(self) -> None:
        self.assertEqual("t", self.assert_allowed("SELECT has_function_privilege('duora_app', "
                                                  "'rooms.room_count()', 'EXECUTE')"))
        self.assertEqual("f", self.assert_allowed("SELECT has_function_privilege('public', "
                                                  "'rooms.room_count()', 'EXECUTE')"))


def _run(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise AssertionError(f"{' '.join(command[:4])} falhou: {result.stderr.strip()[-800:]}")


if __name__ == "__main__":
    unittest.main()
