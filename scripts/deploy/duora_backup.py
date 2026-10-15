#!/usr/bin/env python3
"""Wrapper restrito do pgBackRest do Duora na VPS (spec 18.2 e 18.5).

    duora-backup stanza-create | full | diff | verify | verify-repo | info  [--scheduled]

Roda o pgBackRest dentro do container backup (repository host: credenciais do S3, PGDATA só leitura, SQL como
duora_backup por TCP) por `docker exec`, como root do host, chamado pelos timers systemd e pelo duora-deploy. Só as
ações acima existem;
nenhum argumento é repassado ao pgBackRest. A aplicação não recebe o socket do Docker nem as credenciais do
repositório.

`verify` confere que o filtro de saída da rede backup-egress está aplicado no host, prova o arquivamento agora
(pgbackrest check troca o segmento e espera ele chegar ao repositório) e falha se o último WAL confirmado for mais
velho que o RPO, se o arquivamento estiver falhando, se o repositório não tiver backup válido ou se o último backup
estiver velho demais.

`verify-repo` (timer diário) roda o `pgbackrest verify` nativo, que confere checksum e tamanho de cada arquivo do
repositório, e falha se faltar WAL na cadeia a partir do backup mais novo. O pgbackrest verify não falha com lacuna
de WAL (só lista as faixas contínuas no log DETAIL); quem decide é este wrapper. Lacuna acontece, por exemplo, quando
o archive-push-queue-max descarta a fila: o arquivamento volta a dar sucesso e o verify de 15 min não vê o buraco.

Instalado pelo Ansible como /usr/local/sbin/duora-backup (root, 0755). Só biblioteca padrão do Python 3.12.
Manual: docs/operations/backup-restore.md.
"""

from __future__ import annotations

import argparse
import fcntl
import hashlib
import hmac
import ipaddress
import json
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ElementTree
from contextlib import contextmanager
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Iterator, Protocol

DESCRIPTION = "Wrapper restrito do pgBackRest do Duora (spec 18.2)."
STANZA = "duora"
COMPOSE_PROJECT = "duora"
# Repository host: único container com o pgBackRest do repositório (S3, cifra) e o socket do banco só leitura.
BACKUP_SERVICE = "backup"
BACKUP_DB_HOST = "postgres"
BACKUP_DB_ROLE = "duora_backup"  # papel sem superuser (infra/images/postgres/initdb/10-duora-roles.sh)
# RPO proposto de 5 min (spec 18.1): WAL confirmado no repositório há mais tempo que isso é alerta.
MAX_ARCHIVE_AGE_SECONDS = 300
# Diferencial diário: um dia perdido ainda passa; dois seguidos alertam (o restore ficaria mais longo, RTO).
MAX_BACKUP_AGE_HOURS = 48
# Backup e stanza esperam o deploy em curso terminar (mesmo lock do duora-deploy) por até este tempo.
LOCK_WAIT_SECONDS = 30 * 60
LOCK_POLL_SECONDS = 5
# O verify do timer pula durante um deploy (que recria postgres e backup) só se o último verify bom tiver no máximo
# esta idade: quatro rodadas do timer. Deploy travado ou demorado demais volta a alertar.
MAX_VERIFY_SKIP_SECONDS = 60 * 60
BACKUP_TIMEOUT_SECONDS = 6 * 3600
QUERY_TIMEOUT_SECONDS = 5 * 60
# Política do filtro de saída (infra/ansible/roles/backup): a subnet da rede backup-egress e a chain que a filtra.
EGRESS_ENV = Path("/etc/duora/backup-egress.env")
EGRESS_CHAIN = "DUORA-BACKUP-EGRESS"
# Proteção contra exclusão (spec 18.3, ADR-0023): versionamento + Object Lock com retenção padrão que cubra a janela
# máxima de retenção (21 dias, docs/operations/backup-restore.md). Sem isso o verify falha.
MIN_OBJECT_LOCK_DAYS = 21
DAYS_PER_YEAR = 365
REPO_CONF_IN_CONTAINER = "/etc/pgbackrest/conf.d/repo.conf"
SYSTEM_CA_BUNDLE = "/etc/ssl/certs/ca-certificates.crt"
EMPTY_PAYLOAD_SHA256 = hashlib.sha256(b"").hexdigest()

_PGBACKREST_ARGS = {
    "stanza-create": ["stanza-create"],
    "full": ["backup", "--type=full"],
    "diff": ["backup", "--type=diff"],
}
ACTIONS = (*_PGBACKREST_ARGS, "verify", "verify-repo", "info")
# Esperam o deploy terminar e marcam backup em curso (o verify de 15 min continua rodando durante eles).
_LOCKED_ACTIONS = frozenset((*_PGBACKREST_ARGS, "verify-repo"))

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
    def __call__(self, command: list[str], *, capture: bool = False, timeout_seconds: int = QUERY_TIMEOUT_SECONDS,
                 stdin: str | None = None) -> subprocess.CompletedProcess[str]: ...


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
    # Fail closed: qualquer coisa fora do formato esperado (vazio, JSON inválido, campo ausente ou de outro tipo) é
    # problema, nunca "saudável por omissão".
    try:
        repository = _parse_info(info_text)
    except _InfoShapeError as error:
        return [f"pgbackrest info: {error}"]

    problems = []
    if repository.status_code != 0:
        problems.append(f"pgbackrest info: stanza {STANZA} com status {repository.status_code} "
                        f"({repository.status_message})")
    if not repository.has_archived_wal:
        problems.append("nenhum WAL no repositório (pgbackrest info sem archive.max)")
    if not repository.backups:
        problems.append("nenhum backup no repositório")
        return problems

    newest = max(repository.backups, key=lambda entry: entry.stopped_at)
    age_hours = (now - newest.stopped_at) / 3600
    if age_hours > MAX_BACKUP_AGE_HOURS:
        problems.append(f"último backup terminou há {age_hours:.1f} h (limite {MAX_BACKUP_AGE_HOURS} h)")
    if newest.has_error:
        problems.append(f"último backup ({newest.label}) registrou erro de página; rode um full e investigue o volume")
    return problems


# --- Integridade do repositório e continuidade do WAL (decisão pura) ------------------------------------------------

# Linhas do `pgbackrest verify --verbose --log-level-console=detail` (pgBackRest 2.59).
_WAL_RANGE = re.compile(r"archiveId: (?P<archive>\S+), wal start: (?P<start>[0-9A-F]{24}), "
                        r"wal stop: (?P<stop>[0-9A-F]{24})$", re.MULTILINE)
_COUNTERS = re.compile(r"missing: (\d+), checksum invalid: (\d+), size invalid: (\d+), other: (\d+)")
_STANZA_STATUS = re.compile(r"stanza: (?P<name>\S+)\n\s+status: (?P<status>\S+)")
_BACKUP_STATUS = re.compile(r"backup: (?P<label>[^,\s]+), status: (?P<status>[^,\s]+)")
# wal_segment_size padrão do initdb (16 MiB): 256 segmentos por "log" de 4 GiB. Outro tamanho cai em formato
# inesperado (fail closed) em vez de produzir lacunas falsas.
_SEGMENTS_PER_LOG = 0x100


@dataclass(frozen=True)
class _WalRange:
    timeline: int
    start: int
    stop: int
    start_name: str
    stop_name: str


def repository_verify_problems(verify_text: str, info_text: str) -> list[str]:
    """Lista o que impede considerar o repositório íntegro e a cadeia de WAL contínua; vazia = íntegro.

    Lacuna só conta a partir do início do backup mais novo: antes dele ela não tem ação (o próximo full já foi
    feito) e some quando o expire alcançá-la.
    """
    try:
        repository = _parse_info(info_text)
    except _InfoShapeError as error:
        return [f"pgbackrest info: {error}"]
    if not repository.backups:
        return ["nenhum backup no repositório"]
    newest = max(repository.backups, key=lambda entry: entry.stopped_at)
    problems = _verify_summary_problems(verify_text, newest.label)
    try:
        ranges = _wal_ranges(verify_text, newest.database_id)
        backup_start, backup_stop = _segment(newest.wal_start), _segment(newest.wal_stop)
    except _InfoShapeError as error:
        return problems + [str(error)]
    if not ranges:
        return problems + ["pgbackrest verify não listou WAL do banco do backup mais novo "
                           f"(archiveId *-{newest.database_id})"]
    if not any(r.timeline == backup_start[0] and r.start <= backup_start[1] and r.stop >= backup_stop[1]
               for r in ranges):
        problems.append(f"WAL do backup mais novo ({newest.label}, {newest.wal_start} a {newest.wal_stop}) "
                        "ausente no repositório")
    return problems + _gap_problems(ranges, backup_start)


def _verify_summary_problems(text: str, newest_label: str) -> list[str]:
    problems = []
    stanza = _STANZA_STATUS.search(text)
    if stanza is None or stanza["name"] != STANZA:
        problems.append(f"pgbackrest verify: saída sem o resumo da stanza {STANZA}")
    elif stanza["status"] != "ok":
        problems.append(f"pgbackrest verify: stanza {STANZA} com status {stanza['status']}")
    counters = _COUNTERS.findall(text)
    if not counters:
        problems.append("pgbackrest verify: saída sem os contadores de arquivos")
    elif any(int(value) for line in counters for value in line):
        problems.append("pgbackrest verify encontrou arquivos inválidos ou ausentes no repositório")
    statuses = {match["label"]: match["status"] for match in _BACKUP_STATUS.finditer(text)}
    if statuses.get(newest_label) != "valid":
        problems.append(f"pgbackrest verify: backup mais novo {newest_label} não está valid "
                        f"({statuses.get(newest_label, 'ausente')})")
    return problems


def _wal_ranges(text: str, database_id: int) -> list[_WalRange]:
    ranges = []
    for match in _WAL_RANGE.finditer(text):
        if match["archive"].rsplit("-", 1)[-1] != str(database_id):
            continue
        (timeline, start), (_, stop) = _segment(match["start"]), _segment(match["stop"])
        ranges.append(_WalRange(timeline, start, stop, match["start"], match["stop"]))
    return ranges


def _segment(name: str) -> tuple[int, int]:
    """(timeline, posição contínua do segmento) a partir do nome de 24 dígitos hexadecimais."""
    if not re.fullmatch(r"[0-9A-F]{24}", name) or int(name[16:], 16) >= _SEGMENTS_PER_LOG:
        raise _InfoShapeError(f"segmento de WAL fora do formato esperado (16 MiB): {name!r}")
    return int(name[:8], 16), int(name[8:16], 16) * _SEGMENTS_PER_LOG + int(name[16:], 16)


def _gap_problems(ranges: list[_WalRange], backup_start: tuple[int, int]) -> list[str]:
    backup_timeline, backup_position = backup_start
    problems = []
    for timeline in sorted({r.timeline for r in ranges if r.timeline >= backup_timeline}):
        on_timeline = sorted((r for r in ranges if r.timeline == timeline), key=lambda r: r.start)
        for previous, current in zip(on_timeline, on_timeline[1:]):
            if current.start != previous.stop + 1 and current.start > backup_position:
                problems.append(f"lacuna de WAL na timeline {timeline}: faltam os segmentos depois de "
                                f"{previous.stop_name} até antes de {current.start_name}; sem PITR nesse trecho, "
                                "rode um full (sudo duora-backup full)")
    return problems


class _InfoShapeError(Exception):
    """Saída do `pgbackrest info --output=json` fora do formato que o verify sabe avaliar."""


@dataclass(frozen=True)
class _BackupEntry:
    label: str
    stopped_at: float
    has_error: bool
    wal_start: str
    wal_stop: str
    database_id: int


@dataclass(frozen=True)
class _RepositoryStatus:
    status_code: int
    status_message: str
    has_archived_wal: bool
    backups: list[_BackupEntry]


def _parse_info(text: str) -> _RepositoryStatus:
    """Converte o JSON do info no que o verify avalia, exigindo cada campo com o tipo certo."""
    try:
        stanzas = json.loads(text)
    except json.JSONDecodeError as error:
        raise _InfoShapeError(f"saída não é JSON válido ({error})") from error
    if not isinstance(stanzas, list) or not all(isinstance(stanza, dict) for stanza in stanzas):
        raise _InfoShapeError("esperado uma lista de stanzas (objetos JSON)")
    names = [stanza.get("name") for stanza in stanzas]
    if names != [STANZA]:
        raise _InfoShapeError(f"esperado só a stanza {STANZA}, recebido {names}")
    stanza = stanzas[0]

    status = _object_field(stanza, "status", "stanza")
    archive = _list_field(stanza, "archive", "stanza")
    backups = _list_field(stanza, "backup", "stanza")
    return _RepositoryStatus(
        status_code=_int_field(status, "code", "status"),
        status_message=_str_field(status, "message", "status"),
        has_archived_wal=any(isinstance(_entry(entry, "archive").get("max"), str) and entry["max"]
                             for entry in archive),
        backups=[_parse_backup(_entry(entry, "backup")) for entry in backups],
    )


def _parse_backup(entry: dict) -> _BackupEntry:
    timestamp = _object_field(entry, "timestamp", "backup")
    archive = _object_field(entry, "archive", "backup")
    return _BackupEntry(label=_str_field(entry, "label", "backup"),
                        stopped_at=float(_int_field(timestamp, "stop", "backup.timestamp")),
                        has_error=_bool_field(entry, "error", "backup"),
                        wal_start=_str_field(archive, "start", "backup.archive"),
                        wal_stop=_str_field(archive, "stop", "backup.archive"),
                        database_id=_int_field(_object_field(entry, "database", "backup"), "id", "backup.database"))


def _entry(value: object, where: str) -> dict:
    if not isinstance(value, dict):
        raise _InfoShapeError(f"item de {where} não é objeto: {value!r}")
    return value


def _object_field(mapping: dict, key: str, where: str) -> dict:
    value = _present(mapping, key, where)
    if not isinstance(value, dict):
        raise _unexpected_type(where, key, value)
    return value


def _list_field(mapping: dict, key: str, where: str) -> list:
    value = _present(mapping, key, where)
    if not isinstance(value, list):
        raise _unexpected_type(where, key, value)
    return value


def _int_field(mapping: dict, key: str, where: str) -> int:
    value = _present(mapping, key, where)
    # bool é subclasse de int em Python: um true no lugar de um número também é formato inesperado.
    if not isinstance(value, int) or isinstance(value, bool):
        raise _unexpected_type(where, key, value)
    return value


def _str_field(mapping: dict, key: str, where: str) -> str:
    value = _present(mapping, key, where)
    if not isinstance(value, str):
        raise _unexpected_type(where, key, value)
    return value


def _bool_field(mapping: dict, key: str, where: str) -> bool:
    value = _present(mapping, key, where)
    if not isinstance(value, bool):
        raise _unexpected_type(where, key, value)
    return value


def _present(mapping: dict, key: str, where: str) -> object:
    if key not in mapping:
        raise _InfoShapeError(f"campo {where}.{key} ausente")
    return mapping[key]


def _unexpected_type(where: str, key: str, value: object) -> _InfoShapeError:
    return _InfoShapeError(f"campo {where}.{key} com tipo inesperado: {value!r}")


# --- Ações ----------------------------------------------------------------------------------------------------------

def run_action(action: str, run: Runner, *, project: str, state_dir: Path, egress_env: Path | None,
               scheduled: bool = False, lock_wait_seconds: int = LOCK_WAIT_SECONDS,
               clock: Callable[[], float] = time.time) -> None:
    """egress_env None pula a checagem do filtro de saída: só no drill local, que não tem a chain do host.

    scheduled: chamado por timer. Só muda o verify, que pula durante um deploy (veja _should_skip_scheduled_verify);
    full e diff já esperam o lock.
    """
    if action in _LOCKED_ACTIONS:
        # Um deploy recria o postgres; backup no meio dele falharia. O verify não pega o lock porque o próprio
        # deploy o chama segurando-o.
        with deploy_lock(state_dir, wait_seconds=lock_wait_seconds), backup_running(state_dir):
            container = _find_backup(run, project)
            if action == "verify-repo":
                _verify_repository(run, container)
            else:
                _run_pgbackrest(run, container, _PGBACKREST_ARGS[action], BACKUP_TIMEOUT_SECONDS)
        return
    if action == "info":
        _run_pgbackrest(run, _find_backup(run, project), ["info"], QUERY_TIMEOUT_SECONDS)
        return
    if scheduled and _should_skip_scheduled_verify(state_dir, clock()):
        _step("verify pulado: deploy em andamento (lock do duora-deploy) e último verify bom há menos de "
              f"{MAX_VERIFY_SKIP_SECONDS} s")
        return
    if egress_env is None:
        _step("AVISO: filtro de saída NÃO conferido (--skip-egress-check, só para o drill local)")
    else:
        _check_egress_filter(run, egress_env)
    container = _find_backup(run, project)
    _check_bucket_protection(run, container, clock())
    _verify(run, container)
    _record_verify_success(state_dir, clock())


def _should_skip_scheduled_verify(state_dir: Path, now: float) -> bool:
    """Pula só com deploy em curso E um verify bom recente: o deploy recria postgres e backup e o próprio deploy
    roda o verify antes de parar serviços. Sem verify bom recente (ou com o marcador ilegível), roda e alerta."""
    if not deploy_in_progress(state_dir):
        return False
    last_success = _read_verify_success(state_dir)
    return last_success is not None and now - last_success <= MAX_VERIFY_SKIP_SECONDS


def _record_verify_success(state_dir: Path, now: float) -> None:
    state_dir.mkdir(parents=True, exist_ok=True)
    (state_dir / _VERIFY_OK_FILE).write_text(f"{now}\n", encoding="utf-8")


def _read_verify_success(state_dir: Path) -> float | None:
    try:
        return float((state_dir / _VERIFY_OK_FILE).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _check_egress_filter(run: Runner, egress_env: Path) -> None:
    """Fail closed: sem prova de que o desvio para a chain e o DROP final estão no iptables, o backup não é saudável.

    O filtro é aplicado por duora-backup-egress.service; sem ele o container backup sai para qualquer destino.
    """
    subnet = read_egress_subnet(egress_env)
    for rule in egress_filter_rules(subnet):
        result = run(["iptables", "-w", "-C", *rule], capture=True)
        if result.returncode != 0:
            raise BackupError(f"filtro de saída do backup ausente: regra '{' '.join(rule)}' não está no iptables "
                              "(sudo systemctl restart duora-backup-egress; journalctl -u duora-backup-egress)")


def egress_filter_rules(subnet: str) -> list[list[str]]:
    """Regras que provam o filtro: o Docker desvia o forward para DOCKER-USER, que desvia a subnet para a chain, que
    termina em DROP (infra/ansible/roles/backup/files/duora-backup-egress)."""
    return [
        ["FORWARD", "-j", "DOCKER-USER"],
        ["DOCKER-USER", "-s", subnet, "!", "-d", subnet, "-j", EGRESS_CHAIN],
        [EGRESS_CHAIN, "-j", "DROP"],
    ]


def read_egress_subnet(egress_env: Path) -> str:
    """DUORA_BACKUP_SUBNET do env file gerado pelo Ansible, já validada como rede IPv4."""
    try:
        lines = egress_env.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        raise BackupError(f"política do filtro de saída ilegível ({egress_env}): {error}") from error
    values = dict(line.split("=", 1) for line in lines if "=" in line and not line.lstrip().startswith("#"))
    raw = values.get("DUORA_BACKUP_SUBNET")
    if raw is None:
        raise BackupError(f"DUORA_BACKUP_SUBNET ausente em {egress_env}")
    try:
        return str(ipaddress.IPv4Network(raw.strip()))
    except ValueError as error:
        raise BackupError(f"DUORA_BACKUP_SUBNET inválida em {egress_env}: {raw!r}") from error


def _verify(run: Runner, container: str) -> None:
    _run_pgbackrest(run, container, ["check"], QUERY_TIMEOUT_SECONDS)
    info = _run_pgbackrest(run, container, ["info", "--output=json"], QUERY_TIMEOUT_SECONDS, capture=True)
    # Com o mesmo papel e a mesma senha (.pgpass) do pgBackRest, por TCP; --no-password: sem senha, falha em vez de
    # pedir no terminal. pg_stat_archiver é legível por qualquer papel.
    archiver = _check(run(["docker", "exec", container, "psql", "--no-psqlrc", "--quiet", "--tuples-only",
                           "--no-align", "--host", BACKUP_DB_HOST, "--username", BACKUP_DB_ROLE,
                           "--dbname", "postgres", "--no-password", "--command", ARCHIVER_QUERY], capture=True),
                      "leitura de pg_stat_archiver")
    problems = backup_health_problems(info.stdout, parse_archiver_status(archiver.stdout))
    if problems:
        raise BackupError("backup/WAL não saudável:\n  - " + "\n  - ".join(problems))
    _step("backup e WAL saudáveis")


# --- Proteção do bucket contra exclusão -----------------------------------------------------------------------------

@dataclass(frozen=True)
class S3Settings:
    """O que o pgBackRest usa para chegar ao bucket (pgbackrest_repo.conf), com os padrões dele."""
    endpoint: str
    bucket: str
    region: str
    key: str
    secret: str = field(repr=False)
    path_style: bool = False
    port: int = 443
    ca_file: str = SYSTEM_CA_BUNDLE

    @property
    def host(self) -> str:
        return self.endpoint if self.path_style else f"{self.bucket}.{self.endpoint}"

    @property
    def host_header(self) -> str:
        return self.host if self.port == 443 else f"{self.host}:{self.port}"

    @property
    def bucket_path(self) -> str:
        return f"/{self.bucket}" if self.path_style else "/"


def parse_repo_settings(text: str) -> S3Settings:
    """Lê repo1-* do pgbackrest_repo.conf; falta de campo obrigatório é erro (fail closed)."""
    values = {}
    for line in text.splitlines():
        line = line.strip()
        if line and not line.startswith(("#", "[")) and "=" in line:
            name, value = line.split("=", 1)
            values[name.strip()] = value.strip()
    required = ("repo1-s3-endpoint", "repo1-s3-bucket", "repo1-s3-region", "repo1-s3-key", "repo1-s3-key-secret")
    missing = [name for name in required if not values.get(name)]
    if missing:
        raise BackupError(f"pgbackrest_repo.conf sem {', '.join(missing)}")
    try:
        port = int(values.get("repo1-storage-port", "443"))
    except ValueError as error:
        raise BackupError("pgbackrest_repo.conf com repo1-storage-port inválido") from error
    return S3Settings(endpoint=values["repo1-s3-endpoint"], bucket=values["repo1-s3-bucket"],
                      region=values["repo1-s3-region"], key=values["repo1-s3-key"],
                      secret=values["repo1-s3-key-secret"], path_style=values.get("repo1-s3-uri-style") == "path",
                      port=port, ca_file=values.get("repo1-storage-ca-file", SYSTEM_CA_BUNDLE))


def signed_bucket_request(settings: S3Settings, subresource: str, now: float) -> str:
    """GET de um sub-recurso do bucket (?versioning, ?object-lock) assinado com AWS Signature Version 4."""
    amz_date = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime(now))
    scope = f"{amz_date[:8]}/{settings.region}/s3/aws4_request"
    headers = {"host": settings.host_header, "x-amz-content-sha256": EMPTY_PAYLOAD_SHA256, "x-amz-date": amz_date}
    signed_headers = ";".join(headers)
    canonical = "\n".join(["GET", settings.bucket_path, f"{subresource}=",
                           *(f"{name}:{value}" for name, value in headers.items()), "", signed_headers,
                           EMPTY_PAYLOAD_SHA256])
    string_to_sign = "\n".join(["AWS4-HMAC-SHA256", amz_date, scope,
                                hashlib.sha256(canonical.encode()).hexdigest()])
    key = f"AWS4{settings.secret}".encode()
    for part in (amz_date[:8], settings.region, "s3", "aws4_request"):
        key = hmac.new(key, part.encode(), hashlib.sha256).digest()
    signature = hmac.new(key, string_to_sign.encode(), hashlib.sha256).hexdigest()
    authorization = (f"AWS4-HMAC-SHA256 Credential={settings.key}/{scope}, SignedHeaders={signed_headers}, "
                     f"Signature={signature}")
    return (f"GET {settings.bucket_path}?{subresource}= HTTP/1.1\r\nHost: {settings.host_header}\r\n"
            f"x-amz-content-sha256: {EMPTY_PAYLOAD_SHA256}\r\nx-amz-date: {amz_date}\r\n"
            f"Authorization: {authorization}\r\nConnection: close\r\n\r\n")


def parse_http_response(raw: str) -> tuple[int, str]:
    """(status, corpo) de uma resposta HTTP/1.1 crua, com ou sem chunked encoding.

    O subprocess em modo texto já troca CRLF por LF; normaliza para os dois casos.
    """
    head, separator, body = raw.replace("\r\n", "\n").partition("\n\n")
    match = re.match(r"HTTP/1\.[01] (\d{3})", head)
    if not separator or match is None:
        raise BackupError(f"resposta HTTP ilegível do S3: {raw[:80]!r}")
    if re.search(r"^transfer-encoding:\s*chunked", head, re.IGNORECASE | re.MULTILINE):
        body = _dechunk(body)
    return int(match[1]), body


def _dechunk(body: str) -> str:
    chunks, rest = [], body
    while True:
        size_line, _, rest = rest.partition("\n")
        try:
            size = int(size_line.split(";")[0], 16)
        except ValueError as error:
            raise BackupError("resposta chunked ilegível do S3") from error
        if size == 0:
            return "".join(chunks)
        chunks.append(rest[:size])
        rest = rest[size + 1:]


def bucket_protection_problems(versioning_xml: str, object_lock_xml: str) -> list[str]:
    """Vazia só com versionamento ativo e Object Lock com retenção padrão de pelo menos MIN_OBJECT_LOCK_DAYS."""
    problems = []
    if _xml_text(versioning_xml, "Status") != "Enabled":
        problems.append("versionamento do bucket não está Enabled")
    if _xml_text(object_lock_xml, "ObjectLockEnabled") != "Enabled":
        problems.append("Object Lock do bucket não está habilitado")
        return problems
    mode = _xml_text(object_lock_xml, "Mode")
    if mode not in ("COMPLIANCE", "GOVERNANCE"):
        problems.append(f"Object Lock sem retenção padrão (Mode {mode or 'ausente'})")
        return problems
    days = _retention_days(object_lock_xml)
    if days is None or days < MIN_OBJECT_LOCK_DAYS:
        problems.append(f"retenção padrão do Object Lock de {days if days is not None else '?'} dias "
                        f"(mínimo {MIN_OBJECT_LOCK_DAYS}, a janela de retenção)")
    return problems


def _retention_days(xml_text: str) -> int | None:
    for tag, multiplier in (("Days", 1), ("Years", DAYS_PER_YEAR)):
        value = _xml_text(xml_text, tag)
        if value is not None:
            return int(value) * multiplier if value.isdigit() else None
    return None


def _xml_text(xml_text: str, local_name: str) -> str | None:
    """Texto do primeiro elemento com esse nome local (qualquer namespace); None se ausente ou XML inválido."""
    try:
        root = ElementTree.fromstring(xml_text)
    except ElementTree.ParseError:
        return None
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1] == local_name:
            return (element.text or "").strip()
    return None


def _check_bucket_protection(run: Runner, container: str, now: float) -> None:
    """Fail closed: o bucket precisa provar versionamento e Object Lock (spec 18.3, ADR-0023).

    A consulta sai do próprio container backup (mesma rede, DNS, CA e filtro de saída do pgBackRest) por openssl
    s_client, que já está na imagem; a assinatura é feita aqui, e a chave lida do segredo montado no container.
    """
    conf = _check(run(["docker", "exec", container, "cat", REPO_CONF_IN_CONTAINER], capture=True),
                  "leitura do pgbackrest_repo.conf")
    settings = parse_repo_settings(conf.stdout)
    bodies = {}
    for subresource in ("versioning", "object-lock"):
        command = ["docker", "exec", "-i", container, "openssl", "s_client", "-quiet", "-ign_eof",
                   "-connect", f"{settings.host}:{settings.port}", "-servername", settings.host,
                   "-verify_hostname", settings.host, "-verify_return_error", "-CAfile", settings.ca_file]
        result = _check(run(command, capture=True, stdin=signed_bucket_request(settings, subresource, now)),
                        f"consulta ?{subresource} do bucket")
        status, body = parse_http_response(result.stdout)
        # 404 em ?object-lock = bucket criado sem Object Lock (ObjectLockConfigurationNotFoundError).
        if status == 404 and subresource == "object-lock":
            body = ""
        elif status != 200:
            raise BackupError(f"consulta ?{subresource} do bucket respondeu HTTP {status} (a chave do backup precisa "
                              "de s3:GetBucketVersioning e s3:GetBucketObjectLockConfiguration)")
        bodies[subresource] = body
    problems = bucket_protection_problems(bodies["versioning"], bodies["object-lock"])
    if problems:
        raise BackupError(f"bucket {settings.bucket} sem proteção contra exclusão (pré-requisito de produção, "
                          "docs/operations/backup-restore.md):\n  - " + "\n  - ".join(problems))


def _verify_repository(run: Runner, container: str) -> None:
    info = _run_pgbackrest(run, container, ["info", "--output=json"], QUERY_TIMEOUT_SECONDS, capture=True)
    # Lê o repositório inteiro: no limite do tempo de um backup, não da consulta.
    verify = _run_pgbackrest(run, container, ["verify", "--verbose", "--log-level-console=detail"],
                             BACKUP_TIMEOUT_SECONDS, capture=True)
    problems = repository_verify_problems(verify.stdout, info.stdout)
    if problems:
        raise BackupError("repositório ou cadeia de WAL com problema:\n  - " + "\n  - ".join(problems))
    _step("repositório íntegro e WAL contínuo desde o backup mais novo")


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


_DEPLOY_LOCK_FILE = ".deploy.lock"
# Segurado por full, diff e stanza-create junto com o lock do deploy: separa "deploy em curso" de "backup em curso".
_BACKUP_LOCK_FILE = ".backup.lock"
_VERIFY_OK_FILE = ".backup-verify-ok"


def deploy_in_progress(state_dir: Path) -> bool:
    """Lock do duora-deploy ocupado por alguém que não é um backup deste wrapper."""
    return _is_locked(state_dir / _DEPLOY_LOCK_FILE) and not _is_locked(state_dir / _BACKUP_LOCK_FILE)


def _is_locked(path: Path) -> bool:
    if not path.exists():
        return False
    # "a": não trunca o arquivo de quem segura o lock. Só sonda: solta na hora.
    with open(path, "a", encoding="utf-8") as lock_file:
        try:
            fcntl.flock(lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return True
        fcntl.flock(lock_file, fcntl.LOCK_UN)
        return False


@contextmanager
def backup_running(state_dir: Path) -> Iterator[None]:
    """Marca um backup em curso; só é tomado por quem já segura o lock do deploy, então nunca espera."""
    state_dir.mkdir(parents=True, exist_ok=True)
    with open(state_dir / _BACKUP_LOCK_FILE, "a", encoding="utf-8") as lock_file:
        fcntl.flock(lock_file, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock_file, fcntl.LOCK_UN)


@contextmanager
def deploy_lock(state_dir: Path, *, wait_seconds: int) -> Iterator[None]:
    """O mesmo flock do duora-deploy (/var/lib/duora/.deploy.lock): backup e deploy nunca ao mesmo tempo.

    Duplicado de propósito: os dois comandos são instalados como arquivos soltos em /usr/local/sbin, sem pacote
    comum para importar. O kernel solta o lock se o processo morrer.
    """
    state_dir.mkdir(parents=True, exist_ok=True)
    with open(state_dir / _DEPLOY_LOCK_FILE, "w", encoding="utf-8") as lock_file:
        deadline = time.monotonic() + wait_seconds
        while True:
            try:
                fcntl.flock(lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError as error:
                if time.monotonic() >= deadline:
                    raise BackupError(f"deploy ou backup em andamento há mais de {wait_seconds} s "
                                      f"(lock em {state_dir / _DEPLOY_LOCK_FILE})") from error
                time.sleep(LOCK_POLL_SECONDS)
        try:
            yield
        finally:
            fcntl.flock(lock_file, fcntl.LOCK_UN)


def run_command(command: list[str], *, capture: bool = False, timeout_seconds: int = QUERY_TIMEOUT_SECONDS,
                stdin: str | None = None) -> subprocess.CompletedProcess[str]:
    # Só se captura o que o verify lê (info em JSON, pg_stat_archiver, respostas do S3); o resto vai para o journal.
    try:
        return subprocess.run(command, text=True, check=False, capture_output=capture, timeout=timeout_seconds,
                              input=stdin, stdin=None if stdin is not None else subprocess.DEVNULL)
    except subprocess.TimeoutExpired as error:
        raise BackupError(f"comando excedeu {timeout_seconds} s: {' '.join(command[:6])} ...") from error
    except OSError as error:
        raise BackupError(f"não foi possível executar {command[0]}: {error}") from error


def main(argv: list[str] | None = None, *, run: Runner = run_command) -> int:
    parser = argparse.ArgumentParser(prog="duora-backup", description=DESCRIPTION)
    parser.add_argument("action", choices=ACTIONS)
    parser.add_argument("--compose-project", default=COMPOSE_PROJECT, help="projeto do Compose (drill local)")
    parser.add_argument("--state-dir", type=Path, default=Path("/var/lib/duora"),
                        help="onde fica o lock compartilhado com o duora-deploy")
    parser.add_argument("--scheduled", action="store_true",
                        help="chamado pelo timer: o verify pula durante um deploy com verify bom recente")
    parser.add_argument("--egress-env", type=Path, default=EGRESS_ENV,
                        help="política do filtro de saída que o verify confere no iptables")
    parser.add_argument("--skip-egress-check", action="store_true",
                        help="não confere o filtro de saída: só no drill local (Docker Desktop não tem a DOCKER-USER)")
    args = parser.parse_args(argv)

    egress_env = None if args.skip_egress_check else args.egress_env
    try:
        run_action(args.action, run, project=args.compose_project, state_dir=args.state_dir, egress_env=egress_env,
                   scheduled=args.scheduled)
    except BackupError as error:
        print(f"[duora-backup] ERRO: {error}", file=sys.stderr)
        return 1
    except OSError as error:
        print(f"[duora-backup] ERRO de sistema: {error}", file=sys.stderr)
        return 1
    except Exception as error:
        # Fail closed: "não consegui verificar" nunca é sucesso. A saída 1 bloqueia o deploy e marca a unit do timer
        # como failed; o tipo e a mensagem vão para o operador.
        print(f"[duora-backup] ERRO inesperado ({type(error).__name__}): {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
