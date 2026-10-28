#!/usr/bin/env python3
"""Liga (ou desliga) a selfie da issue #93 no Compose efetivo do Coolify.

A produção não usa o compose.prod.yml do repositório: o observador do Coolify chama o duora-deploy com o
`/opt/duora/compose.coolify.json` fixado por hash em `/etc/duora/coolify-deploy.json`. Este script só transforma
o JSON e escreve o resultado no stdout (o hash vai para o stderr); instalar o arquivo e atualizar o pin são passos
administrativos revisados (docs/operations/deploy.md, "Selfie de presença no Coolify").

    python3 -I selfie_compose.py enable /opt/duora/compose.coolify.json > compose.coolify.selfie.json
    python3 -I selfie_compose.py disable /opt/duora/compose.coolify.json > compose.coolify.sem-selfie.json

Cada serviço recebe só o par do seu usuário IAM: o web assume o papel de captura (sts:AssumeRole); o worker cria a
sessão, lê o resultado e roda DetectFaces. Idempotente; recusa formato inesperado ou valor conflitante.
"""

from __future__ import annotations

import copy
import hashlib
import json
import sys
from pathlib import Path

ROLE_ARN = "arn:aws:iam::394877252106:role/duora-selfie-capture"
ANCHOR_SECRET = "duora_app_connection"
FORBIDDEN_KEYS = ("AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN")
TARGETS = {"access_key_id": "SelfieVerification__AwsAccessKeyId",
           "secret_access_key": "SelfieVerification__AwsSecretAccessKey"}
COMMON_ENVIRONMENT = {"SelfieVerification__Enabled": "true", "SelfieVerification__PrivacyApproved": "true",
                      "SelfieVerification__Region": "us-east-1"}
ENVIRONMENT = {"web": {**COMMON_ENVIRONMENT, "SelfieVerification__CaptureRoleArn": ROLE_ARN},
               "worker": COMMON_ENVIRONMENT}


class ComposeError(ValueError):
    """Compose fora do formato revisado: nada é escrito."""


def enable(compose: dict) -> dict:
    result = copy.deepcopy(compose)
    directory = _secret_directory(result)
    for service in ENVIRONMENT:
        block = _service(result, service)
        environment = _environment(block)
        for key, value in ENVIRONMENT[service].items():
            if environment.setdefault(key, value) != value:
                raise ComposeError(f"{service}: {key} já tem outro valor")
        mounted = block.setdefault("secrets", [])
        if not isinstance(mounted, list):
            raise ComposeError(f"{service}: secrets precisa ser lista")
        for source, target in _pairs(service):
            entry = {"source": source, "target": target}
            if any(isinstance(item, dict) and item.get("target") == target and item != entry for item in mounted):
                raise ComposeError(f"{service}: {target} já vem de outro segredo")
            if entry not in mounted:
                mounted.append(entry)
    for service in ENVIRONMENT:
        for source, _ in _pairs(service):
            declared = {"file": directory + source.replace("_", "-")}
            if result["secrets"].setdefault(source, declared) != declared:
                raise ComposeError(f"segredo {source} já declarado com outro arquivo")
    return result


def disable(compose: dict) -> dict:
    """Desfaz exatamente o que enable acrescenta; o resto fica intacto."""
    result = copy.deepcopy(compose)
    for service in ENVIRONMENT:
        block = _service(result, service)
        environment = _environment(block)
        for key in ENVIRONMENT[service]:
            environment.pop(key, None)
        entries = [{"source": source, "target": target} for source, target in _pairs(service)]
        if "secrets" in block:
            block["secrets"] = [item for item in block["secrets"] if item not in entries]
    for service in ENVIRONMENT:
        for source, _ in _pairs(service):
            result.get("secrets", {}).pop(source, None)
    return result


def _pairs(service: str) -> list[tuple[str, str]]:
    return [(f"selfie_{service}_aws_{part}", target) for part, target in TARGETS.items()]


def _service(compose: dict, name: str) -> dict:
    services = compose.get("services")
    if not isinstance(services, dict) or not isinstance(services.get(name), dict):
        raise ComposeError(f"serviço {name} ausente")
    return services[name]


def _environment(block: dict) -> dict:
    environment = block.setdefault("environment", {})
    if not isinstance(environment, dict):
        raise ComposeError("environment precisa ser objeto (saída de docker compose config --format json)")
    if any(key in environment for key in FORBIDDEN_KEYS):
        raise ComposeError("credencial AWS em texto no environment")
    return environment


def _secret_directory(compose: dict) -> str:
    """Diretório dos segredos já montados: os pares novos ficam ao lado deles, na mesma forma."""
    secrets = compose.get("secrets")
    anchor = secrets.get(ANCHOR_SECRET) if isinstance(secrets, dict) else None
    path = anchor.get("file") if isinstance(anchor, dict) else None
    if not isinstance(path, str) or not path.endswith("/" + ANCHOR_SECRET):
        raise ComposeError(f"segredo {ANCHOR_SECRET} sem arquivo conhecido")
    return path.removesuffix(ANCHOR_SECRET)


def main(argv: list[str]) -> int:
    if len(argv) != 2 or argv[0] not in {"enable", "disable"}:
        print("uso: selfie_compose.py enable|disable <compose.coolify.json>", file=sys.stderr)
        return 2
    try:
        compose = json.loads(Path(argv[1]).read_text(encoding="utf-8"))
        if not isinstance(compose, dict):
            raise ComposeError("Compose precisa ser objeto JSON")
        output = json.dumps((enable if argv[0] == "enable" else disable)(compose), indent=2) + "\n"
    except (OSError, ValueError) as error:
        print(f"selfie_compose: {error}", file=sys.stderr)
        return 1
    sys.stdout.write(output)
    print(f"sha256 {hashlib.sha256(output.encode('utf-8')).hexdigest()}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
