#!/usr/bin/env python3
"""Drill de restore PITR do PostgreSQL do Duora, de ponta a ponta, numa máquina com Docker (spec 18.5).

    docker buildx build -f infra/images/postgres/Dockerfile -t duora-postgres:drill --load .
    python3 scripts/backup/restore_drill.py --image duora-postgres:drill

Sobe postgres, backup (repository host) e o job de restore do compose.prod.yml (mesmos secrets, volumes, redes e
configuração), com um MinIO como dublê do repositório S3 e um host "internet" arbitrário na rede de saída do
backup (scripts/backup/compose.drill.yml; nunca produção), e então:

  1. isolamento: o postgres não alcança o host "internet" (nem por nome, nem por IP) nem o S3; o backup alcança
     o S3; o postgres alcança o servidor TLS do backup;
  2. stanza-create e backup full pelo duora-backup;
  3. grava linhas "before", marca o ponto de recuperação e grava linhas "after";
  4. mede o RPO natural: tempo entre o commit das linhas "after" e o WAL delas confirmado no repositório, só com
     archive_timeout (sem troca forçada);
  5. duora-backup verify;
  6. destrói os containers postgres e backup e o volume do banco;
  7. restore PITR até o ponto marcado (job restore), sobe backup e postgres e espera a promoção;
  8. confere que as linhas "before" existem, as "after" não, e que a timeline avançou;
  9. duora-backup verify de novo: o banco restaurado volta a arquivar.

RTO observado = da destruição do volume até os dados conferidos (passos 6 a 8). Dados sintéticos; tudo é
derrubado no fim (--keep mantém para inspeção). Só biblioteca padrão; precisa de docker e openssl.
"""

from __future__ import annotations

import argparse
import json
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
from collections.abc import Callable
from dataclasses import asdict, dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "deploy"))

# scripts/deploy não é pacote: entra no path acima. Mesmo gerador de certificados que a VPS usa.
import duora_secrets

COMPOSE_FILE = REPO_ROOT / "infra" / "compose" / "compose.prod.yml"
DRILL_FILE = REPO_ROOT / "scripts" / "backup" / "compose.drill.yml"
BACKUP_SCRIPT = REPO_ROOT / "scripts" / "deploy" / "duora_backup.py"
PROJECT = "duora-drill"
BUCKET = "duora-drill"
ROWS_PER_PHASE = 100
# Espera entre as escritas e o ponto de recuperação: o alvo cai com folga entre os dois commits.
TARGET_MARGIN_SECONDS = 2
# archive_timeout é 60 s; acima disso com folga o arquivamento natural falhou.
ARCHIVE_WAIT_SECONDS = 180
PROMOTION_WAIT_SECONDS = 300
CONNECT_TIMEOUT_SECONDS = 5
COMMAND_TIMEOUT_SECONDS = 15 * 60
PGBACKREST_TLS_PORT = 8432
S3_PORT = 9000
UNUSED_IMAGE = "unused.invalid/duora-unused@sha256:" + "0" * 64


class DrillFailure(Exception):
    """O drill não comprovou o restore; a mensagem diz em qual passo."""


@dataclass
class DrillReport:
    image: str
    target_time: str = ""
    rpo_seconds: float = 0.0
    rto_seconds: float = 0.0
    timeline_after_restore: int = 0
    isolation: dict[str, bool] = field(default_factory=dict)
    phases_seconds: dict[str, float] = field(default_factory=dict)


@dataclass(frozen=True)
class RecoveryTarget:
    recovery_time: str
    after_lsn: str
    after_at: float


def main() -> int:
    parser = argparse.ArgumentParser(description="Drill de restore PITR (pgBackRest + MinIO de teste)")
    parser.add_argument("--image", required=True, help="imagem do postgres sob teste (infra/images/postgres)")
    parser.add_argument("--report", type=Path, help="grava o relatório em JSON neste arquivo")
    parser.add_argument("--keep", action="store_true", help="não derruba o ambiente no fim")
    args = parser.parse_args()

    workdir = Path(tempfile.mkdtemp(prefix="duora-drill-"))
    drill = Drill(args.image, workdir)
    try:
        report = drill.run()
    except DrillFailure as error:
        print(f"[drill] FALHOU: {error}", flush=True)
        drill.dump_logs()
        return 1
    finally:
        if not args.keep:
            drill.teardown()
            shutil.rmtree(workdir, ignore_errors=True)

    print(f"[drill] OK: RPO observado {report.rpo_seconds:.1f} s, RTO observado {report.rto_seconds:.1f} s, "
          f"timeline {report.timeline_after_restore}", flush=True)
    rendered = json.dumps(asdict(report), indent=2, ensure_ascii=False)
    print(rendered, flush=True)
    if args.report is not None:
        args.report.write_text(rendered + "\n", encoding="utf-8")
    return 0


class Drill:
    def __init__(self, image: str, workdir: Path) -> None:
        self.report = DrillReport(image=image)
        self._workdir = workdir
        self._env_file = workdir / "drill.env"
        self._state_dir = workdir / "state"
        self._compose = ["docker", "compose", "--project-name", PROJECT, "--file", str(COMPOSE_FILE),
                         "--file", str(DRILL_FILE), "--env-file", str(self._env_file)]
        self._prepared = False

    def run(self) -> DrillReport:
        self._phase("preparação (segredos, certificados, MinIO, postgres, backup)", self._prepare)
        self._phase("isolamento de rede", self._check_isolation)
        self._phase("stanza-create e backup full", self._first_backup)
        target = self._phase("escrita before/after e ponto de recuperação", self._write_rows)
        self._phase("RPO natural (archive_timeout)", lambda: self._measure_rpo(target))
        self._phase("duora-backup verify", lambda: self._duora_backup("verify"))

        started = time.monotonic()
        self._phase("destruição dos containers e do volume", self._destroy)
        self._phase("restore PITR (job restore)", lambda: self._restore(target.recovery_time))
        self._phase("subida e promoção", self._start_restored)
        self._phase("conferência dos dados", self._verify_restored_data)
        self.report.rto_seconds = round(time.monotonic() - started, 1)

        self._phase("duora-backup verify pós-restore", lambda: self._duora_backup("verify"))
        return self.report

    def _phase[T](self, name: str, action: Callable[[], T]) -> T:
        print(f"[drill] {name}", flush=True)
        started = time.monotonic()
        result = action()
        self.report.phases_seconds[name] = round(time.monotonic() - started, 1)
        return result

    # --- Preparação -------------------------------------------------------------------------------------------------

    def _prepare(self) -> None:
        secrets_dir = self._workdir / "secrets"
        certs_dir = self._workdir / "certs"
        # mkdtemp cria 0700; o postgres e o backup (999) e o MinIO (1001) do container precisam atravessar e ler.
        for directory in (self._workdir, secrets_dir, certs_dir):
            directory.mkdir(exist_ok=True)
            directory.chmod(0o755)
        s3_key = "drill" + secrets.token_hex(8)
        s3_secret = secrets.token_urlsafe(24)
        self._write_secrets(secrets_dir, s3_key, s3_secret)
        self._own_secrets_like_the_vps(secrets_dir)
        self._write_s3_certificate(certs_dir)

        env = {f"DUORA_{name.upper()}_IMAGE": UNUSED_IMAGE for name in ("web", "worker", "edge", "migrator")}
        env.update({
            "DUORA_POSTGRES_IMAGE": self.report.image,
            "DUORA_SECRETS_DIR": str(secrets_dir),
            "DUORA_DOMAIN": "drill.invalid",
            "DUORA_TLS": "internal",
            "DUORA_HTTP_PORT": "0",
            "DUORA_HTTPS_PORT": "0",
            "DRILL_S3_KEY": s3_key,
            "DRILL_S3_SECRET": s3_secret,
            "DRILL_S3_BUCKET": BUCKET,
            "DRILL_CERTS_DIR": str(certs_dir),
        })
        self._env_file.write_text("".join(f"{key}={value}\n" for key, value in env.items()), encoding="utf-8")
        self._prepared = True

        # Sobra de um --keep anterior (volume com banco velho) invalidaria o drill.
        _run(self._compose + ["down", "--volumes", "--remove-orphans"], capture=True)
        _run(self._compose + ["up", "--detach", "--wait", "s3", "internet"])
        _run(self._compose + ["up", "--detach", "--wait", "--no-deps", "postgres", "backup"])

    @staticmethod
    def _write_secrets(secrets_dir: Path, s3_key: str, s3_secret: str) -> None:
        # Certificados pelo mesmo gerador da VPS (duora-secrets init-backup-tls).
        duora_secrets.init_backup_tls(secrets_dir, change_owner=False)
        synthetic = {
            "postgres_superuser_password": secrets.token_urlsafe(24),
            "db_migrator_password": secrets.token_urlsafe(24),
            "db_app_password": secrets.token_urlsafe(24),
            "duora_app_connection": "unused",
            "duora_migrator_connection": "unused",
            "pgbackrest_repo.conf": "\n".join([
                "[global]",
                "repo1-s3-endpoint=s3",
                f"repo1-s3-bucket={BUCKET}",
                "repo1-s3-region=us-east-1",
                f"repo1-s3-key={s3_key}",
                f"repo1-s3-key-secret={s3_secret}",
                # Específico do dublê: MinIO por caminho, porta 9000 e CA autoassinada (TLS continua verificado).
                "repo1-s3-uri-style=path",
                f"repo1-storage-port={S3_PORT}",
                "repo1-storage-ca-file=/etc/pgbackrest/drill-ca.crt",
            ]) + "\n",
            "pgbackrest_cipher.conf": f"[global]\nrepo1-cipher-pass={secrets.token_urlsafe(48)}\n",
        }
        for name, content in synthetic.items():
            (secrets_dir / name).write_text(content, encoding="utf-8")

    def _own_secrets_like_the_vps(self, secrets_dir: Path) -> None:
        # Como na VPS (duora-secrets como root): dono 999 e 0400. O pgBackRest recusa chave TLS com permissão de
        # grupo ou de outros. Um container root da própria imagem faz o chown, sem sudo no host do drill.
        _run(["docker", "run", "--rm", "--user", "0", "--network", "none", "--volume", f"{secrets_dir}:/secrets",
              "--entrypoint", "sh", self.report.image, "-c",
              "chown 999:999 /secrets/* && chmod 0400 /secrets/*"])

    @staticmethod
    def _write_s3_certificate(certs_dir: Path) -> None:
        _run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=s3",
              "-addext", "subjectAltName=DNS:s3,DNS:localhost", "-keyout", str(certs_dir / "private.key"),
              "-out", str(certs_dir / "public.crt")], capture=True)
        for name in ("private.key", "public.crt"):
            (certs_dir / name).chmod(0o644)

    # --- Isolamento -------------------------------------------------------------------------------------------------

    def _check_isolation(self) -> None:
        internet_ip = _run(["docker", "inspect", "--format",
                            "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}",
                            f"{PROJECT}-internet-1"], capture=True).strip()
        expectations = {
            "postgres -> internet (nome)": ("postgres", "internet", S3_PORT, False),
            f"postgres -> internet ({internet_ip})": ("postgres", internet_ip, S3_PORT, False),
            "postgres -> s3": ("postgres", "s3", S3_PORT, False),
            "postgres -> backup (TLS do pgBackRest)": ("postgres", "backup", PGBACKREST_TLS_PORT, True),
            "backup -> s3": ("backup", "s3", S3_PORT, True),
        }
        for label, (service, host, port, expected) in expectations.items():
            reached = self._can_connect(service, host, port)
            self.report.isolation[label] = reached
            if reached != expected:
                raise DrillFailure(f"isolamento: {label} {'conectou' if reached else 'não conectou'}; "
                                   f"esperado {'conectar' if expected else 'falhar'}")
        # O postgres não recebe credencial do S3 nem a senha de cifra: só o backup.
        listing = _run(self._compose + ["exec", "--no-TTY", "postgres", "ls", "-A", "/etc/pgbackrest/conf.d",
                                        "/run/secrets"], capture=True)
        has_repo_credentials = any(name in listing for name in ("repo.conf", "cipher.conf", "pgbackrest_repo",
                                                                "pgbackrest_cipher", "tls_server.key"))
        self.report.isolation["postgres tem credenciais do repositório"] = has_repo_credentials
        if has_repo_credentials:
            raise DrillFailure(f"o postgres enxerga credenciais do repositório:\n{listing}")

    def _can_connect(self, service: str, host: str, port: int) -> bool:
        # bash da imagem: abre um socket TCP com teto de tempo; resolução de nome falha também conta como falha.
        probe = f"exec 3<>/dev/tcp/{host}/{port}"
        result = subprocess.run(self._compose + ["exec", "--no-TTY", service, "timeout",
                                                 str(CONNECT_TIMEOUT_SECONDS), "bash", "-c", probe],
                                capture_output=True, text=True, check=False, timeout=COMMAND_TIMEOUT_SECONDS)
        return result.returncode == 0

    # --- Backup, escrita e RPO --------------------------------------------------------------------------------------

    def _first_backup(self) -> None:
        self._duora_backup("stanza-create")
        self._duora_backup("full")

    def _write_rows(self) -> RecoveryTarget:
        self._sql("create table drill_events (id bigint generated always as identity primary key, "
                  "phase text not null, written_at timestamptz not null default clock_timestamp())")
        self._insert_phase("before")
        time.sleep(TARGET_MARGIN_SECONDS)
        recovery_time = self._sql("select to_char(clock_timestamp() at time zone 'UTC', "
                                  "'YYYY-MM-DD HH24:MI:SS.US') || '+00'")
        time.sleep(TARGET_MARGIN_SECONDS)
        self._insert_phase("after")
        after_lsn, after_at = self._sql("select pg_current_wal_lsn(), extract(epoch from clock_timestamp())").split("|")
        self.report.target_time = recovery_time
        return RecoveryTarget(recovery_time, after_lsn, float(after_at))

    def _insert_phase(self, phase: str) -> None:
        self._sql(f"insert into drill_events (phase) select '{phase}' from generate_series(1, {ROWS_PER_PHASE})")

    def _measure_rpo(self, target: RecoveryTarget) -> None:
        """Espera o segmento com o commit "after" chegar ao repositório sem forçar a troca (archive_timeout)."""
        segment = self._sql(f"select pg_walfile_name('{target.after_lsn}')")
        deadline = time.monotonic() + ARCHIVE_WAIT_SECONDS
        while time.monotonic() < deadline:
            archived_wal, archived_at = self._sql(
                "select coalesce(last_archived_wal, ''), coalesce(extract(epoch from last_archived_time), 0) "
                "from pg_stat_archiver").split("|")
            if archived_wal >= segment:
                self.report.rpo_seconds = round(float(archived_at) - target.after_at, 1)
                return
            time.sleep(1)
        raise DrillFailure(f"segmento {segment} não foi arquivado em {ARCHIVE_WAIT_SECONDS} s")

    # --- Desastre e restore -----------------------------------------------------------------------------------------

    def _destroy(self) -> None:
        # O backup monta o PGDATA só leitura: sai junto para o volume poder ser removido.
        _run(self._compose + ["rm", "--stop", "--force", "postgres", "backup"])
        _run(["docker", "volume", "rm", f"{PROJECT}_pgdata"])

    def _restore(self, recovery_time: str) -> None:
        _run(self._compose + ["--profile", "restore", "run", "--rm", "--no-deps", "restore",
                              "--type=time", f"--target={recovery_time}", "--target-action=promote", "restore"])

    def _start_restored(self) -> None:
        # O backup primeiro: o restore_command do postgres busca o WAL nele (archive-get por TLS).
        _run(self._compose + ["up", "--detach", "--wait", "--no-deps", "backup"])
        _run(self._compose + ["up", "--detach", "--wait", "--no-deps", "postgres"])
        deadline = time.monotonic() + PROMOTION_WAIT_SECONDS
        while time.monotonic() < deadline:
            if self._sql("select pg_is_in_recovery()") == "f":
                return
            time.sleep(1)
        raise DrillFailure(f"banco restaurado não saiu de recovery em {PROMOTION_WAIT_SECONDS} s")

    def _verify_restored_data(self) -> None:
        counts = dict(line.split("|") for line in self._sql(
            "select phase, count(*) from drill_events group by phase order by phase").splitlines())
        if counts != {"before": str(ROWS_PER_PHASE)}:
            raise DrillFailure(f"dados restaurados errados: esperado só before={ROWS_PER_PHASE}, recebido {counts}")
        timeline = int(self._sql("select timeline_id from pg_control_checkpoint()"))
        if timeline < 2:
            raise DrillFailure(f"timeline {timeline} depois do PITR; esperado nova timeline (>= 2)")
        self.report.timeline_after_restore = timeline

    # --- Infra ------------------------------------------------------------------------------------------------------

    def _duora_backup(self, action: str) -> None:
        _run([sys.executable, str(BACKUP_SCRIPT), action, "--compose-project", PROJECT,
              "--state-dir", str(self._state_dir)])

    def _sql(self, sql: str) -> str:
        return _run(self._compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--quiet",
                                     "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1",
                                     "--username", "postgres", "--dbname", "postgres", "--command", sql],
                    capture=True).strip()

    def dump_logs(self) -> None:
        if self._prepared:
            subprocess.run(self._compose + ["logs", "--no-color", "--tail", "60", "postgres", "backup", "s3"],
                           check=False)

    def teardown(self) -> None:
        if self._prepared:
            subprocess.run(self._compose + ["--profile", "restore", "down", "--volumes", "--remove-orphans"],
                           check=False, capture_output=True)


def _run(command: list[str], *, capture: bool = False) -> str:
    try:
        result = subprocess.run(command, text=True, check=False, capture_output=capture,
                                timeout=COMMAND_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired as error:
        raise DrillFailure(f"comando excedeu {COMMAND_TIMEOUT_SECONDS} s: {' '.join(command[:8])}") from error
    if result.returncode != 0:
        detail = (result.stderr or "").strip().splitlines()[-5:] if capture else ["veja a saída acima"]
        raise DrillFailure(f"falhou ({result.returncode}): {' '.join(command[:12])}\n" + "\n".join(detail))
    return result.stdout if capture else ""


if __name__ == "__main__":
    sys.exit(main())
