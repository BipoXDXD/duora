#!/usr/bin/env python3
"""Gera, uma única vez, os arquivos de segredo que o compose.prod.yml monta (spec 15.4).

    sudo duora-secrets init /etc/duora/secrets
    sudo duora-secrets init-backup /etc/duora/secrets
    sudo duora-secrets write-backup-repo /etc/duora/secrets --bucket <bucket> [--region us-east-1] [--replace]
    sudo duora-secrets rotate-certificate /etc/duora/secrets
    sudo duora-secrets retire-previous-certificates /etc/duora/secrets \\
        --keys-directory /var/lib/docker/volumes/duora_dataprotection/_data

`init-backup` cria a senha do papel duora_backup (e o .pgpass dela), a CA própria do pgBackRest e os certificados
do servidor TLS do container backup (CN e SAN "backup") e do cliente do postgres (CN "duora-postgres"), com o
openssl do sistema. A chave da CA é descartada: rotação = gerar o conjunto inteiro de novo
(docs/operations/backup-restore.md).

`write-backup-repo` grava o pgbackrest_repo.conf (endpoint, bucket, região e a credencial do usuário duora-backup,
lida do stdin para não ir ao histórico do shell; docs/operations/aws-setup.md). Só troca um arquivo existente com
--replace, na rotação da chave.

`init` cria as senhas e connection strings do banco e o certificado que cifra o key ring do Data Protection em
repouso (spec 11.2). Nunca sobrescreve: com um grupo inteiro presente não faz nada; com um grupo incompleto, recusa
(gerar só uma parte deixaria senha e connection string divergentes, ou chaves cifradas com um certificado que
sumiu). Rotação das senhas é manual; a do certificado tem comando (docs/operations/deploy.md). Rodando como root, cada arquivo fica 0400 com dono = UID do único
container que o lê, porque o Compose monta o arquivo do host como está (sem uid/gid/mode fora do Swarm).
"""

from __future__ import annotations

import argparse
import os
import re
import secrets
import string
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from xml.etree import ElementTree

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

# Segredos do backup: todos lidos pelo usuário postgres (999), no container postgres ou no backup.
#   pgbackrest_tls_*      CA, servidor TLS do backup e cliente do postgres (WAL por TLS)
#   db_backup_password    senha do papel duora_backup (lida pelo initdb do postgres)
#   pgbackrest_db_pgpass  a mesma senha no formato .pgpass, para o pgBackRest do backup conectar por TCP
BACKUP_FILES = (
    "pgbackrest_tls_ca.crt",
    "pgbackrest_tls_server.crt",
    "pgbackrest_tls_server.key",
    "pgbackrest_tls_client.crt",
    "pgbackrest_tls_client.key",
    "db_backup_password",
    "pgbackrest_db_pgpass",
)
BACKUP_ROLE = "duora_backup"
BACKUP_DATABASE = "postgres"  # banco em que o pgBackRest conecta (pg1-database padrão)
TLS_SERVER_HOST = "backup"  # nome do serviço no compose.prod.yml; o postgres confere o certificado por ele
TLS_CLIENT_CN = "duora-postgres"  # autorizado em tls-server-auth (pgbackrest-repo.conf)
TLS_CA_DAYS = 3650
TLS_LEAF_DAYS = 825

# Repositório S3 do pgBackRest (montado como /etc/pgbackrest/conf.d/repo.conf no backup e no restore). Formatos
# conferidos antes de gravar: o arquivo é INI, então quebra de linha ou espaço num valor viraria outra opção.
BACKUP_REPOSITORY_FILE = "pgbackrest_repo.conf"
DEFAULT_S3_REGION = "us-east-1"
S3_URI_STYLES = ("host", "path")
_S3_HOSTNAME = re.compile(r"[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+")
# Sem ponto: com host virtual, o certificado curinga do endpoint não cobre um bucket com ponto no nome.
_S3_BUCKET = re.compile(r"[a-z0-9][a-z0-9-]{1,61}[a-z0-9]")
_S3_REGION = re.compile(r"[a-z]{2}(-[a-z]+)+-[0-9]+")
_S3_ACCESS_KEY = re.compile(r"[A-Za-z0-9]{16,128}")
_S3_SECRET_KEY = re.compile(r"[A-Za-z0-9/+=]{16,128}")

# Certificado do key ring: o atual cifra as chaves novas; os anteriores, em pares PEM do mais novo ao mais antigo, só
# decifram as chaves que cifraram (DataProtection__Certificate e DataProtection__PreviousCertificates no Compose).
CERTIFICATE = "dataprotection_certificate"
PREVIOUS_CERTIFICATES = "dataprotection_previous_certificates"
CERTIFICATE_OWNERS = {CERTIFICATE: APP_UID, PREVIOUS_CERTIFICATES: APP_UID}
CERTIFICATE_SUBJECT = "/CN=duora-data-protection"
CERTIFICATE_RSA_BITS = 3072
# O EncryptedXml não confere a validade; ela só precisa cobrir a vida do certificado até a próxima rotação planejada.
CERTIFICATE_DAYS = 3650
CERTIFICATE_BLOCK = re.compile(r"-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----", re.DOTALL)
# Vida máxima do que o key ring protege depois que a chave deixa de proteger (token de confirmação, 24 h).
PAYLOAD_LIFETIME = timedelta(hours=24)

PASSWORD_BYTES = 32  # token_urlsafe → 43 caracteres, só [A-Za-z0-9_-]: seguro dentro da connection string
_PASSWORD_FIRST_CHARACTERS = string.ascii_letters + string.digits
DATABASE_HOST = "postgres"
DATABASE_NAME = "duora"


class SecretsError(Exception):
    """Estado do diretório que exige decisão humana."""


def init_secrets(directory: Path, *, change_owner: bool) -> bool:
    """Cria cada grupo de segredos que falta inteiro. Devolve True se criou algo, False se já estava tudo lá."""
    existing = {path.name for path in directory.iterdir()} if directory.exists() else set()
    passwords = _missing_group(existing, set(SECRET_OWNERS), directory)
    certificate = _missing_group(existing, set(CERTIFICATE_OWNERS), directory)
    if not passwords and not certificate:
        return False

    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    directory.chmod(0o700)
    # O certificado é gerado antes de gravar qualquer coisa: se o openssl falhar, nada fica pela metade.
    certificate_pem = _generate_certificate(directory) if certificate else None
    if passwords:
        for name, content in _generate().items():
            _write_secret(directory / name, content, owner=SECRET_OWNERS[name] if change_owner else None)
        _sync_directory(directory)
    if certificate_pem is not None:
        _replace_secrets(directory, {CERTIFICATE: certificate_pem, PREVIOUS_CERTIFICATES: ""},
                         change_owner=change_owner)
    return True


def init_backup(directory: Path, *, change_owner: bool) -> bool:
    """Cria CA, certificados, senha do duora_backup e o .pgpass se nenhum existe; True se criou."""
    existing = {path.name for path in directory.iterdir()} if directory.exists() else set()
    expected: set[str] = set(BACKUP_FILES)
    if expected <= existing:
        return False
    if existing & expected:
        missing = ", ".join(sorted(expected - existing))
        raise SecretsError(f"conjunto de backup incompleto ({directory}); faltam: {missing}. Apague os arquivos "
                           "pgbackrest_tls_*, db_backup_password e pgbackrest_db_pgpass e gere todos de novo; a senha "
                           "do duora_backup no banco precisa ser trocada junto (docs/operations/backup-restore.md).")

    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    directory.chmod(0o700)
    # A chave da CA só existe neste diretório temporário: ninguém emite certificado novo com ela depois.
    with tempfile.TemporaryDirectory(prefix="duora-tls-") as scratch:
        generated = _generate_tls(Path(scratch))
    password = generate_password()
    generated["db_backup_password"] = password
    generated["pgbackrest_db_pgpass"] = f"{DATABASE_HOST}:5432:{BACKUP_DATABASE}:{BACKUP_ROLE}:{password}\n"
    for name in BACKUP_FILES:
        _write_secret(directory / name, generated[name], owner=POSTGRES_UID if change_owner else None)
    return True


@dataclass(frozen=True)
class BackupRepository:
    endpoint: str
    bucket: str
    region: str
    uri_style: str
    key: str
    secret: str = field(repr=False)

    def to_conf(self) -> str:
        return (f"[global]\nrepo1-s3-endpoint={self.endpoint}\nrepo1-s3-bucket={self.bucket}\n"
                f"repo1-s3-region={self.region}\nrepo1-s3-uri-style={self.uri_style}\n"
                f"repo1-s3-key={self.key}\nrepo1-s3-key-secret={self.secret}\n")


def parse_backup_repository(*, bucket: str, region: str, endpoint: str, uri_style: str,
                            credentials: str) -> BackupRepository:
    """Valida o destino e a credencial (access key na 1a linha, secret na 2a). Endpoint vazio = o da AWS na região."""
    if _S3_BUCKET.fullmatch(bucket) is None:
        raise SecretsError(f"bucket inválido: {bucket!r} (3 a 63 caracteres [a-z0-9-], sem ponto)")
    if _S3_REGION.fullmatch(region) is None:
        raise SecretsError(f"região inválida: {region!r} (ex.: us-east-1)")
    endpoint = endpoint or f"s3.{region}.amazonaws.com"
    if _S3_HOSTNAME.fullmatch(endpoint) is None:
        raise SecretsError(f"endpoint inválido: {endpoint!r} (só o nome do host, sem https:// nem caminho)")
    if uri_style not in S3_URI_STYLES:
        raise SecretsError(f"uri style inválido: {uri_style!r} (host ou path)")
    lines = credentials.splitlines()
    if len(lines) != 2:
        raise SecretsError("o stdin precisa ter exatamente duas linhas: access key id e secret access key")
    key, secret = lines
    if _S3_ACCESS_KEY.fullmatch(key) is None:
        raise SecretsError("access key id inválido (16 a 128 caracteres [A-Za-z0-9])")
    if _S3_SECRET_KEY.fullmatch(secret) is None:
        # A mensagem nunca repete o valor: ele é o segredo.
        raise SecretsError("secret access key inválido (16 a 128 caracteres [A-Za-z0-9/+=], sem espaço)")
    return BackupRepository(endpoint=endpoint, bucket=bucket, region=region, uri_style=uri_style, key=key,
                            secret=secret)


def write_backup_repository(directory: Path, repository: BackupRepository, *, replace: bool,
                            change_owner: bool) -> None:
    """Grava o pgbackrest_repo.conf 0400 (dono 999); com replace, troca o existente por rename atômico."""
    target = directory / BACKUP_REPOSITORY_FILE
    if target.exists() and not replace:
        raise SecretsError(f"{target} já existe; para trocar a credencial (rotação) use --replace")
    staged = directory / f".{BACKUP_REPOSITORY_FILE}.new"
    staged.unlink(missing_ok=True)
    _write_secret(staged, repository.to_conf(), owner=POSTGRES_UID if change_owner else None)
    os.replace(staged, target)
    _sync_directory(directory)


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


def generate_password() -> str:
    """44 caracteres de [A-Za-z0-9_-], o primeiro alfanumérico: uma senha começando com "-" vira opção quando passa
    por uma linha de comando (o mc do drill falhava com "flag provided but not defined")."""
    return secrets.choice(_PASSWORD_FIRST_CHARACTERS) + secrets.token_urlsafe(PASSWORD_BYTES)


def _openssl(*args: str) -> None:
    result = subprocess.run(["openssl", *args], capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise SecretsError(f"openssl {args[0]} falhou: {result.stderr.strip()[-500:]}")


def rotate_certificate(directory: Path, *, change_owner: bool) -> None:
    """Gera um certificado novo e põe o atual na frente dos anteriores, que seguem decifrando as chaves antigas."""
    current, previous = _read_certificates(directory)
    replacements = {CERTIFICATE: _generate_certificate(directory), PREVIOUS_CERTIFICATES: current + previous}
    _replace_secrets(directory, replacements, change_owner=change_owner)


def retire_previous_certificates(directory: Path, keys_directory: Path, *, change_owner: bool,
                                 now: datetime | None = None) -> None:
    """Esvazia os anteriores, só se nenhuma chave do key ring cifrada por eles ainda for necessária.

    Uma chave protege enquanto não expira e o que ela protegeu vale até PAYLOAD_LIFETIME depois; o Data Protection
    nunca recifra a chave com o certificado novo. Por isso a conferência lê o key ring em vez de confiar num prazo.
    """
    _, previous = _read_certificates(directory)
    needed = _keys_needing(previous, keys_directory, now or datetime.now(timezone.utc))
    if needed:
        raise SecretsError("certificado anterior ainda decifra chaves em uso: " + ", ".join(needed) + ". Espere "
                           "a expiração delas mais 24 h (docs/operations/deploy.md).")
    _replace_secrets(directory, {PREVIOUS_CERTIFICATES: ""}, change_owner=change_owner)


def _keys_needing(previous_pem: str, keys_directory: Path, now: datetime) -> list[str]:
    keys = sorted(keys_directory.glob("key-*.xml"))
    if not keys:
        raise SecretsError(f"nenhuma chave em {keys_directory}: confira o caminho do volume do key ring.")
    certificates = {"".join(block.split()) for block in CERTIFICATE_BLOCK.findall(previous_pem)}
    needed = []
    for key in keys:
        root = ElementTree.parse(key).getroot()
        expiration = _parse_instant(root.findtext("expirationDate") or "")
        # O EncryptedXml grava o certificado em <X509Certificate> no namespace do xmldsig.
        used_by = {"".join((element.text or "").split()) for element in root.iter()
                   if element.tag.rsplit("}", 1)[-1] == "X509Certificate"}
        if expiration + PAYLOAD_LIFETIME > now and used_by & certificates:
            needed.append(f"{root.get('id')} (expira em {expiration.isoformat()})")
    return needed


def _parse_instant(text: str) -> datetime:
    # O .NET grava 7 casas de fração e "Z"; o fromisoformat do 3.12 aceita até 6.
    match = re.fullmatch(r"(.+?T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})", text.strip())
    if match is None:
        raise SecretsError(f"expirationDate ilegível no key ring: {text!r}")
    seconds, fraction, offset = match.groups()
    fraction_text = f".{fraction[:6]}" if fraction else ""
    return datetime.fromisoformat(seconds + fraction_text + ("+00:00" if offset == "Z" else offset))


def _missing_group(existing: set[str], expected: set[str], directory: Path) -> bool:
    if expected <= existing:
        return False
    if existing & expected:
        missing = ", ".join(sorted(expected - existing))
        raise SecretsError(f"diretório incompleto ({directory}); faltam: {missing}. Restaure do backup ou "
                           "recrie o grupo inteiro (com o banco ou com o key ring), nunca só uma parte.")
    return True


def _read_certificates(directory: Path) -> tuple[str, str]:
    paths = [directory / CERTIFICATE, directory / PREVIOUS_CERTIFICATES]
    if not all(path.exists() for path in paths):
        raise SecretsError(f"certificado do key ring ausente em {directory}; rode 'duora-secrets init' antes.")
    current, previous = (path.read_text(encoding="utf-8") for path in paths)
    return current, previous


def _generate_certificate(directory: Path) -> str:
    """Autoassinado, RSA, chave PKCS#8 sem senha: quem protege a chave é o arquivo 0400 e a custódia separada."""
    # Diretório temporário dentro do de segredos (0700): a chave nunca passa por /tmp.
    with tempfile.TemporaryDirectory(dir=directory, prefix=".certificate-") as work:
        key, certificate = Path(work) / "key.pem", Path(work) / "certificate.pem"
        try:
            _openssl("req", "-x509", "-newkey", f"rsa:{CERTIFICATE_RSA_BITS}", "-sha256", "-noenc",
                     "-days", str(CERTIFICATE_DAYS), "-subj", CERTIFICATE_SUBJECT,
                     "-keyout", str(key), "-out", str(certificate))
            return certificate.read_text(encoding="ascii") + key.read_text(encoding="ascii")
        finally:
            # O TemporaryDirectory também apaga; a chave sai já aqui, antes de qualquer outra coisa.
            key.unlink(missing_ok=True)


def _replace_secrets(directory: Path, contents: dict[str, str], *, change_owner: bool) -> None:
    # Grava todos ao lado e só então troca com os definitivos: o rename é atômico, e uma falha no meio não deixa o
    # certificado novo sem o antigo nos anteriores.
    staged = {}
    for name, content in contents.items():
        path = directory / f".{name}.new"
        path.unlink(missing_ok=True)
        _write_secret(path, content, owner=CERTIFICATE_OWNERS[name] if change_owner else None)
        staged[name] = path
    # Anteriores primeiro: se o processo cair entre as trocas, o certificado atual continua o mesmo e os anteriores
    # já o contêm, então nenhuma chave fica sem quem a decifre.
    for name in (PREVIOUS_CERTIFICATES, CERTIFICATE):
        if name in staged:
            os.replace(staged[name], directory / name)
    _sync_directory(directory)


def _sync_directory(directory: Path) -> None:
    """Persiste as entradas do diretório (criações e renames) antes de responder que terminou."""
    descriptor = os.open(directory, os.O_RDONLY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def _generate() -> dict[str, str]:
    superuser = generate_password()
    migrator = generate_password()
    app = generate_password()
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
        file.flush()
        os.fsync(file.fileno())
    if owner is not None:
        os.chown(path, owner, owner)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="duora-secrets", description=DESCRIPTION)
    subcommands = parser.add_subparsers(dest="command", required=True)
    for name, help_text in (("init", "cria os segredos do banco e o certificado do key ring (cada grupo inteiro ou nada)"),
                            ("init-backup", "cria senha do duora_backup e certificados TLS do pgBackRest "
                                            "(todos ou nenhum)")):
        command = subcommands.add_parser(name, help=help_text)
        command.add_argument("directory", type=Path)
        command.add_argument("--no-chown", action="store_true",
                             help="não troca o dono dos arquivos (smoke local sem root)")
    rotate = subcommands.add_parser("rotate-certificate", help="gera certificado novo do key ring; o atual vira anterior")
    rotate.add_argument("directory", type=Path)
    rotate.add_argument("--no-chown", action="store_true", help="não troca o dono dos arquivos")
    retire = subcommands.add_parser("retire-previous-certificates",
                                    help="esvazia os certificados anteriores, se nenhuma chave ainda precisar deles")
    retire.add_argument("directory", type=Path)
    retire.add_argument("--keys-directory", type=Path, required=True,
                        help="diretório do key ring (no host: /var/lib/docker/volumes/duora_dataprotection/_data)")
    retire.add_argument("--no-chown", action="store_true", help="não troca o dono dos arquivos")
    repository = subcommands.add_parser("write-backup-repo",
                                        help="grava o pgbackrest_repo.conf; credencial no stdin (key e secret, uma "
                                             "por linha)")
    repository.add_argument("directory", type=Path)
    repository.add_argument("--bucket", required=True)
    repository.add_argument("--region", default=DEFAULT_S3_REGION)
    repository.add_argument("--endpoint", default="", help="host do S3, sem esquema (padrão: s3.<região>.amazonaws.com)")
    repository.add_argument("--uri-style", choices=S3_URI_STYLES, default="host")
    repository.add_argument("--replace", action="store_true", help="troca um arquivo existente (rotação da chave)")
    repository.add_argument("--no-chown", action="store_true", help="não troca o dono dos arquivos")
    args = parser.parse_args(argv)

    change_owner = not args.no_chown
    if change_owner and os.geteuid() != 0:
        print("duora-secrets: rode como root ou use --no-chown", file=sys.stderr)
        return 1
    try:
        if args.command == "rotate-certificate":
            rotate_certificate(args.directory, change_owner=change_owner)
            message = "certificado do key ring trocado; recrie o web para usar o novo"
        elif args.command == "write-backup-repo":
            parsed = parse_backup_repository(bucket=args.bucket, region=args.region, endpoint=args.endpoint,
                                             uri_style=args.uri_style, credentials=sys.stdin.read())
            write_backup_repository(args.directory, parsed, replace=args.replace, change_owner=change_owner)
            message = "pgbackrest_repo.conf gravado; recrie backup e rode duora-backup verify"
        elif args.command == "retire-previous-certificates":
            retire_previous_certificates(args.directory, args.keys_directory, change_owner=change_owner)
            message = "certificados anteriores do key ring retirados; recrie o web"
        else:
            initialize = init_secrets if args.command == "init" else init_backup
            created = initialize(args.directory, change_owner=change_owner)
            message = "segredos criados" if created else "segredos já existiam"
    except SecretsError as error:
        print(f"duora-secrets: {error}", file=sys.stderr)
        return 1
    print(f"duora-secrets: {message} em {args.directory}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
