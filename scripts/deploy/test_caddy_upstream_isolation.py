"""Alias privado do Duora com um segundo web na rede compartilhada (somente dados sintéticos)."""

from __future__ import annotations

import subprocess
import tempfile
import time
import unittest
import uuid
from pathlib import Path

import yaml

import duora_deploy as deploy
from caddy_readiness import wait_for_edge
from caddy_test_support import CADDYFILE, IMAGE, REPO, docker_available, remove_container

UPSTREAM = "duora-web"


class UpstreamContractTests(unittest.TestCase):
    def test_alias_is_private_and_all_proxy_routes_use_it(self) -> None:
        compose = yaml.safe_load((REPO / "infra/compose/compose.prod.yml").read_text())
        networks = compose["services"]["web"]["networks"]
        self.assertIn(UPSTREAM, networks["edge"]["aliases"])
        self.assertNotIn("coolify", networks)
        proxy_lines = [line.strip() for line in CADDYFILE.read_text().splitlines()
                       if line.strip().startswith("reverse_proxy ")]
        self.assertEqual(3, len(proxy_lines))
        for line in proxy_lines:
            self.assertEqual(f"{UPSTREAM}:8080", line.split()[1])


class DualNetworkUpstreamTests(unittest.TestCase):
    """Caddy real e duas redes Docker; o CI roda a topologia, sem depender da VPS."""

    edge = ""

    @classmethod
    def setUpClass(cls) -> None:
        if not docker_available():
            raise unittest.SkipTest("precisa de Docker; executar no CI remoto")
        token = uuid.uuid4().hex[:12]
        private, shared = f"duora-private-{token}", f"duora-shared-{token}"
        site = tempfile.TemporaryDirectory(prefix="duora-upstream-")
        cls.addClassCleanup(site.cleanup)
        root = Path(site.name)
        for name, status in [("duora", 200), ("foreign", 401)]:
            (root / f"{name}.Caddyfile").write_text(
                f':8080 {{\n respond "{name}-upstream" {status}\n}}\n')
        # A rede compartilhada tem IPv6 como no incidente. A privada continua somente IPv4.
        subnet = f"fd{token[:2]}:{token[2:6]}:{token[6:10]}::/64"
        for name, options in [(private, []), (shared, ["--ipv6", "--subnet", subnet])]:
            _run(["docker", "network", "create", *options, name])
            cls.addClassCleanup(_remove_network, name)
        compose = yaml.safe_load((REPO / "infra/compose/compose.prod.yml").read_text())
        aliases = compose["services"]["web"]["networks"]["edge"]["aliases"]
        upstream, foreign = f"duora-upstream-{token}", f"duora-foreign-{token}"
        for name, network, config, extra_aliases in [
            (upstream, private, "duora", ["web", *aliases]),
            (foreign, shared, "foreign", ["web", "foreign-web"]),
        ]:
            cls.addClassCleanup(remove_container, name)
            options = [option for alias in extra_aliases for option in ["--network-alias", alias]]
            _run(["docker", "run", "--detach", "--name", name, "--network", network, *options,
                  "--volume", f"{root / (config + '.Caddyfile')}:/etc/caddy/Caddyfile:ro", IMAGE])
        cls.edge = f"duora-edge-isolation-{token}"
        cls.addClassCleanup(remove_container, cls.edge)
        _run(["docker", "run", "--detach", "--name", cls.edge, "--network", shared,
              "--env", "DUORA_DOMAIN=localhost", "--env", "DUORA_TLS=internal",
              "--volume", f"{CADDYFILE}:/etc/caddy/Caddyfile:ro", IMAGE])
        _run(["docker", "network", "connect", private, cls.edge])
        # A porta de health pode responder antes da emissão do certificado interno.
        # O ensaio só começa com HTTP e HTTPS do upstream privado prontos, no mesmo deadline.
        wait_for_edge(cls.request)

    @classmethod
    def request(cls, url: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["docker", "exec", cls.edge, "wget", "-q", "-S", "-O-",
                               "--timeout=5", "--no-check-certificate", url],
                              capture_output=True, text=True, check=False, timeout=10)

    def test_foreign_web_is_reachable_but_readiness_uses_duora(self) -> None:
        foreign = self.request("http://foreign-web:8080/health/ready")
        self.assertNotEqual(0, foreign.returncode)
        self.assertIn("401", foreign.stderr)

        def runner(command: list[str], *, capture: bool = False, input_text: str | None = None) -> subprocess.CompletedProcess[str]:
            # Adaptar só o transporte Compose ao nome do contêiner descartável; a URL vem do código de produção.
            wget = command.index("wget")
            return subprocess.run(["docker", "exec", self.edge, *command[wget:]],
                                  capture_output=True, text=True, check=False, timeout=10)

        deploy._wait_ready([], runner, 5, time.sleep)

    def test_api_webhooks_and_hubs_reach_the_duora_upstream(self) -> None:
        for path in ["/api/v1/network-probe", "/webhooks/network-probe", "/hubs/network-probe"]:
            with self.subTest(path=path):
                response = self.request("https://localhost" + path)
                self.assertEqual(0, response.returncode, response.stderr)
                self.assertEqual("duora-upstream", response.stdout)

    def test_health_remains_private(self) -> None:
        response = self.request("https://localhost/health/ready")
        self.assertNotEqual(0, response.returncode)
        self.assertIn("404", response.stderr)


def _run(command: list[str]) -> None:
    subprocess.run(command, capture_output=True, text=True, check=True, timeout=60)


def _remove_network(name: str) -> None:
    subprocess.run(["docker", "network", "rm", name], capture_output=True, check=False, timeout=15)
