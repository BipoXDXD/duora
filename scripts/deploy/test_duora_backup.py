"""Testes do wrapper de backup. Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v"""

import contextlib
import io
import json
import subprocess
import sys
import unittest
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


CONTAINER_ID = "c0ffee123456"


class FakeRunner:
    """Responde como Docker e pgBackRest responderiam; falha no comando que contém fail_on."""

    def __init__(self, *, containers: str = CONTAINER_ID + "\n", fail_on: str | None = None,
                 info: str | None = None, archiver_line: str | None = None, verify: str = "") -> None:
        self.commands: list[list[str]] = []
        self._containers = containers
        self._fail_on = fail_on
        self._info = info if info is not None else info_json()
        self._verify = verify
        self._archiver_line = archiver_line if archiver_line is not None else f"{NOW - 5}||{NOW}\n"

    # capture e timeout_seconds não mudam a resposta do dublê, mas ficam na assinatura para ela casar com o
    # Protocol duora_backup.Runner (o Pyright confere o FakeRunner passado a run_action).
    def __call__(self, command: list[str], *, capture: bool = False,
                 timeout_seconds: int = 0) -> subprocess.CompletedProcess[str]:
        self.commands.append(command)
        joined = " ".join(command)
        if self._fail_on is not None and self._fail_on in joined:
            return subprocess.CompletedProcess(command, 1, "", "falha simulada")
        if command[:2] == ["docker", "ps"]:
            return subprocess.CompletedProcess(command, 0, self._containers, "")
        if "--output=json" in command:
            return subprocess.CompletedProcess(command, 0, self._info, "")
        if "pg_stat_archiver" in joined:
            return subprocess.CompletedProcess(command, 0, self._archiver_line, "")
        if "verify" in command and "pgbackrest" in command:
            return subprocess.CompletedProcess(command, 0, self._verify, "")
        return subprocess.CompletedProcess(command, 0, "", "")

    def pgbackrest_calls(self) -> list[list[str]]:
        return [command[command.index("pgbackrest"):] for command in self.commands if "pgbackrest" in command]


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

        def runner(command: list[str], *, capture: bool = False,
                   timeout_seconds: int = 0) -> subprocess.CompletedProcess[str]:
            if "pgbackrest" in command:
                seen.append(backup.deploy_in_progress(self.state_dir))
            return FakeRunner()(command, capture=capture, timeout_seconds=timeout_seconds)

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


def verify_output(ranges: list[tuple[str, str, str]], *, status: str = "ok", backup_label: str = "NEWEST",
                  backup_status: str = "valid", counters: str = "missing: 0, checksum invalid: 0, size invalid: 0, other: 0",
                  ) -> str:
    """Saída de `pgbackrest verify --verbose --log-level-console=detail` (pgBackRest 2.59.3, capturada no drill)."""
    lines = ["2026-10-15 22:46:45.000 P00   INFO: verify command begin 2.59.3: --stanza=duora"]
    lines += [f"2026-10-15 22:46:45.891 P00 DETAIL: archiveId: {archive}, wal start: {start}, wal stop: {stop}"
              for archive, start, stop in ranges]
    lines += ["2026-10-15 22:46:45.891 P00   INFO: stanza: duora",
              f"                                    status: {status}",
              "                                      archiveId: 18-1, total WAL checked: 4, total valid WAL: 4",
              f"                                        {counters}",
              f"                                      backup: {backup_label}, status: {backup_status}, "
              "total files checked: 1268, total valid files: 1268",
              f"                                        {counters}",
              "2026-10-15 22:46:45.891 P00   INFO: verify command end: completed successfully (476ms)"]
    return "\n".join(lines) + "\n"


def wal(timeline: int, log: int, segment: int) -> str:
    return f"{timeline:08X}{log:08X}{segment:08X}"


def newest_backup_info(*, wal_start: str = wal(1, 0, 2), wal_stop: str = wal(1, 0, 2)) -> str:
    return info_json(backups=[backup_entry(stop=NOW - 100 * HOUR, label="OLDER", wal_start=wal(1, 0, 1),
                                           wal_stop=wal(1, 0, 1)),
                              backup_entry(stop=NOW - HOUR, label="NEWEST", wal_start=wal_start, wal_stop=wal_stop)])


class RepositoryVerifyTests(unittest.TestCase):
    """verify-repo: o pgbackrest verify não falha com lacuna de WAL (só lista as faixas); a decisão é daqui."""

    def problems(self, verify_text: str, info_text: str | None = None) -> list[str]:
        return backup.repository_verify_problems(verify_text, info_text or newest_backup_info())

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

        self.assertIn("pgbackrest verify não listou WAL do banco do backup mais novo (archiveId *-1)", problems)

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
        for label, text in {"vazia": "", "lixo": "garbage\n",
                            "sem contadores": verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 9))],
                                                            counters="")}.items():
            with self.subTest(output=label):
                self.assertNotEqual([], self.problems(text))

    def test_segment_beyond_16_mib_layout_is_a_problem(self) -> None:
        problems = self.problems(verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 0x100))]))

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

    def run_action(self, runner: FakeRunner, lock_wait_seconds: int = backup.LOCK_WAIT_SECONDS) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            backup.run_action("verify-repo", runner, project="duora", state_dir=self.state_dir,
                              egress_env=None, lock_wait_seconds=lock_wait_seconds)

    def test_runs_info_then_native_verify_with_the_wal_ranges(self) -> None:
        runner = FakeRunner(info=newest_backup_info(), verify=self.healthy)

        self.run_action(runner)

        self.assertEqual([["pgbackrest", "--stanza=duora", "info", "--output=json"],
                          ["pgbackrest", "--stanza=duora", "verify", "--verbose", "--log-level-console=detail"]],
                         runner.pgbackrest_calls())

    def test_gap_fails_the_action(self) -> None:
        runner = FakeRunner(info=newest_backup_info(), verify=verify_output([("18-1", wal(1, 0, 1), wal(1, 0, 2)),
                                                                             ("18-1", wal(1, 0, 4), wal(1, 0, 4))]))

        with self.assertRaisesRegex(backup.BackupError, "lacuna de WAL"):
            self.run_action(runner)

    def test_failed_native_verify_fails_the_action(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "verify --verbose"):
            self.run_action(FakeRunner(info=newest_backup_info(), verify=self.healthy, fail_on=" verify "))

    def test_waits_for_the_deploy_lock(self) -> None:
        with backup.deploy_lock(self.state_dir, wait_seconds=0):
            with self.assertRaisesRegex(backup.BackupError, "deploy ou backup em andamento"):
                self.run_action(FakeRunner(info=newest_backup_info(), verify=self.healthy), lock_wait_seconds=0)

    def test_daily_timer_runs_verify_repo(self) -> None:
        role = Path(__file__).resolve().parents[2] / "infra" / "ansible" / "roles" / "backup"
        timer = (role / "files" / "duora-backup-verify-repo.timer").read_text(encoding="utf-8")
        tasks = (role / "tasks" / "main.yml").read_text(encoding="utf-8")

        self.assertIn("Unit=duora-backup@verify-repo.service", timer)
        self.assertIn("OnCalendar=*-*-* 05:00:00 UTC", timer)
        self.assertEqual(2, tasks.count("duora-backup-verify-repo.timer"))


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
        for step in ("iptables", "docker ps", " check", "--output=json", "pg_stat_archiver"):
            with self.subTest(step=step):
                self.assert_blocks(FakeRunner(fail_on=step))

    def test_timeout_of_a_command_is_a_backup_error(self) -> None:
        with self.assertRaisesRegex(backup.BackupError, "excedeu 1 s"):
            backup.run_command([sys.executable, "-c", "import time; time.sleep(5)"], timeout_seconds=1)

    def test_missing_executable_is_a_backup_error(self) -> None:
        with self.assertRaises(backup.BackupError):
            backup.run_command(["/nonexistent/duora-no-such-binary"])

    def test_unexpected_exception_makes_the_cli_fail(self) -> None:
        def exploding_runner(command: list[str], *, capture: bool = False,
                             timeout_seconds: int = 0) -> subprocess.CompletedProcess[str]:
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
