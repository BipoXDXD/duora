"""Filtro do access log do Caddy (infra/caddy/Caddyfile) contra o Caddy real.

Sobe o Caddyfile do repositório na imagem base da edge (mesma versão do infra/images/edge/Dockerfile), faz pedidos
de dentro do container e lê o log. Sem Docker o teste é pulado:

    python3 -m unittest discover -s scripts/deploy -p 'test_caddy_access_log.py' -v
"""

from __future__ import annotations

import json
import shutil
import subprocess
import time
import unittest
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CADDYFILE = REPO / "infra" / "caddy" / "Caddyfile"
# Mesma imagem do estágio "edge" do infra/images/edge/Dockerfile (a edge só troca o binário por um build do xcaddy).
IMAGE = "caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f"
READY_TIMEOUT_SECONDS = 30


def _docker_available() -> bool:
    if shutil.which("docker") is None:
        return False
    return subprocess.run(["docker", "info"], capture_output=True, check=False).returncode == 0


@unittest.skipUnless(_docker_available(), "precisa de Docker")
class CaddyAccessLogTests(unittest.TestCase):
    container = ""

    @classmethod
    def setUpClass(cls) -> None:
        cls.container = f"duora-caddy-log-{uuid.uuid4().hex[:8]}"
        _run(["docker", "run", "--detach", "--name", cls.container,
              "--env", "DUORA_DOMAIN=localhost", "--env", "DUORA_TLS=internal",
              "--volume", f"{CADDYFILE}:/etc/caddy/Caddyfile:ro", IMAGE])
        cls._wait_ready()

    @classmethod
    def tearDownClass(cls) -> None:
        if cls.container:
            subprocess.run(["docker", "rm", "--force", cls.container], capture_output=True, check=False)

    @classmethod
    def _wait_ready(cls) -> None:
        deadline = time.monotonic() + READY_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            probe = subprocess.run(["docker", "exec", cls.container, "wget", "-q", "-O-",
                                    "http://127.0.0.1:8081/healthz"], capture_output=True, check=False)
            if probe.returncode == 0:
                return
            time.sleep(0.5)
        raise AssertionError("Caddy não ficou pronto")

    def request(self, path: str) -> None:
        # O web não existe aqui: o proxy responde 502, e o log de acesso sai do mesmo jeito.
        subprocess.run(["docker", "exec", self.container, "wget", "-q", "-O-", "--no-check-certificate",
                        f"https://localhost{path}"], capture_output=True, check=False)

    def logged_uris(self) -> list[str]:
        logs = subprocess.run(["docker", "logs", self.container], capture_output=True, text=True, check=False)
        uris: list[str] = []
        for line in (logs.stdout + logs.stderr).splitlines():
            try:
                entry = json.loads(line)
            except json.JSONDecodeError:
                continue
            request = entry.get("request") if isinstance(entry, dict) else None
            if isinstance(request, dict) and isinstance(request.get("uri"), str):
                uris.append(request["uri"])
        return uris

    def test_uuid_in_the_path_is_replaced_and_never_logged(self) -> None:
        canary = str(uuid.uuid4())
        self.request(f"/api/v1/me/blocks/{canary}")

        uris = self.logged_uris()
        self.assertIn("/api/v1/me/blocks/{id}", uris)
        self.assertFalse(any(canary in uri for uri in uris), uris)

    def test_uppercase_uuid_and_query_string_are_both_redacted(self) -> None:
        canary = str(uuid.uuid4()).upper()
        self.request(f"/api/v1/me/sessions/{canary}?token=CANARY-{canary}")

        uris = self.logged_uris()
        self.assertIn("/api/v1/me/sessions/{id}?redacted", uris)
        self.assertFalse(any(canary in uri for uri in uris), uris)


def _run(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise AssertionError(f"{' '.join(command[:3])} falhou: {result.stderr.strip()[-800:]}")


if __name__ == "__main__":
    unittest.main()
