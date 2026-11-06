"""Troca administrativa do digest do componente PostgreSQL e as primitivas que o executor divide com ela.

Sidecar do bundle: duora_deploy.py importa daqui, nunca o contrário, porque o executor também é carregado por
SourceFileLoader com outros nomes e um segundo import dele criaria outras classes. O que a troca usa do executor chega
por SwapHost.core. Manual: docs/operations/deploy.md, "Troca do digest do PostgreSQL".
"""
from __future__ import annotations

import ctypes
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import signal
import stat
import subprocess
import sys
import tempfile
import time
from typing import TYPE_CHECKING, Any, Callable, Protocol

import reconcile_payload as _payload
import runtime_guard as _guard

if TYPE_CHECKING:
    from duora_deploy import Paths, Release

RELEASES_DIR = "releases"
STAGING_DIR = "staging"
# Teto de qualquer comando (o migrador é o mais longo); estourou, o deploy para.
COMMAND_TIMEOUT_SECONDS = 15 * 60
PR_SET_PDEATHSIG = 1  # prctl(2) no Linux


class QueryRunner(Protocol):
    def __call__(self, command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]: ...


class Runner(Protocol):
    def __call__(self, command: list[str], *, capture: bool = False, input_text: str | None = None,
                 timeout_seconds: int | None = None) -> subprocess.CompletedProcess[str]: ...


class DeployError(Exception):
    """Falha que interrompe o deploy com mensagem acionável para o operador."""


def _step(message: str) -> None:
    print(f"[duora-deploy] {message}", flush=True)


def _check(result: subprocess.CompletedProcess[str], what: str) -> subprocess.CompletedProcess[str]:
    if result.returncode != 0:
        # Comando não capturado (inclusive o duora-backup) já escreveu stdout e stderr no terminal do operador.
        # Capturado (inspect, psql, ps), o erro vem do Docker ou do psql; nenhum deles imprime segredo.
        detail = "\n".join((result.stderr or "").strip().splitlines()[-5:]) or "veja a saída acima"
        raise DeployError(f"{what} falhou (código {result.returncode}): {detail}")
    return result


def _query(compose: list[str], run: QueryRunner, sql: str, what: str = "leitura do histórico de migrations") -> str:
    result = _check(
        run(compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--quiet", "--tuples-only",
                       "--no-align", "--username", "postgres", "--dbname", "duora", "--command", sql],
            capture=True),
        what)
    return result.stdout.strip()


def _compose_command(paths: Paths, release_env: Path) -> list[str]:
    return [
        "docker", "compose",
        "--file", str(paths.compose_file),
        "--env-file", str(paths.host_env),
        "--env-file", str(release_env),
    ]


def _release_env_text(release: Release) -> str:
    lines = [f"DUORA_{name.upper()}_IMAGE={reference}" for name, reference in release.images.items()]
    lines.append(f"DUORA_RELEASE_ID={release.release_id}")
    return "\n".join(lines) + "\n"


def _private_json(path: Path) -> dict:
    info = path.lstat()
    if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077
            or info.st_size > 256 * 1024):
        raise DeployError("Runtime baseline must be an owned private regular file")
    return _guard.decode_json(path.read_bytes())


def _recovery_pin_bytes(pin: dict) -> bytes:
    # Same insertion-order-preserving representation as the existing receiver's atomic_json, never sorted keys.
    return (json.dumps(pin, indent=2, allow_nan=False) + "\n").encode()


# Sem transação entre arquivos: o
# journal é write-ahead, cada escrita é temp + fsync + rename e o commit é o rename de current.json. Nenhuma falha
# reverte sozinha; quem decide é o operador, com --resume ou --abort.

# verify-repo espera até 30 min pelo lock de um backup em curso (LOCK_WAIT_SECONDS do duora_backup) e depois trabalha.
SWAP_VERIFY_REPO_TIMEOUT_SECONDS = 45 * 60
SWAP_LOCK_LOST = " Outro processo tomou o .deploy.lock enquanto ele estava solto: espere liberar e repita o comando."
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
class ProcessInfo:
    argv: list[str]
    cwd: str = ""
    environ: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class SwapHost:
    """Fronteiras do host, injetáveis nos testes de protocolo; produção usa duora_deploy._production_swap_host."""
    core: Any  # O módulo do executor que está rodando, seja qual for o nome com que foi carregado.
    image_prefix: str
    read_protected: Callable[[Path], bytes]
    inspect_runtime: Callable[[Release, Path], dict]
    require_ingress_idle: Callable[[str], None]
    executor_identity: Callable[[], dict]
    list_processes: Callable[[], list[ProcessInfo]]
    now: Callable[[], datetime]
    sleep: Callable[[float], None] = time.sleep
    preflight: Callable[[], None] = lambda: None
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
    lease: Any = None

    @property
    def phase(self) -> str:
        return self.record["phase"]

    def environment(self, release: Release) -> Path:
        return self.paths.state_dir / RELEASES_DIR / (release.release_id + ".env")

    def target_environment(self) -> Path:
        # staging/ é rascunho e o conteúdo é função do manifesto, então cada --resume pode regravar sem risco.
        return self.host.core._stage_release_files(self.target, self.paths.state_dir)

    def compose(self, release: Release) -> list[str]:
        environment = self.target_environment() if release is self.target else self.environment(release)
        return _compose_command(self.paths, environment)

    def backup(self, action: str) -> subprocess.CompletedProcess[str]:
        return _swap_backup(action, self.paths, self.run, self.lease)


def require_no_open_swap(paths: Paths) -> None:
    if (paths.state_dir / SWAP_DIRECTORY / "attempt.json").exists() or _swap_fence_path(paths.ingress).is_symlink() \
            or _swap_fence_path(paths.ingress).exists():
        raise DeployError("PostgreSQL swap aberto (journal ou fence): resolva com --resume ou --abort antes de qualquer release")


def _swap_backup(action: str, paths: Paths, run: Runner, lease: Any) -> subprocess.CompletedProcess[str]:
    command = [str(paths.backup_command), action]
    if action != "verify-repo": return _check(run(command), "duora-backup " + action)
    # verify-repo toma o .deploy.lock em outro processo: segurá-lo aqui faria a ação esperar até desistir. Só esse lock
    # é solto; host e projeto seguem presos, então nenhuma release entra na brecha.
    if lease is not None: lease.release_executor()
    try:
        result = _check(run(command, timeout_seconds=SWAP_VERIFY_REPO_TIMEOUT_SECONDS), "duora-backup " + action)
    except DeployError as error:
        # O resultado do repositório vale mais que o lock: uma lacuna de WAL não pode sumir atrás de "busy".
        if _swap_lock_lost(lease): raise DeployError(str(error) + SWAP_LOCK_LOST) from error
        raise
    if _swap_lock_lost(lease): raise DeployError("duora-backup verify-repo passou." + SWAP_LOCK_LOST)
    return result


def _swap_lock_lost(lease: Any) -> bool:
    if lease is None: return False
    try:
        lease.reacquire_executor()
        return False
    except DeployError:
        return True


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
    host.core._parse_images({name: reference if name == "postgres" else image for name, image in current.images.items()},
                  host.image_prefix)
    if reference.split("@")[0] != current.images["postgres"].split("@")[0]:
        raise DeployError("O alvo deve ser do mesmo repositório do componente corrente")
    if reference == current.images["postgres"]: raise DeployError("Alvo igual ao digest corrente; nada a trocar")


def _swap_recorded_pin(current: Release, paths: Paths, host: SwapHost) -> dict:
    """A corrente registrada, o manifesto e o pin precisam dizer a mesma coisa antes de qualquer efeito."""
    read = host.read_protected
    record = _guard.decode_json(read(paths.state_dir / "current.json"))
    host.core._require_exact_keys(record, {"releaseId", "commit", "deployedAt"}, "current.json")
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
    image = json.loads(result.stdout, object_pairs_hook=host.core._recovery_unique_pairs)
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
    candidate = replace(current, images={**current.images, "postgres": reference})
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


def _swap_plan(current: Release, reference: str, paths: Paths, run: Runner, host: SwapHost, lease: Any) -> dict:
    """Passo 0. Só leitura, fora o pull por digest; determinístico para o hash revisado valer no apply."""
    _require_swap_window(host.now())
    require_no_open_swap(paths)
    host.core.require_no_current_v2_recovery_intent(paths)
    if (paths.state_dir / "executor-install-pending.json").exists():
        raise DeployError("Instalação do executor pendente; resolva antes da troca")
    _swap_require_target(current, reference, host)
    pin = _swap_recorded_pin(current, paths, host)
    # Solta o .deploy.lock: tudo o que precisa continuar verdade depois da brecha vem abaixo, com o lock de volta.
    _swap_backup("verify-repo", paths, run, lease)
    host.require_ingress_idle(pin["application_uuid"])
    executor = host.executor_identity()
    environment = paths.state_dir / RELEASES_DIR / (current.release_id + ".env")
    _swap_require_preserved(paths, host.inspect_runtime(current, environment)["profile"])
    for image in (reference, current.images["postgres"]):
        _check(run(["docker", "pull", "--quiet", image]), "pull por digest de " + image)
    compose = _compose_command(paths, environment)
    facts = _swap_require_compatible(current, reference, compose, run, host)
    _swap_require_interpolated(current, reference, paths, run)
    _swap_backup("verify", paths, run, lease)
    read = lambda path: hashlib.sha256(host.read_protected(path)).hexdigest()
    return {"schema": 1, "operation": "postgres-component-swap", "sourceReleaseId": current.release_id,
            "sourceManifestSha256": _guard.digest(current.raw), "sourcePostgres": current.images["postgres"],
            "targetPostgres": reference, "facts": facts, "executor": executor,
            "stateSha256": {"baseline": read(paths.state_dir / _guard.BASELINE_NAME), "pin": read(host.pin_path),
                            "current": read(paths.state_dir / "current.json"), "compose": read(paths.compose_file),
                            "hostEnvironment": read(paths.host_env)}}


class _SwapBaselineDiffers(DeployError):
    """Runtime saudável, mas diferente do selado; não é a mesma coisa que runtime quebrado."""


def _swap_require_preserved(paths: Paths, profile: dict) -> None:
    try:
        _guard.require_preserved(_private_json(paths.state_dir / _guard.BASELINE_NAME), profile)
    except (_guard.GuardError, OSError) as error:
        raise _SwapBaselineDiffers("Runtime difere do baseline selado: rode a ressela do instalador antes de fechar") from error


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
    target = host.core.parse_manifest(json.dumps(raw), image_prefix=host.image_prefix)
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
    store = context.host.core.RecoveryStore(context.paths.ingress / SWAP_FENCE_DIRECTORY)
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
    context.host.core._recovery_require_quiescent(compose, context.run, inventory["profile"]["services"]["postgres"]["id"])
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
    context.host.core._wait_ready(compose, context.run, context.host.core.DEFAULT_READINESS_TIMEOUT_SECONDS, context.host.sleep)
    context.host.core._require_services_running(compose, context.run)


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
    # Depois do rename de current.json não há volta: um restart aqui muda startedAt, e a ressela e o fechamento revalidam.
    if _swap_artifact_states(context)[-1][2] != "after": _swap_require_same_services(context)
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
            _check(context.run(["docker", "rm", "--force", "--volumes", name], capture=True), "remoção da referência " + name)
    for suffix in (".env", ".json"):
        (context.paths.state_dir / STAGING_DIR / (context.target.release_id + suffix)).unlink(missing_ok=True)
    history = context.paths.state_dir / SWAP_HISTORY_DIRECTORY
    with context.host.core.RecoveryStore(history)._directory(create=True): pass
    os.replace(_swap_journal(context.paths), history / context.attempt["swapId"])
    _swap_sync_directory(history)
    _swap_sync_directory(context.paths.state_dir)
    # Fence por último: até aqui o receiver recusa, e um crash neste ponto se resolve com --resume.
    _swap_release_fence(context.paths)
    return {"status": status, "swapId": context.attempt["swapId"]}


def _swap_close(context: _SwapContext, release: Release, actions: tuple[str, ...]) -> dict:
    if (context.paths.state_dir / "executor-install-pending.json").exists():
        raise DeployError("Instalação do executor pendente: resolva a ressela interrompida antes de fechar")
    environment = context.environment(release)
    _swap_require_preserved(context.paths, context.host.inspect_runtime(release, environment)["profile"])
    compose = _compose_command(context.paths, environment)
    context.host.core._wait_ready(compose, context.run, context.host.core.DEFAULT_READINESS_TIMEOUT_SECONDS, context.host.sleep)
    context.host.core._require_services_running(compose, context.run)
    for action in actions:
        context.backup(action)
    # verify-repo solta o .deploy.lock por até 45 min: o que valia antes dele não vale depois.
    _swap_require_preserved(context.paths, context.host.inspect_runtime(release, environment)["profile"])
    _swap_set_phase(context, "closing")
    return _swap_finish(context, "closed")


def _swap_resume(context: _SwapContext) -> dict:
    if context.phase == "closing": return _swap_finish(context, "closed")
    if context.phase == "awaiting-reseal": return _swap_close(context, context.target, ("verify-repo",))
    if context.phase == "aborted-awaiting-reseal":
        return _swap_close(context, context.source, ("verify", "verify-repo"))
    if context.phase == "aborting": raise DeployError("Abort interrompido só continua com --abort")
    return _swap_forward(context)


def _swap_await_source_reseal(context: _SwapContext) -> dict:
    _swap_set_phase(context, "aborted-awaiting-reseal")
    return {"status": "aborted-awaiting-reseal", "releaseId": context.source.release_id,
            "swapId": context.attempt["swapId"]}


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
        return _swap_await_source_reseal(context)
    if reached >= SWAP_FORWARD_PHASES.index("quiesce"): _swap_open_application(context, context.source)
    environment = context.environment(context.source)
    try:
        _swap_require_preserved(context.paths, context.host.inspect_runtime(context.source, environment)["profile"])
    except _SwapBaselineDiffers:
        # Reboot ou OOM no meio: mesmo id, startedAt novo. Só a ressela contra a origem resolve, como no passo 4.
        return _swap_await_source_reseal(context)
    _swap_set_phase(context, "closing")
    return _swap_finish(context, "aborted")


def _swap_require_sole_operator(paths: Paths, run: Runner, host: SwapHost) -> None:
    """Cliente morto não prova job parado: dois containers sobre duora_pgdata é o pior caso, mesmo parados."""
    for process in host.list_processes():
        if _swap_is_project_compose(process, paths):
            raise DeployError("Processo compose do projeto ainda vivo; espere terminar antes de --resume ou --abort")
    for role in ("postgres", "backup"):
        result = _check(run(["docker", "ps", "--all", "--no-trunc", "--quiet", "--filter", "label=com.docker.compose.project=duora",
                             "--filter", "label=com.docker.compose.service=" + role], capture=True), "inventário de " + role)
        if len(result.stdout.split()) > 1:
            raise DeployError("Mais de um container " + role + " do projeto; resolva à mão antes de --resume ou --abort")


def _swap_is_project_compose(process: ProcessInfo, paths: Paths) -> bool:
    argv = process.argv
    if not any(Path(item).name in {"compose", "docker-compose"} for item in argv): return False
    compose_file = str(paths.compose_file)
    if (any(compose_file in item for item in argv) or {"--project-name=duora", "-pduora"} & set(argv)
            or process.environ.get("COMPOSE_PROJECT_NAME") == "duora"
            or compose_file in process.environ.get("COMPOSE_FILE", "")):
        return True
    if any(item in {"-p", "--project-name"} and value == "duora" for item, value in zip(argv, argv[1:])): return True
    return process.cwd == str(paths.compose_file.parent)  # Sem -p e sem caminho absoluto, vale o diretório do processo.


def _swap_process_cwd(entry: Path) -> str:
    try: return os.readlink(entry / "cwd")
    except (FileNotFoundError, ProcessLookupError): return ""


def _swap_process_environment(entry: Path) -> dict[str, str]:
    try: raw = (entry / "environ").read_bytes()
    except (FileNotFoundError, ProcessLookupError): return {}
    pairs = (item.decode("utf-8", "replace").partition("=") for item in raw.split(b"\0"))
    return {key: value for key, separator, value in pairs if separator}


def list_processes(root: Path = Path("/proc")) -> list[ProcessInfo]:
    processes = []
    for entry in root.iterdir():
        if not entry.name.isdigit(): continue
        try:
            raw = (entry / "cmdline").read_bytes()
        except (FileNotFoundError, ProcessLookupError):
            continue  # O processo terminou durante a leitura.
        argv = [item.decode("utf-8", "replace") for item in raw.split(b"\0") if item]
        processes.append(ProcessInfo(argv, _swap_process_cwd(entry), _swap_process_environment(entry)))
    return processes


def _swap_load(source: Release, reference: str, paths: Paths, run: Runner, host: SwapHost, lease: Any) -> _SwapContext | None:
    store = host.core.RecoveryStore(_swap_journal(paths))
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
    target = host.core.parse_manifest(json.dumps(attempt["target"]), image_prefix=host.image_prefix)
    if attempt["source"] != source.raw or target.images["postgres"] != reference:
        raise DeployError("--manifest e --target-postgres precisam ser os do journal aberto")
    if attempt["executor"] != host.executor_identity():
        raise DeployError("Executor diferente do que abriu a troca")
    return _SwapContext(source, target, attempt, digest, record, paths, run, host, lease)


def _swap_require_start_gates(context: _SwapContext) -> None:
    """O banco não foi parado: seguir agora é começar a troca, com as regras de começar."""
    try:
        _require_swap_window(context.host.now())
        context.host.preflight()
    except DeployError as error:
        if context.phase != "quiesce": raise
        reason = re.sub(r"[:.]? *[Nn]ada foi parado\.?", "", str(error))
        raise DeployError(reason + ". Web e worker seguem parados: use --abort para reabrir, ou repita --resume na janela") from error


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
                        reviewed_plan_sha256: str | None, host: SwapHost, lease: Any = None) -> dict:
    """Modo administrativo: main já exigiu root direto e tomou os três locks."""
    try:
        if action == "plan" or action == "apply":
            plan = _swap_plan(source, reference, paths, run, host, lease)
            digest = _guard.digest(plan)
            if action == "plan": return {"status": "plan", "planSha256": digest, "plan": plan}
            if digest != reviewed_plan_sha256: raise DeployError("Plano difere do revisado; nada foi alterado")
            attempt = _swap_make_attempt(source, reference, plan, paths, host)
            raw = (json.dumps(attempt, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
            store = host.core.RecoveryStore(_swap_journal(paths))
            with store._directory(create=True): pass
            _swap_write_atomic(store.directory / "attempt.json", raw, mode=0o600)
            context = _swap_load(source, reference, paths, run, host, lease)
            assert context is not None  # O journal acabou de ser gravado sob os três locks.
            _swap_ensure_fence(context)
            return _swap_forward(context)
        context = _swap_load(source, reference, paths, run, host, lease)
        if context is None: return _swap_finish_orphan_fence(paths)
        _swap_require_sole_operator(paths, run, host)
        _swap_ensure_fence(context)
        if action == "abort": return _swap_abort(context)
        if context.phase in SWAP_FORWARD_PHASES[:SWAP_FORWARD_PHASES.index("swap-runtime")]:
            _swap_require_start_gates(context)
        return _swap_resume(context)
    except (_guard.GuardError, _payload.PayloadError, ValueError, KeyError, TypeError, IndexError) as error:
        raise DeployError("Troca do PostgreSQL recusada: " + str(error)) from error


def _die_with_parent(parent: int) -> None:
    """Roda no filho antes do exec: queda da sessão não pode deixar compose órfão recriando serviço."""
    if sys.platform != "linux": return
    if ctypes.CDLL(None).prctl(PR_SET_PDEATHSIG, signal.SIGKILL, 0, 0, 0) != 0 or os.getppid() != parent:
        os._exit(1)


def _kill_group(process: subprocess.Popen) -> None:
    try: os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError: pass
    process.wait()


def run_in_own_group(command: list[str], *, capture: bool = False, input_text: str | None = None,
                     timeout_seconds: int | None = None) -> subprocess.CompletedProcess[str]:
    """Como run_command, mas com filho em sessão própria, teto que mata o grupo inteiro e PDEATHSIG."""
    environment = {"PATH": "/usr/bin:/bin", "LC_ALL": "C"} if command and command[0] == "docker" else None
    parent = os.getpid()
    process = subprocess.Popen(command, text=True, env=environment, start_new_session=True,
        stdin=subprocess.DEVNULL if input_text is None else subprocess.PIPE,
        stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE if capture else None,
        preexec_fn=lambda: _die_with_parent(parent))
    try:
        stdout, stderr = process.communicate(input_text, timeout=timeout_seconds or COMMAND_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired as error:
        _kill_group(process)
        raise DeployError(f"comando excedeu {timeout_seconds or COMMAND_TIMEOUT_SECONDS} s: {' '.join(command[:6])} ...") from error
    except BaseException:
        _kill_group(process)
        raise
    return subprocess.CompletedProcess(command, process.returncode, stdout, stderr)
