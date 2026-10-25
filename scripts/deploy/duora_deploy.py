#!/usr/bin/env python3
"""Comando restrito de deploy do Duora na VPS (spec 15.5 e 17.3).

Recebe o manifesto de uma release aprovada (arquivo ou stdin), valida formato, digests, registry, arquitetura e
gates, confere espaço em disco, baixa as imagens, roda o migrador como job exclusivo, sobe web, worker e edge e
verifica /health/ready pela rede interna. Não é zero-downtime: há uma breve interrupção planejada (spec 17.3).

Instalado pelo Ansible como /usr/local/sbin/duora-deploy (root, 0755) e liberado no sudoers só para o usuário de
deploy. Só usa a biblioteca padrão do Python 3.12 (Ubuntu 24.04). Manual: docs/operations/deploy.md.
"""

from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import os
import platform
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterator, Protocol

# The installed entry can be a symlink loaded by the receiver's SourceFileLoader.
# Bind sidecars to the verified root-owned version, never the caller's working directory.
_SIDECAR_DIRECTORY = str(Path(__file__).resolve().parent)
sys.dont_write_bytecode = True
if _SIDECAR_DIRECTORY not in sys.path:
    sys.path.insert(0, _SIDECAR_DIRECTORY)
import reconcile_payload as _payload
import runtime_guard as _guard

DESCRIPTION = "Comando restrito de deploy do Duora na VPS (spec 15.5 e 17.3)."
IMAGE_NAMES = ("web", "worker", "edge", "migrator", "postgres")
SUPPORTED_ARCHITECTURES = ("amd64", "arm64")
MANIFEST_SCHEMA_VERSION = 1
MAX_MANIFEST_BYTES = 64 * 1024
RELEASES_DIR = "releases"
STAGING_DIR = "staging"
GIB = 1024**3
READINESS_INTERVAL_SECONDS = 2
DEFAULT_READINESS_TIMEOUT_SECONDS = 90
# Teto de qualquer comando (o migrador é o mais longo); estourou, o deploy para.
COMMAND_TIMEOUT_SECONDS = 15 * 60

# Primeira migration com o bootstrap administrativo e o bloqueio do reset por e-mail antes do MFA (I5). Uma release
# anterior a ela deixaria conta privilegiada sem MFA ser tomada pelo reset; o rollback que a cruza só segue sem
# nenhuma conta assim (docs/operations/deploy.md, "Bootstrap administrativo").
ADMIN_BOOTSTRAP_MIGRATION = "20261016102312_AdminBootstrap"
# Privilegiada = qualquer papel que não seja participant, sem lista dos privilegiados: um papel novo no AccountRoles.cs
# faz a guarda recusar em vez de passar. "is distinct from" conta também o papel de nome nulo (roles.name é anulável);
# o exists conta cada conta uma vez, tenha ela quantos papéis tiver.
PRIVILEGED_ACCOUNTS_WITHOUT_MFA_SQL = (
    "select count(*) from identity.users u where not u.two_factor_enabled and exists ("
    "select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id "
    "where ur.user_id = u.id and r.name is distinct from 'participant')"
)

_RELEASE_ID = re.compile(r"[0-9A-Za-z][0-9A-Za-z._-]{0,63}")
_COMMIT = re.compile(r"[0-9a-f]{40}")
_MIGRATION_ID = re.compile(r"\d{14}_[A-Za-z0-9_]+")
_CONTRACT_VERSION = re.compile(r"v\d+")
# Só digest: repositório (com host e porta opcionais) + @sha256. Tag, mesmo junto do digest, é recusada.
_IMAGE_BY_DIGEST = re.compile(r"[a-z0-9][a-z0-9.:_/-]*[a-z0-9]@sha256:[0-9a-f]{64}")
_KERNEL_TO_DOCKER_ARCH = {"x86_64": "amd64", "amd64": "amd64", "aarch64": "arm64", "arm64": "arm64"}

_MANIFEST_FIELDS = {
    "schemaVersion", "releaseId", "commit", "createdAt", "architecture", "images", "contractVersion",
    "schema", "migrations", "sbom", "gates", "previousReleaseId",
}


class DeployError(Exception):
    """Falha que interrompe o deploy com mensagem acionável para o operador."""


@dataclass(frozen=True)
class Release:
    release_id: str
    commit: str
    architecture: str
    images: dict[str, str]
    contract_version: str
    latest_migration: str
    previous_release_id: str | None
    raw: dict


# Upload de fotos ligado (DUORA_MEDIA_REVIEW no duora.env) exige o backup das fotos verificado (adendo do ADR-0010):
# o timer do media-verify ativo e o último verify bom (marcador gravado pelo duora-backup) com no máximo 30 h.
MEDIA_VERIFY_TIMER = "duora-backup-media-verify.timer"
MEDIA_VERIFY_OK_FILE = ".media-verify-ok"
MAX_MEDIA_VERIFY_AGE_HOURS = 30
# Políticas de Media:Review que o boot do web aceita em Production (MediaReview.IsPermitted, ADR-0025 item 8). Vazia:
# TechnicalOnly é a única política implementada e é recusada em Production, então qualquer valor derrubaria o web
# depois da migração. O Worker já registra o pipeline de mídia; uma política nova entra aqui quando aceita pelo
# Web e pelo Worker em Production, preservando os demais gates de ativação do ADR-0025 e do runbook
# docs/operations/backup-restore.md (integração do upload com o Compose).
PRODUCTION_MEDIA_REVIEW_POLICIES: frozenset[str] = frozenset()


# O Compose sempre configura o storage FileSystem e o web o confere no boot, com o upload ligado ou não (adendo de
# 2026-10-19 do ADR-0025). Mesmos nomes de FileSystemObjectStore e do role app_dirs.
MEDIA_AREAS = ("quarantine", "derivatives")
MEDIA_AREA_MARKER = ".duora-media-area"


@dataclass(frozen=True)
class HostConfig:
    image_prefix: str
    min_free_gib: int
    disk_check_path: Path
    media_uploads_enabled: bool = False
    media_dir: Path | None = None


@dataclass(frozen=True)
class Paths:
    compose_file: Path
    host_env: Path
    state_dir: Path
    backup_command: Path = Path("/usr/local/sbin/duora-backup")


class Runner(Protocol):
    def __call__(self, command: list[str], *, capture: bool = False,
                 input_text: str | None = None) -> subprocess.CompletedProcess[str]: ...


def _private_json(path: Path) -> dict:
    info = path.lstat()
    if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077
            or info.st_size > 256 * 1024):
        raise DeployError("Runtime baseline must be an owned private regular file")
    return _guard.decode_json(path.read_bytes())


def runtime_inventory(release: Release, paths: Paths) -> dict:
    """Read-only candidate inventory. This does not enroll it or authenticate an external SQL payload."""
    current = _current_release_id(paths.state_dir)
    if current is None:
        raise DeployError("No qualified running release; explicit administrative bootstrap required")
    runtime_env = paths.state_dir / RELEASES_DIR / (current + ".env")
    if not runtime_env.is_file():
        raise DeployError("Missing current runtime environment; refusing implicit repair")
    try:
        return _guard.collect_runtime(_compose_command(paths, runtime_env),
            postgres_reference=release.images["postgres"],
            compose_sha256=hashlib.sha256(paths.compose_file.read_bytes()).hexdigest(),
            host_id_sha256=hashlib.sha256(Path("/etc/machine-id").read_bytes()).hexdigest(),
            capture=_payload.capture_binary)
    except (_guard.GuardError, _payload.PayloadError, OSError) as error:
        raise DeployError("Cannot qualify the unchanged database/backup runtime") from error


def runtime_recheck(release: Release, paths: Paths) -> dict:
    """Re-observe the sealed runtime without pulling/authenticating SQL or enrolling any drift."""
    try:
        if (paths.state_dir / "executor-install-pending.json").exists():
            raise DeployError("Interrupted executor activation requires explicit own-attempt recovery")
        inventory = runtime_inventory(release, paths)
        _guard.require_preserved(_private_json(paths.state_dir / _guard.BASELINE_NAME), inventory["profile"])
        return inventory
    except (_guard.GuardError, OSError) as error:
        raise DeployError("Database/backup runtime changed; refusing product effects or success") from error


def runtime_preflight(release: Release, paths: Paths, run: Runner, *, authenticate_payload: bool = True) -> str | None:
    try:
        inventory = runtime_recheck(release, paths)
        current = _current_release_id(paths.state_dir)
        compose = _compose_command(paths, paths.state_dir / RELEASES_DIR / (str(current) + ".env"))
        head = _read_migration_head(compose, run)
        _require_release_not_rewritten(release, paths.state_dir)
        rollback = _is_rollback(release, head, paths.state_dir)
        minimum_v2 = _minimum_contract_v2(paths.state_dir)
        if minimum_v2 and release.contract_version == "v1":
            if rollback:
                return None  # The recorded compatible target never executes legacy migrations or SQL.
            raise DeployError("A new legacy-v1 release cannot weaken the current v2 contract")
        legacy_hash = ""
        if release.contract_version == "v1":
            identifier = inventory["profile"]["services"]["postgres"]["id"]
            filename = "/usr/local/share/duora/reconcile-privileges.sql"
            result = _payload.capture_binary(["docker", "exec", identifier, "sha256sum", filename]).decode("ascii").split()
            if len(result) != 2 or result[1] != filename:
                raise DeployError("Cannot bind the qualified legacy SQL snapshot")
            legacy_hash = result[0]
        _guard.require_contract(release.raw, schema_head=head, legacy_sql_sha256=legacy_hash)
        if release.contract_version == "v2" and authenticate_payload and not rollback:
            # OCI ingress need not populate this GHCR RepoDigest. Resolve the authenticated digest before extraction,
            # while all runtime/schema guards have passed and before staging, stopping a worker or migrating.
            _check(run(["docker", "pull", "--quiet", release.images["migrator"]]), "pull do payload por digest")
            sql = _payload.extract_sql(release.images["migrator"], revision=release.commit, architecture=release.architecture)
            _step("SQL externo autenticado sha256=" + hashlib.sha256(sql.encode()).hexdigest())
            return sql
        return None
    except (_guard.GuardError, _payload.PayloadError, OSError, UnicodeError) as error:
        raise DeployError("Runtime/contract/payload qualification refused before product mutation") from error


def _minimum_contract_v2(state_dir: Path) -> bool:
    floor = state_dir / "v2-contract-floor.json"
    if floor.exists() or floor.is_symlink():
        value = _private_json(floor)
        if (set(value) != {"schema", "minimumContract", "releaseId", "commit", "sqlSha256"}
                or type(value["schema"]) is not int or value["schema"] != 1 or value["minimumContract"] != "v2"
                or not isinstance(value["releaseId"], str) or not _RELEASE_ID.fullmatch(value["releaseId"])
                or not isinstance(value["commit"], str) or not _COMMIT.fullmatch(value["commit"])
                or not isinstance(value["sqlSha256"], str) or not re.fullmatch(r"[a-f0-9]{64}", value["sqlSha256"])):
            raise DeployError("Unknown minimum deployment contract; refusing any downgrade")
        return True
    # Successful recorded v2 releases remain qualified even after an application-only rollback selects v1.
    # Staging/failed intents are deliberately excluded: partial v2 effects are instead protected by the floor above.
    for path in (state_dir / RELEASES_DIR).glob("*.json"):
        info = path.lstat()
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o022 or info.st_size > 256 * 1024):
            raise DeployError("Untrusted recorded contract history")
        recorded = parse_manifest(path.read_text(), image_prefix="")
        if recorded.release_id != path.stem: raise DeployError("Recorded contract identity changed")
        if recorded.contract_version == "v2": return True
    return False


def _seal_v2_floor(release: Release, state_dir: Path, sql: str | None) -> None:
    floor = state_dir / "v2-contract-floor.json"
    if floor.exists() or floor.is_symlink():
        _minimum_contract_v2(state_dir)
        return
    if sql is None: raise DeployError("External-v2 SQL must be authenticated before partial effects")
    value = {"schema": 1, "minimumContract": "v2", "releaseId": release.release_id,
             "commit": release.commit, "sqlSha256": hashlib.sha256(sql.encode()).hexdigest()}
    descriptor, name = tempfile.mkstemp(prefix=".v2-floor-", dir=state_dir)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "w") as stream:
            os.fchmod(stream.fileno(), 0o600)
            json.dump(value, stream, sort_keys=True)
            stream.flush()
            os.fsync(stream.fileno())
        if floor.exists() or floor.is_symlink():
            _minimum_contract_v2(state_dir)
            return
        temporary.replace(floor)
        directory = os.open(state_dir, os.O_RDONLY | os.O_DIRECTORY)
        try: os.fsync(directory)
        finally: os.close(directory)
    finally: temporary.unlink(missing_ok=True)


# --- Manifesto ------------------------------------------------------------------------------------------------------

def parse_manifest(text: str, *, image_prefix: str) -> Release:
    """Converte o JSON do manifesto em Release ou falha dizendo qual campo está errado (spec 17.2)."""
    try:
        data = json.loads(text)
    except json.JSONDecodeError as error:
        raise DeployError(f"manifesto não é JSON válido: {error}") from error
    if not isinstance(data, dict):
        raise DeployError("manifesto deve ser um objeto JSON")

    _require_exact_keys(data, _MANIFEST_FIELDS, "manifesto")
    if data["schemaVersion"] != MANIFEST_SCHEMA_VERSION:
        raise DeployError(f"schemaVersion {data['schemaVersion']!r} sem suporte (esperado {MANIFEST_SCHEMA_VERSION})")

    release_id = _require_match(data["releaseId"], _RELEASE_ID, "releaseId")
    commit = _require_match(data["commit"], _COMMIT, "commit")
    _require_utc_timestamp(data["createdAt"], "createdAt")
    architecture = data["architecture"]
    if architecture not in SUPPORTED_ARCHITECTURES:
        raise DeployError(f"architecture {architecture!r} sem suporte (use {', '.join(SUPPORTED_ARCHITECTURES)})")

    images = _parse_images(data["images"], image_prefix)
    _require_exact_keys(data["sbom"], set(IMAGE_NAMES), "sbom")
    for name in IMAGE_NAMES:
        _require_non_empty_string(data["sbom"][name], f"sbom.{name}")

    contract_version = _require_match(data["contractVersion"], _CONTRACT_VERSION, "contractVersion")
    latest_migration = _parse_migrations(data["migrations"], data["schema"])
    _require_passed_gates(data["gates"])

    previous = data["previousReleaseId"]
    if previous is not None:
        _require_match(previous, _RELEASE_ID, "previousReleaseId")
        if previous == release_id:
            raise DeployError("previousReleaseId não pode ser a própria release")

    return Release(release_id, commit, architecture, images, contract_version, latest_migration, previous, data)


def _parse_images(images: object, image_prefix: str) -> dict[str, str]:
    _require_exact_keys(images, set(IMAGE_NAMES), "images")
    assert isinstance(images, dict)
    parsed: dict[str, str] = {}
    for name in IMAGE_NAMES:
        reference = _require_match(images[name], _IMAGE_BY_DIGEST, f"images.{name}")
        repository = reference.split("@", 1)[0]
        if ":" in repository.rsplit("/", 1)[-1]:
            raise DeployError(f"images.{name}: use só o digest, sem tag ({reference})")
        if not reference.startswith(image_prefix):
            raise DeployError(f"images.{name}: fora do registry permitido ({image_prefix}*)")
        parsed[name] = reference
    return parsed


def _parse_migrations(migrations: object, schema: object) -> str:
    if not isinstance(migrations, list) or not migrations:
        raise DeployError("migrations deve ser uma lista não vazia, em ordem de aplicação")
    for index, migration in enumerate(migrations):
        _require_match(migration, _MIGRATION_ID, f"migrations[{index}]")
    if migrations != sorted(set(migrations)):
        raise DeployError("migrations deve estar em ordem crescente e sem repetição")

    _require_exact_keys(schema, {"min", "max"}, "schema")
    assert isinstance(schema, dict)
    if schema["min"] not in migrations:
        raise DeployError("schema.min deve ser uma das migrations da release")
    if schema["max"] != migrations[-1]:
        raise DeployError("schema.max deve ser a última migration da release")
    return migrations[-1]


def _require_passed_gates(gates: object) -> None:
    if not isinstance(gates, dict) or gates.get("status") != "passed":
        raise DeployError("gates.status deve ser 'passed': release sem gates aprovados não é implantada")
    _require_exact_keys(gates, {"status", "runUrl"}, "gates")
    _require_non_empty_string(gates["runUrl"], "gates.runUrl")


def _require_exact_keys(value: object, expected: set[str], field: str) -> None:
    if not isinstance(value, dict):
        raise DeployError(f"{field} deve ser um objeto")
    missing = expected - value.keys()
    unknown = value.keys() - expected
    if missing:
        raise DeployError(f"{field}: campo(s) ausente(s): {', '.join(sorted(missing))}")
    if unknown:
        raise DeployError(f"{field}: campo(s) desconhecido(s): {', '.join(sorted(unknown))}")


def _require_match(value: object, pattern: re.Pattern[str], field: str) -> str:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise DeployError(f"{field} inválido: {value!r}")
    return value


def _require_non_empty_string(value: object, field: str) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > 512:
        raise DeployError(f"{field} deve ser texto não vazio (até 512 caracteres)")
    return value


def _require_utc_timestamp(value: object, field: str) -> None:
    text = _require_non_empty_string(value, field)
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as error:
        raise DeployError(f"{field} não é data ISO 8601: {text!r}") from error
    offset = parsed.utcoffset()
    if offset is None or offset.total_seconds() != 0:
        raise DeployError(f"{field} deve estar em UTC (sufixo Z): {text!r}")


# --- Host -----------------------------------------------------------------------------------------------------------

def parse_host_config(text: str) -> HostConfig:
    """Lê a configuração não sensível do host (o mesmo arquivo que o Compose recebe por --env-file)."""
    values: dict[str, str] = {}
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        key, separator, value = stripped.partition("=")
        if separator:
            key, value = key.strip(), value.strip()
            if key.startswith("export "):
                raise DeployError("configuração do host exige KEY=VALUE, sem export")
            normalized = key.replace("__", ":").casefold()
            if normalized == "media:review" or normalized.endswith((":media:review", "_media:review")):
                raise DeployError("Media:Review deve vir somente de DUORA_MEDIA_REVIEW")
            if key == "DUORA_MEDIA_REVIEW":
                # Mesmo significado no parser do gate e no dotenv do Compose: sem aspas, expansão ou duplicatas.
                if key in values or (value and re.fullmatch(r"[A-Za-z][A-Za-z0-9]*", value) is None):
                    raise DeployError("DUORA_MEDIA_REVIEW deve ser único, vazio ou um nome sem aspas/expansão")
            values[key] = value

    review = values.get("DUORA_MEDIA_REVIEW", "")
    if review and review not in PRODUCTION_MEDIA_REVIEW_POLICIES:
        accepted = ", ".join(sorted(PRODUCTION_MEDIA_REVIEW_POLICIES)) or "nenhuma ainda"
        raise DeployError(f"DUORA_MEDIA_REVIEW={review} não é política aceita em Production (aceitas: {accepted}); "
                          "o web recusaria o boot depois da migração. Deixe duora_media_review vazio no inventário "
                          "e reaplique o app_dirs (ADR-0025 item 8)")

    prefix = values.get("DUORA_IMAGE_PREFIX", "")
    if not prefix:
        raise DeployError("DUORA_IMAGE_PREFIX ausente na configuração do host")
    try:
        min_free_gib = int(values.get("DUORA_MIN_FREE_GIB", "10"))
    except ValueError as error:
        raise DeployError("DUORA_MIN_FREE_GIB deve ser inteiro") from error
    disk_check_path = Path(values.get("DUORA_DISK_CHECK_PATH", "/var/lib/docker"))
    media_dir = values.get("DUORA_MEDIA_DIR", "")
    return HostConfig(prefix, min_free_gib, disk_check_path,
                      media_uploads_enabled=bool(review),
                      media_dir=Path(media_dir) if media_dir else None)


def docker_architecture(kernel_machine: str) -> str:
    try:
        return _KERNEL_TO_DOCKER_ARCH[kernel_machine]
    except KeyError as error:
        raise DeployError(f"arquitetura do host sem suporte: {kernel_machine}") from error


def require_free_space(*, free_bytes: int, minimum_gib: int) -> None:
    """Imagem nova e anterior precisam caber com folga; sem isso o deploy para antes de mexer em algo (spec 15.1)."""
    if free_bytes < minimum_gib * GIB:
        raise DeployError(
            f"espaço em disco insuficiente: {free_bytes / GIB:.1f} GiB livres, mínimo {minimum_gib} GiB")


def require_media_areas(media_dir: Path | None) -> None:
    """Sem o marcador do Ansible em cada área, o web cai no boot; aqui a recusa vem antes do pull e da migração."""
    fix = "reaplique o role app_dirs do Ansible (docs/operations/deploy.md, checklist da release)"
    if media_dir is None:
        raise DeployError(f"DUORA_MEDIA_DIR ausente na configuração do host: {fix}")
    if not media_dir.is_absolute():
        raise DeployError(f"DUORA_MEDIA_DIR deve ser absoluto (recebido {media_dir}): {fix}")
    for area in MEDIA_AREAS:
        marker = media_dir / area / MEDIA_AREA_MARKER
        try:
            is_marker = stat.S_ISREG(marker.stat().st_mode)
        except PermissionError as error:
            # Áreas 0700 do UID 1654: sem root não dá para saber se o marcador existe, e "ausente" seria mentira.
            raise DeployError(f"sem permissão para conferir o marcador {marker}: rode como root (sudo)") from error
        except (FileNotFoundError, NotADirectoryError):
            is_marker = False
        if not is_marker:
            raise DeployError(f"área de fotos sem o marcador {marker} (diretório não preparado ou não montado); "
                              f"o web recusaria o boot depois da migração: {fix}. Nada foi parado.")


# --- Deploy ---------------------------------------------------------------------------------------------------------

def deploy(release: Release, paths: Paths, run: Runner, *,
           verify_backup: bool = True,
           readiness_timeout_seconds: int = DEFAULT_READINESS_TIMEOUT_SECONDS,
           sleep: Callable[[float], None] = time.sleep, media_uploads_enabled: bool = False,
           clock: Callable[[], float] = time.time) -> None:
    """Executa a release. Qualquer falha interrompe; a release corrente só muda depois do smoke de readiness.

    Tudo que pode recusar a release (manifesto, imagens, backup/WAL, schema do banco, elegibilidade de rollback) roda
    antes de parar qualquer serviço. O estado confiável (releases/ e current.json) só muda depois do smoke; até lá a release
    vive em staging/, que nunca é lido para decidir nada.
    """
    _require_release_not_rewritten(release, paths.state_dir)
    if media_uploads_enabled:
        _require_media_backup(run, paths.state_dir, clock())
    canonical_sql = runtime_preflight(release, paths, run)
    release_env = _stage_release_files(release, paths.state_dir)
    compose = _compose_command(paths, release_env)

    _step("baixando imagens por digest")
    _check(run(compose + ["--profile", "migrate", "pull", "--quiet", "web", "worker", "edge", "migrator"]), "pull das imagens da aplicação")
    _require_image_architectures(release, run)

    _step("PostgreSQL e backup preservados; nenhuma operação de criação ou reinício")

    # Spec 17.3, passo 1: estado de backup/WAL desconhecido ou ruim interrompe. Roda com o postgres desta release já
    # de pé, então prova o arquivamento com a configuração que vai ficar no ar.
    if verify_backup:
        _step("verificando backup e WAL (duora-backup verify)")
        _check(run([str(paths.backup_command), "verify"]), "verificação de backup/WAL")
    else:
        _step("AVISO: backup/WAL não verificado (--accept-missing-backup-check)")

    head = _read_migration_head(compose, run)
    rollback = _is_rollback(release, head, paths.state_dir)

    if rollback:
        _require_no_privileged_account_without_mfa(release, head, compose, run)
        # Rollback de aplicação: o banco já está à frente (expand/contract garante que a release anterior aceita o
        # schema). O migrador não roda: ele não desfaz migrations e nunca roda Down (spec 17.4).
        _step(f"rollback para {release.release_id}: banco em {head}, migrador não roda")
    else:
        # Image pulls/backup checks can take minutes. The initial inventory is not a lease on that runtime.
        runtime_recheck(release, paths)
        if release.contract_version == "v2": _seal_v2_floor(release, paths.state_dir, canonical_sql)
        # Spec 17.3, passo 4: o worker para de adquirir jobs antes da migração; jobs retomam pelo estado persistido.
        _step("pausando o worker")
        _check(run(compose + ["stop", "worker"]), "parada do worker")

        runtime_recheck(release, paths)
        _step("aplicando migrations (job exclusivo)")
        _check(run(compose + ["--profile", "migrate", "run", "--rm", "--no-deps", "migrator"]), "migrator")
        # Default privileges não aceitam exceção: o histórico do EF nasce com DML para o app e perde aqui (script
        # versionado na imagem do postgres, docs/data/model.md "Permissões de banco").
        reconciliation = compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1",
                                      "--single-transaction", "--username", "postgres", "--dbname", "duora", "--file"]
        if canonical_sql is None:
            _check(run(reconciliation + ["/usr/local/share/duora/reconcile-privileges.sql"]), "reconciliação de privilégios")
        else:
            _check(run(reconciliation + ["/dev/stdin"], input_text=canonical_sql), "reconciliação de privilégios")
        migrated_head = _read_migration_head(compose, run)
        if migrated_head != release.latest_migration:
            raise DeployError(f"banco na migration {migrated_head or '(nenhuma)'} depois do migrador, "
                              f"release espera {release.latest_migration}")

    # The app-only rollback must obey the same preservation guard before it changes application services.
    if rollback: runtime_recheck(release, paths)
    _step("subindo web, worker e edge da release")
    _check(run(compose + ["up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"]), "subida da aplicação")

    _step("smoke: /health/ready pela rede interna")
    _wait_ready(compose, run, readiness_timeout_seconds, sleep)
    _require_services_running(compose, run)

    runtime_recheck(release, paths)
    _record_release(release, paths.state_dir)
    _mark_current(release, paths.state_dir)
    _step(f"release {release.release_id} implantada")


def _require_media_backup(run: Runner, state_dir: Path, now: float) -> None:
    """Fail closed: com o upload de fotos ligado, sem timer do media-verify ativo e verify bom recente não há deploy."""
    if run(["systemctl", "is-active", "--quiet", MEDIA_VERIFY_TIMER]).returncode != 0:
        raise DeployError(f"upload de fotos ligado sem o backup das fotos: {MEDIA_VERIFY_TIMER} inativo "
                          "(duora_media_backup_enabled e duora-backup media-init; docs/operations/backup-restore.md)")
    marker = state_dir / MEDIA_VERIFY_OK_FILE
    try:
        verified_at = float(marker.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise DeployError(f"upload de fotos ligado sem verify bom do backup das fotos ({marker} ausente ou ilegível; "
                          "sudo duora-backup media-verify)") from error
    age_hours = (now - verified_at) / 3600
    if not 0 <= age_hours <= MAX_MEDIA_VERIFY_AGE_HOURS:
        raise DeployError(f"upload de fotos ligado com o último verify do backup das fotos há {age_hours:.1f} h "
                          f"(aceito: 0 a {MAX_MEDIA_VERIFY_AGE_HOURS} h; sudo duora-backup media-verify)")


def _is_rollback(release: Release, head: str, state_dir: Path) -> bool:
    """Decide antes de parar qualquer coisa se o banco aceita a release.

    Banco vazio ou até schema.max: deploy normal. Mesmo abaixo de schema.min o deploy segue, porque o migrador leva o
    banco até schema.max antes de o app subir (e isso é conferido depois). Banco à frente de schema.max: a release não
    declara suportar esse schema e só entra como rollback elegível (spec 17.2, expand/contract), decidido apenas pelo
    estado que este script gravou depois de deploys bem-sucedidos, nunca pelo manifesto recebido:
      - a release alvo é o previousReleaseId da release corrente registrada;
      - a release alvo já foi implantada antes, com este mesmo manifesto (releases/<id>.json idêntico);
      - teto: o banco não passa do schema.max da release corrente registrada.
    """
    current = _recorded_manifest(state_dir, _current_release_id(state_dir))
    known_previous = current is not None and current.get("previousReleaseId") == release.release_id
    known_current_recovery = (current is not None and current.get("releaseId") == release.release_id
                              and release.contract_version == "v1" and _minimum_contract_v2(state_dir))
    if head <= release.latest_migration and not (known_previous or known_current_recovery):
        return False
    if current is None or not (known_previous or known_current_recovery):
        current_id = current.get("releaseId") if current else None
        raise DeployError(
            f"banco na migration {head}, mais nova que schema.max {release.latest_migration} da release "
            f"{release.release_id}; ela não é a release anterior elegível da corrente ({current_id or 'nenhuma'}). "
            "Nada foi parado.")
    if _recorded_manifest(state_dir, release.release_id) is None:
        raise DeployError(f"release {release.release_id} nunca foi implantada aqui: não é rollback elegível. "
                          "Nada foi parado.")
    if head < release.raw["schema"]["min"]:
        raise DeployError("Rollback com histórico abaixo do schema mínimo conhecido; nada foi parado")
    ceiling = current["schema"]["max"]
    if head > ceiling:
        raise DeployError(f"banco na migration {head}, além do schema.max {ceiling} da release corrente; estado "
                          "desconhecido, rollback recusado. Nada foi parado.")
    return True


def _current_release_id(state_dir: Path) -> str | None:
    current_file = state_dir / "current.json"
    if not current_file.exists():
        return None
    release_id = json.loads(current_file.read_text(encoding="utf-8")).get("releaseId")
    return release_id if isinstance(release_id, str) and _RELEASE_ID.fullmatch(release_id) else None


def _recorded_manifest(state_dir: Path, release_id: str | None) -> dict | None:
    """Manifesto gravado em releases/ depois de um deploy bem-sucedido (o id já passou pela allowlist)."""
    if release_id is None:
        return None
    manifest_file = state_dir / RELEASES_DIR / f"{release_id}.json"
    if not manifest_file.exists():
        return None
    data = json.loads(manifest_file.read_text(encoding="utf-8"))
    return data if isinstance(data, dict) else None


def _require_release_not_rewritten(release: Release, state_dir: Path) -> None:
    # Releases são imutáveis: o mesmo releaseId com outro conteúdo mudaria o que a elegibilidade de rollback lê.
    recorded = _recorded_manifest(state_dir, release.release_id)
    if recorded is not None and _canonical(recorded) != _canonical(release.raw):
        raise DeployError(f"release {release.release_id} já registrada com outro conteúdo; gere um releaseId novo")


def _canonical(manifest: dict) -> str:
    return json.dumps(manifest, sort_keys=True, separators=(",", ":"))


def _compose_command(paths: Paths, release_env: Path) -> list[str]:
    return [
        "docker", "compose",
        "--file", str(paths.compose_file),
        "--env-file", str(paths.host_env),
        "--env-file", str(release_env),
    ]


def _stage_release_files(release: Release, state_dir: Path) -> Path:
    # Em subdiretório: um releaseId "current" ou "previous" nunca colide com o estado na raiz. staging/ é rascunho:
    # nada nele decide elegibilidade.
    releases = state_dir / STAGING_DIR
    releases.mkdir(parents=True, exist_ok=True)
    lines = [f"DUORA_{name.upper()}_IMAGE={reference}" for name, reference in release.images.items()]
    lines.append(f"DUORA_RELEASE_ID={release.release_id}")
    release_env = releases / f"{release.release_id}.env"
    release_env.write_text("\n".join(lines) + "\n", encoding="utf-8")
    (releases / f"{release.release_id}.json").write_text(
        json.dumps(release.raw, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return release_env


def _require_image_architectures(release: Release, run: Runner) -> None:
    for name, reference in release.images.items():
        result = _check(run(["docker", "image", "inspect", "--format", "{{.Architecture}}", reference],
                            capture=True),
                        f"inspeção da imagem {name}")
        actual = result.stdout.strip()
        if actual != release.architecture:
            raise DeployError(f"imagem {name} com arquitetura {actual}, manifesto pede {release.architecture}")


def _require_no_privileged_account_without_mfa(release: Release, head: str, compose: list[str], run: Runner) -> None:
    """Rollback para antes do bootstrap só sem conta privilegiada sem MFA: lá o reset por e-mail a tomaria (I5)."""
    crosses_bootstrap = release.latest_migration < ADMIN_BOOTSTRAP_MIGRATION <= head
    if not crosses_bootstrap:
        return
    count = _query(compose, run, PRIVILEGED_ACCOUNTS_WITHOUT_MFA_SQL, "contagem de contas privilegiadas sem MFA")
    if count != "0":
        raise DeployError(f"{count or '?'} conta(s) privilegiada(s) sem MFA: a release {release.release_id} é anterior "
                          "ao bootstrap administrativo e reabriria o reset por e-mail delas (I5). Rollback recusado; "
                          "corrija para frente. Nada foi parado.")


def _read_migration_head(compose: list[str], run: Runner) -> str:
    """Última migration aplicada, ou "" com o banco ainda sem histórico (primeiro deploy)."""
    exists = _query(compose, run, "select to_regclass('ops.__ef_migrations_history') is not null")
    if exists != "t":
        return ""
    return _query(compose, run, 'select coalesce(max("MigrationId"), \'\') from ops.__ef_migrations_history')


def _query(compose: list[str], run: Runner, sql: str, what: str = "leitura do histórico de migrations") -> str:
    result = _check(
        run(compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--quiet", "--tuples-only",
                       "--no-align", "--username", "postgres", "--dbname", "duora", "--command", sql],
            capture=True),
        what)
    return result.stdout.strip()


def _wait_ready(compose: list[str], run: Runner, timeout_seconds: int, sleep: Callable[[float], None]) -> None:
    # O edge (Caddy, Alpine) tem wget e está na mesma rede do web; a saúde nunca passa pela borda pública.
    probe = compose + ["exec", "--no-TTY", "edge", "wget", "--quiet", "--spider", "--timeout=5",
                       "http://duora-web:8080/health/ready"]
    attempts = max(1, timeout_seconds // READINESS_INTERVAL_SECONDS + 1)
    for attempt in range(attempts):
        if run(probe).returncode == 0:
            return
        if attempt + 1 < attempts:
            sleep(READINESS_INTERVAL_SECONDS)
    raise DeployError(f"web não ficou pronto em {timeout_seconds} s (GET /health/ready)")


def _require_services_running(compose: list[str], run: Runner) -> None:
    # "up --wait" aceita um container que caiu logo depois de subir; um restart em laço aparece aqui.
    services = ("web", "worker", "edge")
    result = _check(run(compose + ["ps", "--all", "--format", "{{.Service}}={{.State}}", *services],
                        capture=True),
                    "estado dos serviços")
    states = dict(line.split("=", 1) for line in result.stdout.split() if "=" in line)
    for service in services:
        if states.get(service) != "running":
            raise DeployError(f"serviço {service} em estado {states.get(service, 'ausente')}, esperado running")


def _record_release(release: Release, state_dir: Path) -> None:
    """Promove os arquivos da release de staging/ para releases/, só depois do smoke."""
    recorded = state_dir / RELEASES_DIR
    recorded.mkdir(parents=True, exist_ok=True)
    for suffix in (".env", ".json"):
        staged = state_dir / STAGING_DIR / f"{release.release_id}{suffix}"
        staged.replace(recorded / f"{release.release_id}{suffix}")


def _mark_current(release: Release, state_dir: Path) -> None:
    current = state_dir / "current.json"
    if current.exists():
        shutil.copyfile(current, state_dir / "previous.json")
    record = {
        "releaseId": release.release_id,
        "commit": release.commit,
        "deployedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
    }
    temporary = state_dir / "current.json.tmp"
    temporary.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    temporary.replace(current)


def _check(result: subprocess.CompletedProcess[str], what: str) -> subprocess.CompletedProcess[str]:
    if result.returncode != 0:
        # Comando não capturado (inclusive o duora-backup) já escreveu stdout e stderr no terminal do operador.
        # Capturado (inspect, psql, ps), o erro vem do Docker ou do psql; nenhum deles imprime segredo.
        detail = "\n".join((result.stderr or "").strip().splitlines()[-5:]) or "veja a saída acima"
        raise DeployError(f"{what} falhou (código {result.returncode}): {detail}")
    return result


def _step(message: str) -> None:
    print(f"[duora-deploy] {message}", flush=True)


def run_command(command: list[str], *, capture: bool = False,
                input_text: str | None = None) -> subprocess.CompletedProcess[str]:
    # Só se captura o que o deploy lê (arquitetura, migration, estado). O resto, inclusive a saída do migrador,
    # vai direto ao operador: "run --rm" apaga o container e os logs dele junto.
    # Compose dá precedência ao shell sobre --env-file. O gate lê o arquivo do host, então o shell não pode
    # substituir DUORA_* nem injetar arquivos/profiles via COMPOSE_*. Outros comandos preservam seu ambiente.
    environment = None
    if command and command[0] == "docker":
        environment = {"PATH": "/usr/bin:/bin", "LC_ALL": "C"}
    try:
        return subprocess.run(command, text=True, check=False, capture_output=capture,
                              timeout=COMMAND_TIMEOUT_SECONDS, env=environment, input=input_text)
    except subprocess.TimeoutExpired as error:
        raise DeployError(f"comando excedeu {COMMAND_TIMEOUT_SECONDS} s: {' '.join(command[:6])} ...") from error


# --- CLI ------------------------------------------------------------------------------------------------------------

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="duora-deploy", description=DESCRIPTION)
    parser.add_argument("--manifest", required=True, help="caminho do manifesto, ou - para ler do stdin")
    parser.add_argument("--compose-file", type=Path, default=Path("/opt/duora/compose.prod.yml"))
    parser.add_argument("--host-env", type=Path, default=Path("/etc/duora/duora.env"))
    parser.add_argument("--state-dir", type=Path, default=Path("/var/lib/duora"))
    parser.add_argument("--preflight-only", action="store_true", help="runtime/contrato somente leitura; payload é autenticado no deploy antes do worker")
    parser.add_argument("--inventory-only", action="store_true", help="inventário sanitizado somente leitura; não sela baseline")
    parser.add_argument("--accept-missing-backup-check", action="store_true",
                        help="segue sem verificar backup/WAL: só o primeiro deploy (antes da stanza existir), "
                             "homologação ou smoke. Fora do sudoers do executor de deploy")
    args = parser.parse_args(argv)

    try:
        with deploy_lock(args.state_dir, readonly=args.preflight_only or args.inventory_only):
            host = parse_host_config(args.host_env.read_text(encoding="utf-8"))
            release = parse_manifest(read_manifest(args.manifest), image_prefix=host.image_prefix)
            preflight(release, host)
            paths = Paths(args.compose_file, args.host_env, args.state_dir)
            if args.inventory_only:
                print(json.dumps(runtime_inventory(release, paths), sort_keys=True))
                return 0
            if args.preflight_only:
                runtime_preflight(release, paths, run_command, authenticate_payload=False)
                return 0
            deploy(release, paths, run_command,
                   verify_backup=not args.accept_missing_backup_check,
                   media_uploads_enabled=host.media_uploads_enabled)
    except DeployError as error:
        print(f"[duora-deploy] ERRO: {error}", file=sys.stderr)
        return 1
    except OSError as error:
        print(f"[duora-deploy] ERRO de sistema: {error}", file=sys.stderr)
        return 1
    return 0


@contextmanager
def deploy_lock(state_dir: Path, *, readonly: bool = False) -> Iterator[None]:
    """Um deploy por vez (spec 17.3, passo 5): dois migradores ou dois "up" concorrentes deixam estado misto.

    flock é solto pelo kernel quando o processo morre, então um deploy interrompido não trava os próximos.
    """
    if not readonly:
        state_dir.mkdir(parents=True, exist_ok=True)
    with open(state_dir / ".deploy.lock", "r" if readonly else "a", encoding="utf-8") as lock_file:
        try:
            fcntl.flock(lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise DeployError(f"outro deploy em andamento (lock em {state_dir / '.deploy.lock'})") from error
        try:
            yield
        finally:
            fcntl.flock(lock_file, fcntl.LOCK_UN)


def read_manifest(source: str) -> str:
    if source == "-":
        data = sys.stdin.buffer.read(MAX_MANIFEST_BYTES + 1)
    else:
        with open(source, "rb") as manifest_file:
            data = manifest_file.read(MAX_MANIFEST_BYTES + 1)
    if len(data) > MAX_MANIFEST_BYTES:
        raise DeployError(f"manifesto maior que {MAX_MANIFEST_BYTES} bytes")
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError as error:
        raise DeployError(f"manifesto não é UTF-8 válido (byte {error.start})") from error


def preflight(release: Release, host: HostConfig) -> None:
    # Antes das checagens: sem root, uma recusa abaixo (marcador das fotos ilegível) já vem explicada.
    if os.geteuid() != 0:
        _step("AVISO: rodando sem root; o usuário precisa de acesso ao Docker")
    host_arch = docker_architecture(platform.machine())
    if host_arch != release.architecture:
        raise DeployError(f"release para {release.architecture}, host é {host_arch}")
    require_free_space(free_bytes=shutil.disk_usage(host.disk_check_path).free, minimum_gib=host.min_free_gib)
    require_media_areas(host.media_dir)


if __name__ == "__main__":
    sys.exit(main())
