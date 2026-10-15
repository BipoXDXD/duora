#!/usr/bin/env python3
"""Gera, uma única vez, os arquivos de segredo que o compose.prod.yml monta (spec 15.4).

    sudo duora-secrets init /etc/duora/secrets

Senhas aleatórias e connection strings coerentes com elas. Nunca sobrescreve: com tudo presente não faz nada; com
o diretório incompleto, recusa (gerar só uma parte deixaria senha e connection string divergentes). Rotação é
procedimento manual (docs/operations/deploy.md). Rodando como root, cada arquivo fica 0400 com dono = UID do único
container que o lê, porque o Compose monta o arquivo do host como está (sem uid/gid/mode fora do Swarm).
"""

from __future__ import annotations

import argparse
import os
import secrets
import sys
from pathlib import Path

DESCRIPTION = "Gera os arquivos de segredo do compose.prod.yml (spec 15.4)."
POSTGRES_UID = 999  # usuário postgres da imagem oficial
APP_UID = 1654  # usuário app das imagens .NET (web, worker, migrator)

SECRET_OWNERS = {
    "postgres_superuser_password": POSTGRES_UID,
    "db_migrator_password": POSTGRES_UID,
    "db_app_password": POSTGRES_UID,
    "duora_app_connection": APP_UID,
    "duora_migrator_connection": APP_UID,
}

PASSWORD_BYTES = 32  # token_urlsafe → 43 caracteres, só [A-Za-z0-9_-]: seguro dentro da connection string
DATABASE_HOST = "postgres"
DATABASE_NAME = "duora"


class SecretsError(Exception):
    """Estado do diretório que exige decisão humana."""


def init_secrets(directory: Path, *, change_owner: bool) -> bool:
    """Cria todos os segredos se nenhum existe. Devolve True se criou, False se já estavam lá."""
    existing = {path.name for path in directory.iterdir()} if directory.exists() else set()
    expected = set(SECRET_OWNERS)
    if expected <= existing:
        return False
    if existing & expected:
        missing = ", ".join(sorted(expected - existing))
        raise SecretsError(f"diretório incompleto ({directory}); faltam: {missing}. Restaure do backup ou "
                           "recrie tudo junto com o banco, nunca só uma parte.")

    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    directory.chmod(0o700)
    for name, content in _generate().items():
        _write_secret(directory / name, content, owner=SECRET_OWNERS[name] if change_owner else None)
    return True


def _generate() -> dict[str, str]:
    superuser = secrets.token_urlsafe(PASSWORD_BYTES)
    migrator = secrets.token_urlsafe(PASSWORD_BYTES)
    app = secrets.token_urlsafe(PASSWORD_BYTES)
    return {
        "postgres_superuser_password": superuser,
        "db_migrator_password": migrator,
        "db_app_password": app,
        "duora_app_connection": _connection_string("duora_app", app),
        "duora_migrator_connection": _connection_string("duora_migrator", migrator),
    }


def _connection_string(username: str, password: str) -> str:
    # GSS desligado: a conexão é por senha (SCRAM) e as imagens não trazem a libgssapi que o Npgsql tentaria carregar.
    return (f"Host={DATABASE_HOST};Port=5432;Database={DATABASE_NAME};Username={username};Password={password};"
            "Gss Encryption Mode=Disable;")


def _write_secret(path: Path, content: str, *, owner: int | None) -> None:
    # O_EXCL: nunca sobrescreve; 0400 desde a criação, sem janela com permissão aberta.
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o400)
    with os.fdopen(descriptor, "w", encoding="utf-8") as file:
        file.write(content)
    if owner is not None:
        os.chown(path, owner, owner)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="duora-secrets", description=DESCRIPTION)
    subcommands = parser.add_subparsers(dest="command", required=True)
    init = subcommands.add_parser("init", help="cria os segredos que faltam (todos ou nenhum)")
    init.add_argument("directory", type=Path)
    init.add_argument("--no-chown", action="store_true",
                      help="não troca o dono dos arquivos (smoke local sem root)")
    args = parser.parse_args(argv)

    change_owner = not args.no_chown
    if change_owner and os.geteuid() != 0:
        print("duora-secrets: rode como root ou use --no-chown", file=sys.stderr)
        return 1
    try:
        created = init_secrets(args.directory, change_owner=change_owner)
    except SecretsError as error:
        print(f"duora-secrets: {error}", file=sys.stderr)
        return 1
    print(f"duora-secrets: {'segredos criados' if created else 'segredos já existiam'} em {args.directory}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
