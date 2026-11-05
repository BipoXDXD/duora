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
import ctypes
import fcntl
import hashlib
import json
import os
import platform
import re
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
from contextlib import ExitStack, contextmanager
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
PR_SET_PDEATHSIG = 1  # prctl(2) no Linux

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
# Políticas de Media:Review que o boot do Web e do Worker aceita em Production (MediaReview.IsPermitted, ADR-0025
# item 8). HumanReview entrou pela decisão do dono de 2026-10-28 (ADR-0035); TechnicalOnly continua recusada. Estar na
# allowlist não liga o upload: isso exige duora_media_review no inventário e os demais gates de ativação do ADR-0025
# e do runbook docs/operations/backup-restore.md (integração do upload com o Compose), tratados na #69.
PRODUCTION_MEDIA_REVIEW_POLICIES: frozenset[str] = frozenset({"HumanReview"})
# Trava independente da allowlist: com o upload ligado em Production, uma exclusão de foto real só sobrevive a um
# restore se o ledger externo de exclusões em B2 existir (docs/api/media-review.md). Ele é da #69 e ainda não existe;
# até lá qualquer DUORA_MEDIA_REVIEW não vazio é recusado. Só a #69 vira esta constante, junto com o gate real.
PRODUCTION_DELETION_LEDGER_READY = False


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
    ingress: Path = Path("/var/lib/coolify-release")


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
    return _collect_runtime(release, runtime_env, paths)


def _collect_runtime(release: Release, environment: Path, paths: Paths) -> dict:
    try:
        return _guard.collect_runtime(_compose_command(paths, environment),
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
        require_no_open_swap(paths)
        inventory = runtime_inventory(release, paths)
        _guard.require_preserved(_private_json(paths.state_dir / _guard.BASELINE_NAME), inventory["profile"])
        return inventory
    except (_guard.GuardError, OSError) as error:
        raise DeployError("Database/backup runtime changed; refusing product effects or success") from error


def runtime_preflight(release: Release, paths: Paths, run: Runner, *, authenticate_payload: bool = True) -> str | None:
    try:
        if _recovery_current_steady(release, paths, run): return None
        require_no_current_v2_recovery_intent(paths)
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
        accepted = ", ".join(sorted(PRODUCTION_MEDIA_REVIEW_POLICIES)) or "nenhuma"
        raise DeployError(f"DUORA_MEDIA_REVIEW={review} não é política aceita em Production (aceitas: {accepted}); "
                          "o web recusaria o boot depois da migração. Deixe duora_media_review vazio no inventário "
                          "e reaplique o app_dirs (ADR-0025 item 8)")
    if review and not PRODUCTION_DELETION_LEDGER_READY:
        raise DeployError(f"DUORA_MEDIA_REVIEW={review} exige o ledger externo de exclusões em B2, que ainda não "
                          "existe (#69); deixe duora_media_review vazio no inventário")

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
    if _recovery_current_steady(release, paths, run):
        _step("current-v2 recuperado: aplicação preservada; entrada do observador somente leitura")
        return
    require_no_current_v2_recovery_intent(paths)
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
    recovery = None

    if rollback:
        _require_no_privileged_account_without_mfa(release, head, compose, run)
        # Rollback de aplicação: o banco já está à frente (expand/contract garante que a release anterior aceita o
        # schema). O migrador não roda: ele não desfaz migrations e nunca roda Down (spec 17.4).
        _step(f"rollback para {release.release_id}: banco em {head}, migrador não roda")
    else:
        # Image pulls/backup checks can take minutes. The initial inventory is not a lease on that runtime.
        runtime_recheck(release, paths)
        if release.contract_version == "v2": _seal_v2_floor(release, paths.state_dir, canonical_sql)
        recovery = arm_current_v2_deploy(release, paths, canonical_sql, run=run)
        # Spec 17.3, passo 4: o worker para de adquirir jobs antes da migração; jobs retomam pelo estado persistido.
        _step("pausando o worker")
        _check(_run_current_v2_operation(recovery, "worker-stop", compose + ["stop", "worker"], run), "parada do worker")

        runtime_recheck(release, paths)
        _step("aplicando migrations (job exclusivo)")
        _check(_run_current_v2_operation(recovery, "migration",
            compose + ["--profile", "migrate", "run", "--rm", "--no-deps", "migrator"], run), "migrator")
        # Default privileges não aceitam exceção: o histórico do EF nasce com DML para o app e perde aqui (script
        # versionado na imagem do postgres, docs/data/model.md "Permissões de banco").
        reconciliation = compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1",
                                      "--single-transaction", "--username", "postgres", "--dbname", "duora", "--file"]
        if canonical_sql is None:
            _check(_run_current_v2_operation(recovery, "privileges",
                reconciliation + ["/usr/local/share/duora/reconcile-privileges.sql"], run), "reconciliação de privilégios")
        else:
            _check(_run_current_v2_operation(recovery, "privileges", reconciliation + ["/dev/stdin"], run,
                input_text=canonical_sql), "reconciliação de privilégios")
        migrated_head = _read_migration_head(compose, run)
        if migrated_head != release.latest_migration:
            raise DeployError(f"banco na migration {migrated_head or '(nenhuma)'} depois do migrador, "
                              f"release espera {release.latest_migration}")

    # The app-only rollback must obey the same preservation guard before it changes application services.
    if rollback: runtime_recheck(release, paths)
    _step("subindo web, worker e edge da release")
    _check(_run_current_v2_operation(recovery, "application-up",
        compose + ["up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"], run), "subida da aplicação")

    _step("smoke: /health/ready pela rede interna")
    _wait_ready(compose, run, readiness_timeout_seconds, sleep)
    _require_services_running(compose, run)

    runtime_recheck(release, paths)
    _record_release(release, paths.state_dir)
    _mark_current(release, paths.state_dir)
    if recovery is not None: finish_current_v2_forward(recovery, release, paths)
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
    release_env = releases / f"{release.release_id}.env"
    release_env.write_text(_release_env_text(release), encoding="utf-8")
    (releases / f"{release.release_id}.json").write_text(
        json.dumps(release.raw, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return release_env


def _release_env_text(release: Release) -> str:
    lines = [f"DUORA_{name.upper()}_IMAGE={reference}" for name, reference in release.images.items()]
    lines.append(f"DUORA_RELEASE_ID={release.release_id}")
    return "\n".join(lines) + "\n"


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


# Current-v2 recovery is a separate, deliberately finite qualification. A migration head is not an ACL witness.
RECOVERY_CATALOG_35_SHA256 = "7f84f101a6555b18beee07566f26b22dda8901f71bbbffd3e98289ad3bdc7c6a"
RECOVERY_CATALOG_48_SHA256 = "4e4b537c7c42dfedc521502008f19c98095ebdd409cc43223304d379f1334f26"
RECOVERY_SQL_48_SHA256 = "5d666adcaefeaa7883d4e2596c6ef66ee03b67d04bca79d590e690f801573747"
RECOVERY_CATALOG_49_SHA256 = "56054aa8ca5b2ec32f9c687ddc57ab21c4251aa9fbc31c5fc86f9a2ee45de5d8"
RECOVERY_SQL_49_SHA256 = "ee88da57b072243a02a1c71f8ee2c2c5a00190dc53f81bde34ca15fc70dc6685"
RECOVERY_CURRENT_48_COMMIT = "c9341ab39a5e3133067eb579021efdd8c19b0033"
RECOVERY_CURRENT_48_POSTGRES = "ghcr.io/bipoxdxd/duora-postgres@sha256:d32f8c4408e804bf8b57583c6fdfb8935380e6326bcc7ccb7397b1f386ada06c"
RECOVERY_CURRENT_48_IMAGES = {
    "web": "ghcr.io/bipoxdxd/duora-web@sha256:2dfea2ca945197c09bdd4f6d924b2a9c44e82c317061df1d59dcd967dee2138d",
    "worker": "ghcr.io/bipoxdxd/duora-worker@sha256:73a8b6ada4f2ce322ab68fdce465c40fbab0e6ddc7c96a48eac8e07dcb1be995",
    "migrator": "ghcr.io/bipoxdxd/duora-migrator@sha256:fc9f02c2522d79d0a0fc65bd923a78e0e283f07583bca2b0964ccf50abd363d7",
    "edge": "ghcr.io/bipoxdxd/duora-edge@sha256:31076998ebfbb42c6ead5b38343e7796f5dde39998279af7f24920d2b9017616",
    "postgres": RECOVERY_CURRENT_48_POSTGRES,
}
RECOVERY_BINDINGS = {"current", "previous", "manifest", "environment", "floor", "runtime", "compose", "hostEnvironment"}
RECOVERY_CONTROL = {"baselineSha256", "incomingPinSha256", "globalIntentSha256", "projectIntentSha256"}


def _recovery_digest(value: object) -> str:
    return hashlib.sha256(json.dumps(value, separators=(",", ":"), allow_nan=False).encode()).hexdigest()


def _recovery_qualified_current48(current: Release) -> bool:
    return (current.contract_version == "v2" and current.commit == RECOVERY_CURRENT_48_COMMIT
            and current.architecture == "amd64" and current.images == RECOVERY_CURRENT_48_IMAGES
            and len(current.raw["migrations"]) == 48
            and _recovery_digest(current.raw["migrations"]) == RECOVERY_CATALOG_48_SHA256)


def _recovery_qualified_pair49(current: Release, incoming: Release, sql_sha256: str) -> bool:
    before, after = current.raw["migrations"], incoming.raw["migrations"]
    return (_recovery_qualified_current48(current) and len(after) == 49 and after[:48] == before
            and _recovery_digest(after) == RECOVERY_CATALOG_49_SHA256
            and sql_sha256 == RECOVERY_SQL_49_SHA256)


def _recovery_pair_hashes(current: Release, incoming: Release) -> tuple[str, str, str]:
    """Only the two independently scoped catalog transitions; never a generic schema ceiling."""
    before, after = current.raw["migrations"], incoming.raw["migrations"]
    if (current.contract_version != "v2" or incoming.contract_version != "v2"
            or current.release_id == incoming.release_id or incoming.previous_release_id != current.release_id
            or current.architecture != incoming.architecture or current.images["postgres"] != incoming.images["postgres"]):
        raise DeployError("Unqualified current-v2 recovery identities")
    if (len(before) == 35 and len(after) == 48 and after[:35] == before
            and _recovery_digest(before) == RECOVERY_CATALOG_35_SHA256
            and _recovery_digest(after) == RECOVERY_CATALOG_48_SHA256):
        return RECOVERY_CATALOG_35_SHA256, RECOVERY_CATALOG_48_SHA256, RECOVERY_SQL_48_SHA256
    if _recovery_qualified_pair49(current, incoming, RECOVERY_SQL_49_SHA256):
        return RECOVERY_CATALOG_48_SHA256, RECOVERY_CATALOG_49_SHA256, RECOVERY_SQL_49_SHA256
    raise DeployError("Unqualified current-v2 recovery catalog pair")


def _recovery_attempt_hashes(attempt: dict) -> tuple[str, str, str]:
    current = parse_manifest(json.dumps(attempt["current"]), image_prefix="ghcr.io/bipoxdxd/duora-")
    incoming = parse_manifest(json.dumps(attempt["incoming"]), image_prefix="ghcr.io/bipoxdxd/duora-")
    hashes = _recovery_pair_hashes(current, incoming)
    if attempt["sqlSha256"] != hashes[2]:
        raise DeployError("Recovery attempt payload differs from its closed catalog pair")
    return hashes


def _recovery_retained_catalog(current: Release) -> str:
    if (current.contract_version == "v2" and len(current.raw["migrations"]) == 35
            and _recovery_digest(current.raw["migrations"]) == RECOVERY_CATALOG_35_SHA256):
        return RECOVERY_CATALOG_48_SHA256
    if _recovery_qualified_current48(current):
        return RECOVERY_CATALOG_49_SHA256
    raise DeployError("No qualified retained catalog for this current application")


def _recovery_hashes(value: object, names: set[str]) -> None:
    _require_exact_keys(value, names, "recovery bindings")
    assert isinstance(value, dict)  # The closed-shape validator established this postcondition.
    for checksum in value.values():
        _require_match(checksum, re.compile(r"[a-f0-9]{64}"), "recovery digest")


def make_recovery_attempt(current: Release, incoming: Release, *, sql_sha256: str,
                          bindings: dict, control: dict, nonce: str | None = None,
                          expanded_origin: dict | None = None) -> dict:
    """Bind an intention before effects. This is never a successful-release or ACL-completion record."""
    _recovery_hashes(bindings, RECOVERY_BINDINGS)
    _recovery_hashes(control, RECOVERY_CONTROL)
    nonce = secrets.token_hex(16) if nonce is None else nonce
    _require_match(nonce, re.compile(r"[a-f0-9]{32}"), "recovery nonce")
    hashes = _recovery_pair_hashes(current, incoming)
    if sql_sha256 != hashes[2]:
        raise DeployError("Unqualified current-v2 recovery pair or incoming SQL")
    # Copy public manifests/bindings: a caller cannot mutate the armed intention through an alias.
    value = {"schema": 1, "nonce": nonce, "current": current.raw, "incoming": incoming.raw,
        "sqlSha256": sql_sha256, "bindings": bindings, "control": control}
    if expanded_origin is not None:
        _require_exact_keys(expanded_origin, {"catalogSha256", "compatibilitySha256", "attemptSha256", "pointer"}, "expanded origin")
        _recovery_hashes({name: expanded_origin[name] for name in ("catalogSha256", "compatibilitySha256", "attemptSha256")},
            {"catalogSha256", "compatibilitySha256", "attemptSha256"})
        pointer = expanded_origin["pointer"]
        _require_exact_keys(pointer, {"schema", "releaseId", "nonce", "attemptSha256", "applicationOutcomeSha256", "controlOutcomeSha256"}, "immutable origin pointer")
        _require_match(pointer["nonce"], re.compile(r"[a-f0-9]{32}"), "immutable origin nonce")
        _recovery_hashes({name: pointer[name] for name in ("attemptSha256", "applicationOutcomeSha256", "controlOutcomeSha256")},
            {"attemptSha256", "applicationOutcomeSha256", "controlOutcomeSha256"})
        raw_pointer = (json.dumps(pointer, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
        if (expanded_origin["catalogSha256"] != hashes[1] or type(pointer["schema"]) is not int
                or pointer["schema"] != 1 or pointer["releaseId"] != current.release_id
                or pointer["attemptSha256"] != expanded_origin["attemptSha256"]
                or hashlib.sha256(raw_pointer).hexdigest() != expanded_origin["compatibilitySha256"]):
            raise DeployError("Expanded origin does not bind the immutable exact previously recovered catalog pointer")
        value["expandedOrigin"] = expanded_origin
    return json.loads(json.dumps(value, allow_nan=False))


def qualify_recovery_attempt(attempt: dict, *, current: Release, incoming: Release,
                             bindings: dict, control: dict, applied_catalog: list[str]) -> str:
    try:
        expected_keys = {"schema", "nonce", "current", "incoming", "sqlSha256", "bindings", "control"}
        if "expandedOrigin" in attempt: expected_keys.add("expandedOrigin")
        _require_exact_keys(attempt, expected_keys, "recovery attempt")
        if type(attempt["schema"]) is not int or attempt["schema"] != 1:
            raise DeployError("Unsupported recovery attempt")
        expected = make_recovery_attempt(current, incoming, sql_sha256=attempt["sqlSha256"],
            bindings=bindings, control=control, nonce=attempt["nonce"], expanded_origin=attempt.get("expandedOrigin"))
        if attempt != expected:
            raise DeployError("Recovery source, preserved files or control binding changed")
        if type(applied_catalog) is not list:
            raise DeployError("Exact ordered migration catalog required")
        if applied_catalog == current.raw["migrations"]:
            if "expandedOrigin" in attempt: raise DeployError("An admitted expanded origin cannot contract back to catalog35")
            return "initial"
        if applied_catalog == incoming.raw["migrations"]: return "expanded"
        raise DeployError("Intermediate, reordered or unknown migration catalog is not qualified")
    except (TypeError, KeyError, ValueError) as error:
        raise DeployError("Current-v2 recovery qualification refused") from error


class RecoveryStore:
    """Private dirfd-relative append-only attempts. Unknown and partial writes are preserved, never repaired here.

    The caller binds the root-owned state ancestry and holds the receiver/host, release and executor locks.
    Constructing or loading a store never creates files, enrolls a baseline or acknowledges an attempt.
    """
    def __init__(self, directory: Path):
        self.directory = directory

    @contextmanager
    def _directory(self, *, create: bool = False):
        try:
            if create:
                try:
                    self.directory.mkdir(mode=0o700)
                except FileExistsError: pass
                # A previous mkdir may have returned but lost its parent-directory sync acknowledgement.
                parent = os.open(self.directory.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try: os.fsync(parent)
                finally: os.close(parent)
            fd = os.open(self.directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                info = os.fstat(fd)
                if (info.st_uid != os.geteuid() or info.st_gid != os.getegid()
                        or stat.S_IMODE(info.st_mode) != 0o700):
                    raise DeployError("Private owned recovery directory required")
                yield fd
            finally: os.close(fd)
        except OSError as error:
            raise DeployError("Protected recovery directory unavailable") from error

    @staticmethod
    def _read(directory: int, name: str) -> bytes:
        try:
            fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
            try:
                info = os.fstat(fd)
                if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_gid != os.getegid()
                        or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o600 or info.st_size > 256 * 1024):
                    raise DeployError("Untrusted recovery file")
                with os.fdopen(os.dup(fd), "rb") as stream: raw = stream.read(256 * 1024 + 1)
                after = os.fstat(fd)
                identity = lambda value: (value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns, value.st_ctime_ns)
                if len(raw) > 256 * 1024 or identity(info) != identity(after):
                    raise DeployError("Recovery file changed during bounded read")
                return raw
            finally: os.close(fd)
        except OSError as error:
            raise DeployError("Recovery state missing, unsafe or unreadable") from error

    @staticmethod
    def _write(directory: int, name: str, value: dict) -> bytes:
        raw = (json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
        if len(raw) > 256 * 1024: raise DeployError("Recovery state exceeds its budget")
        try:
            fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
            with os.fdopen(fd, "wb") as stream:
                os.fchmod(stream.fileno(), 0o600)
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            os.fsync(directory)
            return raw
        except OSError as error:
            # A partial own file is evidence of UNKNOWN, not permission to delete or overwrite it.
            raise DeployError("Recovery state was not durably committed; preserve all intents") from error

    @staticmethod
    def _sync_existing(directory: int, name: str, expected_raw: bytes, *, private: bool = True) -> None:
        """Matching cached bytes are not an acknowledgement of an earlier failed fsync. Re-establish both barriers."""
        descriptor = None
        try:
            if type(expected_raw) is not bytes or not 0 < len(expected_raw) <= 256 * 1024 or type(private) is not bool:
                raise DeployError("Bounded known file required for durability re-observation")
            descriptor = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
            info = os.fstat(descriptor)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_gid != os.getegid()
                    or info.st_nlink != 1 or info.st_size > 256 * 1024 or info.st_mode & 0o022
                    or (private and stat.S_IMODE(info.st_mode) != 0o600)):
                raise DeployError("Known durability target has unsafe metadata")
            with os.fdopen(os.dup(descriptor), "rb") as stream: raw = stream.read(256 * 1024 + 1)
            if raw != expected_raw: raise DeployError("Known durability target differs")
            identity = lambda value: (value.st_dev, value.st_ino, value.st_uid, value.st_gid, value.st_nlink,
                value.st_mode, value.st_size, value.st_mtime_ns, value.st_ctime_ns)
            if identity(info) != identity(os.fstat(descriptor)):
                raise DeployError("Durability target changed during its read")
            os.fsync(descriptor)
            os.fsync(directory)
            if (identity(info) != identity(os.fstat(descriptor))
                    or identity(info) != identity(os.stat(name, dir_fd=directory, follow_symlinks=False))):
                raise DeployError("Durability target changed before acknowledgement")
        except OSError as error:
            raise DeployError("Durability remains unknown; preserve the existing file and all intents") from error
        finally:
            if descriptor is not None: os.close(descriptor)

    def arm(self, attempt: dict) -> dict:
        _require_match(attempt.get("nonce"), re.compile(r"[a-f0-9]{32}"), "recovery nonce")
        with self._directory(create=True) as directory:
            if os.listdir(directory):
                raise DeployError("Existing or orphan recovery evidence requires explicit resolution")
            name = "attempt-" + attempt["nonce"] + ".json"
            raw = self._write(directory, name, attempt)
            pointer = {"name": name, "sha256": hashlib.sha256(raw).hexdigest()}
            self._write(directory, "pending.json", pointer)
            return pointer

    def load(self) -> dict:
        try:
            with self._directory() as directory:
                pointer = _guard.decode_json(self._read(directory, "pending.json"))
                _require_exact_keys(pointer, {"name", "sha256"}, "recovery pointer")
                _require_match(pointer["name"], re.compile(r"attempt-[a-f0-9]{32}\.json"), "recovery filename")
                _require_match(pointer["sha256"], re.compile(r"[a-f0-9]{64}"), "recovery checksum")
                raw = self._read(directory, pointer["name"])
                if hashlib.sha256(raw).hexdigest() != pointer["sha256"]:
                    raise DeployError("Recovery attempt checksum differs")
                attempt = _guard.decode_json(raw)
                if pointer["name"] != "attempt-" + attempt.get("nonce", "") + ".json":
                    raise DeployError("Recovery pointer nonce differs")
                return attempt
        except (ValueError, TypeError, KeyError) as error:
            raise DeployError("Unknown or damaged recovery attempt") from error

    def require_terminal_operations(self, *, outcomes: dict[str, str] | None = None, durable: bool = False,
                                    active_observer_sha256: str | None = None) -> None:
        """Snapshots cannot prove that an orphan producer will not send a later Docker/SQL request.

        Every mutating producer must have its pre-effect intent AND a durable positive return acknowledgement.
        A nonzero CLI exit can itself be a lost transport result, so it deliberately remains UNKNOWN here.
        These conservative refusals are not a claim that every crash window is automatically recoverable.
        """
        self.load()
        if type(durable) is not bool: raise DeployError("Explicit durability mode required")
        if active_observer_sha256 is not None:
            _require_match(active_observer_sha256, re.compile(r"[a-f0-9]{64}"), "readonly controlled observer command")
            if durable: raise DeployError("A mutating producer can never exempt an unknown observer acknowledgement")
        try:
            with self._directory() as directory:
                pointer_raw = self._read(directory, "pending.json")
                pointer = _guard.decode_json(pointer_raw)
                attempt_raw = self._read(directory, pointer["name"])
                if hashlib.sha256(attempt_raw).hexdigest() != pointer["sha256"]:
                    raise DeployError("Recovery attempt changed before producer qualification")
                known_files = {"pending.json": pointer_raw, pointer["name"]: attempt_raw}
                names = set(os.listdir(directory))
                if len(names) > 48: raise DeployError("Recovery journal budget exceeded")
                outcomes = {} if outcomes is None else outcomes
                if set(outcomes) - {"forward-outcome.json", "application-outcome.json", "control-outcome.json",
                    "control-ready.json", "environment-preimage.json", "global-intent.preimage.json", "project-intent.preimage.json",
                    "compatibility-preimage.json", "compatibility-pointer.json"}:
                    raise DeployError("Unknown recovery outcome kind")
                for name, checksum in outcomes.items():
                    _require_match(checksum, re.compile(r"[a-f0-9]{64}"), "recovery outcome digest")
                    raw = self._read(directory, name)
                    if hashlib.sha256(raw).hexdigest() != checksum:
                        raise DeployError("Recovery outcome differs from its exact qualified context")
                    known_files[name] = raw
                pattern = re.compile(r"operation-(worker-stop|migration|privileges|application-up|recovery-(?:sql|app|wal)-[a-f0-9]{32}|control-(?:pin|env|observer))-(intent|terminal)\.json")
                operations: dict[str, dict] = {}
                for name in names - {"pending.json", pointer["name"], *outcomes}:
                    match = pattern.fullmatch(name)
                    if match is None: raise DeployError("Unknown recovery journal member")
                    phase, kind = match.groups()
                    if phase == "control-observer" and kind == "terminal" and active_observer_sha256 is not None:
                        # The parent may be publishing this final-name file concurrently while EX is handed off.
                        # A strictly readonly child uses only its exact intent plus preceding PIN/env ACKs. It never
                        # treats these in-flight bytes as an acknowledgement or permits another producer/API call.
                        continue
                    raw = self._read(directory, name)
                    known_files[name] = raw
                    operations.setdefault(phase, {})[kind] = (raw, _guard.decode_json(raw))
                if active_observer_sha256 is not None and "control-observer" not in operations:
                    raise DeployError("Readonly controlled entry requires its exact existing observer intent")
                for phase, records in operations.items():
                    if set(records) != {"intent", "terminal"}:
                        if (phase == "control-observer" and set(records) == {"intent"} and active_observer_sha256 is not None
                                and {"control-pin", "control-env"} <= set(operations)
                                and all(set(operations[prior]) == {"intent", "terminal"} for prior in ("control-pin", "control-env"))):
                            _, candidate = records["intent"]
                            _require_exact_keys(candidate, {"schema", "attemptSha256", "name", "commandSha256"}, "active observer intent")
                            if (type(candidate["schema"]) is int and candidate["schema"] == 1
                                    and candidate["attemptSha256"] == pointer["sha256"] and candidate["name"] == phase
                                    and candidate["commandSha256"] == active_observer_sha256):
                                continue  # Only permits the controlled child's entirely readonly steady path.
                        raise DeployError("Previous producer result unknown; do not start another operation")
                    intent_raw, intent = records["intent"]
                    _, terminal = records["terminal"]
                    _require_exact_keys(intent, {"schema", "attemptSha256", "name", "commandSha256"}, "producer intent")
                    terminal_keys = {"schema", "intentSha256", "returncode", "acknowledged"}
                    if phase == "control-observer": terminal_keys.add("deploymentUuid")
                    _require_exact_keys(terminal, terminal_keys, "producer terminal")
                    _require_match(intent["commandSha256"], re.compile(r"[a-f0-9]{64}"), "producer command digest")
                    if (type(intent["schema"]) is not int or intent["schema"] != 1
                            or intent["attemptSha256"] != pointer["sha256"] or intent["name"] != phase
                            or type(terminal["schema"]) is not int or terminal["schema"] != 1
                            or terminal["intentSha256"] != hashlib.sha256(intent_raw).hexdigest()
                            or type(terminal["returncode"]) is not int or terminal["returncode"] != 0
                            or terminal["acknowledged"] is not True):
                        raise DeployError("Previous producer has no exact positive terminal acknowledgement")
                    if phase == "control-observer" and (type(terminal["deploymentUuid"]) is not str
                            or re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", terminal["deploymentUuid"]) is None):
                        raise DeployError("The acknowledged observer deployment identifier is unknown")
                if durable:
                    # Only complete, hash-linked positive acknowledgements reach this point. A failure before a
                    # later effect must not be repaired merely by rereading cached bytes on the next invocation.
                    for name, raw in known_files.items(): self._sync_existing(directory, name, raw)
                    if set(os.listdir(directory)) != names:
                        raise DeployError("Recovery journal changed during durability qualification")
        except (ValueError, TypeError, KeyError) as error:
            raise DeployError("Previous producer state is unknown or damaged") from error

    def run_operation(self, name: str, command_sha256: str,
                      operation: Callable[[], subprocess.CompletedProcess[str]], *,
                      outcomes: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
        _require_match(name, re.compile(r"worker-stop|migration|privileges|application-up|recovery-(?:sql|app|wal)-[a-f0-9]{32}|control-(?:pin|env|observer)"),
                       "producer operation")
        _require_match(command_sha256, re.compile(r"[a-f0-9]{64}"), "producer command digest")
        self.require_terminal_operations(outcomes=outcomes, durable=True)
        with self._directory() as directory:
            pointer = _guard.decode_json(self._read(directory, "pending.json"))
            intent_raw = self._write(directory, "operation-" + name + "-intent.json", {
                "schema": 1, "attemptSha256": pointer["sha256"], "name": name, "commandSha256": command_sha256})
        # Production supplies a waited subprocess, positive API callback or synchronous local inverse. An exception
        # produces NO terminal acknowledgement and must never become permission to repeat an uncertain request.
        result = operation()
        if not isinstance(result, subprocess.CompletedProcess) or type(result.returncode) is not int:
            raise DeployError("Producer did not return a known waited command result")
        terminal = {"schema": 1, "intentSha256": hashlib.sha256(intent_raw).hexdigest(),
                    "returncode": result.returncode, "acknowledged": result.returncode == 0}
        if name == "control-observer":
            identifier = result.stdout if result.returncode == 0 else None
            if result.returncode == 0 and (type(identifier) is not str
                    or re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", identifier) is None):
                raise DeployError("Observer API returned an unknown deployment identifier; preserve the intent")
            terminal["deploymentUuid"] = identifier
        with self._directory() as directory:
            self._write(directory, "operation-" + name + "-terminal.json", terminal)
        return result


def reconcile_recovery_permissions(stage: str, sql: str | None, expected_sha256: str, *,
                                   quiescent: Callable[[], None], execute: Callable[[str], None],
                                   witness: Callable[[], None]) -> None:
    """Always repair-forward at48, even if an earlier SQL commit lost its acknowledgement. No hidden retry.

    execute MUST be the authenticated incoming payload in a single ON_ERROR_STOP transaction, witness the actual
    effective table/column ACLs, and quiescent MUST cover both migration and uncertain SQL backends. No app starts here.
    """
    if stage not in {"initial", "expanded"}: raise DeployError("Unqualified recovery stage")
    if stage == "expanded" and (not isinstance(sql, str) or not sql
            or hashlib.sha256(sql.encode()).hexdigest() != expected_sha256):
        raise DeployError("Exact authenticated incoming SQL required for schema48 recovery")
    quiescent()
    if stage == "expanded":
        assert isinstance(sql, str)  # Authenticated above before the quiescence observation.
        execute(sql)
        witness()


def _recovery_require_quiescent(compose: list[str], run: Runner, postgres_id: str) -> None:
    """A dead client/SSH/flock does not prove its Docker job or PostgreSQL transaction has stopped."""
    _require_match(postgres_id, re.compile(r"[a-f0-9]{64}"), "preserved PostgreSQL id")
    result = _check(run(["docker", "ps", "--all", "--no-trunc", "--quiet", "--filter",
        "label=com.docker.compose.project=duora", "--filter", "label=com.docker.compose.service=migrator"],
        capture=True), "inventário de migradores para recuperação")
    identifiers = result.stdout.splitlines()
    if len(identifiers) > 16 or len(set(identifiers)) != len(identifiers):
        raise DeployError("Unknown migration container inventory")
    for identifier in identifiers:
        _require_match(identifier, re.compile(r"[a-f0-9]{64}"), "migration container id")
    if identifiers:
        result = _check(run(["docker", "inspect", *identifiers], capture=True), "quiescência dos migradores")
        try:
            records = _guard.decode_json(result.stdout.encode())
            # decode_json intentionally requires an object, whereas Docker inspect is an array.
        except _guard.GuardError:
            try:
                records = json.loads(result.stdout, object_pairs_hook=_recovery_unique_pairs)
            except (ValueError, TypeError) as error:
                raise DeployError("Unknown migration container metadata") from error
        try:
            if type(records) is not list or len(records) != len(identifiers):
                raise DeployError("Incomplete migration container inspection")
            seen = set()
            for record in records:
                labels, state = record["Config"]["Labels"], record["State"]
                if (record["Id"] not in identifiers or record["Id"] in seen
                        or labels.get("com.docker.compose.project") != "duora"
                        or labels.get("com.docker.compose.service") != "migrator"
                        or labels.get("com.docker.compose.oneoff") not in {"True", "true"}
                        or state["Status"] != "exited" or state["Running"] is not False
                        or state["Paused"] is not False or state["Restarting"] is not False
                        or type(state["Pid"]) is not int or state["Pid"] != 0):
                    raise DeployError("Migration process still active or unknown; nothing will be killed")
                seen.add(record["Id"])
        except (KeyError, TypeError) as error:
            raise DeployError("Incomplete migration process state") from error
    # Even an idle privileged connection can belong to an uncertain SQL client between statements. Only this
    # bounded observation's own backend is exempt. No participant rows, query texts or connection secrets are read.
    sessions = _query(compose, run, "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() "
        "AND pid<>pg_backend_pid() AND usename IN ('postgres','duora_migrator')", "quiescência SQL")
    if sessions != "0": raise DeployError("Privileged SQL session still present or unknown")
    processes = _check(run(["docker", "top", postgres_id, "-eo", "pid,comm"], capture=True),
                       "quiescência dos processos SQL").stdout.splitlines()
    if not processes or processes[0].split() != ["PID", "COMMAND"] or len(processes) > 256:
        raise DeployError("Unknown PostgreSQL process inventory")
    for line in processes[1:]:
        fields = line.split()
        if (len(fields) != 2 or re.fullmatch(r"[1-9][0-9]*", fields[0]) is None
                or re.fullmatch(r"[A-Za-z0-9_.-]+", fields[1]) is None or fields[1] == "psql"):
            raise DeployError("SQL client still active or process result unknown")


def _recovery_unique_pairs(items: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in items:
        if key in result: raise DeployError("Duplicate recovery metadata field")
        result[key] = value
    return result


def _recovery_execute_sql(compose: list[str], run: Runner, sql: str) -> None:
    _check(run(compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1",
        "--single-transaction", "--username", "postgres", "--dbname", "duora", "--file", "/dev/stdin"],
        input_text=sql), "reparação para frente das permissões")


def _recovery_acl_witness(compose: list[str], run: Runner, *, catalog: list[str] | None = None) -> None:
    """Read effective relation AND column privileges independently of any earlier phase seal."""
    catalog_hash = None if catalog is None else _recovery_digest(catalog)
    if catalog_hash not in {None, RECOVERY_CATALOG_48_SHA256, RECOVERY_CATALOG_49_SHA256}:
        raise DeployError("Unqualified effective ACL catalog")
    append_only = ["ops.audit_events", "ops.country_database_approvals", "ops.country_database_retired",
        "ops.country_database_versions", "safety.photo_review_grants", "media.review_promotion_variants",
        "safety.photo_review_appeal_assignment_commands", "safety.photo_review_appeal_assignments",
        "safety.photo_review_appeal_commands", "safety.photo_review_appeal_evidence_accesses",
        "safety.photo_review_assignments", "safety.photo_review_commands", "safety.photo_review_decision_commands",
        "safety.photo_review_decision_variants", "safety.photo_review_decisions", "safety.photo_review_evidence_accesses"]
    if catalog_hash == RECOVERY_CATALOG_49_SHA256:
        append_only.append("safety.photo_review_appeal_decision_commands")
    rows = [(name, privilege) for name in append_only for privilege in ("UPDATE", "DELETE", "TRUNCATE")]
    rows += [("ops.__ef_migrations_history", privilege) for privilege in
             ("SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER")]
    rows += [(name, privilege) for name in ("profiles.countries", "profiles.subdivisions")
             for privilege in ("INSERT", "UPDATE", "DELETE", "TRUNCATE")]
    rows += [(name, "TRUNCATE") for name in ("profiles.regional_preferences", "profiles.regional_preference_countries")]
    rows += [(name, privilege) for name in ("ops.country_database_deliveries", "ops.country_database_notifications",
             "safety.photo_review_appeals") for privilege in ("DELETE", "TRUNCATE")]
    # Only literal, reviewed identifiers above enter this statement; a receipt cannot supply SQL or table names.
    values = ",".join("('" + name + "','" + privilege + "')" for name, privilege in rows)
    sql = "/* recovery_acl_witness */ WITH protected(relation,privilege) AS (VALUES " + values + ") "
    sql += ("SELECT bool_and(CASE WHEN to_regclass(relation) IS NULL THEN false ELSE "
            "NOT has_table_privilege('duora_app',to_regclass(relation),privilege) AND "
            "CASE WHEN privilege IN ('SELECT','INSERT','UPDATE','REFERENCES') THEN "
            "NOT has_any_column_privilege('duora_app',to_regclass(relation),privilege) ELSE true END END) FROM protected")
    if _query(compose, run, sql, "testemunho ACL efetivo da recuperação") != "t":
        raise DeployError("Effective schema48 privileges are unsafe, incomplete or unknown")


def _recovery_catalog_acl_witness(compose: list[str], run: Runner, catalog: list[str]) -> None:
    digest = _recovery_digest(catalog)
    if digest == RECOVERY_CATALOG_49_SHA256:
        _recovery_acl_witness(compose, run, catalog=catalog)
    elif digest in {RECOVERY_CATALOG_35_SHA256, RECOVERY_CATALOG_48_SHA256}:
        _recovery_acl_witness(compose, run)
    else:
        raise DeployError("Unknown recovery catalog cannot select an ACL witness")


def _recovery_pin_bytes(pin: dict) -> bytes:
    # Same insertion-order-preserving representation as the existing receiver's atomic_json, never sorted keys.
    return (json.dumps(pin, indent=2, allow_nan=False) + "\n").encode()


def make_recovery_control_baseline(current: Release, pin_raw: bytes, *, bindings: dict,
                                   receiver_current_sha256: str, receiver_config_sha256: str) -> dict:
    """A public projection only. Never put full observer configuration or environment values in a receipt."""
    try:
        _recovery_hashes(bindings, RECOVERY_BINDINGS)
        _recovery_hashes({"current": receiver_current_sha256, "config": receiver_config_sha256}, {"current", "config"})
        pin = _guard.decode_json(pin_raw)
        if (current.contract_version != "v2"
                or not (_recovery_digest(current.raw["migrations"]) == RECOVERY_CATALOG_35_SHA256
                        or _recovery_qualified_current48(current))
                or pin_raw != _recovery_pin_bytes(pin)
                or pin["manifest"] != "/opt/duora/releases/" + current.release_id + ".json"
                or pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] != current.images["edge"]):
            raise DeployError("Recovery baseline is not the exact qualified running v2 control")
        _require_match(pin["manifest_sha256"], re.compile(r"[a-f0-9]{64}"), "recorded observer manifest digest")
        _require_match(pin["application_uuid"], re.compile(r"[a-z0-9][a-z0-9-]{0,63}"), "observer resource")
        return {"schema": 1, "currentReleaseId": current.release_id, "bindings": dict(bindings),
            "pinSha256": hashlib.sha256(pin_raw).hexdigest(), "manifestPath": pin["manifest"],
            "manifestSha256": pin["manifest_sha256"], "edgeImage": current.images["edge"],
            "applicationUuid": pin["application_uuid"], "receiverCurrentSha256": receiver_current_sha256,
            "receiverConfigSha256": receiver_config_sha256}
    except (KeyError, TypeError, ValueError) as error:
        raise DeployError("Cannot enroll bounded current-v2 control baseline") from error


def qualify_recovery_control(baseline: dict, incoming: Release, pin_raw: bytes, *,
                             incoming_manifest_sha256: str, receiver_current_sha256: str,
                             receiver_config_sha256: str, global_intent: dict, project_intent: dict) -> dict:
    try:
        _require_exact_keys(baseline, {"schema", "currentReleaseId", "bindings", "pinSha256", "manifestPath",
            "manifestSha256", "edgeImage", "applicationUuid", "receiverCurrentSha256", "receiverConfigSha256"},
            "recovery control baseline")
        _recovery_hashes(baseline["bindings"], RECOVERY_BINDINGS)
        if (type(baseline["schema"]) is not int or baseline["schema"] != 1
                or incoming.contract_version != "v2" or incoming.previous_release_id != baseline["currentReleaseId"]
                or baseline["receiverCurrentSha256"] != receiver_current_sha256
                or baseline["receiverConfigSha256"] != receiver_config_sha256):
            raise DeployError("Recorded receiver control or lineage differs")
        _require_match(incoming_manifest_sha256, re.compile(r"[a-f0-9]{64}"), "incoming manifest digest")
        _require_match(baseline["manifestSha256"], re.compile(r"[a-f0-9]{64}"), "prior observer manifest digest")
        _require_match(baseline["pinSha256"], re.compile(r"[a-f0-9]{64}"), "prior observer config digest")
        if baseline["manifestPath"] != "/opt/duora/releases/" + baseline["currentReleaseId"] + ".json":
            raise DeployError("Unqualified prior observer manifest path")
        pin = _guard.decode_json(pin_raw)
        if (pin_raw != _recovery_pin_bytes(pin) or pin["application_uuid"] != baseline["applicationUuid"]
                or pin["manifest"] != "/opt/duora/releases/" + incoming.release_id + ".json"
                or pin["manifest_sha256"] != incoming_manifest_sha256
                or pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] != incoming.images["edge"]):
            raise DeployError("Incoming observer fields do not bind the exact attempted release")
        restored = json.loads(pin_raw)
        restored["manifest"] = baseline["manifestPath"]
        restored["manifest_sha256"] = baseline["manifestSha256"]
        restored["observer_environment"]["DUORA_OBSERVER_IMAGE"] = baseline["edgeImage"]
        if hashlib.sha256(_recovery_pin_bytes(restored)).hexdigest() != baseline["pinSha256"]:
            raise DeployError("Control inverse would change more than the three permitted fields")
        _require_exact_keys(global_intent, {"project", "commit", "status"}, "global ingress intent")
        _require_exact_keys(project_intent, {"release", "deployment_uuid"}, "project ingress intent")
        envelope = project_intent["release"]
        if (global_intent != {"project": "duora", "commit": incoming.commit, "status": "importing"}
                or type(envelope) is not dict or envelope.get("project") != "duora"
                or envelope.get("commit") != incoming.commit or envelope.get("duora") != incoming.raw):
            raise DeployError("Pending ingress does not describe this exact incoming attempt")
        _require_match(project_intent["deployment_uuid"], re.compile(r"[a-z0-9][a-z0-9-]{0,63}"), "pending deployment")
        # The real adapter additionally hashes the exact raw intent files. These canonical semantic digests
        # make this pure qualification deterministic without exposing private payloads in its result.
        return {"baselineSha256": _guard.digest(baseline), "incomingPinSha256": hashlib.sha256(pin_raw).hexdigest(),
                "globalIntentSha256": _guard.digest(global_intent), "projectIntentSha256": _guard.digest(project_intent)}
    except (KeyError, TypeError, ValueError) as error:
        raise DeployError("Current-v2 control restoration qualification refused") from error


def recover_current_v2_application(current: Release, paths: Paths, run: Runner, *, stage: str,
                                   recorded_environment: Path, incoming: Release, sql_sha256: str,
                                   authenticate_sql: Callable[[], str], require_bindings: Callable[[], None],
                                   require_quiescent: Callable[[], None], witness_acl: Callable[[], None],
                                   finish_application: Callable[[], None], preflight_only: bool = False,
                                   sleep: Callable[[float], None] = time.sleep) -> dict:
    """Only the application phase of an explicitly admitted current-v2 recovery; never claim control completion.

    The administrative adapter must hold all three real locks, qualify the immutable attempt/current/catalog/control
    and supply re-observation callbacks bound to that exact context. This function never enrolls or repairs bindings,
    changes historical release files, selects staging, pulls an image or invokes a migration. A failure preserves the
    unknown attempt. The later control phase remains mandatory; an application-ready result alone is not SUCCESS.
    """
    if (stage not in {"initial", "expanded"} or current.contract_version != "v2"
            or incoming.contract_version != "v2" or incoming.previous_release_id != current.release_id
            or recorded_environment != paths.state_dir / RELEASES_DIR / (current.release_id + ".env")):
        raise DeployError("Explicit recorded current-v2 recovery required")
    require_bindings()
    require_quiescent()
    if preflight_only:
        return {"status": "preflight", "complete": False, "payloadAuthenticated": False}
    runtime_recheck(current, paths)
    compose = _compose_command(paths, recorded_environment)
    expected_head = current.latest_migration if stage == "initial" else incoming.latest_migration
    if _read_migration_head(compose, run) != expected_head:
        raise DeployError("Recovery migration head changed after exact catalog qualification")
    _require_no_privileged_account_without_mfa(current, expected_head, compose, run)
    # This never pulls cold images. The adapter also binds all five immutable local image identities/provenance.
    _require_image_architectures(current, run)
    _check(run([str(paths.backup_command), "verify"]), "verificação obrigatória de backup/WAL da recuperação")
    sql = authenticate_sql() if stage == "expanded" else None
    require_bindings()
    runtime_recheck(current, paths)
    reconcile_recovery_permissions(stage, sql, sql_sha256, quiescent=require_quiescent,
        execute=lambda payload: _recovery_execute_sql(compose, run, payload), witness=witness_acl)
    require_bindings()
    runtime_recheck(current, paths)
    if _read_migration_head(compose, run) != expected_head:
        raise DeployError("Recovery schema changed; no application will be started")
    _check(run(compose + ["up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"]),
           "retorno da aplicação corrente registrada")
    _wait_ready(compose, run, DEFAULT_READINESS_TIMEOUT_SECONDS, sleep)
    _require_services_running(compose, run)
    runtime_recheck(current, paths)
    require_bindings()
    finish_application()
    return {"status": "application-recovered-control-pending", "complete": False,
            "releaseId": current.release_id, "commit": current.commit}


def _recovery_root_read(path: Path) -> bytes:
    """Bound every component by a root dirfd. Never follow a symlink or disclose a private file in an error."""
    if not path.is_absolute() or ".." in path.parts:
        raise DeployError("Absolute protected recovery path required")
    directory = None
    descriptor = None
    try:
        directory = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        for part in path.parts[1:-1]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            os.close(directory)
            directory = child
            info = os.fstat(directory)
            if info.st_uid != 0 or info.st_gid != 0 or info.st_mode & 0o022:
                raise DeployError("Recovery ancestry is not protected root state")
        descriptor = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        info = os.fstat(descriptor)
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_gid != 0 or info.st_nlink != 1
                or info.st_mode & 0o022 or info.st_size > 256 * 1024):
            raise DeployError("Recovery input is not a protected bounded root file")
        with os.fdopen(os.dup(descriptor), "rb") as stream: raw = stream.read(256 * 1024 + 1)
        identity = lambda value: (value.st_dev, value.st_ino, value.st_uid, value.st_gid, value.st_nlink,
                                 value.st_mode, value.st_size, value.st_mtime_ns, value.st_ctime_ns)
        if (len(raw) > 256 * 1024 or identity(info) != identity(os.fstat(descriptor))
                or identity(info) != identity(os.stat(path.name, dir_fd=directory, follow_symlinks=False))):
            raise DeployError("Recovery input changed during protected observation")
        return raw
    except OSError as error:
        raise DeployError("Protected recovery input is missing, unsafe or unreadable") from error
    finally:
        if descriptor is not None: os.close(descriptor)
        if directory is not None: os.close(directory)


def _recovery_preservation_paths(current: Release, paths: Paths) -> dict[str, Path]:
    return {"current": paths.state_dir / "current.json", "previous": paths.state_dir / "previous.json",
        "manifest": paths.state_dir / RELEASES_DIR / (current.release_id + ".json"),
        "environment": paths.state_dir / RELEASES_DIR / (current.release_id + ".env"),
        "floor": paths.state_dir / "v2-contract-floor.json", "runtime": paths.state_dir / _guard.BASELINE_NAME,
        "compose": paths.compose_file, "hostEnvironment": paths.host_env}


def observe_current_v2_recovery(incoming: Release, paths: Paths, *,
                                read_root: Callable[[Path], bytes] | None = None,
                                inspect_runtime: Callable[[Release], dict] | None = None,
                                read_catalog: Callable[[], list[str]] | None = None) -> dict:
    """A read-only re-observation, never implicit enrollment, missing-file repair or a choice of arbitrary paths.

    Dependency seams support synthetic protocol tests; production callers omit them and use protected dirfds,
    the real sealed runtime collector and the actual ordered PostgreSQL catalog.
    """
    read = _recovery_root_read if read_root is None else read_root
    try:
        record = _guard.decode_json(read(paths.state_dir / "current.json"))
        _require_exact_keys(record, {"releaseId", "commit", "deployedAt"}, "recorded current")
        _require_match(record["releaseId"], _RELEASE_ID, "recorded current id")
        current = parse_manifest(read(paths.state_dir / RELEASES_DIR / (record["releaseId"] + ".json")).decode(),
                                 image_prefix="ghcr.io/bipoxdxd/duora-")
        if current.release_id != record["releaseId"] or current.commit != record["commit"]:
            raise DeployError("Registered current identity differs")
        baseline_raw = read(paths.state_dir / "recovery-control" / (current.release_id + ".json"))
        baseline = _guard.decode_json(baseline_raw)
        preservation = _recovery_preservation_paths(current, paths)
        bindings = {name: hashlib.sha256(read(path)).hexdigest() for name, path in preservation.items()}
        if baseline.get("bindings") != bindings:
            raise DeployError("Preserved current files drifted; no new baseline will be enrolled")
        if (baseline.get("manifestPath") != "/opt/duora/releases/" + current.release_id + ".json"
                or hashlib.sha256(read(Path(baseline["manifestPath"]))).hexdigest() != baseline.get("manifestSha256")):
            raise DeployError("Historical observer manifest changed")
        pin_raw = read(Path("/etc/duora/coolify-deploy.json"))
        pin = _guard.decode_json(pin_raw)
        if (pin.get("compose") != str(paths.compose_file)
                or pin.get("compose_sha256") != bindings["compose"]):
            raise DeployError("Recovery requires the actual hash-qualified observer-pinned Compose")
        incoming_raw = read(Path("/opt/duora/releases") / (incoming.release_id + ".json"))
        if _guard.decode_json(incoming_raw) != incoming.raw:
            raise DeployError("Incoming observer manifest differs from the authenticated attempt")
        global_raw = read(Path("/var/lib/coolify-release/pending.json"))
        project_raw = read(Path("/var/lib/coolify-release/duora/pending.json"))
        control = qualify_recovery_control(baseline, incoming, pin_raw,
            incoming_manifest_sha256=hashlib.sha256(incoming_raw).hexdigest(),
            receiver_current_sha256=hashlib.sha256(read(Path("/var/lib/coolify-release/duora/current.json"))).hexdigest(),
            receiver_config_sha256=hashlib.sha256(read(Path("/etc/coolify-release/duora.json"))).hexdigest(),
            global_intent=_guard.decode_json(global_raw), project_intent=_guard.decode_json(project_raw))
        # Bind byte-for-byte inputs, not merely equivalent JSON. The pure inverse checker above does not write.
        control.update(baselineSha256=hashlib.sha256(baseline_raw).hexdigest(),
                       globalIntentSha256=hashlib.sha256(global_raw).hexdigest(),
                       projectIntentSha256=hashlib.sha256(project_raw).hexdigest())
        inventory = runtime_recheck(current, paths) if inspect_runtime is None else inspect_runtime(current)
        postgres_id = inventory["profile"]["services"]["postgres"]["id"]
        _require_match(postgres_id, re.compile(r"[a-f0-9]{64}"), "preserved PostgreSQL id")
        if read_catalog is None:
            compose = _compose_command(paths, preservation["environment"])
            catalog = json.loads(_query(compose, run_command,
                'SELECT coalesce(json_agg("MigrationId" ORDER BY "MigrationId"),\'[]\'::json)::text '
                'FROM ops.__ef_migrations_history', "catálogo exato de migrations da recuperação"))
        else: catalog = read_catalog()
        if type(catalog) is not list or any(type(item) is not str for item in catalog):
            raise DeployError("Exact ordered migration catalog unavailable")
        return {"current": current, "bindings": bindings, "control": control, "baseline": baseline,
                "catalog": catalog, "postgresId": postgres_id}
    except (OSError, ValueError, UnicodeError, TypeError, KeyError) as error:
        raise DeployError("Recorded current/control observation refused; no state was enrolled or changed") from error


def enroll_current_v2_recovery_control(state_dir: Path, *, inspect: Callable[[], dict],
                                       reviewed_sha256: str | None = None) -> dict:
    """Explicit administrative enrollment only, under the three locks and an idle receiver gate.

    inspect is the protected current/control/runtime observation. No hash means a read-only projection for review;
    an expected hash permits only that exact immutable public baseline, never a full PIN/environment copy.
    """
    baseline = inspect()
    _require_exact_keys(baseline, {"schema", "currentReleaseId", "bindings", "pinSha256", "manifestPath",
        "manifestSha256", "edgeImage", "applicationUuid", "receiverCurrentSha256", "receiverConfigSha256"},
        "recovery enrollment projection")
    _recovery_hashes(baseline["bindings"], RECOVERY_BINDINGS)
    _require_match(baseline["currentReleaseId"], _RELEASE_ID, "recovery enrollment current")
    if type(baseline["schema"]) is not int or baseline["schema"] != 1:
        raise DeployError("Unsupported recovery enrollment")
    if baseline["manifestPath"] != "/opt/duora/releases/" + baseline["currentReleaseId"] + ".json":
        raise DeployError("Unqualified enrolled observer path")
    _recovery_hashes({key: baseline[key] for key in ("pinSha256", "manifestSha256", "receiverCurrentSha256",
        "receiverConfigSha256")}, {"pinSha256", "manifestSha256", "receiverCurrentSha256", "receiverConfigSha256"})
    _require_match(baseline["applicationUuid"], re.compile(r"[a-z0-9][a-z0-9-]{0,63}"), "enrolled observer resource")
    _require_match(baseline["edgeImage"], re.compile(r"ghcr\.io/bipoxdxd/duora-edge@sha256:[a-f0-9]{64}"), "enrolled observer image")
    raw = (json.dumps(baseline, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    checksum = hashlib.sha256(raw).hexdigest()
    if reviewed_sha256 is None:
        return {"status": "preflight", "sha256": checksum, "baseline": baseline}
    if reviewed_sha256 != checksum or inspect() != baseline:
        raise DeployError("Recovery enrollment differs from the exact reviewed projection")
    store = RecoveryStore(state_dir / "recovery-control")
    name = baseline["currentReleaseId"] + ".json"
    with store._directory(create=True) as directory:
        if name in os.listdir(directory):
            if store._read(directory, name) != raw:
                raise DeployError("An existing recovery baseline cannot be replaced or reenrolled")
            store._sync_existing(directory, name, raw)
            if inspect() != baseline:
                raise DeployError("Control changed during enrollment durability re-observation")
            return {"status": "already-enrolled", "sha256": checksum}
        store._write(directory, name, baseline)
    if inspect() != baseline:
        raise DeployError("Control changed after enrollment; preserve the baseline and refuse forward effects")
    return {"status": "enrolled", "sha256": checksum}


def inspect_current_v2_recovery_control(current: Release, paths: Paths, *,
                                        read_root: Callable[[Path], bytes] | None = None,
                                        inspect_runtime: Callable[[Release], dict] | None = None) -> dict:
    """Inspect the unchanged running control before enrollment; the caller separately holds locks and idle gates."""
    read = _recovery_root_read if read_root is None else read_root
    try:
        record = _guard.decode_json(read(paths.state_dir / "current.json"))
        if record.get("releaseId") != current.release_id or record.get("commit") != current.commit:
            raise DeployError("Current changed before recovery-control enrollment")
        preserved = _recovery_preservation_paths(current, paths)
        bindings = {name: hashlib.sha256(read(path)).hexdigest() for name, path in preserved.items()}
        if _guard.decode_json(read(preserved["manifest"])) != current.raw:
            raise DeployError("Registered current manifest differs before enrollment")
        pin_raw = read(Path("/etc/duora/coolify-deploy.json"))
        pin = _guard.decode_json(pin_raw)
        if (pin.get("manifest") != "/opt/duora/releases/" + current.release_id + ".json"
                or pin.get("compose") != str(paths.compose_file)
                or pin.get("compose_sha256") != bindings["compose"]):
            raise DeployError("The effective observer-pinned Compose/current must be reviewed before enrollment")
        manifest_raw = read(Path(pin["manifest"]))
        if (hashlib.sha256(manifest_raw).hexdigest() != pin.get("manifest_sha256")
                or _guard.decode_json(manifest_raw) != current.raw):
            raise DeployError("Current observer manifest does not match its exact recorded release")
        config_raw = read(Path("/etc/coolify-release/duora.json"))
        if _guard.decode_json(config_raw).get("uuid") != pin.get("application_uuid"):
            raise DeployError("Receiver and observer do not identify the same application")
        if inspect_runtime is None: runtime_recheck(current, paths)
        else: inspect_runtime(current)
        return make_recovery_control_baseline(current, pin_raw, bindings=bindings,
            receiver_current_sha256=hashlib.sha256(read(Path("/var/lib/coolify-release/duora/current.json"))).hexdigest(),
            receiver_config_sha256=hashlib.sha256(config_raw).hexdigest())
    except (OSError, ValueError, TypeError, KeyError, UnicodeError) as error:
        raise DeployError("Cannot inspect the unchanged current-v2 recovery control") from error


def _recovery_recorded_current(paths: Paths) -> Release:
    try:
        record = _guard.decode_json(_recovery_root_read(paths.state_dir / "current.json"))
        _require_match(record["releaseId"], _RELEASE_ID, "recorded current recovery identity")
        current = parse_manifest(_recovery_root_read(paths.state_dir / RELEASES_DIR /
            (record["releaseId"] + ".json")).decode(), image_prefix="ghcr.io/bipoxdxd/duora-")
        if current.release_id != record["releaseId"] or current.commit != record.get("commit"):
            raise DeployError("Registered current recovery identity differs")
        return current
    except (ValueError, TypeError, KeyError, UnicodeError) as error:
        raise DeployError("Qualified recorded current is unavailable") from error


def _recovery_same_catalog_current(incoming: Release, paths: Paths) -> bool:
    """Decline extra enrollment only for exact same-catalog normal v2; never admit recovery or relax its registry."""
    if (incoming.contract_version != "v2" or _recovery_digest(incoming.raw["migrations"]) not in
            {RECOVERY_CATALOG_48_SHA256, RECOVERY_CATALOG_49_SHA256}):
        return False
    try:
        record = _guard.decode_json(_recovery_root_read(paths.state_dir / "current.json"))
        _require_exact_keys(record, {"releaseId", "commit", "deployedAt"}, "same-catalog recorded current")
        _require_match(record["releaseId"], _RELEASE_ID, "same-catalog recorded id")
        _require_match(record["commit"], _COMMIT, "same-catalog recorded commit")
        _require_utc_timestamp(record["deployedAt"], "same-catalog recorded time")
        # The normal immutable-history reader uses this same complete syntax validator without a registry selector.
        # This result can only decline enrollment; all admitted recovery paths below retain their strict GHCR parser.
        current = parse_manifest(_recovery_root_read(paths.state_dir / RELEASES_DIR /
            (record["releaseId"] + ".json")).decode(), image_prefix="")
        if current.release_id != record["releaseId"] or current.commit != record["commit"]:
            raise DeployError("Recorded same-catalog identity differs")
        return (current.contract_version == "v2" and current.architecture == incoming.architecture
                and current.images["postgres"] == incoming.images["postgres"]
                and current.raw["migrations"] == incoming.raw["migrations"])
    except (OSError, ValueError, TypeError, KeyError, UnicodeError) as error:
        raise DeployError("Cannot classify protected same-catalog current; no enrollment was changed") from error


def arm_current_v2_deploy(incoming: Release, paths: Paths, sql: str | None, *,
                          observe: Callable[[], dict] | None = None, run: Runner | None = None) -> RecoveryStore | None:
    """Arm only a closed admitted pair, after authenticated SQL/pulls/WAL and before the first producer."""
    if observe is None and _recovery_same_catalog_current(incoming, paths):
        return None  # Normal same-schema v2 still executes all existing runtime/contract/payload/backup guards.
    if incoming.contract_version == "v2" and _recovery_digest(incoming.raw["migrations"]) == RECOVERY_CATALOG_49_SHA256:
        if observe is None:
            current = _recovery_recorded_current(paths)
            if not _recovery_qualified_current48(current):
                return None  # Same-schema/future releases do not implicitly enroll another current baseline.
            context = observe_current_v2_recovery(incoming, paths)
        else:
            context = observe()
        current = context["current"]
        expanded_origin = None
        if context["catalog"] != current.raw["migrations"]:
            if context["catalog"] != incoming.raw["migrations"]:
                raise DeployError("Intermediate or unknown retained catalog is not an admitted origin")
            expanded_origin = _recovery_expanded_origin(current, context, paths, run=run)
        if not isinstance(sql, str):
            raise DeployError("Exact authenticated incoming49 SQL is required")
        attempt = make_recovery_attempt(current, incoming, sql_sha256=hashlib.sha256(sql.encode()).hexdigest(),
            bindings=context["bindings"], control=context["control"], expanded_origin=expanded_origin)
        _recovery_require_cached_images(current)
        store = RecoveryStore(paths.state_dir / "current-v2-recovery")
        store.arm(attempt)
        return store
    if incoming.contract_version != "v2" or _recovery_digest(incoming.raw["migrations"]) != RECOVERY_CATALOG_48_SHA256:
        return None
    if observe is None:
        current = _recovery_recorded_current(paths)
        if current.contract_version != "v2" or _recovery_digest(current.raw["migrations"]) != RECOVERY_CATALOG_35_SHA256:
            return None  # Legacy-v1 and same-schema/future releases retain their normal guarded contract.
        context = observe_current_v2_recovery(incoming, paths)
    else: context = observe()
    current = context["current"]
    expanded_origin = None
    if context["catalog"] != current.raw["migrations"]:
        if context["catalog"] != incoming.raw["migrations"]:
            raise DeployError("Intermediate or unknown forward catalog cannot be an expanded origin")
        expanded_origin = _recovery_expanded_origin(current, context, paths, run=run)
    if not isinstance(sql, str) or hashlib.sha256(sql.encode()).hexdigest() != RECOVERY_SQL_48_SHA256:
        raise DeployError("Exact authenticated incoming SQL is required before arming an expansion")
    _recovery_require_cached_images(current)
    attempt = make_recovery_attempt(current, incoming, sql_sha256=hashlib.sha256(sql.encode()).hexdigest(),
        bindings=context["bindings"], control=context["control"], expanded_origin=expanded_origin)
    store = RecoveryStore(paths.state_dir / "current-v2-recovery")
    store.arm(attempt)
    return store


def require_no_current_v2_recovery_intent(paths: Paths) -> None:
    directory = paths.state_dir / "current-v2-recovery"
    if directory.exists() or directory.is_symlink():
        with RecoveryStore(directory)._directory() as descriptor:
            if os.listdir(descriptor):
                raise DeployError("Existing expansion/recovery evidence requires explicit completion; no forward effects")


def _run_current_v2_operation(store: RecoveryStore | None, name: str, command: list[str], run: Runner, *,
                               input_text: str | None = None) -> subprocess.CompletedProcess[str]:
    def operation():
        return run(command) if input_text is None else run(command, input_text=input_text)
    if store is None: return operation()
    checksum = _guard.digest({"argv": command,
        "inputSha256": None if input_text is None else hashlib.sha256(input_text.encode()).hexdigest()})
    return store.run_operation(name, checksum, operation)


def _recovery_require_cached_images(current: Release, *,
                                     capture: Callable[[list[str]], bytes] | None = None) -> dict[str, str]:
    """No pull/import/tag: bind all five already-registered immutable references, role and source identities.

    Image.Id can be a config digest or an OCI leaf depending on the Docker backend; never equate these types.
    PostgreSQL is the qualified frozen component, so an application revision label is intentionally not required.
    """
    capture = _payload.capture_binary if capture is None else capture
    try:
        if current.images["postgres"] != _guard.LEGACY_DIGESTS[current.architecture]:
            raise DeployError("Recovery PostgreSQL component is not the already-qualified immutable pin")
        identifiers = {}
        for role, reference in current.images.items():
            metadata = _payload._inspect("image", reference, capture)
            _require_match(metadata["Id"], re.compile(r"sha256:[a-f0-9]{64}"), "local image identity")
            if (metadata.get("Os") != "linux" or metadata.get("Architecture") != current.architecture
                    or type(metadata.get("RepoDigests")) is not list or reference not in metadata["RepoDigests"]):
                raise DeployError("Recorded immutable image is missing, foreign or on another platform")
            if role != "postgres":
                labels = metadata["Config"]["Labels"]
                if (labels.get("org.opencontainers.image.source") != "https://github.com/BipoXDXD/duora"
                        or labels.get("org.opencontainers.image.title") != "duora-" + role
                        or labels.get("org.opencontainers.image.revision") != current.commit):
                    raise DeployError("Recorded application role/source/revision is not the exact local image")
            identifiers[role] = metadata["Id"]
        return identifiers
    except (_payload.PayloadError, OSError, TypeError, KeyError, ValueError) as error:
        raise DeployError("All five exact local return images must be qualified; nothing was pulled") from error


def finish_current_v2_forward(store: RecoveryStore, incoming: Release, paths: Paths, *,
                               read_root: Callable[[Path], bytes] | None = None, readonly: bool = False) -> dict:
    """Archive only a fully recorded forward attempt. This does not claim receiver/Coolify publication success."""
    read = _recovery_root_read if read_root is None else read_root
    try:
        attempt = store.load()
        if attempt.get("incoming") != incoming.raw:
            raise DeployError("Forward completion does not describe this exact admitted attempt")
        current = parse_manifest(json.dumps(attempt["current"]), image_prefix="ghcr.io/bipoxdxd/duora-")
        # Requalify the closed protocol, including catalogs, bindings and the immutable incoming SQL hash.
        qualify_recovery_attempt(attempt, current=current, incoming=incoming, bindings=attempt["bindings"],
            control=attempt["control"], applied_catalog=incoming.raw["migrations"])
        for name, path in _recovery_preservation_paths(current, paths).items():
            if name in {"current", "previous"}: continue  # These are the two intentional normal-forward pointer changes.
            if hashlib.sha256(read(path)).hexdigest() != attempt["bindings"][name]:
                raise DeployError("Forward completion would discard evidence of preserved-state drift")
        current_raw, previous_raw = read(paths.state_dir / "current.json"), read(paths.state_dir / "previous.json")
        record = _guard.decode_json(current_raw)
        _require_exact_keys(record, {"releaseId", "commit", "deployedAt"}, "new recorded current")
        _require_utc_timestamp(record["deployedAt"], "new recorded current timestamp")
        if (record["releaseId"] != incoming.release_id or record["commit"] != incoming.commit
                or hashlib.sha256(previous_raw).hexdigest() != attempt["bindings"]["current"]):
            raise DeployError("Forward current/previous pointers are incomplete or unknown")
        manifest_raw = read(paths.state_dir / RELEASES_DIR / (incoming.release_id + ".json"))
        environment_raw = read(paths.state_dir / RELEASES_DIR / (incoming.release_id + ".env"))
        expected_manifest = (json.dumps(incoming.raw, indent=2, sort_keys=True) + "\n").encode()
        expected_environment = ("\n".join([f"DUORA_{name.upper()}_IMAGE={reference}" for name, reference in incoming.images.items()]
            + ["DUORA_RELEASE_ID=" + incoming.release_id]) + "\n").encode()
        if manifest_raw != expected_manifest or environment_raw != expected_environment:
            raise DeployError("The new successful release files have not been completely recorded")
        with store._directory() as directory:
            pointer = _guard.decode_json(store._read(directory, "pending.json"))
            outcome = {"schema": 1, "status": "forward-recorded", "attemptSha256": pointer["sha256"],
                "releaseId": incoming.release_id, "commit": incoming.commit,
                "currentSha256": hashlib.sha256(current_raw).hexdigest(),
                "previousSha256": hashlib.sha256(previous_raw).hexdigest(),
                "manifestSha256": hashlib.sha256(manifest_raw).hexdigest(),
                "environmentSha256": hashlib.sha256(environment_raw).hexdigest()}
            expected_raw = (json.dumps(outcome, sort_keys=True, separators=(",", ":")) + "\n").encode()
            existing = "forward-outcome.json" in os.listdir(directory)
            if existing and store._read(directory, "forward-outcome.json") != expected_raw:
                raise DeployError("An unknown forward outcome cannot be overwritten")
        store.require_terminal_operations(outcomes={"forward-outcome.json": hashlib.sha256(expected_raw).hexdigest()} if existing else {},
                                          durable=not readonly)
        if readonly:
            return {"status": "preflight", "complete": False, "forwardOutcomeSha256": hashlib.sha256(expected_raw).hexdigest()}
        # The classic writer has already recorded these normal forward files. Durably seal their exact bytes before
        # removing our interrupted-expansion fence; retain legitimate historical0644 modes without widening/chmod.
        successful_files = [(paths.state_dir / "current.json", current_raw), (paths.state_dir / "previous.json", previous_raw),
            (paths.state_dir / RELEASES_DIR / (incoming.release_id + ".json"), manifest_raw),
            (paths.state_dir / RELEASES_DIR / (incoming.release_id + ".env"), environment_raw)]
        for path, raw in successful_files:
            parent = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                info = os.fstat(parent)
                if info.st_uid != os.geteuid() or info.st_gid != os.getegid() or info.st_mode & 0o022:
                    raise DeployError("Recorded forward directory is unsafe")
                store._sync_existing(parent, path.name, raw, private=False)
            finally: os.close(parent)
        with store._directory() as directory:
            if existing: store._sync_existing(directory, "forward-outcome.json", expected_raw)
            else: store._write(directory, "forward-outcome.json", outcome)
            source_identity = os.fstat(directory)
            history = RecoveryStore(paths.state_dir / "current-v2-recovery-history")
            with history._directory(create=True) as archived:
                if attempt["nonce"] in os.listdir(archived):
                    raise DeployError("An existing historical attempt must never be replaced")
                parent = os.open(paths.state_dir, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try:
                    parent_info = os.fstat(parent)
                    entry = os.stat(store.directory.name, dir_fd=parent, follow_symlinks=False)
                    if (parent_info.st_uid != os.geteuid() or parent_info.st_mode & 0o022
                            or (entry.st_dev, entry.st_ino) != (source_identity.st_dev, source_identity.st_ino)):
                        raise DeployError("Own attempt directory changed before its bounded archive")
                    os.rename(store.directory.name, attempt["nonce"], src_dir_fd=parent, dst_dir_fd=archived)
                    os.fsync(archived)
                    os.fsync(parent)
                finally: os.close(parent)
        return outcome
    except (OSError, ValueError, TypeError, KeyError, UnicodeError) as error:
        raise DeployError("Forward completion uncertain; preserve the attempt and do not replay deployment") from error


def _recovery_verified_receiver():
    """Use the already-installed qualified1ccd administrative client; never import the uninstalled e9 candidate."""
    try:
        import install_executor as installer
        before = installer.receiver_binding()
        receiver = installer._module(installer.RECEIVER_PATH, "duora_explicit_recovery_receiver")
        if installer.receiver_binding() != before:
            raise DeployError("Qualified installed receiver changed during loading")
        return receiver
    except (ValueError, OSError, ImportError) as error:
        raise DeployError("Cannot bind the unchanged installed administrative receiver") from error


def _recovery_other_ingress_idle(*, ingress: Path = Path("/var/lib/coolify-release"), own_fence: dict | None = None) -> None:
    """Exempt only the exact own Duora intents, separately authenticated against the attempt. No other pending job."""
    root = None
    try:
        expected_fence_raw = None
        if own_fence is not None:
            _require_exact_keys(own_fence, {"schema", "project", "kind", "nonce", "attemptSha256"}, "own ingress fence")
            if (type(own_fence["schema"]) is not int or own_fence != _current_v2_fence_value(
                    nonce=own_fence["nonce"], attempt_sha256=own_fence["attemptSha256"])):
                raise DeployError("Unknown own ingress fence identity")
            expected_fence_raw = (json.dumps(own_fence, sort_keys=True, separators=(",", ":")) + "\n").encode()
        root = os.open(ingress, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        info = os.fstat(root)
        if info.st_uid != os.geteuid() or info.st_gid != os.getegid() or info.st_mode & 0o022:
            raise DeployError("Ingress ancestry is not protected administrative state")
        names = os.listdir(root)
        if len(names) > 32: raise DeployError("Unknown ingress directory budget")
        for name in names:
            entry = os.stat(name, dir_fd=root, follow_symlinks=False)
            if (stat.S_ISLNK(entry.st_mode) or entry.st_uid != os.geteuid()
                    or entry.st_gid != os.getegid() or entry.st_mode & 0o022):
                raise DeployError("Unknown or unsafe ingress member")
            if name == "duora" or not stat.S_ISDIR(entry.st_mode): continue
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=root)
            try:
                children = set(os.listdir(child))
                if "pending.json" in children:
                    if (name == "duora-current-v2-recovery" and expected_fence_raw is not None
                            and children == {"pending.json"} and RecoveryStore._read(child, "pending.json") == expected_fence_raw):
                        continue
                    raise DeployError("Another project or administrative operation has a pending intent")
            finally: os.close(child)
    except OSError as error:
        raise DeployError("Cannot establish the idle ingress context for own recovery") from error
    finally:
        if root is not None: os.close(root)


def recovery_administrative_entry(release: Release, paths: Paths, run: Runner, *, mode: str,
                                   readonly: bool, reviewed_sha256: str | None, lease) -> dict:
    """Explicit root-only mode; the public main entry has already acquired host, release and executor locks."""
    if mode not in {"enroll", "recover", "finish-forward", "complete-control"}: raise DeployError("Unknown administrative recovery mode")
    if mode == "complete-control":
        return _recovery_complete_control(release, paths, run, readonly=readonly, lease=lease)
    if mode == "recover":
        store = RecoveryStore(paths.state_dir / "current-v2-recovery")
        attempt = store.load()
        application_raw = _recovery_known_application_outcome(store, attempt)
        known_outcomes = {} if application_raw is None else {"application-outcome.json": hashlib.sha256(application_raw).hexdigest()}
        store.require_terminal_operations(outcomes=known_outcomes)  # Unknown producer refuses before further API/payload action.
        current = _recovery_recorded_current(paths)
        if current.raw != release.raw or attempt.get("current") != current.raw:
            raise DeployError("Explicit recovery must target the exact registered current, not staging or a new envelope")
        incoming = parse_manifest(json.dumps(attempt["incoming"]), image_prefix="ghcr.io/bipoxdxd/duora-")
        context = observe_current_v2_recovery(incoming, paths)
        stage = qualify_recovery_attempt(attempt, current=current, incoming=incoming,
            bindings=context["bindings"], control=context["control"], applied_catalog=context["catalog"])
        receiver = _recovery_verified_receiver()
        _recovery_other_ingress_idle()
        receiver.require_idle(context["baseline"]["applicationUuid"])
        image_ids = _recovery_require_cached_images(current)
        compose = _compose_command(paths, paths.state_dir / RELEASES_DIR / (current.release_id + ".env"))
        _recovery_require_quiescent(compose, run, context["postgresId"])
        if readonly:
            return {"status": "preflight", "complete": False, "payloadAuthenticated": False,
                    "releaseId": current.release_id, "catalogStage": stage}
        return _recovery_perform_application(store, attempt, context, current, incoming, paths, run, stage=stage,
            image_ids=image_ids, existing_raw=application_raw)
    current = _recovery_recorded_current(paths)
    if current.raw != release.raw: raise DeployError("Administrative target must be the exact registered current")
    receiver = _recovery_verified_receiver()
    pin = _guard.decode_json(_recovery_root_read(Path("/etc/duora/coolify-deploy.json")))
    if pin.get("compose") != str(paths.compose_file):
        raise DeployError("Administrative operation requires the actual observer-pinned Compose path")
    if mode == "enroll":
        require_no_current_v2_recovery_intent(paths)
        receiver.require_host_idle()
        receiver.require_idle(pin["application_uuid"])
        return enroll_current_v2_recovery_control(paths.state_dir,
            inspect=lambda: inspect_current_v2_recovery_control(current, paths),
            reviewed_sha256=None if readonly else reviewed_sha256)
    # A completed product change may have lost only its final local archive acknowledgement. This path does not
    # stop, start, migrate or replay SQL: it re-observes the known current and closes only its own exact journal.
    _recovery_other_ingress_idle()
    receiver.require_idle(pin["application_uuid"])
    runtime_recheck(current, paths)
    compose = _compose_command(paths, paths.state_dir / RELEASES_DIR / (current.release_id + ".env"))
    pending = RecoveryStore(paths.state_dir / "current-v2-recovery")
    forward_attempt = pending.load()
    if (forward_attempt.get("incoming") != current.raw
            or _recovery_digest(current.raw["migrations"]) != _recovery_attempt_hashes(forward_attempt)[1]
            or _read_migration_head(compose, run) != current.latest_migration):
        raise DeployError("Recorded forward catalog is incomplete or unknown")
    _recovery_catalog_acl_witness(compose, run, current.raw["migrations"])
    _wait_ready(compose, run, DEFAULT_READINESS_TIMEOUT_SECONDS, time.sleep)
    _require_services_running(compose, run)
    return finish_current_v2_forward(pending, current, paths, readonly=readonly)


def _recovery_known_application_outcome(store: RecoveryStore, attempt: dict) -> bytes | None:
    """Authenticate the closed public outcome shape before exempting its file from producer classification."""
    try:
        with store._directory() as directory:
            if "application-outcome.json" not in os.listdir(directory): return None
            pointer = _guard.decode_json(store._read(directory, "pending.json"))
            raw = store._read(directory, "application-outcome.json")
            result = _guard.decode_json(raw)
        _require_exact_keys(result, {"schema", "status", "attemptSha256", "releaseId", "commit", "stage",
            "catalogSha256", "sqlSha256", "preservationSha256", "controlSha256", "imageIds", "runtimeApps"}, "application recovery outcome")
        stage = result["stage"]
        hashes = _recovery_attempt_hashes(attempt)
        if (type(result["schema"]) is not int or result["schema"] != 1
                or result["status"] != "application-ready-control-pending" or stage not in {"initial", "expanded"}
                or result["attemptSha256"] != pointer["sha256"]
                or result["releaseId"] != attempt["current"]["releaseId"] or result["commit"] != attempt["current"]["commit"]
                or result["preservationSha256"] != _guard.digest(attempt["bindings"])
                or result["controlSha256"] != _guard.digest(attempt["control"])
                or result["catalogSha256"] != (hashes[0] if stage == "initial" else hashes[1])
                or result["sqlSha256"] != (None if stage == "initial" else hashes[2])):
            raise DeployError("Application recovery outcome is unknown or belongs to another attempt")
        _require_exact_keys(result["imageIds"], set(IMAGE_NAMES), "recorded recovery images")
        for identifier in result["imageIds"].values():
            _require_match(identifier, re.compile(r"sha256:[a-f0-9]{64}"), "recorded recovery image identity")
        _require_exact_keys(result["runtimeApps"], {"web", "worker", "edge"}, "recovered runtime identities")
        identifiers = set()
        for role, runtime in result["runtimeApps"].items():
            _require_exact_keys(runtime, {"id", "imageId", "reference", "commit", "startedAt", "restartCount"}, "recovered application identity")
            _require_match(runtime["id"], re.compile(r"[a-f0-9]{64}"), "recovered application id")
            _guard._timestamp(runtime["startedAt"])
            if (runtime["id"] in identifiers or runtime["imageId"] != result["imageIds"][role]
                    or runtime["reference"] != attempt["current"]["images"][role] or runtime["commit"] != result["commit"]
                    or type(runtime["restartCount"]) is not int or runtime["restartCount"] != 0):
                raise DeployError("Sealed application identity is foreign, unstable or unknown")
            identifiers.add(runtime["id"])
        return raw
    except (KeyError, TypeError, ValueError) as error:
        raise DeployError("Unknown application outcome must be preserved, never overwritten") from error


def _recovery_perform_application(store: RecoveryStore, attempt: dict, context: dict, current: Release,
                                  incoming: Release, paths: Paths, run: Runner, *, stage: str,
                                  image_ids: dict[str, str], existing_raw: bytes | None) -> dict:
    compose = _compose_command(paths, paths.state_dir / RELEASES_DIR / (current.release_id + ".env"))
    with store._directory() as directory:
        pointer = _guard.decode_json(store._read(directory, "pending.json"))
    outcome = {"schema": 1, "status": "application-ready-control-pending", "attemptSha256": pointer["sha256"],
        "releaseId": current.release_id, "commit": current.commit, "stage": stage,
        "catalogSha256": _recovery_digest(context["catalog"]), "sqlSha256": None if stage == "initial" else attempt["sqlSha256"],
        "preservationSha256": _guard.digest(context["bindings"]), "controlSha256": _guard.digest(context["control"]),
        "imageIds": image_ids}
    if existing_raw is not None:
        outcome["runtimeApps"] = _recovery_require_running_applications(current, paths, image_ids)
        expected_raw = (json.dumps(outcome, sort_keys=True, separators=(",", ":")) + "\n").encode()
        if existing_raw != expected_raw:
            raise DeployError("Application outcome differs from its actual running/preserved/control/image context")
    outcomes = {} if existing_raw is None else {"application-outcome.json": hashlib.sha256(existing_raw).hexdigest()}

    def reobserve():
        store.require_terminal_operations(outcomes=outcomes)
        fresh = observe_current_v2_recovery(incoming, paths)
        if fresh["current"].raw != current.raw or fresh["postgresId"] != context["postgresId"]:
            raise DeployError("Recovery current or preserved database container changed")
        if qualify_recovery_attempt(attempt, current=current, incoming=incoming, bindings=fresh["bindings"],
                control=fresh["control"], applied_catalog=fresh["catalog"]) != stage:
            raise DeployError("Recovery catalog stage changed")

    if existing_raw is not None:
        reobserve()
        if stage == "expanded": _recovery_catalog_acl_witness(compose, run, context["catalog"])
        _wait_ready(compose, run, DEFAULT_READINESS_TIMEOUT_SECONDS, time.sleep)
        _require_services_running(compose, run)
        runtime_recheck(current, paths)
        reobserve()
        if _recovery_require_running_applications(current, paths, image_ids) != outcome["runtimeApps"]:
            raise DeployError("Sealed application identity changed while observing readiness")
        with store._directory() as directory:
            store._sync_existing(directory, "application-outcome.json", existing_raw)
        return {"status": "application-recovered-control-pending", "complete": False,
                "releaseId": current.release_id, "commit": current.commit, "reusedApplicationOutcome": True}

    invocation = secrets.token_hex(16)
    sql_command = compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1",
        "--single-transaction", "--username", "postgres", "--dbname", "duora", "--file", "/dev/stdin"]
    application_command = compose + ["up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"]
    backup_command = [str(paths.backup_command), "verify"]
    query_prefix = compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--quiet", "--tuples-only",
        "--no-align", "--username", "postgres", "--dbname", "duora", "--command"]
    readonly_queries = {"select to_regclass('ops.__ef_migrations_history') is not null",
        'select coalesce(max("MigrationId"), \'\') from ops.__ef_migrations_history', PRIVILEGED_ACCOUNTS_WITHOUT_MFA_SQL}
    readiness = compose + ["exec", "--no-TTY", "edge", "wget", "--quiet", "--spider", "--timeout=5",
        "http://duora-web:8080/health/ready"]
    states = compose + ["ps", "--all", "--format", "{{.Service}}={{.State}}", "web", "worker", "edge"]

    def guarded_run(command: list[str], *, capture: bool = False, input_text: str | None = None):
        phase = None
        if command == sql_command:
            if input_text is None or hashlib.sha256(input_text.encode()).hexdigest() != attempt["sqlSha256"]:
                raise DeployError("Recovery SQL producer differs from the exact authenticated incoming payload")
            phase = "sql"
        elif input_text is not None:
            raise DeployError("Unexpected recovery producer input")
        elif command == application_command: phase = "app"
        elif command == backup_command: phase = "wal"
        if phase is not None:
            return _run_current_v2_operation(store, "recovery-" + phase + "-" + invocation, command, run, input_text=input_text)
        image_inspect = (len(command) == 6 and command[:5] == ["docker", "image", "inspect", "--format", "{{.Architecture}}"]
                         and command[-1] in current.images.values())
        query = command[:-1] == query_prefix and command[-1] in readonly_queries
        if not (image_inspect or query or command == readiness or command == states):
            raise DeployError("Command outside the finite app-only recovery contract")
        return run(command, capture=capture)

    def finish():
        reobserve()
        outcome["runtimeApps"] = _recovery_require_running_applications(current, paths, image_ids)
        with store._directory() as directory: store._write(directory, "application-outcome.json", outcome)

    return recover_current_v2_application(current, paths, guarded_run, stage=stage,
        recorded_environment=paths.state_dir / RELEASES_DIR / (current.release_id + ".env"), incoming=incoming,
        sql_sha256=attempt["sqlSha256"], authenticate_sql=lambda: _payload.extract_sql(incoming.images["migrator"],
            revision=incoming.commit, architecture=incoming.architecture), require_bindings=reobserve,
        require_quiescent=lambda: _recovery_require_quiescent(compose, run, context["postgresId"]),
        witness_acl=lambda: _recovery_catalog_acl_witness(compose, run, context["catalog"]), finish_application=finish)


def _recovery_environment_projection(rows: object, *, expected_images: set[str]) -> dict:
    """Metadata witness only. Coolify4.5 shown-once values are intentionally hidden even from API reads.

    https://github.com/coollabsio/coolify/blob/v4.5.0/app/Http/Controllers/Api/ApplicationsController.php
    Never disable that setting or infer the stored image value from a hidden field. The controlled observer's
    actual image/environment must independently witness the effective value after the positive API acknowledgement.
    Other values are hashed in memory and never returned, recorded, logged or included in a receipt.
    """
    try:
        if type(rows) is not list or not 1 <= len(rows) <= 64:
            raise DeployError("Unknown observer environment inventory")
        seen, target, others = set(), None, []
        for row in rows:
            if (type(row) is not dict or type(row.get("key")) is not str or not 1 <= len(row["key"]) <= 256
                    or type(row.get("is_preview")) is not bool):
                raise DeployError("Ambiguous observer environment metadata")
            identity = (row["key"], row["is_preview"])
            if identity in seen: raise DeployError("Duplicate observer environment identity")
            seen.add(identity)
            if identity == ("DUORA_OBSERVER_IMAGE", False): target = row
            else: others.append(row)
        if target is None or target.get("is_literal") is not True or target.get("is_shown_once") is not True:
            raise DeployError("Existing observer variable identity/privacy flags must remain unchanged")
        visible = [target[key] for key in ("value", "real_value") if key in target]
        if any(type(value) is not str or value not in expected_images for value in visible) or len(set(visible)) > 1:
            raise DeployError("Visible observer value does not bind an exact allowed image")
        target_metadata = {key: value for key, value in target.items()
            if key not in {"value", "real_value", "created_at", "updated_at", "version"}}
        return {"targetSha256": _guard.digest(target_metadata),
            "othersSha256": _guard.digest(sorted(others, key=lambda row: (row["key"], row["is_preview"]))),
            "valueVisibility": "visible" if visible else "hidden"}
    except (TypeError, ValueError, KeyError) as error:
        raise DeployError("Observer environment preservation witness refused without exposing its values") from error


def _recovery_require_running_applications(current: Release, paths: Paths, image_ids: dict[str, str], *,
                                           capture: Callable[[list[str]], bytes] | None = None) -> dict:
    """Bind the actual running containers, not just a cache hit or a successful readiness response."""
    capture = _payload.capture_binary if capture is None else capture
    compose = _compose_command(paths, paths.state_dir / RELEASES_DIR / (current.release_id + ".env"))
    result = {}
    try:
        for role in ("web", "worker", "edge"):
            _require_match(image_ids[role], re.compile(r"sha256:[a-f0-9]{64}"), "qualified running image")
            identifier = capture(compose + ["ps", "--all", "--quiet", role]).decode("ascii").strip()
            _require_match(identifier, re.compile(r"[a-f0-9]{64}"), "single running application container")
            metadata = _payload._inspect("container", identifier, capture)
            labels, state = metadata["Config"]["Labels"], metadata["State"]
            if (metadata["Id"] != identifier or metadata["Image"] != image_ids[role]
                    or metadata["Config"]["Image"] != current.images[role]
                    or labels.get("com.docker.compose.project") != "duora"
                    or labels.get("com.docker.compose.service") != role
                    or labels.get("com.docker.compose.oneoff") not in {"False", "false"}
                    or labels.get("org.opencontainers.image.source") != "https://github.com/BipoXDXD/duora"
                    or labels.get("org.opencontainers.image.title") != "duora-" + role
                    or labels.get("org.opencontainers.image.revision") != current.commit
                    or state["Status"] != "running" or state["Running"] is not True
                    or state["Paused"] is not False or state["Restarting"] is not False
                    or state["OOMKilled"] is not False or type(metadata["RestartCount"]) is not int
                    or metadata["RestartCount"] != 0):
                raise DeployError("Running application does not match the exact recorded healthy return identity")
            health = state.get("Health")
            if health is not None and (type(health) is not dict or health.get("Status") != "healthy"):
                raise DeployError("Application health is not stably qualified")
            _guard._timestamp(state["StartedAt"])
            result[role] = {"id": identifier, "imageId": image_ids[role], "reference": current.images[role],
                            "commit": current.commit, "startedAt": state["StartedAt"], "restartCount": 0}
        return result
    except (_payload.PayloadError, _guard.GuardError, ValueError, TypeError, KeyError, OSError) as error:
        raise DeployError("Actual application role/image/revision/health binding refused") from error


def _recovery_require_running_observer(current: Release, pin: dict, edge_image_id: str, *,
                                       capture: Callable[[list[str]], bytes] | None = None) -> dict:
    """Observe the sleeping container after the controlled API job. A masked API value is not this witness."""
    capture = _payload.capture_binary if capture is None else capture
    try:
        _require_match(edge_image_id, re.compile(r"sha256:[a-f0-9]{64}"), "observer image identity")
        name, application = pin["observer_container_name"], pin["application_uuid"]
        _require_match(name, re.compile(r"[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}"), "observer container name")
        _require_match(application, re.compile(r"[a-z0-9][a-z0-9-]{0,63}"), "observer application")
        image = _payload._inspect("image", edge_image_id, capture)
        item = _payload._inspect("container", name, capture)
        config, host, state = item["Config"], item["HostConfig"], item["State"]
        _require_match(item["Id"], re.compile(r"[a-f0-9]{64}"), "observer runtime id")
        if (image["Id"] != edge_image_id or item["Image"] != edge_image_id
                or item["Name"] != "/" + name or config["Image"] != current.images["edge"]
                or config["User"] != "10001:10001"
                or config["Entrypoint"] != ["/bin/sh", "-c", "exec sleep 2147483647"]
                or config.get("Cmd") not in (None, []) or state["Status"] != "running"
                or state["Running"] is not True or state["Paused"] is not False
                or state["Restarting"] is not False or state["OOMKilled"] is not False
                or state["Health"]["Status"] != "healthy" or type(item["RestartCount"]) is not int
                or item["RestartCount"] != 0):
            raise DeployError("Observer is not the exact stably healthy unprivileged sleeping return image")
        if (host["ReadonlyRootfs"] is not True or host["Privileged"] is not False
                or host["CapDrop"] != ["ALL"] or host.get("CapAdd") not in (None, [])
                or host["SecurityOpt"] not in (["no-new-privileges"], ["no-new-privileges:true"])
                or type(host["Memory"]) is not int or host["Memory"] != 16777216
                or type(host["PidsLimit"]) is not int or host["PidsLimit"] != 16
                or host["RestartPolicy"] != {"Name": "unless-stopped", "MaximumRetryCount": 0}
                or any(host.get(key) not in (None, [], {}) for key in ("Binds", "VolumesFrom", "Devices", "PortBindings"))
                or host.get("PidMode") != "" or item["Mounts"] != []
                or any(value is not None for value in item["NetworkSettings"]["Ports"].values())):
            raise DeployError("Observer isolation differs from the reviewed sleeping resource")
        networks = pin["observer_networks"]
        if (type(networks) is not dict or not networks or any(type(value) is not dict for value in networks.values())
                or set(item["NetworkSettings"]["Networks"]) != {value["name"] for value in networks.values()}
                or host["NetworkMode"] not in {value["name"] for value in networks.values()}):
            raise DeployError("Observer network membership differs from its protected PIN")
        image_labels, labels = image["Config"].get("Labels") or {}, config["Labels"]
        provenance = {"org.opencontainers.image.source": "https://github.com/BipoXDXD/duora",
            "org.opencontainers.image.title": "duora-edge", "org.opencontainers.image.revision": current.commit}
        expected_labels = {**image_labels, **pin["observer_labels"], **provenance,
            "com.docker.compose.project": application, "com.docker.compose.service": "deployment-observer",
            "com.docker.compose.oneoff": "False"}
        if (any(image_labels.get(key) != value for key, value in provenance.items())
                or any(labels.get(key) != value for key, value in expected_labels.items())
                or labels.get("traefik.enable") != "false"):
            raise DeployError("Observer ownership or immutable image provenance differs")

        def environment(values):
            if type(values) is not list or len(values) > 128: raise DeployError("Bounded observer environment required")
            result = {}
            for value in values:
                if type(value) is not str or "=" not in value: raise DeployError("Malformed observer environment")
                key, content = value.split("=", 1)
                if not key or key in result: raise DeployError("Duplicate observer environment identity")
                result[key] = content
            return result

        defaults = environment(image["Config"].get("Env") or [])
        protected = pin["observer_environment"]
        if type(protected) is not dict or protected.get("DUORA_OBSERVER_IMAGE") != current.images["edge"]:
            raise DeployError("Observer image variable is not the exact restored PIN value")
        expected_environment = {**defaults, **protected}
        if environment(config["Env"]) != expected_environment:
            raise DeployError("Effective observer environment differs from the protected image/PIN")
        _guard._timestamp(state["StartedAt"])
        return {"id": item["Id"], "imageId": edge_image_id, "reference": current.images["edge"],
            "startedAt": state["StartedAt"], "restartCount": 0,
            "environmentSha256": _guard.digest(expected_environment)}
    except (_payload.PayloadError, _guard.GuardError, ValueError, KeyError, TypeError, OSError) as error:
        raise DeployError("Actual sleeping observer identity/environment/isolation witness refused") from error


def _recovery_pin_inverse(current: Release, incoming: Release, baseline: dict, actual_raw: bytes, *,
                           incoming_manifest_sha256: str, admitted_incoming_pin_sha256: str) -> dict:
    """Pure three-field inverse. Reconstruct an incoming view in memory only when the actual PIN is already old."""
    try:
        _require_match(incoming_manifest_sha256, re.compile(r"[a-f0-9]{64}"), "incoming observer manifest digest")
        _require_match(admitted_incoming_pin_sha256, re.compile(r"[a-f0-9]{64}"), "admitted observer PIN digest")
        pin = _guard.decode_json(actual_raw)
        if (actual_raw != _recovery_pin_bytes(pin) or incoming.previous_release_id != current.release_id
                or baseline["currentReleaseId"] != current.release_id
                or baseline["manifestPath"] != "/opt/duora/releases/" + current.release_id + ".json"
                or baseline["edgeImage"] != current.images["edge"]
                or pin["application_uuid"] != baseline["applicationUuid"]):
            raise DeployError("Observer PIN does not belong to the exact known inverse")
        actual_sha = hashlib.sha256(actual_raw).hexdigest()
        if actual_sha == admitted_incoming_pin_sha256:
            if (pin["manifest"] != "/opt/duora/releases/" + incoming.release_id + ".json"
                    or pin["manifest_sha256"] != incoming_manifest_sha256
                    or pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] != incoming.images["edge"]):
                raise DeployError("Incoming observer PIN fields differ")
            pin["manifest"], pin["manifest_sha256"] = baseline["manifestPath"], baseline["manifestSha256"]
            pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = current.images["edge"]
            restored = _recovery_pin_bytes(pin)
            if hashlib.sha256(restored).hexdigest() != baseline["pinSha256"]:
                raise DeployError("Observer inverse would alter another field")
            return {"stage": "incoming", "restoredRaw": restored, "virtualIncomingRaw": actual_raw}
        if actual_sha == baseline["pinSha256"]:
            if (pin["manifest"] != baseline["manifestPath"] or pin["manifest_sha256"] != baseline["manifestSha256"]
                    or pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] != current.images["edge"]):
                raise DeployError("Restored observer PIN fields differ")
            pin["manifest"] = "/opt/duora/releases/" + incoming.release_id + ".json"
            pin["manifest_sha256"] = incoming_manifest_sha256
            pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = incoming.images["edge"]
            virtual = _recovery_pin_bytes(pin)
            if hashlib.sha256(virtual).hexdigest() != admitted_incoming_pin_sha256:
                raise DeployError("Restored observer PIN is not the exact prior inverse")
            return {"stage": "restored", "restoredRaw": actual_raw, "virtualIncomingRaw": virtual}
        raise DeployError("Unknown observer PIN cannot be restored from a snapshot")
    except (ValueError, KeyError, TypeError) as error:
        raise DeployError("Exact observer inverse qualification refused") from error


def _recovery_apply_pin(path: Path, *, expected_current_sha256: str, restored_raw: bytes, nonce: str) -> dict:
    """Only the exact admitted PIN may be atomically replaced. Unknown destinations and partial temps are preserved."""
    _require_match(expected_current_sha256, re.compile(r"[a-f0-9]{64}"), "expected observer PIN")
    _require_match(nonce, re.compile(r"[a-f0-9]{32}"), "observer inverse nonce")
    if not path.is_absolute() or type(restored_raw) is not bytes or not 0 < len(restored_raw) <= 256 * 1024:
        raise DeployError("Bounded absolute observer inverse required")
    parent = None
    try:
        _guard.decode_json(restored_raw)
        before_raw = _recovery_root_read(path)
        desired = hashlib.sha256(restored_raw).hexdigest()
        parent = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        directory = os.fstat(parent)
        info = os.stat(path.name, dir_fd=parent, follow_symlinks=False)
        if (directory.st_uid != os.geteuid() or directory.st_gid != os.getegid() or directory.st_mode & 0o022
                or not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_gid != os.getegid()
                or stat.S_IMODE(info.st_mode) != 0o600 or info.st_nlink != 1):
            raise DeployError("Existing observer PIN is not the protected private regular file")
        if hashlib.sha256(before_raw).hexdigest() == desired:
            RecoveryStore._sync_existing(parent, path.name, restored_raw)
            return {"status": "already-restored", "sha256": desired}
        if hashlib.sha256(before_raw).hexdigest() != expected_current_sha256:
            raise DeployError("Unknown observer PIN must not be overwritten")
        temporary = ".duora-recovery-pin-" + nonce + ".tmp"
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=parent)
        with os.fdopen(descriptor, "wb") as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(restored_raw)
            stream.flush()
            os.fsync(stream.fileno())
        current = os.stat(path.name, dir_fd=parent, follow_symlinks=False)
        if ((current.st_dev, current.st_ino) != (info.st_dev, info.st_ino)
                or hashlib.sha256(_recovery_root_read(path)).hexdigest() != expected_current_sha256):
            raise DeployError("Observer PIN changed before inverse; preserve the own partial temp")
        os.replace(temporary, path.name, src_dir_fd=parent, dst_dir_fd=parent)
        os.fsync(parent)
        if hashlib.sha256(_recovery_root_read(path)).hexdigest() != desired:
            raise DeployError("Observer inverse result is unknown")
        return {"status": "restored", "sha256": desired}
    except (OSError, ValueError, TypeError) as error:
        raise DeployError("Observer inverse write refused/unknown; preserve all evidence") from error
    finally:
        if parent is not None: os.close(parent)


def observe_current_v2_control(attempt: dict, paths: Paths, *, read_root: Callable[[Path], bytes] | None = None,
                                inspect_runtime: Callable[[Release], dict] | None = None,
                                read_catalog: Callable[[], list[str]] | None = None,
                                archived_intents: dict[Path, bytes] | None = None) -> dict:
    """Authenticate the same original frame while recognizing only its exact three-field PIN inverse.

    The controller may supply hash-bound copies of its own two intents ONLY after qualifying its completed control
    phase and its ingress fence. A copy fills actual ENOENT only; it never hides a foreign file, permissions error,
    environment, schema, baseline or any other missing state. All virtual views remain in memory, never on disk.
    """
    read = _recovery_root_read if read_root is None else read_root
    try:
        current = parse_manifest(json.dumps(attempt["current"]), image_prefix="ghcr.io/bipoxdxd/duora-")
        incoming = parse_manifest(json.dumps(attempt["incoming"]), image_prefix="ghcr.io/bipoxdxd/duora-")
        baseline_raw = read(paths.state_dir / "recovery-control" / (current.release_id + ".json"))
        if hashlib.sha256(baseline_raw).hexdigest() != attempt["control"]["baselineSha256"]:
            raise DeployError("Recovery control baseline differs from the exact original attempt")
        baseline = _guard.decode_json(baseline_raw)
        actual_raw = read(Path("/etc/duora/coolify-deploy.json"))
        incoming_raw = read(Path("/opt/duora/releases") / (incoming.release_id + ".json"))
        inverse = _recovery_pin_inverse(current, incoming, baseline, actual_raw,
            incoming_manifest_sha256=hashlib.sha256(incoming_raw).hexdigest(),
            admitted_incoming_pin_sha256=attempt["control"]["incomingPinSha256"])
        archived_intents = {} if archived_intents is None else archived_intents
        allowed = {Path("/var/lib/coolify-release/pending.json"): attempt["control"]["globalIntentSha256"],
                   Path("/var/lib/coolify-release/duora/pending.json"): attempt["control"]["projectIntentSha256"]}
        if set(archived_intents) - set(allowed):
            raise DeployError("Only the exact two original own intents may be observed through their archived copies")
        for path, raw in archived_intents.items():
            if type(raw) is not bytes or hashlib.sha256(raw).hexdigest() != allowed[path]:
                raise DeployError("Archived intent is not the byte-identical original")

        def original_frame(path):
            if path == Path("/etc/duora/coolify-deploy.json"): return inverse["virtualIncomingRaw"]
            try: return read(path)
            except (OSError, DeployError) as error:
                cause = error if isinstance(error, OSError) else error.__cause__
                if isinstance(cause, FileNotFoundError) and path in archived_intents:
                    return archived_intents[path]
                raise

        context = observe_current_v2_recovery(incoming, paths, read_root=original_frame,
            inspect_runtime=inspect_runtime, read_catalog=read_catalog)
        stage = qualify_recovery_attempt(attempt, current=context["current"], incoming=incoming,
            bindings=context["bindings"], control=context["control"], applied_catalog=context["catalog"])
        if "expandedOrigin" in attempt and _recovery_origin_proof(current, context, paths, admitted_origin=attempt["expandedOrigin"]) != attempt["expandedOrigin"]:
            raise DeployError("The admitted expanded origin no longer binds its exact previous recovery")
        return {**context, "incoming": incoming, "catalogStage": stage, "pinStage": inverse["stage"],
                "restoredPinRaw": inverse["restoredRaw"], "actualPinSha256": hashlib.sha256(actual_raw).hexdigest()}
    except (ValueError, TypeError, KeyError, OSError, UnicodeError) as error:
        raise DeployError("Exact recovery control frame observation refused") from error


def _current_v2_fence_value(*, nonce: str, attempt_sha256: str) -> dict:
    _require_match(nonce, re.compile(r"[a-f0-9]{32}"), "own recovery fence nonce")
    _require_match(attempt_sha256, re.compile(r"[a-f0-9]{64}"), "own recovery fence attempt")
    return {"schema": 1, "project": "duora", "kind": "current-v2-recovery", "nonce": nonce,
            "attemptSha256": attempt_sha256}


def ensure_current_v2_ingress_fence(ingress: Path, *, nonce: str, attempt_sha256: str) -> dict:
    """Fence normal ingress before either old pending marker is ever removed. No unknown marker is replaced."""
    value = _current_v2_fence_value(nonce=nonce, attempt_sha256=attempt_sha256)
    raw = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()
    parent = None
    try:
        parent = os.open(ingress, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        info = os.fstat(parent)
        if info.st_uid != os.geteuid() or info.st_gid != os.getegid() or info.st_mode & 0o022:
            raise DeployError("Administrative ingress parent is not protected")
        store = RecoveryStore(ingress / "duora-current-v2-recovery")
        with store._directory(create=True) as directory:
            names = set(os.listdir(directory))
            if names - {"pending.json"}: raise DeployError("Unknown recovery ingress fence member")
            if "pending.json" in names:
                if store._read(directory, "pending.json") != raw:
                    raise DeployError("An unknown recovery ingress fence must not be overwritten")
                store._sync_existing(directory, "pending.json", raw)
                os.fsync(parent)
                entry = os.stat(store.directory.name, dir_fd=parent, follow_symlinks=False)
                identity = os.fstat(directory)
                if (entry.st_dev, entry.st_ino) != (identity.st_dev, identity.st_ino) or store._read(directory, "pending.json") != raw:
                    raise DeployError("Own ingress fence changed during durability re-observation")
                return {"sha256": hashlib.sha256(raw).hexdigest(), "created": False}
            store._write(directory, "pending.json", value)
            os.fsync(parent)
            entry = os.stat(store.directory.name, dir_fd=parent, follow_symlinks=False)
            identity = os.fstat(directory)
            if (entry.st_dev, entry.st_ino) != (identity.st_dev, identity.st_ino) or store._read(directory, "pending.json") != raw:
                raise DeployError("New own ingress fence changed before acknowledgement")
            return {"sha256": hashlib.sha256(raw).hexdigest(), "created": True}
    except OSError as error:
        raise DeployError("Cannot durably establish the exact own ingress fence") from error
    finally:
        if parent is not None: os.close(parent)


def release_current_v2_ingress_fence(ingress: Path, *, nonce: str, attempt_sha256: str) -> None:
    """Call only after qualified control, preserved intent copies and compatibility/archive commit. Keep the directory."""
    raw = (json.dumps(_current_v2_fence_value(nonce=nonce, attempt_sha256=attempt_sha256),
        sort_keys=True, separators=(",", ":")) + "\n").encode()
    store = RecoveryStore(ingress / "duora-current-v2-recovery")
    with store._directory() as directory:
        if set(os.listdir(directory)) != {"pending.json"} or store._read(directory, "pending.json") != raw:
            raise DeployError("Only the exact own ingress fence can be released; unknown evidence is preserved")
        try:
            os.unlink("pending.json", dir_fd=directory)
            os.fsync(directory)
        except OSError as error:
            raise DeployError("Own ingress fence release is unknown; do not claim an available window") from error


def _recovery_write_same(store: RecoveryStore, name: str, value: dict) -> bytes:
    """Commit a known public result exactly once; a re-entry re-establishes its durability, never overwrites it."""
    raw = (json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    with store._directory() as directory:
        if name in os.listdir(directory):
            if store._read(directory, name) != raw:
                raise DeployError("Unknown recovery outcome is preserved, not overwritten")
            store._sync_existing(directory, name, raw)
        else: store._write(directory, name, value)
    return raw


def _recovery_optional_read(path: Path) -> bytes | None:
    try: return _recovery_root_read(path)
    except (OSError, DeployError) as error:
        cause = error if isinstance(error, OSError) else error.__cause__
        if isinstance(cause, FileNotFoundError): return None
        raise


def _recovery_preserve_raw(store: RecoveryStore, name: str, raw: bytes) -> bytes:
    if (name not in {"global-intent.preimage.json", "project-intent.preimage.json", "compatibility-preimage.json"}
            or type(raw) is not bytes or not 0 < len(raw) <= 256 * 1024):
        raise DeployError("Only bounded own intent/pointer preimages may be preserved")
    with store._directory() as directory:
        if name in os.listdir(directory):
            if store._read(directory, name) != raw: raise DeployError("Unknown/partial preimage must never be replaced")
            store._sync_existing(directory, name, raw)
            return raw
        try:
            fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
            with os.fdopen(fd, "wb") as stream:
                os.fchmod(stream.fileno(), 0o600)
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            os.fsync(directory)
            return raw
        except OSError as error:
            raise DeployError("Own preimage durability unknown; retain all original fences") from error


def _recovery_locate_store(active: Path, history: Path, attempt: dict) -> RecoveryStore:
    archived = history / attempt["nonce"]
    present = lambda path: path.exists() or path.is_symlink()
    if active != archived and present(active) and present(archived): raise DeployError("Ambiguous active and archived attempt; neither may be overwritten")
    location = active if present(active) else archived
    store = RecoveryStore(location)
    if store.load() != attempt: raise DeployError("Known recovery archive identity differs")
    return store


def _recovery_archive_owned(store: RecoveryStore, history_path: Path, nonce: str) -> RecoveryStore:
    _require_match(nonce, re.compile(r"[a-f0-9]{32}"), "own archive nonce")
    history = RecoveryStore(history_path)
    with history._directory(create=True) as archived:
        if store.directory == history_path / nonce:
            with store._directory() as source: os.fsync(source)
            os.fsync(archived)
            return store
        with store._directory() as source:
            if nonce in os.listdir(archived): raise DeployError("An existing recovery archive is never replaced")
            parent = os.open(store.directory.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                info = os.fstat(parent)
                entry, actual = os.stat(store.directory.name, dir_fd=parent, follow_symlinks=False), os.fstat(source)
                if (info.st_uid != os.geteuid() or info.st_gid != os.getegid() or info.st_mode & 0o022
                        or (entry.st_dev, entry.st_ino) != (actual.st_dev, actual.st_ino)):
                    raise DeployError("Own archive source changed")
                os.rename(store.directory.name, nonce, src_dir_fd=parent, dst_dir_fd=archived)
                os.fsync(archived)
                os.fsync(parent)
            finally: os.close(parent)
    return RecoveryStore(history_path / nonce)


def _recovery_pointer_value(attempt: dict, attempt_sha: str, application_sha: str, control_sha: str) -> dict:
    return {"schema": 1, "releaseId": attempt["current"]["releaseId"], "nonce": attempt["nonce"],
        "attemptSha256": attempt_sha, "applicationOutcomeSha256": application_sha, "controlOutcomeSha256": control_sha}


def _recovery_parse_compatibility(raw: bytes, paths: Paths, *, allow_active: bool = False) -> tuple[dict, RecoveryStore, RecoveryStore]:
    pointer = _guard.decode_json(raw)
    _require_exact_keys(pointer, {"schema", "releaseId", "nonce", "attemptSha256", "applicationOutcomeSha256", "controlOutcomeSha256"}, "compatibility pointer")
    if type(pointer["schema"]) is not int or pointer["schema"] != 1: raise DeployError("Unknown compatibility schema")
    _require_match(pointer["nonce"], re.compile(r"[a-f0-9]{32}"), "compatibility nonce")
    _require_match(pointer["releaseId"], _RELEASE_ID, "compatibility release")
    _recovery_hashes({name: pointer[name] for name in ("attemptSha256", "applicationOutcomeSha256", "controlOutcomeSha256")},
        {"attemptSha256", "applicationOutcomeSha256", "controlOutcomeSha256"})
    a = RecoveryStore(paths.state_dir / "current-v2-recovery-history" / pointer["nonce"])
    b = RecoveryStore(paths.state_dir / "current-v2-control-history" / pointer["nonce"])
    if allow_active:
        candidates = []
        for active, archived in ((paths.state_dir / "current-v2-recovery", a.directory), (paths.state_dir / "current-v2-control", b.directory)):
            active_exists = active.exists() or active.is_symlink()
            archived_exists = archived.exists() or archived.is_symlink()
            if active_exists and archived_exists: raise DeployError("Ambiguous active/archive compatibility identity")
            candidates.append(RecoveryStore(active if active_exists else archived))
        a, b = candidates
    attempt = a.load()
    if b.load() != attempt: raise DeployError("Archived application/control attempts differ")
    with a._directory() as directory: original = _guard.decode_json(a._read(directory, "pending.json"))
    application = _recovery_known_application_outcome(a, attempt)
    if application is None: raise DeployError("Compatibility archive has no positive application outcome")
    with b._directory() as directory: control_raw = b._read(directory, "control-outcome.json")
    if pointer != _recovery_pointer_value(attempt, original["sha256"], hashlib.sha256(application).hexdigest(), hashlib.sha256(control_raw).hexdigest()):
        raise DeployError("Compatibility pointer does not bind its immutable archived outcomes")
    control = _guard.decode_json(control_raw)
    if (control.get("status") != "control-recovered" or control.get("attemptSha256") != pointer["attemptSha256"]
            or control.get("applicationOutcomeSha256") != pointer["applicationOutcomeSha256"]):
        raise DeployError("Archived control outcome is not this exact completed recovery")
    return pointer, a, b


def _recovery_origin_proof(current: Release, context: dict, paths: Paths, *, admitted_origin: dict | None = None) -> dict:
    if admitted_origin is None:
        raw = _recovery_optional_read(paths.state_dir / "current-v2-compatibility.json")
        if raw is None: raise DeployError("A head48 alone is not a qualified expanded forward origin")
    else:
        # Once admitted, this origin refers to an immutable previous control archive. Our own tail is allowed to
        # advance the active compatibility pointer; it cannot change the historical origin of this attempt.
        embedded = admitted_origin.get("pointer")
        _require_exact_keys(embedded, {"schema", "releaseId", "nonce", "attemptSha256", "applicationOutcomeSha256", "controlOutcomeSha256"}, "admitted origin pointer")
        assert isinstance(embedded, dict)
        _require_match(embedded["nonce"], re.compile(r"[a-f0-9]{32}"), "admitted origin archive nonce")
        expected_raw = (json.dumps(embedded, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
        if hashlib.sha256(expected_raw).hexdigest() != admitted_origin.get("compatibilitySha256"):
            raise DeployError("Admitted origin pointer snapshot checksum differs")
        raw = _recovery_root_read(paths.state_dir / "current-v2-control-history" / embedded["nonce"] / "compatibility-pointer.json")
        if raw != expected_raw: raise DeployError("Immutable origin archive pointer differs; no mutable-pointer fallback")
    pointer, a, b = _recovery_parse_compatibility(raw, paths)
    previous = a.load()
    if (previous["current"] != current.raw or previous["bindings"] != context["bindings"]
            or previous["control"]["baselineSha256"] != context["control"]["baselineSha256"]):
        raise DeployError("Prior completed recovery does not bind the unchanged current/preservation baseline")
    application_raw = _recovery_known_application_outcome(a, previous)
    if application_raw is None: raise DeployError("No known prior recovered application")
    application = _guard.decode_json(application_raw)
    hashes = _recovery_attempt_hashes(previous)
    if application["stage"] != "expanded" or application["sqlSha256"] != hashes[2]:
        raise DeployError("An initial-only recovery is not proof of retained catalog compatibility")
    a.require_terminal_operations(outcomes={"application-outcome.json": hashlib.sha256(application_raw).hexdigest()})
    _recovery_control_evidence(b, previous, context)
    with b._directory() as directory:
        if b._read(directory, "compatibility-pointer.json") != raw:
            raise DeployError("The prior compatibility pointer does not match its exact immutable archive")
    return {"catalogSha256": hashes[1], "compatibilitySha256": hashlib.sha256(raw).hexdigest(),
        "attemptSha256": pointer["attemptSha256"], "pointer": pointer}


def _recovery_expanded_origin(current: Release, context: dict, paths: Paths, *, run: Runner | None = None) -> dict:
    """Admit the next forward only with an actual completed recovery chain and current ACL/runtime witnesses.

    This does not claim the next payload already ran. The next attempt still journals and authenticates its own
    migration/reconciliation producers; any later failure uses its own independent repair-forward protocol.
    """
    if _recovery_digest(context["catalog"]) != _recovery_retained_catalog(current):
        raise DeployError("Only the exact qualified retained catalog can be admitted")
    origin = _recovery_origin_proof(current, context, paths)
    _, a, _ = _recovery_parse_compatibility(_recovery_root_read(paths.state_dir / "current-v2-compatibility.json"), paths)
    application_raw = _recovery_known_application_outcome(a, a.load())
    if application_raw is None: raise DeployError("The prior expanded origin application outcome disappeared")
    application = _guard.decode_json(application_raw)
    image_ids = _recovery_require_cached_images(current)
    if image_ids != application["imageIds"] or _recovery_require_running_applications(current, paths, image_ids) != application["runtimeApps"]:
        raise DeployError("Expanded origin no longer has the exact recovered running application identities")
    compose = _compose_command(paths, paths.state_dir / RELEASES_DIR / (current.release_id + ".env"))
    effective_run = run_command if run is None else run
    _recovery_require_quiescent(compose, effective_run, context["postgresId"])
    _recovery_catalog_acl_witness(compose, effective_run, context["catalog"])
    return origin


def _recovery_resolve_stores(paths: Paths) -> tuple[RecoveryStore | None, RecoveryStore | None, dict | None]:
    active = paths.state_dir / "current-v2-recovery"
    if active.exists() or active.is_symlink():
        a = RecoveryStore(active)
        attempt = a.load()
        b_path = paths.state_dir / "current-v2-control"
        archived_b = paths.state_dir / "current-v2-control-history" / attempt["nonce"]
        if b_path.exists() or b_path.is_symlink() or archived_b.exists() or archived_b.is_symlink():
            b = _recovery_locate_store(b_path, paths.state_dir / "current-v2-control-history", attempt)
        else: b = None
        return a, b, attempt
    pointer_path = paths.state_dir / "current-v2-compatibility.json"
    try: pointer_path.lstat()
    except FileNotFoundError: return None, None, None
    raw = _recovery_optional_read(pointer_path)
    if raw is None: raise DeployError("Recovery compatibility pointer disappeared during resolution")
    _, a, b = _recovery_parse_compatibility(raw, paths, allow_active=True)
    return a, b, a.load()


def _recovery_qualified_preimages(a: RecoveryStore, b: RecoveryStore | None, attempt: dict, context: dict, paths: Paths, *,
                                   ingress: Path = Path("/var/lib/coolify-release")) -> dict[Path, bytes]:
    if b is None: return {}
    required = {"control-ready.json", "control-outcome.json", "global-intent.preimage.json", "project-intent.preimage.json", "compatibility-pointer.json"}
    with b._directory() as directory:
        if not required <= set(os.listdir(directory)): return {}
    _recovery_control_evidence(b, attempt, context)
    application_raw = _recovery_known_application_outcome(a, attempt)
    if application_raw is None: raise DeployError("Archived intents require a known application recovery outcome")
    with b._directory() as directory:
        outcome = _guard.decode_json(b._read(directory, "control-outcome.json"))
        pointer_raw = b._read(directory, "compatibility-pointer.json")
        copies = {Path("/var/lib/coolify-release/pending.json"): b._read(directory, "global-intent.preimage.json"),
            Path("/var/lib/coolify-release/duora/pending.json"): b._read(directory, "project-intent.preimage.json")}
    if outcome["applicationOutcomeSha256"] != hashlib.sha256(application_raw).hexdigest():
        raise DeployError("Control/raw-intent capability does not bind the known application outcome")
    with a._directory() as directory: pointer = _guard.decode_json(a._read(directory, "pending.json"))
    expected = (json.dumps(_current_v2_fence_value(nonce=attempt["nonce"], attempt_sha256=pointer["sha256"]),
        sort_keys=True, separators=(",", ":")) + "\n").encode()
    fence_path = ingress / "duora-current-v2-recovery/pending.json"
    fence = _recovery_optional_read(fence_path)
    if fence is None:
        if (a.directory != paths.state_dir / "current-v2-recovery-history" / attempt["nonce"]
                or b.directory != paths.state_dir / "current-v2-control-history" / attempt["nonce"]
                or _recovery_root_read(paths.state_dir / "current-v2-compatibility.json") != pointer_raw):
            raise DeployError("Missing fence is qualified only after both archives and their exact compatibility pointer")
        _recovery_parse_compatibility(pointer_raw, paths)
        with RecoveryStore(fence_path.parent)._directory() as directory:
            if os.listdir(directory): raise DeployError("Unknown administrative ingress member after completed recovery")
        if any(_recovery_optional_read(path) is not None for path in (ingress / "pending.json", ingress / "duora/pending.json")):
            raise DeployError("Completed compatibility entry requires idle original ingress")
    elif fence != expected: raise DeployError("Unqualified raw-intent capability has an unknown fence")
    return copies


def _recovery_observe_resolved(a: RecoveryStore, b: RecoveryStore | None, attempt: dict, paths: Paths) -> dict:
    current = parse_manifest(json.dumps(attempt["current"]), image_prefix="ghcr.io/bipoxdxd/duora-")
    baseline_raw = _recovery_root_read(paths.state_dir / "recovery-control" / (current.release_id + ".json"))
    if hashlib.sha256(baseline_raw).hexdigest() != attempt["control"]["baselineSha256"]:
        raise DeployError("Resolved recovery baseline differs")
    preliminary = {"current": current, "baseline": _guard.decode_json(baseline_raw)}
    copies = _recovery_qualified_preimages(a, b, attempt, preliminary, paths)
    return observe_current_v2_control(attempt, paths, archived_intents=copies)


def _recovery_remove_original_intent(path: Path, raw: bytes) -> None:
    parent = None
    try:
        before = _recovery_optional_read(path)
        parent = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        info = os.fstat(parent)
        if info.st_uid != os.geteuid() or info.st_gid != os.getegid() or info.st_mode & 0o022:
            raise DeployError("Original ingress parent is not protected")
        if before is not None:
            if before != raw: raise DeployError("Unknown original ingress intent must be preserved")
            entry = os.stat(path.name, dir_fd=parent, follow_symlinks=False)
            if (not stat.S_ISREG(entry.st_mode) or entry.st_uid != os.geteuid() or entry.st_gid != os.getegid()
                    or entry.st_nlink != 1 or entry.st_mode & 0o022 or _recovery_root_read(path) != raw):
                raise DeployError("Own original ingress intent changed before removal")
            os.unlink(path.name, dir_fd=parent)
        os.fsync(parent)
        if _recovery_optional_read(path) is not None:
            raise DeployError("Original ingress intent removal is not known complete")
    except OSError as error:
        raise DeployError("Own original intent cleanup interrupted; keep the fence and known preimages") from error
    finally:
        if parent is not None: os.close(parent)


def _recovery_finish_control_tail(application_store: RecoveryStore, control_store: RecoveryStore, attempt: dict,
                                  context: dict, paths: Paths, *, ingress: Path = Path("/var/lib/coolify-release"),
                                  confirm: Callable[[], object]) -> dict:
    """No RPCs or service actions. Preserve raw intents before unlink; archive own journals; release fence last."""
    try:
        a = _recovery_locate_store(application_store.directory, paths.state_dir / "current-v2-recovery-history", attempt)
        b = _recovery_locate_store(control_store.directory, paths.state_dir / "current-v2-control-history", attempt)
        application_raw = _recovery_known_application_outcome(a, attempt)
        if application_raw is None: raise DeployError("No known application outcome for control-tail completion")
        with a._directory() as directory: pointer = _guard.decode_json(a._read(directory, "pending.json"))
        expected_fence = (json.dumps(_current_v2_fence_value(nonce=attempt["nonce"], attempt_sha256=pointer["sha256"]),
            sort_keys=True, separators=(",", ":")) + "\n").encode()
        fence_path = ingress / "duora-current-v2-recovery/pending.json"
        fence = _recovery_optional_read(fence_path)
        both_archived = (a.directory == paths.state_dir / "current-v2-recovery-history" / attempt["nonce"]
            and b.directory == paths.state_dir / "current-v2-control-history" / attempt["nonce"])
        if fence != expected_fence and not (fence is None and both_archived):
            raise DeployError("Only the exact own fenced tail can remove its original intents")
        known = _recovery_control_evidence(b, attempt, context)
        with b._directory() as directory:
            ready_raw = b._read(directory, "control-ready.json")
            ready = _guard.decode_json(ready_raw)
        confirm()
        originals = [("global-intent.preimage.json", ingress / "pending.json", attempt["control"]["globalIntentSha256"]),
            ("project-intent.preimage.json", ingress / "duora/pending.json", attempt["control"]["projectIntentSha256"])]
        for name, path, checksum in originals:
            with b._directory() as directory:
                names = set(os.listdir(directory))
                saved = b._read(directory, name) if name in names else None
            actual = _recovery_optional_read(path)
            if actual is not None and hashlib.sha256(actual).hexdigest() != checksum:
                raise DeployError("Foreign original intent blocks cleanup; all evidence remains preserved")
            if saved is None:
                if actual is None: raise DeployError("Missing original without an authenticated raw preimage")
                saved = _recovery_preserve_raw(b, name, actual)
            if hashlib.sha256(saved).hexdigest() != checksum or (actual is not None and actual != saved):
                raise DeployError("Raw own intent preimage differs from the original admitted attempt")
            known[name] = hashlib.sha256(saved).hexdigest()
        # Freeze the previous compatibility pointer BEFORE a new one can be published. The exact preimage remains
        # immutable in this journal; an old pointer must already bind a completed known history, never an arbitrary file.
        pointer_path = paths.state_dir / "current-v2-compatibility.json"
        absent = b'{"absent":true,"schema":1}\n'
        with b._directory() as directory:
            saved_pointer = b._read(directory, "compatibility-preimage.json") if "compatibility-preimage.json" in os.listdir(directory) else None
        if saved_pointer is None:
            old = _recovery_optional_read(pointer_path)
            if old is not None: _recovery_parse_compatibility(old, paths)
            saved_pointer = _recovery_preserve_raw(b, "compatibility-preimage.json", absent if old is None else old)
        elif saved_pointer != absent:
            previous, _, _ = _recovery_parse_compatibility(saved_pointer, paths)
            if previous["nonce"] == attempt["nonce"]: raise DeployError("Compatibility history cannot reference itself")
        known["compatibility-preimage.json"] = hashlib.sha256(saved_pointer).hexdigest()
        outcome = {"schema": 1, "status": "control-recovered", "attemptSha256": pointer["sha256"],
            "releaseId": attempt["current"]["releaseId"], "commit": attempt["current"]["commit"],
            "applicationOutcomeSha256": hashlib.sha256(application_raw).hexdigest(),
            "controlReadySha256": hashlib.sha256(ready_raw).hexdigest(),
            "globalIntentSha256": attempt["control"]["globalIntentSha256"],
            "projectIntentSha256": attempt["control"]["projectIntentSha256"],
            "previousCompatibilitySha256": None if saved_pointer == absent else hashlib.sha256(saved_pointer).hexdigest()}
        outcome_raw = _recovery_write_same(b, "control-outcome.json", outcome)
        known["control-outcome.json"] = hashlib.sha256(outcome_raw).hexdigest()
        compatibility = _recovery_pointer_value(attempt, pointer["sha256"], outcome["applicationOutcomeSha256"], known["control-outcome.json"])
        compatibility_raw = _recovery_write_same(b, "compatibility-pointer.json", compatibility)
        known["compatibility-pointer.json"] = hashlib.sha256(compatibility_raw).hexdigest()
        a.require_terminal_operations(outcomes={"application-outcome.json": hashlib.sha256(application_raw).hexdigest()}, durable=True)
        b.require_terminal_operations(outcomes=known, durable=True)
        confirm()
        for name, path, _ in originals:
            with b._directory() as directory: raw = b._read(directory, name)
            _recovery_remove_original_intent(path, raw)
        actual_pointer = _recovery_optional_read(pointer_path)
        if actual_pointer != compatibility_raw:
            if actual_pointer != (None if saved_pointer == absent else saved_pointer):
                raise DeployError("Compatibility pointer drifted; never overwrite unknown history")
            if actual_pointer is None:
                parent = os.open(pointer_path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try: RecoveryStore._write(parent, pointer_path.name, compatibility)
                finally: os.close(parent)
            else:
                _recovery_apply_pin(pointer_path, expected_current_sha256=hashlib.sha256(actual_pointer).hexdigest(),
                    restored_raw=compatibility_raw, nonce=attempt["nonce"])
        else:
            parent = os.open(pointer_path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try: RecoveryStore._sync_existing(parent, pointer_path.name, compatibility_raw)
            finally: os.close(parent)
        a = _recovery_archive_owned(a, paths.state_dir / "current-v2-recovery-history", attempt["nonce"])
        b = _recovery_archive_owned(b, paths.state_dir / "current-v2-control-history", attempt["nonce"])
        # The compatibility pointer is not sufficient by itself: both immutable archives must now exist and bind it.
        _recovery_parse_compatibility(_recovery_root_read(pointer_path), paths)
        confirm()
        if _recovery_optional_read(fence_path) is None:
            # A crash after unlink but before its directory fsync cannot be called durable by cached absence alone.
            with RecoveryStore(fence_path.parent)._directory() as directory:
                if os.listdir(directory): raise DeployError("Unexpected ingress member after own fence release")
                os.fsync(directory)
        else: release_current_v2_ingress_fence(ingress, nonce=attempt["nonce"], attempt_sha256=pointer["sha256"])
        return {"status": "current-v2-recovered", "complete": True, "releaseId": outcome["releaseId"],
            "commit": outcome["commit"], "deploymentUuid": ready["deploymentUuid"],
            "compatibilitySha256": hashlib.sha256(compatibility_raw).hexdigest()}
    except (OSError, ValueError, TypeError, KeyError) as error:
        raise DeployError("Control tail interrupted/unknown; preserve all own journals and fences without replay") from error


def _recovery_control_evidence(store: RecoveryStore, attempt: dict, context: dict, *, inflight: bool = False) -> dict:
    """Authenticate every known public/preimage file before exempting it from the closed journal inventory."""
    if store.load() != attempt: raise DeployError("Control journal does not bind the exact recovery attempt")
    with store._directory() as directory:
        pointer = _guard.decode_json(store._read(directory, "pending.json"))
        names = set(os.listdir(directory))
        envraw = store._read(directory, "environment-preimage.json")
        env = _guard.decode_json(envraw)
        _require_exact_keys(env, {"schema", "attemptSha256", "projection"}, "control environment preimage")
        _require_exact_keys(env["projection"], {"targetSha256", "othersSha256", "valueVisibility"}, "environment projection")
        _recovery_hashes({name: env["projection"][name] for name in ("targetSha256", "othersSha256")}, {"targetSha256", "othersSha256"})
        if (type(env["schema"]) is not int or env["schema"] != 1 or env["attemptSha256"] != pointer["sha256"]
                or env["projection"]["valueVisibility"] not in {"visible", "hidden"}):
            raise DeployError("Unqualified control environment preimage")
        known = {"environment-preimage.json": hashlib.sha256(envraw).hexdigest()}
        current, baseline = context["current"], context["baseline"]
        descriptors = {
            "pin": {"kind": "three-field-inverse", "from": attempt["control"]["incomingPinSha256"], "to": baseline["pinSha256"]},
            "env": {"kind": "observer-env-inverse", "application": baseline["applicationUuid"],
                    "image": current.images["edge"], "preimageSha256": known["environment-preimage.json"]},
            "observer": {"kind": "start-restored-observer", "application": baseline["applicationUuid"],
                    "pinSha256": baseline["pinSha256"], "edgeImage": current.images["edge"]}}
        for phase, descriptor in descriptors.items():
            intent = _guard.decode_json(store._read(directory, "operation-control-" + phase + "-intent.json"))
            if intent.get("commandSha256") != _guard.digest(descriptor):
                raise DeployError("Controlled observer entry does not bind its exact preceding PIN/environment inverse")
        if "control-ready.json" in names:
            raw = store._read(directory, "control-ready.json")
            ready = _guard.decode_json(raw)
            _require_exact_keys(ready, {"schema", "status", "attemptSha256", "deploymentUuid", "observer", "environmentPreimageSha256"}, "control-ready")
            _require_match(ready["deploymentUuid"], re.compile(r"[a-z0-9][a-z0-9-]{0,63}"), "known observer job")
            acknowledged = _guard.decode_json(store._read(directory, "operation-control-observer-terminal.json"))
            if acknowledged.get("deploymentUuid") != ready["deploymentUuid"]:
                raise DeployError("Control-ready UUID is not the positively acknowledged observer operation")
            if (type(ready["schema"]) is not int or ready["schema"] != 1 or ready["status"] != "control-ready"
                    or ready["attemptSha256"] != pointer["sha256"]
                    or ready["environmentPreimageSha256"] != known["environment-preimage.json"]):
                raise DeployError("Unknown control-ready seal")
            known["control-ready.json"] = hashlib.sha256(raw).hexdigest()
        for name, field in (("global-intent.preimage.json", "globalIntentSha256"), ("project-intent.preimage.json", "projectIntentSha256")):
            if name in names:
                raw = store._read(directory, name)
                if hashlib.sha256(raw).hexdigest() != attempt["control"][field]: raise DeployError("Unknown raw ingress preimage")
                known[name] = hashlib.sha256(raw).hexdigest()
        if "compatibility-preimage.json" in names:
            raw = store._read(directory, "compatibility-preimage.json")
            if raw != b'{"absent":true,"schema":1}\n':
                value = _guard.decode_json(raw)
                _require_exact_keys(value, {"schema", "releaseId", "nonce", "attemptSha256", "applicationOutcomeSha256", "controlOutcomeSha256"}, "prior compatibility pointer")
                if type(value["schema"]) is not int or value["schema"] != 1: raise DeployError("Unknown prior compatibility schema")
            known["compatibility-preimage.json"] = hashlib.sha256(raw).hexdigest()
        if "control-outcome.json" in names:
            raw = store._read(directory, "control-outcome.json")
            value = _guard.decode_json(raw)
            _require_exact_keys(value, {"schema", "status", "attemptSha256", "releaseId", "commit", "applicationOutcomeSha256",
                "controlReadySha256", "globalIntentSha256", "projectIntentSha256", "previousCompatibilitySha256"}, "control outcome")
            if (type(value["schema"]) is not int or value["schema"] != 1 or value["status"] != "control-recovered"
                    or value["attemptSha256"] != pointer["sha256"] or value["releaseId"] != current.release_id
                    or value["commit"] != current.commit or value["controlReadySha256"] != known.get("control-ready.json")
                    or value["globalIntentSha256"] != attempt["control"]["globalIntentSha256"]
                    or value["projectIntentSha256"] != attempt["control"]["projectIntentSha256"]
                    or "compatibility-preimage.json" not in known): raise DeployError("Unknown control outcome binding")
            _require_match(value["applicationOutcomeSha256"], re.compile(r"[a-f0-9]{64}"), "control/application outcome binding")
            if value["previousCompatibilitySha256"] is not None:
                if value["previousCompatibilitySha256"] != known["compatibility-preimage.json"]: raise DeployError("Prior compatibility binding differs")
            known["control-outcome.json"] = hashlib.sha256(raw).hexdigest()
        if "compatibility-pointer.json" in names:
            raw = store._read(directory, "compatibility-pointer.json")
            if "control-outcome.json" not in known: raise DeployError("Compatibility requires a known completed control outcome")
            outcome = _guard.decode_json(store._read(directory, "control-outcome.json"))
            if _guard.decode_json(raw) != _recovery_pointer_value(attempt, pointer["sha256"], outcome["applicationOutcomeSha256"], known["control-outcome.json"]):
                raise DeployError("Compatibility pointer differs from its own qualified outcomes")
            known["compatibility-pointer.json"] = hashlib.sha256(raw).hexdigest()
    store.require_terminal_operations(outcomes=known,
        active_observer_sha256=_guard.digest(descriptors["observer"]) if inflight and "control-ready.json" not in known else None)
    return known


def _recovery_confirm_application(store: RecoveryStore, attempt: dict, context: dict, paths: Paths, run: Runner) -> dict:
    raw = _recovery_known_application_outcome(store, attempt)
    if raw is None: raise DeployError("Known application recovery outcome is required before control return")
    outcome = _guard.decode_json(raw)
    store.require_terminal_operations(outcomes={"application-outcome.json": hashlib.sha256(raw).hexdigest()})
    current = context["current"]
    if (outcome["stage"] != context["catalogStage"] or context["bindings"] != attempt["bindings"]
            or context["control"] != attempt["control"] or not _minimum_contract_v2(paths.state_dir)):
        raise DeployError("Recovered application/contract stage differs from its exact original preservation frame")
    images = _recovery_require_cached_images(current)
    if images != outcome["imageIds"]:
        raise DeployError("Recovery image cache identity changed")
    compose = _compose_command(paths, paths.state_dir / RELEASES_DIR / (current.release_id + ".env"))
    _recovery_require_quiescent(compose, run, context["postgresId"])
    _require_no_privileged_account_without_mfa(current, context["catalog"][-1], compose, run)
    if context["catalogStage"] == "expanded": _recovery_catalog_acl_witness(compose, run, context["catalog"])
    _wait_ready(compose, run, DEFAULT_READINESS_TIMEOUT_SECONDS, time.sleep)
    _require_services_running(compose, run)
    if _recovery_require_running_applications(current, paths, images) != outcome["runtimeApps"]:
        raise DeployError("Sealed recovered runtime identities changed")
    return outcome


def _recovery_current_steady(release: Release, paths: Paths, run: Runner) -> bool:
    """Only the exact recovered recorded current may serve an observer without any deployment effects.

    An in-flight child uses the known original fence and exact observer intent. A completed child additionally
    requires both immutable archives and their hash-bound compatibility pointer; no ghost fence is recreated.
    """
    store, control, attempt = _recovery_resolve_stores(paths)
    if store is None or attempt is None or attempt.get("current") != release.raw: return False
    # A historical compatibility marker must not intercept a normal previous-release rollback after a later
    # successful forward. The ordinary path still checks current/knownPrevious/floor and any active intent.
    if _recovery_recorded_current(paths).raw != release.raw: return False
    context = _recovery_observe_resolved(store, control, attempt, paths)
    if context["pinStage"] != "restored" or control is None:
        raise DeployError("Steady recovery requires the exact restored prior PIN and its known control journal")
    with store._directory() as directory:
        pointer = _guard.decode_json(store._read(directory, "pending.json"))
    fence = _recovery_optional_read(Path("/var/lib/coolify-release/duora-current-v2-recovery/pending.json"))
    expected = (json.dumps(_current_v2_fence_value(nonce=attempt["nonce"], attempt_sha256=pointer["sha256"]),
        sort_keys=True, separators=(",", ":")) + "\n").encode()
    both_archived = (store.directory == paths.state_dir / "current-v2-recovery-history" / attempt["nonce"]
        and control.directory == paths.state_dir / "current-v2-control-history" / attempt["nonce"])
    if fence != expected and not (fence is None and both_archived):
        raise DeployError("Only an exact fenced child or fully qualified completed compatibility entry may be steady")
    _recovery_control_evidence(control, attempt, context, inflight=not both_archived)
    _recovery_confirm_application(store, attempt, context, paths, run)
    return True


def _recovery_complete_control(release: Release, paths: Paths, run: Runner, *, readonly: bool, lease) -> dict:
    store, control, attempt = _recovery_resolve_stores(paths)
    if store is None or attempt is None: raise DeployError("No known current-v2 recovery exists")
    if attempt.get("current") != release.raw or _recovery_recorded_current(paths).raw != release.raw:
        raise DeployError("Control return targets only the unchanged registered current, never a new/staged envelope")
    application_raw = _recovery_known_application_outcome(store, attempt)
    if application_raw is None: raise DeployError("Complete the explicit application recovery phase first")
    outcomes = {"application-outcome.json": hashlib.sha256(application_raw).hexdigest()}
    store.require_terminal_operations(outcomes=outcomes, durable=not readonly)
    context = _recovery_observe_resolved(store, control, attempt, paths)
    _recovery_confirm_application(store, attempt, context, paths, run)
    with store._directory() as directory:
        pointer = _guard.decode_json(store._read(directory, "pending.json"))
    own_fence = _current_v2_fence_value(nonce=attempt["nonce"], attempt_sha256=pointer["sha256"])
    _recovery_other_ingress_idle(own_fence=own_fence)
    if readonly:
        return {"status": "preflight", "complete": False, "controlMutation": False,
                "releaseId": release.release_id, "commit": release.commit}
    control = RecoveryStore(paths.state_dir / "current-v2-control") if control is None else control

    def resolve():
        fresh_a, fresh_b, fresh_attempt = _recovery_resolve_stores(paths)
        if fresh_a is None or fresh_attempt != attempt: raise DeployError("Resolved recovery changed during control completion")
        return fresh_a, fresh_b, _recovery_observe_resolved(fresh_a, fresh_b, attempt, paths)
    def confirm():
        fresh_a, _, fresh = resolve()
        _recovery_confirm_application(fresh_a, attempt, fresh, paths, run)
    def observer():
        _, _, fresh = resolve()
        if fresh["pinStage"] != "restored": raise DeployError("Sleeping observer requires the already-restored exact PIN")
        pin = _guard.decode_json(fresh["restoredPinRaw"])
        outcome = _guard.decode_json(application_raw)
        return _recovery_require_running_observer(release, pin, outcome["imageIds"]["edge"])

    ready = None
    if control.directory.exists() or control.directory.is_symlink():
        with control._directory() as directory:
            if "control-ready.json" in os.listdir(directory): ready = _guard.decode_json(control._read(directory, "control-ready.json"))
    if ready is not None:
        _recovery_control_evidence(control, attempt, context)
        if context["pinStage"] != "restored" or ready["observer"] != observer():
            raise DeployError("Previously known observer control identity changed; never replay its start")
        confirm()
    else:
        receiver = _recovery_verified_receiver()
        _recovery_control_phases(control, attempt, context, paths, receiver=receiver, lease=lease,
            confirm_application=confirm, observe_frame=lambda: resolve()[2], observe_observer=observer)
    with control._directory() as directory:
        ready = _guard.decode_json(control._read(directory, "control-ready.json"))
    def confirm_tail():
        confirm()
        if observer() != ready["observer"]:
            raise DeployError("Known observer identity changed during tail; preserve the remaining fence")
    return _recovery_finish_control_tail(store, control, attempt, context, paths, confirm=confirm_tail)


def _recovery_control_phases(store: RecoveryStore, attempt: dict, context: dict, paths: Paths, *,
                              receiver, lease, confirm_application: Callable[[], object],
                              observe_frame: Callable[[], dict], observe_observer: Callable[[], dict],
                              ingress: Path = Path("/var/lib/coolify-release"),
                              sleep: Callable[[float], None] = time.sleep) -> dict:
    """Three separately journaled control producers. No app, DB, current or history mutation happens here.

    The root caller holds host/project/executor leases and authenticates A/application-outcome before entry.
    An unknown RPC result never permits another call, including when the service happens to look healthy.
    Tail cleanup is deliberately separate: control-ready still reports complete=False and retains every fence.
    """
    confirm_application()
    if store.directory.exists() or store.directory.is_symlink():
        if store.load() != attempt: raise DeployError("Control journal belongs to another attempt")
    else: store.arm(attempt)
    with store._directory() as directory:
        pointer = _guard.decode_json(store._read(directory, "pending.json"))
        names = set(os.listdir(directory))
        known = {}
        preimage = None
        if "environment-preimage.json" in names:
            raw = store._read(directory, "environment-preimage.json")
            preimage = _guard.decode_json(raw)
            _require_exact_keys(preimage, {"schema", "attemptSha256", "projection"}, "environment preimage")
            projection = preimage["projection"]
            _require_exact_keys(projection, {"targetSha256", "othersSha256", "valueVisibility"}, "environment projection")
            _recovery_hashes({key: projection[key] for key in ("targetSha256", "othersSha256")}, {"targetSha256", "othersSha256"})
            if (type(preimage["schema"]) is not int or preimage["schema"] != 1
                    or preimage["attemptSha256"] != pointer["sha256"] or projection["valueVisibility"] not in {"visible", "hidden"}):
                raise DeployError("Unknown control environment preimage")
            known["environment-preimage.json"] = hashlib.sha256(raw).hexdigest()
        if "control-ready.json" in names:
            raw = store._read(directory, "control-ready.json")
            ready = _guard.decode_json(raw)
            _require_exact_keys(ready, {"schema", "status", "attemptSha256", "deploymentUuid", "observer", "environmentPreimageSha256"}, "control-ready")
            if (type(ready["schema"]) is not int or ready["schema"] != 1 or ready["status"] != "control-ready"
                    or ready["attemptSha256"] != pointer["sha256"] or preimage is None
                    or ready["environmentPreimageSha256"] != known["environment-preimage.json"]
                    or not {"operation-control-" + phase + "-terminal.json" for phase in ("pin", "env", "observer")} <= names):
                raise DeployError("Incomplete control-ready state cannot authorize a new producer")
            known["control-ready.json"] = hashlib.sha256(raw).hexdigest()
    store.require_terminal_operations(outcomes=known, durable=True)
    current, incoming = context["current"], context["incoming"]
    uuid = context["baseline"]["applicationUuid"]
    _require_match(uuid, re.compile(r"[a-z0-9][a-z0-9-]{0,63}"), "control application")
    allowed_images = {current.images["edge"], incoming.images["edge"]}
    now_projection = _recovery_environment_projection(receiver.api("applications/" + uuid + "/envs"), expected_images=allowed_images)
    if preimage is None:
        preimage = {"schema": 1, "attemptSha256": pointer["sha256"], "projection": now_projection}
        raw = _recovery_write_same(store, "environment-preimage.json", preimage)
        known["environment-preimage.json"] = hashlib.sha256(raw).hexdigest()
    elif any(now_projection[key] != preimage["projection"][key] for key in ("targetSha256", "othersSha256")):
        raise DeployError("Observer or unrelated environment metadata drifted before control return")
    ensure_current_v2_ingress_fence(ingress, nonce=attempt["nonce"], attempt_sha256=pointer["sha256"])

    def operation(phase, descriptor, action):
        expected_command = _guard.digest(descriptor)
        confirm_application()
        store.require_terminal_operations(outcomes=known, durable=True)
        with store._directory() as directory:
            intent_name = "operation-control-" + phase + "-intent.json"
            if intent_name in os.listdir(directory):
                intent = _guard.decode_json(store._read(directory, intent_name))
                if intent["commandSha256"] != expected_command:
                    raise DeployError("Existing control producer is not this exact inverse")
                terminal = _guard.decode_json(store._read(directory, "operation-control-" + phase + "-terminal.json"))
                return subprocess.CompletedProcess(["control-" + phase], 0, terminal.get("deploymentUuid", ""), "")
        receiver.require_idle(uuid)
        return store.run_operation("control-" + phase, expected_command, action, outcomes=known)

    desired_pin_sha = hashlib.sha256(context["restoredPinRaw"]).hexdigest()
    def pin_inverse():
        _recovery_apply_pin(Path("/etc/duora/coolify-deploy.json"),
            expected_current_sha256=attempt["control"]["incomingPinSha256"],
            restored_raw=context["restoredPinRaw"], nonce=attempt["nonce"])
        return subprocess.CompletedProcess(["control-pin"], 0, "", "")
    operation("pin", {"kind": "three-field-inverse", "from": attempt["control"]["incomingPinSha256"], "to": desired_pin_sha}, pin_inverse)
    fresh = observe_frame()
    if fresh["pinStage"] != "restored" or fresh["restoredPinRaw"] != context["restoredPinRaw"]:
        raise DeployError("Acknowledged PIN inverse has not restored the exact prior control")

    def environment_inverse():
        receiver.update_envs(uuid, {"DUORA_OBSERVER_IMAGE": current.images["edge"]})
        return subprocess.CompletedProcess(["control-env"], 0, "", "")
    operation("env", {"kind": "observer-env-inverse", "application": uuid, "image": current.images["edge"],
        "preimageSha256": known["environment-preimage.json"]}, environment_inverse)
    after = _recovery_environment_projection(receiver.api("applications/" + uuid + "/envs"), expected_images={current.images["edge"]})
    if any(after[key] != preimage["projection"][key] for key in ("targetSha256", "othersSha256")):
        raise DeployError("Acknowledged environment inverse changed unrelated metadata")

    def start_observer():
        result = receiver.api("applications/" + uuid + "/start", "POST")
        if type(result) is not dict or type(result.get("deployment_uuid")) is not str:
            raise DeployError("Observer start result is unknown; do not replay the request")
        return subprocess.CompletedProcess(["control-observer"], 0, result["deployment_uuid"], "")

    # Only this lock is handed to the controlled observer entry. Ingress and project remain exclusively held.
    lease.release_executor()
    try:
        result = operation("observer", {"kind": "start-restored-observer", "application": uuid,
            "pinSha256": desired_pin_sha, "edgeImage": current.images["edge"]}, start_observer)
        identifier = result.stdout
        _require_match(identifier, re.compile(r"[a-z0-9][a-z0-9-]{0,63}"), "acknowledged observer job")
        deadline = time.monotonic() + 1200
        while True:
            if receiver.deployment_finished(receiver.api("deployments/" + identifier)): break
            if time.monotonic() >= deadline: raise DeployError("Known observer job did not finish; preserve its exact UUID and do not restart it")
            sleep(5)
    finally: lease.reacquire_executor()
    confirm_application()
    final = observe_frame()
    if final["pinStage"] != "restored" or final["restoredPinRaw"] != context["restoredPinRaw"]:
        raise DeployError("Restored PIN changed during the known observer job")
    observer = observe_observer()
    confirm_application()
    ready = {"schema": 1, "status": "control-ready", "attemptSha256": pointer["sha256"],
        "deploymentUuid": identifier, "observer": observer,
        "environmentPreimageSha256": known["environment-preimage.json"]}
    _recovery_write_same(store, "control-ready.json", ready)
    return {"status": "control-ready-tail-pending", "complete": False, "deploymentUuid": identifier,
        "releaseId": current.release_id, "commit": current.commit}


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


# --- Troca do digest do componente PostgreSQL --------------------------------------------------------------------------
# Operação administrativa: docs/operations/deploy.md, "Troca do digest do PostgreSQL". Sem transação entre arquivos: o
# journal é write-ahead, cada escrita é temp + fsync + rename e o commit é o rename de current.json. Nenhuma falha
# reverte sozinha; quem decide é o operador, com --resume ou --abort.

SWAP_DIRECTORY = "postgres-swap"
SWAP_HISTORY_DIRECTORY = "postgres-swap-history"
SWAP_FENCE_DIRECTORY = "duora-postgres-swap"
SWAP_TEMPORARY_NAME = ".postgres-swap.tmp"
SWAP_FORWARD_PHASES = ("journaled", "verify-backup", "quiesce", "swap-runtime", "verify-runtime", "reopen", "commit")
SWAP_OTHER_PHASES = ("awaiting-reseal", "aborting", "aborted-awaiting-reseal", "closing")
SWAP_ID = re.compile(r"[a-f0-9]{32}")
SWAP_POSTGRES_LABEL = "io.duora.postgres.version"
SWAP_PGBACKREST_LABEL = "io.duora.pgbackrest.version"
SWAP_LOCALE = re.compile(r"[A-Za-z0-9_.@-]{0,64}")
# docs/operations/deploy.md, "Janela": 07:00-21:00 UTC fora de backups, verify-repo e prune; quarta e quinta só depois
# das 08:00 (verify-repo-full e media-check).
SWAP_WINDOW_START_HOUR, SWAP_WINDOW_LATE_START_HOUR, SWAP_WINDOW_END_HOUR = 7, 8, 21
SWAP_LATE_WEEKDAYS = (2, 3)
SWAP_COMPATIBILITY_SQL = (
    "select json_build_object('serverVersionNum',current_setting('server_version_num'),"
    "'controlVersion',c.pg_control_version,'catalogVersion',c.catalog_version_no,"
    "'encoding',pg_encoding_to_char(d.encoding),'provider',d.datlocprovider,'collate',d.datcollate,'ctype',d.datctype,"
    "'locale',d.datlocale,'recorded',d.datcollversion,'actual',pg_database_collation_actual_version(d.oid))::text "
    "from pg_control_system() c, pg_database d where d.datname=current_database()")
SWAP_COMPATIBLE_FIELDS = ("controlVersion", "catalogVersion", "encoding", "provider", "collate", "ctype", "locale")
SWAP_ACL_SQL = (
    "select md5(string_agg(n.nspname||'.'||c.relname||':'||p.priv||':'||"
    "has_table_privilege('duora_app',c.oid,p.priv)::text,',' order by n.nspname,c.relname,p.priv)) "
    "from pg_class c join pg_namespace n on n.oid=c.relnamespace cross join "
    "(values('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE'))p(priv) "
    "where c.relkind in ('r','p') and n.nspname not in ('pg_catalog','information_schema') "
    "and n.nspname not like 'pg_%'")
SWAP_PHOTO_SQL = (
    "select json_build_object('systemIdentifier',c.system_identifier::text,'catalogVersion',c.catalog_version_no,"
    "'controlVersion',c.pg_control_version,'timeline',k.timeline_id,"
    "'head',(select coalesce(max(\"MigrationId\"),'') from ops.__ef_migrations_history),"
    "'acl',(" + SWAP_ACL_SQL + "),"
    "'collation',json_build_object('provider',d.datlocprovider,'collate',d.datcollate,'ctype',d.datctype,"
    "'version',d.datcollversion))::text "
    "from pg_control_system() c, pg_control_checkpoint() k, pg_database d where d.datname=current_database()")
# Cluster descartável, sem rede, só para medir o alvo: nada aqui toca o volume nem a rede do Compose.
SWAP_PROBE_SCRIPT = (
    'set -eu; d=$(mktemp -d); case "$SWAP_PROVIDER" in i) l="--locale-provider=icu --icu-locale=$SWAP_LOCALE";; '
    'b) l="--locale-provider=builtin --builtin-locale=$SWAP_LOCALE";; *) l="--locale-provider=libc";; esac; '
    'initdb -D "$d/data" --username=postgres --auth=trust --encoding="$SWAP_ENCODING" --lc-collate="$SWAP_COLLATE" '
    '--lc-ctype="$SWAP_CTYPE" $l >/dev/null; '
    'pg_ctl -D "$d/data" -w -l "$d/log" -o "-c listen_addresses= -c unix_socket_directories=$d" start >/dev/null; '
    'psql -h "$d" -U postgres -d postgres -Atc "$SWAP_SQL"; pg_ctl -D "$d/data" -m fast stop >/dev/null')


@dataclass(frozen=True)
class SwapHost:
    """Fronteiras do host, injetáveis nos testes de protocolo; produção usa _production_swap_host."""
    image_prefix: str
    read_protected: Callable[[Path], bytes]
    inspect_runtime: Callable[[Release, Path], dict]
    require_ingress_idle: Callable[[str], None]
    executor_identity: Callable[[], dict]
    list_processes: Callable[[], list[list[str]]]
    now: Callable[[], datetime]
    sleep: Callable[[float], None] = time.sleep
    releases_root: Path = Path("/opt/duora/releases")
    pin_path: Path = Path("/etc/duora/coolify-deploy.json")
    architecture: str = "amd64"


@dataclass
class _SwapContext:
    source: Release
    target: Release
    attempt: dict
    attempt_sha256: str
    record: dict
    paths: Paths
    run: Runner
    host: SwapHost

    @property
    def phase(self) -> str:
        return self.record["phase"]

    def environment(self, release: Release) -> Path:
        return self.paths.state_dir / RELEASES_DIR / (release.release_id + ".env")

    def target_environment(self) -> Path:
        # staging/ é rascunho e o conteúdo é função do manifesto, então cada --resume pode regravar sem risco.
        return _stage_release_files(self.target, self.paths.state_dir)

    def compose(self, release: Release) -> list[str]:
        environment = self.target_environment() if release is self.target else self.environment(release)
        return _compose_command(self.paths, environment)

    def backup(self, action: str) -> subprocess.CompletedProcess[str]:
        return _check(self.run([str(self.paths.backup_command), action]), "duora-backup " + action)


def require_no_open_swap(paths: Paths) -> None:
    if (paths.state_dir / SWAP_DIRECTORY / "attempt.json").exists() or _swap_fence_path(paths.ingress).is_symlink() \
            or _swap_fence_path(paths.ingress).exists():
        raise DeployError("PostgreSQL swap aberto (journal ou fence): resolva com --resume ou --abort antes de qualquer release")


def _swap_fence_path(ingress: Path) -> Path:
    return ingress / SWAP_FENCE_DIRECTORY / "pending.json"


def _require_swap_window(now: datetime) -> None:
    moment = now.astimezone(timezone.utc)
    start = SWAP_WINDOW_LATE_START_HOUR if moment.weekday() in SWAP_LATE_WEEKDAYS else SWAP_WINDOW_START_HOUR
    if not start <= moment.hour < SWAP_WINDOW_END_HOUR:
        raise DeployError(f"Fora da janela da troca ({start:02d}:00-{SWAP_WINDOW_END_HOUR}:00 UTC): nada foi parado")


def _swap_require_target(current: Release, reference: str, host: SwapHost) -> None:
    if current.contract_version != "v2": raise DeployError("A troca exige release corrente v2")
    if current.architecture != host.architecture:
        raise DeployError(f"A troca só roda em {host.architecture}: a produção é a única arquitetura qualificada")
    _parse_images({name: reference if name == "postgres" else image for name, image in current.images.items()},
                  host.image_prefix)
    if reference.split("@")[0] != current.images["postgres"].split("@")[0]:
        raise DeployError("O alvo deve ser do mesmo repositório do componente corrente")
    if reference == current.images["postgres"]: raise DeployError("Alvo igual ao digest corrente; nada a trocar")


def _swap_recorded_pin(current: Release, paths: Paths, host: SwapHost) -> dict:
    """A corrente registrada, o manifesto e o pin precisam dizer a mesma coisa antes de qualquer efeito."""
    read = host.read_protected
    record = _guard.decode_json(read(paths.state_dir / "current.json"))
    _require_exact_keys(record, {"releaseId", "commit", "deployedAt"}, "current.json")
    recorded = _guard.decode_json(read(paths.state_dir / RELEASES_DIR / (current.release_id + ".json")))
    if record["releaseId"] != current.release_id or record["commit"] != current.commit or recorded != current.raw:
        raise DeployError("--manifest precisa ser exatamente a release corrente registrada")
    read(paths.state_dir / RELEASES_DIR / (current.release_id + ".env"))
    pin_raw = read(host.pin_path)
    pin = _guard.decode_json(pin_raw)
    manifest_path = host.releases_root / (current.release_id + ".json")
    if (pin_raw != _recovery_pin_bytes(pin) or pin["manifest"] != str(manifest_path)
            or pin["compose"] != str(paths.compose_file)):
        raise DeployError("Pin do observer não descreve a release corrente nem o Compose em uso")
    manifest_raw = read(manifest_path)
    if hashlib.sha256(manifest_raw).hexdigest() != pin["manifest_sha256"] or _guard.decode_json(manifest_raw) != current.raw:
        raise DeployError("Manifesto do pin difere da release corrente registrada")
    return pin


def _swap_image(reference: str, run: Runner, host: SwapHost) -> dict:
    result = _check(run(["docker", "image", "inspect", "--format", "{{json .}}", reference], capture=True),
                    "inspeção local da imagem " + reference)
    image = json.loads(result.stdout, object_pairs_hook=_recovery_unique_pairs)
    labels = image["Config"]["Labels"]
    if image["Architecture"] != host.architecture:
        raise DeployError("Imagem fora da arquitetura qualificada: " + reference)
    return {"id": image["Id"], "postgres": labels[SWAP_POSTGRES_LABEL], "pgbackrest": labels[SWAP_PGBACKREST_LABEL]}


def _swap_docker_run(reference: str, entrypoint: str, arguments: list[str], run: Runner, *,
                     environment: dict[str, str] | None = None) -> str:
    variables = [item for key, value in (environment or {}).items() for item in ("--env", key + "=" + value)]
    command = ["docker", "run", "--rm", "--pull", "never", "--network", "none", *variables, "--entrypoint", entrypoint,
               reference, *arguments]
    return _check(run(command, capture=True), "sonda descartável do alvo (" + entrypoint + ")").stdout


def _swap_major(version: str) -> int:
    return int(version.split(".", 1)[0])


def _swap_require_compatible(current: Release, reference: str, compose: list[str], run: Runner, host: SwapHost) -> dict:
    """Mesma major, catálogo, pgBackRest e colação: é o que dispensa pg_upgrade, stanza-upgrade e reindexação."""
    old, new = _swap_image(current.images["postgres"], run, host), _swap_image(reference, run, host)
    if _swap_major(old["postgres"]) != _swap_major(new["postgres"]) or old["pgbackrest"] != new["pgbackrest"]:
        raise DeployError("Labels do alvo divergem em major do PostgreSQL ou versão do pgBackRest")
    binary = re.search(r"PostgreSQL\) ([0-9.]+)", _swap_docker_run(reference, "postgres", ["--version"], run))
    if binary is None or binary[1] != new["postgres"]:
        raise DeployError("postgres --version do alvo diverge do label")
    version = lambda text: text.strip().split()[-1]
    if (version(_swap_docker_run(reference, "pgbackrest", ["version"], run))
            != version(_check(run(compose + ["exec", "--no-TTY", "backup", "pgbackrest", "version"], capture=True),
                              "versão do pgBackRest corrente").stdout)):
        raise DeployError("pgBackRest do alvo diverge do corrente; stanza-upgrade não é dispensável")
    live = json.loads(_query(compose, run, SWAP_COMPATIBILITY_SQL, "compatibilidade do banco corrente"))
    if not all(SWAP_LOCALE.fullmatch(live[key] or "") for key in ("encoding", "provider", "collate", "ctype", "locale")):
        raise DeployError("Colação corrente fora do formato conhecido")
    probe = json.loads(_swap_docker_run(reference, "sh", ["-c", SWAP_PROBE_SCRIPT], run, environment={
        "SWAP_PROVIDER": live["provider"], "SWAP_LOCALE": live["locale"] or "", "SWAP_ENCODING": live["encoding"],
        "SWAP_COLLATE": live["collate"], "SWAP_CTYPE": live["ctype"], "SWAP_SQL": SWAP_COMPATIBILITY_SQL}).splitlines()[-1])
    if (int(live["serverVersionNum"]) // 10000 != int(probe["serverVersionNum"]) // 10000
            or int(live["serverVersionNum"]) // 10000 != _swap_major(new["postgres"])
            or any(live[key] != probe[key] for key in SWAP_COMPATIBLE_FIELDS) or live["recorded"] != probe["actual"]):
        raise DeployError("Catálogo, major ou colação do alvo diverge do banco corrente")
    return {"sourceImageId": old["id"], "targetImageId": new["id"], "postgres": new["postgres"],
            "pgbackrest": new["pgbackrest"], "compatibility": {key: live[key] for key in SWAP_COMPATIBLE_FIELDS},
            "collationVersion": live["recorded"]}


def _swap_require_interpolated(current: Release, reference: str, paths: Paths, run: Runner) -> None:
    """Com digest literal no Compose, subir com o env novo não trocaria nada e o desenho inteiro cairia."""
    def config(environment: Path) -> dict:
        result = _check(run(_compose_command(paths, environment) + ["config", "--format", "json"], capture=True),
                        "renderização do Compose")
        return json.loads(result.stdout)
    candidate = Release(current.release_id, current.commit, current.architecture,
                        {**current.images, "postgres": reference}, current.contract_version, current.latest_migration,
                        current.previous_release_id, current.raw)
    before = config(paths.state_dir / RELEASES_DIR / (current.release_id + ".env"))
    with tempfile.TemporaryDirectory(prefix="duora-swap-") as directory:
        environment = Path(directory) / "target.env"
        environment.write_text(_release_env_text(candidate), encoding="utf-8")
        after = config(environment)
    for role in ("postgres", "backup"):
        if after["services"][role]["image"] != reference:
            raise DeployError("Compose não interpola a imagem de " + role + "; o env da troca não teria efeito")
        after["services"][role]["image"] = before["services"][role]["image"]
    if after != before: raise DeployError("Trocar a imagem mudaria outro campo do Compose")


def _swap_plan(current: Release, reference: str, paths: Paths, run: Runner, host: SwapHost) -> dict:
    """Passo 0. Só leitura, fora o pull por digest; determinístico para o hash revisado valer no apply."""
    _require_swap_window(host.now())
    require_no_open_swap(paths)
    require_no_current_v2_recovery_intent(paths)
    if (paths.state_dir / "executor-install-pending.json").exists():
        raise DeployError("Instalação do executor pendente; resolva antes da troca")
    _swap_require_target(current, reference, host)
    pin = _swap_recorded_pin(current, paths, host)
    host.require_ingress_idle(pin["application_uuid"])
    executor = host.executor_identity()
    environment = paths.state_dir / RELEASES_DIR / (current.release_id + ".env")
    _swap_require_preserved(paths, host.inspect_runtime(current, environment)["profile"])
    for image in (reference, current.images["postgres"]):
        _check(run(["docker", "pull", "--quiet", image]), "pull por digest de " + image)
    compose = _compose_command(paths, environment)
    facts = _swap_require_compatible(current, reference, compose, run, host)
    _swap_require_interpolated(current, reference, paths, run)
    for action in ("verify", "verify-repo"):
        _check(run([str(paths.backup_command), action]), "duora-backup " + action)
    read = lambda path: hashlib.sha256(host.read_protected(path)).hexdigest()
    return {"schema": 1, "operation": "postgres-component-swap", "sourceReleaseId": current.release_id,
            "sourceManifestSha256": _guard.digest(current.raw), "sourcePostgres": current.images["postgres"],
            "targetPostgres": reference, "facts": facts, "executor": executor,
            "stateSha256": {"baseline": read(paths.state_dir / _guard.BASELINE_NAME), "pin": read(host.pin_path),
                            "current": read(paths.state_dir / "current.json"), "compose": read(paths.compose_file),
                            "hostEnvironment": read(paths.host_env)}}


def _swap_require_preserved(paths: Paths, profile: dict) -> None:
    try:
        _guard.require_preserved(_private_json(paths.state_dir / _guard.BASELINE_NAME), profile)
    except (_guard.GuardError, OSError) as error:
        raise DeployError("Runtime difere do baseline selado: rode a ressela do instalador antes de fechar") from error


def _swap_manifest_bytes(release: Release) -> bytes:
    return (json.dumps(release.raw, indent=2, sort_keys=True) + "\n").encode()


def _swap_pin_bytes(actual: bytes, manifest: str, manifest_sha256: str) -> bytes:
    pin = _guard.decode_json(actual)
    pin.update(manifest=manifest, manifest_sha256=manifest_sha256)
    return _recovery_pin_bytes(pin)


def _swap_sha(raw: bytes | None) -> str | None:
    return None if raw is None else hashlib.sha256(raw).hexdigest()


def _swap_mode(path: Path) -> int:
    return stat.S_IMODE(path.stat().st_mode)


def _swap_make_attempt(current: Release, reference: str, plan: dict, paths: Paths, host: SwapHost) -> dict:
    """Congela R', os hashes de antes e depois e os bytes de current.json e previous.json antes de qualquer efeito."""
    swap_id = secrets.token_hex(16)
    now = host.now().astimezone(timezone.utc)
    provenance = "postgres-component-swap:" + swap_id
    raw = json.loads(json.dumps(current.raw))
    raw.update(releaseId="pgswap-" + reference[-64:][:12] + "-" + swap_id[:16], previousReleaseId=current.release_id,
               createdAt=now.strftime("%Y-%m-%dT%H:%M:%SZ"), gates={"status": "passed", "runUrl": provenance})
    raw["images"]["postgres"] = reference
    raw["sbom"]["postgres"] = provenance + "#sbom-postgres"
    target = parse_manifest(json.dumps(raw), image_prefix=host.image_prefix)
    releases = paths.state_dir / RELEASES_DIR
    manifest, environment = _swap_manifest_bytes(target), _release_env_text(target).encode()
    current_before = (paths.state_dir / "current.json").read_bytes()
    previous_before = _swap_read(paths.state_dir / "previous.json")
    current_after = (json.dumps({"releaseId": target.release_id, "commit": target.commit,
                                 "deployedAt": now.isoformat(timespec="seconds")}, indent=2) + "\n").encode()
    pin_before = host.pin_path.read_bytes()
    pin = _guard.decode_json(pin_before)
    manifest_path = host.releases_root / (target.release_id + ".json")
    pin_after = _swap_pin_bytes(pin_before, str(manifest_path), hashlib.sha256(manifest).hexdigest())
    current_path = paths.state_dir / "current.json"
    artifact = lambda name, path, before, after, mode: {"name": name, "path": str(path), "beforeSha256": _swap_sha(before),
                                                        "afterSha256": _swap_sha(after), "mode": mode}
    artifacts = [artifact("manifest", manifest_path, None, manifest, _swap_mode(Path(pin["manifest"]))),
        artifact("release-manifest", releases / (target.release_id + ".json"), None, manifest,
                 _swap_mode(releases / (current.release_id + ".json"))),
        artifact("release-environment", releases / (target.release_id + ".env"), None, environment,
                 _swap_mode(releases / (current.release_id + ".env"))),
        artifact("pin", host.pin_path, pin_before, pin_after, _swap_mode(host.pin_path)),
        artifact("previous", paths.state_dir / "previous.json", previous_before, current_before, _swap_mode(current_path)),
        artifact("current", current_path, current_before, current_after, _swap_mode(current_path))]
    # Do pin só entram hashes e os dois campos que voltam: o resto dele não pertence a um journal.
    return {"schema": 1, "kind": "postgres-component-swap", "swapId": swap_id, "planSha256": _guard.digest(plan),
            "plan": plan, "executor": plan["executor"], "source": current.raw, "target": raw,
            "commit": {"artifacts": artifacts, "currentBefore": current_before.decode(), "currentAfter": current_after.decode(),
                       "previousBefore": None if previous_before is None else previous_before.decode(),
                       "pinManifestBefore": pin["manifest"], "pinManifestSha256Before": pin["manifest_sha256"]}}


def _swap_sync_directory(path: Path) -> None:
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try: os.fsync(descriptor)
    finally: os.close(descriptor)


def _swap_write_atomic(path: Path, raw: bytes, *, mode: int) -> None:
    """Temp + fsync + rename + fsync do diretório. O nome é fixo: sobra de um SIGKILL não acumula."""
    temporary = path.parent / SWAP_TEMPORARY_NAME
    temporary.unlink(missing_ok=True)
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
    with os.fdopen(descriptor, "wb") as stream:
        os.fchmod(stream.fileno(), mode)
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)
    _swap_sync_directory(path.parent)


def _swap_read(path: Path) -> bytes | None:
    if path.is_symlink(): raise DeployError("Arquivo da troca virou symlink: " + path.name)
    return path.read_bytes() if path.exists() else None


def _swap_journal(paths: Paths) -> Path:
    return paths.state_dir / SWAP_DIRECTORY


def _swap_set_phase(context: _SwapContext, phase: str, **data: object) -> None:
    context.record.update(data, phase=phase, attemptSha256=context.attempt_sha256)
    raw = (json.dumps(context.record, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    _swap_write_atomic(_swap_journal(context.paths) / "phase.json", raw, mode=0o600)


def _swap_fence_raw(swap_id: str, attempt_sha256: str) -> bytes:
    return (json.dumps({"schema": 1, "project": "duora", "kind": "postgres-swap", "swapId": swap_id,
                        "attemptSha256": attempt_sha256}, sort_keys=True, separators=(",", ":")) + "\n").encode()


def _swap_ensure_fence(context: _SwapContext) -> None:
    """O receiver já recusa qualquer pending.json em subdiretório do ingress, sem mudança de código nem de configuração."""
    raw = _swap_fence_raw(context.attempt["swapId"], context.attempt_sha256)
    store = RecoveryStore(context.paths.ingress / SWAP_FENCE_DIRECTORY)
    with store._directory(create=True) as directory:
        names = set(os.listdir(directory)) - {SWAP_TEMPORARY_NAME}
        if names - {"pending.json"}: raise DeployError("Membro desconhecido na fence da troca")
        if "pending.json" in names:
            if store._read(directory, "pending.json") != raw: raise DeployError("Fence desconhecida não é sobrescrita")
            return
    _swap_write_atomic(store.directory / "pending.json", raw, mode=0o600)


def _swap_release_fence(paths: Paths) -> None:
    path = _swap_fence_path(paths.ingress)
    path.unlink()
    _swap_sync_directory(path.parent)


def _swap_hold_names(attempt: dict) -> dict[str, str]:
    prefix = "duora-postgres-swap-hold-" + attempt["swapId"][:12] + "-"
    return {prefix + "source": attempt["source"]["images"]["postgres"], prefix + "target": attempt["target"]["images"]["postgres"]}


def _swap_hold_exists(name: str, run: Runner) -> bool:
    return run(["docker", "container", "inspect", "--format", "{{.Id}}", name], capture=True).returncode == 0


def _swap_hold_images(context: _SwapContext) -> None:
    """O prune diário remove imagem sem container, mesmo com tag: um container criado e nunca iniciado segura as duas."""
    _swap_ensure_fence(context)
    for name, reference in _swap_hold_names(context.attempt).items():
        if not _swap_hold_exists(name, context.run):
            _check(context.run(["docker", "create", "--name", name, "--pull", "never", "--network", "none",
                                "--entrypoint", "/bin/true", reference], capture=True), "referência à imagem " + reference)


def _swap_step_verify_backup(context: _SwapContext) -> None:
    context.backup("verify")


def _swap_photo(compose: list[str], run: Runner) -> dict:
    return json.loads(_query(compose, run, SWAP_PHOTO_SQL, "foto do banco"))


def _swap_stop(context: _SwapContext, compose: list[str], *services: str) -> None:
    for service in services:
        _step("parando " + service)
        _check(context.run(compose + ["stop", service]), "parada de " + service)


def _swap_step_quiesce(context: _SwapContext) -> None:
    compose = context.compose(context.source)
    _swap_stop(context, compose, "worker", "web")
    inventory = context.host.inspect_runtime(context.source, context.environment(context.source))
    _recovery_require_quiescent(compose, context.run, inventory["profile"]["services"]["postgres"]["id"])
    context.record["snapshot"] = _swap_photo(compose, context.run)
    # pg_switch_wal() não faz nada em banco ocioso; o check grava e espera o segmento chegar ao repositório.
    _check(context.run(compose + ["exec", "--no-TTY", "backup", "pgbackrest", "--stanza=duora", "check"]),
           "pgbackrest check antes da parada")


def _swap_up(context: _SwapContext, compose: list[str], *services: str) -> None:
    _check(context.run(compose + ["up", "--detach", "--wait", "--no-deps", *services]), "subida de " + " ".join(services))


def _swap_recreate(context: _SwapContext, release: Release) -> None:
    # O backup recebe o WAL: para depois do postgres e sobe antes dele. Nunca "down" nem "-v".
    _swap_stop(context, context.compose(context.source), "postgres", "backup")
    compose = context.compose(release)
    _swap_up(context, compose, "backup")
    _swap_up(context, compose, "postgres")


def _swap_step_replace(context: _SwapContext) -> None:
    _swap_recreate(context, context.target)


def _swap_require_runtime(context: _SwapContext, release: Release, environment: Path) -> dict:
    """Mesmo host, Docker, volume e Compose, no digest esperado; ids novos são o efeito esperado da troca."""
    profile = context.host.inspect_runtime(release, environment)["profile"]
    baseline = _private_json(context.paths.state_dir / _guard.BASELINE_NAME)
    stable = lambda value: {key: item for key, item in value["context"].items() if key != "configHashes"}
    facts = context.attempt["plan"]["facts"]
    image_id = facts["targetImageId" if release is context.target else "sourceImageId"]
    if (stable(profile) != stable(baseline) or profile["volumes"] != baseline["volumes"]
            or any(item["imageId"] != image_id for item in profile["services"].values())):
        raise DeployError("Host, Compose, volume ou imagem diverge depois de recriar o banco")
    if _swap_photo(_compose_command(context.paths, environment), context.run) != context.record["snapshot"]:
        raise DeployError("foto do banco mudou depois de recriar: identidade, timeline, head, ACL ou colação")
    return profile


def _swap_step_verify_runtime(context: _SwapContext) -> None:
    context.record["services"] = _swap_require_runtime(context, context.target, context.target_environment())["services"]
    try:
        context.backup("verify")
    except DeployError as error:
        raise DeployError("Banco conferido no digest alvo, mas o verify falhou. Aplicação segue parada: decida entre "
                          "--resume ou --abort (" + str(error) + ")") from error


def _swap_open_application(context: _SwapContext, release: Release) -> None:
    compose = context.compose(release)
    _swap_up(context, compose, "web", "worker", "edge")
    _wait_ready(compose, context.run, DEFAULT_READINESS_TIMEOUT_SECONDS, context.host.sleep)
    _require_services_running(compose, context.run)


def _swap_require_same_services(context: _SwapContext) -> None:
    profile = context.host.inspect_runtime(context.target, context.target_environment())["profile"]
    if profile["services"] != context.record["services"]:
        raise DeployError("Containers do banco mudaram depois da conferência; nada será publicado")


def _swap_step_reopen(context: _SwapContext) -> None:
    _swap_open_application(context, context.target)
    _swap_require_same_services(context)


def _swap_artifact_states(context: _SwapContext) -> list[tuple[dict, bytes | None, str]]:
    """Classifica arquivo a arquivo. Só hash de antes ou de depois, e nenhum 'antes' seguido de 'depois'."""
    states = []
    for artifact in context.attempt["commit"]["artifacts"]:
        path = Path(artifact["path"])
        (path.parent / SWAP_TEMPORARY_NAME).unlink(missing_ok=True)
        raw = _swap_read(path)
        digest = _swap_sha(raw)
        if digest not in {artifact["beforeSha256"], artifact["afterSha256"]}:
            raise DeployError("Arquivo estranho em " + artifact["name"] + ": nem o hash de antes nem o de depois")
        states.append((artifact, raw, "after" if digest == artifact["afterSha256"] else "before"))
    names = [state for _, _, state in states]
    if names != sorted(names):  # "after" < "before": todo commit escreve em ordem.
        raise DeployError("Commit fora de ordem: estado de arquivos desconhecido")
    return states


def _swap_after_bytes(context: _SwapContext, name: str, actual: bytes | None) -> bytes:
    commit = context.attempt["commit"]
    if name == "release-environment": return _release_env_text(context.target).encode()
    if name == "pin":
        assert actual is not None  # O plano exigiu o pin e a classificação acima conferiu o hash de antes.
        manifest = next(item for item in commit["artifacts"] if item["name"] == "manifest")
        return _swap_pin_bytes(actual, manifest["path"], manifest["afterSha256"])
    if name == "previous": return commit["currentBefore"].encode()
    if name == "current": return commit["currentAfter"].encode()
    return _swap_manifest_bytes(context.target)


def _swap_before_bytes(context: _SwapContext, name: str, actual: bytes | None) -> bytes | None:
    commit = context.attempt["commit"]
    if name == "pin":
        assert actual is not None
        return _swap_pin_bytes(actual, commit["pinManifestBefore"], commit["pinManifestSha256Before"])
    if name == "previous":
        return None if commit["previousBefore"] is None else commit["previousBefore"].encode()
    return None


def _swap_commit(context: _SwapContext) -> None:
    """Passo 7, escritor próprio: _mark_current e _record_release não são atômicos nem idempotentes."""
    for artifact, raw, state in _swap_artifact_states(context):
        if state == "before":
            _swap_write_atomic(Path(artifact["path"]), _swap_after_bytes(context, artifact["name"], raw), mode=artifact["mode"])


def _swap_undo_commit(context: _SwapContext) -> None:
    for artifact, raw, state in reversed(_swap_artifact_states(context)):
        if state == "before": continue
        path = Path(artifact["path"])
        before = _swap_before_bytes(context, artifact["name"], raw)
        if before is None:
            path.unlink()
            _swap_sync_directory(path.parent)
        else:
            _swap_write_atomic(path, before, mode=artifact["mode"])


def _swap_step_commit(context: _SwapContext) -> None:
    _swap_require_same_services(context)
    _swap_commit(context)


_SWAP_STEPS: dict[str, Callable[[_SwapContext], None]] = {
    "journaled": _swap_hold_images, "verify-backup": _swap_step_verify_backup, "quiesce": _swap_step_quiesce,
    "swap-runtime": _swap_step_replace, "verify-runtime": _swap_step_verify_runtime, "reopen": _swap_step_reopen,
    "commit": _swap_step_commit}


def _swap_forward(context: _SwapContext) -> dict:
    for phase in SWAP_FORWARD_PHASES[SWAP_FORWARD_PHASES.index(context.phase):]:
        _swap_set_phase(context, phase)
        _step("troca do PostgreSQL: " + phase)
        _SWAP_STEPS[phase](context)
    _swap_set_phase(context, "awaiting-reseal")
    return {"status": "awaiting-reseal", "releaseId": context.target.release_id, "swapId": context.attempt["swapId"]}


def _swap_finish(context: _SwapContext, status: str) -> dict:
    for name in _swap_hold_names(context.attempt):
        if _swap_hold_exists(name, context.run):
            _check(context.run(["docker", "rm", "--force", name], capture=True), "remoção da referência " + name)
    for suffix in (".env", ".json"):
        (context.paths.state_dir / STAGING_DIR / (context.target.release_id + suffix)).unlink(missing_ok=True)
    history = context.paths.state_dir / SWAP_HISTORY_DIRECTORY
    with RecoveryStore(history)._directory(create=True): pass
    os.replace(_swap_journal(context.paths), history / context.attempt["swapId"])
    _swap_sync_directory(history)
    _swap_sync_directory(context.paths.state_dir)
    # Fence por último: até aqui o receiver recusa, e um crash neste ponto se resolve com --resume.
    _swap_release_fence(context.paths)
    return {"status": status, "swapId": context.attempt["swapId"]}


def _swap_close(context: _SwapContext, release: Release, actions: tuple[str, ...]) -> dict:
    environment = context.environment(release)
    _swap_require_preserved(context.paths, context.host.inspect_runtime(release, environment)["profile"])
    compose = _compose_command(context.paths, environment)
    _wait_ready(compose, context.run, DEFAULT_READINESS_TIMEOUT_SECONDS, context.host.sleep)
    _require_services_running(compose, context.run)
    for action in actions:
        context.backup(action)
    _swap_set_phase(context, "closing")
    return _swap_finish(context, "closed")


def _swap_resume(context: _SwapContext) -> dict:
    if context.phase == "closing": return _swap_finish(context, "closed")
    if context.phase == "awaiting-reseal": return _swap_close(context, context.target, ("verify-repo",))
    if context.phase == "aborted-awaiting-reseal":
        return _swap_close(context, context.source, ("verify", "verify-repo"))
    if context.phase == "aborting": raise DeployError("Abort interrompido só continua com --abort")
    return _swap_forward(context)


def _swap_abort(context: _SwapContext) -> dict:
    if context.phase == "closing": return _swap_finish(context, "closed")
    if context.phase == "awaiting-reseal":
        raise DeployError("Commit já feito: feche com --resume; para voltar, use a troca reversa")
    if context.phase == "aborted-awaiting-reseal":
        raise DeployError("Abort já feito: rode a ressela contra a release de origem e feche com --resume")
    states = _swap_artifact_states(context)
    if states[-1][2] == "after": raise DeployError("current.json já foi trocado: só --resume segue daqui")
    origin = context.record.get("abortedFrom", context.phase)
    _swap_set_phase(context, "aborting", abortedFrom=origin)
    _swap_undo_commit(context)
    reached = SWAP_FORWARD_PHASES.index(origin)
    if reached >= SWAP_FORWARD_PHASES.index("swap-runtime"):
        _swap_stop(context, context.compose(context.source), "worker", "web")
        _swap_recreate(context, context.source)
        _swap_require_runtime(context, context.source, context.environment(context.source))
        _swap_open_application(context, context.source)
        # stop muda startedAt, que o baseline fixa: sem ressela a próxima release seria recusada e travaria o host.
        _swap_set_phase(context, "aborted-awaiting-reseal")
        return {"status": "aborted-awaiting-reseal", "releaseId": context.source.release_id,
                "swapId": context.attempt["swapId"]}
    if reached >= SWAP_FORWARD_PHASES.index("quiesce"): _swap_open_application(context, context.source)
    environment = context.environment(context.source)
    _swap_require_preserved(context.paths, context.host.inspect_runtime(context.source, environment)["profile"])
    _swap_set_phase(context, "closing")
    return _swap_finish(context, "aborted")


def _swap_require_sole_operator(paths: Paths, run: Runner, host: SwapHost) -> None:
    """Cliente morto não prova job parado: dois containers sobre duora_pgdata é o pior caso, mesmo parados."""
    for argv in host.list_processes():
        if _swap_is_project_compose(argv, paths):
            raise DeployError("Processo compose do projeto ainda vivo; espere terminar antes de --resume ou --abort")
    for role in ("postgres", "backup"):
        result = _check(run(["docker", "ps", "--all", "--no-trunc", "--quiet", "--filter", "label=com.docker.compose.project=duora",
                             "--filter", "label=com.docker.compose.service=" + role], capture=True), "inventário de " + role)
        if len(result.stdout.split()) > 1:
            raise DeployError("Mais de um container " + role + " do projeto; resolva à mão antes de --resume ou --abort")


def _swap_is_project_compose(argv: list[str], paths: Paths) -> bool:
    if not any(Path(item).name in {"compose", "docker-compose"} for item in argv): return False
    if any(str(paths.compose_file) in item for item in argv) or "--project-name=duora" in argv: return True
    return any(item in {"-p", "--project-name"} and value == "duora" for item, value in zip(argv, argv[1:]))


def _swap_list_processes(root: Path = Path("/proc")) -> list[list[str]]:
    processes = []
    for entry in root.iterdir():
        if not entry.name.isdigit(): continue
        try:
            raw = (entry / "cmdline").read_bytes()
        except (FileNotFoundError, ProcessLookupError):
            continue  # O processo terminou durante a leitura.
        processes.append([item.decode("utf-8", "replace") for item in raw.split(b"\0") if item])
    return processes


def _swap_load(source: Release, reference: str, paths: Paths, run: Runner, host: SwapHost) -> _SwapContext | None:
    store = RecoveryStore(_swap_journal(paths))
    if not (store.directory / "attempt.json").exists(): return None
    with store._directory() as directory:
        raw = store._read(directory, "attempt.json")
        record = (_guard.decode_json(store._read(directory, "phase.json"))
                  if "phase.json" in os.listdir(directory) else None)
    attempt = _guard.decode_json(raw)
    digest = hashlib.sha256(raw).hexdigest()
    if type(attempt.get("schema")) is not int or attempt["schema"] != 1 or attempt["kind"] != "postgres-component-swap":
        raise DeployError("Journal da troca desconhecido")
    record = {"phase": SWAP_FORWARD_PHASES[0], "attemptSha256": digest} if record is None else record
    if record.get("attemptSha256") != digest or record.get("phase") not in {*SWAP_FORWARD_PHASES, *SWAP_OTHER_PHASES}:
        raise DeployError("Fase da troca não pertence a este journal")
    target = parse_manifest(json.dumps(attempt["target"]), image_prefix=host.image_prefix)
    if attempt["source"] != source.raw or target.images["postgres"] != reference:
        raise DeployError("--manifest e --target-postgres precisam ser os do journal aberto")
    if attempt["executor"] != host.executor_identity():
        raise DeployError("Executor diferente do que abriu a troca")
    return _SwapContext(source, target, attempt, digest, record, paths, run, host)


def _swap_finish_orphan_fence(paths: Paths) -> dict:
    """Journal já arquivado e fence restante: só a fence exata daquele journal é removida."""
    path = _swap_fence_path(paths.ingress)
    if not path.exists(): raise DeployError("Nenhuma troca aberta")
    fence = _guard.decode_json(path.read_bytes())
    swap_id = fence.get("swapId")
    if not isinstance(swap_id, str) or SWAP_ID.fullmatch(swap_id) is None:
        raise DeployError("Fence desconhecida; nada foi removido")
    attempt = _swap_read(paths.state_dir / SWAP_HISTORY_DIRECTORY / swap_id / "attempt.json")
    if attempt is None or path.read_bytes() != _swap_fence_raw(swap_id, hashlib.sha256(attempt).hexdigest()):
        raise DeployError("Fence desconhecida; nada foi removido")
    _swap_release_fence(paths)
    return {"status": "closed", "swapId": swap_id}


def postgres_swap_entry(source: Release, reference: str, paths: Paths, run: Runner, *, action: str,
                        reviewed_plan_sha256: str | None, host: SwapHost) -> dict:
    """Modo administrativo: main já exigiu root direto e tomou os três locks."""
    try:
        if action == "plan" or action == "apply":
            plan = _swap_plan(source, reference, paths, run, host)
            digest = _guard.digest(plan)
            if action == "plan": return {"status": "plan", "planSha256": digest, "plan": plan}
            if digest != reviewed_plan_sha256: raise DeployError("Plano difere do revisado; nada foi alterado")
            attempt = _swap_make_attempt(source, reference, plan, paths, host)
            raw = (json.dumps(attempt, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
            store = RecoveryStore(_swap_journal(paths))
            with store._directory(create=True): pass
            _swap_write_atomic(store.directory / "attempt.json", raw, mode=0o600)
            context = _swap_load(source, reference, paths, run, host)
            assert context is not None  # O journal acabou de ser gravado sob os três locks.
            _swap_ensure_fence(context)
            return _swap_forward(context)
        context = _swap_load(source, reference, paths, run, host)
        if context is None: return _swap_finish_orphan_fence(paths)
        _swap_require_sole_operator(paths, run, host)
        _swap_ensure_fence(context)
        return _swap_resume(context) if action == "resume" else _swap_abort(context)
    except (_guard.GuardError, _payload.PayloadError, ValueError, KeyError, TypeError, IndexError) as error:
        raise DeployError("Troca do PostgreSQL recusada: " + str(error)) from error


def _swap_require_ingress_idle(application_uuid: str) -> None:
    receiver = _recovery_verified_receiver()
    receiver.require_host_idle()
    receiver.require_idle(application_uuid)


def _swap_executor_identity(paths: Paths) -> dict:
    """A isenção do instalador compara este commit: a ressela não pode virar troca de código."""
    receipt = _guard.decode_json(_recovery_root_read(paths.state_dir / "executor-install-receipt.json"))
    own = hashlib.sha256(Path(__file__).resolve().read_bytes()).hexdigest()
    if receipt.get("status") != "installed" or receipt.get("executorSha256") != own:
        raise DeployError("Executor em execução não é o instalado pelo recibo")
    return {name: receipt[name] for name in ("sourceCommit", "manifestSha256", "executorSha256")}


def _production_swap_host(image_prefix: str, paths: Paths) -> SwapHost:
    return SwapHost(image_prefix, _recovery_root_read, lambda release, environment: _collect_runtime(release, environment, paths),
                    _swap_require_ingress_idle, lambda: _swap_executor_identity(paths), _swap_list_processes,
                    lambda: datetime.now(timezone.utc))


def _die_with_parent(parent: int) -> None:
    """Roda no filho antes do exec: queda da sessão não pode deixar compose órfão recriando serviço."""
    if sys.platform != "linux": return
    if ctypes.CDLL(None).prctl(PR_SET_PDEATHSIG, signal.SIGKILL, 0, 0, 0) != 0 or os.getppid() != parent:
        os._exit(1)


def _kill_group(process: subprocess.Popen) -> None:
    try: os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError: pass
    process.wait()


def run_in_own_group(command: list[str], *, capture: bool = False,
                     input_text: str | None = None) -> subprocess.CompletedProcess[str]:
    """Como run_command, mas com filho em sessão própria, teto que mata o grupo inteiro e PDEATHSIG."""
    environment = {"PATH": "/usr/bin:/bin", "LC_ALL": "C"} if command and command[0] == "docker" else None
    parent = os.getpid()
    process = subprocess.Popen(command, text=True, env=environment, start_new_session=True,
        stdin=subprocess.DEVNULL if input_text is None else subprocess.PIPE,
        stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE if capture else None,
        preexec_fn=lambda: _die_with_parent(parent))
    try:
        stdout, stderr = process.communicate(input_text, timeout=COMMAND_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired as error:
        _kill_group(process)
        raise DeployError(f"comando excedeu {COMMAND_TIMEOUT_SECONDS} s: {' '.join(command[:6])} ...") from error
    except BaseException:
        _kill_group(process)
        raise
    return subprocess.CompletedProcess(command, process.returncode, stdout, stderr)


# --- CLI ------------------------------------------------------------------------------------------------------------

def _require_recovery_administrator() -> None:
    # The existing delegated normal-deploy command must not acquire new administrative recovery powers through
    # a trailing sudoers wildcard or the receiver's forced SSH command. This entry is for a direct root operator.
    if (os.getuid() != 0 or os.geteuid() != 0 or os.environ.get("SUDO_UID", "0") != "0"
            or os.environ.get("SSH_ORIGINAL_COMMAND")):
        raise DeployError("Direct administrative root execution is required for explicit recovery/enrollment")


@dataclass
class RecoveryAdministrativeLease:
    executor: int
    held: bool = True

    def release_executor(self) -> None:
        if self.held:
            fcntl.flock(self.executor, fcntl.LOCK_UN)
            self.held = False

    def reacquire_executor(self) -> None:
        if not self.held:
            try: fcntl.flock(self.executor, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as error:
                raise DeployError("Executor is busy; preserve the known control phase and do not repeat it") from error
            self.held = True


@contextmanager
def recovery_administrative_locks(paths: Paths, *, ingress: Path = Path("/var/lib/coolify-release")):
    """Same order as the receiver/static installer. All locks already exist; inspection creates none.

    The executor lock alone may be released for the known sleeping observer's controlled start. Host and project
    locks stay held throughout that handoff, so another ingress publication cannot enter its configuration window.
    """
    with ExitStack() as stack:
        descriptors = []
        try:
            for path in (ingress / "host.lock", ingress / "duora/release.lock", paths.state_dir / ".deploy.lock"):
                _recovery_root_read(path)
                descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
                handle = stack.enter_context(os.fdopen(descriptor, "r"))
                info = os.fstat(descriptor)
                if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_gid != os.getegid()
                        or info.st_mode & 0o022 or info.st_nlink != 1):
                    raise DeployError("Administrative lock is not the protected existing regular file")
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                descriptors.append(descriptor)
            yield RecoveryAdministrativeLease(descriptors[-1])
        except OSError as error:
            raise DeployError("Administrative release window is busy, missing or unsafe; no lock was created") from error

def _swap_arguments_error(args: argparse.Namespace, mode: str | None) -> str | None:
    if mode != "swap":
        used = args.target_postgres or args.reviewed_plan_sha256 or args.resume or args.abort
        return "--target-postgres, --reviewed-plan-sha256, --resume e --abort exigem --swap-postgres-component" if used else None
    if not args.target_postgres: return "--swap-postgres-component exige --target-postgres"
    if sum((args.preflight_only, args.resume, args.abort, args.reviewed_plan_sha256 is not None)) != 1:
        return "use exatamente um entre --preflight-only, --reviewed-plan-sha256, --resume e --abort"
    if args.reviewed_plan_sha256 and re.fullmatch(r"[a-f0-9]{64}", args.reviewed_plan_sha256) is None:
        return "--reviewed-plan-sha256 exige um hash sha256"
    return None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="duora-deploy", description=DESCRIPTION)
    parser.add_argument("--manifest", required=True, help="caminho do manifesto, ou - para ler do stdin")
    parser.add_argument("--compose-file", type=Path)
    parser.add_argument("--host-env", type=Path, default=Path("/etc/duora/duora.env"))
    parser.add_argument("--state-dir", type=Path, default=Path("/var/lib/duora"))
    parser.add_argument("--preflight-only", action="store_true", help="runtime/contrato somente leitura; payload é autenticado no deploy antes do worker")
    parser.add_argument("--inventory-only", action="store_true", help="inventário sanitizado somente leitura; não sela baseline")
    parser.add_argument("--accept-missing-backup-check", action="store_true",
                        help="segue sem verificar backup/WAL: só o primeiro deploy (antes da stanza existir), "
                             "homologação ou smoke. Fora do sudoers do executor de deploy")
    administrative = parser.add_mutually_exclusive_group()
    administrative.add_argument("--enroll-current-v2-recovery-control", action="store_true")
    administrative.add_argument("--recover-current-v2", action="store_true")
    administrative.add_argument("--finish-current-v2-forward", action="store_true")
    administrative.add_argument("--complete-current-v2-recovery-control", action="store_true")
    administrative.add_argument("--swap-postgres-component", action="store_true",
                                help="troca o digest do PostgreSQL; sem --preflight-only exige o hash do plano revisado")
    parser.add_argument("--reviewed-control-sha256", help="hash da projeção administrativa de controle já revisada")
    parser.add_argument("--target-postgres", help="referência por digest do componente alvo")
    parser.add_argument("--reviewed-plan-sha256", help="planSha256 impresso pelo --preflight-only e já revisado")
    parser.add_argument("--resume", action="store_true", help="continua a troca aberta depois de interrupção ou ressela")
    parser.add_argument("--abort", action="store_true", help="desfaz a troca aberta antes do commit")
    args = parser.parse_args(argv)
    mode = ("enroll" if args.enroll_current_v2_recovery_control else "recover" if args.recover_current_v2
            else "finish-forward" if args.finish_current_v2_forward
            else "complete-control" if args.complete_current_v2_recovery_control
            else "swap" if args.swap_postgres_component else None)
    if mode and (args.accept_missing_backup_check or args.inventory_only):
        parser.error("recuperação administrativa não aceita bypass de backup nem inventory-only")
    if args.reviewed_control_sha256 and (mode != "enroll" or args.preflight_only
            or re.fullmatch(r"[a-f0-9]{64}", args.reviewed_control_sha256) is None):
        parser.error("reviewed-control-sha256 exige enrollment explícito com hash exato, fora do modo somente leitura")
    swap_error = _swap_arguments_error(args, mode)
    if swap_error: parser.error(swap_error)

    try:
        if mode: _require_recovery_administrator()
        compose_file = args.compose_file
        if compose_file is None:
            compose_file = (Path(_guard.decode_json(_recovery_root_read(Path("/etc/duora/coolify-deploy.json")))["compose"])
                            if mode else Path("/opt/duora/compose.prod.yml"))
        paths = Paths(compose_file, args.host_env, args.state_dir)
        lock = (recovery_administrative_locks(paths) if mode else
                deploy_lock(args.state_dir, readonly=args.preflight_only or args.inventory_only))
        with lock as lease:
            host = parse_host_config(args.host_env.read_text(encoding="utf-8"))
            release = parse_manifest(read_manifest(args.manifest), image_prefix=host.image_prefix)
            # --resume e --abort não dependem de folga em disco: recuperar não pode travar pela mesma causa da falha.
            if not (args.resume or args.abort): preflight(release, host)
            if mode == "swap":
                action = ("resume" if args.resume else "abort" if args.abort
                          else "plan" if args.preflight_only else "apply")
                result = postgres_swap_entry(release, args.target_postgres, paths, run_in_own_group, action=action,
                    reviewed_plan_sha256=args.reviewed_plan_sha256, host=_production_swap_host(host.image_prefix, paths))
                print(json.dumps(result, sort_keys=True))
                return 0
            if mode:
                result = recovery_administrative_entry(release, paths, run_command, mode=mode,
                    readonly=args.preflight_only, reviewed_sha256=args.reviewed_control_sha256, lease=lease)
                print(json.dumps(result, sort_keys=True))
                return 0
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
