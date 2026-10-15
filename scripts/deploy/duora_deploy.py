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
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Protocol

DESCRIPTION = "Comando restrito de deploy do Duora na VPS (spec 15.5 e 17.3)."
IMAGE_NAMES = ("web", "worker", "edge", "migrator", "postgres")
SUPPORTED_ARCHITECTURES = ("amd64", "arm64")
MANIFEST_SCHEMA_VERSION = 1
MAX_MANIFEST_BYTES = 64 * 1024
GIB = 1024**3
READINESS_INTERVAL_SECONDS = 2
DEFAULT_READINESS_TIMEOUT_SECONDS = 90
# Teto de qualquer comando (o migrador é o mais longo); estourou, o deploy para.
COMMAND_TIMEOUT_SECONDS = 15 * 60

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


@dataclass(frozen=True)
class HostConfig:
    image_prefix: str
    min_free_gib: int
    disk_check_path: Path


@dataclass(frozen=True)
class Paths:
    compose_file: Path
    host_env: Path
    state_dir: Path


class Runner(Protocol):
    def __call__(self, command: list[str]) -> subprocess.CompletedProcess[str]: ...


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
            values[key.strip()] = value.strip()

    prefix = values.get("DUORA_IMAGE_PREFIX", "")
    if not prefix:
        raise DeployError("DUORA_IMAGE_PREFIX ausente na configuração do host")
    try:
        min_free_gib = int(values.get("DUORA_MIN_FREE_GIB", "10"))
    except ValueError as error:
        raise DeployError("DUORA_MIN_FREE_GIB deve ser inteiro") from error
    disk_check_path = Path(values.get("DUORA_DISK_CHECK_PATH", "/var/lib/docker"))
    return HostConfig(prefix, min_free_gib, disk_check_path)


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


# --- Deploy ---------------------------------------------------------------------------------------------------------

def deploy(release: Release, paths: Paths, run: Runner, *,
           readiness_timeout_seconds: int = DEFAULT_READINESS_TIMEOUT_SECONDS,
           sleep: Callable[[float], None] = time.sleep) -> None:
    """Executa a release. Qualquer falha interrompe; a release corrente só muda depois do smoke de readiness."""
    release_env = _write_release_env(release, paths.state_dir)
    compose = _compose_command(paths, release_env)

    _step("baixando imagens por digest")
    _check(run(compose + ["--profile", "migrate", "pull", "--quiet"]), "pull das imagens")
    _require_image_architectures(release, run)

    _step("garantindo o PostgreSQL saudável")
    _check(run(compose + ["up", "--detach", "--wait", "postgres"]), "subida do postgres")

    # Spec 17.3, passo 4: o worker para de adquirir jobs antes da migração; jobs retomam pelo estado persistido.
    _step("pausando o worker")
    _check(run(compose + ["stop", "worker"]), "parada do worker")

    _step("aplicando migrations (job exclusivo)")
    _check(run(compose + ["--profile", "migrate", "run", "--rm", "migrator"]), "migrator")
    _require_migration_head(release, compose, run)

    _step("subindo web, worker e edge da release")
    _check(run(compose + ["up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"]), "subida da aplicação")

    _step("smoke: /health/ready pela rede interna")
    _wait_ready(compose, run, readiness_timeout_seconds, sleep)
    _require_services_running(compose, run)

    _mark_current(release, paths.state_dir)
    _step(f"release {release.release_id} implantada")


def _compose_command(paths: Paths, release_env: Path) -> list[str]:
    return [
        "docker", "compose",
        "--file", str(paths.compose_file),
        "--env-file", str(paths.host_env),
        "--env-file", str(release_env),
    ]


def _write_release_env(release: Release, state_dir: Path) -> Path:
    state_dir.mkdir(parents=True, exist_ok=True)
    lines = [f"DUORA_{name.upper()}_IMAGE={reference}" for name, reference in release.images.items()]
    lines.append(f"DUORA_RELEASE_ID={release.release_id}")
    release_env = state_dir / f"{release.release_id}.env"
    release_env.write_text("\n".join(lines) + "\n", encoding="utf-8")
    (state_dir / f"{release.release_id}.json").write_text(
        json.dumps(release.raw, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return release_env


def _require_image_architectures(release: Release, run: Runner) -> None:
    for name, reference in release.images.items():
        result = _check(run(["docker", "image", "inspect", "--format", "{{.Architecture}}", reference]),
                        f"inspeção da imagem {name}")
        actual = result.stdout.strip()
        if actual != release.architecture:
            raise DeployError(f"imagem {name} com arquitetura {actual}, manifesto pede {release.architecture}")


def _require_migration_head(release: Release, compose: list[str], run: Runner) -> None:
    query = 'select "MigrationId" from ops.__ef_migrations_history order by "MigrationId" desc limit 1'
    result = _check(
        run(compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--quiet", "--tuples-only",
                       "--no-align", "--username", "postgres", "--dbname", "duora", "--command", query]),
        "leitura do histórico de migrations")
    head = result.stdout.strip()
    if head != release.latest_migration:
        raise DeployError(f"banco na migration {head or '(nenhuma)'}, release espera {release.latest_migration}")


def _wait_ready(compose: list[str], run: Runner, timeout_seconds: int, sleep: Callable[[float], None]) -> None:
    # O edge (Caddy, Alpine) tem wget e está na mesma rede do web; a saúde nunca passa pela borda pública.
    probe = compose + ["exec", "--no-TTY", "edge", "wget", "--quiet", "--spider", "--timeout=5",
                       "http://web:8080/health/ready"]
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
    result = _check(run(compose + ["ps", "--all", "--format", "{{.Service}}={{.State}}", *services]),
                    "estado dos serviços")
    states = dict(line.split("=", 1) for line in result.stdout.split() if "=" in line)
    for service in services:
        if states.get(service) != "running":
            raise DeployError(f"serviço {service} em estado {states.get(service, 'ausente')}, esperado running")


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
        detail = (result.stderr or "").strip().splitlines()[-1:] or ["veja a saída acima"]
        raise DeployError(f"{what} falhou (código {result.returncode}): {detail[0]}")
    return result


def _step(message: str) -> None:
    print(f"[duora-deploy] {message}", flush=True)


def run_command(command: list[str]) -> subprocess.CompletedProcess[str]:
    # stdout capturado (o deploy lê arquitetura, migration e estado); stderr, com o progresso e os erros do docker,
    # vai direto ao operador.
    try:
        return subprocess.run(command, text=True, check=False, stdout=subprocess.PIPE,
                              timeout=COMMAND_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired as error:
        raise DeployError(f"comando excedeu {COMMAND_TIMEOUT_SECONDS} s: {' '.join(command[:6])} ...") from error


# --- CLI ------------------------------------------------------------------------------------------------------------

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="duora-deploy", description=DESCRIPTION)
    parser.add_argument("--manifest", required=True, help="caminho do manifesto, ou - para ler do stdin")
    parser.add_argument("--compose-file", type=Path, default=Path("/opt/duora/compose.prod.yml"))
    parser.add_argument("--host-env", type=Path, default=Path("/etc/duora/duora.env"))
    parser.add_argument("--state-dir", type=Path, default=Path("/var/lib/duora/releases"))
    parser.add_argument("--accept-missing-backup-check", action="store_true",
                        help="segue sem verificar backup/WAL (só homologação ou smoke, enquanto o pgBackRest "
                             "não está configurado)")
    args = parser.parse_args(argv)

    try:
        host = parse_host_config(args.host_env.read_text(encoding="utf-8"))
        release = parse_manifest(_read_manifest(args.manifest), image_prefix=host.image_prefix)
        preflight(release, host, accept_missing_backup_check=args.accept_missing_backup_check)
        deploy(release, Paths(args.compose_file, args.host_env, args.state_dir), run_command)
    except DeployError as error:
        print(f"[duora-deploy] ERRO: {error}", file=sys.stderr)
        return 1
    except OSError as error:
        print(f"[duora-deploy] ERRO de sistema: {error}", file=sys.stderr)
        return 1
    return 0


def _read_manifest(source: str) -> str:
    if source == "-":
        data = sys.stdin.buffer.read(MAX_MANIFEST_BYTES + 1)
    else:
        with open(source, "rb") as manifest_file:
            data = manifest_file.read(MAX_MANIFEST_BYTES + 1)
    if len(data) > MAX_MANIFEST_BYTES:
        raise DeployError(f"manifesto maior que {MAX_MANIFEST_BYTES} bytes")
    return data.decode("utf-8")


def preflight(release: Release, host: HostConfig, *, accept_missing_backup_check: bool) -> None:
    host_arch = docker_architecture(platform.machine())
    if host_arch != release.architecture:
        raise DeployError(f"release para {release.architecture}, host é {host_arch}")
    require_free_space(free_bytes=shutil.disk_usage(host.disk_check_path).free, minimum_gib=host.min_free_gib)
    # Spec 17.3, passo 1: estado de backup/WAL desconhecido interrompe. A verificação real chega com o pgBackRest.
    if not accept_missing_backup_check:
        raise DeployError("verificação de backup/WAL ainda não implementada (pgBackRest pendente); "
                          "produção não segue sem ela")
    _step("AVISO: backup/WAL não verificado (--accept-missing-backup-check)")
    if os.geteuid() != 0:
        _step("AVISO: rodando sem root; o usuário precisa de acesso ao Docker")


if __name__ == "__main__":
    sys.exit(main())
