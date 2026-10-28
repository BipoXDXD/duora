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
    python3 c2_shadow.py down
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import secrets
import shutil
import subprocess
import sys
import time
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

import c2_probe
import duora_secrets

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


class ShadowError(RuntimeError):
    pass


Runner = Callable[..., str]


def default_runner(argv: Sequence[str], *, stdin: str | None = None, check: bool = True,
                   timeout: int = COMMAND_TIMEOUT_SECONDS) -> str:
    print("[c2-shadow] " + " ".join(argv[:8]) + (" ..." if len(argv) > 8 else ""), flush=True)
    result = subprocess.run(list(argv), input=stdin, capture_output=True, text=True, timeout=timeout, check=False)
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
    commands.add_parser("down")
    args = parser.parse_args(argv)
    layout = Layout(args.root)
    if args.command != "restic-command" and os.geteuid() != 0:
        print("c2-shadow: rode como root (chown dos segredos e diretórios sintéticos)", file=sys.stderr)
        return 1
    try:
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
        else:
            down(layout)
    except (ShadowError, OSError, ValueError) as error:
        print(f"c2-shadow: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
