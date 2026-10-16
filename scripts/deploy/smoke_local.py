#!/usr/bin/env python3
"""Smoke local da release: o mesmo caminho da VPS, numa máquina com Docker (docs/operations/deploy.md).

    python3 scripts/deploy/smoke_local.py --commit "$(git rev-parse HEAD)"

1. Sobe um registry descartável em 127.0.0.1:5050 e publica as cinco imagens para a arquitetura do Docker local;
   confere que a licença da Six Labors não ficou na imagem do worker (check_image_secret.py).
2. Gera segredos de teste num diretório temporário (fora do git) e um manifesto com os digests publicados.
3. Roda o duora-deploy de verdade (pull por digest, migrator, up, readiness), com Caddy em "tls internal".
4. Confere pela borda HTTPS: SPA, cache, CSP, 404 de assets, fallback só para navegação, /api e /hubs (upgrade
   WebSocket) chegando ao web, /health fechado, logs sem query/cookie, e o endurecimento dos containers.
5. Confere que o key ring no volume está cifrado com o certificado, recria o container web e confere que um token
   antiforgery emitido antes continua válido: o key ring mora no volume, não na camada do container (T-15).
6. Derruba tudo com "down -v" e remove o registry (a menos que --keep).

Usa as portas 80 e 443 do host. Só biblioteca padrão; os segredos gerados nunca saem do diretório temporário.
"""

from __future__ import annotations

import argparse
import base64
import http.client
import json
import os
import re
import shutil
import ssl
import subprocess
import sys
import tempfile
import time
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import duora_deploy
import duora_secrets

REPO_ROOT = Path(__file__).resolve().parents[2]
COMPOSE_FILE = REPO_ROOT / "infra" / "compose" / "compose.prod.yml"
REGISTRY_IMAGE = "registry:3@sha256:ddf754342cfc8acc51a56d5d0ab6af06826461864460636d8bd5c546dab2a7b8"
REGISTRY_CONTAINER = "duora-smoke-registry"
REGISTRY = "localhost:5050"
IMAGE_PREFIX = f"{REGISTRY}/duora/"
DOMAIN = "localhost"
EDGE_SUBNET = "172.30.10.0/24"  # mesmo valor de compose.prod.yml e docs/operations/deploy.md
BUILDS = {
    "web": ("infra/images/dotnet/Dockerfile", "web"),
    "worker": ("infra/images/dotnet/Dockerfile", "worker"),
    "migrator": ("infra/images/dotnet/Dockerfile", "migrator"),
    "edge": ("infra/images/edge/Dockerfile", None),
    "postgres": ("infra/images/postgres/Dockerfile", None),
}
LOGIN_PATH = "/api/v1/auth/login"
SIXLABORS_LICENSE_ENV = "SIXLABORS_LICENSE_KEY"
READY_TIMEOUT_SECONDS = 90
# Lista do repositório; o smoke confere que nenhuma delas falta no banco.
MIGRATIONS = sorted(path.name.removesuffix(".cs") for path in
                    (REPO_ROOT / "backend/src/Duora.Infrastructure/Persistence/Migrations").glob("*_*.cs")
                    if not path.name.endswith(".Designer.cs"))


class SmokeFailure(Exception):
    pass


@dataclass
class Response:
    status: int
    headers: dict[str, str]
    body: bytes
    set_cookies: list[str]


def main() -> int:
    parser = argparse.ArgumentParser(description="Smoke local de compose.prod.yml")
    parser.add_argument("--commit", required=True, help="SHA completo do commit sob teste")
    parser.add_argument("--keep", action="store_true", help="não derruba o ambiente no fim")
    args = parser.parse_args()

    workdir = Path(tempfile.mkdtemp(prefix="duora-smoke-"))
    host_env = workdir / "duora.env"
    compose = None
    media_helper_image: str | None = None
    failures: list[str] = []
    try:
        architecture = run(["docker", "version", "--format", "{{.Server.Arch}}"]).strip()
        start_registry()
        images = {name: build_and_push(name, architecture, args.commit, workdir) for name in BUILDS}
        check_license_absent(images["worker"])

        secrets_dir = workdir / "secrets"
        duora_secrets.init_secrets(secrets_dir, change_owner=False)
        duora_secrets.init_backup(secrets_dir, change_owner=False)
        # Repositório placeholder: o smoke não testa backup (deploy com --accept-missing-backup-check); o drill
        # scripts/backup/restore_drill.py testa. Sem destino válido o archive falha e o WAL fica no pg_wal.
        for name, content in (("pgbackrest_repo.conf", "[global]\nrepo1-s3-endpoint=s3.invalid\n"
                                                        "repo1-s3-bucket=smoke\nrepo1-s3-region=us-east-1\n"
                                                        "repo1-s3-key=smoke\nrepo1-s3-key-secret=smoke\n"),
                              ("pgbackrest_cipher.conf", "[global]\nrepo1-cipher-pass=smoke-synthetic\n")):
            (secrets_dir / name).write_text(content, encoding="utf-8")
        media_dir = workdir / "media"
        for area in ("quarantine", "derivatives"):
            directory = media_dir / area
            directory.mkdir(parents=True)
            # Marcador sintético, com o mesmo UID do role app_dirs (o modo das áreas fica abaixo).
            marker = directory / ".duora-media-area"
            marker.write_text("synthetic smoke mount\n", encoding="utf-8")

        media_helper_image = images["postgres"]
        # 0711 e não o 0700 do role: aqui o duora-deploy roda sem root e precisa achar o marcador no preflight.
        run(["docker", "run", "--rm", "--network", "none", "--user", "0:0",
             "--volume", f"{media_dir}:/media", "--entrypoint", "sh", media_helper_image, "-c",
             "chown -R 1654:1654 /media && chmod 0711 /media/quarantine /media/derivatives "
             "&& chmod 0400 /media/quarantine/.duora-media-area /media/derivatives/.duora-media-area"])
        host_env.write_text("\n".join([
            f"DUORA_IMAGE_PREFIX={IMAGE_PREFIX}",
            "DUORA_MIN_FREE_GIB=5",
            "DUORA_DISK_CHECK_PATH=/",
            f"DUORA_SECRETS_DIR={secrets_dir}",
            f"DUORA_DOMAIN={DOMAIN}",
            f"DUORA_MEDIA_DIR={media_dir}",
            "DUORA_MEDIA_REVIEW=",
            "DUORA_TLS=internal",
            "DUORA_HTTP_PORT=80",
            "DUORA_HTTPS_PORT=443",
        ]) + "\n", encoding="utf-8")
        manifest = workdir / "manifest.json"
        manifest.write_text(json.dumps(build_manifest(args.commit, architecture, images), indent=2), "utf-8")

        state_dir = workdir / "releases"
        compose = ["docker", "compose", "--file", str(COMPOSE_FILE), "--env-file", str(host_env),
                   "--env-file", str(state_dir / "releases" / "smoke-1.env")]
        exit_code = duora_deploy.main([
            "--manifest", str(manifest), "--compose-file", str(COMPOSE_FILE), "--host-env", str(host_env),
            "--state-dir", str(state_dir), "--accept-missing-backup-check",
        ])
        if exit_code != 0:
            raise SmokeFailure("duora-deploy falhou")

        failures = verify(compose, workdir)
    except SmokeFailure as error:
        failures.append(str(error))
    finally:
        if not args.keep:
            teardown(compose)
            if media_helper_image is not None:
                return_media_dir_to_user(workdir / "media", media_helper_image)
            shutil.rmtree(workdir, ignore_errors=True)

    for failure in failures:
        print(f"[smoke] FALHOU: {failure}")
    print(f"[smoke] {'OK' if not failures else f'{len(failures)} falha(s)'}")
    return 0 if not failures else 1


# --- Preparação -----------------------------------------------------------------------------------------------------

def start_registry() -> None:
    running = run(["docker", "ps", "--quiet", "--filter", f"name=^{REGISTRY_CONTAINER}$"]).strip()
    if not running:
        run(["docker", "run", "--detach", "--rm", "--name", REGISTRY_CONTAINER, "--publish", "127.0.0.1:5050:5000",
             REGISTRY_IMAGE])


def build_and_push(name: str, architecture: str, commit: str, workdir: Path) -> str:
    dockerfile, target = BUILDS[name]
    metadata = workdir / f"{name}.metadata.json"
    command = ["docker", "buildx", "build", "--file", dockerfile, "--platform", f"linux/{architecture}",
               "--build-arg", f"GIT_COMMIT={commit}", "--tag", f"{IMAGE_PREFIX}{name}:smoke", "--push",
               "--metadata-file", str(metadata), "--quiet"]
    if target is not None:
        command += ["--target", target]
    if name == "worker":
        # Licença da Six Labors (ADR-0025, deploy.md): secret do BuildKit lido da variável, nunca argumento nem arquivo.
        if not os.environ.get(SIXLABORS_LICENSE_ENV, "").strip():
            raise SystemExit(f"[smoke] defina {SIXLABORS_LICENSE_ENV} na sessão: o build Release do worker exige a "
                             "licença da Six Labors (docs/operations/deploy.md).")
        command += ["--secret", f"id=sixlabors_license,env={SIXLABORS_LICENSE_ENV}"]
    print(f"[smoke] build {name} (linux/{architecture})", flush=True)
    run(command + [str(REPO_ROOT)])
    digest = json.loads(metadata.read_text(encoding="utf-8"))["containerimage.digest"]
    return f"{IMAGE_PREFIX}{name}@{digest}"


def check_license_absent(image: str) -> None:
    """A licença entra só como secret do BuildKit: não pode ter ficado no histórico nem no filesystem do worker."""
    run(["docker", "pull", "--quiet", image])
    checker = REPO_ROOT / "scripts" / "supply-chain" / "check_image_secret.py"
    result = subprocess.run([sys.executable, str(checker), image, SIXLABORS_LICENSE_ENV], check=False)
    if result.returncode != 0:
        raise SmokeFailure(f"licença da Six Labors na imagem do worker (check_image_secret.py saiu com {result.returncode})")


def build_manifest(commit: str, architecture: str, images: dict[str, str]) -> dict:
    return {
        "schemaVersion": 1,
        "releaseId": "smoke-1",
        "commit": commit,
        "createdAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "architecture": architecture,
        "images": images,
        "contractVersion": "v1",
        "schema": {"min": MIGRATIONS[0], "max": MIGRATIONS[-1]},
        "migrations": MIGRATIONS,
        "sbom": {name: "pendente (smoke local)" for name in images},
        "gates": {"status": "passed", "runUrl": "local://smoke"},
        "previousReleaseId": None,
    }


# --- Verificações ---------------------------------------------------------------------------------------------------

def verify(compose: list[str], workdir: Path) -> list[str]:
    ca_file = workdir / "caddy-root.crt"
    ca_file.write_text(run(compose + ["exec", "--no-TTY", "edge", "cat",
                                      "/data/caddy/pki/authorities/local/root.crt"]), encoding="utf-8")
    context = ssl.create_default_context(cafile=str(ca_file))
    canary = f"CANARY-{uuid.uuid4()}"
    checks = Checks()

    index = get(context, "/", {"Accept": "text/html", "Sec-Fetch-Mode": "navigate"})
    checks.expect(index.status == 200 and b'<div id="root">' in index.body, "GET / serve o index.html da SPA")
    checks.expect(index.headers.get("cache-control") == "no-cache", "index.html com Cache-Control no-cache")
    csp = index.headers.get("content-security-policy", "")
    checks.expect("frame-ancestors 'none'" in csp and "unsafe-eval" not in csp and "wss://localhost" in csp,
                  f"CSP restritiva com wss (recebido: {csp!r})")
    checks.expect("server" not in index.headers, "sem cabeçalho Server")
    checks.expect("max-age=31536000" in index.headers.get("strict-transport-security", ""), "HSTS presente")

    asset_path = re.search(rb'src="(/assets/[^"]+\.js)"', index.body)
    checks.expect(asset_path is not None, "index.html referencia um asset com hash")
    if asset_path is not None:
        asset = get(context, asset_path.group(1).decode())
        checks.expect(asset.status == 200 and "javascript" in asset.headers.get("content-type", ""),
                      "asset com hash servido como JavaScript")
        checks.expect("immutable" in asset.headers.get("cache-control", ""), "asset com hash imutável")
    missing_asset = get(context, "/assets/nao-existe-123.js", {"Accept": "*/*"})
    checks.expect(missing_asset.status == 404 and b"<html" not in missing_asset.body.lower(),
                  f"asset inexistente = 404 sem HTML (status {missing_asset.status})")

    spa_route = get(context, "/salas/abc", {"Accept": "text/html", "Sec-Fetch-Mode": "navigate"})
    checks.expect(spa_route.status == 200 and b'<div id="root">' in spa_route.body, "rota da SPA cai no index")
    fetch_missing = get(context, "/salas/abc", {"Accept": "application/json", "Sec-Fetch-Mode": "cors"})
    checks.expect(fetch_missing.status == 404, f"fetch fora da SPA = 404 (status {fetch_missing.status})")

    api = get(context, f"/api/v1?token={canary}", {"Accept": "application/json", "Cookie": f"session={canary}",
                                                     "Authorization": f"Bearer {canary}"})
    checks.expect(api.status not in (502, 503, 504) and b"<div id=\"root\">" not in api.body,
                  f"/api/v1 respondido pelo web via proxy, sem HTML da SPA (status {api.status})")
    checks.expect(api.headers.get("cache-control") == "no-store", "API sem cache por padrão")

    # UUID canário no caminho (como o alvo de um bloqueio): o log não pode guardar quem foi bloqueado.
    path_canary = str(uuid.uuid4())
    get(context, f"/api/v1/me/blocks/{path_canary}", {"Accept": "application/json"})

    # Webhook de fornecedor chega ao web (sem fornecedor configurado, o backend responde 404 em Problem Details),
    # e corpo acima de 64 KiB morre na borda com 413.
    webhook = request(context, "POST", f"/webhooks/fake?signature={canary}", b"{}",
                      {"Content-Type": "application/json"})
    checks.expect(webhook.status not in (502, 503, 504) and b'<div id="root">' not in webhook.body,
                  f"/webhooks/* respondido pelo web via proxy, sem HTML da SPA (status {webhook.status})")
    oversized = request(context, "POST", "/webhooks/fake", b"a" * (64 * 1024 + 1),
                        {"Content-Type": "application/json"})
    checks.expect(oversized.status == 413, f"/webhooks/* com corpo acima de 64 KiB = 413 na borda (status {oversized.status})")

    health = get(context, "/health/ready", {"Accept": "*/*"})
    checks.expect(health.status == 404, f"/health fechado na borda (status {health.status})")

    # Sem sessão, o backend nega por padrão (política de fallback): 401 também prova que o upgrade chegou ao web.
    hub_status = websocket_upgrade(context, "/hubs/updates")
    checks.expect(hub_status in (101, 401, 404),
                  f"/hubs/updates com upgrade WebSocket chega ao web: 101, 401 ou 404 do backend (status {hub_status})")

    edge_logs = run(compose + ["logs", "--no-color", "edge"])
    checks.expect(canary not in edge_logs, "log do Caddy sem query string, cookie nem Authorization")
    checks.expect('"uri":"/api/v1?redacted"' in edge_logs, "log do Caddy registra a rota sem a query")
    checks.expect('"uri":"/webhooks/fake?redacted"' in edge_logs, "log do Caddy registra o webhook sem a query")
    checks.expect(path_canary not in edge_logs, "log do Caddy sem UUID no caminho")
    checks.expect('"uri":"/api/v1/me/blocks/{id}"' in edge_logs, "log do Caddy troca o UUID do caminho por {id}")

    history_update = subprocess.run(
        compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--host", "127.0.0.1", "--username",
                   "duora_app", "--dbname", "duora", "--command",
                   'UPDATE ops.__ef_migrations_history SET "ProductVersion" = \'x\''],
        capture_output=True, text=True, check=False)
    checks.expect(history_update.returncode != 0 and "permission denied" in history_update.stderr,
                  "duora_app não altera o histórico real de migrations")

    edge_network = json.loads(run(["docker", "network", "inspect", "duora_edge"]))[0]
    subnets = [config.get("Subnet") for config in edge_network["IPAM"]["Config"]]
    checks.expect(subnets == [EDGE_SUBNET], f"rede edge na subnet fixa {EDGE_SUBNET} (recebido {subnets})")

    check_hardening(compose, checks)
    # Por último e isolada: ela recria o web, e um erro aqui (rede, docker) não pode apagar as falhas de cima.
    try:
        check_key_ring_survives_recreation(compose, context, checks)
    except (SmokeFailure, OSError, http.client.HTTPException) as error:
        checks.expect(False, f"key ring sobrevive à recriação do web: {type(error).__name__}: {error}")
    return checks.failures


def check_hardening(compose: list[str], checks: Checks) -> None:
    for service, user in (("web", "1654:1654"), ("worker", "1654:1654"), ("edge", "10001:10001"),
                          ("backup", "999:999")):
        container = run(compose + ["ps", "--quiet", service]).strip()
        info = json.loads(run(["docker", "inspect", container]))[0]["HostConfig"]
        config = json.loads(run(["docker", "inspect", container]))[0]["Config"]
        checks.expect(info["ReadonlyRootfs"], f"{service}: filesystem só leitura")
        checks.expect(info["CapDrop"] == ["ALL"], f"{service}: cap_drop ALL")
        checks.expect("no-new-privileges:true" in info["SecurityOpt"], f"{service}: no-new-privileges")
        checks.expect(config["User"] == user, f"{service}: usuário {user} (recebido {config['User']!r})")
        published = {port for port, bindings in (info.get("PortBindings") or {}).items() if bindings}
        expected_ports = {"80/tcp", "443/tcp", "443/udp"} if service == "edge" else set()
        checks.expect(published == expected_ports, f"{service}: portas publicadas {sorted(published)}")
    postgres = run(compose + ["ps", "--quiet", "postgres"]).strip()
    bindings = json.loads(run(["docker", "inspect", postgres]))[0]["HostConfig"].get("PortBindings") or {}
    checks.expect(not any(bindings.values()), "postgres sem portas publicadas")


def check_key_ring_survives_recreation(compose: list[str], context: ssl.SSLContext, checks: Checks) -> None:
    """T-15: o par antiforgery é protegido pelo Data Protection. Válido depois de recriar o web = chave no volume.

    Login com credencial inexistente: 401 significa que o token passou; 403 seria o token ilegível (chave perdida).
    O token forjado prova que a checagem distingue os dois casos.
    """
    cookies, token = csrf_pair(context)
    checks.expect(login_status(context, cookies, token) == 401, "token antiforgery válido antes de recriar o web (401)")
    keys = key_ring_files(compose)
    checks.expect("EncryptedData" in keys and "<value>" not in keys,
                  "key ring no volume cifrado com o certificado (spec 11.2), sem chave em claro")
    checks.expect(login_status(context, cookies, "CfDJ8-forjado") == 403, "token antiforgery forjado = 403")

    before = run(compose + ["ps", "--quiet", "web"]).strip()
    # --renew-anon-volumes: um volume anônimo no lugar do nomeado seria reaproveitado e mascararia a perda.
    run(compose + ["up", "--detach", "--no-deps", "--force-recreate", "--renew-anon-volumes", "web"])
    after = run(compose + ["ps", "--quiet", "web"]).strip()
    checks.expect(bool(after) and after != before, "web recriado (container novo)")
    wait_ready(compose)

    status = login_status(context, cookies, token)
    checks.expect(status == 401, f"token emitido antes da recriação continua válido: key ring no volume (status {status})")


def key_ring_files(compose: list[str]) -> str:
    """Conteúdo das chaves no volume, lido por um container descartável: a imagem do web não tem shell."""
    postgres = run(compose + ["ps", "--quiet", "postgres"]).strip()
    image = json.loads(run(["docker", "inspect", postgres]))[0]["Config"]["Image"]
    return run(["docker", "run", "--rm", "--user", "1654:1654", "--network", "none",
                "--volume", "duora_dataprotection:/keys:ro", "--entrypoint", "sh", image,
                "-c", "cat /keys/key-*.xml"])


def csrf_pair(context: ssl.SSLContext) -> tuple[str, str]:
    response = request(context, "GET", "/api/v1/auth/csrf")
    pairs = [header.split(";", 1)[0] for header in response.set_cookies]
    token = next((pair.split("=", 1)[1] for pair in pairs if pair.startswith("XSRF-TOKEN=")), None)
    if response.status != 204 or token is None:
        raise SmokeFailure(f"GET /api/v1/auth/csrf sem XSRF-TOKEN (status {response.status})")
    return "; ".join(pairs), token


def login_status(context: ssl.SSLContext, cookies: str, token: str) -> int:
    body = json.dumps({"email": f"smoke-{uuid.uuid4()}@example.com", "password": "frase que nao existe"})
    return request(context, "POST", LOGIN_PATH, body.encode(), {
        "Content-Type": "application/json", "Cookie": cookies, "X-XSRF-TOKEN": token,
        "Origin": f"https://{DOMAIN}", "Sec-Fetch-Site": "same-origin",
    }).status


def wait_ready(compose: list[str]) -> None:
    probe = compose + ["exec", "--no-TTY", "edge", "wget", "--quiet", "--spider", "--timeout=5",
                       "http://web:8080/health/ready"]
    deadline = time.monotonic() + READY_TIMEOUT_SECONDS
    while time.monotonic() < deadline:
        if subprocess.run(probe, capture_output=True, check=False).returncode == 0:
            return
        time.sleep(2)
    raise SmokeFailure(f"web recriado não ficou pronto em {READY_TIMEOUT_SECONDS} s")


class Checks:
    def __init__(self) -> None:
        self.failures: list[str] = []

    def expect(self, condition: bool, description: str) -> None:
        print(f"[smoke] {'ok   ' if condition else 'FALHA'} {description}", flush=True)
        if not condition:
            self.failures.append(description)


def get(context: ssl.SSLContext, path: str, headers: dict[str, str] | None = None) -> Response:
    return request(context, "GET", path, None, headers)


def request(context: ssl.SSLContext, method: str, path: str, body: bytes | None = None,
            headers: dict[str, str] | None = None) -> Response:
    connection = http.client.HTTPSConnection(DOMAIN, 443, context=context, timeout=10)
    try:
        connection.request(method, path, body=body, headers=headers or {})
        response = connection.getresponse()
        return Response(response.status, {k.lower(): v for k, v in response.getheaders()}, response.read(),
                        response.headers.get_all("Set-Cookie") or [])
    finally:
        connection.close()


def websocket_upgrade(context: ssl.SSLContext, path: str) -> int:
    connection = http.client.HTTPSConnection(DOMAIN, 443, context=context, timeout=10)
    try:
        connection.request("GET", path, headers={
            "Connection": "Upgrade",
            "Upgrade": "websocket",
            "Sec-WebSocket-Version": "13",
            # RFC 6455: nonce de 16 bytes aleatórios em base64, novo a cada handshake.
            "Sec-WebSocket-Key": base64.b64encode(os.urandom(16)).decode("ascii"),
            "Origin": f"https://{DOMAIN}",
        })
        return connection.getresponse().status
    finally:
        connection.close()


# --- Infra ----------------------------------------------------------------------------------------------------------

def run(command: list[str]) -> str:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise SmokeFailure(f"{' '.join(command[:4])} ... saiu com {result.returncode}: {result.stderr.strip()[-500:]}")
    return result.stdout


def return_media_dir_to_user(media_dir: Path, helper_image: str) -> None:
    """Devolve o diretório sintético (dono 1654) ao usuário do smoke para a limpeza local.

    Roda dentro do finally: uma falha aqui é registrada e não substitui o resultado do smoke nem interrompe o resto da
    limpeza.
    """
    command = ["docker", "run", "--rm", "--network", "none", "--user", "0:0", "--volume", f"{media_dir}:/media",
               "--entrypoint", "chown", helper_image, "-R", f"{os.getuid()}:{os.getgid()}", "/media"]
    try:
        result = subprocess.run(command, capture_output=True, text=True, check=False)
    except OSError as error:
        print(f"[smoke] AVISO: não devolvi {media_dir} ao usuário ({error}); apague-o com sudo", file=sys.stderr)
        return
    if result.returncode != 0:
        print(f"[smoke] AVISO: não devolvi {media_dir} ao usuário (chown saiu com {result.returncode}: "
              f"{result.stderr.strip()[-500:]}); apague-o com sudo", file=sys.stderr)


def teardown(compose: list[str] | None) -> None:
    if compose is not None:
        subprocess.run(compose + ["--profile", "migrate", "down", "--volumes", "--remove-orphans"], check=False)
    subprocess.run(["docker", "rm", "--force", REGISTRY_CONTAINER], check=False, capture_output=True)


if __name__ == "__main__":
    sys.exit(main())
