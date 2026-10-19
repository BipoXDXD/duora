"""CSP e Permissions-Policy da borda (infra/caddy/Caddyfile) contra o Caddy real.

Sobe o Caddyfile do repositório na mesma imagem do test_caddy_access_log.py, com um index.html mínimo em /srv, pede
o documento de dentro do container e compara os cabeçalhos com a política esperada, diretiva por diretiva. Sem
Docker o teste é pulado:

    python3 -m unittest discover -s scripts/deploy -p 'test_caddy_security_headers.py' -v

A política esperada mora em caddy_test_support.py, compartilhada com o smoke_local.py: mudar a CSP ou a
Permissions-Policy exige mudá-la no mesmo PR (docs/operations/deploy.md, "Cabeçalhos de segurança da borda").
"""

from __future__ import annotations

import re
import shutil
import subprocess
import tempfile
import time
import unittest
import uuid
from pathlib import Path

from caddy_test_support import (CADDYFILE, EXPECTED_CSP, EXPECTED_PERMISSIONS_POLICY, IMAGE, READY_TIMEOUT_SECONDS,
                                docker_available, is_exact_policy, parse_csp, parse_permissions_policy,
                                remove_container)

# Fontes que abririam a política: curinga em qualquer posição, qualquer palavra-chave unsafe ('unsafe-*' e
# 'wasm-unsafe-eval'), esquema genérico, qualquer http: e os esquemas locais blob:, data:, mediastream: e filesystem:.
FORBIDDEN_SOURCE = re.compile(r"\*|'unsafe-|-unsafe-|^(https|wss?|filesystem):$|^(http|blob|data|mediastream):",
                              re.IGNORECASE)


class PolicyParserTests(unittest.TestCase):
    """Sem Docker: o parser não pode esconder a política que o navegador de fato aplica."""

    def test_repeated_csp_directive_is_refused(self) -> None:
        # O navegador aplica a primeira ocorrência e ignora as seguintes (CSP3 2.2.1); guardar a última esconderia
        # um script-src aberto.
        with self.assertRaises(ValueError):
            parse_csp("script-src * 'unsafe-inline' 'unsafe-eval'; script-src 'self'")

    def test_repeated_csp_directive_is_refused_regardless_of_case(self) -> None:
        with self.assertRaises(ValueError):
            parse_csp("img-src *; IMG-SRC 'self'")

    def test_comma_separated_csp_policies_are_refused(self) -> None:
        # Vírgula separa políticas num mesmo cabeçalho (CSP3 3.1); duas políticas não são a política esperada.
        with self.assertRaises(ValueError):
            parse_csp("default-src 'none', script-src *")

    def test_csp_directives_keep_their_sources(self) -> None:
        self.assertEqual({"default-src": ["'none'"], "connect-src": ["'self'", "wss://localhost"]},
                         parse_csp("default-src 'none'; connect-src 'self' wss://localhost;"))

    def test_repeated_permissions_policy_feature_is_refused(self) -> None:
        with self.assertRaises(ValueError):
            parse_permissions_policy("microphone=(self), microphone=*")

    def test_forbidden_source_catches_every_opening_source(self) -> None:
        for source in ["*", "*.example.com", "https://*.example.com", "'unsafe-inline'", "'unsafe-eval'",
                       "'unsafe-hashes'", "'wasm-unsafe-eval'", "https:", "wss:", "ws:", "http:",
                       "http://cdn.example.com", "blob:", "data:", "mediastream:", "filesystem:"]:
            with self.subTest(source=source):
                self.assertRegex(source, FORBIDDEN_SOURCE)

    def test_forbidden_source_keeps_the_expected_sources(self) -> None:
        for source in ["'self'", "'none'", "wss://localhost", "https://meetduora.com"]:
            with self.subTest(source=source):
                self.assertNotRegex(source, FORBIDDEN_SOURCE)

    def test_single_header_with_the_expected_policy_matches(self) -> None:
        self.assertTrue(is_exact_policy(["camera=(), microphone=(self)"], parse_permissions_policy,
                                        {"camera": "()", "microphone": "(self)"}))

    def test_two_headers_never_match_even_if_one_is_the_expected_policy(self) -> None:
        self.assertFalse(is_exact_policy(["default-src *", "default-src 'none'"], parse_csp,
                                         {"default-src": ["'none'"]}))

    def test_missing_header_never_matches(self) -> None:
        self.assertFalse(is_exact_policy([], parse_csp, {}))

    def test_ambiguous_policy_does_not_match_instead_of_raising(self) -> None:
        self.assertFalse(is_exact_policy(["script-src *; script-src 'self'"], parse_csp, {"script-src": ["'self'"]}))

    def test_permissions_policy_features_keep_their_allowlist(self) -> None:
        self.assertEqual({"camera": "()", "microphone": "(self)"},
                         parse_permissions_policy("camera=(), microphone=(self)"))


@unittest.skipUnless(docker_available(), "precisa de Docker")
class CaddySecurityHeadersTests(unittest.TestCase):
    container = ""
    site = ""
    headers: dict[str, list[str]] = {}

    @classmethod
    def setUpClass(cls) -> None:
        # Limpeza registrada antes de cada recurso existir: tearDownClass não roda se o setUpClass falhar no meio.
        cls.site = tempfile.mkdtemp(prefix="duora-caddy-headers-")
        cls.addClassCleanup(shutil.rmtree, cls.site, ignore_errors=True)
        Path(cls.site, "index.html").write_text('<!doctype html><div id="root"></div>', encoding="utf-8")
        cls.container = f"duora-caddy-headers-{uuid.uuid4().hex[:8]}"
        # Antes do run: um container criado que não sobe também precisa sair.
        cls.addClassCleanup(remove_container, cls.container)
        _run(["docker", "run", "--detach", "--name", cls.container,
              "--env", "DUORA_DOMAIN=localhost", "--env", "DUORA_TLS=internal",
              "--volume", f"{CADDYFILE}:/etc/caddy/Caddyfile:ro", "--volume", f"{cls.site}:/srv:ro", IMAGE])
        cls._wait_ready()
        cls.headers = cls._document_headers()

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

    @classmethod
    def _document_headers(cls) -> dict[str, list[str]]:
        # wget do BusyBox: -S escreve os cabeçalhos da resposta no stderr, um por linha, com dois espaços na frente.
        result = subprocess.run(["docker", "exec", cls.container, "wget", "-S", "-q", "-O", "/dev/null",
                                 "--no-check-certificate", "--header", "Accept: text/html",
                                 "https://localhost/"], capture_output=True, text=True, check=False)
        if result.returncode != 0:
            raise AssertionError(f"GET / falhou: {result.stderr.strip()[-800:]}")
        # Lista por nome: um segundo cabeçalho CSP somaria uma política (CSP3 3.1) e não pode sumir num dict.
        headers: dict[str, list[str]] = {}
        for line in result.stderr.splitlines():
            name, separator, value = line.strip().partition(": ")
            if separator:
                headers.setdefault(name.lower(), []).append(value)
        return headers

    def single_header(self, name: str) -> str:
        values = self.headers.get(name, [])
        self.assertEqual(1, len(values), f"{name}: esperado um cabeçalho, recebido {values}")
        return values[0]

    def test_document_sends_one_csp_and_one_permissions_policy(self) -> None:
        self.assertEqual(1, len(self.headers.get("content-security-policy", [])))
        self.assertEqual(1, len(self.headers.get("permissions-policy", [])))

    def test_csp_has_exactly_the_expected_directives(self) -> None:
        self.assertEqual(EXPECTED_CSP, parse_csp(self.single_header("content-security-policy")))

    def test_csp_has_no_wildcard_unsafe_or_blob_source(self) -> None:
        policy = parse_csp(self.single_header("content-security-policy"))

        opening = {name: [source for source in sources if FORBIDDEN_SOURCE.search(source)]
                   for name, sources in policy.items()}
        self.assertEqual({}, {name: sources for name, sources in opening.items() if sources})

    def test_csp_does_not_block_webrtc(self) -> None:
        # Sem a diretiva webrtc o navegador permite os candidatos ICE (CSP3 4.3.1); default-src não a substitui.
        self.assertNotIn("webrtc", parse_csp(self.single_header("content-security-policy")))

    def test_permissions_policy_allows_only_the_own_origin_microphone(self) -> None:
        self.assertEqual(EXPECTED_PERMISSIONS_POLICY,
                         parse_permissions_policy(self.single_header("permissions-policy")))

    def test_permissions_policy_has_no_wildcard(self) -> None:
        self.assertNotIn("*", self.single_header("permissions-policy"))


def _run(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise AssertionError(f"{' '.join(command[:3])} falhou: {result.stderr.strip()[-800:]}")


if __name__ == "__main__":
    unittest.main()
