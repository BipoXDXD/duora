"""Projeto sombra `duora-c2` da issue #86: a mesma release e os mesmos limites do compose.prod.yml, com dados,
segredos, redes, volumes e repositório de backup **exclusivamente sintéticos**, na VPS (docs/operations/capacity-c2.md).

Roda na VPS como root, a partir de uma cópia do repositório no commit revisado. Nunca toca o projeto `duora`: o nome
do projeto é validado, as redes usam sub-redes próprias (as fixas da produção colidiriam) e o repositório pgBackRest
é o MinIO do drill (scripts/backup/compose.drill.yml), não o B2. A borda só publica no loopback; a carga chega pelo
túnel SSH. Imagens: só as chaves DUORA_*_IMAGE do env da release corrente são lidas; nenhum segredo de produção.

    python3 c2_shadow.py prepare --release-env /var/lib/duora/releases/<id>.env
    python3 c2_shadow.py up
    python3 c2_shadow.py media-fixture --mib 2048
    python3 c2_shadow.py restore-rehearsal [--no-final-full]
    systemd-run --unit=duora-c2-swap --collect --service-type=exec --property=KillMode=mixed \\
        --property=TimeoutStopSec=900 python3 c2_shadow.py swap-rehearsal --target <digest revisado> --fill-mib 256
    systemd-run --unit=duora-c2-swap-abort --collect --service-type=exec --property=KillMode=mixed \\
        --property=TimeoutStopSec=900 python3 c2_shadow.py swap-abort
    python3 c2_shadow.py down
"""
from __future__ import annotations

import argparse
import base64
import fcntl
import hashlib
import json
import os
import secrets
import shutil
import signal
import subprocess
import sys
import time
import uuid
from collections.abc import Callable, Iterator, Sequence
from contextlib import contextmanager, nullcontext
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

import c2_probe
import duora_secrets
from postgres_swap import SWAP_COMPATIBILITY_SQL, SWAP_COMPATIBLE_FIELDS, SWAP_PHOTO_SQL

REPO_ROOT = Path(__file__).resolve().parents[2]
COMPOSE_FILE = REPO_ROOT / "infra" / "compose" / "compose.prod.yml"
DRILL_FILE = REPO_ROOT / "scripts" / "backup" / "compose.drill.yml"
OVERLAY_FILE = REPO_ROOT / "scripts" / "deploy" / "c2" / "compose.c2.yml"
BACKUP_SCRIPT = REPO_ROOT / "scripts" / "deploy" / "duora_backup.py"
HBA_SOURCE = REPO_ROOT / "infra" / "images" / "postgres" / "pg_hba.duora.conf"
REPO_CONF_SOURCE = REPO_ROOT / "infra" / "images" / "postgres" / "pgbackrest-repo.conf"
PROJECT = "duora-c2"
DEFAULT_ROOT = Path("/var/lib/duora-c2")
DOMAIN = "c2.duora.test"
HTTP_BIND = "127.0.0.1:18080"
HTTPS_BIND = "127.0.0.1:18443"
PRODUCTION_BACKUP_IP = "172.30.21.10"
SHADOW_BACKUP_IP = "10.231.21.10"
SHADOW_SUBNETS = ("10.231.10.0/24", "10.231.20.0/24", "10.231.21.0/24")
BUCKET = "duora-c2"
S3_PORT = 9000
BUCKET_LOCK = ("COMPLIANCE", "1d")
IMAGE_KEYS = ("DUORA_POSTGRES_IMAGE", "DUORA_WEB_IMAGE", "DUORA_WORKER_IMAGE", "DUORA_EDGE_IMAGE",
              "DUORA_MIGRATOR_IMAGE")
APP_UID = 1654
# O Compose de produção monta estes arquivos mesmo com a selfie desligada (#93); o sombra roda sem selfie e sem
# credencial real da AWS, então recebe valores sintéticos inertes.
SYNTHETIC_SELFIE_SECRETS = ("selfie-web-aws-access-key-id", "selfie-web-aws-secret-access-key",
                            "selfie-worker-aws-access-key-id", "selfie-worker-aws-secret-access-key")
POSTGRES_UID = 999
ACCOUNT_DOMAIN = "c2.invalid"
CADDY_ROOT_CA = "/data/caddy/pki/authorities/local/root.crt"
# ASP.NET Core Identity v3 (padrão do .NET 8+): PBKDF2-HMAC-SHA512, 100.000 iterações, sal 16 B, chave 32 B.
IDENTITY_PRF_SHA512 = 2
IDENTITY_ITERATIONS = 100_000
MARKER_SECONDS = 120
OUTAGE_AFTER_SECONDS = 90
WAIT_SECONDS = 600
COMMAND_TIMEOUT_SECONDS = 3600
# Mesmo ambiente de postgres_swap.py: variável do root (DUORA_*_DIR, DOCKER_HOST, DOCKER_CONTEXT, COMPOSE_*) venceria o
# --env-file e poderia apontar o Compose para a produção.
RUNNER_ENVIRONMENT = {"PATH": "/usr/bin:/bin", "LC_ALL": "C"}


class ShadowError(RuntimeError):
    pass


Runner = Callable[..., str]


def _say(text: str, *, stream: str = "stdout") -> None:
    """A sessão SSH pode cair no meio do rollback (EIO): perder o log não pode interromper a volta à origem."""
    try:
        print(text, file=getattr(sys, stream), flush=True)
    except OSError:
        pass


def default_runner(argv: Sequence[str], *, stdin: str | None = None, check: bool = True,
                   timeout: int = COMMAND_TIMEOUT_SECONDS) -> str:
    _say("[c2-shadow] " + " ".join(argv[:8]) + (" ..." if len(argv) > 8 else ""))
    result = subprocess.run(list(argv), input=stdin, capture_output=True, text=True, timeout=timeout, check=False,
                            env=RUNNER_ENVIRONMENT)
    if check and result.returncode != 0:
        raise ShadowError(f"{' '.join(argv[:4])} terminou com código {result.returncode}: {result.stderr[-2000:]}")
    return result.stdout


@dataclass(frozen=True)
class Layout:
    root: Path

    @property
    def secrets(self) -> Path:
        return self.root / "secrets"

    @property
    def certs(self) -> Path:
        return self.root / "certs"

    @property
    def conf(self) -> Path:
        return self.root / "conf"

    @property
    def media(self) -> Path:
        return self.root / "media"

    @property
    def state(self) -> Path:
        return self.root / "state"

    @property
    def env_file(self) -> Path:
        return self.root / "c2.env"


def compose_command(layout: Layout, project: str = PROJECT) -> list[str]:
    c2_probe.validate_project(project)
    return ["docker", "compose", "--project-name", project, "--file", str(COMPOSE_FILE), "--file", str(DRILL_FILE),
            "--file", str(OVERLAY_FILE), "--env-file", str(layout.env_file)]


def read_release_images(text: str) -> dict[str, str]:
    """Só as cinco referências de imagem por digest; qualquer outra chave do env da release é ignorada."""
    images = {}
    for line in text.splitlines():
        key, separator, value = line.partition("=")
        if separator and key.strip() in IMAGE_KEYS:
            images[key.strip()] = value.strip().strip('"').strip("'")
    missing = [key for key in IMAGE_KEYS if key not in images]
    if missing:
        raise ShadowError("env da release sem " + ", ".join(missing))
    for key, value in images.items():
        if "@sha256:" not in value:
            raise ShadowError(f"{key} sem digest (use a release corrente, não uma tag)")
    return images


def shadow_conf(source: str, *, expected_count: int = 1) -> str:
    """A configuração da imagem com o IP do backup trocado pela sub-rede sombra; nada mais muda."""
    count = source.count(PRODUCTION_BACKUP_IP)
    if count != expected_count:
        raise ShadowError(f"esperado {expected_count} ocorrência(s) de {PRODUCTION_BACKUP_IP}, achei {count}")
    return source.replace(PRODUCTION_BACKUP_IP, SHADOW_BACKUP_IP)


def render_env(layout: Layout, images: dict[str, str], s3_key: str, s3_secret: str) -> str:
    values = {
        **images,
        "DUORA_SECRETS_DIR": str(layout.secrets),
        "DUORA_DOMAIN": DOMAIN,
        "DUORA_MEDIA_DIR": str(layout.media),
        "DUORA_MEDIA_REVIEW": "",
        "DUORA_TLS": "internal",
        "DUORA_HTTP_PORT": HTTP_BIND,
        "DUORA_HTTPS_PORT": HTTPS_BIND,
        "DRILL_S3_KEY": s3_key,
        "DRILL_S3_SECRET": s3_secret,
        "DRILL_CERTS_DIR": str(layout.certs),
        "C2_CONF_DIR": str(layout.conf),
    }
    return "".join(f"{key}={value}\n" for key, value in values.items())


def repository_conf(s3_key: str, s3_secret: str) -> str:
    return "\n".join(["[global]", "repo1-s3-endpoint=s3", f"repo1-s3-bucket={BUCKET}", "repo1-s3-region=us-east-1",
                      f"repo1-s3-key={s3_key}", f"repo1-s3-key-secret={s3_secret}", "repo1-s3-uri-style=path",
                      f"repo1-storage-port={S3_PORT}", "repo1-storage-ca-file=/etc/pgbackrest/drill-ca.crt"]) + "\n"


def prepare(layout: Layout, release_env: Path, run: Runner = default_runner) -> None:
    if layout.root.exists() and any(layout.root.iterdir()):
        raise ShadowError(f"{layout.root} já existe: rode `down` antes de preparar de novo")
    images = read_release_images(release_env.read_text(encoding="utf-8"))
    for directory in (layout.root, layout.certs, layout.conf):
        directory.mkdir(parents=True, exist_ok=True)
        directory.chmod(0o755)
    layout.state.mkdir(mode=0o700, exist_ok=True)
    s3_key = "c2" + secrets.token_hex(8)
    s3_secret = duora_secrets.generate_password()
    # Mesmo gerador e mesmos donos da VPS (999 para banco/backup, 1654 para web/worker/migrador), valores novos.
    duora_secrets.init_secrets(layout.secrets, change_owner=True)
    duora_secrets.init_backup(layout.secrets, change_owner=True)
    synthetic = {"pgbackrest_repo.conf": repository_conf(s3_key, s3_secret),
                 "pgbackrest_cipher.conf": f"[global]\nrepo1-cipher-pass={duora_secrets.generate_password()}\n"}
    for name, content in synthetic.items():
        path = layout.secrets / name
        path.write_text(content, encoding="utf-8")
        os.chown(path, POSTGRES_UID, POSTGRES_UID)
        path.chmod(0o400)
    for name in SYNTHETIC_SELFIE_SECRETS:
        path = layout.secrets / name
        path.write_text("c2-synthetic-" + secrets.token_hex(8), encoding="utf-8")
        os.chown(path, APP_UID, APP_UID)
        path.chmod(0o400)
    (layout.conf / "pg_hba.conf").write_text(shadow_conf(HBA_SOURCE.read_text(encoding="utf-8")), encoding="utf-8")
    (layout.conf / "pgbackrest-repo.conf").write_text(shadow_conf(REPO_CONF_SOURCE.read_text(encoding="utf-8")),
                                                      encoding="utf-8")
    for path in layout.conf.iterdir():
        path.chmod(0o644)
    run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "2", "-subj", "/CN=s3",
         "-addext", "subjectAltName=DNS:s3,DNS:localhost", "-keyout", str(layout.certs / "private.key"),
         "-out", str(layout.certs / "public.crt")])
    for name in ("private.key", "public.crt"):
        (layout.certs / name).chmod(0o644)
    layout.media.mkdir(mode=0o755, exist_ok=True)
    for area in ("quarantine", "derivatives"):
        directory = layout.media / area
        directory.mkdir(mode=0o700, exist_ok=True)
        marker = directory / ".duora-media-area"
        marker.write_text("Projeto sombra duora-c2 (issue #86): prova de montagem sintética.\n", encoding="utf-8")
        marker.chmod(0o400)
        for path in (directory, marker):
            os.chown(path, APP_UID, APP_UID)
    layout.env_file.write_text(render_env(layout, images, s3_key, s3_secret), encoding="utf-8")
    layout.env_file.chmod(0o600)


def preflight(run: Runner = default_runner) -> None:
    """Recusa subir se alguma rede Docker existente já usa as sub-redes sombra."""
    ids = run(["docker", "network", "ls", "--quiet"]).split()
    if not ids:
        return
    used = run(["docker", "network", "inspect", "--format", "{{range .IPAM.Config}}{{.Subnet}} {{end}}", *ids])
    clash = sorted(set(used.split()) & set(SHADOW_SUBNETS))
    if clash:
        raise ShadowError("sub-redes sombra já em uso: " + ", ".join(clash))


def up(layout: Layout, accounts: int, run: Runner = default_runner, sleep: Callable[[float], None] = time.sleep) -> None:
    preflight(run)
    compose = compose_command(layout)
    run([*compose, "up", "--detach", "--wait", "s3"])
    mode, retention = BUCKET_LOCK
    _mc(run, compose, "mb", "--with-lock", f"c2/{BUCKET}", sleep=sleep)
    _mc(run, compose, "retention", "set", "--default", mode, retention, f"c2/{BUCKET}", sleep=sleep)
    run([*compose, "up", "--detach", "--wait", "--no-deps", "postgres", "backup"])
    backup(layout, "stanza-create", run)
    run([*compose, "--profile", "migrate", "run", "--rm", "migrator"])
    seed(layout, accounts, run)
    run([*compose, "up", "--detach", "--wait", "web", "worker", "edge"])
    ca = run([*compose, "exec", "--no-TTY", "edge", "cat", CADDY_ROOT_CA])
    (layout.state / "c2-root.crt").write_text(ca, encoding="utf-8")


def _mc(run: Runner, compose: list[str], *args: str, sleep: Callable[[float], None]) -> None:
    script = 'MC_HOST_c2="https://$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD@localhost:9000" exec mc "$@"'
    for _ in range(30):
        try:
            run([*compose, "exec", "--no-TTY", "s3", "sh", "-c", script, "mc", *args])
            return
        except ShadowError:
            sleep(2)
    raise ShadowError("mc no MinIO sombra falhou 30 vezes")


def backup(layout: Layout, action: str, run: Runner = default_runner) -> None:
    run([sys.executable, str(BACKUP_SCRIPT), action, "--compose-project", PROJECT, "--state-dir", str(layout.state),
         "--skip-egress-check"])


# ---------------------------------------------------------------- dados sintéticos


def identity_password_hash(password: str, salt: bytes) -> str:
    if len(salt) != 16:
        raise ValueError("sal de 16 bytes")
    subkey = hashlib.pbkdf2_hmac("sha512", password.encode("utf-8"), salt, IDENTITY_ITERATIONS, dklen=32)
    header = bytes([1]) + IDENTITY_PRF_SHA512.to_bytes(4, "big") + IDENTITY_ITERATIONS.to_bytes(4, "big") \
        + len(salt).to_bytes(4, "big")
    return base64.b64encode(header + salt + subkey).decode("ascii")


def _literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def seed_sql(accounts: int, now: datetime, password_hash: str, *, events: int, interval_minutes: int,
             first_event_minutes: int = 15, capacity: int = 100) -> tuple[str, list[str]]:
    """SQL transacional com contas elegíveis, preferências, conteúdo e eventos futuros com jobs do worker.

    Mesmo formato do BrowserScenarioSeed dos testes de integração; nada além do domínio @c2.invalid.
    """
    if accounts < 1 or events < 1:
        raise ValueError("ao menos uma conta e um evento")
    emails = [f"p{index:04d}@{ACCOUNT_DOMAIN}" for index in range(1, accounts + 1)]
    lines = ["\\set ON_ERROR_STOP on", "begin;",
             "create schema if not exists c2;",
             "create table if not exists c2.markers(seq bigserial primary key, "
             "at timestamptz not null default clock_timestamp());"]
    stamp = _literal(now.isoformat())
    for email in emails:
        user = str(uuid.uuid4())
        lines.append(
            "insert into identity.users(id,status,created_at,user_name,normalized_user_name,email,normalized_email,"
            "email_confirmed,password_hash,security_stamp,concurrency_stamp,phone_number_confirmed,two_factor_enabled,"
            f"lockout_enabled,access_failed_count) values('{user}','Active',{stamp},{_literal(email)},"
            f"upper({_literal(email)}),{_literal(email)},upper({_literal(email)}),true,{_literal(password_hash)},"
            f"'{uuid.uuid4()}','{uuid.uuid4()}',false,false,true,0);")
        lines.append(f"insert into identity.user_roles(user_id,role_id) select '{user}',id from identity.roles "
                     "where normalized_name='PARTICIPANT';")
        lines.append("insert into identity.selfie_verifications(id,user_id,state,policy_version,requested_at,"
                     f"expires_at,completed_at,version) values('{uuid.uuid4()}','{user}','Passed','c2-synthetic',"
                     f"{stamp},{stamp},{stamp},1);")
        lines.append("insert into profiles.preferences(user_id,allow_different_display_locales,version,updated_at) "
                     f"values('{user}',true,1,{stamp});")
        lines.append(f"insert into profiles.communication_languages(user_id,language_code) values('{user}','en');")
    content = str(uuid.uuid4())
    english = json.dumps({"version": 1, "title": "Shared choices", "description": "Explore choices together.",
                          "cards": [{"cardId": "morning", "prompt": "What makes a pleasant morning?",
                                     "options": [{"optionId": "tea", "label": "Tea"},
                                                 {"optionId": "walk", "label": "A walk"}]}]})
    portuguese = json.dumps({"version": 1, "title": "Escolhas em comum", "description": "Explore escolhas em dupla.",
                             "cards": [{"cardId": "morning", "prompt": "O que torna a manhã agradável?",
                                        "options": [{"optionId": "tea", "label": "Chá"},
                                                    {"optionId": "walk", "label": "Uma caminhada"}]}]},
                            ensure_ascii=False)
    lines.append("insert into experiences.experience_versions(id,content_version,rules_version,schema_version,"
                 f"editorial_state,content_en,content_pt,published_at) values('{content}',1,1,1,'Published',"
                 f"{_literal(english)}::jsonb,{_literal(portuguese)}::jsonb,{stamp});")
    for index in range(events):
        event, round_id = str(uuid.uuid4()), str(uuid.uuid4())
        starts = now + timedelta(minutes=first_event_minutes + index * interval_minutes)
        closes = starts + timedelta(minutes=12)
        lines.append(f"insert into events.events(id,title,state,starts_at,capacity,experience_version_id) values("
                     f"'{event}',{_literal(f'C2 synthetic event {index + 1}')},'Published',"
                     f"{_literal(starts.isoformat())},{capacity},'{content}');")
        lines.append("insert into events.rounds(id,event_id,number,state,starts_at,deadline_at,seed) values("
                     f"'{round_id}','{event}',1,'Scheduled',{_literal(starts.isoformat())},"
                     f"{_literal(closes.isoformat())},{86 + index});")
        for kind, due in (("build-round", starts), ("close-round", closes)):
            lines.append(f"insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values('{uuid.uuid4()}',"
                         f"'{kind}','{round_id}',{_literal(due.isoformat())},{_literal(due.isoformat())});")
    lines.append("commit;")
    return "\n".join(lines) + "\n", emails


def seed(layout: Layout, accounts: int, run: Runner = default_runner, *, events: int = 48,
         interval_minutes: int = 15) -> None:
    password = secrets.token_urlsafe(18)
    sql, emails = seed_sql(accounts, datetime.now(UTC), identity_password_hash(password, secrets.token_bytes(16)),
                           events=events, interval_minutes=interval_minutes)
    run([*compose_command(layout), "exec", "--no-TTY", "postgres", "psql", "--username", "postgres", "--dbname",
         "duora", "--quiet"], stdin=sql)
    accounts_file = layout.state / "accounts.json"
    accounts_file.write_text(json.dumps({"schema": "duora.c2-accounts/v1", "domain": ACCOUNT_DOMAIN,
                                         "accounts": [{"email": email, "password": password} for email in emails]},
                                        indent=1) + "\n", encoding="utf-8")
    accounts_file.chmod(0o600)


def media_fixture(layout: Layout, mib: int, run: Runner = default_runner) -> None:
    """Arquivos aleatórios (incompressíveis) e repositório restic local para a fase de restic, fora do B2."""
    source = layout.root / "media-fixture"
    source.mkdir(mode=0o700, exist_ok=False)
    chunk = 4 * 1024 * 1024
    for index in range(max(1, mib // 4)):
        (source / f"synthetic-{index:05d}.bin").write_bytes(os.urandom(chunk))
    password = layout.state / "restic-password"
    password.write_text(secrets.token_urlsafe(24) + "\n", encoding="utf-8")
    password.chmod(0o600)
    run(["restic", "--repo", str(layout.root / "restic"), "--password-file", str(password), "init"])


def restic_backup_command(layout: Layout) -> str:
    """Comando da fase de restic: slice transitória com os limites da duora-media.slice, sem dividi-la com a
    produção (um restic da produção na mesma hora não pode ser sufocado pelo ensaio)."""
    return (f"sudo systemd-run --quiet --wait --collect --unit=duora-c2-restic --slice=duora-c2-media.slice "
            f"--property=MemoryMax=1024M --property=MemoryHigh=768M --property=MemorySwapMax=0 "
            f"--property=CPUQuota=50% restic --repo {layout.root}/restic --password-file "
            f"{layout.state}/restic-password backup --one-file-system {layout.root}/media-fixture")


# ---------------------------------------------------------------- restore do ensaio (RPO/RTO observados)


def _sql(run: Runner, compose: list[str], statement: str) -> str:
    return run([*compose, "exec", "--no-TTY", "postgres", "psql", "--username", "postgres", "--dbname", "duora",
                "--quiet", "--tuples-only", "--no-align", "--command", statement]).strip()


def restore_rehearsal(layout: Layout, run: Runner = default_runner, clock: Callable[[], float] = time.time,
                      sleep: Callable[[float], None] = time.sleep, *, final_full: bool = True) -> dict[str, float]:
    """Escreve marcadores por 90 s, mata o postgres (perda do host), apaga o volume, restaura até o fim do WAL do
    repositório e mede: RPO = último marcador confirmado - último restaurado; RTO = perda até web/worker de pé.
    `final_full` refaz a cadeia de backup depois do restore; na janela de 3 h o sombra é derrubado em seguida e o
    FULL novo só gastaria tempo, sem entrar no RPO/RTO."""
    compose = compose_command(layout)
    last_ack = 0.0
    started = clock()
    while clock() - started < OUTAGE_AFTER_SECONDS:
        last_ack = float(_sql(run, compose, "insert into c2.markers default values "
                                            "returning extract(epoch from at)"))
        sleep(1)
    outage_at = clock()
    run(["docker", "kill", f"{PROJECT}-postgres-1"])
    run([*compose, "stop", "web", "worker", "edge"])
    run([*compose, "rm", "--stop", "--force", "postgres", "backup"])
    run(["docker", "volume", "rm", f"{PROJECT}_pgdata"])
    run([*compose, "--profile", "restore", "run", "--rm", "--no-deps", "restore", "restore"])
    run([*compose, "up", "--detach", "--wait", "--no-deps", "backup"])
    run([*compose, "up", "--detach", "--wait", "--no-deps", "postgres"])
    deadline = clock() + WAIT_SECONDS
    while _sql(run, compose, "select pg_is_in_recovery()") != "f":
        if clock() > deadline:
            raise ShadowError("banco restaurado não saiu de recovery")
        sleep(2)
    restored = float(_sql(run, compose, "select coalesce(extract(epoch from max(at)), 0) from c2.markers"))
    run([*compose, "up", "--detach", "--wait", "web", "worker", "edge"])
    ready_at = clock()
    result = {"last_ack_at": last_ack, "restored_max_at": restored, "outage_at": outage_at, "ready_at": ready_at,
              "rpo_seconds": round(last_ack - restored, 1), "rto_seconds": round(ready_at - outage_at, 1)}
    (layout.state / "restore-rehearsal.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    if final_full:
        backup(layout, "full", run)
    return result


def down(layout: Layout, run: Runner = default_runner) -> None:
    run([*compose_command(layout), "--profile", "restore", "--profile", "migrate", "down", "--volumes",
         "--remove-orphans"])
    if layout.root.resolve() == DEFAULT_ROOT or layout.root.name.startswith("duora-c2"):
        shutil.rmtree(layout.root)
    else:
        raise ShadowError(f"não removo {layout.root}: fora do padrão duora-c2")


# ---------------------------------------------------------------- ensaio da troca de digest do PostgreSQL
# Repete no sombra só os passos 3 a 5 da troca (docs/operations/deploy.md) para medir a queda com volume de dados e o
# pull a partir da VPS. Não usa o executor: ele fixa o projeto `duora`, e aqui nada pode alcançar a produção.

SWAP_SOURCE_IMAGE = "ghcr.io/bipoxdxd/duora-postgres@sha256:d32f8c4408e804bf8b57583c6fdfb8935380e6326bcc7ccb7397b1f386ada06c"
# Digest amd64 da imagem construída na run 42073873377 do GitHub Actions.
SWAP_TARGET_IMAGE = "ghcr.io/bipoxdxd/duora-postgres@sha256:b5103bfd8ac7832915eed8170ffd0bcb94d9bca834cbd659114fa25606f0434e"
# A produção tinha 117 MB em 06/11: o recomendado é 256 MiB, e o teto dá uma folga grande sem pressionar a VPS.
SWAP_MAX_FILL_MIB = 2048
SWAP_FILL_BATCH_MIB = 64
# Cada linha são 100 md5 (3,2 KiB) com storage external, sem compressão. O TOAST ocupa cerca de 30% a mais: 16000 linhas
# dão perto de 64 MiB no disco, medido em PostgreSQL 18.
SWAP_FILL_ROWS = 16000
SWAP_FILL_MD5_PER_ROW = 100
SWAP_FILL_CPUS = "0.5"
SWAP_FILL_CPU_SHARES = 256
SWAP_FILL_PAUSE_SECONDS = 1
# Tabela, WAL no pg_wal e a cópia cifrada no MinIO, mais a imagem alvo e folga.
SWAP_DISK_FACTOR = 3
SWAP_DISK_MARGIN_MIB = 4096
SWAP_ARCHIVE_POLL_SECONDS = 2
# Guardas MEM_LOW, DISK e OTHER_APPS_PSI de docs/operations/capacity-c2.md; o ensaio começa com folga sobre o piso.
SWAP_MIN_AVAILABLE_MIB = 1024
SWAP_MEMORY_FLOOR_MIB = 512
SWAP_MAX_DISK_PERCENT = 85
# PSI: memória e IO têm teto absoluto, porque em repouso a VPS fica em 0,00 e qualquer valor alto é disputa real. A CPU
# não: em 06/11, com load 1,0 em 2 vCPU e processos a menos de 3%, o `some` estava em 26/36/38 (avg10/60/300), e um teto
# fixo de 40 pararia o ensaio sem motivo. O `full` não ajuda: no nível do sistema o kernel o publica sempre zerado. Por
# isso a CPU compara com a linha de base do próprio início, o maior dos três avg, mais uma margem.
SWAP_MAX_STALL_PRESSURE = 40.0
SWAP_MAX_CPU_BASELINE = 60.0
SWAP_CPU_PRESSURE_MARGIN = 25.0
SWAP_LOOPBACK = ("127.0.0.1", "::1")
SWAP_CONTAINERS = (f"{PROJECT}-postgres-1", f"{PROJECT}-backup-1")
SWAP_FAILURES = (ShadowError, c2_probe.CommandRefused, subprocess.TimeoutExpired, OSError, ValueError, KeyError)


def _memory_available_mib(meminfo: Path = Path("/proc/meminfo")) -> int:
    for line in meminfo.read_text(encoding="utf-8").splitlines():
        if line.startswith("MemAvailable:"):
            return int(line.split()[1]) // 1024
    raise ShadowError(f"MemAvailable ausente em {meminfo}")


def _some_averages(path: Path) -> dict[str, float]:
    fields = path.read_text(encoding="utf-8").splitlines()[0].split()[1:]
    return {name: float(value) for name, value in (field.split("=") for field in fields) if name != "total"}


def _disk_mib(path: Path) -> tuple[int, int]:
    usage = shutil.disk_usage(path)
    return usage.total // (1024 * 1024), usage.free // (1024 * 1024)


def _device(path: Path) -> int:
    return path.stat().st_dev


@dataclass(frozen=True)
class HostPressure:
    """`some`, em %: `cpu`, `memory` e `io` são o avg10; `cpu_baseline`, o maior entre avg10, avg60 e avg300 da CPU."""

    cpu: float
    cpu_baseline: float
    memory: float
    io: float


def _host_pressure(directory: Path = Path("/proc/pressure")) -> HostPressure:
    cpu = _some_averages(directory / "cpu")
    return HostPressure(cpu["avg10"], max(cpu.values()), _some_averages(directory / "memory")["avg10"],
                        _some_averages(directory / "io")["avg10"])


@dataclass(frozen=True)
class SwapResources:
    available_mib: Callable[[], int]
    pressure: Callable[[], HostPressure]
    disk_mib: Callable[[Path], tuple[int, int]]
    device: Callable[[Path], int]


HOST_RESOURCES = SwapResources(_memory_available_mib, _host_pressure, _disk_mib, _device)


def _swap_write(path: Path, content: dict) -> None:
    path.write_text(json.dumps(content, indent=2) + "\n", encoding="utf-8")


@contextmanager
def _swap_lock(layout: Layout) -> Iterator[None]:
    """Sem espera: dois subcomandos no mesmo sombra disputariam os mesmos containers.

    Fica ao lado da raiz, não dentro: `down` apaga a raiz e precisa funcionar mesmo sem `state/`.
    """
    descriptor = os.open(layout.root.parent / f".{layout.root.name}.lock", os.O_CREAT | os.O_RDWR, 0o600)
    try:
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ShadowError("outro subcomando do sombra está em execução") from error
        yield
    finally:
        os.close(descriptor)


@contextmanager
def _swap_signals() -> Iterator[Callable[[], None]]:
    """SIGHUP, SIGTERM e SIGINT viram erro para o ensaio voltar à origem; o rollback chama a blindagem devolvida."""
    def interrupt(number: int, _frame: object) -> None:
        raise ShadowError(f"interrompido pelo sinal {signal.Signals(number).name}")

    numbers = (signal.SIGHUP, signal.SIGTERM, signal.SIGINT)
    previous = {number: signal.signal(number, interrupt) for number in numbers}

    def shield() -> None:
        # Handler vazio, não SIG_IGN: o ignorado seria herdado pelos filhos docker.
        for number in numbers:
            signal.signal(number, lambda _number, _frame: None)

    try:
        yield shield
    finally:
        for number, handler in previous.items():
            signal.signal(number, handler)


def _swap_guard(layout: Layout, run: Runner) -> list[str]:
    """Antes de qualquer efeito: só o sombra, no digest esperado, com o Compose resolvendo para os nomes do sombra."""
    root = layout.root.resolve()
    if not root.name.startswith(PROJECT):
        raise ShadowError(f"não ensaio em {layout.root}: fora do padrão {PROJECT}")
    compose = compose_command(layout)
    images = dict(line.split("=", 1) for line in layout.env_file.read_text(encoding="utf-8").splitlines() if "=" in line)
    if images.get("DUORA_POSTGRES_IMAGE") != SWAP_SOURCE_IMAGE:
        raise ShadowError("o sombra não está no digest de origem do ensaio")
    config = json.loads(run([*compose, "config", "--format", "json"]))
    if config["name"] != PROJECT or config["volumes"]["pgdata"]["name"] != PROJECT + "_pgdata":
        raise ShadowError("o Compose não resolve para o projeto e o volume do sombra")
    _swap_require_confined(config, root)
    return compose


def _swap_require_confined(config: dict, root: Path) -> None:
    services = config["services"]
    if {services[name]["image"] for name in ("postgres", "backup")} != {SWAP_SOURCE_IMAGE}:
        raise ShadowError("o Compose não resolve postgres e backup para o digest de origem")
    sources = [volume["source"] for service in services.values() for volume in service.get("volumes", [])
               if volume["type"] == "bind"]
    sources += [item["file"] for section in ("secrets", "configs") for item in config.get(section, {}).values()
                if "file" in item]
    outside = sorted(source for source in sources if not Path(source).resolve().is_relative_to(root))
    if outside:
        raise ShadowError(f"o Compose monta caminhos fora de {root}: {outside}")
    exposed = [port.get("host_ip") for service in services.values() for port in service.get("ports", [])]
    if any(address not in SWAP_LOOPBACK for address in exposed):
        raise ShadowError(f"o Compose publica portas fora do loopback: {exposed}")


def _swap_require_running(run: Runner, image: str) -> None:
    """Confere o id real, não só a referência pedida: uma tag ou um pull trocado apareceriam aqui."""
    expected = run(["docker", "image", "inspect", "--format", "{{.Id}}", image]).strip()
    running = run(["docker", "inspect", "--format", "{{.Config.Image}} {{.Image}}", *SWAP_CONTAINERS]).splitlines()
    if {tuple(line.split()) for line in running} != {(image, expected)}:
        raise ShadowError(f"containers fora do digest esperado ({image}): {sorted(running)}; "
                          "depois de um ensaio morto, rode swap-abort")


def _swap_require_resources(resources: SwapResources, path: Path, *, memory_mib: int, disk_needed_mib: int,
                            cpu_ceiling: float) -> None:
    available = resources.available_mib()
    if available < memory_mib:
        raise ShadowError(f"memória disponível: {available} MiB, abaixo de {memory_mib} MiB")
    total, free = resources.disk_mib(path)
    if (total - free + disk_needed_mib) * 100 >= total * SWAP_MAX_DISK_PERCENT:
        raise ShadowError(f"disco de {path} chegaria a {SWAP_MAX_DISK_PERCENT}% com mais {disk_needed_mib} MiB")
    pressure = resources.pressure()
    if max(pressure.memory, pressure.io) > SWAP_MAX_STALL_PRESSURE:
        raise ShadowError(f"PSI de memória {pressure.memory} ou IO {pressure.io} acima de {SWAP_MAX_STALL_PRESSURE}")
    if pressure.cpu > cpu_ceiling:
        raise ShadowError(f"PSI de CPU {pressure.cpu} acima de {cpu_ceiling} (linha de base mais margem)")


def _swap_preflight(layout: Layout, fill_mib: int, run: Runner, resources: SwapResources) -> tuple[Path, float]:
    """Devolve o diretório do Docker e a linha de base de PSI de CPU deste início."""
    docker_root = Path(run(["docker", "info", "--format", "{{.DockerRootDir}}"]).strip())
    if resources.device(layout.root) != resources.device(docker_root):
        raise ShadowError(f"{docker_root} e {layout.root} em filesystems diferentes: a conta de disco não valeria")
    baseline = resources.pressure().cpu_baseline
    if baseline > SWAP_MAX_CPU_BASELINE:
        raise ShadowError(f"PSI de CPU em {baseline}, acima de {SWAP_MAX_CPU_BASELINE}: o host não está calmo para o ensaio")
    _swap_require_resources(resources, docker_root, memory_mib=SWAP_MIN_AVAILABLE_MIB,
                            disk_needed_mib=fill_mib * SWAP_DISK_FACTOR + SWAP_DISK_MARGIN_MIB,
                            cpu_ceiling=baseline + SWAP_CPU_PRESSURE_MARGIN)
    return docker_root, baseline


def _swap_compose(layout: Layout, image: str) -> list[str]:
    """O env do sombra mais um arquivo só com a imagem: a segunda --env-file vence, como no executor."""
    path = layout.state / "swap-image.env"
    path.write_text(f"DUORA_POSTGRES_IMAGE={image}\n", encoding="utf-8")
    path.chmod(0o600)
    return [*compose_command(layout), "--env-file", str(path)]


def _swap_postgres_limits(config: dict) -> tuple[str, str]:
    """Do Compose, não do container: depois de um ensaio morto, o container ainda teria os limites reduzidos."""
    service = config["services"]["postgres"]
    return f"{float(service['deploy']['resources']['limits']['cpus']):g}", str(service["cpu_shares"])


def _swap_fill(run: Runner, compose: list[str], fill_mib: int, between_batches: Callable[[], None],
               sleep: Callable[[float], None]) -> None:
    _sql(run, compose, "truncate c2.swap_ballast")
    row = (f"(select string_agg(md5(random()::text||g::text||i::text),'') from generate_series(1,{SWAP_FILL_MD5_PER_ROW}) i)")
    cpus, cpu_shares = _swap_postgres_limits(json.loads(run([*compose, "config", "--format", "json"])))
    run(["docker", "update", "--cpus", SWAP_FILL_CPUS, "--cpu-shares", str(SWAP_FILL_CPU_SHARES), SWAP_CONTAINERS[0]])
    try:
        for _ in range(-(-fill_mib // SWAP_FILL_BATCH_MIB)):
            between_batches()
            _sql(run, compose, f"insert into c2.swap_ballast(payload) select {row} from generate_series(1,{SWAP_FILL_ROWS}) g")
            sleep(SWAP_FILL_PAUSE_SECONDS)
    finally:
        run(["docker", "update", "--cpus", cpus, "--cpu-shares", cpu_shares, SWAP_CONTAINERS[0]])


def _swap_wait_archived(run: Runner, compose: list[str], clock: Callable[[], float], sleep: Callable[[float], None]) -> None:
    """O check espera o WAL corrente por pouco tempo: a fila do preenchimento precisa escoar antes de parar algo."""
    segment = _sql(run, compose, "select pg_walfile_name(pg_switch_wal())")
    deadline = clock() + WAIT_SECONDS
    while _sql(run, compose, "select coalesce(last_archived_wal,'') from pg_stat_archiver") < segment:
        if clock() > deadline:
            raise ShadowError(f"WAL não arquivado em {WAIT_SECONDS} s; nada foi parado")
        sleep(SWAP_ARCHIVE_POLL_SECONDS)


def _swap_check(run: Runner, compose: list[str]) -> None:
    run([*compose, "exec", "--no-TTY", "backup", "pgbackrest", "--stanza=duora", "check"])


def _swap_recreate(layout: Layout, image: str, run: Runner, clock: Callable[[], float]) -> dict[str, float]:
    # Mesma ordem da operação real: o backup recebe o WAL, para depois do postgres e sobe antes dele.
    compose = _swap_compose(layout, image)
    moments = [clock()]
    for command in (["stop", "postgres"], ["stop", "backup"], ["up", "--detach", "--wait", "--no-deps", "backup"],
                    ["up", "--detach", "--wait", "--no-deps", "postgres"]):
        run([*compose, *command])
        moments.append(clock())
    return {"stopPostgresSeconds": round(moments[1] - moments[0], 1), "stopBackupSeconds": round(moments[2] - moments[1], 1),
            "upBackupSeconds": round(moments[3] - moments[2], 1), "upPostgresSeconds": round(moments[4] - moments[3], 1),
            "databaseDownSeconds": round(moments[4] - moments[0], 1)}


def _swap_snapshot(run: Runner, compose: list[str]) -> dict:
    """O que não pode mudar: identidade do banco, compatibilidade do binário e o conteúdo do lastro."""
    live = json.loads(_sql(run, compose, SWAP_COMPATIBILITY_SQL))
    if live["recorded"] != live["actual"]:
        raise ShadowError(f"colação do binário ({live['actual']}) diverge da gravada no banco ({live['recorded']})")
    ballast = _sql(run, compose, "select count(*)||':'||coalesce(md5(string_agg(digest,'')),'') from "
                                 "(select md5(payload) digest from c2.swap_ballast order by id) rows")
    return {"photo": json.loads(_sql(run, compose, SWAP_PHOTO_SQL)), "major": int(live["serverVersionNum"]) // 10000,
            "compatibility": {key: live[key] for key in SWAP_COMPATIBLE_FIELDS}, "ballast": ballast}


def _swap_volume(run: Runner) -> str:
    return run(["docker", "volume", "inspect", "--format", "{{.CreatedAt}}", PROJECT + "_pgdata"]).strip()


def _swap_verify(run: Runner, compose: list[str], baseline: dict) -> None:
    if _swap_snapshot(run, compose) != baseline["snapshot"]:
        raise ShadowError("foto do banco mudou: identidade, timeline, head, ACL, colação ou lastro")
    if _swap_volume(run) != baseline["volume"]:
        raise ShadowError("volume de dados mudou de identidade")


def _swap_direction(layout: Layout, image: str, baseline: dict, run: Runner, clock: Callable[[], float]) -> dict[str, float]:
    timing = _swap_recreate(layout, image, run, clock)
    compose = _swap_compose(layout, image)
    _swap_require_running(run, image)
    _swap_verify(run, compose, baseline)
    started = clock()
    _swap_check(run, compose)
    timing["checkSeconds"] = round(clock() - started, 1)
    return timing


def swap_abort(layout: Layout, run: Runner = default_runner) -> dict:
    """Volta ao digest de origem no mesmo volume e confere antes de religar; serve também depois de um ensaio morto.

    Se a conferência falhar, a aplicação fica parada de propósito: o relatório em swap-abort.json diz o motivo.
    """
    _swap_guard(layout, run)
    compose = _swap_compose(layout, SWAP_SOURCE_IMAGE)
    baseline_path = layout.state / "swap-baseline.json"
    report: dict = {}
    try:
        run([*compose, "stop", "worker"])
        run([*compose, "stop", "web"])
        _swap_recreate(layout, SWAP_SOURCE_IMAGE, run, time.monotonic)
        _swap_require_running(run, SWAP_SOURCE_IMAGE)
        report["digest"] = SWAP_SOURCE_IMAGE
        report["baselineCompared"] = baseline_path.exists()
        if report["baselineCompared"]:
            _swap_verify(run, compose, json.loads(baseline_path.read_text(encoding="utf-8")))
        _swap_check(run, compose)
        report["check"] = True
        run([*compose, "up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"])
    except SWAP_FAILURES as error:
        report["error"] = str(error)
        raise ShadowError(f"abort sem confirmação, aplicação parada: {error}") from error
    finally:
        _swap_write(layout.state / "swap-abort.json", report)
    return report


def _swap_prepare(compose: list[str], target: str, fill_mib: int, result: dict, run: Runner, clock: Callable[[], float],
                  sleep: Callable[[float], None], calm: Callable[[], None]) -> None:
    """Tudo o que antecede a primeira parada: uma falha aqui deixa o sombra de pé, no digest de origem."""
    started = clock()
    run(["docker", "pull", "--quiet", target])
    result["pullSeconds"] = round(clock() - started, 1)
    _sql(run, compose, "create table if not exists c2.swap_ballast(id bigserial primary key, payload text not null); "
                       "alter table c2.swap_ballast alter column payload set storage external")
    if fill_mib:
        started = clock()
        _swap_fill(run, compose, fill_mib, calm, sleep)
        result["fillSeconds"] = round(clock() - started, 1)
    result["databaseSizeMib"] = int(_sql(run, compose, "select pg_database_size(current_database())/1048576"))
    started = clock()
    _swap_wait_archived(run, compose, clock, sleep)
    result["archiveWaitSeconds"] = round(clock() - started, 1)


def _swap_round_trip(layout: Layout, compose: list[str], target: str, result: dict, run: Runner, clock: Callable[[], float],
                     calm: Callable[[], None]) -> None:
    stopping = clock()
    run([*compose, "stop", "worker"])
    run([*compose, "stop", "web"])
    result["stopApplicationSeconds"] = round(clock() - stopping, 1)
    baseline = {"snapshot": _swap_snapshot(run, compose), "volume": _swap_volume(run)}
    _swap_write(layout.state / "swap-baseline.json", baseline)
    result["ballast"] = baseline["snapshot"]["ballast"]
    _swap_check(run, compose)
    calm()
    result["forward"] = _swap_direction(layout, target, baseline, run, clock)
    calm()
    result["back"] = _swap_direction(layout, SWAP_SOURCE_IMAGE, baseline, run, clock)
    reopening = clock()
    run([*compose, "up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"])
    finished = clock()
    result["reopenApplicationSeconds"] = round(finished - reopening, 1)
    result["applicationDownSeconds"] = round(finished - stopping, 1)


def swap_rehearsal(layout: Layout, target: str, *, fill_mib: int, run: Runner = default_runner,
                   clock: Callable[[], float] = time.monotonic, sleep: Callable[[float], None] = time.sleep,
                   resources: SwapResources = HOST_RESOURCES, shield: Callable[[], None] = lambda: None) -> dict:
    compose = _swap_guard(layout, run)
    if target != SWAP_TARGET_IMAGE:
        raise ShadowError("alvo fora da allowlist do ensaio")
    if not 0 <= fill_mib <= SWAP_MAX_FILL_MIB:
        raise ShadowError(f"--fill-mib fora de 0..{SWAP_MAX_FILL_MIB}")
    docker_root, cpu_baseline = _swap_preflight(layout, fill_mib, run, resources)
    _swap_require_running(run, SWAP_SOURCE_IMAGE)
    (layout.state / "swap-baseline.json").unlink(missing_ok=True)

    def calm() -> None:
        _swap_require_resources(resources, docker_root, memory_mib=SWAP_MEMORY_FLOOR_MIB, disk_needed_mib=0,
                                cpu_ceiling=cpu_baseline + SWAP_CPU_PRESSURE_MARGIN)

    result: dict = {"source": SWAP_SOURCE_IMAGE, "target": target, "cpuPressureBaseline": cpu_baseline}
    evidence = layout.state / "swap-rehearsal.json"
    try:
        _swap_prepare(compose, target, fill_mib, result, run, clock, sleep, calm)
    except SWAP_FAILURES as error:
        result.update(aborted=True, error=str(error))
        _swap_write(evidence, result)
        raise ShadowError(f"ensaio parado antes de parar qualquer serviço: {error}") from error
    try:
        _swap_round_trip(layout, compose, target, result, run, clock, calm)
    except SWAP_FAILURES as error:
        shield()
        result.update(aborted=True, error=str(error))
        _swap_write(evidence, result)
        try:
            result["abort"] = swap_abort(layout, run)
        finally:
            result.setdefault("abort", {"error": "sem confirmação; ver swap-abort.json"})
            _swap_write(evidence, result)
        raise ShadowError(f"ensaio abortado, de volta ao digest de origem: {error}") from error
    result["photoStable"] = True
    _swap_write(evidence, result)
    (layout.state / "swap-baseline.json").unlink()
    return result


LOCKED_COMMANDS = ("up", "media-fixture", "restore-rehearsal", "swap-rehearsal", "swap-abort", "down")


def _run_command(args: argparse.Namespace, layout: Layout) -> None:
    if args.command == "prepare":
        prepare(layout, args.release_env)
    elif args.command == "up":
        up(layout, args.accounts)
    elif args.command == "media-fixture":
        media_fixture(layout, args.mib)
    elif args.command == "restic-command":
        print(restic_backup_command(layout))
    elif args.command == "restore-rehearsal":
        print(json.dumps(restore_rehearsal(layout, final_full=args.final_full), indent=2))
    elif args.command == "swap-rehearsal":
        with _swap_signals() as shield:
            _say(json.dumps(swap_rehearsal(layout, args.target, fill_mib=args.fill_mib, shield=shield), indent=2))
    elif args.command == "swap-abort":
        with _swap_signals() as shield:
            shield()
            _say(json.dumps(swap_abort(layout), indent=2))
    else:
        down(layout)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="projeto sombra duora-c2 (issue #86)")
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_parser = commands.add_parser("prepare")
    prepare_parser.add_argument("--release-env", type=Path, required=True)
    up_parser = commands.add_parser("up")
    up_parser.add_argument("--accounts", type=int, default=1000)
    fixture = commands.add_parser("media-fixture")
    fixture.add_argument("--mib", type=int, default=2048)
    commands.add_parser("restic-command")
    rehearsal = commands.add_parser("restore-rehearsal")
    rehearsal.add_argument("--no-final-full", dest="final_full", action="store_false",
                           help="não refaz o FULL depois do restore (sombra derrubado em seguida)")
    swap_parser = commands.add_parser("swap-rehearsal")
    swap_parser.add_argument("--target", required=True, help="digest alvo; só o revisado no código é aceito")
    swap_parser.add_argument("--fill-mib", type=int, default=0, help="dados sintéticos gravados antes da troca (recomendado: 256)")
    commands.add_parser("swap-abort")
    commands.add_parser("down")
    args = parser.parse_args(argv)
    layout = Layout(args.root)
    if args.command != "restic-command" and os.geteuid() != 0:
        print("c2-shadow: rode como root (chown dos segredos e diretórios sintéticos)", file=sys.stderr)
        return 1
    try:
        with _swap_lock(layout) if args.command in LOCKED_COMMANDS else nullcontext():
            _run_command(args, layout)
    except SWAP_FAILURES as error:
        _say(f"c2-shadow: {error}", stream="stderr")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
