"""Apoio comum aos testes que sobem o Caddyfile do repositório no Caddy real (test_caddy_*.py)."""

from __future__ import annotations

import shutil
import subprocess
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
