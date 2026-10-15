#!/usr/bin/env python3
"""Smoke local da release: o mesmo caminho da VPS, numa máquina com Docker (docs/operations/deploy.md).

    python3 scripts/deploy/smoke_local.py --commit "$(git rev-parse HEAD)"

1. Sobe um registry descartável em 127.0.0.1:5050 e publica as cinco imagens para a arquitetura do Docker local.
2. Gera segredos de teste num diretório temporário (fora do git) e um manifesto com os digests publicados.
3. Roda o duora-deploy de verdade (pull por digest, migrator, up, readiness), com Caddy em "tls internal".
4. Confere pela borda HTTPS: SPA, cache, CSP, 404 de assets, fallback só para navegação, /api e /hubs (upgrade
   WebSocket) chegando ao web, /health fechado, logs sem query/cookie, e o endurecimento dos containers.
5. Derruba tudo com "down -v" e remove o registry (a menos que --keep).

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
BUILDS = {
    "web": ("infra/images/dotnet/Dockerfile", "web"),
    "worker": ("infra/images/dotnet/Dockerfile", "worker"),
    "migrator": ("infra/images/dotnet/Dockerfile", "migrator"),
    "edge": ("infra/images/edge/Dockerfile", None),
    "postgres": ("infra/images/postgres/Dockerfile", None),
}
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


def main() -> int:
    parser = argparse.ArgumentParser(description="Smoke local de compose.prod.yml")
    parser.add_argument("--commit", required=True, help="SHA completo do commit sob teste")
    parser.add_argument("--keep", action="store_true", help="não derruba o ambiente no fim")
    args = parser.parse_args()

    workdir = Path(tempfile.mkdtemp(prefix="duora-smoke-"))
    host_env = workdir / "duora.env"
    compose = None
    failures: list[str] = []
    try:
        architecture = run(["docker", "version", "--format", "{{.Server.Arch}}"]).strip()
        start_registry()
        images = {name: build_and_push(name, architecture, args.commit, workdir) for name in BUILDS}

        secrets_dir = workdir / "secrets"
        duora_secrets.init_secrets(secrets_dir, change_owner=False)
        host_env.write_text("\n".join([
            f"DUORA_IMAGE_PREFIX={IMAGE_PREFIX}",
            "DUORA_MIN_FREE_GIB=5",
            "DUORA_DISK_CHECK_PATH=/",
            f"DUORA_SECRETS_DIR={secrets_dir}",
            f"DUORA_DOMAIN={DOMAIN}",
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
    print(f"[smoke] build {name} (linux/{architecture})", flush=True)
    run(command + [str(REPO_ROOT)])
    digest = json.loads(metadata.read_text(encoding="utf-8"))["containerimage.digest"]
    return f"{IMAGE_PREFIX}{name}@{digest}"


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

    health = get(context, "/health/ready", {"Accept": "*/*"})
    checks.expect(health.status == 404, f"/health fechado na borda (status {health.status})")

    hub_status = websocket_upgrade(context, "/hubs/updates")
    checks.expect(hub_status in (101, 404),
                  f"/hubs/updates com upgrade WebSocket chega ao web: 101 ou 404 do backend (status {hub_status})")

    edge_logs = run(compose + ["logs", "--no-color", "edge"])
    checks.expect(canary not in edge_logs, "log do Caddy sem query string, cookie nem Authorization")
    checks.expect('"uri":"/api/v1?redacted"' in edge_logs, "log do Caddy registra a rota sem a query")

    history_update = subprocess.run(
        compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--host", "127.0.0.1", "--username",
                   "duora_app", "--dbname", "duora", "--command",
                   'UPDATE ops.__ef_migrations_history SET "ProductVersion" = \'x\''],
        capture_output=True, text=True, check=False)
    checks.expect(history_update.returncode != 0 and "permission denied" in history_update.stderr,
                  "duora_app não altera o histórico real de migrations")

    check_hardening(compose, checks)
    return checks.failures


def check_hardening(compose: list[str], checks: Checks) -> None:
    for service, user in (("web", "1654:1654"), ("worker", "1654:1654"), ("edge", "10001:10001")):
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


class Checks:
    def __init__(self) -> None:
        self.failures: list[str] = []

    def expect(self, condition: bool, description: str) -> None:
        print(f"[smoke] {'ok   ' if condition else 'FALHA'} {description}", flush=True)
        if not condition:
            self.failures.append(description)


def get(context: ssl.SSLContext, path: str, headers: dict[str, str] | None = None) -> Response:
    connection = http.client.HTTPSConnection(DOMAIN, 443, context=context, timeout=10)
    try:
        connection.request("GET", path, headers=headers or {})
        response = connection.getresponse()
        return Response(response.status, {k.lower(): v for k, v in response.getheaders()}, response.read())
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


def teardown(compose: list[str] | None) -> None:
    if compose is not None:
        subprocess.run(compose + ["--profile", "migrate", "down", "--volumes", "--remove-orphans"], check=False)
    subprocess.run(["docker", "rm", "--force", REGISTRY_CONTAINER], check=False, capture_output=True)


if __name__ == "__main__":
    sys.exit(main())
