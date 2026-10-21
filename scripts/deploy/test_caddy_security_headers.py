"""CSP e Permissions-Policy da borda (infra/caddy/Caddyfile) contra o Caddy real.

Sobe o Caddyfile do repositório na mesma imagem do test_caddy_access_log.py, com um site sintético em /srv
(index.html, JavaScript, áudio, vídeo e GLB) e um upstream sintético no lugar do web, e pede documentos, assets,
API, hub e erros pela porta publicada só no loopback do host. Compara os cabeçalhos com a política esperada, diretiva por diretiva.
Sem Docker o teste é pulado:

    python3 -m unittest discover -s scripts/deploy -p 'test_caddy_security_headers.py' -v

Com DUORA_TEST_BROWSER=1, roda também a sonda Chromium (test_caddy_browser.cjs), que exige as dependências do
frontend instaladas e o Chromium do Playwright (`pnpm exec playwright install chromium`).

A política esperada mora em caddy_test_support.py, compartilhada com o smoke_local.py: mudar a CSP ou a
Permissions-Policy exige mudá-la no mesmo PR (docs/operations/deploy.md, "Cabeçalhos de segurança da borda").
"""

from __future__ import annotations

import http.client
import json
import os
import re
import shutil
import ssl
import subprocess
import tempfile
import time
import unittest
import uuid
import wave
from pathlib import Path

from caddy_test_support import (ALLOWED_FEATURES, CADDYFILE, DENIED_FEATURES, EXPECTED_CSP,
                                EXPECTED_PERMISSIONS_POLICY, IMAGE, READY_TIMEOUT_SECONDS, REPO, docker_available,
                                is_exact_policy, parse_csp, parse_permissions_policy, remove_container)

# Fontes que abririam a política: curinga em qualquer posição, qualquer palavra-chave unsafe ('unsafe-*' e
# 'wasm-unsafe-eval'), esquema genérico, qualquer http: e os esquemas locais blob:, data:, mediastream: e filesystem:.
FORBIDDEN_SOURCE = re.compile(r"\*|'unsafe-|-unsafe-|^(https|wss?|filesystem):$|^(http|blob|data|mediastream):",
                              re.IGNORECASE)
# Única exceção ao FORBIDDEN_SOURCE: a textura embutida no GLB, lida por fetch(blob:) (docs/operations/deploy.md).
ALLOWED_LOCAL_SOURCES = {("connect-src", "blob:")}

# Tipo que o Caddy 2.11.7 dá a cada extensão de mídia do catálogo (ADR-0027): AAC-LC em .m4a, Opus em .webm/.ogg e o
# GLB do 3D em tempo real. Com outro tipo, o nosniff faz o navegador recusar o arquivo.
MEDIA_TYPES = {
    "/assets/sample.m4a": "audio/mp4",
    "/assets/sample.ogg": "audio/ogg",
    "/assets/sample.opus": "audio/ogg",
    "/assets/sample.webm": "video/webm",
    "/assets/sample.glb": "model/gltf-binary",
}
MEDIA_SIZE_BYTES = 4096
UPSTREAM_ADMIN = "127.0.0.1:2020"
CADDY_LOCAL_ROOT_CA = "/data/caddy/pki/authorities/local/root.crt"

UPSTREAM_CADDYFILE = f"""{{
    admin {UPSTREAM_ADMIN}
    auto_https off
}}
http://:8080 {{
    bind 127.0.0.1
    header X-Duora-Test-Upstream "yes"
    @conflicting path /api/conflicting-headers
    header @conflicting Content-Security-Policy "default-src * 'unsafe-inline' 'unsafe-eval'"
    header @conflicting Permissions-Policy "camera=*, microphone=*"
    @upload path /api/v1/me/photos
    handle @upload {{
        header Content-Type application/problem+json
        header Cache-Control no-store
        header Pragma no-cache
        header Vary "Accept-Encoding, Duora-Validation-Format"
        header X-Validation-Request "{{http.request.header.Duora-Validation-Format}}"
        @codes header X-Synthetic-Response codes
        handle @codes {{
            header Duora-Validation-Format codes-v1
            respond `{{"errors":{{"body":["PHOTO_BODY_EMPTY"]}}}}` 400
        }}
        respond `{{"errors":{{"body":["A foto está vazia."]}}}}` 400
    }}
    respond /api/unauthorized 401
    respond /api/forbidden 403
    respond /api/failure 500
    respond /hubs/unauthorized 401
    respond /hubs/missing 404
    respond "synthetic upstream" 200
}}
"""


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
        for source in ["'self'", "'none'", "wss://localhost", "https://meetduora.com", "'script'", "lit-html",
                       "'sha256-PXtZZkJMMyc18o/DN1ZEFOJ2qEGq95IQbVaJFMOXOb0='"]:
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


class ExpectedPolicyTests(unittest.TestCase):
    """Sem Docker: a política esperada é uma allowlist, não uma denylist de poucas features."""

    def test_only_voice_and_audio_features_are_allowed_and_only_for_the_own_origin(self) -> None:
        allowed = {feature: allowlist for feature, allowlist in EXPECTED_PERMISSIONS_POLICY.items()
                   if allowlist != "()"}
        self.assertEqual({"autoplay": "(self)", "microphone": "(self)", "speaker-selection": "(self)"}, allowed)

    def test_no_feature_is_both_allowed_and_denied_or_listed_twice(self) -> None:
        self.assertEqual(len(ALLOWED_FEATURES) + len(DENIED_FEATURES), len(EXPECTED_PERMISSIONS_POLICY))

    def test_powerful_features_the_product_does_not_use_are_denied(self) -> None:
        # Amostra fixa do que a matriz de capacidades nega: câmera e tela (voz sem vídeo), AR e sensores (o
        # <model-viewer> sem ar), tela cheia, clipboard, passkeys (spec 11.1: depois), pagamento e hardware.
        for feature in ["camera", "display-capture", "xr-spatial-tracking", "accelerometer", "gyroscope",
                        "magnetometer", "fullscreen", "clipboard-read", "clipboard-write", "publickey-credentials-get",
                        "payment", "usb", "serial", "hid", "bluetooth", "midi", "geolocation", "screen-wake-lock"]:
            with self.subTest(feature=feature):
                self.assertEqual("()", EXPECTED_PERMISSIONS_POLICY.get(feature))


@unittest.skipUnless(docker_available(), "precisa de Docker")
class CaddySecurityHeadersTests(unittest.TestCase):
    container = ""
    site = ""
    port = 0
    tls: ssl.SSLContext
    headers: dict[str, list[str]] = {}

    @classmethod
    def setUpClass(cls) -> None:
        # Limpeza registrada antes de cada recurso existir: tearDownClass não roda se o setUpClass falhar no meio.
        cls.site = tempfile.mkdtemp(prefix="duora-caddy-headers-")
        cls.addClassCleanup(shutil.rmtree, cls.site, ignore_errors=True)
        _write_site(Path(cls.site))
        cls.container = f"duora-caddy-headers-{uuid.uuid4().hex[:8]}"
        # Antes do run: um container criado que não sobe também precisa sair.
        cls.addClassCleanup(remove_container, cls.container)
        # Porta efêmera só no loopback do host: o teste fala HTTPS com o Caddy como o navegador falaria.
        _run(["docker", "run", "--detach", "--name", cls.container,
              "--env", "DUORA_DOMAIN=localhost", "--env", "DUORA_TLS=internal", "--add-host", "web:127.0.0.1",
              "--publish", "127.0.0.1::443",
              "--volume", f"{CADDYFILE}:/etc/caddy/Caddyfile:ro", "--volume", f"{cls.site}:/srv:ro", IMAGE])
        _run(["docker", "exec", "--detach", cls.container, "caddy", "run", "--config",
              "/srv/upstream.Caddyfile", "--adapter", "caddyfile"])
        cls.port = int(_output(["docker", "port", cls.container, "443/tcp"]).splitlines()[0].rsplit(":", 1)[1])
        cls._wait_ready()
        cls.headers = cls._document_headers()

    @classmethod
    def _wait_ready(cls) -> None:
        deadline = time.monotonic() + READY_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            # A CA local nasce com o primeiro certificado; confiar só nela, nunca desligar a verificação.
            root = subprocess.run(["docker", "exec", cls.container, "cat", CADDY_LOCAL_ROOT_CA],
                                  capture_output=True, text=True, check=False)
            if root.returncode == 0:
                cls.tls = ssl.create_default_context(cadata=root.stdout)
                try:
                    status, headers = cls._response("/api/ok")
                    if status == 200 and headers.get("x-duora-test-upstream") == ["yes"]:
                        return
                except (OSError, http.client.HTTPException):
                    pass
            time.sleep(0.5)
        raise AssertionError("Caddy não ficou pronto")

    @classmethod
    def _document_headers(cls) -> dict[str, list[str]]:
        status, headers = cls._response("/")
        if status != 200:
            raise AssertionError(f"GET / falhou: {status}")
        return headers

    @classmethod
    def _response(cls, path: str, accept: str = "text/html",
                  extra: dict[str, str] | None = None) -> tuple[int, dict[str, list[str]]]:
        connection = http.client.HTTPSConnection("localhost", cls.port, context=cls.tls, timeout=5)
        try:
            # Host sem a porta efêmera: o que o navegador manda na 443 de produção, e o que {hostport} vira na CSP.
            connection.request("GET", path, headers={"Host": "localhost", "Accept": accept, **(extra or {})})
            response = connection.getresponse()
            # Lista por nome: um segundo cabeçalho CSP somaria uma política (CSP3 3.1) e não pode sumir num dict.
            headers: dict[str, list[str]] = {}
            for name, value in response.getheaders():
                headers.setdefault(name.lower(), []).append(value)
            response.read()
            return response.status, headers
        finally:
            connection.close()

    def test_upload_validation_headers_and_bodies_survive_the_real_edge(self) -> None:
        # The upstream is deliberately synthetic. Endpoint semantics are tested through Kestrel;
        # this test exercises the repository's unchanged edge configuration in a real Caddy.
        for opt_in, coded_response in [(False, False), (True, True), (True, False)]:
            with self.subTest(opt_in=opt_in, coded_response=coded_response):
                connection = http.client.HTTPSConnection("localhost", self.port, context=self.tls, timeout=5)
                try:
                    headers = {"Host": "localhost", "Content-Type": "image/jpeg"}
                    if opt_in:
                        headers["Duora-Validation-Format"] = "codes-v1"
                    if coded_response:
                        headers["X-Synthetic-Response"] = "codes"
                    connection.request("POST", "/api/v1/me/photos", body=b"", headers=headers)
                    response = connection.getresponse()
                    received: dict[str, list[str]] = {}
                    for name, value in response.getheaders():
                        received.setdefault(name.lower(), []).append(value)
                    self.assertEqual(400, response.status)
                    self.assertEqual({"errors": {"body": ["PHOTO_BODY_EMPTY" if coded_response else "A foto está vazia."]}},
                                     json.loads(response.read()))
                    self.assertEqual(["codes-v1"] if coded_response else None,
                                     received.get("duora-validation-format"))
                    if opt_in:
                        self.assertEqual(["codes-v1"], received.get("x-validation-request"))
                    vary = [value.strip().lower() for line in received.get("vary", []) for value in line.split(",")]
                    self.assertIn("duora-validation-format", vary)
                    self.assertIn("accept-encoding", vary)
                    self.assertEqual(["no-store"], received.get("cache-control"))
                    self.assertEqual(["no-cache"], received.get("pragma"))
                    self.assertNotIn("retry-after", received)
                    self.assert_edge_policies("/api/v1/me/photos", received)
                finally:
                    connection.close()

    def assert_edge_policies(self, path: str, headers: dict[str, list[str]]) -> None:
        self.assertNotIn("server", headers)
        self.assertNotIn("via", headers)
        self.assertEqual(["max-age=31536000"], headers.get("strict-transport-security"))
        self.assertEqual(["nosniff"], headers.get("x-content-type-options"))
        self.assertEqual(["same-origin"], headers.get("cross-origin-resource-policy"))
        self.assertTrue(is_exact_policy(headers.get("content-security-policy", []), parse_csp, EXPECTED_CSP),
                        f"{path}: CSP {headers.get('content-security-policy')}")
        self.assertTrue(is_exact_policy(headers.get("permissions-policy", []), parse_permissions_policy,
                                        EXPECTED_PERMISSIONS_POLICY),
                        f"{path}: Permissions-Policy {headers.get('permissions-policy')}")

    def test_policies_on_static_spa_media_proxy_and_error_responses(self) -> None:
        cases = [("/", 200), ("/rooms/synthetic", 200), ("/assets/probe.js", 200),
                 *[(path, 200) for path in MEDIA_TYPES],
                 ("/assets/missing.js", 404), ("/assets/missing.m4a", 404), ("/missing", 404),
                 ("/health/ready", 404), ("/openapi/v1.json", 404), ("/api/ok", 200), ("/api/unauthorized", 401),
                 ("/api/forbidden", 403), ("/api/failure", 500), ("/hubs/ok", 200), ("/hubs/unauthorized", 401),
                 ("/hubs/missing", 404), ("/webhooks/probe", 200)]
        for path, expected_status in cases:
            with self.subTest(path=path):
                accept = "text/html" if path in ("/", "/rooms/synthetic") else "*/*"
                status, headers = self._response(path, accept)
                self.assertEqual(expected_status, status)
                self.assert_edge_policies(path, headers)
                if path.startswith(("/api/", "/hubs/", "/webhooks/")):
                    self.assertEqual(["yes"], headers.get("x-duora-test-upstream"))

    def test_error_generated_by_the_edge_is_never_cached(self) -> None:
        # Sem handle_errors, o 404 do file_server herdava o Cache-Control imutável de /assets/.
        for path in ("/assets/missing.js", "/assets/missing.m4a", "/missing", "/health/ready"):
            with self.subTest(path=path):
                status, headers = self._response(path, "*/*")
                self.assertEqual(404, status)
                self.assertEqual(["no-store"], headers.get("cache-control"))

    def test_upstream_cannot_replace_or_add_edge_policies(self) -> None:
        # O backend não deve emitir CSP nem Permissions-Policy; se emitir, a borda continua mandando uma só, a dela.
        status, headers = self._response("/api/conflicting-headers", "*/*")

        self.assertEqual(200, status)
        self.assert_edge_policies("/api/conflicting-headers", headers)

    def test_media_assets_have_their_type_immutable_cache_and_no_compression(self) -> None:
        for path, media_type in MEDIA_TYPES.items():
            with self.subTest(path=path):
                status, headers = self._response(path, "*/*", {"Accept-Encoding": "zstd, gzip"})
                self.assertEqual(200, status)
                self.assertEqual([media_type], headers.get("content-type"))
                self.assertEqual([str(MEDIA_SIZE_BYTES)], headers.get("content-length"))
                # Mídia já comprimida: recomprimir gasta CPU e quebra o Range que o <audio> usa para buscar trechos.
                self.assertNotIn("content-encoding", headers)
                self.assertEqual(["public, max-age=31536000, immutable"], headers.get("cache-control"))

    def test_media_assets_answer_byte_ranges(self) -> None:
        # O elemento de mídia pede trechos (Range) para buscar e retomar; sem 206 o Safari não toca o áudio.
        for path in MEDIA_TYPES:
            with self.subTest(path=path):
                status, headers = self._response(path, "*/*",
                                                 {"Range": "bytes=100-199", "Accept-Encoding": "zstd, gzip"})
                self.assertEqual(206, status)
                self.assertEqual([f"bytes 100-199/{MEDIA_SIZE_BYTES}"], headers.get("content-range"))
                self.assertEqual(["100"], headers.get("content-length"))
                self.assertNotIn("content-encoding", headers)
                self.assert_edge_policies(path, headers)

    def test_z_upstream_unavailable_keeps_policies(self) -> None:
        # Último (ordem alfabética): encerra só o upstream sintético desta fixture.
        _run(["docker", "exec", self.container, "caddy", "stop", "--address", UPSTREAM_ADMIN])
        for path in ("/api/unavailable", "/hubs/unavailable"):
            with self.subTest(path=path):
                status, headers = self._response(path, "*/*")
                self.assertEqual(502, status)
                self.assert_edge_policies(path, headers)
                self.assertEqual(["no-store"], headers.get("cache-control"))

    @unittest.skipUnless(os.environ.get("DUORA_TEST_BROWSER") == "1", "sonda de navegador só com DUORA_TEST_BROWSER=1")
    def test_browser_enforces_the_policies(self) -> None:
        result = subprocess.run(["node", str(Path(__file__).with_name("test_caddy_browser.cjs"))],
                                env=dict(os.environ, DUORA_TEST_PORT=str(self.port),
                                         DUORA_TEST_FRONTEND=str(REPO / "frontend")),
                                capture_output=True, text=True, check=False, timeout=180)
        print(result.stdout, flush=True)
        self.assertEqual(0, result.returncode, result.stderr[-4000:])

    def single_header(self, name: str) -> str:
        values = self.headers.get(name, [])
        self.assertEqual(1, len(values), f"{name}: esperado um cabeçalho, recebido {values}")
        return values[0]

    def test_document_sends_one_csp_and_one_permissions_policy(self) -> None:
        self.assertEqual(1, len(self.headers.get("content-security-policy", [])))
        self.assertEqual(1, len(self.headers.get("permissions-policy", [])))

    def test_csp_has_exactly_the_expected_directives(self) -> None:
        self.assertEqual(EXPECTED_CSP, parse_csp(self.single_header("content-security-policy")))

    def test_csp_has_no_wildcard_unsafe_or_local_scheme_beyond_the_glb_texture(self) -> None:
        policy = parse_csp(self.single_header("content-security-policy"))

        opening = {(name, source) for name, sources in policy.items() for source in sources
                   if FORBIDDEN_SOURCE.search(source)}
        self.assertEqual(ALLOWED_LOCAL_SOURCES, opening)

    def test_csp_does_not_block_webrtc(self) -> None:
        # Sem a diretiva webrtc o navegador permite os candidatos ICE (CSP3 4.3.1); default-src não a substitui.
        self.assertNotIn("webrtc", parse_csp(self.single_header("content-security-policy")))

    def test_permissions_policy_is_exactly_the_allowlist(self) -> None:
        self.assertEqual(EXPECTED_PERMISSIONS_POLICY,
                         parse_permissions_policy(self.single_header("permissions-policy")))

    def test_permissions_policy_has_no_wildcard(self) -> None:
        self.assertNotIn("*", self.single_header("permissions-policy"))


def _write_site(site: Path) -> None:
    Path(site, "index.html").write_text('<!doctype html><div id="root"></div>', encoding="utf-8")
    assets = site / "assets"
    assets.mkdir()
    Path(assets, "probe.js").write_text("// synthetic asset\n", encoding="utf-8")
    # Bytes sintéticos: o tipo vem da extensão, e o Range e a compressão não dependem do conteúdo.
    for path in MEDIA_TYPES:
        Path(site, path.removeprefix("/")).write_bytes(bytes(range(256)) * (MEDIA_SIZE_BYTES // 256))
    # Áudio decodificável para a sonda de navegador (<audio> e Web Audio).
    with wave.open(str(assets / "probe.wav"), "wb") as audio:
        audio.setparams((1, 2, 8000, 8000, "NONE", "not compressed"))
        audio.writeframes(b"\x00\x00" * 8000)
    Path(site, "upstream.Caddyfile").write_text(UPSTREAM_CADDYFILE, encoding="utf-8")


def _run(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise AssertionError(f"{' '.join(command[:3])} falhou: {result.stderr.strip()[-800:]}")


def _output(command: list[str]) -> str:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise AssertionError(f"{' '.join(command[:3])} falhou: {result.stderr.strip()[-800:]}")
    return result.stdout


if __name__ == "__main__":
    unittest.main()
