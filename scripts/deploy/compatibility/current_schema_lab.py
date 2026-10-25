"""Actions-only synthetic OLDWEB3d99/schema48 functional lab. Never a product deploy entry point."""

import argparse
import hashlib
import http.client
import importlib.util
import json
import os
import platform
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import uuid
from http.cookies import SimpleCookie
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from old_image_identity import bind_old_image
from current_schema_receipt import (
    check_history,
    check_preservation,
    need,
    validate_receipt,
)

LABEL = "io.duora.current-schema-lab"
SCOPE = "old_web_schema48_function_only_not_executor_recovery"
MAX_OUTPUT = 2 * 1024 * 1024


class InjectedPostDdlFailure(Exception):
    pass


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def command(argv, *, text=None, timeout=180):
    result = subprocess.run(
        argv, input=text, capture_output=True, text=True, check=False, timeout=timeout
    )
    if result.returncode:
        # Never emit SQL, configuration, HTTP payload, credentials or container environment.
        raise RuntimeError(
            f"lab command failed: {Path(argv[0]).name} exit={result.returncode}"
        )
    need(len(result.stdout.encode()) <= MAX_OUTPUT, "command output exceeded bound")
    return result.stdout.strip()


def git(repo, *args):
    return command(
        ["/usr/bin/git", "-c", "safe.directory=" + str(repo), "-C", str(repo), *args]
    )


def require_actions():
    need(
        sys.platform == "linux"
        and platform.machine() == "x86_64"
        and os.geteuid() == 0,
        "refused: disposable Linux amd64/root runner required",
    )
    need(sys.version_info[:2] == (3, 12), "Python3.12 required")
    need(
        os.environ.get("GITHUB_ACTIONS") == "true"
        and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
        and os.environ.get("GITHUB_REPOSITORY") == "BipoXDXD/duora"
        and os.environ.get("GITHUB_EVENT_NAME") == "pull_request",
        "hosted Actions PR260 required",
    )
    event_path = Path(os.environ["GITHUB_EVENT_PATH"])
    need(event_path.stat().st_size <= MAX_OUTPUT, "event metadata too large")
    event = json.loads(event_path.read_text())
    need(event.get("number") == 260, "only existing PR260 is eligible")
    for name in ("GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"):
        need(
            os.environ.get(name, "").isdigit() and int(os.environ[name]) > 0,
            "missing Actions run identity",
        )
    need(
        all(
            len(os.environ.get(name, "")) == 40
            for name in ("GITHUB_WORKFLOW_SHA", "GITHUB_SHA")
        ),
        "missing actual workflow/merge identity",
    )
    memory = dict(
        line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines()
    )
    need(
        int(memory["MemTotal"].split()[0]) >= 6 * 1024 * 1024,
        "runner memory insufficient; no fallback to local runtime",
    )
    return event["pull_request"]["head"]["sha"]


class Browser:
    def __init__(self, port):
        self.port, self.cookies = port, {}
        self.origin = f"https://localhost:{port}"
        # Private localhost fixture uses the existing Caddy tls-internal contract only.
        self.context = ssl._create_unverified_context()

    def request(self, method, path, body=None, extra=None):
        headers = {"Origin": self.origin, "Sec-Fetch-Site": "same-origin"}
        if self.cookies:
            headers["Cookie"] = "; ".join(
                key + "=" + value for key, value in self.cookies.items()
            )
        if method not in {"GET", "HEAD"}:
            need("XSRF-TOKEN" in self.cookies, "missing real antiforgery issuance")
            headers["X-XSRF-TOKEN"] = self.cookies["XSRF-TOKEN"]
        if body is not None:
            body = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        headers.update(extra or {})
        connection = http.client.HTTPSConnection(
            "localhost", self.port, context=self.context, timeout=10
        )
        try:
            connection.request(method, path, body=body, headers=headers)
            response = connection.getresponse()
            raw = response.read(65537)
            need(len(raw) <= 65536, "HTTP response exceeded bound")
            for key, value in response.getheaders():
                if key.lower() == "set-cookie":
                    parsed = SimpleCookie()
                    parsed.load(value)
                    for name, morsel in parsed.items():
                        if morsel["max-age"] == "0":
                            self.cookies.pop(name, None)
                        else:
                            self.cookies[name] = morsel.value
            return (
                response.status,
                dict((key.lower(), value) for key, value in response.getheaders()),
                raw,
            )
        finally:
            connection.close()

    def csrf(self):
        status, _, _ = self.request("GET", "/api/v1/auth/csrf")
        need(
            status == 204 and self.cookies.get("XSRF-TOKEN"),
            "real CSRF endpoint failed",
        )


class Lab:
    def __init__(self, arguments, workflow_head):
        self.args, self.workflow_head = arguments, workflow_head
        self.pins = json.loads(
            Path(__file__).with_name("current_schema_pins.json").read_text()
        )
        self.nonce = uuid.uuid4().hex
        self.prefix = "duora259-compat-" + self.nonce
        self.created = {"container": [], "volume": [], "network": [], "image": []}
        self.session = None
        self.root = None
        self.schema_state = 0
        self.sql_confirmed = False
        self.helper_counts = {
            "quiescent_calls": 0,
            "execute_sql_calls": 0,
            "acl_witness_calls": 0,
        }
        self.compose_transport = ["OWNED_LAB_COMPOSE", self.nonce]
        self.monitor_name = "duora-compat-sentinel-" + self.nonce
        self.pg, self.web, self.edge = (
            self.prefix + "-" + role for role in ("postgres", "web", "edge")
        )
        self.network, self.pgdata, self.keys = (
            self.prefix + "-" + role for role in ("net", "pgdata", "keys")
        )
        self.report = {
            "scope": SCOPE,
            "success": False,
            "requested_schema_states": [35, 48],
            "qualified_schema_states": [],
        }

    def docker(self, *args, **kwargs):
        return command(["/usr/bin/docker", *args], **kwargs)

    def inspect(self, kind, name):
        return json.loads(self.docker(kind, "inspect", name))[0]

    def owned(self, kind, name):
        item = self.inspect(kind, name)
        labels = (
            item.get("Config", {}).get("Labels", {})
            if kind in {"container", "image"}
            else item.get("Labels", {})
        )
        need(
            labels and labels.get(LABEL) == self.nonce,
            "refused to mutate foreign lab resource",
        )
        return item

    def create(self, kind, name, *args):
        self.created[kind].append(name)
        if kind in {"volume", "network"}:
            return self.docker(
                kind, "create", "--label", LABEL + "=" + self.nonce, *args, name
            )
        raise ValueError("unsupported resource creation")

    def run(self, name, options, image, *args, detached=False, timeout=180):
        if name not in self.created["container"]:
            self.created["container"].append(name)
        return self.docker(
            "run",
            "--name",
            name,
            "--label",
            LABEL + "=" + self.nonce,
            *(["--detach"] if detached else ["--rm"]),
            "--memory",
            "1536m" if name == self.pg else "768m",
            "--cpus",
            "1.5",
            "--pids-limit",
            "256",
            "--cap-drop",
            "ALL",
            "--security-opt",
            "no-new-privileges:true",
            *options,
            image,
            *args,
            timeout=timeout,
        )

    def load_product_sql_helpers(self):
        path = self.args.workflow_source / "scripts/deploy/duora_deploy.py"
        spec = importlib.util.spec_from_file_location(
            "duora_reviewed_sql_helpers", path
        )
        assert spec is not None and spec.loader is not None
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)
        for name in (
            "_recovery_require_quiescent",
            "_recovery_execute_sql",
            "_recovery_acl_witness",
        ):
            need(
                callable(getattr(module, name, None)),
                "reviewed SQL helper interface missing; new M2 head required",
            )
        self.helpers = module
        self.report["source"]["sql_helper_sidecars_sha256"] = {
            name: digest((path.parent / name).read_bytes())
            for name in ("runtime_guard.py", "reconcile_payload.py")
        }

    def helper_runner(self, argv, *, capture=False, input_text=None):
        # Transport mapping only: preserve actual psql argv, SQL input, stdout, stderr and returncode.
        # No synthetic command results, source monkeypatches, filter rewriting or guard exclusions.
        prefix = self.compose_transport + ["exec", "--no-TTY", "postgres", "psql"]
        if argv[: len(prefix)] == prefix:
            self.owned("container", self.pg)
            actual = [
                "/usr/bin/docker",
                "exec",
                "--interactive",
                "--user",
                "999:999",
                self.pg,
                "psql",
                *argv[len(prefix) :],
            ]
        elif argv and argv[0] == "docker" and argv[1] in {"ps", "inspect", "top"}:
            actual = ["/usr/bin/docker", *argv[1:]]
        else:
            raise ValueError("helper transport refused unexpected command")
        result = subprocess.run(
            actual,
            input=input_text,
            text=True,
            capture_output=True,
            check=False,
            timeout=45,
        )
        need(
            len(result.stdout.encode()) + len(result.stderr.encode()) <= MAX_OUTPUT,
            "helper output exceeded bound",
        )
        return result

    def helper_quiescent(self):
        self.helpers._recovery_require_quiescent(
            self.compose_transport,
            self.helper_runner,
            self.owned("container", self.pg)["Id"],
        )
        self.helper_counts["quiescent_calls"] += 1

    def helper_acl(self, *, unsafe=False):
        self.helper_counts["acl_witness_calls"] += 1
        if not unsafe:
            self.helpers._recovery_acl_witness(
                self.compose_transport, self.helper_runner
            )
            return
        try:
            self.helpers._recovery_acl_witness(
                self.compose_transport, self.helper_runner
            )
        except self.helpers.DeployError as error:
            need(
                str(error)
                == "Effective schema48 privileges are unsafe, incomplete or unknown",
                "adversarial ACL reached an unrelated helper failure",
            )
        else:
            raise ValueError("actual executor ACL witness accepted adversarial grants")

    def sql(self, text, *, app=False):
        options = ["--env", "PGPASSFILE=/run/secrets/lab_app_pgpass"] if app else []
        connection = (
            ["-h", "127.0.0.1", "-U", "duora_app"] if app else ["-U", "postgres"]
        )
        return self.docker(
            "exec",
            "--interactive",
            "--user",
            "999:999",
            *options,
            self.pg,
            "psql",
            "--no-psqlrc",
            "--quiet",
            "-At",
            "-v",
            "ON_ERROR_STOP=1",
            "-d",
            "duora",
            *connection,
            text=text,
            timeout=30,
        )

    def history(self):
        return json.loads(
            self.sql(
                'select coalesce(json_agg("MigrationId" order by "MigrationId"),\'[]\'::json) from ops.__ef_migrations_history;'
            )
        )

    def database(self):
        item = self.owned("container", self.pg)
        return {
            "id": item["Id"],
            "image": item["Image"],
            "started_at": item["State"]["StartedAt"],
            "restart_count": item["RestartCount"],
            "postmaster_start": self.sql("select pg_postmaster_start_time();"),
            "mounts": sorted(item["Mounts"], key=lambda mount: mount["Destination"]),
        }

    def start_readonly_session(self):
        self.report["stage"] = "readonly_duora_app_pgbench_driver"
        try:
            version = self.docker(
                "exec", "--user", "999:999", self.pg, "pgbench", "--version"
            )
        except RuntimeError as error:
            raise ValueError(
                "pinned PG image lacks working pgbench18.6; no install/fallback"
            ) from error
        need(
            "18.6" in version,
            "pinned PG lacks pgbench18.6; no installation/fallback allowed",
        )
        self.report["readonly_session_driver_version"] = version
        self.session = subprocess.Popen(
            [
                "/usr/bin/docker",
                "exec",
                "--user",
                "999:999",
                "--env",
                "PGPASSFILE=/run/secrets/lab_app_pgpass",
                "--env",
                "PGAPPNAME=" + self.monitor_name,
                "--env",
                "PGOPTIONS=-c default_transaction_read_only=on",
                self.pg,
                "pgbench",
                "--no-vacuum",
                "--client=1",
                "--jobs=1",
                "--time=1800",
                "--rate=2",
                "--protocol=simple",
                "--file=/run/lab/sentinel.sql",
                "--host=127.0.0.1",
                "--username=duora_app",
                "duora",
            ],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            need(self.session.poll() is None, "readonly app session driver exited")
            found = self.session_identity(required=False)
            if found is not None:
                return found
            time.sleep(0.2)
        raise ValueError("readonly app session did not appear")

    def session_identity(self, *, required=True):
        need(
            self.session is not None and self.session.poll() is None,
            "persistent readonly app session died",
        )
        records = json.loads(
            self.sql(
                "select coalesce(json_agg(json_build_object('pid',pid,'backend_start',backend_start)), '[]'::json) "
                "from pg_stat_activity where datname=current_database() and usename='duora_app' and application_name='"
                + self.monitor_name
                + "';"
            )
        )
        if not required and not records:
            return None
        need(
            len(records) == 1
            and type(records[0]["pid"]) is int
            and records[0]["backend_start"],
            "readonly app session identity unknown or reconnected",
        )
        return records[0]

    def sentinel_value(self):
        return self.sql(
            "select id||':'||payload from ops.current_schema_lab_sentinel order by id;",
            app=True,
        )

    def schema_digest(self):
        raw = self.docker(
            "exec",
            "--user",
            "999:999",
            self.pg,
            "pg_dump",
            "-U",
            "postgres",
            "-d",
            "duora",
            "--schema-only",
            "--no-owner",
            timeout=45,
        )
        # PostgreSQL18 pg_dump randomizes its psql restrict nonce. Keep all DDL/grants; remove only this transport nonce.
        normalized = "\n".join(
            line
            for line in raw.splitlines()
            if not line.startswith(("\\restrict ", "\\unrestrict "))
        )
        return digest(normalized.encode())

    def verify_sources(self):
        self.report["stage"] = "source_identity_preflight"
        pins = self.pins
        for path, commit, tree in (
            (self.args.cohort, pins["cohort_commit"], pins["cohort_tree"]),
            (self.args.old_source, pins["old_commit"], pins["old_tree"]),
        ):
            need(
                git(path, "rev-parse", "HEAD") == commit
                and git(path, "rev-parse", "HEAD^{tree}") == tree,
                "source checkout commit/tree differs",
            )
            need(
                not git(path, "status", "--porcelain", "--untracked-files=no"),
                "tracked source modified",
            )
        need(
            git(self.args.workflow_source, "rev-parse", "HEAD") == self.workflow_head,
            "workflow checkout differs from actual PR260 head",
        )
        need(
            not git(
                self.args.workflow_source,
                "status",
                "--porcelain",
                "--untracked-files=no",
            ),
            "workflow tracked source modified",
        )
        for name, expected in pins["cohort_input_sha256"].items():
            need(
                digest((self.args.cohort / name).read_bytes()) == expected,
                "cohort fixture identity differs",
            )
        old_sql = self.args.old_source / pins["canonical_sql_path"]
        need(
            digest(old_sql.read_bytes()) == pins["historical_sql_sha256"],
            "old canonical SQL identity differs",
        )
        self.report.setdefault("source", {}).update(
            {
                key: pins[key]
                for key in (
                    "cohort_commit",
                    "cohort_tree",
                    "old_commit",
                    "canonical_sql_sha256",
                )
            }
        )
        self.report["source"].update(
            workflow_head=self.workflow_head,
            executor_source_sha256=digest(
                (
                    self.args.workflow_source / "scripts/deploy/duora_deploy.py"
                ).read_bytes()
            ),
            harness_sha256=digest(Path(__file__).read_bytes()),
        )

    def prepare(self):
        self.verify_sources()
        self.load_product_sql_helpers()
        need(
            self.docker("version", "--format", "{{.Server.Arch}}") == "amd64",
            "native Docker amd64 required",
        )
        self.root = Path(
            tempfile.mkdtemp(prefix=self.prefix + "-", dir=os.environ["RUNNER_TEMP"])
        )
        self.root.chmod(0o700)
        docker_config = self.root / "docker-config"
        docker_config.mkdir(mode=0o700)
        os.environ["DOCKER_CONFIG"] = str(docker_config)
        need(
            not self.docker(
                "ps",
                "--all",
                "--quiet",
                "--filter",
                "label=com.docker.compose.project=duora",
            ),
            "foreign duora project present on disposable runner",
        )
        self.report["stage"] = "immutable_old_image_acquisition"
        self.report["observed_images"] = {}
        for role in ("web", "worker", "edge", "migrator", "postgres"):
            pin = self.pins["old_images"][role]
            self.docker(
                "pull",
                "--quiet",
                "--platform",
                "linux/amd64",
                pin["reference"],
                timeout=240,
            )
            item = self.inspect("image", pin["reference"])
            raw_manifest = (self.args.workflow_source / "scripts/deploy/compatibility/old-oci" / (role + ".json")).read_bytes()
            self.report["observed_images"][role] = bind_old_image(raw_manifest, item, pin, role=role, revision=self.pins["old_commit"])
        self.report["stage"] = "incoming_migrator_only_build"
        self.migrator = self.prefix + "-migrator48"
        self.created["image"].append(self.migrator)
        self.docker(
            "build",
            "--quiet",
            "--platform",
            "linux/amd64",
            "--target",
            "migrator",
            "--build-arg",
            "GIT_COMMIT=" + self.pins["cohort_commit"],
            "--label",
            LABEL + "=" + self.nonce,
            "--tag",
            self.migrator,
            "--file",
            str(self.args.cohort / "infra/images/dotnet/Dockerfile"),
            str(self.args.cohort),
            timeout=1200,
        )
        item = self.owned("image", self.migrator)
        need(
            item["Config"]["Labels"].get("org.opencontainers.image.revision")
            == self.pins["cohort_commit"],
            "new migrator source differs",
        )
        self.report["new_migrator"] = {
            "docker_image_id": item["Id"],
            "source_head": self.pins["cohort_commit"],
        }
        need(
            item["Architecture"] == "amd64" and item["Os"] == "linux",
            "new migrator platform differs",
        )
        self.migrator = item[
            "Id"
        ]  # Execute the actual immutable build object, never a mutable local tag.
        self.report["stage"] = "synthetic_secrets_tls_and_filesystem_markers"
        module_path = self.args.cohort / "scripts/deploy/duora_secrets.py"
        spec = importlib.util.spec_from_file_location(
            "duora_compat_secrets", module_path
        )
        assert spec is not None and spec.loader is not None
        helper = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = helper
        spec.loader.exec_module(helper)
        self.secrets = self.root / "secrets"
        helper.init_secrets(self.secrets, change_owner=True)
        backup_password = self.secrets / "db_backup_password"
        backup_password.write_text(helper.generate_password())
        backup_password.chmod(0o400)
        os.chown(backup_password, 999, 999)
        pgpass = self.secrets / "lab_app_pgpass"
        pgpass.write_text(
            "127.0.0.1:5432:duora:duora_app:"
            + (self.secrets / "db_app_password").read_text().strip()
            + "\n"
        )
        pgpass.chmod(0o400)
        os.chown(pgpass, 999, 999)
        self.media = self.root / "media"
        for area in ("quarantine", "derivatives"):
            directory = self.media / area
            directory.mkdir(parents=True, mode=0o700)
            marker = directory / ".duora-media-area"
            marker.write_text("synthetic compatibility fixture\n")
            marker.chmod(0o400)
            os.chown(marker, 1654, 1654)
            os.chown(directory, 1654, 1654)
        sentinel_script = self.root / "sentinel.sql"
        sentinel_script.write_text(
            "SELECT 1 / (current_setting('transaction_read_only') = 'on')::integer;\n"
            "SELECT payload FROM ops.current_schema_lab_sentinel WHERE id=1;\nSELECT pg_sleep(0.2);\n"
        )
        sentinel_script.chmod(0o444)
        self.report["source"]["readonly_session_script_sha256"] = digest(
            sentinel_script.read_bytes()
        )
        self.create("network", self.network, "--internal")
        self.create("volume", self.pgdata)
        self.create("volume", self.keys)
        self.subnet = self.inspect("network", self.network)["IPAM"]["Config"][0][
            "Subnet"
        ]
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            self.port = reserved.getsockname()[1]

    def start_database(self):
        assert self.root is not None
        self.report["stage"] = "postgres_initdb_and_roles"
        options = [
            "--user",
            "999:999",
            "--init",
            "--network",
            self.network,
            "--network-alias",
            "postgres",
            "--mount",
            f"type=volume,src={self.pgdata},dst=/var/lib/postgresql",
        ]
        options += [
            "--mount",
            f"type=bind,src={self.root / 'sentinel.sql'},dst=/run/lab/sentinel.sql,readonly",
        ]
        names = {
            "POSTGRES_PASSWORD_FILE": "postgres_superuser_password",
            "DUORA_MIGRATOR_PASSWORD_FILE": "db_migrator_password",
            "DUORA_APP_PASSWORD_FILE": "db_app_password",
            "DUORA_BACKUP_PASSWORD_FILE": "db_backup_password",
        }
        for key, name in names.items():
            options += ["--env", key + "=/run/secrets/" + name]
        for name in (*names.values(), "lab_app_pgpass"):
            options += [
                "--mount",
                f"type=bind,src={self.secrets / name},dst=/run/secrets/{name},readonly",
            ]
        self.run(
            self.pg,
            options,
            self.pins["old_images"]["postgres"]["reference"],
            "postgres",
            "-c",
            "archive_mode=off",
            "-c",
            "archive_command=",
            "-c",
            "shared_buffers=256MB",
            "-c",
            "max_connections=50",
            detached=True,
        )
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            try:
                if (
                    self.sql(
                        "select count(*) from pg_roles where rolname in ('duora_app','duora_migrator','duora_backup');"
                    )
                    == "3"
                ):
                    break
            except RuntimeError:
                pass
            time.sleep(1)
        else:
            raise RuntimeError("synthetic initdb role setup did not complete")
        need(
            self.sql("show server_version_num;") == "180006",
            "actual PG18.6 runtime required",
        )
        self.report["postgres_server_version"] = self.sql("show server_version;")

    def migrate(self, image, suffix, sql_path, expected):
        self.report["stage"] = suffix
        connection = self.secrets / "duora_migrator_connection"
        self.run(
            self.prefix + "-" + suffix,
            [
                "--label",
                "com.docker.compose.project=duora",
                "--label",
                "com.docker.compose.service=migrator",
                "--label",
                "com.docker.compose.oneoff=True",
                "--user",
                "1654:1654",
                "--network",
                self.network,
                "--read-only",
                "--tmpfs",
                "/tmp:size=128m,mode=1777",
                "--mount",
                f"type=bind,src={connection},dst=/run/secrets/duora_migrator_connection,readonly",
            ],
            image,
            timeout=240,
        )
        check_history(self.history(), expected)
        self.schema_state = len(expected)
        self.sql_confirmed = False
        if self.schema_state == 35:
            need(
                digest(sql_path.read_bytes()) == self.pins["historical_sql_sha256"],
                "old bootstrap SQL differs",
            )
            self.sql(sql_path.read_text())
            self.sql_confirmed = True
        else:
            need(
                self.schema_state == 48,
                "intermediate schema state is unqualified/refused",
            )

    def start_web(self):
        need(
            self.schema_state in {35, 48} and self.sql_confirmed,
            "lab refuses old web until confirmed canonical SQL",
        )
        options = [
            "--user",
            "1654:1654",
            "--network",
            self.network,
            "--network-alias",
            "duora-web",
            "--read-only",
            "--tmpfs",
            "/tmp:size=64m,mode=1777",
            "--mount",
            f"type=volume,src={self.keys},dst=/home/app/.aspnet/DataProtection-Keys",
        ]
        env = {
            "ASPNETCORE_ENVIRONMENT": "Production",
            "SecretsDirectory": "/run/secrets",
            "Media__Storage__Provider": "FileSystem",
            "Media__Storage__FileSystemRoot": "/var/lib/duora-media",
            "Duora__PublicOrigin": f"https://localhost:{self.port}",
            "DataProtection__KeysDirectory": "/home/app/.aspnet/DataProtection-Keys",
            "ReverseProxy__KnownNetworks__0": self.subnet,
        }
        for key, value in env.items():
            options += ["--env", key + "=" + value]
        for source, target in (
            ("duora_app_connection", "ConnectionStrings__Duora"),
            ("dataprotection_certificate", "DataProtection__Certificate"),
            (
                "dataprotection_previous_certificates",
                "DataProtection__PreviousCertificates",
            ),
        ):
            options += [
                "--mount",
                f"type=bind,src={self.secrets / source},dst=/run/secrets/{target},readonly",
            ]
        for area in ("quarantine", "derivatives"):
            options += [
                "--mount",
                f"type=bind,src={self.media / area},dst=/var/lib/duora-media/{area}"
                + (",readonly" if area == "derivatives" else ""),
            ]
        self.run(
            self.web,
            options,
            self.pins["old_images"]["web"]["reference"],
            detached=True,
        )
        if not self.report.get("edge_started"):
            self.run(
                self.edge,
                [
                    "--user",
                    "10001:10001",
                    "--network",
                    self.network,
                    "--read-only",
                    "--sysctl",
                    "net.ipv4.ip_unprivileged_port_start=0",
                    "--tmpfs",
                    "/tmp:size=16m,mode=1777",
                    "--tmpfs",
                    "/data:size=32m,uid=10001,gid=10001,mode=700",
                    "--tmpfs",
                    "/config:size=16m,uid=10001,gid=10001,mode=700",
                    "--env",
                    "DUORA_DOMAIN=localhost",
                    "--env",
                    "DUORA_TLS=internal",
                    "--publish",
                    f"127.0.0.1:{self.port}:443",
                ],
                self.pins["old_images"]["edge"]["reference"],
                detached=True,
            )
            self.report["edge_started"] = True
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            try:
                Browser(self.port).csrf()
                return
            except (OSError, ValueError, http.client.HTTPException):
                time.sleep(1)
        raise RuntimeError(
            "old web startup/CSRF failed; fixture requirements unresolved"
        )

    def exercise(self, phase):
        self.report["stage"] = phase
        browser = Browser(self.port)
        browser.csrf()
        email = "compat-" + uuid.uuid4().hex + "@example.com"
        password = "synthetic compatibility phrase 259"
        status, _, _ = browser.request(
            "POST",
            "/api/v1/auth/register",
            {
                "email": email,
                "password": password,
                "acceptedTermsVersion": "2026-10-01",
                "acceptedPrivacyVersion": "2026-10-01",
            },
        )
        need(status == 202, "real old registration failed")
        user = self.sql("select id from identity.users where email='" + email + "';")
        uuid.UUID(user)
        proof = self.sql(
            "select payload->>'token' from ops.outbox_events where resource_id='"
            + user
            + "' and type='identity.account-email-requested' and payload->>'kind'='EmailConfirmation' order by occurred_at desc limit 1;"
        )
        need(
            len(proof) == 8 and proof.isdigit(),
            "synthetic outbox confirmation proof missing",
        )
        confirm, _, _ = browser.request(
            "POST",
            "/api/v1/auth/confirm-email",
            {"userId": user, "token": proof, "password": password},
        )
        need(confirm == 204, "real old confirmation failed")
        login, _, _ = browser.request(
            "POST", "/api/v1/auth/login", {"email": email, "password": password}
        )
        need(login == 204, "real old login failed")
        browser.csrf()
        read, headers, _ = browser.request("GET", "/api/v1/me/profile")
        need(read == 200 and headers.get("etag"), "real old profile read failed")
        display = "Compat " + phase
        patched, _, _ = browser.request(
            "PATCH",
            "/api/v1/me/profile",
            {"displayName": display},
            {"If-Match": headers["etag"]},
        )
        need(patched == 200, "real old profile patch failed")
        observed, _, body = browser.request("GET", "/api/v1/me/profile")
        need(
            observed == 200 and json.loads(body).get("displayName") == display,
            "old profile write not read back",
        )
        evidence = {
            "name": phase,
            "register_status": status,
            "confirm_status": confirm,
            "login_status": login,
            "profile_read_status": read,
            "profile_patch_status": patched,
            "profile_value_verified": True,
            "consent_count": int(
                self.sql(
                    "select count(*) from identity.consent_records where user_id='"
                    + user
                    + "';"
                )
            ),
            "outbox_count": int(
                self.sql(
                    "select count(*) from ops.outbox_events where resource_id='"
                    + user
                    + "' and type='identity.account-email-requested';"
                )
            ),
            "session_count": int(
                self.sql(
                    "select count(*) from identity.login_sessions where user_id='"
                    + user
                    + "';"
                )
            ),
        }
        need(
            evidence["consent_count"] == 2
            and evidence["session_count"] > 0
            and evidence["outbox_count"] > 0,
            "old DB writes missing",
        )
        return evidence, browser, user, display

    def incoming_sql_transaction(self, *, fail=False):
        raw = (self.args.cohort / self.pins["canonical_sql_path"]).read_bytes()
        need(
            digest(raw) == self.pins["canonical_sql_sha256"],
            "incoming repair SQL identity differs",
        )
        self.helper_quiescent()
        payload = raw.decode() + ("\nSELECT 1/0;\n" if fail else "")
        self.helper_counts["execute_sql_calls"] += 1
        if not fail:
            self.helpers._recovery_execute_sql(
                self.compose_transport, self.helper_runner, payload
            )
            self.helper_acl()
            return
        try:
            self.helpers._recovery_execute_sql(
                self.compose_transport, self.helper_runner, payload
            )
        except self.helpers.DeployError as error:
            need(
                "division by zero" in str(error),
                "transaction fault did not reach its exact boundary",
            )
        else:
            raise ValueError(
                "actual executor SQL helper accepted deliberate transaction failure"
            )

    def repair_forward_cases(self):
        check_history(self.history(), self.pins["new_migrations"])
        self.sql_confirmed = False
        # Deliberately unsafe ACL is confined to this empty-owned disposable DB and no app is running.
        self.sql("GRANT UPDATE, DELETE, TRUNCATE ON ops.audit_events TO duora_app;")
        unsafe = "select case when has_table_privilege('duora_app','ops.audit_events','UPDATE') then 'unsafe' else 'closed' end;"
        need(self.sql(unsafe) == "unsafe", "negative ACL fixture did not take effect")
        self.helper_quiescent()
        self.helper_acl(unsafe=True)
        cases = []

        def record(name, ready, committed):
            self.report["stage"] = name
            self.helper_quiescent()
            check_history(self.history(), self.pins["new_migrations"])
            if not ready:
                try:
                    self.start_web()
                except ValueError as error:
                    need(
                        str(error)
                        == "lab refuses old web until confirmed canonical SQL",
                        "unexpected refusal boundary",
                    )
                else:
                    raise RuntimeError("unconfirmed head48 started an app")
                need(
                    not self.docker(
                        "container",
                        "ls",
                        "--all",
                        "--quiet",
                        "--filter",
                        "name=^/" + self.web + "$",
                    ),
                    "old web exists before repair-forward confirmation",
                )
            cases.append(
                {
                    "name": name,
                    "schema_count": 48,
                    "app_start_permitted": ready,
                    "confirmed": ready,
                    "transaction_committed": committed,
                }
            )

        record("head48_pre_sql", False, False)
        before_failure = self.schema_digest()
        self.incoming_sql_transaction(fail=True)
        need(
            self.schema_digest() == before_failure and self.sql(unsafe) == "unsafe",
            "failed incoming SQL transaction did not fully roll back",
        )
        record("transaction_failed", False, False)
        self.incoming_sql_transaction()
        need(self.sql(unsafe) == "closed", "committed canonical SQL did not repair ACL")
        record("commit_before_confirmation", False, True)
        before_replay = self.schema_digest()
        # Commit may have occurred without product confirmation: replay exact SQL transaction, never Down or oldSQL.
        self.incoming_sql_transaction()
        after_replay = self.schema_digest()
        need(
            before_replay == after_replay and self.sql(unsafe) == "closed",
            "incoming repair-forward is not idempotent",
        )
        self.sql_confirmed = True
        record("post_sql_confirmed", True, True)
        self.report["product_sql_helpers"] = {
            "source_sha256": self.report["source"]["executor_source_sha256"],
            "transport": "strict_compose_exec_to_owned_docker_exec_real_results",
            **self.helper_counts,
            "adversarial_acl_refused": True,
            "repaired_acl_witness_passed": True,
        }
        self.report["repair_forward"] = {
            "sql_sha256": self.pins["canonical_sql_sha256"],
            "source_head": self.pins["cohort_commit"],
            "transactional": True,
            "idempotent": True,
            "no_old_sql_repair": True,
            "product_seal_written": False,
            "negative_acl_fixture": True,
            "confirmation_basis": "fresh_replay_commit_and_bound_sql_digest_not_product_seal",
            "acl_before_replay": before_replay,
            "acl_after_replay": after_replay,
            "failed_transaction_rolled_back": True,
            "cases": cases,
        }

    def consent_digest(self, user):
        value = self.sql(
            "select json_agg(json_build_array(id,user_id,document,version,accepted_at,document_locale) order by id)::text from identity.consent_records where user_id='"
            + user
            + "';"
        )
        return digest(value.encode())

    def execute(self):
        self.prepare()
        self.start_database()
        sql = self.pins["canonical_sql_path"]
        self.migrate(
            self.pins["old_images"]["migrator"]["reference"],
            "migrate35",
            self.args.old_source / sql,
            self.pins["old_migrations"],
        )
        self.start_web()
        first, browser, user, display = self.exercise("old_schema35")
        self.report["phases"] = [first]
        self.report["migrations_before"] = self.history()
        old_consent = self.consent_digest(user)
        self.sql(
            "create table ops.current_schema_lab_sentinel (id integer primary key, payload text not null); insert into ops.current_schema_lab_sentinel values (1,'"
            + self.nonce
            + "');"
        )
        self.sql("GRANT SELECT ON ops.current_schema_lab_sentinel TO duora_app;")
        session_before = self.start_readonly_session()
        backend = session_before["pid"]
        sentinel = digest(self.sentinel_value().encode())
        self.report["database_before"] = self.database()
        self.owned("container", self.web)
        self.docker("stop", "--time", "20", self.web)
        self.docker("rm", self.web)
        self.migrate(
            self.migrator,
            "migrate48",
            self.args.cohort / sql,
            self.pins["new_migrations"],
        )
        check_preservation(self.report["database_before"], self.database())
        self.report["migrations_after"] = self.history()
        self.repair_forward_cases()
        schema48 = self.schema_digest()
        need(
            self.sql(
                "select is_nullable||':'||data_type from information_schema.columns where table_schema='identity' and table_name='consent_records' and column_name='document_sha256';"
            )
            == "YES:text",
            "fingerprint expansion is not nullable text",
        )
        self.report["new_fingerprint_nullable"] = True
        try:
            # This is ONLY a lab boundary after REAL DDL48. No product executor, attempt receipt or success history is forged.
            raise InjectedPostDdlFailure("after_schema48_before_current")
        except InjectedPostDdlFailure:
            self.report["boundary"] = {
                "stage": "after_schema48_before_current",
                "injected": True,
                "product_deploy_flow_invoked": False,
                "product_current_record_written": False,
            }
        self.start_web()  # Same exact OLD binary and keys; retain migrated DB48, never Down/recreate.
        status, _, body = browser.request("GET", "/api/v1/me/profile")
        need(
            status == 200 and json.loads(body).get("displayName") == display,
            "pre-migration authenticated profile not preserved",
        )
        self.report["retained_old_profile_read_status"] = status
        need(
            self.consent_digest(user) == old_consent,
            "pre-migration consent rows changed",
        )
        self.report["retained_old_consent_unchanged"] = True
        second, _, later, _ = self.exercise("old_schema48_after_failure")
        self.report["phases"].append(second)
        need(
            self.sql(
                "select count(*) from identity.consent_records where user_id='"
                + later
                + "' and document_sha256 is null and document_locale is null;"
            )
            == "2",
            "old writer supplied/inferred a new fingerprint",
        )
        legacy = str(uuid.uuid4())
        self.sql(
            "insert into identity.consent_records (id,user_id,document,version,accepted_at) values ('"
            + legacy
            + "','"
            + later
            + "','Terms','lab-legacy','2026-01-01Z');",
            app=True,
        )
        need(
            self.sql(
                "select count(*) from identity.consent_records where id='"
                + legacy
                + "' and document_sha256 is null and document_locale is null;"
            )
            == "1",
            "legacy runtime-role insert failed",
        )
        self.report["legacy_insert_as_app_has_null_fingerprint_and_locale"] = True
        self.report["database_after"] = self.database()
        session_after = self.session_identity()
        assert session_after is not None
        self.report["persistent_session"] = {
            "before": backend,
            "after": session_after["pid"],
            "driver": "pgbench",
            "role": "duora_app",
            "backend_start_before": session_before["backend_start"],
            "backend_start_after": session_after["backend_start"],
            "select_only_enforced": True,
        }
        self.report["sentinel"] = {
            "before": sentinel,
            "after": digest(self.sentinel_value().encode()),
        }
        self.report["schema48"] = {
            "before_recovery": schema48,
            "after_recovery": self.schema_digest(),
        }
        check_history(self.history(), self.pins["new_migrations"])
        self.verify_sources()

    def cleanup(self):
        errors = []
        if self.session is not None:
            if self.session.poll() is None:
                self.session.terminate()
                try:
                    self.session.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.session.kill()
                    self.session.wait(timeout=5)
            # The owned PostgreSQL container is removed below, ending only this lab's readonly pgbench client.
        for kind in ("container", "volume", "network", "image"):
            for name in reversed(self.created[kind]):
                try:
                    result = subprocess.run(
                        ["/usr/bin/docker", kind, "inspect", name],
                        capture_output=True,
                        text=True,
                        check=False,
                        timeout=15,
                    )
                    if result.returncode and "No such" in result.stderr:
                        continue
                    need(result.returncode == 0, "cleanup inspection failed")
                    self.owned(kind, name)
                    self.docker(
                        kind,
                        "rm",
                        *(["--force"] if kind == "container" else []),
                        name,
                        timeout=60,
                    )
                except (RuntimeError, ValueError, subprocess.TimeoutExpired):
                    errors.append(kind)
        if self.root is not None:
            try:
                shutil.rmtree(self.root)
            except OSError:
                errors.append("scratch")
        return not errors


def main():
    parser = argparse.ArgumentParser()
    for name in ("cohort", "old-source", "workflow-source", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--recovery-handoff", type=Path)
    args = parser.parse_args()
    workflow_head = (
        require_actions()
    )  # Refuses Mac BEFORE mkdir, Docker, build, SDK or network.
    temporary = Path(os.environ["RUNNER_TEMP"]).resolve()
    need(
        args.output.resolve().is_relative_to(temporary),
        "receipt must stay inside RUNNER_TEMP",
    )
    if args.recovery_handoff is not None:
        need(args.recovery_handoff.resolve().is_relative_to(temporary) and not args.recovery_handoff.exists(),
             "new same-job recovery handoff must stay in RUNNER_TEMP")
    os.environ["GIT_NO_LAZY_FETCH"] = "1"
    lab = Lab(args, workflow_head)
    result = lab.report
    result.update(
        runtime={
            "system": platform.system(),
            "architecture": platform.machine(),
            "uid": os.geteuid(),
            "github_hosted": True,
            "python": platform.python_version(),
        },
        run={
            "id": os.environ["GITHUB_RUN_ID"],
            "attempt": os.environ["GITHUB_RUN_ATTEMPT"],
            "pull_request": 260,
            "workflow_sha": os.environ["GITHUB_WORKFLOW_SHA"],
            "merge_sha": os.environ["GITHUB_SHA"],
            "workflow_ref": os.environ["GITHUB_WORKFLOW_REF"],
            "url": "https://github.com/BipoXDXD/duora/actions/runs/"
            + os.environ["GITHUB_RUN_ID"]
            + "/attempts/"
            + os.environ["GITHUB_RUN_ATTEMPT"],
        },
    )
    code = 1
    keeper = None
    try:
        lab.execute()
        if args.recovery_handoff is not None:
            # Transfer only the single actual immutable build. Original runtime/secrets still clean below.
            keeper = "duora259-recovery-keeper-" + uuid.uuid4().hex
            lab.docker("image", "tag", lab.migrator, keeper)
            need(lab.inspect("image", keeper)["Id"] == result["new_migrator"]["docker_image_id"],
                 "keeper must bind the same functional cohort build")
        result["success"] = True
        result["qualified_schema_states"] = [35, 48]
    except Exception as error:
        result["success"] = False
        result["failure_type"] = type(error).__name__
        result["failure_stage"] = result.get("stage", "source_preflight")
        if type(error) in {RuntimeError, ValueError}:
            result["failure_code"] = str(error)[:240]
    finally:
        result["cleanup_successful"] = lab.cleanup()
    try:
        validate_receipt(result, lab.pins)
        code = 0
    except ValueError:
        result["success"] = False
        result["qualified_schema_states"] = []
    raw = json.dumps(result, indent=2) + "\n"
    need(len(raw.encode()) <= 65536, "receipt exceeded public metadata bound")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(raw)
    args.output.chmod(0o644)
    if keeper is not None:
        if code == 0:
            handoff = {"schema": 1, "run_id": os.environ["GITHUB_RUN_ID"],
                       "attempt": os.environ["GITHUB_RUN_ATTEMPT"], "workflow_head": workflow_head,
                       "keeper": keeper, "docker_image_id": result["new_migrator"]["docker_image_id"],
                       "functional_receipt_sha256": digest(raw.encode())}
            args.recovery_handoff.parent.mkdir(parents=True, exist_ok=True)
            with args.recovery_handoff.open("x") as handle:
                os.chmod(args.recovery_handoff, 0o600)
                json.dump(handoff, handle, sort_keys=True)
                handle.write("\n")
                handle.flush()
                os.fsync(handle.fileno())
        else:
            need(lab.inspect("image", keeper)["Id"] == result["new_migrator"]["docker_image_id"], "foreign keeper cleanup refused")
            lab.docker("image", "rm", keeper)
    print(
        json.dumps(
            {
                "scope": SCOPE,
                "success": result["success"],
                "native_run": True,
                "schema_capability": [35, 48],
                "executor_recovery_tested": False,
            }
        )
    )
    return code


if __name__ == "__main__":
    sys.exit(main())
