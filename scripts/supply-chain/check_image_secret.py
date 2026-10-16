#!/usr/bin/env python3
"""Confere que um segredo de build não ficou numa imagem (ADR-0025: licença da Six Labors).

Uso: check_image_secret.py <imagem> <VARIAVEL_DE_AMBIENTE>

Lê o segredo da variável de ambiente (nunca de argumento, para não aparecer na lista de processos) e procura por ele,
e por cada pedaço longo dele, em UTF-8, UTF-16LE (strings de .NET em recursos) e base64 (padrão e URL), no histórico
da imagem (`docker history --no-trunc`) e em cada camada dela (`docker save`), não no filesystem achatado: um arquivo
gravado numa camada e apagado na seguinte some do `docker export`, mas continua na imagem publicada. Camadas gzip são
descomprimidas; outra compressão falha fechado. Sai com 1 se achar, 2 se o segredo faltar. Nunca imprime o segredo nem
o trecho encontrado. Só biblioteca padrão.
"""

import base64
import gzip
import os
import subprocess
import sys
import tarfile

CHUNK = 1024 * 1024
# Pedaços menores que isso aparecem por acaso em binários; o que importa são a assinatura e o id.
MIN_FRAGMENT = 16
GZIP_MAGIC = b"\x1f\x8b"
ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"


class UnsupportedLayer(RuntimeError):
    """Camada numa compressão que este verificador não lê: passar sem ler seria falso negativo."""


def needles(secret):
    """O segredo inteiro e cada campo dele (separados por ; = | :) longo o bastante para ser seu, em cada codificação."""
    parts = {secret}
    for separator in ";=|:":
        parts = {piece for part in parts for piece in part.split(separator)} | parts
    found = set()
    for part in (p for p in parts if len(p) >= MIN_FRAGMENT):
        raw = part.encode()
        found.update({raw, part.encode("utf-16-le")})
        found.update(base64_cores(raw))
    return sorted((n for n in found if len(n) >= MIN_FRAGMENT), key=len, reverse=True)


def base64_cores(data):
    """Trechos do base64 de `data` que não dependem do que vem antes ou depois dele, para cada alinhamento (mod 3).

    Cada caractere carrega 6 bits: com `offset` bytes antes, o primeiro caractere só do dado é o ceil(8*offset/6) e o
    último é o floor(8*(offset+len)/6) - 1; o resto mistura bits vizinhos e muda com o contexto.
    """
    cores = set()
    for offset in range(3):
        start = -(-8 * offset // 6)
        end = 8 * (offset + len(data)) // 6
        for encode in (base64.b64encode, base64.urlsafe_b64encode):
            cores.add(encode(b"\0" * offset + data)[start:end])
    return cores


def stream_contains(stream, patterns):
    """Procura em blocos, com sobreposição do tamanho do maior padrão, para não perder um trecho cortado entre blocos."""
    if not patterns:
        return False
    overlap = max(len(p) for p in patterns) - 1
    tail = b""
    while True:
        block = stream.read(CHUNK)
        if not block:
            return False
        window = tail + block
        if any(p in window for p in patterns):
            return True
        tail = window[-overlap:] if overlap else b""


def image_history(image):
    result = subprocess.run(
        ["docker", "history", "--no-trunc", "--format", "{{.CreatedBy}}", image],
        check=True, capture_output=True)
    return result.stdout


def archive_contains(stream, patterns):
    """Procura em cada arquivo do tar do `docker save` (camadas, config e manifestos), descomprimindo camadas gzip.

    Uma camada é um tar sem compressão por dentro: o conteúdo de cada arquivo dela aparece contíguo no stream.
    """
    with tarfile.open(fileobj=stream, mode="r|") as archive:
        for member in archive:
            if not member.isfile():
                continue
            content = archive.extractfile(member)
            head = content.peek(len(ZSTD_MAGIC))[:len(ZSTD_MAGIC)]
            if head.startswith(ZSTD_MAGIC):
                raise UnsupportedLayer(f"{member.name} está em zstd; o verificador só lê camadas sem compressão ou gzip")
            if head.startswith(GZIP_MAGIC):
                content = gzip.GzipFile(fileobj=content)
            if stream_contains(content, patterns):
                return True
    return False


def save_contains(image, patterns):
    with subprocess.Popen(["docker", "save", image], stdout=subprocess.PIPE) as save:
        try:
            found = archive_contains(save.stdout, patterns)
        finally:
            save.stdout.close()
            save.wait()
        if not found and save.returncode != 0:
            raise RuntimeError(f"docker save saiu com {save.returncode}")
        return found


def main(argv):
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    image, variable = argv[1], argv[2]
    secret = os.environ.get(variable, "")
    if not secret.strip():
        print(f"::error::{variable} vazia: sem o segredo não há o que conferir na imagem {image}.", file=sys.stderr)
        return 2
    patterns = needles(secret.strip())
    if any(p in image_history(image) for p in patterns):
        print(f"::error::o segredo de {variable} aparece no histórico de {image} (ARG/ENV ou comando).")
        return 1
    if save_contains(image, patterns):
        print(f"::error::o segredo de {variable} aparece numa camada de {image}.")
        return 1
    print(f"{image}: segredo de {variable} ausente do histórico e de todas as camadas.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
