"""Apoio comum aos testes da borda: Caddy real (test_caddy_*.py) e smoke local (smoke_local.py).

A política esperada da CSP e da Permissions-Policy mora aqui, num só lugar, para os dois conferirem o mesmo valor.
"""

from __future__ import annotations

import shutil
import subprocess
from collections.abc import Callable, Iterable
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CADDYFILE = REPO / "infra" / "caddy" / "Caddyfile"
# Mesma imagem do estágio "edge" do infra/images/edge/Dockerfile (a edge só troca o binário por um build do xcaddy).
IMAGE = "caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f"
READY_TIMEOUT_SECONDS = 30


# Diretiva -> fontes, com DUORA_DOMAIN=localhost na 443 (Caddy de teste e smoke). 'self' não cobre blob: (CSP3 6.7.2.8), e nenhuma diretiva libera blob: hoje: a Web Audio lê o
# arquivo por fetch() (connect-src) e a voz ao vivo chega por srcObject, que não passa pelo Fetch.
EXPECTED_CSP = {
    "default-src": ["'none'"],
    "script-src": ["'self'"],
    "style-src": ["'self'"],
    "img-src": ["'self'"],
    "font-src": ["'self'"],
    "media-src": ["'self'"],
    "connect-src": ["'self'", "wss://localhost"],
    "manifest-src": ["'self'"],
    "base-uri": ["'none'"],
    "form-action": ["'self'"],
    "frame-ancestors": ["'none'"],
    "object-src": ["'none'"],
}

EXPECTED_PERMISSIONS_POLICY = {
    "camera": "()",
    "microphone": "(self)",
    "geolocation": "()",
    "payment": "()",
    "usb": "()",
    "browsing-topics": "()",
}


def docker_available() -> bool:
    if shutil.which("docker") is None:
        return False
    return subprocess.run(["docker", "info"], capture_output=True, check=False).returncode == 0


def parse_csp(header: str) -> dict[str, list[str]]:
    """Diretiva -> fontes de um cabeçalho com uma só política; ValueError se houver vírgula ou diretiva repetida.

    O navegador aplica a primeira ocorrência de uma diretiva e ignora as seguintes (CSP3 2.2.1), e a vírgula abre
    outra política (CSP3 3.1): em vez de escolher uma leitura, a política ambígua é recusada.
    """
    if "," in header:
        raise ValueError(f"mais de uma política no mesmo cabeçalho: {header!r}")
    directives = [directive.split() for directive in header.split(";") if directive.strip()]
    return _unique_pairs((tokens[0].lower(), tokens[1:]) for tokens in directives)


def parse_permissions_policy(header: str) -> dict[str, str]:
    """Feature -> allowlist; ValueError se uma feature aparecer duas vezes."""
    members = (member.strip().partition("=") for member in header.split(",") if member.strip())
    return _unique_pairs((name.lower(), allowlist) for name, _, allowlist in members)


def is_exact_policy[T](values: list[str], parse: Callable[[str], dict[str, T]], expected: dict[str, T]) -> bool:
    """True só com um único cabeçalho cuja política, sem ambiguidade, é exatamente a esperada."""
    if len(values) != 1:
        return False
    try:
        return parse(values[0]) == expected
    except ValueError:
        return False

def _unique_pairs[T](pairs: Iterable[tuple[str, T]]) -> dict[str, T]:
    pair_list = list(pairs)
    names = [name for name, _ in pair_list]
    repeated = sorted({name for name in names if names.count(name) > 1})
    if repeated:
        raise ValueError(f"nome repetido na política: {', '.join(repeated)}")
    return dict(pair_list)


def remove_container(name: str) -> None:
    """Remove o container pelo nome exato; sem efeito se ele não chegou a existir."""
    subprocess.run(["docker", "rm", "--force", name], capture_output=True, check=False)
