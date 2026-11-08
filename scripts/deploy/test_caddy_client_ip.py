"""Cadeia de IP real da opção B: Traefik -> Caddy -> web, com o Caddy real e um web de eco no mesmo container.

O Caddyfile do repositório recebe, só aqui, o que o arquivo do host tem atrás do Traefik (caddyfile_behind_traefik).
O peer confiável é o loopback do container, no papel do Traefik; o endereço eth0 do container faz o papel de quem não é
o Traefik. O outro lado da cadeia, do X-Forwarded-For à partição do rate limit, está em ForwardedHeadersTests.
A Cloudflare e o Traefik não participam: o que sobra sem prova está na descrição do PR que criou este arquivo.

    python3 -m unittest discover -s scripts/deploy -p 'test_caddy_client_ip.py' -v
"""

from __future__ import annotations

import json
import subprocess
import tempfile
import time
import unittest
import uuid
from pathlib import Path

from caddy_test_support import (
    CADDYFILE,
    IMAGE,
    caddyfile_behind_traefik,
    docker_available,
    remove_container,
)

TRAEFIK_ROLE = "127.0.0.1"
EDGE_PORT = 80
WEB_PORT = 8080
REQUEST_TIMEOUT_SECONDS = 10
READY_TIMEOUT_SECONDS = 30
READY_POLL_SECONDS = 0.25
LOG_TIMEOUT_SECONDS = 5
LOG_POLL_SECONDS = 0.05

# O web de eco devolve o que recebeu, um cabeçalho por campo; valores repetidos chegam unidos por vírgula.
ECHO_SEPARATOR = "|"
ECHO_FIELDS = ("X-Forwarded-For", "X-Forwarded-Proto", "Cf-Connecting-Ip")
ECHO_CADDYFILE = (
    "{\n\tadmin off\n\tauto_https off\n}\n"
    f":{WEB_PORT} {{\n\trespond \""
    + ECHO_SEPARATOR.join(f"{field}={{http.request.header.{field}}}" for field in ECHO_FIELDS)
    + "\"\n}\n"
)

CLIENT_A = "198.51.100.1"
CLIENT_B = "198.51.100.2"
CLIENT_V6 = "2001:db8:a:b::1"
FORGED = "203.0.113.66"


class BehindTraefikFixtureTests(unittest.TestCase):
    def test_every_proxied_route_rewrites_forwarded_for(self) -> None:
        config = caddyfile_behind_traefik(CADDYFILE.read_text(encoding="utf-8"), TRAEFIK_ROLE)

        self.assertEqual(3, config.count("reverse_proxy duora-web:8080 {"))
        self.assertEqual(3, config.count("header_up X-Forwarded-For {client_ip}"))
        self.assertEqual(3, config.count("header_up X-Forwarded-Proto https"))

    def test_only_the_expected_trust_is_declared_once(self) -> None:
        config = caddyfile_behind_traefik(CADDYFILE.read_text(encoding="utf-8"), TRAEFIK_ROLE)

        self.assertEqual(1, config.count(f"trusted_proxies static {TRAEFIK_ROLE}\n"))
        self.assertEqual(1, config.count("client_ip_headers Cf-Connecting-IP\n"))
        self.assertEqual(1, config.count("trusted_proxies_strict\n"))

    def test_control_leaves_the_proxy_routes_alone(self) -> None:
        source = CADDYFILE.read_text(encoding="utf-8")

        config = caddyfile_behind_traefik(source, TRAEFIK_ROLE, rewrite_forwarded_for=False)

        self.assertNotIn("header_up", config)
        self.assertEqual(source.count("reverse_proxy duora-web:8080"), config.count("reverse_proxy duora-web:8080"))

    def test_a_caddyfile_of_another_shape_is_refused_instead_of_silently_testing_something_else(self) -> None:
        source = CADDYFILE.read_text(encoding="utf-8")

        for shape_change in ("\t\treverse_proxy duora-web:8080\n", "\ttls {$DUORA_TLS}\n", "\tlog default {\n"):
            with self.subTest(shape_change=shape_change), self.assertRaises(ValueError):
                caddyfile_behind_traefik(source.replace(shape_change, "", 1), TRAEFIK_ROLE)


class _ChainContainer:
    """Caddy com a fixture e o web de eco; quem usa registra stop como cleanup antes de start."""

    def __init__(self, config: str) -> None:
        self.name = f"duora-caddy-ip-{uuid.uuid4().hex[:8]}"
        self._config = config

    def start(self) -> None:
        _run(["docker", "create", "--name", self.name, "--env", "DUORA_DOMAIN=localhost",
              "--add-host", "duora-web:127.0.0.1", IMAGE])
        # docker cp, não bind mount: o arquivo gerado sai do repositório e o Docker do host pode não enxergar o temporário.
        with tempfile.TemporaryDirectory() as directory:
            caddyfile = Path(directory) / "Caddyfile"
            caddyfile.write_text(self._config, encoding="utf-8")
            echo = Path(directory) / "echo.Caddyfile"
            echo.write_text(ECHO_CADDYFILE, encoding="utf-8")
            _run(["docker", "cp", str(caddyfile), f"{self.name}:/etc/caddy/Caddyfile"])
            _run(["docker", "cp", str(echo), f"{self.name}:/tmp/echo.Caddyfile"])
        _run(["docker", "start", self.name])
        _run(["docker", "exec", "--detach", self.name, "caddy", "run", "--adapter", "caddyfile",
              "--config", "/tmp/echo.Caddyfile"])
        self._wait_until_ready()
        self.own_address = _run(["docker", "exec", self.name, "hostname", "-i"]).stdout.split()[0]

    def stop(self) -> None:
        remove_container(self.name)

    def get(self, path: str, headers: dict[str, str] | None = None, *, through: str = TRAEFIK_ROLE) -> dict[str, str]:
        """Pedido à borda a partir de `through`; devolve o que o web de eco recebeu."""
        result = self._wget(path, headers or {}, through)
        if result.returncode != 0:
            raise AssertionError(f"pedido a {path} falhou: {result.stderr[-800:]}")
        pairs = (line.partition("=") for line in result.stdout.split(ECHO_SEPARATOR))
        return {name: value for name, _, value in pairs}

    def logged_entry(self, uri: str) -> dict:
        deadline = time.monotonic() + LOG_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            logs = subprocess.run(["docker", "logs", self.name], capture_output=True, text=True, check=False,
                                  timeout=REQUEST_TIMEOUT_SECONDS)
            for line in (logs.stdout + logs.stderr).splitlines():
                try:
                    entry = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(entry, dict) and isinstance(entry.get("request"), dict) \
                        and entry["request"].get("uri") == uri and "status" in entry:
                    return entry
            time.sleep(LOG_POLL_SECONDS)
        raise AssertionError(f"sem entrada de access log para {uri}")

    def _wget(self, path: str, headers: dict[str, str], through: str) -> subprocess.CompletedProcess[str]:
        header_options = [option for name, value in {"Host": "localhost", **headers}.items()
                          for option in ("--header", f"{name}: {value}")]
        return subprocess.run(
            ["docker", "exec", self.name, "wget", "-q", "-O-", "--timeout=5", *header_options,
             f"http://{through}:{EDGE_PORT}{path}"],
            capture_output=True, text=True, check=False, timeout=REQUEST_TIMEOUT_SECONDS)

    def _wait_until_ready(self) -> None:
        deadline = time.monotonic() + READY_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            if self._wget("/api/v1/ready", {}, TRAEFIK_ROLE).stdout.startswith(ECHO_FIELDS[0]):
                return
            time.sleep(READY_POLL_SECONDS)
        logs = subprocess.run(["docker", "logs", self.name], capture_output=True, text=True, check=False)
        raise AssertionError(f"borda de teste não ficou pronta: {(logs.stdout + logs.stderr)[-800:]}")


def _run(command: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, capture_output=True, text=True, check=True, timeout=READY_TIMEOUT_SECONDS)


@unittest.skipUnless(docker_available(), "precisa de Docker")
class ClientIpChainTests(unittest.TestCase):
    edge: _ChainContainer

    @classmethod
    def setUpClass(cls) -> None:
        cls.edge = _ChainContainer(caddyfile_behind_traefik(CADDYFILE.read_text(encoding="utf-8"), TRAEFIK_ROLE))
        # Antes do start: tearDownClass não roda se o setUpClass falhar, e um container criado também precisa sair.
        cls.addClassCleanup(cls.edge.stop)
        cls.edge.start()

    def forwarded_for(self, headers: dict[str, str], path: str = "/api/v1/login") -> str:
        return self.edge.get(path, headers)["X-Forwarded-For"]

    def test_the_web_receives_only_the_cloudflare_client(self) -> None:
        self.assertEqual(CLIENT_A, self.forwarded_for({"Cf-Connecting-IP": CLIENT_A}))

    def test_distinct_clients_reach_the_web_as_distinct_addresses(self) -> None:
        self.assertEqual(CLIENT_A, self.forwarded_for({"Cf-Connecting-IP": CLIENT_A}))
        self.assertEqual(CLIENT_B, self.forwarded_for({"Cf-Connecting-IP": CLIENT_B}))

    def test_forwarded_for_forged_by_the_client_never_reaches_the_web(self) -> None:
        # A Cloudflare e o Traefik acrescentam ao que o cliente mandou: o forjado chega aqui à esquerda da cadeia.
        chain = f"{FORGED}, 162.158.1.1, 172.71.2.2"

        self.assertEqual(CLIENT_A, self.forwarded_for({"Cf-Connecting-IP": CLIENT_A, "X-Forwarded-For": chain}))

    def test_rotating_cloudflare_edges_keep_the_same_client(self) -> None:
        for edge_address in ("162.158.1.1", "172.71.2.2", "104.23.3.3"):
            with self.subTest(edge_address=edge_address):
                headers = {"Cf-Connecting-IP": CLIENT_A, "X-Forwarded-For": f"{CLIENT_A}, {edge_address}"}
                self.assertEqual(CLIENT_A, self.forwarded_for(headers))

    def test_ipv6_clients_of_one_slash_64_arrive_whole_for_the_web_to_group(self) -> None:
        for client in (CLIENT_V6, "2001:db8:a:b:ffff:ffff:ffff:fffe"):
            with self.subTest(client=client):
                self.assertEqual(client, self.forwarded_for({"Cf-Connecting-IP": client}))

    def test_every_proxied_route_carries_the_client_and_the_scheme(self) -> None:
        for path in ("/api/v1/login", "/webhooks/fake", "/hubs/live"):
            with self.subTest(path=path):
                received = self.edge.get(path, {"Cf-Connecting-IP": CLIENT_A, "X-Forwarded-For": FORGED})
                self.assertEqual(CLIENT_A, received["X-Forwarded-For"])
                self.assertEqual("https", received["X-Forwarded-Proto"])

    def test_without_the_cloudflare_header_the_web_sees_the_trusted_peer_alone(self) -> None:
        self.assertEqual(TRAEFIK_ROLE, self.forwarded_for({"X-Forwarded-For": FORGED}))

    def test_an_unparsable_cloudflare_header_falls_back_to_the_trusted_peer(self) -> None:
        for value in ("not-an-ip", "", "1.2.3.4.5"):
            with self.subTest(value=value):
                self.assertEqual(TRAEFIK_ROLE, self.forwarded_for({"Cf-Connecting-IP": value}))

    def test_a_port_in_the_cloudflare_header_does_not_reach_the_web(self) -> None:
        self.assertEqual(CLIENT_A, self.forwarded_for({"Cf-Connecting-IP": f"{CLIENT_A}:443"}))

    def test_a_list_in_the_cloudflare_header_yields_its_rightmost_address(self) -> None:
        # trusted_proxies_strict: à esquerda da lista fica o que quem enviou escreveu.
        self.assertEqual(CLIENT_A, self.forwarded_for({"Cf-Connecting-IP": f"{FORGED}, {CLIENT_A}"}))

    def test_an_untrusted_peer_chooses_nothing(self) -> None:
        received = self.edge.get("/api/v1/login", {"Cf-Connecting-IP": FORGED, "X-Forwarded-For": FORGED},
                                 through=self.edge.own_address)

        self.assertEqual(self.edge.own_address, received["X-Forwarded-For"])

    def test_the_trusted_peer_is_believed_so_only_traefik_may_reach_the_edge(self) -> None:
        # Fronteira da opção B: o Caddy acredita em quem vier do Traefik. Barrar quem fala direto com a origem é papel
        # da ipAllowList do Traefik, que este teste não cobre.
        self.assertEqual(FORGED, self.forwarded_for({"Cf-Connecting-IP": FORGED}))

    def test_the_access_log_records_the_client_only_truncated(self) -> None:
        uri = f"/api/v1/log-{uuid.uuid4().hex}"
        self.edge.get(uri, {"Cf-Connecting-IP": CLIENT_A})

        request = self.edge.logged_entry(uri)["request"]

        self.assertEqual("198.51.100.0", request["client_ip"])
        self.assertEqual("127.0.0.0", request["remote_ip"])


@unittest.skipUnless(docker_available(), "precisa de Docker")
class ForwardedForRewriteControlTests(unittest.TestCase):
    """Sem o header_up, client_ip_headers sozinho não entrega o cliente ao web."""

    def test_client_ip_headers_alone_leaves_the_web_with_the_peer(self) -> None:
        config = caddyfile_behind_traefik(CADDYFILE.read_text(encoding="utf-8"), TRAEFIK_ROLE,
                                          rewrite_forwarded_for=False)
        edge = _ChainContainer(config)
        self.addCleanup(edge.stop)
        edge.start()

        received = edge.get("/api/v1/login", {"Cf-Connecting-IP": CLIENT_A, "X-Forwarded-For": FORGED})

        self.assertEqual(f"{FORGED}, {TRAEFIK_ROLE}", received["X-Forwarded-For"])


if __name__ == "__main__":
    unittest.main()
