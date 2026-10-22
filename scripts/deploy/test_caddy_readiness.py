"""Regressões de prontidão HTTP/TLS sem Docker, rede ou espera real."""

from __future__ import annotations

import subprocess
import unittest

from caddy_readiness import wait_for_edge


class EdgeReadinessTests(unittest.TestCase):
    def test_http_ready_does_not_release_tests_before_tls_is_ready(self) -> None:
        clock = Clock()
        tls_attempts = 0

        def request(url: str) -> subprocess.CompletedProcess[str]:
            nonlocal tls_attempts
            if url.startswith("https://"):
                tls_attempts += 1
                return subprocess.CompletedProcess([], 0 if tls_attempts > 1 else 1,
                                                   stdout="duora-upstream", stderr="synthetic TLS not ready")
            return subprocess.CompletedProcess([], 0, stdout="ready", stderr="")

        wait_for_edge(request, sleep=clock.sleep, now=lambda: clock.value)
        self.assertEqual(2, tls_attempts)
        self.assertEqual(0.5, clock.value)

    def test_foreign_upstream_never_counts_as_ready(self) -> None:
        clock = Clock()

        def request(url: str) -> subprocess.CompletedProcess[str]:
            return subprocess.CompletedProcess([], 0, stdout="foreign-upstream" if url.startswith("https://") else "ready", stderr="")

        with self.assertRaisesRegex(AssertionError, "edge de teste"):
            wait_for_edge(request, sleep=clock.sleep, now=lambda: clock.value)
        self.assertEqual(30, clock.value)

    def test_http_failure_is_still_bounded_and_does_not_probe_tls(self) -> None:
        clock = Clock()
        urls: list[str] = []

        def request(url: str) -> subprocess.CompletedProcess[str]:
            urls.append(url)
            return subprocess.CompletedProcess([], 1, stdout="", stderr="synthetic not ready")

        with self.assertRaisesRegex(AssertionError, "edge de teste"):
            wait_for_edge(request, sleep=clock.sleep, now=lambda: clock.value)
        self.assertEqual(30, clock.value)
        self.assertTrue(all(url.startswith("http://") for url in urls))


class Clock:
    value = 0.0

    def sleep(self, seconds: float) -> None:
        self.value += seconds
