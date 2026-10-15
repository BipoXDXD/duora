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
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterator, Protocol

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
    def __call__(self, command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]: ...


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
    """Executa a release. Qualquer falha interrompe; a release corrente só muda depois do smoke de readiness.

    Tudo que pode recusar a release (manifesto, imagens, schema do banco, elegibilidade de rollback) roda antes de
    parar qualquer serviço. O estado confiável (releases/ e current.json) só muda depois do smoke; até lá a release
    vive em staging/, que nunca é lido para decidir nada.
    """
    _require_release_not_rewritten(release, paths.state_dir)
    release_env = _stage_release_files(release, paths.state_dir)
    compose = _compose_command(paths, release_env)

    _step("baixando imagens por digest")
    _check(run(compose + ["--profile", "migrate", "pull", "--quiet"]), "pull das imagens")
    _require_image_architectures(release, run)

    _step("garantindo o PostgreSQL saudável")
    _check(run(compose + ["up", "--detach", "--wait", "postgres"]), "subida do postgres")

    head = _read_migration_head(compose, run)
    rollback = _is_rollback(release, head, paths.state_dir)

    if rollback:
        # Rollback de aplicação: o banco já está à frente (expand/contract garante que a release anterior aceita o
        # schema). O migrador não roda: ele não desfaz migrations e nunca roda Down (spec 17.4).
        _step(f"rollback para {release.release_id}: banco em {head}, migrador não roda")
    else:
        # Spec 17.3, passo 4: o worker para de adquirir jobs antes da migração; jobs retomam pelo estado persistido.
        _step("pausando o worker")
        _check(run(compose + ["stop", "worker"]), "parada do worker")

        _step("aplicando migrations (job exclusivo)")
        _check(run(compose + ["--profile", "migrate", "run", "--rm", "migrator"]), "migrator")
        # Default privileges não aceitam exceção: o histórico do EF nasce com DML para o app e perde aqui (script
        # versionado na imagem do postgres, docs/data/model.md "Permissões de banco").
        _check(run(compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1",
                              "--username", "postgres", "--dbname", "duora",
                              "--file", "/usr/local/share/duora/reconcile-privileges.sql"]),
               "reconciliação de privilégios")
        migrated_head = _read_migration_head(compose, run)
        if migrated_head != release.latest_migration:
            raise DeployError(f"banco na migration {migrated_head or '(nenhuma)'} depois do migrador, "
                              f"release espera {release.latest_migration}")

    _step("subindo web, worker e edge da release")
    _check(run(compose + ["up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"]), "subida da aplicação")

    _step("smoke: /health/ready pela rede interna")
    _wait_ready(compose, run, readiness_timeout_seconds, sleep)
    _require_services_running(compose, run)

    _record_release(release, paths.state_dir)
    _mark_current(release, paths.state_dir)
    _step(f"release {release.release_id} implantada")


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
    if head <= release.latest_migration:
        return False
    current = _recorded_manifest(state_dir, _current_release_id(state_dir))
    if current is None or current.get("previousReleaseId") != release.release_id:
        current_id = current.get("releaseId") if current else None
        raise DeployError(
            f"banco na migration {head}, mais nova que schema.max {release.latest_migration} da release "
            f"{release.release_id}; ela não é a release anterior elegível da corrente ({current_id or 'nenhuma'}). "
            "Nada foi parado.")
    if _recorded_manifest(state_dir, release.release_id) is None:
        raise DeployError(f"release {release.release_id} nunca foi implantada aqui: não é rollback elegível. "
                          "Nada foi parado.")
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


def _read_migration_head(compose: list[str], run: Runner) -> str:
    """Última migration aplicada, ou "" com o banco ainda sem histórico (primeiro deploy)."""
    exists = _query(compose, run, "select to_regclass('ops.__ef_migrations_history') is not null")
    if exists != "t":
        return ""
    return _query(compose, run, 'select coalesce(max("MigrationId"), \'\') from ops.__ef_migrations_history')


def _query(compose: list[str], run: Runner, sql: str) -> str:
    result = _check(
        run(compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--quiet", "--tuples-only",
                       "--no-align", "--username", "postgres", "--dbname", "duora", "--command", sql],
            capture=True),
        "leitura do histórico de migrations")
    return result.stdout.strip()


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
        # Comando não capturado já escreveu stdout e stderr no terminal do operador. Capturado (inspect, psql,
        # ps), o erro vem do Docker ou do psql; nenhum deles imprime segredo.
        detail = "\n".join((result.stderr or "").strip().splitlines()[-5:]) or "veja a saída acima"
        raise DeployError(f"{what} falhou (código {result.returncode}): {detail}")
    return result


def _step(message: str) -> None:
    print(f"[duora-deploy] {message}", flush=True)


def run_command(command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
    # Só se captura o que o deploy lê (arquitetura, migration, estado). O resto, inclusive a saída do migrador,
    # vai direto ao operador: "run --rm" apaga o container e os logs dele junto.
    try:
        return subprocess.run(command, text=True, check=False, capture_output=capture,
                              timeout=COMMAND_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired as error:
        raise DeployError(f"comando excedeu {COMMAND_TIMEOUT_SECONDS} s: {' '.join(command[:6])} ...") from error


# --- CLI ------------------------------------------------------------------------------------------------------------

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="duora-deploy", description=DESCRIPTION)
    parser.add_argument("--manifest", required=True, help="caminho do manifesto, ou - para ler do stdin")
    parser.add_argument("--compose-file", type=Path, default=Path("/opt/duora/compose.prod.yml"))
    parser.add_argument("--host-env", type=Path, default=Path("/etc/duora/duora.env"))
    parser.add_argument("--state-dir", type=Path, default=Path("/var/lib/duora"))
    parser.add_argument("--accept-missing-backup-check", action="store_true",
                        help="segue sem verificar backup/WAL (só homologação ou smoke, enquanto o pgBackRest "
                             "não está configurado)")
    args = parser.parse_args(argv)

    try:
        with deploy_lock(args.state_dir):
            host = parse_host_config(args.host_env.read_text(encoding="utf-8"))
            release = parse_manifest(read_manifest(args.manifest), image_prefix=host.image_prefix)
            preflight(release, host, accept_missing_backup_check=args.accept_missing_backup_check)
            deploy(release, Paths(args.compose_file, args.host_env, args.state_dir), run_command)
    except DeployError as error:
        print(f"[duora-deploy] ERRO: {error}", file=sys.stderr)
        return 1
    except OSError as error:
        print(f"[duora-deploy] ERRO de sistema: {error}", file=sys.stderr)
        return 1
    return 0


@contextmanager
def deploy_lock(state_dir: Path) -> Iterator[None]:
    """Um deploy por vez (spec 17.3, passo 5): dois migradores ou dois "up" concorrentes deixam estado misto.

    flock é solto pelo kernel quando o processo morre, então um deploy interrompido não trava os próximos.
    """
    state_dir.mkdir(parents=True, exist_ok=True)
    with open(state_dir / ".deploy.lock", "w", encoding="utf-8") as lock_file:
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
