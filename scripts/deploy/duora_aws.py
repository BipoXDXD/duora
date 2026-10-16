#!/usr/bin/env python3
"""Preenche as políticas e a configuração dos buckets na AWS (infra/aws) e lista os CIDRs do S3 para o filtro de saída.

Roda na máquina do responsável, nunca na VPS, e não fala com a AWS: só lê os modelos do repositório e escreve JSON
pronto para o `aws` CLI (docs/operations/aws-setup.md).

    python3 scripts/deploy/duora_aws.py render --backup-bucket duora-backup-<sufixo> \\
        --media-bucket duora-media-<sufixo> --account-id <12 dígitos> --vps-ipv4 <IP público da VPS> \\
        --out ~/duora-ops/aws
    curl -fsS https://ip-ranges.amazonaws.com/ip-ranges.json | python3 scripts/deploy/duora_aws.py s3-cidrs

Os modelos usam marcadores {{NOME}}; ${...} fica intacto, porque é a sintaxe de variável de política da AWS.
"""

from __future__ import annotations

import argparse
import ipaddress
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path

TEMPLATE_ROOT = Path(__file__).resolve().parents[2] / "infra" / "aws"
DEFAULT_REGION = "us-east-1"
PLACEHOLDER = re.compile(r"\{\{([A-Z0-9_]+)\}\}")
# Regras de nome da AWS, mais "sem ponto": com ponto, o certificado curinga de *.s3.<região>.amazonaws.com não cobre
# o host virtual do bucket, e o pgBackRest e o verify usam host virtual (repo1-s3-uri-style=host).
BUCKET_NAME = re.compile(r"[a-z0-9][a-z0-9-]{1,61}[a-z0-9]")
RESERVED_BUCKET_PREFIXES = ("xn--", "sthree-", "amzn-s3-demo-")
RESERVED_BUCKET_SUFFIXES = ("-s3alias", "--ol-s3", "--x-s3", "--table-s3")
ACCOUNT_ID = re.compile(r"[0-9]{12}")
# Um CIDR mais largo que isso no ip-ranges.json seria documento adulterado ou ilegível, não um bloco do S3.
WIDEST_S3_PREFIX = 8


class AwsInputError(Exception):
    """Entrada que não serve para gerar a configuração; nada é escrito."""


def parse_bucket_name(value: str) -> str:
    if (BUCKET_NAME.fullmatch(value) is None or value.startswith(RESERVED_BUCKET_PREFIXES)
            or value.endswith(RESERVED_BUCKET_SUFFIXES)):
        raise AwsInputError(f"nome de bucket inválido: {value!r} (3 a 63 caracteres [a-z0-9-], sem ponto, começando e "
                            "terminando com letra ou dígito)")
    return value


def parse_account_id(value: str) -> str:
    if ACCOUNT_ID.fullmatch(value) is None:
        raise AwsInputError(f"id de conta AWS inválido: {value!r} (12 dígitos)")
    return value


def parse_vps_ipv4(value: str) -> str:
    """IPv4 público da VPS, de onde saem as requisições ao S3 (condição aws:SourceIp)."""
    try:
        address = ipaddress.IPv4Address(value)
    except ipaddress.AddressValueError as error:
        raise AwsInputError(f"IPv4 da VPS inválido: {value!r}") from error
    if not address.is_global:
        raise AwsInputError(f"IPv4 da VPS não é público: {value}")
    return str(address)


@dataclass(frozen=True)
class TemplateValues:
    backup_bucket: str
    media_bucket: str
    account_id: str
    vps_ipv4: str

    def __post_init__(self) -> None:
        parse_bucket_name(self.backup_bucket)
        parse_bucket_name(self.media_bucket)
        parse_account_id(self.account_id)
        parse_vps_ipv4(self.vps_ipv4)
        if self.backup_bucket == self.media_bucket:
            raise AwsInputError("os buckets de backup e de mídia precisam ser diferentes (ADR-0023 e ADR-0025)")

    def placeholders(self) -> dict[str, str]:
        return {"BACKUP_BUCKET": self.backup_bucket, "MEDIA_BUCKET": self.media_bucket,
                "ACCOUNT_ID": self.account_id, "VPS_IPV4": self.vps_ipv4}


def render_text(template: str, values: TemplateValues) -> str:
    known = values.placeholders()
    unknown = sorted({name for name in PLACEHOLDER.findall(template) if name not in known})
    if unknown:
        raise AwsInputError(f"marcador sem valor: {', '.join(unknown)}")
    return PLACEHOLDER.sub(lambda match: known[match[1]], template)


def render(template_root: Path, out_dir: Path, values: TemplateValues) -> list[Path]:
    """Escreve cada modelo preenchido no mesmo caminho relativo; tudo é conferido antes de gravar o primeiro."""
    if out_dir.exists() and any(out_dir.iterdir()):
        raise AwsInputError(f"{out_dir} já tem arquivos; use um diretório novo para não misturar versões")
    outputs: dict[Path, str] = {}
    for template in sorted(template_root.rglob("*.json")):
        text = render_text(template.read_text(encoding="utf-8"), values)
        try:
            json.loads(text)
        except json.JSONDecodeError as error:
            raise AwsInputError(f"{template.relative_to(template_root)} não é JSON válido: {error}") from error
        outputs[out_dir / template.relative_to(template_root)] = text
    for path, text in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    return list(outputs)


def s3_cidrs(ip_ranges_json: str, region: str) -> list[str]:
    """Prefixos IPv4 do serviço S3 na região, do ip-ranges.json publicado pela AWS, ordenados e sem repetição."""
    try:
        document = json.loads(ip_ranges_json)
        prefixes = document["prefixes"]
        if not isinstance(prefixes, list):
            raise TypeError("prefixes não é lista")
        selected = {ipaddress.IPv4Network(entry["ip_prefix"]) for entry in prefixes
                    if entry.get("service") == "S3" and entry.get("region") == region}
    except (json.JSONDecodeError, KeyError, TypeError, AttributeError, ValueError) as error:
        raise AwsInputError(f"ip-ranges.json ilegível: {error}") from error
    if not selected:
        raise AwsInputError(f"nenhum prefixo IPv4 do S3 em {region} no ip-ranges.json")
    too_wide = sorted(str(network) for network in selected if network.prefixlen < WIDEST_S3_PREFIX)
    if too_wide:
        raise AwsInputError(f"prefixo largo demais para ser do S3: {', '.join(too_wide)}")
    return [str(network) for network in sorted(selected)]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="duora_aws.py", description=__doc__.splitlines()[0] if __doc__ else None)
    commands = parser.add_subparsers(dest="command", required=True)
    render_command = commands.add_parser("render", help="preenche infra/aws com os nomes reais")
    render_command.add_argument("--backup-bucket", required=True)
    render_command.add_argument("--media-bucket", required=True)
    render_command.add_argument("--account-id", required=True)
    render_command.add_argument("--vps-ipv4", required=True)
    render_command.add_argument("--out", type=Path, required=True)
    cidrs_command = commands.add_parser("s3-cidrs", help="CIDRs IPv4 do S3 (ip-ranges.json no stdin) em YAML")
    cidrs_command.add_argument("--region", default=DEFAULT_REGION)
    args = parser.parse_args(argv)

    try:
        if args.command == "render":
            values = TemplateValues(backup_bucket=args.backup_bucket, media_bucket=args.media_bucket,
                                    account_id=args.account_id, vps_ipv4=args.vps_ipv4)
            written = render(TEMPLATE_ROOT, args.out, values)
            print(f"duora_aws: {len(written)} arquivos em {args.out}")
        else:
            cidrs = s3_cidrs(sys.stdin.read(), args.region)
            print("duora_backup_s3_cidrs:")
            for cidr in cidrs:
                print(f"  - {cidr}")
    except AwsInputError as error:
        print(f"duora_aws: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
