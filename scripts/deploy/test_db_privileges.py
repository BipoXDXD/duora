"""Privilégios de duora_app no PostgreSQL real da imagem derivada (infra/images/postgres).

Precisa de Docker e da imagem já construída; sem DUORA_POSTGRES_IMAGE o teste é pulado:

    docker buildx build -f infra/images/postgres/Dockerfile -t duora-postgres:test --load .
    DUORA_POSTGRES_IMAGE=duora-postgres:test python3 -m unittest discover -s scripts/deploy -p 'test_db_privileges.py' -v

Simula o que o migrador faz (objetos criados como duora_migrator, incluindo o histórico do EF Core e uma tabela
append-only com o REVOKE da convenção) e roda reconcile-privileges.sql como o deploy roda.
"""

from __future__ import annotations

import os
import hashlib
import json
import re
import subprocess
import tempfile
import time
import unittest
import uuid
from pathlib import Path

import duora_deploy as deploy
import reconcile_payload as payload

IMAGE = os.environ.get("DUORA_POSTGRES_IMAGE", "")
READY_TIMEOUT_SECONDS = 60
SECRETS = {
    "postgres_superuser_password": "superuser-synthetic",
    "db_migrator_password": "migrator-synthetic",
    "db_app_password": "app-synthetic",
    "db_backup_password": "backup-synthetic",
}

# O que uma migration faz, como duora_migrator. audit_events nasce aqui SEM o REVOKE da convenção: o teste prova que
# reconcile-privileges.sql sozinho deixa a tabela append-only.
MIGRATION_SQL = """
SET ROLE duora_migrator;
CREATE SCHEMA ops;
CREATE TABLE ops.__ef_migrations_history (
  "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
INSERT INTO ops.__ef_migrations_history VALUES ('20261015175752_Baseline', '10.0.13');
CREATE SCHEMA rooms;
CREATE TABLE rooms.rooms (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);
CREATE TABLE ops.audit_events (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, action text NOT NULL);
CREATE FUNCTION rooms.room_count() RETURNS bigint LANGUAGE sql AS 'SELECT count(*) FROM rooms.rooms';
"""


@unittest.skipUnless(IMAGE, "native image proof runs with DUORA_POSTGRES_IMAGE in both CI architectures")
class MigratorPayloadImageTests(unittest.TestCase):
    def test_real_stopped_image_payload_matches_canonical_sql(self) -> None:
        commands: list[list[str]] = []
        observations: list[dict] = []
        archives: list[dict] = []

        def capture(command: list[str]) -> bytes:
            commands.append(command)
            result = payload.capture_binary(command)
            if command[:3] == ["docker", "container", "inspect"]:
                item = json.loads(result)
                observations.append({"id": item["Id"], "image": item["Image"],
                                     "state": item["State"], "mounts": item["Mounts"],
                                     "network": item["HostConfig"]["NetworkMode"]})
            if command[:2] == ["docker", "cp"]:
                archives.append({"path": command[-2].split(":", 1)[1], "bytes": len(result),
                                 "sha256": hashlib.sha256(result).hexdigest()})
            return result

        image = payload._inspect("image", "duora-migrator:ci", capture)
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
        self.assertRegex(revision, r"^[a-f0-9]{40}$")
        self.assertEqual(image["Config"]["Labels"]["org.opencontainers.image.revision"], revision)
        self.assertEqual(image["Config"]["Labels"]["org.opencontainers.image.title"], "duora-migrator")
        self.assertEqual(image["Config"]["Labels"]["org.opencontainers.image.source"],
                         "https://github.com/BipoXDXD/duora")
        self.assertIn(image["Config"].get("Volumes"), [None, {}])
        expected_arch = {"x86_64": "amd64", "aarch64": "arm64"}[os.uname().machine]
        self.assertEqual(image["Architecture"], expected_arch)
        self.assertEqual(image["Os"], "linux")
        image_id = image["Id"]
        self.assertTrue(re.fullmatch(r"sha256:[a-f0-9]{64}", image_id))
        sql = payload._copy_stopped_sql(image_id, image_id, capture=capture)
        canonical = Path(__file__).resolve().parents[2] / "infra/images/dotnet/reconcile-privileges.sql"
        self.assertEqual(sql.encode("utf-8"), canonical.read_bytes())
        self.assertTrue(observations)
        self.assertEqual(len(archives), 2)
        for item in observations:
            self.assertEqual(item["image"], image_id)
            self.assertFalse(item["state"]["Running"])
            self.assertEqual(item["state"]["Status"], "created")
            self.assertTrue(item["state"]["StartedAt"].startswith("0001-01-01"))
            self.assertEqual(item["mounts"], [])
            self.assertEqual(item["network"], "none")
        self.assertFalse(any(c[:2] in [["docker", "start"], ["docker", "run"], ["docker", "exec"]]
                             for c in commands))
        self.assertTrue(any(c[:2] == ["docker", "rm"] for c in commands))
        with self.assertRaises(payload.PayloadError):
            payload.capture_binary(["docker", "container", "inspect", observations[-1]["id"]])
        print("[reconcile-native] " + json.dumps({"origin": "Actions candidate build, not published GHCR",
              "revision": revision, "architecture": expected_arch, "image_id": image_id,
              "sql_sha256": hashlib.sha256(sql.encode()).hexdigest(), "archives": archives,
              "container_observations": observations, "owned_container_removed": True}))


@unittest.skipUnless(IMAGE, "defina DUORA_POSTGRES_IMAGE com a imagem derivada (docker)")
class AppRolePrivilegesTests(unittest.TestCase):
    container = ""
    _secrets_dir: tempfile.TemporaryDirectory[str]

    @classmethod
    def setUpClass(cls) -> None:
        cls._secrets_dir = tempfile.TemporaryDirectory(prefix="duora-pg-test-")
        # mkdtemp cria 0700 do usuário do teste; o postgres do container (uid 999) precisa atravessar o diretório.
        Path(cls._secrets_dir.name).chmod(0o755)
        for name, value in SECRETS.items():
            path = Path(cls._secrets_dir.name) / name
            path.write_text(value, encoding="utf-8")
            path.chmod(0o644)  # dados sintéticos; o container (uid 999) precisa ler
        cls.container = f"duora-pg-privileges-{uuid.uuid4().hex[:8]}"
        _run(["docker", "run", "--detach", "--name", cls.container, "--user", "999:999",
              "--volume", f"{cls._secrets_dir.name}:/run/secrets:ro",
              "--env", "POSTGRES_PASSWORD_FILE=/run/secrets/postgres_superuser_password",
              "--env", "DUORA_MIGRATOR_PASSWORD_FILE=/run/secrets/db_migrator_password",
              "--env", "DUORA_APP_PASSWORD_FILE=/run/secrets/db_app_password",
              "--env", "DUORA_BACKUP_PASSWORD_FILE=/run/secrets/db_backup_password",
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
        logs = subprocess.run(["docker", "logs", "--tail", "30", cls.container], capture_output=True, text=True,
                              check=False)
        raise AssertionError(f"PostgreSQL não ficou pronto:\n{logs.stdout}{logs.stderr}")

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

    def test_reconcile_fails_when_app_can_still_rewrite_the_audit_trail(self) -> None:
        # Privilégio herdado (PUBLIC) não sai com o REVOKE de duora_app: a checagem final tem de barrar o deploy.
        _run(self._psql_superuser() + ["--command", "GRANT UPDATE ON ops.audit_events TO PUBLIC"])
        try:
            result = subprocess.run(
                self._psql_superuser() + ["--file", "/usr/local/share/duora/reconcile-privileges.sql"],
                capture_output=True, text=True, check=False)
            self.assertNotEqual(0, result.returncode, "o reconcile devia falhar")
            self.assertIn("append-only", result.stderr)
        finally:
            _run(self._psql_superuser() + ["--command", "REVOKE UPDATE ON ops.audit_events FROM PUBLIC"])

    def test_app_has_full_dml_on_business_tables(self) -> None:
        self.assert_allowed("INSERT INTO rooms.rooms (name) VALUES ('sala 1')")
        self.assert_allowed("UPDATE rooms.rooms SET name = 'sala 2' WHERE name = 'sala 1'")
        self.assertEqual("sala 2", self.assert_allowed("SELECT name FROM rooms.rooms WHERE name = 'sala 2'"))
        self.assert_allowed("DELETE FROM rooms.rooms WHERE name = 'sala 2'")

    def test_app_cannot_read_server_files_or_run_programs(self) -> None:
        # Spec 18.3: a aplicação não chega ao backup. Pelo SQL, o único caminho até os certificados do pgBackRest
        # (/run/secrets) ou a um comando seria ler arquivo ou executar programa no servidor.
        for sql in [
            "SELECT pg_read_file('/run/secrets/postgres_superuser_password')",
            "COPY rooms.rooms (name) FROM '/etc/pgbackrest/pgbackrest.conf'",
            "COPY rooms.rooms (name) FROM PROGRAM 'pgbackrest info'",
        ]:
            with self.subTest(sql=sql):
                self.assert_denied(sql)

    def test_rollback_guard_counts_every_account_without_mfa_that_is_not_only_participant(self) -> None:
        # Tabelas do Identity no formato real (roles.name anulável) num schema próprio do teste. Um papel novo que só
        # existe no C# (auditor) e um sem nome contam como privilegiados: a guarda não pode depender de uma lista.
        roles = {"participant": "11111111-0000-0000-0000-000000000001", "moderator": "11111111-0000-0000-0000-000000000002",
                 "operator": "11111111-0000-0000-0000-000000000003", "auditor": "11111111-0000-0000-0000-000000000004",
                 None: "11111111-0000-0000-0000-000000000005"}
        accounts = [  # (id, MFA, papéis); contam 2, 3, 4, 6 e 7
            ("22222222-0000-0000-0000-000000000001", False, ["participant"]),
            ("22222222-0000-0000-0000-000000000002", False, ["moderator"]),
            ("22222222-0000-0000-0000-000000000003", False, ["auditor"]),
            ("22222222-0000-0000-0000-000000000004", False, [None]),
            ("22222222-0000-0000-0000-000000000005", True, ["moderator"]),
            ("22222222-0000-0000-0000-000000000006", False, ["participant", "auditor"]),
            ("22222222-0000-0000-0000-000000000007", False, ["moderator", "operator"]),
            ("22222222-0000-0000-0000-000000000008", False, []),
        ]
        statements = [
            "CREATE SCHEMA identity",
            "CREATE TABLE identity.roles (id uuid PRIMARY KEY, name varchar(256) NULL)",
            "CREATE TABLE identity.users (id uuid PRIMARY KEY, two_factor_enabled boolean NOT NULL)",
            "CREATE TABLE identity.user_roles (user_id uuid NOT NULL REFERENCES identity.users, "
            "role_id uuid NOT NULL REFERENCES identity.roles, PRIMARY KEY (user_id, role_id))",
        ]
        statements += [f"INSERT INTO identity.roles VALUES ('{role_id}', {'NULL' if name is None else repr(name)})"
                       for name, role_id in roles.items()]
        for user_id, mfa, names in accounts:
            statements.append(f"INSERT INTO identity.users VALUES ('{user_id}', {str(mfa).lower()})")
            statements += [f"INSERT INTO identity.user_roles VALUES ('{user_id}', '{roles[name]}')" for name in names]
        _run(self._psql_superuser() + ["--command", "; ".join(statements)])
        self.addCleanup(_run, self._psql_superuser() + ["--command", "DROP SCHEMA identity CASCADE"])

        self.assertEqual("5", self.superuser_query(deploy.PRIVILEGED_ACCOUNTS_WITHOUT_MFA_SQL))

    def superuser_query(self, sql: str) -> str:
        result = subprocess.run(self._psql_superuser() + ["--tuples-only", "--no-align", "--command", sql],
                                capture_output=True, text=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def connect_as_backup(self, *, host: str | None, password: str | None) -> subprocess.CompletedProcess[str]:
        env = ["--env", f"PGPASSWORD={password}"] if password is not None else []
        target = ["--host", host] if host is not None else []
        return subprocess.run(["docker", "exec", *env, self.container, "psql", "--no-psqlrc", "--no-password",
                               *target, "--username", "duora_backup", "--dbname", "postgres",
                               "--command", "select 1"], capture_output=True, text=True, check=False)

    def test_no_trust_rule_in_the_active_pg_hba(self) -> None:
        self.assertEqual("/etc/postgresql/pg_hba.duora.conf", self.superuser_query("show hba_file"))
        self.assertEqual("0", self.superuser_query(
            "select count(*) from pg_hba_file_rules where auth_method = 'trust' or error is not null"))

    def test_geo_maintenance_uses_password_only_on_duora_without_app_membership(self) -> None:
        _run(self._psql_superuser() + ["--command", """
            CREATE ROLE duora_geo_maintenance LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
                NOREPLICATION NOBYPASSRLS PASSWORD 'maintenance-synthetic';
            GRANT CONNECT ON DATABASE duora TO duora_geo_maintenance;
            """])

        def connect(password: str, database: str, sql: str) -> subprocess.CompletedProcess[str]:
            return subprocess.run(
                ["docker", "exec", "--env", f"PGPASSWORD={password}", self.container, "psql",
                 "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "--host", "127.0.0.1",
                 "--username", "duora_geo_maintenance", "--dbname", database,
                 "--tuples-only", "--no-align", "--command", sql],
                capture_output=True, text=True, check=False)

        allowed = connect("maintenance-synthetic", "duora", "select current_user")
        self.assertEqual(0, allowed.returncode, allowed.stderr)
        self.assertEqual("duora_geo_maintenance", allowed.stdout.strip())
        wrong_password = connect("incorrect-synthetic", "duora", "select 1")
        self.assertNotEqual(0, wrong_password.returncode)
        self.assertIn("password authentication failed", wrong_password.stderr)
        other_database = connect("maintenance-synthetic", "postgres", "select 1")
        self.assertNotEqual(0, other_database.returncode)
        self.assertIn("pg_hba.conf rejects connection", other_database.stderr)
        forbidden_table = connect("maintenance-synthetic", "duora", "select * from rooms.rooms")
        self.assertNotEqual(0, forbidden_table.returncode)
        self.assertIn("permission denied", forbidden_table.stderr)
        self.assert_denied("set role duora_geo_maintenance")

    def test_backup_role_is_not_superuser_and_has_only_backup_privileges(self) -> None:
        self.assertEqual("f|f|f|f", self.superuser_query(
            "select rolsuper, rolcreaterole, rolcreatedb, rolreplication from pg_roles "
            "where rolname = 'duora_backup'"))
        self.assertEqual("t", self.superuser_query("select pg_has_role('duora_backup', 'pg_read_all_settings', "
                                                   "'member')"))
        self.assertEqual("f", self.superuser_query("select has_database_privilege('duora_backup', 'duora', "
                                                   "'CONNECT')"))
        self.assertEqual("f", self.superuser_query("select pg_has_role('duora_backup', 'pg_read_server_files', "
                                                   "'member')"))

    def test_backup_role_without_password_is_refused(self) -> None:
        result = self.connect_as_backup(host="127.0.0.1", password=None)

        self.assertNotEqual(0, result.returncode)

    def test_backup_role_is_refused_outside_the_backup_container_address(self) -> None:
        # Senha certa, origem errada (127.0.0.1 não é 172.30.21.10): o pg_hba recusa antes da senha.
        result = self.connect_as_backup(host="127.0.0.1", password=SECRETS["db_backup_password"])

        self.assertNotEqual(0, result.returncode)
        self.assertIn("rejects connection", result.stderr)

    def test_backup_role_is_refused_on_the_local_socket(self) -> None:
        result = self.connect_as_backup(host=None, password=SECRETS["db_backup_password"])

        self.assertNotEqual(0, result.returncode)
        self.assertIn("rejects connection", result.stderr)

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
