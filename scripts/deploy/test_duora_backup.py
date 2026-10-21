"""Testes do wrapper de backup. Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v"""

import contextlib
import io
import json
import os
import subprocess
import sys
import unittest
from unittest.mock import patch
from dataclasses import dataclass
from pathlib import Path
from tempfile import TemporaryDirectory

import duora_backup as backup

NOW = 1_760_000_000.0  # instante fixo do banco nos testes
HOUR = 3600.0


def info_json(*, status_code: int = 0, backups: list[dict] | None = None, archive_max: str | None = "0000000100000000000000A1",
              name: str = "duora") -> str:
    """Formato de `pgbackrest info --output=json` (pgBackRest 2.59), só com os campos que o verify lê."""
    if backups is None:
        backups = [backup_entry(stop=NOW - 2 * HOUR)]
    archive = [] if archive_max is None else [{"id": "18-1", "min": "000000010000000000000001", "max": archive_max}]
    return json.dumps([{
        "name": name,
        "archive": archive,
        "backup": backups,
        "status": {"code": status_code, "message": "ok" if status_code == 0 else "no valid backups",
                   "lock": {"backup": {"held": False}}},
    }])


def backup_entry(*, stop: float, kind: str = "full", error: bool = False,
                 wal_start: str = "000000010000000000000002", wal_stop: str = "000000010000000000000002",
                 label: str | None = None) -> dict:
    return {"label": label or f"label-{int(stop)}", "type": kind, "error": error,
            "archive": {"start": wal_start, "stop": wal_stop}, "database": {"id": 1, "repo-key": 1},
            "timestamp": {"start": int(stop) - 60, "stop": int(stop)}}


def archiver(*, archived_ago: float | None = 30.0, failed_ago: float | None = None) -> backup.ArchiverStatus:
    return backup.ArchiverStatus(
        last_archived_at=None if archived_ago is None else NOW - archived_ago,
        last_failed_at=None if failed_ago is None else NOW - failed_ago,
        now=NOW,
    )


class ArchiverStatusParsingTests(unittest.TestCase):
    def test_reads_epochs_from_psql_line(self) -> None:
        status = backup.parse_archiver_status("1759999970.5|1759999000|1760000000.25\n")

        self.assertEqual(backup.ArchiverStatus(1759999970.5, 1759999000.0, 1760000000.25), status)

    def test_empty_columns_mean_never_happened(self) -> None:
        status = backup.parse_archiver_status("||1760000000\n")

        self.assertEqual(backup.ArchiverStatus(None, None, 1760000000.0), status)

    def test_malformed_line_is_an_error(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "pg_stat_archiver"):
            backup.parse_archiver_status("garbage\n")

    def test_missing_database_clock_is_an_error(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "pg_stat_archiver"):
            backup.parse_archiver_status("1|2|\n")


class BackupHealthTests(unittest.TestCase):
    def test_healthy_repository_has_no_problems(self) -> None:
        self.assertEqual([], backup.backup_health_problems(info_json(), archiver()))

    def test_archive_exactly_at_rpo_limit_is_healthy(self) -> None:
        problems = backup.backup_health_problems(info_json(), archiver(archived_ago=300))

        self.assertEqual([], problems)

    def test_archive_older_than_rpo_is_a_problem(self) -> None:
        problems = backup.backup_health_problems(info_json(), archiver(archived_ago=301))

        self.assertEqual(1, len(problems))
        self.assertIn("301 s", problems[0])

    def test_never_archived_is_a_problem(self) -> None:
        problems = backup.backup_health_problems(info_json(), archiver(archived_ago=None))

        self.assertEqual(["nenhum WAL arquivado (pg_stat_archiver.last_archived_time vazio)"], problems)

    def test_failure_after_last_success_means_archiving_is_failing(self) -> None:
        problems = backup.backup_health_problems(info_json(), archiver(archived_ago=30, failed_ago=10))

        self.assertEqual(1, len(problems))
        self.assertIn("falhando", problems[0])

    def test_failure_before_last_success_is_history(self) -> None:
        problems = backup.backup_health_problems(info_json(), archiver(archived_ago=10, failed_ago=30))

        self.assertEqual([], problems)

    def test_stanza_status_not_ok_is_a_problem(self) -> None:
        problems = backup.backup_health_problems(info_json(status_code=2), archiver())

        self.assertIn("pgbackrest info: stanza duora com status 2 (no valid backups)", problems)

    def test_repository_without_backups_is_a_problem(self) -> None:
        problems = backup.backup_health_problems(info_json(backups=[]), archiver())

        self.assertIn("nenhum backup no repositório", problems)

    def test_repository_without_archived_wal_is_a_problem(self) -> None:
        problems = backup.backup_health_problems(info_json(archive_max=None), archiver())

        self.assertIn("nenhum WAL no repositório (pgbackrest info sem archive.max)", problems)

    def test_newest_backup_at_age_limit_is_healthy(self) -> None:
        problems = backup.backup_health_problems(info_json(backups=[backup_entry(stop=NOW - 48 * HOUR)]), archiver())

        self.assertEqual([], problems)

    def test_newest_backup_older_than_limit_is_a_problem(self) -> None:
        problems = backup.backup_health_problems(
            info_json(backups=[backup_entry(stop=NOW - 48 * HOUR - 1)]), archiver())

        self.assertEqual(1, len(problems))
        self.assertIn("último backup terminou há 48.0 h", problems[0])

    def test_age_is_measured_from_the_newest_backup_regardless_of_order(self) -> None:
        backups = [backup_entry(stop=NOW - 2 * HOUR, kind="diff"), backup_entry(stop=NOW - 100 * HOUR)]

        self.assertEqual([], backup.backup_health_problems(info_json(backups=backups), archiver()))

    def test_newest_backup_with_error_is_a_problem(self) -> None:
        problems = backup.backup_health_problems(
            info_json(backups=[backup_entry(stop=NOW - HOUR, error=True)]), archiver())

        self.assertEqual(1, len(problems))
        self.assertIn("erro", problems[0])

    def test_other_stanza_is_a_problem(self) -> None:
        problems = backup.backup_health_problems(info_json(name="main"), archiver())

        self.assertEqual(["pgbackrest info: esperado só a stanza duora, recebido ['main']"], problems)

    def test_invalid_info_json_is_a_problem(self) -> None:
        problems = backup.backup_health_problems("not json", archiver())

        self.assertEqual(1, len(problems))
        self.assertIn("JSON", problems[0])

    def test_reports_every_problem_at_once(self) -> None:
        problems = backup.backup_health_problems(
            info_json(status_code=2, backups=[]), archiver(archived_ago=None))

        self.assertEqual(3, len(problems))


@dataclass(frozen=True)
class RepoCheck:
    """O que o verify-repo lê: o resumo do `pgbackrest verify` e a listagem do archive (`repo-ls --recurse`)."""
    verify: str
    listing: str


CONTAINER_ID = "c0ffee123456"
S3_SECRET = "segredo-sintetico-do-teste"
REPO_CONF = f"""[global]
repo1-s3-endpoint=s3.example.test
repo1-s3-bucket=duora-backup
repo1-s3-region=us-east-1
repo1-s3-key=AKIASINTETICA
repo1-s3-key-secret={S3_SECRET}
"""
VERSIONING_ENABLED = ('<VersioningConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">'
                      "<Status>Enabled</Status></VersioningConfiguration>")


def object_lock(*, enabled: str = "Enabled", mode: str = "COMPLIANCE", period: str = "<Days>21</Days>") -> str:
    rule = f"<Rule><DefaultRetention><Mode>{mode}</Mode>{period}</DefaultRetention></Rule>" if mode else ""
    return f"<ObjectLockConfiguration><ObjectLockEnabled>{enabled}</ObjectLockEnabled>{rule}</ObjectLockConfiguration>"


def http(status: int, body: str) -> str:
    """Resposta como o subprocess em modo texto entrega (CRLF já virou LF)."""
    return f"HTTP/1.1 {status} X\nContent-Length: {len(body)}\nContent-Type: application/xml\n\n{body}"


class FakeRunner:
    """Responde como Docker e pgBackRest responderiam; falha no comando que contém fail_on."""

    def __init__(self, *, containers: str = CONTAINER_ID + "\n", fail_on: str | None = None,
                 info: str | None = None, archiver_line: str | None = None, verify: RepoCheck | None = None,
                 bucket: dict[str, str] | None = None, repo_conf: str | None = None,
                 b2_authorize: str | None = None, snapshots: str | None = None) -> None:
        self.commands: list[list[str]] = []
        self._snapshots = snapshots
        # Padrão de produção: repositório no B2 com a chave mínima. REPO_CONF (S3 genérico) só no drill.
        self._repo_conf = repo_conf if repo_conf is not None else B2_REPO_CONF
        self._b2_authorize = b2_authorize
        self.stdins: list[str | None] = []
        self._containers = containers
        self._fail_on = fail_on
        self._info = info if info is not None else info_json()
        self._verify = verify
        self._bucket = bucket if bucket is not None else {"versioning": http(200, VERSIONING_ENABLED),
                                                          "object-lock": http(200, object_lock())}
        self._archiver_line = archiver_line if archiver_line is not None else f"{NOW - 5}||{NOW}\n"

    # capture e timeout_seconds não mudam a resposta do dublê, mas ficam na assinatura para ela casar com o
    # Protocol duora_backup.Runner (o Pyright confere o FakeRunner passado a run_action).
    def __call__(self, command: list[str], *, capture: bool = False, timeout_seconds: int = 0,
                 stdin: str | None = None) -> subprocess.CompletedProcess[str]:
        self.commands.append(command)
        self.stdins.append(stdin)
        joined = " ".join(command)
        if self._fail_on is not None and (self._fail_on in joined or self._fail_on in (stdin or "")):
            return subprocess.CompletedProcess(command, 1, "", "falha simulada")
        if "restic" in command and "snapshots" in command:
            return subprocess.CompletedProcess(command, 0, self._snapshots or "[]", "")
        if "restic" in command:
            return subprocess.CompletedProcess(command, 0, "", "")
        if command[:2] == ["docker", "ps"]:
            return subprocess.CompletedProcess(command, 0, self._containers, "")
        if "cat" in command:
            return subprocess.CompletedProcess(command, 0, self._repo_conf, "")
        if "s_client" in command and "b2_authorize_account" in (stdin or ""):
            response = self._b2_authorize if self._b2_authorize is not None else b2_authorize()
            return subprocess.CompletedProcess(command, 0, response, "")
        if "s_client" in command:
            subresource = "object-lock" if "?object-lock=" in (stdin or "") else "versioning"
            return subprocess.CompletedProcess(command, 0, self._bucket[subresource], "")
        if "--output=json" in command:
            return subprocess.CompletedProcess(command, 0, self._info, "")
        if "pg_stat_archiver" in joined:
            return subprocess.CompletedProcess(command, 0, self._archiver_line, "")
        if "pgbackrest" in command and self._verify is not None and ("verify" in command or "repo-ls" in command):
            output = self._verify.verify if "verify" in command else self._verify.listing
            return subprocess.CompletedProcess(command, 0, output, "")
        return subprocess.CompletedProcess(command, 0, "", "")

    def pgbackrest_calls(self) -> list[list[str]]:
        return [command[command.index("pgbackrest"):] for command in self.commands if "pgbackrest" in command]

    def restic_calls(self) -> list[list[str]]:
        """Só os argumentos depois das opções globais do wrapper (--option, --cache-dir)."""
        calls = []
        for command in self.commands:
            if "restic" in command:
                args = command[command.index("restic") + 1:]
                calls.append(args[args.index("--cache-dir") + 2:])
        return calls


EGRESS_SUBNET = "172.30.20.0/24"


def write_egress_env(directory: Path, content: str = f"DUORA_BACKUP_SUBNET={EGRESS_SUBNET}\n") -> Path:
    """/etc/duora/backup-egress.env como o template do Ansible gera."""
    env_file = directory / "backup-egress.env"
    env_file.write_text("# Gerenciado pelo Ansible\n" + content + "DUORA_BACKUP_S3_CIDRS=\n", encoding="utf-8")
    return env_file


class ActionTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.state_dir = Path(self._tmp.name)
        self.egress_env = write_egress_env(self.state_dir)

    def run_action(self, action: str, runner: FakeRunner) -> None:
        backup.run_action(action, runner, project="duora", state_dir=self.state_dir, egress_env=self.egress_env)

    def test_full_runs_full_backup_in_the_postgres_container(self) -> None:
        runner = FakeRunner()

        self.run_action("full", runner)

        self.assertEqual([["pgbackrest", "--stanza=duora", "backup", "--type=full"]], runner.pgbackrest_calls())
        self.assertEqual(["docker", "exec", CONTAINER_ID], runner.commands[-1][:3])

    def test_diff_runs_differential_backup(self) -> None:
        runner = FakeRunner()

        self.run_action("diff", runner)

        self.assertEqual([["pgbackrest", "--stanza=duora", "backup", "--type=diff"]], runner.pgbackrest_calls())

    def test_stanza_create_runs_stanza_create(self) -> None:
        runner = FakeRunner()

        self.run_action("stanza-create", runner)

        self.assertEqual([["pgbackrest", "--stanza=duora", "stanza-create"]], runner.pgbackrest_calls())

    def test_backup_container_is_found_by_compose_project_and_service_labels(self) -> None:
        runner = FakeRunner()

        self.run_action("full", runner)

        lookup = runner.commands[0]
        self.assertEqual(["docker", "ps"], lookup[:2])
        self.assertIn("label=com.docker.compose.project=duora", lookup)
        self.assertIn("label=com.docker.compose.service=backup", lookup)

    def test_archiver_is_read_by_the_backup_role_over_tcp(self) -> None:
        runner = FakeRunner()

        self.run_action("verify", runner)

        psql = runner.commands[-1]
        self.assertEqual(["docker", "exec", CONTAINER_ID, "psql"], psql[:4])
        self.assertEqual(["--host", "postgres", "--username", "duora_backup", "--dbname", "postgres",
                          "--no-password"], psql[psql.index("--host"):psql.index("--host") + 7])

    def test_missing_backup_container_is_an_error(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "nenhum container backup"):
            self.run_action("full", FakeRunner(containers=""))

    def test_more_than_one_backup_container_is_an_error(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "2 containers backup"):
            self.run_action("full", FakeRunner(containers="aaa\nbbb\n"))

    def test_failed_backup_is_an_error(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "backup --type=full falhou"):
            self.run_action("full", FakeRunner(fail_on="--type=full"))

    def test_verify_checks_archive_then_reads_info_and_archiver(self) -> None:
        runner = FakeRunner()

        self.run_action("verify", runner)

        self.assertEqual([["pgbackrest", "--stanza=duora", "check"],
                          ["pgbackrest", "--stanza=duora", "info", "--output=json"]], runner.pgbackrest_calls())
        self.assertIn("pg_stat_archiver", " ".join(runner.commands[-1]))

    def test_verify_stops_when_check_fails(self) -> None:
        runner = FakeRunner(fail_on=" check")

        with self.assertRaisesRegex(backup.BackupError, "check falhou"):
            self.run_action("verify", runner)
        self.assertFalse(any("--output=json" in command for command in runner.commands))

    def test_verify_fails_with_every_health_problem(self) -> None:
        runner = FakeRunner(info=info_json(backups=[]), archiver_line=f"||{NOW}\n")

        with self.assertRaises(backup.BackupError) as raised:
            self.run_action("verify", runner)
        self.assertIn("nenhum backup no repositório", str(raised.exception))
        self.assertIn("nenhum WAL arquivado", str(raised.exception))

    def test_verify_uses_database_clock_for_archive_age(self) -> None:
        runner = FakeRunner(archiver_line=f"{NOW - 400}||{NOW}\n")

        with self.assertRaisesRegex(backup.BackupError, "400 s"):
            self.run_action("verify", runner)

    def test_backup_waits_for_the_deploy_lock(self) -> None:
        lock_file = self.state_dir / ".deploy.lock"
        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            with self.assertRaisesRegex(backup.BackupError, "deploy ou backup em andamento"):
                backup.run_action("full", FakeRunner(), project="duora", state_dir=self.state_dir,
                                  egress_env=self.egress_env, lock_wait_seconds=0)
        self.assertTrue(lock_file.exists())

    def test_verify_does_not_take_the_deploy_lock(self) -> None:
        # O deploy chama o verify segurando o lock; pegá-lo aqui travaria o próprio deploy.
        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            backup.run_action("verify", FakeRunner(), project="duora", state_dir=self.state_dir,
                              egress_env=self.egress_env, lock_wait_seconds=0)


class ArchiverStatsLagTests(unittest.TestCase):
    """Um archive confirmado pode anteceder a publicação de pg_stat_archiver pelo PostgreSQL."""

    def setUp(self) -> None:
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.state_dir = Path(temporary.name)
        sleep = patch.object(backup.time, "sleep")
        self.sleep = sleep.start()
        self.addCleanup(sleep.stop)

    def verify(self, lines: list[str], *, info: str | None = None, fail_on: str | None = None) -> FakeRunner:
        runner = FakeRunner(info=info, fail_on=fail_on)
        reads = 0

        def run(command: list[str], *, capture: bool = False, timeout_seconds: int = 0,
                stdin: str | None = None) -> subprocess.CompletedProcess[str]:
            nonlocal reads
            result = runner(command, capture=capture, timeout_seconds=timeout_seconds, stdin=stdin)
            if "pg_stat_archiver" in " ".join(command) and result.returncode == 0:
                result.stdout = lines[min(reads, len(lines) - 1)]
                reads += 1
            return result

        backup.run_action("verify", run, project="duora", state_dir=self.state_dir, egress_env=None,
                          clock=lambda: NOW)
        return runner

    def test_confirmed_archive_can_become_visible_after_the_first_stats_read(self) -> None:
        self.verify([f"{NOW - 639}||{NOW}", f"{NOW - 1}||{NOW}"])

        self.assertEqual(NOW, float((self.state_dir / ".backup-verify-ok").read_text()))
        self.sleep.assert_called_once_with(1)

    def test_healthy_stats_do_not_wait(self) -> None:
        self.verify([f"{NOW - 1}||{NOW}"])

        self.sleep.assert_not_called()

    def test_stale_stats_still_block_after_five_seconds_of_polling(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "639 s"):
            self.verify([f"{NOW - 639}||{NOW}"])

        self.assertEqual([unittest.mock.call(1)] * 5, self.sleep.call_args_list)
        self.assertFalse((self.state_dir / ".backup-verify-ok").exists())

    def test_persistent_archive_failure_does_not_record_success(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "arquivamento falhando"):
            self.verify([f"{NOW - 30}|{NOW - 10}|{NOW}"])

        self.assertFalse((self.state_dir / ".backup-verify-ok").exists())

    def test_invalid_stats_and_query_errors_fail_immediately(self) -> None:
        for line, failure in [("garbage", None), (f"{NOW}||{NOW}", "pg_stat_archiver")]:
            with self.subTest(line=line, failure=failure), self.assertRaises(backup.BackupError):
                self.verify([line], fail_on=failure)
        self.sleep.assert_not_called()
        self.assertFalse((self.state_dir / ".backup-verify-ok").exists())

    def test_bad_repository_is_not_retried_as_statistics_lag(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "nenhum backup"):
            self.verify([f"{NOW - 639}||{NOW}", f"{NOW - 1}||{NOW}"], info=info_json(backups=[]))

        self.sleep.assert_not_called()
        self.assertFalse((self.state_dir / ".backup-verify-ok").exists())


class EgressFilterCheckTests(unittest.TestCase):
    """O verify só aprova o backup com o filtro de saída da rede backup-egress aplicado no host (fail closed)."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.state_dir = Path(self._tmp.name)

    def verify(self, runner: FakeRunner, egress_env: Path | None) -> None:
        backup.run_action("verify", runner, project="duora", state_dir=self.state_dir, egress_env=egress_env)

    def iptables_checks(self, runner: FakeRunner) -> list[list[str]]:
        return [command for command in runner.commands if command[0] == "iptables"]

    def test_verify_checks_the_docker_user_jump_and_the_final_drop(self) -> None:
        runner = FakeRunner()

        self.verify(runner, write_egress_env(self.state_dir))

        self.assertEqual([
            ["iptables", "-w", "-C", "FORWARD", "-j", "DOCKER-USER"],
            ["iptables", "-w", "-C", "DOCKER-USER", "-s", EGRESS_SUBNET, "!", "-d", EGRESS_SUBNET,
             "-j", "DUORA-BACKUP-EGRESS"],
            ["iptables", "-w", "-C", "DUORA-BACKUP-EGRESS", "-j", "DROP"],
        ], self.iptables_checks(runner))

    def test_filter_is_checked_before_touching_the_repository(self) -> None:
        runner = FakeRunner()

        self.verify(runner, write_egress_env(self.state_dir))

        self.assertEqual("iptables", runner.commands[0][0])

    def test_each_missing_rule_blocks_before_pgbackrest(self) -> None:
        for rule in ("FORWARD", "DOCKER-USER -s", "DUORA-BACKUP-EGRESS -j DROP"):
            with self.subTest(rule=rule):
                runner = FakeRunner(fail_on=f"-C {rule}")

                with self.assertRaisesRegex(backup.BackupError, "filtro de saída"):
                    self.verify(runner, write_egress_env(self.state_dir))
                self.assertEqual([], runner.pgbackrest_calls())

    def test_missing_policy_file_blocks(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "política do filtro de saída ilegível"):
            self.verify(FakeRunner(), self.state_dir / "nao-existe.env")

    def test_policy_without_subnet_blocks(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "DUORA_BACKUP_SUBNET"):
            self.verify(FakeRunner(), write_egress_env(self.state_dir, content=""))

    def test_policy_with_invalid_subnet_blocks(self) -> None:
        for subnet in ("", "172.30.20.0/33", "172.30.20.1/24", "qualquer", "172.30.20.0/24 -j ACCEPT"):
            with self.subTest(subnet=subnet):
                env_file = write_egress_env(self.state_dir, content=f"DUORA_BACKUP_SUBNET={subnet}\n")

                with self.assertRaisesRegex(backup.BackupError, "DUORA_BACKUP_SUBNET"):
                    self.verify(FakeRunner(), env_file)

    def test_skipping_the_check_runs_no_iptables(self) -> None:
        runner = FakeRunner()

        with contextlib.redirect_stdout(io.StringIO()) as stdout:
            self.verify(runner, None)

        self.assertEqual([], self.iptables_checks(runner))
        self.assertIn("filtro de saída NÃO conferido", stdout.getvalue())

    def test_cli_checks_the_filter_by_default(self) -> None:
        with contextlib.redirect_stderr(io.StringIO()) as stderr:
            code = backup.main(["verify", "--state-dir", str(self.state_dir),
                                "--egress-env", str(write_egress_env(self.state_dir))],
                               run=FakeRunner(fail_on="-C DOCKER-USER"))

        self.assertEqual(1, code)
        self.assertIn("filtro de saída", stderr.getvalue())

    def test_cli_skip_flag_is_only_for_the_local_drill(self) -> None:
        runner = FakeRunner()

        with contextlib.redirect_stdout(io.StringIO()):
            code = backup.main(["verify", "--state-dir", str(self.state_dir), "--skip-egress-check"], run=runner)

        self.assertEqual(0, code)
        self.assertEqual([], self.iptables_checks(runner))


class ScheduledVerifyDuringDeployTests(unittest.TestCase):
    """O verify do timer não alerta durante um deploy (que recria postgres e backup), sem esconder falha real."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.state_dir = Path(self._tmp.name)
        self.egress_env = write_egress_env(self.state_dir)
        self.now = NOW

    def verify(self, runner: FakeRunner, *, scheduled: bool = True) -> str:
        with contextlib.redirect_stdout(io.StringIO()) as stdout:
            backup.run_action("verify", runner, project="duora", state_dir=self.state_dir,
                              egress_env=self.egress_env, scheduled=scheduled, clock=lambda: self.now)
        return stdout.getvalue()

    def record_success(self, ago: float) -> None:
        self.now = NOW - ago
        self.verify(FakeRunner(), scheduled=False)
        self.now = NOW

    def test_skips_while_a_deploy_holds_the_lock_after_a_recent_success(self) -> None:
        self.record_success(ago=15 * 60)
        runner = FakeRunner(fail_on="docker")

        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            output = self.verify(runner)

        self.assertEqual([], runner.commands)
        self.assertIn("deploy em andamento", output)

    def test_runs_during_a_deploy_when_there_is_no_previous_success(self) -> None:
        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            with self.assertRaises(backup.BackupError):
                self.verify(FakeRunner(fail_on=" check"))

    def test_runs_during_a_deploy_when_the_last_success_is_too_old(self) -> None:
        self.record_success(ago=backup.MAX_VERIFY_SKIP_SECONDS + 1)

        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            with self.assertRaises(backup.BackupError):
                self.verify(FakeRunner(fail_on=" check"))

    def test_skip_at_the_age_limit_is_still_allowed(self) -> None:
        self.record_success(ago=backup.MAX_VERIFY_SKIP_SECONDS)
        runner = FakeRunner()

        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            self.verify(runner)

        self.assertEqual([], runner.commands)

    def test_runs_while_a_backup_holds_the_lock(self) -> None:
        # Um full pode levar horas: o WAL continua conferido a cada 15 min durante ele.
        self.record_success(ago=60)
        with backup.deploy_lock(self.state_dir, wait_seconds=0), backup.backup_running(self.state_dir):
            with self.assertRaises(backup.BackupError):
                self.verify(FakeRunner(fail_on=" check"))

    def test_backup_actions_mark_the_backup_as_running(self) -> None:
        seen: list[bool] = []

        def runner(command: list[str], *, capture: bool = False, timeout_seconds: int = 0,
                   stdin: str | None = None) -> subprocess.CompletedProcess[str]:
            if "pgbackrest" in command:
                seen.append(backup.deploy_in_progress(self.state_dir))
            return FakeRunner()(command, capture=capture, timeout_seconds=timeout_seconds, stdin=stdin)

        backup.run_action("full", runner, project="duora", state_dir=self.state_dir, egress_env=self.egress_env)

        self.assertEqual([False], seen)

    def test_manual_verify_ignores_the_deploy_lock(self) -> None:
        self.record_success(ago=60)
        runner = FakeRunner(fail_on=" check")

        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            with self.assertRaises(backup.BackupError):
                self.verify(runner, scheduled=False)

    def test_without_deploy_runs_normally(self) -> None:
        self.record_success(ago=60)

        with self.assertRaises(backup.BackupError):
            self.verify(FakeRunner(fail_on=" check"))

    def test_failed_verify_does_not_refresh_the_last_success(self) -> None:
        self.record_success(ago=backup.MAX_VERIFY_SKIP_SECONDS + 1)
        with self.assertRaises(backup.BackupError):
            self.verify(FakeRunner(fail_on=" check"), scheduled=False)

        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            with self.assertRaises(backup.BackupError):
                self.verify(FakeRunner(fail_on=" check"))

    def test_corrupt_success_marker_does_not_allow_skipping(self) -> None:
        (self.state_dir / ".backup-verify-ok").write_text("ontem", encoding="utf-8")

        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            with self.assertRaises(backup.BackupError):
                self.verify(FakeRunner(fail_on=" check"))

    def test_timer_unit_runs_the_scheduled_variant(self) -> None:
        unit = (Path(__file__).resolve().parents[2] / "infra" / "ansible" / "roles" / "backup" / "files"
                / "duora-backup@.service").read_text(encoding="utf-8")

        self.assertIn("ExecStart=/usr/local/sbin/duora-backup %i --scheduled", unit)

    def test_cli_accepts_scheduled(self) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            code = backup.main(["verify", "--scheduled", "--state-dir", str(self.state_dir),
                                "--egress-env", str(self.egress_env)], run=FakeRunner())

        self.assertEqual(0, code)


class ProcLocksTests(unittest.TestCase):
    """No Linux o verify do timer lê o flock em /proc/locks sem tomá-lo (o deploy não espera o lock). No CI (Linux),
    ScheduledVerifyDuringDeployTests exercita o /proc/locks real com flocks de verdade."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.state_dir = Path(self._tmp.name)
        for name in (".deploy.lock", ".backup.lock"):
            (self.state_dir / name).touch()
        self.proc_locks = self.state_dir / "locks"

    def lock_line(self, name: str, *, waiting: bool = False) -> str:
        status = (self.state_dir / name).stat()
        key = f"{os.major(status.st_dev):02x}:{os.minor(status.st_dev):02x}:{status.st_ino}"
        return f"1: {'-> ' if waiting else ''}FLOCK  ADVISORY  WRITE 4183 {key} 0 EOF"

    def in_progress(self, *lines: str) -> bool:
        self.proc_locks.write_text("\n".join(["2: POSIX  ADVISORY  WRITE 99 00:01:7 0 EOF", *lines]) + "\n",
                                   encoding="utf-8")
        return backup.deploy_in_progress(self.state_dir, proc_locks=self.proc_locks)

    def test_deploy_lock_held_means_deploy_in_progress(self) -> None:
        self.assertTrue(self.in_progress(self.lock_line(".deploy.lock")))

    def test_backup_lock_also_held_means_a_backup(self) -> None:
        self.assertFalse(self.in_progress(self.lock_line(".deploy.lock"), self.lock_line(".backup.lock")))

    def test_only_waiters_do_not_hold_the_lock(self) -> None:
        self.assertFalse(self.in_progress(self.lock_line(".deploy.lock", waiting=True)))

    def test_no_lock_lines_means_no_deploy(self) -> None:
        self.assertFalse(self.in_progress())


def verify_output(ranges: list[tuple[str, str, str]], *, status: str = "ok", backup_label: str = "NEWEST",
                  backup_status: str = "valid", counters: str = "missing: 0, checksum invalid: 0, size invalid: 0, other: 0",
                  ) -> RepoCheck:
    """Resumo do `pgbackrest verify --verbose` (pgBackRest 2.59.3, capturado no drill) e o archive com os segmentos
    das faixas contínuas dadas, como o `repo-ls --recurse archive/duora` lista (com arquivos que não são segmento)."""
    summary = ["2026-10-15 22:46:45.000 P00   INFO: verify command begin 2.59.3: --stanza=duora",
               "2026-10-15 22:46:45.891 P00   INFO: stanza: duora",
               f"                                    status: {status}",
               "                                      archiveId: 18-1, total WAL checked: 4, total valid WAL: 4",
               f"                                        {counters}",
               f"                                      backup: {backup_label}, status: {backup_status}, "
               "total files checked: 1268, total valid files: 1268",
               f"                                        {counters}",
               "2026-10-15 22:46:45.891 P00   INFO: verify command end: completed successfully (476ms)"]
    listing = ["18-1", "18-1/00000002.history"]
    for archive, first, last in ranges:
        timeline = int(first[:8], 16)
        for position in range(_position(first), _position(last) + 1):
            name = wal(timeline, position // 0x100, position % 0x100)
            listing.append(f"{archive}/{name[:16]}/{name}-{'ab' * 20}.zst")
        listing.append(f"{archive}/{first[:16]}/{first}.00000028.backup")
        listing.append(f"{archive}/{last[:16]}/{last}.partial-{'cd' * 20}.zst")
    return RepoCheck("\n".join(summary) + "\n", "\n".join(listing) + "\n")


def _position(name: str) -> int:
    return int(name[8:16], 16) * 0x100 + int(name[16:], 16)


def wal(timeline: int, log: int, segment: int) -> str:
    return f"{timeline:08X}{log:08X}{segment:08X}"


def newest_backup_info(*, wal_start: str = wal(1, 0, 2), wal_stop: str = wal(1, 0, 2)) -> str:
    return info_json(backups=[backup_entry(stop=NOW - 100 * HOUR, label="OLDER", wal_start=wal(1, 0, 1),
                                           wal_stop=wal(1, 0, 1)),
                              backup_entry(stop=NOW - HOUR, label="NEWEST", wal_start=wal_start, wal_stop=wal_stop)])


class RepositoryVerifyTests(unittest.TestCase):
    """verify-repo: o pgbackrest verify não falha com lacuna de WAL (só lista as faixas); a decisão é daqui."""

    def problems(self, check: RepoCheck, info_text: str | None = None) -> list[str]:
        return backup.repository_verify_problems(check.verify, check.listing, info_text or newest_backup_info())

    def test_contiguous_chain_is_healthy(self) -> None:
        self.assertEqual([], self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))])))

    def test_gap_after_the_newest_backup_is_a_problem(self) -> None:
        # Caso real do drill: segmento 3 apagado do repositório; o pgbackrest verify sai com 0.
        problems = self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 2)),
                                                ("18-1", wal(1, 0, 4), wal(1, 0, 4))]))

        self.assertEqual([f"lacuna de WAL na timeline 1: faltam os segmentos depois de {wal(1, 0, 2)} até antes "
                          f"de {wal(1, 0, 4)}; sem PITR nesse trecho, rode um full (sudo duora-backup full)"], problems)

    def test_gap_older_than_the_newest_backup_is_not_actionable(self) -> None:
        info = newest_backup_info(wal_start=wal(1, 0, 6), wal_stop=wal(1, 0, 6))

        problems = self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 2)),
                                                ("18-1", wal(1, 0, 4), wal(1, 0, 9))]), info)

        self.assertEqual([], problems)

    def test_gap_starting_right_at_the_newest_backup_is_a_problem(self) -> None:
        info = newest_backup_info(wal_start=wal(1, 0, 4), wal_stop=wal(1, 0, 4))

        problems = self.problems(verify_output([("18-1", wal(1, 0, 4), wal(1, 0, 4)),
                                                ("18-1", wal(1, 0, 6), wal(1, 0, 9))]), info)

        self.assertEqual(1, len(problems))

    def test_next_log_file_is_contiguous(self) -> None:
        problems = self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 0xFF)),
                                                ("18-1", wal(1, 1, 0), wal(1, 1, 3))]))

        self.assertEqual([], problems)

    def test_new_timeline_after_a_restore_is_not_a_gap(self) -> None:
        problems = self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 4)),
                                                ("18-1", wal(2, 0, 3), wal(2, 0, 7))]))

        self.assertEqual([], problems)

    def test_gap_on_a_newer_timeline_is_a_problem(self) -> None:
        problems = self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 4)),
                                                ("18-1", wal(2, 0, 3), wal(2, 0, 4)),
                                                ("18-1", wal(2, 0, 6), wal(2, 0, 7))]))

        self.assertEqual(1, len(problems))
        self.assertIn("timeline 2", problems[0])

    def test_gap_on_a_timeline_older_than_the_newest_backup_is_ignored(self) -> None:
        info = newest_backup_info(wal_start=wal(2, 0, 5), wal_stop=wal(2, 0, 5))

        problems = self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 2)),
                                                ("18-1", wal(1, 0, 7), wal(1, 0, 8)),
                                                ("18-1", wal(2, 0, 3), wal(2, 0, 9))]), info)

        self.assertEqual([], problems)

    def test_ranges_of_another_database_are_ignored(self) -> None:
        problems = self.problems(verify_output([("17-2", wal(1, 0, 1), wal(1, 0, 2)),
                                                ("17-2", wal(1, 0, 8), wal(1, 0, 9)),
                                                ("18-1", wal(1, 0, 1), wal(1, 0, 9))]))

        self.assertEqual([], problems)

    def test_no_wal_listed_for_the_current_database_is_a_problem(self) -> None:
        problems = self.problems(verify_output([("17-2", wal(1, 0, 1), wal(1, 0, 9))]))

        self.assertIn("o archive não tem WAL do banco do backup mais novo (archiveId *-1)", problems)

    def test_wal_of_the_newest_backup_missing_is_a_problem(self) -> None:
        info = newest_backup_info(wal_start=wal(1, 0, 2), wal_stop=wal(1, 0, 3))

        problems = self.problems(verify_output([("18-1", wal(1, 0, 3), wal(1, 0, 9))]), info)

        self.assertIn(f"WAL do backup mais novo (NEWEST, {wal(1, 0, 2)} a {wal(1, 0, 3)}) ausente no repositório",
                      problems)

    def test_invalid_files_are_a_problem(self) -> None:
        for counters in ("missing: 1, checksum invalid: 0, size invalid: 0, other: 0",
                         "missing: 0, checksum invalid: 2, size invalid: 0, other: 0",
                         "missing: 0, checksum invalid: 0, size invalid: 3, other: 0",
                         "missing: 0, checksum invalid: 0, size invalid: 0, other: 4"):
            with self.subTest(counters=counters):
                text = verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))], counters=counters)

                self.assertIn("pgbackrest verify encontrou arquivos inválidos ou ausentes no repositório",
                              self.problems(text))

    def test_stanza_status_not_ok_is_a_problem(self) -> None:
        text = verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))], status="error")

        self.assertIn("pgbackrest verify: stanza duora com status error", self.problems(text))

    def test_newest_backup_not_valid_is_a_problem(self) -> None:
        text = verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))], backup_status="invalid")

        self.assertIn("pgbackrest verify: backup mais novo NEWEST não está valid (invalid)", self.problems(text))

    def test_unrecognized_output_is_a_problem(self) -> None:
        healthy = verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))])
        for label, text in {"vazia": RepoCheck("", healthy.listing), "lixo": RepoCheck("garbage\n", healthy.listing),
                            "sem contadores": verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))],
                                                            counters=""),
                            "archive vazio": RepoCheck(healthy.verify, "")}.items():
            with self.subTest(output=label):
                self.assertNotEqual([], self.problems(text))

    def test_segment_beyond_16_mib_layout_is_a_problem(self) -> None:
        healthy = verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))])
        odd = RepoCheck(healthy.verify, healthy.listing + f"18-1/0000000100000000/{wal(1, 0, 0x100)}-{'ab' * 20}.zst\n")

        problems = self.problems(odd)

        self.assertTrue(any("segmento de WAL fora do formato" in problem for problem in problems))

    def test_unusable_info_is_a_problem(self) -> None:
        problems = self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))]), "not json")

        self.assertEqual(1, len(problems))
        self.assertIn("JSON", problems[0])


class VerifyRepositoryActionTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.state_dir = Path(self._tmp.name)
        self.healthy = verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))])

    def run_action(self, runner: FakeRunner, action: str = "verify-repo",
                   lock_wait_seconds: int = backup.LOCK_WAIT_SECONDS) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            backup.run_action(action, runner, project="duora", state_dir=self.state_dir,
                              egress_env=None, lock_wait_seconds=lock_wait_seconds)

    def test_daily_verifies_only_the_newest_backup_and_lists_the_archive(self) -> None:
        runner = FakeRunner(info=newest_backup_info(), verify=self.healthy)

        self.run_action(runner)

        self.assertEqual([["pgbackrest", "--stanza=duora", "info", "--output=json"],
                          ["pgbackrest", "--stanza=duora", "--log-level-console=off", "repo-ls", "--recurse",
                           "archive/duora"],
                          ["pgbackrest", "--stanza=duora", "verify", "--set=NEWEST", "--verbose"]],
                         runner.pgbackrest_calls())

    def test_weekly_verifies_the_whole_repository(self) -> None:
        runner = FakeRunner(info=newest_backup_info(), verify=self.healthy)

        self.run_action(runner, action="verify-repo-full")

        self.assertEqual(["pgbackrest", "--stanza=duora", "verify", "--verbose"], runner.pgbackrest_calls()[-1])

    def test_gap_fails_both_modes(self) -> None:
        gap = verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 2)), ("18-1", wal(1, 0, 4), wal(1, 0, 4))])
        for action in ("verify-repo", "verify-repo-full"):
            with self.subTest(action=action), self.assertRaisesRegex(backup.BackupError, "lacuna de WAL"):
                self.run_action(FakeRunner(info=newest_backup_info(), verify=gap), action=action)

    def test_failed_native_verify_fails_the_action(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "verify --set=NEWEST --verbose falhou"):
            self.run_action(FakeRunner(info=newest_backup_info(), verify=self.healthy, fail_on=" verify "))

    def test_failed_archive_listing_fails_the_action(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "repo-ls"):
            self.run_action(FakeRunner(info=newest_backup_info(), verify=self.healthy, fail_on="repo-ls"))

    def test_repository_without_backups_fails_before_verifying(self) -> None:
        runner = FakeRunner(info=info_json(backups=[]), verify=self.healthy)

        with self.assertRaisesRegex(backup.BackupError, "nenhum backup"):
            self.run_action(runner)
        self.assertFalse(any("verify" in call for call in runner.pgbackrest_calls()))

    def test_waits_for_the_deploy_lock(self) -> None:
        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            with self.assertRaisesRegex(backup.BackupError, "deploy ou backup em andamento"):
                self.run_action(FakeRunner(info=newest_backup_info(), verify=self.healthy), lock_wait_seconds=0)

    def test_timers_run_daily_light_and_weekly_full_outside_backups_and_reboot(self) -> None:
        role = Path(__file__).resolve().parents[2] / "infra" / "ansible" / "roles" / "backup"
        daily = (role / "files" / "duora-backup-verify-repo.timer").read_text(encoding="utf-8")
        weekly = (role / "files" / "duora-backup-verify-repo-full.timer").read_text(encoding="utf-8")
        tasks = (role / "tasks" / "main.yml").read_text(encoding="utf-8")

        self.assertIn("Unit=duora-backup@verify-repo.service", daily)
        self.assertIn("OnCalendar=*-*-* 05:00:00 UTC", daily)
        self.assertIn("Unit=duora-backup@verify-repo-full.service", weekly)
        # Quarta: longe do full de domingo, e depois do reinício automático das 04:30.
        self.assertIn("OnCalendar=Wed *-*-* 06:00:00 UTC", weekly)
        self.assertEqual(2, tasks.count("duora-backup-verify-repo.timer"))
        self.assertEqual(2, tasks.count("duora-backup-verify-repo-full.timer"))


class RepoSettingsTests(unittest.TestCase):
    def test_defaults_follow_pgbackrest(self) -> None:
        settings = backup.parse_repo_settings(REPO_CONF)

        self.assertEqual(("duora-backup.s3.example.test", 443, "/", "/etc/ssl/certs/ca-certificates.crt"),
                         (settings.host, settings.port, settings.bucket_path, settings.ca_file))
        self.assertEqual("duora-backup.s3.example.test", settings.host_header)

    def test_path_style_custom_port_and_ca(self) -> None:
        settings = backup.parse_repo_settings(REPO_CONF + "repo1-s3-uri-style=path\nrepo1-storage-port=9000\n"
                                              "repo1-storage-ca-file=/etc/pgbackrest/ca.crt\n")

        self.assertEqual(("s3.example.test", "s3.example.test:9000", "/duora-backup", "/etc/pgbackrest/ca.crt"),
                         (settings.host, settings.host_header, settings.bucket_path, settings.ca_file))

    def test_missing_required_field_is_an_error(self) -> None:
        for field_name in ("repo1-s3-endpoint", "repo1-s3-bucket", "repo1-s3-region", "repo1-s3-key",
                           "repo1-s3-key-secret"):
            with self.subTest(field=field_name):
                text = "\n".join(line for line in REPO_CONF.splitlines() if not line.startswith(field_name + "="))

                with self.assertRaisesRegex(backup.BackupError, field_name):
                    backup.parse_repo_settings(text)

    def test_invalid_port_is_an_error(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "repo1-storage-port"):
            backup.parse_repo_settings(REPO_CONF + "repo1-storage-port=https\n")

    def test_secret_is_not_in_the_repr(self) -> None:
        self.assertNotIn(S3_SECRET, repr(backup.parse_repo_settings(REPO_CONF)))


class SignedRequestTests(unittest.TestCase):
    def test_matches_the_aws_signature_v4_example(self) -> None:
        # Exemplo "GET Bucket Lifecycle" da documentação do Amazon S3 (Signature Version 4, header Authorization).
        settings = backup.S3Settings(endpoint="s3.amazonaws.com", bucket="examplebucket", region="us-east-1",
                                     key="AKIAIOSFODNN7EXAMPLE", secret="wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")

        request = backup.signed_bucket_request(settings, "lifecycle", 1369353600.0)  # 2013-05-24T00:00:00Z

        self.assertIn("Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543", request)
        self.assertTrue(request.startswith("GET /?lifecycle= HTTP/1.1\r\nHost: examplebucket.s3.amazonaws.com\r\n"))
        self.assertTrue(request.endswith("Connection: close\r\n\r\n"))

    def test_path_style_signs_the_bucket_path_and_port(self) -> None:
        settings = backup.parse_repo_settings(REPO_CONF + "repo1-s3-uri-style=path\nrepo1-storage-port=9000\n")

        request = backup.signed_bucket_request(settings, "versioning", NOW)

        self.assertTrue(request.startswith("GET /duora-backup?versioning= HTTP/1.1\r\nHost: s3.example.test:9000\r\n"))
        self.assertNotIn(S3_SECRET, request)


class HttpResponseTests(unittest.TestCase):
    def test_plain_response(self) -> None:
        self.assertEqual((200, "<a/>"), backup.parse_http_response("HTTP/1.1 200 OK\r\nA: b\r\n\r\n<a/>"))

    def test_text_mode_line_endings(self) -> None:
        self.assertEqual((404, "<e/>"), backup.parse_http_response("HTTP/1.1 404 Not Found\nA: b\n\n<e/>"))

    def test_chunked_response(self) -> None:
        raw = "HTTP/1.1 200 OK\nTransfer-Encoding: chunked\n\n3\n<a>\n4\n</a>\n0\n\n"

        self.assertEqual((200, "<a></a>"), backup.parse_http_response(raw))

    def test_garbage_is_an_error(self) -> None:
        for raw in ("", "garbage", "HTTP/1.1 200 OK"):
            with self.subTest(raw=raw), self.assertRaisesRegex(backup.BackupError, "ilegível"):
                backup.parse_http_response(raw)


class BucketProtectionTests(unittest.TestCase):
    def test_versioning_and_compliance_lock_for_21_days_is_protected(self) -> None:
        self.assertEqual([], backup.bucket_protection_problems(VERSIONING_ENABLED, object_lock()))

    def test_retention_in_years_is_accepted(self) -> None:
        self.assertEqual([], backup.bucket_protection_problems(VERSIONING_ENABLED,
                                                               object_lock(period="<Years>1</Years>")))

    def test_governance_mode_is_a_problem(self) -> None:
        # Decisão do usuário: COMPLIANCE. GOVERNANCE cede a quem tiver s3:BypassGovernanceRetention.
        problems = backup.bucket_protection_problems(VERSIONING_ENABLED,
                                                     object_lock(mode="GOVERNANCE", period="<Days>30</Days>"))

        self.assertEqual(["Object Lock sem retenção padrão COMPLIANCE (Mode GOVERNANCE)"], problems)

    def test_versioning_not_enabled_is_a_problem(self) -> None:
        for versioning in ("<VersioningConfiguration/>",
                           "<VersioningConfiguration><Status>Suspended</Status></VersioningConfiguration>",
                           "not xml", ""):
            with self.subTest(versioning=versioning):
                self.assertEqual(["versionamento do bucket não está Enabled"],
                                 backup.bucket_protection_problems(versioning, object_lock()))

    def test_object_lock_not_enabled_is_a_problem(self) -> None:
        for lock in ("", "not xml", object_lock(enabled="Disabled")):
            with self.subTest(lock=lock):
                self.assertEqual(["Object Lock do bucket não está habilitado"],
                                 backup.bucket_protection_problems(VERSIONING_ENABLED, lock))

    def test_lock_without_default_retention_is_a_problem(self) -> None:
        self.assertEqual(["Object Lock sem retenção padrão COMPLIANCE (Mode ausente)"],
                         backup.bucket_protection_problems(VERSIONING_ENABLED, object_lock(mode="")))

    def test_retention_shorter_than_the_window_is_a_problem(self) -> None:
        problems = backup.bucket_protection_problems(VERSIONING_ENABLED, object_lock(period="<Days>20</Days>"))

        self.assertEqual(["retenção padrão do Object Lock de 20 dias (mínimo 21, a janela de retenção)"], problems)

    def test_retention_period_missing_or_invalid_is_a_problem(self) -> None:
        for period in ("", "<Days>vinte</Days>"):
            with self.subTest(period=period):
                self.assertEqual(1, len(backup.bucket_protection_problems(VERSIONING_ENABLED,
                                                                          object_lock(period=period))))


def generic_s3_runner(**kwargs) -> FakeRunner:
    """S3 genérico (MinIO do drill): só aceito com --skip-egress-check (egress_env None)."""
    return FakeRunner(repo_conf=REPO_CONF, **kwargs)


class BucketProtectionActionTests(unittest.TestCase):
    """O verify (timer e deploy) falha fechado sem versionamento + Object Lock no bucket (spec 18.3, ADR-0023)."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.state_dir = Path(self._tmp.name)

    def verify(self, runner: FakeRunner) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            backup.run_action("verify", runner, project="duora", state_dir=self.state_dir, egress_env=None)

    def test_queries_both_subresources_from_the_backup_container_with_tls_verification(self) -> None:
        runner = generic_s3_runner()

        self.verify(runner)

        queries = [(command, stdin) for command, stdin in zip(runner.commands, runner.stdins) if "s_client" in command]
        self.assertEqual(2, len(queries))
        for command, _ in queries:
            self.assertEqual(["docker", "exec", "-i", CONTAINER_ID, "timeout", "--kill-after=5", "30", "openssl",
                              "s_client"], command[:9])
            self.assertIn("-verify_return_error", command)
            self.assertEqual("duora-backup.s3.example.test", command[command.index("-verify_hostname") + 1])
            self.assertEqual("duora-backup.s3.example.test:443", command[command.index("-connect") + 1])
        self.assertEqual(["GET /?versioning=", "GET /?object-lock="],
                         [(stdin or "").split(" HTTP/1.1")[0] for _, stdin in queries])

    def test_bucket_is_checked_before_the_archive_check(self) -> None:
        runner = generic_s3_runner()

        self.verify(runner)

        joined = [" ".join(command) for command in runner.commands]
        first_query = next(i for i, command in enumerate(joined) if "s_client" in command)
        check = next(i for i, command in enumerate(joined) if command.endswith(" check"))
        self.assertLess(first_query, check)

    def test_secret_never_goes_to_a_command_line(self) -> None:
        runner = generic_s3_runner()

        self.verify(runner)

        self.assertFalse(any(S3_SECRET in " ".join(command) for command in runner.commands))
        self.assertFalse(any(S3_SECRET in (stdin or "") for stdin in runner.stdins))

    def test_bucket_without_object_lock_blocks(self) -> None:
        # MinIO e S3 respondem 404 ObjectLockConfigurationNotFoundError para bucket criado sem lock.
        runner = generic_s3_runner(bucket={"versioning": http(200, VERSIONING_ENABLED),
                                    "object-lock": http(404, "<Error><Code>ObjectLockConfigurationNotFoundError"
                                                             "</Code></Error>")})

        with self.assertRaisesRegex(backup.BackupError, "Object Lock do bucket não está habilitado"):
            self.verify(runner)
        self.assertEqual([], runner.pgbackrest_calls())

    def test_unversioned_bucket_blocks(self) -> None:
        runner = generic_s3_runner(bucket={"versioning": http(200, "<VersioningConfiguration/>"),
                                    "object-lock": http(200, object_lock())})

        with self.assertRaisesRegex(backup.BackupError, "sem proteção contra exclusão"):
            self.verify(runner)

    def test_denied_query_blocks(self) -> None:
        for subresource in ("versioning", "object-lock"):
            with self.subTest(subresource=subresource):
                bucket = {"versioning": http(200, VERSIONING_ENABLED), "object-lock": http(200, object_lock())}
                bucket[subresource] = http(403, "<Error><Code>AccessDenied</Code></Error>")

                with self.assertRaisesRegex(backup.BackupError, "HTTP 403"):
                    self.verify(generic_s3_runner(bucket=bucket))

    def test_versioning_404_blocks(self) -> None:
        bucket = {"versioning": http(404, "<Error/>"), "object-lock": http(200, object_lock())}

        with self.assertRaisesRegex(backup.BackupError, "HTTP 404"):
            self.verify(generic_s3_runner(bucket=bucket))

    def test_unreadable_response_blocks(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "ilegível"):
            self.verify(generic_s3_runner(bucket={"versioning": "", "object-lock": http(200, object_lock())}))


# --- Backblaze B2 (ADR-0023, adendo de 2026-10-16; docs/operations/backblaze-setup.md) ---------------------------

B2_ENDPOINT = "s3.us-east-005.backblazeb2.com"
B2_KEY_ID = "005a1b2c3d4e5f60000000001"
B2_SECRET = "K005SinteticoNaoRealParaTeste0000"
B2_REPO_CONF = f"""[global]
repo1-s3-endpoint={B2_ENDPOINT}
repo1-s3-bucket=duora-prod-backup-x7k2q9
repo1-s3-region=us-east-005
repo1-s3-uri-style=host
repo1-s3-key={B2_KEY_ID}
repo1-s3-key-secret={B2_SECRET}
"""
B2_BACKUP_CAPABILITIES = ["listFiles", "readFiles", "writeFiles", "readBuckets", "readBucketRetentions"]
# Como o B2 responde ao GetBucketVersioning (bucket do B2 é sempre versionado) e ao GetObjectLockConfiguration de um
# bucket com File Lock e retenção padrão COMPLIANCE de 21 dias: XML do S3 com namespace.
B2_VERSIONING = ('<?xml version="1.0" encoding="UTF-8"?>\n<VersioningConfiguration '
                 'xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Status>Enabled</Status></VersioningConfiguration>')
B2_OBJECT_LOCK = ('<?xml version="1.0" encoding="UTF-8"?>\n<ObjectLockConfiguration '
                  'xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><ObjectLockEnabled>Enabled</ObjectLockEnabled>'
                  "<Rule><DefaultRetention><Mode>COMPLIANCE</Mode><Days>21</Days></DefaultRetention></Rule>"
                  "</ObjectLockConfiguration>")


def b2_authorize(*, capabilities: list[str] | None = None, buckets: list[dict] | None = None,
                 name_prefix: str | None = "duora/", s3_api_url: str = f"https://{B2_ENDPOINT}") -> str:
    """Resposta do b2_authorize_account v4 (docs da Backblaze), só com os campos que o verify lê e o token."""
    allowed = {"buckets": buckets if buckets is not None else [{"id": "4a5b6c", "name": "duora-prod-backup-x7k2q9"}],
               "capabilities": capabilities if capabilities is not None else B2_BACKUP_CAPABILITIES,
               "namePrefix": name_prefix}
    body = json.dumps({"accountId": "conta-sintetica", "authorizationToken": "TOKEN-SINTETICO-NUNCA-IMPRESSO",
                       "applicationKeyExpirationTimestamp": None,
                       "apiInfo": {"storageApi": {"apiUrl": "https://api005.backblazeb2.com",
                                                  "downloadUrl": "https://f005.backblazeb2.com",
                                                  "s3ApiUrl": s3_api_url, "allowed": allowed}}})
    return f"HTTP/1.1 200 OK\nContent-Type: application/json;charset=UTF-8\n\n{body}"


def b2_runner(*, authorize: str | None = None, versioning: str = B2_VERSIONING,
              object_lock_xml: str = B2_OBJECT_LOCK) -> FakeRunner:
    return FakeRunner(repo_conf=B2_REPO_CONF, b2_authorize=authorize if authorize is not None else b2_authorize(),
                      bucket={"versioning": http(200, versioning), "object-lock": http(200, object_lock_xml)})


class B2RepoSettingsTests(unittest.TestCase):
    def test_b2_endpoint_uses_virtual_host_and_signs_with_the_b2_region(self) -> None:
        settings = backup.parse_repo_settings(B2_REPO_CONF)

        request = backup.signed_bucket_request(settings, "object-lock", NOW)

        self.assertTrue(settings.is_backblaze_b2)
        self.assertEqual("duora-prod-backup-x7k2q9.s3.us-east-005.backblazeb2.com", settings.host)
        self.assertIn("/us-east-005/s3/aws4_request", request)
        self.assertTrue(request.startswith("GET /?object-lock= HTTP/1.1\r\n"
                                           "Host: duora-prod-backup-x7k2q9.s3.us-east-005.backblazeb2.com\r\n"))

    def test_other_endpoints_are_not_b2(self) -> None:
        for endpoint in ("s3.example.test", "minio", "s3.us-east-005.backblazeb2.com.evil.test",
                         "evilbackblazeb2.com"):
            with self.subTest(endpoint=endpoint):
                text = REPO_CONF.replace("repo1-s3-endpoint=s3.example.test", f"repo1-s3-endpoint={endpoint}")
                self.assertFalse(backup.parse_repo_settings(text).is_backblaze_b2)

    def test_authorize_request_uses_basic_auth_and_never_puts_the_secret_in_clear(self) -> None:
        request = backup.b2_authorize_request(backup.parse_repo_settings(B2_REPO_CONF))

        self.assertTrue(request.startswith("GET /b2api/v4/b2_authorize_account HTTP/1.1\r\n"
                                           "Host: api.backblazeb2.com\r\n"))
        self.assertIn("Authorization: Basic MDA1YTFiMmMzZDRlNWY2MDAwMDAwMDAwMTpLMDA1U2ludGV0aWNvTmFvUmVhbFBhcmFUZXN0"
                      "ZTAwMDA=\r\n", request)
        self.assertNotIn(B2_SECRET, request)
        self.assertTrue(request.endswith("Connection: close\r\n\r\n"))


class B2KeyProblemsTests(unittest.TestCase):
    """A chave do backup na VPS só pode o que o backup usa: sem apagar versão, sem mexer em retenção (ADR-0023)."""

    settings = backup.parse_repo_settings(B2_REPO_CONF)

    def problems(self, response: str) -> list[str]:
        return backup.b2_key_problems(backup.parse_http_response(response)[1], self.settings)

    def test_minimal_key_restricted_to_the_bucket_and_prefix_has_no_problems(self) -> None:
        self.assertEqual([], self.problems(b2_authorize()))

    def test_harmless_read_capabilities_are_accepted(self) -> None:
        capabilities = [*B2_BACKUP_CAPABILITIES, "listBuckets", "listAllBucketNames", "readBucketEncryption",
                        "readFileRetentions", "readFileLegalHolds"]

        self.assertEqual([], self.problems(b2_authorize(capabilities=capabilities)))

    def test_each_dangerous_capability_is_a_problem(self) -> None:
        for capability in ("deleteFiles", "bypassGovernance", "writeFileRetentions", "writeBucketRetentions",
                           "writeFileLegalHolds", "writeBuckets", "deleteBuckets", "writeKeys", "deleteKeys",
                           "listKeys", "writeBucketEncryption", "writeBucketReplications", "shareFiles",
                           "capacidadeNovaDaBackblaze"):
            with self.subTest(capability=capability):
                problems = self.problems(b2_authorize(capabilities=[*B2_BACKUP_CAPABILITIES, capability]))

                self.assertEqual([f"chave do backup com capacidades além do necessário: {capability}"], problems)

    def test_missing_capability_is_a_problem(self) -> None:
        capabilities = [c for c in B2_BACKUP_CAPABILITIES if c != "readBucketRetentions"]

        self.assertEqual(["chave do backup sem as capacidades: readBucketRetentions"],
                         self.problems(b2_authorize(capabilities=capabilities)))

    def test_key_not_restricted_to_exactly_this_bucket_is_a_problem(self) -> None:
        for buckets in ([], [{"id": "1", "name": "outro-projeto"}],
                        [{"id": "4a5b6c", "name": "duora-prod-backup-x7k2q9"}, {"id": "2", "name": "outro-projeto"}],
                        [{"id": "4a5b6c", "name": None}]):
            with self.subTest(buckets=buckets):
                self.assertEqual(["chave do backup não está restrita só ao bucket duora-prod-backup-x7k2q9"],
                                 self.problems(b2_authorize(buckets=buckets)))

    def test_key_without_the_repository_prefix_is_a_problem(self) -> None:
        for prefix in (None, "", "duora", "media/", "duora/archive/"):
            with self.subTest(prefix=prefix):
                self.assertEqual([f"chave do backup sem a restrição de prefixo duora/ (namePrefix {prefix!r})"],
                                 self.problems(b2_authorize(name_prefix=prefix)))

    def test_endpoint_of_another_region_is_a_problem(self) -> None:
        problems = self.problems(b2_authorize(s3_api_url="https://s3.us-east-004.backblazeb2.com"))

        self.assertEqual(["repo1-s3-endpoint s3.us-east-005.backblazeb2.com não é o da conta "
                          "(s3ApiUrl https://s3.us-east-004.backblazeb2.com): confira o número da região no bucket"],
                         problems)

    def test_reports_every_problem_at_once(self) -> None:
        response = b2_authorize(capabilities=["deleteFiles", "writeFiles"], buckets=[], name_prefix=None)

        self.assertEqual(4, len(self.problems(response)))

    def test_unexpected_shape_is_a_problem_without_echoing_the_body(self) -> None:
        for body in ("not json", "[]", '{"apiInfo": {}}', '{"apiInfo": {"storageApi": {"allowed": []}}}',
                     '{"authorizationToken": "TOKEN-SINTETICO-NUNCA-IMPRESSO", "apiInfo": {"storageApi": '
                     '{"allowed": {"capabilities": "writeFiles", "buckets": []}}}}'):
            with self.subTest(body=body):
                problems = backup.b2_key_problems(body, self.settings)

                self.assertEqual(["resposta do b2_authorize_account em formato inesperado"], problems)
                self.assertNotIn("TOKEN", " ".join(problems))


class B2BucketProtectionActionTests(unittest.TestCase):
    """O verify contra o B2: chave conferida primeiro, depois versionamento e File Lock pela API S3."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.state_dir = Path(self._tmp.name)

    def verify(self, runner: FakeRunner) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            backup.run_action("verify", runner, project="duora", state_dir=self.state_dir, egress_env=None)

    def queries(self, runner: FakeRunner) -> list[tuple[list[str], str]]:
        return [(command, stdin or "") for command, stdin in zip(runner.commands, runner.stdins)
                if "s_client" in command]

    def test_protected_bucket_with_minimal_key_passes(self) -> None:
        runner = b2_runner()

        self.verify(runner)

        self.assertIn(["pgbackrest", "--stanza=duora", "check"], runner.pgbackrest_calls())
        targets = [command[command.index("-connect") + 1] for command, _ in self.queries(runner)]
        self.assertEqual(["api.backblazeb2.com:443", "duora-prod-backup-x7k2q9.s3.us-east-005.backblazeb2.com:443",
                          "duora-prod-backup-x7k2q9.s3.us-east-005.backblazeb2.com:443"], targets)

    def test_authorize_goes_out_of_the_backup_container_with_tls_verification_and_system_ca(self) -> None:
        runner = b2_runner()

        self.verify(runner)

        command, stdin = self.queries(runner)[0]
        self.assertEqual(["docker", "exec", "-i", CONTAINER_ID, "timeout", "--kill-after=5", "30", "openssl",
                          "s_client"], command[:9])
        self.assertIn("-verify_return_error", command)
        self.assertEqual("api.backblazeb2.com", command[command.index("-verify_hostname") + 1])
        self.assertEqual("/etc/ssl/certs/ca-certificates.crt", command[command.index("-CAfile") + 1])
        self.assertIn("b2_authorize_account", stdin)

    def test_key_with_delete_files_blocks_before_any_pgbackrest_call(self) -> None:
        runner = b2_runner(authorize=b2_authorize(capabilities=[*B2_BACKUP_CAPABILITIES, "deleteFiles"]))

        with self.assertRaisesRegex(backup.BackupError, "deleteFiles"):
            self.verify(runner)
        self.assertEqual([], runner.pgbackrest_calls())

    def test_rejected_key_blocks_without_echoing_the_response(self) -> None:
        body = '{"code": "unauthorized", "message": "TOKEN-SINTETICO-NUNCA-IMPRESSO", "status": 401}'
        runner = b2_runner(authorize=f"HTTP/1.1 401 Unauthorized\nContent-Type: application/json\n\n{body}")

        with self.assertRaises(backup.BackupError) as raised:
            self.verify(runner)
        self.assertIn("HTTP 401", str(raised.exception))
        self.assertNotIn("TOKEN", str(raised.exception))

    def test_bucket_without_file_lock_blocks(self) -> None:
        # Bucket do B2 criado sem File Lock: sem ObjectLockEnabled (ou 404, coberto pelos testes genéricos).
        runner = b2_runner(object_lock_xml='<ObjectLockConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/"/>')

        with self.assertRaisesRegex(backup.BackupError, "Object Lock do bucket não está habilitado"):
            self.verify(runner)

    def test_governance_default_retention_blocks(self) -> None:
        runner = b2_runner(object_lock_xml=B2_OBJECT_LOCK.replace("COMPLIANCE", "GOVERNANCE"))

        with self.assertRaisesRegex(backup.BackupError, "GOVERNANCE"):
            self.verify(runner)

    def test_secret_never_goes_to_a_command_line_nor_in_clear_to_stdin(self) -> None:
        runner = b2_runner()

        self.verify(runner)

        self.assertFalse(any(B2_SECRET in " ".join(command) for command in runner.commands))
        self.assertFalse(any(B2_SECRET in (stdin or "") for stdin in runner.stdins))

    def test_non_b2_endpoint_in_the_drill_does_not_call_the_b2_api(self) -> None:
        runner = generic_s3_runner()

        self.verify(runner)

        self.assertFalse(any("b2_authorize_account" in (stdin or "") for stdin in runner.stdins))

    def test_production_refuses_an_endpoint_outside_backblaze_b2(self) -> None:
        # Fora do drill (--skip-egress-check), um repo.conf para outro S3 não tem a prova da chave: recusa.
        runner = generic_s3_runner()
        egress_env = write_egress_env(self.state_dir)

        with self.assertRaisesRegex(backup.BackupError, "Backblaze B2"):
            with contextlib.redirect_stdout(io.StringIO()):
                backup.run_action("verify", runner, project="duora", state_dir=self.state_dir, egress_env=egress_env)
        self.assertEqual([], runner.pgbackrest_calls())

    def test_tls_queries_have_a_connection_timeout(self) -> None:
        runner = b2_runner()

        self.verify(runner)

        for command in (c for c in runner.commands if "s_client" in c):
            self.assertEqual(["timeout", "--kill-after=5", "30"], command[4:7])


class B2RepositoryPrefixTests(unittest.TestCase):
    def test_key_prefix_matches_the_repo_path_of_the_image(self) -> None:
        # A restrição de prefixo da chave (namePrefix) precisa ser o repo1-path do pgBackRest, sem a barra inicial.
        conf = (Path(__file__).resolve().parents[2] / "infra/images/postgres/pgbackrest-repo.conf").read_text(
            encoding="utf-8")

        self.assertIn(f"repo1-path=/{backup.B2_BACKUP_NAME_PREFIX.rstrip('/')}\n", conf)


# --- Backup das fotos com restic (adendo do ADR-0010; docs/operations/backup-restore.md) ---------------------------

MEDIA_BUCKET = "duora-prod-backup-x7k2q9"
MEDIA_KEY_ID = "005a1b2c3d4e5f60000000002"
MEDIA_SECRET = "K005SinteticoDaMidiaNaoReal00000"
MEDIA_PASSWORD = "senha-sintetica-do-restic-nao-real-0000000000"


def write_media_secrets(directory: Path, *, repository: str | None = None,
                        credentials: str | None = None) -> Path:
    """Os três arquivos que o duora-secrets write-media-backup-repo grava."""
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "restic_media_repository").write_text(
        repository if repository is not None else f"s3:https://{B2_ENDPOINT}/{MEDIA_BUCKET}/media\n", encoding="utf-8")
    (directory / "restic_media_password").write_text(MEDIA_PASSWORD + "\n", encoding="utf-8")
    (directory / "restic_media_credentials").write_text(
        credentials if credentials is not None else
        f"[default]\naws_access_key_id = {MEDIA_KEY_ID}\naws_secret_access_key = {MEDIA_SECRET}\n", encoding="utf-8")
    return directory


def snapshot(at: float) -> str:
    from datetime import datetime, timezone
    # Nanossegundos como o restic grava; zerados para o teste da fronteira cair exatamente no limite.
    stamp = datetime.fromtimestamp(at, tz=timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000000000Z")
    return json.dumps([{"time": stamp, "hostname": "duora", "paths": ["/var/lib/duora-media/derivatives"],
                        "tags": ["derivatives"], "id": "4bba301e", "short_id": "4bba301e"}])


def media_runner(*, prefix: str | None = "media/", snapshots: str | None = None,
                 object_lock_xml: str = B2_OBJECT_LOCK) -> FakeRunner:
    return FakeRunner(b2_authorize=b2_authorize(name_prefix=prefix),
                      bucket={"versioning": http(200, B2_VERSIONING), "object-lock": http(200, object_lock_xml)},
                      snapshots=snapshots if snapshots is not None else snapshot(NOW - 3 * HOUR))


class MediaBackupTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        root = Path(self._tmp.name)
        self.state_dir = root / "state"
        self.secrets = write_media_secrets(root / "secrets")
        self.media_root = root / "media"
        (self.media_root / "derivatives").mkdir(parents=True)

    def run_media(self, action: str, runner: FakeRunner, *, target: Path | None = None) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            backup.run_action(action, runner, project="duora", state_dir=self.state_dir, egress_env=None,
                              clock=lambda: NOW, media_secrets_dir=self.secrets, media_root=self.media_root,
                              restore_target=target)

    def test_backup_copies_only_the_derivatives_and_keeps_14_days(self) -> None:
        runner = media_runner()

        self.run_media("media-backup", runner)

        self.assertEqual([["backup", "--tag", "derivatives", "--host", "duora", "--one-file-system",
                           str(self.media_root / "derivatives")],
                          ["forget", "--tag", "derivatives", "--host", "duora", "--keep-within", "14d", "--prune"]],
                         runner.restic_calls())

    def test_restic_reads_every_secret_from_a_file_and_never_from_the_command_line(self) -> None:
        runner = media_runner()

        self.run_media("media-backup", runner)

        command = next(command for command in runner.commands if "restic" in command)
        self.assertEqual(["env", f"RESTIC_REPOSITORY_FILE={self.secrets / 'restic_media_repository'}",
                          f"RESTIC_PASSWORD_FILE={self.secrets / 'restic_media_password'}",
                          f"AWS_SHARED_CREDENTIALS_FILE={self.secrets / 'restic_media_credentials'}", "restic",
                          "--option", "s3.region=us-east-005", "--retry-lock", "15m"],
                         command[:command.index("--cache-dir")])
        for secret in (MEDIA_SECRET, MEDIA_PASSWORD):
            self.assertFalse(any(secret in " ".join(c) for c in runner.commands))
            self.assertFalse(any(secret in (stdin or "") for stdin in runner.stdins))

    def test_backup_without_the_mounted_directory_fails_before_restic(self) -> None:
        (self.media_root / "derivatives").rmdir()
        runner = media_runner()

        with self.assertRaisesRegex(backup.BackupError, "derivatives"):
            self.run_media("media-backup", runner)
        self.assertEqual([], runner.restic_calls())

    def test_failed_backup_does_not_prune(self) -> None:
        runner = FakeRunner(fail_on="--one-file-system")

        with self.assertRaisesRegex(backup.BackupError, "restic backup"):
            self.run_media("media-backup", runner)
        self.assertFalse(any("forget" in call for call in runner.restic_calls()))

    def test_verify_proves_bucket_and_key_from_the_host_then_snapshot_age_and_structure(self) -> None:
        runner = media_runner()

        self.run_media("media-verify", runner)

        queries = [command for command in runner.commands if "s_client" in command]
        self.assertEqual(3, len(queries))
        self.assertTrue(all(command[:5] == ["timeout", "--kill-after=5", "30", "openssl", "s_client"]
                            for command in queries))
        self.assertEqual("api.backblazeb2.com:443", queries[0][queries[0].index("-connect") + 1])
        self.assertEqual(f"{MEDIA_BUCKET}.{B2_ENDPOINT}:443", queries[1][queries[1].index("-connect") + 1])
        self.assertEqual([["snapshots", "--json", "--latest", "1", "--tag", "derivatives", "--host", "duora"],
                          ["check"]], runner.restic_calls())

    def test_verify_refuses_a_key_outside_the_media_prefix(self) -> None:
        # A chave do pgBackRest (prefixo duora/) não serve para as fotos, e vice-versa.
        runner = media_runner(prefix="duora/")

        with self.assertRaisesRegex(backup.BackupError, "prefixo media/"):
            self.run_media("media-verify", runner)
        self.assertEqual([], runner.restic_calls())

    def test_verify_refuses_an_unlocked_bucket_before_touching_the_repository(self) -> None:
        runner = media_runner(object_lock_xml=B2_OBJECT_LOCK.replace("COMPLIANCE", "GOVERNANCE"))

        with self.assertRaisesRegex(backup.BackupError, "GOVERNANCE"):
            self.run_media("media-verify", runner)
        self.assertEqual([], runner.restic_calls())

    def test_verify_accepts_a_snapshot_on_the_age_limit_and_refuses_one_past_it(self) -> None:
        limit = backup.MAX_MEDIA_SNAPSHOT_AGE_HOURS * HOUR
        self.run_media("media-verify", media_runner(snapshots=snapshot(NOW - limit)))

        with self.assertRaisesRegex(backup.BackupError, "snapshot mais novo das fotos"):
            self.run_media("media-verify", media_runner(snapshots=snapshot(NOW - limit - 1)))

    def test_verify_refuses_no_snapshot_or_an_unreadable_listing(self) -> None:
        for listing in ("[]", "not json", '[{"id": "x"}]', '[{"time": "ontem"}]', '[{"time": "2026-10-16T02:00:00"}]',
                        "{}"):
            with self.subTest(listing=listing), self.assertRaises(backup.BackupError):
                self.run_media("media-verify", media_runner(snapshots=listing))

    def test_successful_verify_records_when_it_passed_for_the_deploy_gate(self) -> None:
        self.run_media("media-verify", media_runner())

        self.assertEqual(NOW, float((self.state_dir / backup.MEDIA_VERIFY_OK_FILE).read_text(encoding="utf-8")))

    def test_failed_verify_does_not_record_success(self) -> None:
        with self.assertRaises(backup.BackupError):
            self.run_media("media-verify", media_runner(snapshots="[]"))

        self.assertFalse((self.state_dir / backup.MEDIA_VERIFY_OK_FILE).exists())

    def test_weekly_check_reads_all_the_data(self) -> None:
        runner = media_runner()

        self.run_media("media-check", runner)

        self.assertEqual([["check", "--read-data"]], runner.restic_calls())

    def test_init_only_runs_on_a_protected_bucket(self) -> None:
        runner = media_runner()
        self.run_media("media-init", runner)
        self.assertEqual([["init"]], runner.restic_calls())

        unprotected = media_runner(object_lock_xml='<ObjectLockConfiguration/>')
        with self.assertRaises(backup.BackupError):
            self.run_media("media-init", unprotected)
        self.assertEqual([], unprotected.restic_calls())

    def test_restore_goes_to_an_empty_staging_directory(self) -> None:
        target = Path(self._tmp.name) / "restore-fotos"
        runner = media_runner()

        self.run_media("media-restore", runner, target=target)

        self.assertEqual([["restore", "latest", "--tag", "derivatives", "--host", "duora", "--target", str(target.resolve())]],
                         runner.restic_calls())

    def test_restore_refuses_unsafe_targets(self) -> None:
        busy = Path(self._tmp.name) / "ocupado"
        busy.mkdir()
        (busy / "arquivo").write_text("x", encoding="utf-8")
        for target in (None, Path("relativo"), busy, self.media_root / "derivatives", self.media_root / "novo"):
            with self.subTest(target=target):
                runner = media_runner()
                with self.assertRaises(backup.BackupError):
                    self.run_media("media-restore", runner, target=target)
                self.assertEqual([], runner.restic_calls())

    def test_repository_files_out_of_shape_fail_closed(self) -> None:
        cases = {
            "outro prefixo": {"repository": f"s3:https://{B2_ENDPOINT}/{MEDIA_BUCKET}/duora\n"},
            "sem s3:": {"repository": f"https://{B2_ENDPOINT}/{MEDIA_BUCKET}/media\n"},
            "endpoint sem região": {"repository": f"s3:https://storage.example.test/{MEDIA_BUCKET}/media\n"},
            "endpoint fora do B2": {"repository": f"s3:https://s3.us-east-1.example.test/{MEDIA_BUCKET}/media\n"},
            "credencial sem segredo": {"credentials": f"[default]\naws_access_key_id = {MEDIA_KEY_ID}\n"},
            "credencial ilegível": {"credentials": "sem seção"},
        }
        for name, override in cases.items():
            with self.subTest(case=name):
                secrets = write_media_secrets(Path(self._tmp.name) / name.replace(" ", "-"), **override)
                with self.assertRaises(backup.BackupError) as raised:
                    backup.read_media_settings(secrets)
                self.assertNotIn(MEDIA_SECRET, str(raised.exception))

    def test_missing_secret_files_fail_closed(self) -> None:
        (self.secrets / "restic_media_password").unlink()

        with self.assertRaisesRegex(backup.BackupError, "restic_media_password"):
            self.run_media("media-backup", media_runner())

    def test_cli_requires_target_only_for_restore(self) -> None:
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, backup.main(["media-restore", "--media-secrets-dir", str(self.secrets),
                                             "--media-root", str(self.media_root)], run=media_runner()))


class MediaBackupUnitsTests(unittest.TestCase):
    role = Path(__file__).resolve().parents[2] / "infra" / "ansible" / "roles" / "backup"

    def test_timers_run_backup_daily_verify_after_it_and_full_read_weekly(self) -> None:
        expected = {
            "duora-backup-media.timer": ("Unit=duora-media-backup@media-backup.service",
                                         "OnCalendar=*-*-* 02:30:00 UTC"),
            "duora-backup-media-verify.timer": ("Unit=duora-media-backup@media-verify.service",
                                                "OnCalendar=*-*-* 05:30:00 UTC"),
            "duora-backup-media-check.timer": ("Unit=duora-media-backup@media-check.service",
                                               "OnCalendar=Thu *-*-* 06:30:00 UTC"),
        }
        tasks = (self.role / "tasks" / "main.yml").read_text(encoding="utf-8")
        for timer, lines in expected.items():
            with self.subTest(timer=timer):
                text = (self.role / "files" / timer).read_text(encoding="utf-8")
                for line in lines:
                    self.assertIn(line, text)
                self.assertIn("Persistent=true", text)
                # Instalado e ligado (ou desligado) pelo role.
                self.assertEqual(2, tasks.count(timer))

    def test_media_actions_alert_on_failure_and_do_not_depend_on_docker(self) -> None:
        unit = (self.role / "templates" / "duora-media-backup@.service.j2").read_text(encoding="utf-8")

        self.assertIn("OnFailure=duora-backup-failed@%n.service", unit)
        self.assertNotIn("docker", unit.lower())

    def test_media_actions_use_the_inventory_media_directory(self) -> None:
        # Um duora_media_dir trocado no inventário vale para o Compose (DUORA_MEDIA_DIR) e para o backup das fotos;
        # o MEDIA_ROOT fixo do wrapper é só o padrão de quem o chama à mão.
        unit = (self.role / "templates" / "duora-media-backup@.service.j2").read_text(encoding="utf-8")
        tasks = (self.role / "tasks" / "main.yml").read_text(encoding="utf-8")

        self.assertIn('ExecStart=/usr/local/sbin/duora-backup %i --media-root "{{ duora_media_dir }}"\n', unit)
        self.assertIn("    src: duora-media-backup@.service.j2\n"
                      "    dest: /etc/systemd/system/duora-media-backup@.service\n", tasks)
        self.assertFalse((self.role / "files" / "duora-media-backup@.service").exists())

    def test_ansible_writes_the_mount_marker_the_boot_requires_in_each_area(self) -> None:
        tasks = (self.role.parents[0] / "app_dirs" / "tasks" / "main.yml").read_text(encoding="utf-8")
        store = (Path(__file__).resolve().parents[2] / "backend" / "src" / "Duora.Infrastructure" / "Media"
                 / "FileSystemObjectStore.cs").read_text(encoding="utf-8")

        self.assertIn('AreaMarkerFileName = ".duora-media-area"', store)
        self.assertIn('dest: "{{ duora_media_dir }}/{{ item }}/.duora-media-area"', tasks)
        self.assertIn('["quarantine", "derivatives"]', store)

    def test_media_root_and_switch_match_the_inventory_defaults(self) -> None:
        group_vars = (self.role.parents[1] / "group_vars" / "all.yml").read_text(encoding="utf-8")

        self.assertIn(f"duora_media_dir: {backup.MEDIA_ROOT}\n", group_vars)
        self.assertIn("duora_media_backup_enabled: false\n", group_vars)


def _without(entry: dict, *path: str) -> dict:
    """Cópia de um backup do info sem o campo indicado (ex.: "timestamp", "stop")."""
    copy = json.loads(json.dumps(entry))
    target = copy
    for key in path[:-1]:
        target = target[key]
    del target[path[-1]]
    return copy


class FailClosedVerifyTests(unittest.TestCase):
    """O verify decide se o deploy segue: tudo que não prova backup saudável BLOQUEIA (BackupError)."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.state_dir = Path(self._tmp.name)
        self.egress_env = write_egress_env(self.state_dir)

    def assert_blocks(self, runner: FakeRunner) -> None:
        with self.assertRaises(backup.BackupError):
            backup.run_action("verify", runner, project="duora", state_dir=self.state_dir,
                              egress_env=self.egress_env)

    def test_unusable_info_output_blocks(self) -> None:
        healthy = backup_entry(stop=NOW - HOUR)
        cases = {
            "vazia": "",
            "só espaços": "   \n",
            "não é JSON": "not json",
            "JSON truncado": info_json()[:-5],
            "objeto em vez de lista": json.dumps({"name": "duora"}),
            "lista de não-objetos": "[1, 2]",
            "stanza ausente (lista vazia)": "[]",
            "duas stanzas": json.dumps(json.loads(info_json()) * 2),
            "sem campo status": json.dumps([{k: v for k, v in json.loads(info_json())[0].items() if k != "status"}]),
            "status não é objeto": json.dumps([{**json.loads(info_json())[0], "status": "ok"}]),
            "status.code como texto": json.dumps([{**json.loads(info_json())[0],
                                                   "status": {"code": "0", "message": "ok"}}]),
            "sem campo backup": json.dumps([{k: v for k, v in json.loads(info_json())[0].items() if k != "backup"}]),
            "backup não é lista": json.dumps([{**json.loads(info_json())[0], "backup": {"x": 1}}]),
            "backup sem timestamp": info_json(backups=[_without(healthy, "timestamp")]),
            "backup sem timestamp.stop": info_json(backups=[_without(healthy, "timestamp", "stop")]),
            "timestamp.stop como texto": info_json(backups=[{**healthy, "timestamp": {"start": 1, "stop": "agora"}}]),
            "backup sem campo error": info_json(backups=[_without(healthy, "error")]),
            "sem campo archive": json.dumps([{k: v for k, v in json.loads(info_json())[0].items() if k != "archive"}]),
            "nenhum backup": info_json(backups=[]),
        }
        for label, info in cases.items():
            with self.subTest(info=label):
                self.assert_blocks(FakeRunner(info=info))

    def test_unusable_archiver_output_blocks(self) -> None:
        cases = {
            "vazia": "",
            "lixo": "garbage",
            "colunas a mais": f"{NOW}||{NOW}|x",
            "número inválido": f"ontem||{NOW}",
            "relógio do banco ausente": f"{NOW}||",
            "nunca arquivou": f"||{NOW}",
            "archive mais velho que o limite": f"{NOW - 301}||{NOW}",
            "falha depois do último sucesso": f"{NOW - 30}|{NOW - 10}|{NOW}",
        }
        for label, line in cases.items():
            with self.subTest(archiver=label):
                self.assert_blocks(FakeRunner(archiver_line=line))

    def test_non_zero_exit_of_any_step_blocks(self) -> None:
        for step in ("iptables", "docker ps", " cat ", "?versioning=", "?object-lock=", " check", "--output=json",
                     "pg_stat_archiver"):
            with self.subTest(step=step):
                self.assert_blocks(FakeRunner(fail_on=step))

    def test_timeout_of_a_command_is_a_backup_error(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "excedeu 1 s"):
            backup.run_command([sys.executable, "-c", "import time; time.sleep(5)"], timeout_seconds=1)

    def test_missing_executable_is_a_backup_error(self) -> None:
        with self.assertRaises(backup.BackupError):
            backup.run_command(["/nonexistent/duora-no-such-binary"])

    def test_unexpected_exception_makes_the_cli_fail(self) -> None:
        def exploding_runner(command: list[str], *, capture: bool = False, timeout_seconds: int = 0,
                             stdin: str | None = None) -> subprocess.CompletedProcess[str]:
            raise RuntimeError("inesperado")

        with contextlib.redirect_stderr(io.StringIO()) as stderr:
            code = backup.main(["verify", "--state-dir", str(self.state_dir), "--egress-env", str(self.egress_env)],
                               run=exploding_runner)

        self.assertEqual(1, code)
        self.assertIn("inesperado", stderr.getvalue())

    def test_healthy_verify_makes_the_cli_succeed(self) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            code = backup.main(["verify", "--state-dir", str(self.state_dir), "--egress-env", str(self.egress_env)],
                               run=FakeRunner())

        self.assertEqual(0, code)


class CliTests(unittest.TestCase):
    def test_unknown_action_is_rejected_by_the_parser(self) -> None:
        with self.assertRaises(SystemExit):
            backup.main(["restore"])

    def test_extra_arguments_are_not_passed_through(self) -> None:
        with self.assertRaises(SystemExit):
            backup.main(["full", "--", "--repo1-path=/outro"])


if __name__ == "__main__":
    unittest.main()
