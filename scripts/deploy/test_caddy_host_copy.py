"""Compara a fixture "atrás do Traefik" com a cópia versionada do arquivo do host (issue #396), sem Docker.

Só a janela do dono gera a cópia (docs/operations/deploy.md, "Cópia versionada do Caddyfile do host"). Enquanto ela não
existe, a comparação real sai como skipped, nunca como ok; os testes sintéticos abaixo rodam sempre e provam o comparador.

    python3 -m unittest discover -s scripts/deploy -p 'test_caddy_host_copy.py' -v
"""

from __future__ import annotations

import hashlib
import tempfile
import unittest
from pathlib import Path

from caddy_test_support import (
    CADDYFILE,
    HOST_COPY,
    HOST_COPY_DIGEST,
    HOST_COPY_MISSING,
    HOST_COPY_PENDING,
    HostCopyMismatch,
    HostCopyPending,
    expected_host_caddyfile,
    host_copy_differences,
    verify_host_copy,
)

REPO_CADDYFILE = CADDYFILE.read_text(encoding="utf-8")
HOST_COMMENT = "# Generated for Coolify TLS termination; trust only verified proxy IP.\n"
FORWARDED_FOR_LINE = "\t\t\theader_up X-Forwarded-For {client_ip}\n"
TRUST_LINE = "\t\ttrusted_proxies static 172.16.1.6\n"
CLIENT_IP_LINE = "\t\tclient_ip_headers Cf-Connecting-IP\n"


class ExpectedHostFileTests(unittest.TestCase):
    def test_the_host_comment_is_the_first_line(self) -> None:
        self.assertTrue(expected_host_caddyfile(REPO_CADDYFILE).startswith(HOST_COMMENT))

    def test_it_trusts_only_the_coolify_proxy_peer(self) -> None:
        expected = expected_host_caddyfile(REPO_CADDYFILE)

        self.assertEqual(1, expected.count(TRUST_LINE))
        self.assertEqual(1, expected.count("trusted_proxies static"))


class HostCopyDifferencesTests(unittest.TestCase):
    def setUp(self) -> None:
        self.expected = expected_host_caddyfile(REPO_CADDYFILE)

    def test_the_same_file_has_no_differences(self) -> None:
        self.assertEqual([], host_copy_differences(self.expected, REPO_CADDYFILE))

    def test_each_divergence_of_the_host_is_reported_with_its_line(self) -> None:
        cases = {
            "outro peer": (self.expected.replace(TRUST_LINE, "\t\ttrusted_proxies static 172.16.1.7\n"),
                           "trusted_proxies static 172.16.1.7"),
            "peer a mais": (self.expected.replace(TRUST_LINE, "\t\ttrusted_proxies static 172.16.1.6 10.0.0.1\n"),
                            "172.16.1.6 10.0.0.1"),
            "header_up a menos numa rota": (self.expected.replace(FORWARDED_FOR_LINE, "", 1), "X-Forwarded-For"),
            "linha a mais": (self.expected + "# linha nova\n", "# linha nova"),
            "antes da Fase 4": (self.expected.replace(CLIENT_IP_LINE, ""), "client_ip_headers"),
            "sem o comentário": (self.expected.removeprefix(HOST_COMMENT), "Generated for Coolify"),
        }
        for name, (copy, line) in cases.items():
            with self.subTest(name):
                differences = host_copy_differences(copy, REPO_CADDYFILE)

                self.assertTrue(any(line in difference for difference in differences), differences)


class VerifyHostCopyTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.copy = Path(directory.name) / "Caddyfile"
        self.digest = Path(directory.name) / "Caddyfile.sha256"

    def _write(self, content: str) -> None:
        self.copy.write_text(content, encoding="utf-8")
        self.digest.write_text(hashlib.sha256(content.encode()).hexdigest() + "\n", encoding="utf-8")

    def _verify(self, *, pending: bool) -> None:
        verify_host_copy(self.copy, self.digest, REPO_CADDYFILE, pending=pending)

    def test_pending_without_the_copy_is_reported_as_pending_with_the_instruction(self) -> None:
        with self.assertRaises(HostCopyPending) as caught:
            self._verify(pending=True)

        self.assertEqual(HOST_COPY_MISSING, str(caught.exception))
        self.assertIn("cópia do host ausente, gerar na janela", HOST_COPY_MISSING)

    def test_pending_with_the_copy_fails_so_the_marker_cannot_outlive_the_window(self) -> None:
        self._write(expected_host_caddyfile(REPO_CADDYFILE))

        with self.assertRaises(HostCopyMismatch):
            self._verify(pending=True)

    def test_pending_with_only_the_digest_fails(self) -> None:
        self.digest.write_text("0" * 64 + "\n", encoding="utf-8")

        with self.assertRaises(HostCopyMismatch):
            self._verify(pending=True)

    def test_a_copy_removed_after_the_window_fails_instead_of_skipping(self) -> None:
        with self.assertRaises(HostCopyMismatch) as caught:
            self._verify(pending=False)

        self.assertIn(HOST_COPY_MISSING, str(caught.exception))

    def test_a_copy_without_its_digest_fails(self) -> None:
        self.copy.write_text(expected_host_caddyfile(REPO_CADDYFILE), encoding="utf-8")

        with self.assertRaises(HostCopyMismatch):
            self._verify(pending=False)

    def test_a_copy_edited_without_the_digest_of_the_window_fails(self) -> None:
        self._write(expected_host_caddyfile(REPO_CADDYFILE))
        self.copy.write_text(expected_host_caddyfile(REPO_CADDYFILE) + "\n", encoding="utf-8")

        with self.assertRaises(HostCopyMismatch) as caught:
            self._verify(pending=False)

        self.assertIn("sha256", str(caught.exception))

    def test_a_divergent_copy_fails_showing_the_line(self) -> None:
        self._write(expected_host_caddyfile(REPO_CADDYFILE).replace(CLIENT_IP_LINE, ""))

        with self.assertRaises(HostCopyMismatch) as caught:
            self._verify(pending=False)

        self.assertIn("client_ip_headers", str(caught.exception))

    def test_a_matching_copy_with_its_digest_is_accepted(self) -> None:
        self._write(expected_host_caddyfile(REPO_CADDYFILE))

        self._verify(pending=False)


class VersionedHostCopyTests(unittest.TestCase):
    def test_the_fixture_matches_the_copy_of_the_host_file(self) -> None:
        try:
            verify_host_copy(HOST_COPY, HOST_COPY_DIGEST, REPO_CADDYFILE, pending=HOST_COPY_PENDING)
        except HostCopyPending as pending:
            self.skipTest(str(pending))


if __name__ == "__main__":
    unittest.main()
