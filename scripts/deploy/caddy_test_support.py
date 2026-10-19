"""Apoio comum aos testes que sobem o Caddyfile do repositório no Caddy real (test_caddy_*.py)."""

from __future__ import annotations

import shutil
import subprocess
from collections.abc import Iterable
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CADDYFILE = REPO / "infra" / "caddy" / "Caddyfile"
# Mesma imagem do estágio "edge" do infra/images/edge/Dockerfile (a edge só troca o binário por um build do xcaddy).
IMAGE = "caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f"
READY_TIMEOUT_SECONDS = 30


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


def _unique_pairs[T](pairs: Iterable[tuple[str, T]]) -> dict[str, T]:
    pair_list = list(pairs)
    names = [name for name, _ in pair_list]
    repeated = sorted({name for name in names if names.count(name) > 1})
    if repeated:
        raise ValueError(f"nome repetido na política: {', '.join(repeated)}")
    return dict(pair_list)
