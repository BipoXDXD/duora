#!/usr/bin/env python3
"""Gera, uma única vez, os arquivos de segredo que o compose.prod.yml monta (spec 15.4).

    sudo duora-secrets init /etc/duora/secrets
    sudo duora-secrets init-backup-tls /etc/duora/secrets

`init-backup-tls` cria a CA própria do pgBackRest e os certificados do servidor TLS do container backup (CN e SAN
"backup") e do cliente do postgres (CN "duora-postgres"), com o openssl do sistema. A chave da CA é descartada:
rotação = gerar o conjunto inteiro de novo (docs/operations/backup-restore.md).

Senhas aleatórias e connection strings coerentes com elas. Nunca sobrescreve: com tudo presente não faz nada; com
o diretório incompleto, recusa (gerar só uma parte deixaria senha e connection string divergentes). Rotação é
procedimento manual (docs/operations/deploy.md). Rodando como root, cada arquivo fica 0400 com dono = UID do único
container que o lê, porque o Compose monta o arquivo do host como está (sem uid/gid/mode fora do Swarm).
"""

from __future__ import annotations

import argparse
import os
import secrets
import subprocess
import sys
import tempfile
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

# Certificados do pgBackRest: todos lidos pelo usuário postgres (999), seja no container postgres ou no backup.
TLS_FILES = (
    "pgbackrest_tls_ca.crt",
    "pgbackrest_tls_server.crt",
    "pgbackrest_tls_server.key",
    "pgbackrest_tls_client.crt",
    "pgbackrest_tls_client.key",
)
TLS_SERVER_HOST = "backup"  # nome do serviço no compose.prod.yml; o postgres confere o certificado por ele
TLS_CLIENT_CN = "duora-postgres"  # autorizado em tls-server-auth (pgbackrest-repo.conf)
TLS_CA_DAYS = 3650
TLS_LEAF_DAYS = 825

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


def init_backup_tls(directory: Path, *, change_owner: bool) -> bool:
    """Cria CA, certificado do servidor backup e do cliente postgres se nenhum existe; True se criou."""
    existing = {path.name for path in directory.iterdir()} if directory.exists() else set()
    expected: set[str] = set(TLS_FILES)
    if expected <= existing:
        return False
    if existing & expected:
        missing = ", ".join(sorted(expected - existing))
        raise SecretsError(f"conjunto TLS incompleto ({directory}); faltam: {missing}. Apague os arquivos "
                           "pgbackrest_tls_* e gere todos de novo (o postgres e o backup precisam ser recriados).")

    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    directory.chmod(0o700)
    # A chave da CA só existe neste diretório temporário: ninguém emite certificado novo com ela depois.
    with tempfile.TemporaryDirectory(prefix="duora-tls-") as scratch:
        generated = _generate_tls(Path(scratch))
    for name in TLS_FILES:
        _write_secret(directory / name, generated[name], owner=POSTGRES_UID if change_owner else None)
    return True


def _generate_tls(scratch: Path) -> dict[str, str]:
    ca_key, ca_cert = scratch / "ca.key", scratch / "ca.crt"
    _openssl("req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256", "-nodes",
             "-days", str(TLS_CA_DAYS), "-subj", "/CN=duora-pgbackrest-ca",
             "-addext", "basicConstraints=critical,CA:TRUE", "-addext", "keyUsage=critical,keyCertSign,cRLSign",
             "-keyout", str(ca_key), "-out", str(ca_cert))
    files = {"pgbackrest_tls_ca.crt": ca_cert.read_text(encoding="utf-8")}
    leaves = {
        "server": (TLS_SERVER_HOST, f"subjectAltName=DNS:{TLS_SERVER_HOST}\nextendedKeyUsage=serverAuth"),
        "client": (TLS_CLIENT_CN, "extendedKeyUsage=clientAuth"),
    }
    for role, (common_name, extensions) in leaves.items():
        key, request, cert, ext = (scratch / f"{role}.{suffix}" for suffix in ("key", "csr", "crt", "ext"))
        ext.write_text(f"{extensions}\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\n",
                       encoding="utf-8")
        _openssl("req", "-new", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256", "-nodes",
                 "-subj", f"/CN={common_name}", "-keyout", str(key), "-out", str(request))
        _openssl("x509", "-req", "-in", str(request), "-CA", str(ca_cert), "-CAkey", str(ca_key),
                 "-days", str(TLS_LEAF_DAYS), "-extfile", str(ext), "-out", str(cert))
        files[f"pgbackrest_tls_{role}.crt"] = cert.read_text(encoding="utf-8")
        files[f"pgbackrest_tls_{role}.key"] = key.read_text(encoding="utf-8")
    return files


def _openssl(*args: str) -> None:
    result = subprocess.run(["openssl", *args], capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise SecretsError(f"openssl {args[0]} falhou: {result.stderr.strip()[-500:]}")


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
    for name, help_text in (("init", "cria os segredos do banco (todos ou nenhum)"),
                            ("init-backup-tls", "cria CA e certificados TLS do pgBackRest (todos ou nenhum)")):
        command = subcommands.add_parser(name, help=help_text)
        command.add_argument("directory", type=Path)
        command.add_argument("--no-chown", action="store_true",
                             help="não troca o dono dos arquivos (smoke local sem root)")
    args = parser.parse_args(argv)

    change_owner = not args.no_chown
    if change_owner and os.geteuid() != 0:
        print("duora-secrets: rode como root ou use --no-chown", file=sys.stderr)
        return 1
    try:
        initialize = init_secrets if args.command == "init" else init_backup_tls
        created = initialize(args.directory, change_owner=change_owner)
    except SecretsError as error:
        print(f"duora-secrets: {error}", file=sys.stderr)
        return 1
    print(f"duora-secrets: {'segredos criados' if created else 'segredos já existiam'} em {args.directory}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
