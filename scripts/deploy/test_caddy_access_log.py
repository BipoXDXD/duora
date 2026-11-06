"""Filtro do access log do Caddy (infra/caddy/Caddyfile) contra o Caddy real.

Sobe o Caddyfile do repositório na imagem base da edge (mesma versão do infra/images/edge/Dockerfile), faz pedidos
de dentro do container e lê o log. Sem Docker o teste é pulado:

    python3 -m unittest discover -s scripts/deploy -p 'test_caddy_access_log.py' -v
"""

from __future__ import annotations

import json
import re
import subprocess
import time
import unittest
import uuid

from caddy_readiness import wait_for_edge
from caddy_test_support import CADDYFILE, IMAGE, docker_available, remove_container


WGET_OPTIONS = ("-q", "-S", "-O-", "--timeout=5", "--no-check-certificate")
WGET_TIMEOUT_SECONDS = 10
HTTP_STATUS_LINE = re.compile(r"^\s*HTTP/\S+ (\d{3})", re.MULTILINE)


@unittest.skipUnless(docker_available(), "precisa de Docker")
class CaddyAccessLogTests(unittest.TestCase):
    container = ""

    @classmethod
    def setUpClass(cls) -> None:
        cls.container = f"duora-caddy-log-{uuid.uuid4().hex[:8]}"
        # Registrada antes do run: tearDownClass não roda se o setUpClass falhar, e um container criado que não sobe
        # também precisa sair.
        cls.addClassCleanup(remove_container, cls.container)
        _run(["docker", "run", "--detach", "--name", cls.container,
              "--env", "DUORA_DOMAIN=localhost", "--env", "DUORA_TLS=internal",
              "--add-host", "web:127.0.0.2", "--add-host", "duora-web:127.0.0.1",
              "--volume", f"{CADDYFILE}:/etc/caddy/Caddyfile:ro", IMAGE])
        # Um "web" mínimo no próprio container, para a sonda HTTPS da prontidão e para o proxy ter a quem mandar o corpo:
        # o teto de 64 KiB é aplicado enquanto o corpo é lido, e sem upstream o proxy responderia 502 antes de ler.
        _run(["docker", "exec", "--detach", cls.container, "caddy", "respond", "--listen", ":8080", "duora-upstream"])
        # A porta de health responde antes da emissão do certificado interno, e um handshake sem certificado não
        # gera entrada no access log.
        wait_for_edge(cls.wget)

    @classmethod
    def wget(cls, url: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["docker", "exec", cls.container, "wget", *WGET_OPTIONS, url],
                              capture_output=True, text=True, check=False, timeout=WGET_TIMEOUT_SECONDS)

    def request(self, path: str) -> None:
        self._assert_answered(self.wget(f"https://localhost{path}"), 200)

    def post(self, path: str, body_bytes: int, expected_status: int = 200) -> None:
        # Corpo gerado dentro do container, para o teste não depender do tamanho máximo da linha de comando.
        script = (f"head -c {body_bytes} /dev/zero | tr '\\0' 'a' > /tmp/body && "
                  f"wget {' '.join(WGET_OPTIONS)} --post-file=/tmp/body https://localhost{path}")
        result = subprocess.run(["docker", "exec", self.container, "sh", "-c", script], capture_output=True, text=True,
                                check=False, timeout=WGET_TIMEOUT_SECONDS)
        self._assert_answered(result, expected_status)

    def _assert_answered(self, result: subprocess.CompletedProcess[str], expected_status: int) -> None:
        # O exit code não basta: o wget sai com 1 em todo status de erro, inclusive no 413 esperado.
        statuses = [int(status) for status in HTTP_STATUS_LINE.findall(result.stderr)]
        self.assertEqual([expected_status], statuses, f"wget saiu com {result.returncode}: {result.stderr[-800:]}")

    def logged_statuses(self, uri: str) -> list[int]:
        statuses: list[int] = []
        for entry in self._logged_entries(uri):
            request = entry.get("request")
            if isinstance(request, dict) and request.get("uri") == uri and isinstance(entry.get("status"), int):
                statuses.append(entry["status"])
        return statuses

    def logged_uris(self, expected_uri: str | None = None) -> list[str]:
        uris: list[str] = []
        for entry in self._logged_entries(expected_uri):
            request = entry.get("request")
            if isinstance(request, dict) and isinstance(request.get("uri"), str):
                uris.append(request["uri"])
        return uris

    def _logged_entries(self, expected_uri: str | None) -> list[dict]:
        deadline = time.monotonic() + 5
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError(f"Expected access log entry was not observed within 5 seconds: {expected_uri}")
            logs = subprocess.run(["docker", "logs", self.container], capture_output=True, text=True,
                                  check=False, timeout=remaining)
            entries: list[dict] = []
            for line in (logs.stdout + logs.stderr).splitlines():
                try:
                    entry = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(entry, dict):
                    entries.append(entry)
            if expected_uri is None or any(
                    isinstance(entry.get("request"), dict) and entry["request"].get("uri") == expected_uri
                    and isinstance(entry.get("status"), int) for entry in entries):
                # Preserve every entry. Filtering to the expected URI could conceal an unredacted canary.
                return entries
            time.sleep(min(0.05, remaining))

    def test_uuid_in_the_path_is_replaced_and_never_logged(self) -> None:
        canary = str(uuid.uuid4())
        self.request(f"/api/v1/me/blocks/{canary}")

        uris = self.logged_uris("/api/v1/me/blocks/{id}")
        self.assertIn("/api/v1/me/blocks/{id}", uris)
        self.assertFalse(any(canary in uri for uri in uris), uris)

    def test_uppercase_uuid_and_query_string_are_both_redacted(self) -> None:
        canary = str(uuid.uuid4()).upper()
        self.request(f"/api/v1/me/sessions/{canary}?token=CANARY-{canary}")

        uris = self.logged_uris("/api/v1/me/sessions/{id}?redacted")
        self.assertIn("/api/v1/me/sessions/{id}?redacted", uris)
        self.assertFalse(any(canary in uri for uri in uris), uris)

    def test_webhook_route_is_proxied_and_its_query_is_redacted(self) -> None:
        canary = f"CANARY-{uuid.uuid4()}"
        self.post(f"/webhooks/fake?signature={canary}", 16)

        uris = self.logged_uris("/webhooks/fake?redacted")
        self.assertIn("/webhooks/fake?redacted", uris)
        self.assertFalse(any(canary in uri for uri in uris), uris)
        # O "web" de teste responde 200: a rota foi ao proxy, não ao fallback da SPA.
        self.assertIn(200, self.logged_statuses("/webhooks/fake?redacted"))

    def test_webhook_body_above_64_kib_is_refused_at_the_edge(self) -> None:
        path = f"/webhooks/limite-{uuid.uuid4().hex[:8]}"
        self.post(path, 64 * 1024 + 1, expected_status=413)

        self.assertIn(413, self.logged_statuses(path))


def _run(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise AssertionError(f"{' '.join(command[:3])} falhou: {result.stderr.strip()[-800:]}")


if __name__ == "__main__":
    unittest.main()
