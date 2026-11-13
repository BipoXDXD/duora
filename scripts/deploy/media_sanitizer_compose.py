#!/usr/bin/env python3
"""Liga (ou desliga) a ligação do Worker ao launcher do media-sanitizer (#174, ADR-0036) no Compose do Coolify.

Opt-in e inerte por padrão: o compose.prod.yml do repositório não tem nada disto, e a produção só muda quando um
operador gera este JSON, revisa a diferença, instala o arquivo e atualiza o pin `compose_sha256` (nova selagem), como
no patch da selfie (docs/operations/media-sanitizer-host.md, "Ligação do Worker"). Este script só transforma o JSON e
escreve o resultado no stdout (o hash vai para o stderr).

    python3 -I media_sanitizer_compose.py enable --launcher-uid <uid> --client-gid <gid> \\
        --profile-sha256 <pin> /opt/duora/compose.coolify.json > compose.coolify.sanitizer.json
    python3 -I media_sanitizer_compose.py disable /opt/duora/compose.coolify.json > compose.coolify.sem-sanitizer.json

Só o worker recebe: o grupo do socket de controle (`group_add`), o diretório do socket montado somente leitura e as
três chaves `Media__Processing__Sanitizer__*`. O pin do perfil é o digest canônico que o role imprime e que já cobre a
imagem por digest; o upload continua governado só por DUORA_MEDIA_REVIEW. Idempotente; recusa formato inesperado ou
valor conflitante.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
import sys
from pathlib import Path

SERVICE = "worker"
WORKER_UID = 1654
SOCKET_DIRECTORY = "/run/duora-media"
CONTROL_SOCKET = SOCKET_DIRECTORY + "/control.sock"
PREFIX = "Media__Processing__Sanitizer__"
MOUNT = {"type": "bind", "source": SOCKET_DIRECTORY, "target": SOCKET_DIRECTORY, "read_only": True,
         "bind": {"create_host_path": False}}
_PROFILE = re.compile(r"[0-9a-f]{64}")
_FORBIDDEN_SOURCES = ("docker.sock", "/run/user")


class ComposeError(ValueError):
    """Compose ou identidade fora do formato revisado: nada é escrito."""


def enable(compose: dict, *, launcher_uid: int, client_gid: int, profile_sha256: str) -> dict:
    environment_values = _environment_values(launcher_uid, client_gid, profile_sha256)
    result = copy.deepcopy(compose)
    _refuse_socket_outside_worker(result)
    worker = _service(result, SERVICE)
    environment = _environment(worker)
    for key, value in environment_values.items():
        if environment.setdefault(key, value) != value:
            raise ComposeError(f"{SERVICE}: {key} já tem outro valor")
    groups = worker.setdefault("group_add", [])
    if groups not in ([], [str(client_gid)]):
        raise ComposeError(f"{SERVICE}: group_add já tem outro grupo")
    worker["group_add"] = [str(client_gid)]
    volumes = _volumes(worker)
    if MOUNT not in volumes:
        volumes.append(copy.deepcopy(MOUNT))
    return result


def disable(compose: dict) -> dict:
    """Desfaz exatamente o que enable acrescenta; o resto fica intacto."""
    result = copy.deepcopy(compose)
    worker = _service(result, SERVICE)
    environment = _environment(worker)
    enabled = PREFIX + "LauncherSocket" in environment
    for key in [key for key in environment if key.startswith(PREFIX)]:
        del environment[key]
    if enabled and "group_add" in worker:
        del worker["group_add"]
    if "volumes" in worker:
        worker["volumes"] = [item for item in _volumes(worker) if item != MOUNT]
    return result


def _environment_values(launcher_uid: int, client_gid: int, profile_sha256: str) -> dict[str, str]:
    for name, value in (("launcher-uid", launcher_uid), ("client-gid", client_gid)):
        if type(value) is not int or value <= 0:
            raise ComposeError(f"{name} precisa ser inteiro positivo (nunca root)")
    if launcher_uid == WORKER_UID:
        raise ComposeError("launcher-uid não pode ser o UID do Worker")
    if not isinstance(profile_sha256, str) or _PROFILE.fullmatch(profile_sha256) is None:
        raise ComposeError("profile-sha256 precisa de 64 dígitos hexadecimais minúsculos")
    return {PREFIX + "LauncherSocket": CONTROL_SOCKET, PREFIX + "LauncherUid": str(launcher_uid),
            PREFIX + "ProfileSha256": profile_sha256}


def _refuse_socket_outside_worker(compose: dict) -> None:
    services = compose.get("services")
    if not isinstance(services, dict):
        raise ComposeError("services ausente")
    for name, block in services.items():
        for item in block.get("volumes", []) if isinstance(block, dict) else []:
            source = str(item.get("source", "")) if isinstance(item, dict) else str(item)
            target = str(item.get("target", "")) if isinstance(item, dict) else str(item)
            if any(marker in source for marker in _FORBIDDEN_SOURCES):
                raise ComposeError(f"{name}: montagem de socket de runtime ({source}) não é aceita")
            if name != SERVICE and SOCKET_DIRECTORY in (source, target):
                raise ComposeError(f"{name}: só o worker pode montar {SOCKET_DIRECTORY}")


def _service(compose: dict, name: str) -> dict:
    services = compose.get("services")
    if not isinstance(services, dict) or not isinstance(services.get(name), dict):
        raise ComposeError(f"serviço {name} ausente")
    return services[name]


def _environment(block: dict) -> dict:
    environment = block.setdefault("environment", {})
    if not isinstance(environment, dict):
        raise ComposeError("environment precisa ser objeto (saída de docker compose config --format json)")
    return environment


def _volumes(block: dict) -> list:
    volumes = block.setdefault("volumes", [])
    if not isinstance(volumes, list) or not all(isinstance(item, dict) for item in volumes):
        raise ComposeError("volumes precisa ser lista de objetos (saída de docker compose config --format json)")
    if any(item.get("target") == SOCKET_DIRECTORY and item != MOUNT for item in volumes):
        raise ComposeError(f"{SOCKET_DIRECTORY} já vem de outra montagem")
    return volumes


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="ligação opt-in do Worker ao media-sanitizer (#174)")
    commands = parser.add_subparsers(dest="command", required=True)
    on = commands.add_parser("enable")
    on.add_argument("--launcher-uid", type=int, required=True)
    on.add_argument("--client-gid", type=int, required=True)
    on.add_argument("--profile-sha256", required=True)
    on.add_argument("compose", type=Path)
    off = commands.add_parser("disable")
    off.add_argument("compose", type=Path)
    args = parser.parse_args(argv)
    try:
        compose = json.loads(args.compose.read_text(encoding="utf-8"))
        if not isinstance(compose, dict):
            raise ComposeError("Compose precisa ser objeto JSON")
        if args.command == "enable":
            patched = enable(compose, launcher_uid=args.launcher_uid, client_gid=args.client_gid,
                             profile_sha256=args.profile_sha256)
        else:
            patched = disable(compose)
        output = json.dumps(patched, indent=2) + "\n"
    except (OSError, ValueError) as error:
        print(f"media_sanitizer_compose: {error}", file=sys.stderr)
        return 1
    sys.stdout.write(output)
    print(f"sha256 {hashlib.sha256(output.encode('utf-8')).hexdigest()}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
