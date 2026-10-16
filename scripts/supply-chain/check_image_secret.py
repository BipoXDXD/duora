#!/usr/bin/env python3
"""Confere que um segredo de build não ficou numa imagem (ADR-0025: licença da Six Labors).

Uso: check_image_secret.py <imagem> <VARIAVEL_DE_AMBIENTE>

Lê o segredo da variável de ambiente (nunca de argumento, para não aparecer na lista de processos) e procura por ele,
e por cada pedaço longo dele, no histórico da imagem (`docker history --no-trunc`) e no filesystem achatado de um
container criado a partir dela (`docker export`, tar sem compressão). Sai com 1 se achar, 2 se o segredo faltar.
Nunca imprime o segredo nem o trecho encontrado. Só biblioteca padrão.
"""

import os
import subprocess
import sys

CHUNK = 1024 * 1024
# Pedaços menores que isso aparecem por acaso em binários; o que importa são a assinatura e o id.
MIN_FRAGMENT = 16


def needles(secret):
    """O segredo inteiro e cada campo dele (separados por ; = | :) longo o bastante para ser seu."""
    parts = {secret}
    for separator in ";=|:":
        parts = {piece for part in parts for piece in part.split(separator)} | parts
    return sorted({part.encode() for part in parts if len(part) >= MIN_FRAGMENT}, key=len, reverse=True)


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


def export_contains(image, patterns):
    container = subprocess.run(["docker", "create", image], check=True, capture_output=True, text=True).stdout.strip()
    try:
        with subprocess.Popen(["docker", "export", container], stdout=subprocess.PIPE) as export:
            found = stream_contains(export.stdout, patterns)
            export.stdout.close()
            export.wait()
            if not found and export.returncode != 0:
                raise RuntimeError(f"docker export saiu com {export.returncode}")
            return found
    finally:
        subprocess.run(["docker", "rm", container], check=False, capture_output=True)


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
    if export_contains(image, patterns):
        print(f"::error::o segredo de {variable} aparece no filesystem de {image}.")
        return 1
    print(f"{image}: segredo de {variable} ausente do histórico e do filesystem.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
