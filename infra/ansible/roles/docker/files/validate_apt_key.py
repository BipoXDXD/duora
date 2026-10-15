#!/usr/bin/env python3
"""Valida um arquivo de chave para o signed_by do apt: exatamente UMA chave primária, com a impressão digital
esperada. O apt confia em todas as chaves do arquivo, então procurar a impressão digital esperada no meio da saída
não basta: a chave certa mais uma chave do atacante passaria.

    validate-apt-key <arquivo .asc ou .gpg> <impressão digital de 40 hex>

Sai com 0 se válido e 1 com o motivo no stderr. Instalado pelo role docker; só biblioteca padrão + gpg.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

_FINGERPRINT = re.compile(r"[0-9A-F]{40}")


class KeyValidationError(Exception):
    """O arquivo não pode ir para o signed_by."""


def validate(path: Path, expected_fingerprint: str) -> None:
    expected = expected_fingerprint.upper()
    if _FINGERPRINT.fullmatch(expected) is None:
        raise KeyValidationError(f"impressão digital esperada inválida: {expected_fingerprint!r}")

    result = subprocess.run(["gpg", "--batch", "--show-keys", "--with-colons", "--with-fingerprint", str(path)],
                            capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise KeyValidationError(f"gpg não leu {path} como chave: {result.stderr.strip()[-300:]}")
    records = [line.split(":") for line in result.stdout.splitlines() if line]

    primary_indexes = [index for index, record in enumerate(records) if record[0] in ("pub", "sec")]
    if len(primary_indexes) != 1:
        raise KeyValidationError(f"{path} tem {len(primary_indexes)} chaves primárias; esperado exatamente 1")
    if records[primary_indexes[0]][0] != "pub":
        raise KeyValidationError(f"{path} contém chave secreta")

    # O fpr da chave primária vem logo depois do registro pub; os fpr seguintes são de subchaves dela.
    following = primary_indexes[0] + 1
    if following >= len(records) or records[following][0] != "fpr":
        raise KeyValidationError(f"{path}: gpg não informou a impressão digital da chave primária")
    actual = records[following][9]
    if actual != expected:
        raise KeyValidationError(f"impressão digital {actual} diferente da esperada {expected}")


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("uso: validate-apt-key <arquivo> <impressão digital>", file=sys.stderr)
        return 2
    try:
        validate(Path(argv[0]), argv[1])
    except KeyValidationError as error:
        print(f"validate-apt-key: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
