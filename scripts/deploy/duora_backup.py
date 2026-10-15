#!/usr/bin/env python3
"""Wrapper restrito do pgBackRest do Duora na VPS (spec 18.2 e 18.5).

    duora-backup stanza-create | full | diff | verify | info

Roda o pgBackRest dentro do container backup (repository host: credenciais do S3, PGDATA e socket do postgres só
leitura) por `docker exec`, como root do host, chamado pelos timers systemd e pelo duora-deploy. Só as ações acima existem;
nenhum argumento é repassado ao pgBackRest. A aplicação não recebe o socket do Docker nem as credenciais do
repositório.

`verify` prova o arquivamento agora (pgbackrest check troca o segmento e espera ele chegar ao repositório) e
falha se o último WAL confirmado for mais velho que o RPO, se o arquivamento estiver falhando, se o repositório
não tiver backup válido ou se o último backup estiver velho demais.

Instalado pelo Ansible como /usr/local/sbin/duora-backup (root, 0755). Só biblioteca padrão do Python 3.12.
Manual: docs/operations/backup-restore.md.
"""

from __future__ import annotations

import argparse
import fcntl
import json
import subprocess
import sys
import time
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator, Protocol

DESCRIPTION = "Wrapper restrito do pgBackRest do Duora (spec 18.2)."
STANZA = "duora"
COMPOSE_PROJECT = "duora"
# Repository host: único container com o pgBackRest do repositório (S3, cifra) e o socket do banco só leitura.
BACKUP_SERVICE = "backup"
# RPO proposto de 5 min (spec 18.1): WAL confirmado no repositório há mais tempo que isso é alerta.
MAX_ARCHIVE_AGE_SECONDS = 300
# Diferencial diário: um dia perdido ainda passa; dois seguidos alertam (o restore ficaria mais longo, RTO).
MAX_BACKUP_AGE_HOURS = 48
# Backup e stanza esperam o deploy em curso terminar (mesmo lock do duora-deploy) por até este tempo.
LOCK_WAIT_SECONDS = 30 * 60
LOCK_POLL_SECONDS = 5
BACKUP_TIMEOUT_SECONDS = 6 * 3600
QUERY_TIMEOUT_SECONDS = 5 * 60

_PGBACKREST_ARGS = {
    "stanza-create": ["stanza-create"],
    "full": ["backup", "--type=full"],
    "diff": ["backup", "--type=diff"],
}
ACTIONS = (*_PGBACKREST_ARGS, "verify", "info")
_LOCKED_ACTIONS = frozenset(_PGBACKREST_ARGS)

# Epoch do último sucesso, da última falha e do relógio do banco: idades medidas no mesmo relógio.
ARCHIVER_QUERY = ("select extract(epoch from last_archived_time), extract(epoch from last_failed_time), "
                  "extract(epoch from now()) from pg_stat_archiver")


class BackupError(Exception):
    """Falha que exige ação do operador; a mensagem diz qual."""


@dataclass(frozen=True)
class ArchiverStatus:
    last_archived_at: float | None
    last_failed_at: float | None
    now: float


class Runner(Protocol):
    def __call__(self, command: list[str], *, capture: bool = False,
                 timeout_seconds: int = QUERY_TIMEOUT_SECONDS) -> subprocess.CompletedProcess[str]: ...


# --- Saúde do backup (decisão pura) ---------------------------------------------------------------------------------

def parse_archiver_status(text: str) -> ArchiverStatus:
    """Linha de `psql -At` com ARCHIVER_QUERY: campos vazios = nunca aconteceu."""
    fields = text.strip().split("|")
    if len(fields) != 3 or not fields[2]:
        raise BackupError(f"resposta inesperada de pg_stat_archiver: {text.strip()!r}")
    try:
        archived, failed, now = (float(field) if field else None for field in fields)
    except ValueError as error:
        raise BackupError(f"resposta inesperada de pg_stat_archiver: {text.strip()!r}") from error
    assert now is not None
    return ArchiverStatus(archived, failed, now)


def backup_health_problems(info_text: str, archiver: ArchiverStatus) -> list[str]:
    """Lista tudo que impede considerar o backup saudável; vazia = saudável."""
    return _repository_problems(info_text, archiver.now) + _archiving_problems(archiver)


def _archiving_problems(archiver: ArchiverStatus) -> list[str]:
    if archiver.last_archived_at is None:
        return ["nenhum WAL arquivado (pg_stat_archiver.last_archived_time vazio)"]
    problems = []
    age = archiver.now - archiver.last_archived_at
    if age > MAX_ARCHIVE_AGE_SECONDS:
        problems.append(f"último WAL confirmado no repositório há {age:.0f} s (limite {MAX_ARCHIVE_AGE_SECONDS} s, "
                        "RPO)")
    if archiver.last_failed_at is not None and archiver.last_failed_at > archiver.last_archived_at:
        problems.append("arquivamento falhando: a última falha é posterior ao último sucesso (log do postgres)")
    return problems


def _repository_problems(info_text: str, now: float) -> list[str]:
    try:
        stanzas = json.loads(info_text)
    except json.JSONDecodeError as error:
        return [f"pgbackrest info não devolveu JSON válido: {error}"]
    names = [stanza.get("name") for stanza in stanzas] if isinstance(stanzas, list) else []
    if names != [STANZA] or not isinstance(stanzas[0], dict):
        return [f"pgbackrest info: esperado só a stanza {STANZA}, recebido {names}"]
    stanza = stanzas[0]

    problems = []
    status = stanza.get("status") or {}
    if status.get("code") != 0:
        problems.append(f"pgbackrest info: stanza {STANZA} com status {status.get('code')} "
                        f"({status.get('message')})")
    if not any(isinstance(entry, dict) and entry.get("max") for entry in stanza.get("archive") or []):
        problems.append("nenhum WAL no repositório (pgbackrest info sem archive.max)")
    backups = [entry for entry in stanza.get("backup") or [] if isinstance(entry, dict)]
    if not backups:
        problems.append("nenhum backup no repositório")
        return problems

    newest = max(backups, key=lambda entry: entry["timestamp"]["stop"])
    age_hours = (now - newest["timestamp"]["stop"]) / 3600
    if age_hours > MAX_BACKUP_AGE_HOURS:
        problems.append(f"último backup terminou há {age_hours:.1f} h (limite {MAX_BACKUP_AGE_HOURS} h)")
    if newest.get("error"):
        problems.append(f"último backup ({newest.get('label')}) registrou erro de página; rode um full e "
                        "investigue o volume")
    return problems


# --- Ações ----------------------------------------------------------------------------------------------------------

def run_action(action: str, run: Runner, *, project: str, state_dir: Path,
               lock_wait_seconds: int = LOCK_WAIT_SECONDS) -> None:
    if action in _LOCKED_ACTIONS:
        # Um deploy recria o postgres; backup no meio dele falharia. O verify não pega o lock porque o próprio
        # deploy o chama segurando-o.
        with deploy_lock(state_dir, wait_seconds=lock_wait_seconds):
            _run_pgbackrest(run, _find_backup(run, project), _PGBACKREST_ARGS[action], BACKUP_TIMEOUT_SECONDS)
        return
    container = _find_backup(run, project)
    if action == "info":
        _run_pgbackrest(run, container, ["info"], QUERY_TIMEOUT_SECONDS)
        return
    _verify(run, container)


def _verify(run: Runner, container: str) -> None:
    _run_pgbackrest(run, container, ["check"], QUERY_TIMEOUT_SECONDS)
    info = _run_pgbackrest(run, container, ["info", "--output=json"], QUERY_TIMEOUT_SECONDS, capture=True)
    # Pelo socket do postgres montado no backup (autenticação local da imagem).
    archiver = _check(run(["docker", "exec", container, "psql", "--no-psqlrc", "--quiet", "--tuples-only",
                           "--no-align", "--username", "postgres", "--dbname", "postgres",
                           "--command", ARCHIVER_QUERY], capture=True),
                      "leitura de pg_stat_archiver")
    problems = backup_health_problems(info.stdout, parse_archiver_status(archiver.stdout))
    if problems:
        raise BackupError("backup/WAL não saudável:\n  - " + "\n  - ".join(problems))
    _step("backup e WAL saudáveis")


def _find_backup(run: Runner, project: str) -> str:
    # Pelos rótulos do Compose: não depende do nome do container nem dos env files da release.
    result = _check(run(["docker", "ps", "--filter", f"label=com.docker.compose.project={project}",
                         "--filter", f"label=com.docker.compose.service={BACKUP_SERVICE}",
                         "--filter", "status=running", "--format", "{{.ID}}"], capture=True),
                    "busca do container backup")
    containers = result.stdout.split()
    if not containers:
        raise BackupError(f"nenhum container backup rodando no projeto {project}")
    if len(containers) > 1:
        raise BackupError(f"{len(containers)} containers backup rodando no projeto {project}; esperado 1")
    return containers[0]


def _run_pgbackrest(run: Runner, container: str, args: list[str], timeout_seconds: int, *,
                    capture: bool = False) -> subprocess.CompletedProcess[str]:
    command = ["docker", "exec", container, "pgbackrest", f"--stanza={STANZA}", *args]
    return _check(run(command, capture=capture, timeout_seconds=timeout_seconds), f"pgbackrest {' '.join(args)}")


def _check(result: subprocess.CompletedProcess[str], what: str) -> subprocess.CompletedProcess[str]:
    if result.returncode != 0:
        detail = "\n".join((result.stderr or "").strip().splitlines()[-5:]) or "veja a saída acima"
        raise BackupError(f"{what} falhou (código {result.returncode}): {detail}")
    return result


def _step(message: str) -> None:
    print(f"[duora-backup] {message}", flush=True)


@contextmanager
def deploy_lock(state_dir: Path, *, wait_seconds: int) -> Iterator[None]:
    """O mesmo flock do duora-deploy (/var/lib/duora/.deploy.lock): backup e deploy nunca ao mesmo tempo.

    Duplicado de propósito: os dois comandos são instalados como arquivos soltos em /usr/local/sbin, sem pacote
    comum para importar. O kernel solta o lock se o processo morrer.
    """
    state_dir.mkdir(parents=True, exist_ok=True)
    with open(state_dir / ".deploy.lock", "w", encoding="utf-8") as lock_file:
        deadline = time.monotonic() + wait_seconds
        while True:
            try:
                fcntl.flock(lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError as error:
                if time.monotonic() >= deadline:
                    raise BackupError(f"deploy ou backup em andamento há mais de {wait_seconds} s "
                                      f"(lock em {state_dir / '.deploy.lock'})") from error
                time.sleep(LOCK_POLL_SECONDS)
        try:
            yield
        finally:
            fcntl.flock(lock_file, fcntl.LOCK_UN)


def run_command(command: list[str], *, capture: bool = False,
                timeout_seconds: int = QUERY_TIMEOUT_SECONDS) -> subprocess.CompletedProcess[str]:
    # Só se captura o que o verify lê (info em JSON, pg_stat_archiver); o resto vai para o journal do timer.
    try:
        return subprocess.run(command, text=True, check=False, capture_output=capture, timeout=timeout_seconds)
    except subprocess.TimeoutExpired as error:
        raise BackupError(f"comando excedeu {timeout_seconds} s: {' '.join(command[:6])} ...") from error


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="duora-backup", description=DESCRIPTION)
    parser.add_argument("action", choices=ACTIONS)
    parser.add_argument("--compose-project", default=COMPOSE_PROJECT, help="projeto do Compose (drill local)")
    parser.add_argument("--state-dir", type=Path, default=Path("/var/lib/duora"),
                        help="onde fica o lock compartilhado com o duora-deploy")
    args = parser.parse_args(argv)

    try:
        run_action(args.action, run_command, project=args.compose_project, state_dir=args.state_dir)
    except BackupError as error:
        print(f"[duora-backup] ERRO: {error}", file=sys.stderr)
        return 1
    except OSError as error:
        print(f"[duora-backup] ERRO de sistema: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
