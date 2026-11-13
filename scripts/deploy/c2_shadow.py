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
import re
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
import capacity_inventory
import duora_secrets
from postgres_swap import SWAP_COMPATIBILITY_SQL, SWAP_COMPATIBLE_FIELDS, SWAP_PHOTO_SQL

REPO_ROOT = Path(__file__).resolve().parents[2]
COMPOSE_FILE = REPO_ROOT / "infra" / "compose" / "compose.prod.yml"
DRILL_FILE = REPO_ROOT / "scripts" / "backup" / "compose.drill.yml"
OVERLAY_FILE = REPO_ROOT / "scripts" / "deploy" / "c2" / "compose.c2.yml"
SELFIE_SEED_FILE = REPO_ROOT / "scripts" / "deploy" / "c2" / "selfie-restore-seed.sql"
SELFIE_CHECK_FILE = REPO_ROOT / "scripts" / "deploy" / "c2" / "selfie-restore-check.sql"
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
# Mesmo ambiente e mesmo escopo de postgres_swap.run_in_own_group: só o `docker` o recebe. Variável do root (DUORA_*_DIR,
# DOCKER_HOST, DOCKER_CONTEXT, COMPOSE_*) venceria o --env-file e poderia apontar o Compose para a produção. Os demais
# comandos (openssl, restic, duora_backup.py) herdam o ambiente, como nas janelas anteriores.
RUNNER_ENVIRONMENT = {"PATH": "/usr/bin:/bin", "LC_ALL": "C"}


class ShadowError(RuntimeError):
    pass


class SwapInterrupted(ShadowError):
    """Um sinal, não uma falha do que estava rodando: nenhum tratamento de sonda pode absorvê-lo."""


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
    environment = RUNNER_ENVIRONMENT if argv and argv[0] == "docker" else None
    result = subprocess.run(list(argv), input=stdin, capture_output=True, text=True, timeout=timeout, check=False,
                            env=environment)
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
    _psql_file(run, compose_command(layout), SELFIE_SEED_FILE)
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


def _psql_file(run: Runner, compose: list[str], script: Path) -> None:
    run([*compose, "exec", "--no-TTY", "postgres", "psql", "--username", "postgres", "--dbname", "duora", "--quiet",
         "--set", "ON_ERROR_STOP=1"], stdin=script.read_text(encoding="utf-8"))


def _checked_selfie_rows(run: Runner, compose: list[str]) -> int:
    """Confere a fixture sintética (o psql termina com erro se ela divergir) e devolve o total da tabela."""
    _psql_file(run, compose, SELFIE_CHECK_FILE)
    return int(_sql(run, compose, "select count(*) from identity.selfie_verifications"))


def restore_rehearsal(layout: Layout, run: Runner = default_runner, clock: Callable[[], float] = time.time,
                      sleep: Callable[[float], None] = time.sleep, *, final_full: bool = True) -> dict[str, float]:
    """Escreve marcadores por 90 s, mata o postgres (perda do host), apaga o volume, restaura até o fim do WAL do
    repositório e mede: RPO = último marcador confirmado - último restaurado; RTO = perda até web/worker de pé.
    As tentativas de selfie são conferidas antes da perda e depois da promoção, com web e worker ainda parados: a
    conferência vê o que o restore entregou, antes de qualquer limpeza do worker. Com a selfie desligada no sombra,
    nada escreve na tabela, então o total restaurado é igual ao de antes.
    `final_full` refaz a cadeia de backup depois do restore; na janela de 3 h o sombra é derrubado em seguida e o
    FULL novo só gastaria tempo, sem entrar no RPO/RTO."""
    compose = compose_command(layout)
    selfie_rows = _checked_selfie_rows(run, compose)
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
    restored_selfie_rows = _checked_selfie_rows(run, compose)
    if restored_selfie_rows != selfie_rows:
        raise ShadowError(f"selfie_verifications: {selfie_rows} linhas antes da perda, {restored_selfie_rows} "
                          "depois do restore")
    run([*compose, "up", "--detach", "--wait", "web", "worker", "edge"])
    ready_at = clock()
    result = {"last_ack_at": last_ack, "restored_max_at": restored, "outage_at": outage_at, "ready_at": ready_at,
              "rpo_seconds": round(last_ack - restored, 1), "rto_seconds": round(ready_at - outage_at, 1),
              "selfie_rows": restored_selfie_rows}
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
# Digest amd64 da imagem construída na run 43372869832 do GitHub Actions.
SWAP_TARGET_IMAGE = "ghcr.io/bipoxdxd/duora-postgres@sha256:cd9e980845df574a23e9d92accc18c88cbcdabd8110537336f848fa9d4554cb6"
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
# O guarda mede dano à produção, não atividade do host. PSI de memória e IO têm teto absoluto: em repouso a VPS fica em 0,00.
# O PSI de CPU global não decide nada, só vai ao JSON: no ensaio de 06/11 ele bateu 71,79 por causa de cgroups do OpenClaw
# presos na própria cota, com CPU 91% ociosa, e `duora-web-1` em 0,83. Medido localmente, um único cgroup preso levou o global
# acima de 90 com o alvo em 0,00, e nenhum teto abaixo de 100 separa os dois casos.
# Por isso, a falta real de CPU vem do delta de /proc/stat: idle + iowait abaixo de 10% significa host sem folga para ninguém,
# inclusive para o que não é container (sshd, receiver, gateway). Cada container fora do sombra, menos o painel `coolify`
# (plano de controle), mantém o teto 40 do OTHER_APPS_PSI, mas ele sozinho não basta: a revisão mediu p99 22 vezes maior num alvo
# leve com PSI do alvo em 22,54. Só a sonda `ready` vê a latência que o usuário sente, com a regra do harness
# (health_latency_*, health_consecutive_failures).
# A volta à origem não passa por este guarda, de propósito: parar no meio dela seria pior que terminar.
SWAP_MAX_STALL_PRESSURE = 40.0
SWAP_MAX_CONTAINER_CPU_PRESSURE = 40.0
# Só o painel do Coolify fica fora do teto por container, como no OTHER_APPS_PSI: dono `coolify` em
# capacity_inventory.CONTAINER_RULES e nome exato. coolify-proxy (Traefik de todos os sites), -realtime e -db seguem guardados.
SWAP_CONTROL_PLANE_OWNER = "coolify"
SWAP_CONTROL_PLANE_CONTAINER = "coolify"
SWAP_MIN_CPU_IDLE_PERCENT = 10.0
SWAP_PRODUCTION_CONTAINERS = ("duora-web-1", "duora-worker-1", "duora-postgres-1")
SWAP_CONTAINER_ID = re.compile(r"[0-9a-f]{64}")
SWAP_DOCKER_PS_TIMEOUT_SECONDS = 10
# Mesma sonda de duora_deploy._wait_ready, no edge de produção: só leitura, e o web não passa pela borda pública.
SWAP_READY_PROBE = ("docker", "exec", "duora-edge-1", "wget", "--quiet", "--spider", "--timeout=5",
                    "http://duora-web:8080/health/ready")
SWAP_READY_TIMEOUT_SECONDS = 15
SWAP_READY_BASELINE_SAMPLES = 5
SWAP_READY_FACTOR = 3.0
SWAP_READY_FLOOR_MS = 500.0
SWAP_READY_CONSECUTIVE = 2
SWAP_ARCHIVE_GUARD_SECONDS = 10
SWAP_LOOPBACK = ("127.0.0.1", "::1")
SWAP_CONTAINERS = (f"{PROJECT}-postgres-1", f"{PROJECT}-backup-1")
# IndexError e TypeError: leitura de /proc ou JSON fora do formato esperado também tem de voltar à origem.
SWAP_FAILURES = (ShadowError, c2_probe.CommandRefused, subprocess.TimeoutExpired, OSError, ValueError, KeyError,
                 IndexError, TypeError)


def _memory_available_mib(meminfo: Path = Path("/proc/meminfo")) -> int:
    for line in meminfo.read_text(encoding="utf-8").splitlines():
        if line.startswith("MemAvailable:"):
            return int(line.split()[1]) // 1024
    raise ShadowError(f"MemAvailable ausente em {meminfo}")


def _some_averages(path: Path) -> dict[str, float]:
    lines = path.read_text(encoding="utf-8").splitlines()
    if not lines or not lines[0].startswith("some "):
        raise ShadowError(f"linha 'some' ausente em {path}")
    fields = lines[0].split()[1:]
    return {name: float(value) for name, value in (field.split("=") for field in fields) if name != "total"}


def _disk_mib(path: Path) -> tuple[int, int]:
    usage = shutil.disk_usage(path)
    return usage.total // (1024 * 1024), usage.free // (1024 * 1024)


def _device(path: Path) -> int:
    return path.stat().st_dev


@dataclass(frozen=True)
class HostPressure:
    """`some avg10` do host, em %."""

    cpu: float
    memory: float
    io: float


def _host_pressure(directory: Path = Path("/proc/pressure")) -> HostPressure:
    cpu, memory, io = (_some_averages(directory / name)["avg10"] for name in ("cpu", "memory", "io"))
    return HostPressure(cpu, memory, io)


def _cpu_ticks(stat: Path = Path("/proc/stat")) -> tuple[int, int]:
    """Acumulados desde o boot: (idle + iowait, total dos 8 campos); guest já está somado em user."""
    fields = [int(value) for value in stat.read_text(encoding="utf-8").splitlines()[0].split()[1:1 + len(c2_probe.CPU_FIELDS)]]
    return fields[c2_probe.CPU_FIELDS.index("idle")] + fields[c2_probe.CPU_FIELDS.index("iowait")], sum(fields)


def _oom_kills(vmstat: Path = Path("/proc/vmstat")) -> int:
    for line in vmstat.read_text(encoding="utf-8").splitlines():
        name, value = line.split()
        if name == "oom_kill":
            return int(value)
    raise ShadowError(f"oom_kill ausente em {vmstat}")


def _container_cpu_pressure(identifier: str, root: Path = Path(c2_probe.CGROUP_ROOT)) -> float:
    """Só leitura. Mesmo caminho de c2_probe.Sampler: driver systemd, cgroup v2."""
    return _some_averages(root / "system.slice" / f"docker-{identifier}.scope" / "cpu.pressure")["avg10"]


@dataclass(frozen=True)
class SwapResources:
    available_mib: Callable[[], int]
    pressure: Callable[[], HostPressure]
    container_pressure: Callable[[str], float]
    cpu_ticks: Callable[[], tuple[int, int]]
    oom_kills: Callable[[], int]
    disk_mib: Callable[[Path], tuple[int, int]]
    device: Callable[[Path], int]


HOST_RESOURCES = SwapResources(_memory_available_mib, _host_pressure, _container_cpu_pressure, _cpu_ticks, _oom_kills, _disk_mib,
                               _device)


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
        raise SwapInterrupted(f"interrompido pelo sinal {signal.Signals(number).name}")

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


def _swap_ready_ceiling_ms(baseline_ms: float) -> float:
    return max(baseline_ms * SWAP_READY_FACTOR, baseline_ms + SWAP_READY_FLOOR_MS)


class SwapGuard:
    """Decide se o ensaio segue. Guarda o estado entre leituras e escreve em `evidence` os piores valores vistos."""

    def __init__(self, resources: SwapResources, docker_root: Path, run: Runner, clock: Callable[[], float], evidence: dict) -> None:
        self._resources = resources
        self._docker_root = docker_root
        self._run = run
        self._clock = clock
        self._evidence = evidence
        self._required = set(SWAP_PRODUCTION_CONTAINERS)
        self._bad_ready = 0
        self._ticks = resources.cpu_ticks()
        self._oom_kills = resources.oom_kills()
        self._ready_ceiling_ms = 0.0

    def freeze(self) -> None:
        """Foto do início: quem está de pé agora tem de seguir de pé, e a sonda ganha sua linha de base."""
        self._required |= self._container_pressures().keys()
        try:
            samples = sorted(self._ready_ms() for _ in range(SWAP_READY_BASELINE_SAMPLES))
        except SwapInterrupted:
            raise
        except (ShadowError, subprocess.TimeoutExpired, OSError) as error:
            raise ShadowError(f"ready falhou na linha de base: {error}") from error
        baseline = samples[len(samples) // 2]
        self._ready_ceiling_ms = _swap_ready_ceiling_ms(baseline)
        self._evidence.update(readyBaselineMs=baseline, readyMaxMs=baseline)

    def check(self, *, memory_mib: int = SWAP_MEMORY_FLOOR_MIB, disk_needed_mib: int = 0) -> None:
        self._require_host(memory_mib, disk_needed_mib)
        self._require_calm_containers()
        self._require_ready()

    def check_between_directions(self) -> None:
        """Entre a ida e a volta, o estouro de PSI por container só vai à evidência e ao log. Abortar ali faria a mesma
        volta, sem segurança a mais, e perderia a medição. Os demais guardas, inclusive o vizinho ilegível, seguem iguais.
        Os containers são lidos primeiro para a medição ficar no JSON mesmo quando um guarda de host aborta em seguida."""
        self._record_container_overflows()
        self._require_host(SWAP_MEMORY_FLOOR_MIB, 0)
        self._require_ready()

    def _require_host(self, memory_mib: int, disk_needed_mib: int) -> None:
        available = self._resources.available_mib()
        if available < memory_mib:
            raise ShadowError(f"memória disponível: {available} MiB, abaixo de {memory_mib} MiB")
        total, free = self._resources.disk_mib(self._docker_root)
        if (total - free + disk_needed_mib) * 100 >= total * SWAP_MAX_DISK_PERCENT:
            raise ShadowError(f"disco de {self._docker_root} chegaria a {SWAP_MAX_DISK_PERCENT}% com mais {disk_needed_mib} MiB")
        pressure = self._resources.pressure()
        self._note_worst("hostCpuPressureMax", pressure.cpu)
        if max(pressure.memory, pressure.io) > SWAP_MAX_STALL_PRESSURE:
            raise ShadowError(f"PSI de memória {pressure.memory} ou IO {pressure.io} acima de {SWAP_MAX_STALL_PRESSURE}")
        self._require_cpu_idle()
        self._require_no_new_oom()

    def _require_cpu_idle(self) -> None:
        ticks = self._resources.cpu_ticks()
        idle, total = ticks[0] - self._ticks[0], ticks[1] - self._ticks[1]
        self._ticks = ticks
        if total <= 0:
            return  # Duas leituras no mesmo tick: não há intervalo para medir.
        percent = round(100.0 * idle / total, 1)
        self._evidence["cpuIdleMinPercent"] = min(percent, self._evidence.get("cpuIdleMinPercent", percent))
        if percent < SWAP_MIN_CPU_IDLE_PERCENT:
            raise ShadowError(f"CPU com idle + iowait em {percent}%, abaixo de {SWAP_MIN_CPU_IDLE_PERCENT}%")

    def _require_no_new_oom(self) -> None:
        count = self._resources.oom_kills()
        if count > self._oom_kills:
            raise ShadowError(f"oom_kill subiu de {self._oom_kills} para {count}")

    def _require_calm_containers(self) -> None:
        name, worst = max(self._noted_container_pressures().items(), key=lambda item: item[1])
        if worst > SWAP_MAX_CONTAINER_CPU_PRESSURE:
            raise ShadowError(f"PSI de CPU de {name} em {worst}, acima de {SWAP_MAX_CONTAINER_CPU_PRESSURE}")

    def _record_container_overflows(self) -> None:
        overflows = [{"container": name, "cpuPressure": value}
                     for name, value in sorted(self._noted_container_pressures().items())
                     if value > SWAP_MAX_CONTAINER_CPU_PRESSURE]
        self._evidence["containerCpuPressureOverflowsBetweenDirections"] = overflows
        for item in overflows:
            _say(f"[c2-shadow] PSI de CPU de {item['container']} em {item['cpuPressure']}, acima de "
                 f"{SWAP_MAX_CONTAINER_CPU_PRESSURE}: só registrado entre a ida e a volta")

    def _noted_container_pressures(self) -> dict[str, float]:
        pressures = self._container_pressures()
        self._note_worst("containerCpuPressureMax", max(pressures.values()))
        return pressures

    def _container_pressures(self) -> dict[str, float]:
        """Todo container fora do sombra, menos o painel `coolify`. Falha fechada: sem a leitura de um deles, não há como afirmar
        que ele vai bem."""
        pressures: dict[str, float] = {}
        listing = self._run(list(c2_probe.PS_COMMAND), timeout=SWAP_DOCKER_PS_TIMEOUT_SECONDS)
        for row in listing.splitlines():
            identifier, name, project = row.split("\t")
            if project == PROJECT:
                continue  # O próprio preenchimento pressiona o sombra de propósito.
            if not SWAP_CONTAINER_ID.fullmatch(identifier):
                raise ShadowError(f"cgroup de {name}: id de container fora do formato")
            if _swap_is_control_plane(name, project):
                continue
            try:
                pressures[name] = self._resources.container_pressure(identifier)
            except (OSError, ValueError, KeyError, IndexError) as error:
                raise ShadowError(f"cgroup de {name} ilegível, recuso seguir: {error!r}") from error
        missing = sorted(self._required - pressures.keys())
        if missing:
            raise ShadowError(f"containers sem leitura: {missing}")
        return pressures

    def _ready_ms(self) -> float:
        started = self._clock()
        self._run(list(SWAP_READY_PROBE), timeout=SWAP_READY_TIMEOUT_SECONDS)
        return round((self._clock() - started) * 1000, 1)

    def _require_ready(self) -> None:
        try:
            elapsed = self._ready_ms()
        except SwapInterrupted:
            raise
        except (ShadowError, subprocess.TimeoutExpired, OSError) as error:
            self._count_bad_ready(f"ready falhou: {error}")
            return
        self._note_worst("readyMaxMs", elapsed)
        if elapsed > self._ready_ceiling_ms:
            self._count_bad_ready(f"ready em {elapsed} ms, acima de {self._ready_ceiling_ms} ms")
        else:
            self._bad_ready = 0

    def _count_bad_ready(self, reason: str) -> None:
        self._bad_ready += 1
        if self._bad_ready >= SWAP_READY_CONSECUTIVE:
            raise ShadowError(f"{self._bad_ready} leituras ruins seguidas; {reason}")

    def _note_worst(self, key: str, value: float) -> None:
        self._evidence[key] = max(value, self._evidence.get(key, value))


def _swap_is_control_plane(name: str, project: str) -> bool:
    """Nome exato e dono pelo inventário C1: a regra de projeto (stillon, goblinshift) continua vencendo a de nome."""
    return (name == SWAP_CONTROL_PLANE_CONTAINER
            and capacity_inventory.classify_container({"name": name, "project": project}) == SWAP_CONTROL_PLANE_OWNER)


def _swap_preflight(layout: Layout, fill_mib: int, run: Runner, clock: Callable[[], float], resources: SwapResources,
                    evidence: dict) -> SwapGuard:
    docker_root = Path(run(["docker", "info", "--format", "{{.DockerRootDir}}"]).strip())
    if resources.device(layout.root) != resources.device(docker_root):
        raise ShadowError(f"{docker_root} e {layout.root} em filesystems diferentes: a conta de disco não valeria")
    guard = SwapGuard(resources, docker_root, run, clock, evidence)
    guard.freeze()
    guard.check(memory_mib=SWAP_MIN_AVAILABLE_MIB, disk_needed_mib=fill_mib * SWAP_DISK_FACTOR + SWAP_DISK_MARGIN_MIB)
    return guard


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


def _swap_wait_archived(run: Runner, compose: list[str], clock: Callable[[], float], sleep: Callable[[float], None],
                        calm: Callable[[], None]) -> None:
    """O check espera o WAL corrente por pouco tempo: a fila do preenchimento precisa escoar antes de parar algo."""
    segment = _sql(run, compose, "select pg_walfile_name(pg_switch_wal())")
    deadline = clock() + WAIT_SECONDS
    next_guard = clock() + SWAP_ARCHIVE_GUARD_SECONDS
    while _sql(run, compose, "select coalesce(last_archived_wal,'') from pg_stat_archiver") < segment:
        if clock() > deadline:
            raise ShadowError(f"WAL não arquivado em {WAIT_SECONDS} s; nada foi parado")
        if clock() >= next_guard:
            calm()
            next_guard = clock() + SWAP_ARCHIVE_GUARD_SECONDS
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
    _swap_wait_archived(run, compose, clock, sleep, calm)
    result["archiveWaitSeconds"] = round(clock() - started, 1)


def _swap_round_trip(layout: Layout, compose: list[str], target: str, result: dict, run: Runner, clock: Callable[[], float],
                     calm: Callable[[], None], calm_between: Callable[[], None]) -> None:
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
    calm_between()
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
    result: dict = {"source": SWAP_SOURCE_IMAGE, "target": target}
    guard = _swap_preflight(layout, fill_mib, run, clock, resources, result)
    _swap_require_running(run, SWAP_SOURCE_IMAGE)
    (layout.state / "swap-baseline.json").unlink(missing_ok=True)
    calm = guard.check
    evidence = layout.state / "swap-rehearsal.json"
    try:
        _swap_prepare(compose, target, fill_mib, result, run, clock, sleep, calm)
    except SWAP_FAILURES as error:
        result.update(aborted=True, error=str(error))
        _swap_write(evidence, result)
        raise ShadowError(f"ensaio parado antes de parar qualquer serviço: {error}") from error
    try:
        _swap_round_trip(layout, compose, target, result, run, clock, calm, guard.check_between_directions)
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
