"""Testes do watchdog dos testes do backend (scripts/ci/test-watchdog.sh, #507).

O createdump é substituído por um script que registra a chamada; o "host de teste" é um `sleep` com argv[0]
no formato dos módulos de teste do Duora. Nada é despejado de verdade e nenhum tempo real é gasto.
"""
from __future__ import annotations

from pathlib import Path
import os
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
WATCHDOG = ROOT / "scripts/ci/test-watchdog.sh"


class TestWatchdogTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="duora-watchdog-")
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.out = self.root / "out"
        self.out.mkdir()
        self.calls = self.root / "calls"

    def fake_createdump(self, exit_code: int = 0) -> Path:
        path = self.root / "createdump"
        path.write_text(textwrap.dedent(f"""\
            #!/usr/bin/env bash
            printf '%s\\n' "$*" >> {self.calls}
            exit {exit_code}
        """))
        path.chmod(0o755)
        return path

    def fake_test_host(self, name: str) -> subprocess.Popen[bytes]:
        host = subprocess.Popen(["bash", "-c", f"exec -a {name} sleep 60"])
        self.addCleanup(host.wait)
        self.addCleanup(host.kill)
        return host

    def run_watchdog(self, createdump: Path) -> subprocess.CompletedProcess[str]:
        env = {**os.environ, "DUORA_CREATEDUMP": str(createdump)}
        return subprocess.run(["bash", str(WATCHDOG), "0", str(self.out)], env=env,
                              capture_output=True, text=True, timeout=30, check=False)

    def test_dumps_every_live_test_host_into_the_output_directory(self):
        host = self.fake_test_host("Duora.FakeIntegrationTests")

        result = self.run_watchdog(self.fake_createdump())

        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.calls.read_text().splitlines()
        self.assertEqual(1, len(calls))
        self.assertTrue(calls[0].endswith(f" {host.pid}"), calls[0])
        self.assertIn("--withheap", calls[0])
        self.assertIn(f"--name {self.out}/", calls[0])
        self.assertIn(str(host.pid), result.stdout)

    def test_a_process_that_is_not_a_test_host_is_left_alone(self):
        self.fake_test_host("Duora.Web")

        result = self.run_watchdog(self.fake_createdump())

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.calls.exists())
        self.assertIn("nenhum host de teste", result.stdout)

    def test_a_failed_dump_never_fails_the_step(self):
        self.fake_test_host("Duora.FakeIntegrationTests")

        result = self.run_watchdog(self.fake_createdump(exit_code=1))

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("falhou", result.stdout)


if __name__ == "__main__":
    unittest.main()
