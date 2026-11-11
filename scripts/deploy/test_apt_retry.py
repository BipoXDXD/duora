"""Testes do helper scripts/ci/apt-retry.sh (retry de comandos de rede no CI).

O helper é carregado com `source` num bash real; `sudo` e `sleep` são substituídos por funções de shell
que registram as chamadas, então nada é instalado e nenhum tempo real é gasto.
"""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "scripts/ci/apt-retry.sh"
TRANSIENT_EXIT = 100  # código do apt-get para "Temporary failure resolving"


class AptRetryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="duora-apt-retry-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def run_helper(self, body: str, *, failures: int, extra_env: dict[str, str] | None = None):
        """Executa `body` com `sudo` falhando nas primeiras `failures` chamadas."""
        (self.root / "calls").write_text("")
        (self.root / "sleeps").write_text("")
        harness = textwrap.dedent(f'''
            sudo() {{
              printf '%s\\n' "$*" >> "$root/calls"
              # Só as chamadas do apt-get contam para as falhas simuladas; `dpkg --configure -a` sempre funciona.
              [ "$1" = apt-get ] || return 0
              local n
              n=$(grep -c '^apt-get ' "$root/calls")
              [ "$n" -gt {failures} ] || return {TRANSIENT_EXIT}
            }}
            sleep() {{ printf '%s\\n' "$1" >> "$root/sleeps"; }}
            source "$helper"
        ''')
        inherited = {k: v for k, v in os.environ.items() if not k.startswith("CI_RETRY_")}
        env = {**inherited, "root": str(self.root), "helper": str(HELPER), **(extra_env or {})}
        return subprocess.run(["bash", "--noprofile", "--norc", "-euo", "pipefail", "-c", harness + body],
                              env=env, capture_output=True, text=True, timeout=30)

    def calls(self) -> list[str]:
        return [line for line in (self.root / "calls").read_text().splitlines() if line.startswith("apt-get ")]

    def dpkg_recoveries(self) -> list[str]:
        return [line for line in (self.root / "calls").read_text().splitlines() if line.startswith("dpkg ")]

    def sleeps(self) -> list[str]:
        return (self.root / "sleeps").read_text().splitlines()

    def test_succeeds_on_the_first_attempt_without_waiting(self):
        result = self.run_helper('apt_retry -o Foo=bar update -qq', failures=0)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len(self.calls()))
        self.assertEqual([], self.sleeps())

    def test_apt_options_bound_retries_and_network_timeouts(self):
        self.run_helper('apt_retry -o Foo=bar update -qq', failures=0)
        call = self.calls()[0]
        for option in ("Acquire::Retries=5", "Acquire::http::Timeout=30", "Acquire::https::Timeout=30"):
            self.assertIn(option, call)
        self.assertTrue(call.startswith("apt-get "), call)
        self.assertTrue(call.endswith("-o Foo=bar update -qq"), call)

    def test_recovers_from_a_transient_failure(self):
        result = self.run_helper('apt_retry update -qq', failures=2)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(3, len(self.calls()))

    def test_default_backoff_waits_15_then_45_seconds(self):
        self.run_helper('apt_retry update -qq', failures=2)
        self.assertEqual(["15", "45"], self.sleeps())

    def test_delays_can_be_overridden(self):
        result = self.run_helper('apt_retry update -qq', failures=2, extra_env={"CI_RETRY_DELAYS": "1 2"})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["1", "2"], self.sleeps())

    def test_persistent_failure_stops_after_three_attempts_with_the_real_exit_code(self):
        result = self.run_helper('apt_retry update -qq', failures=99)
        self.assertEqual(TRANSIENT_EXIT, result.returncode, result.stderr)
        self.assertEqual(3, len(self.calls()))
        self.assertIn("3 tentativas", result.stderr)

    def test_a_failed_install_does_not_let_the_script_continue(self):
        result = self.run_helper('apt_retry install -y pacote\necho depois-do-install', failures=99)
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("depois-do-install", result.stdout)

    def test_a_retried_install_first_finishes_a_half_configured_dpkg(self):
        result = self.run_helper('apt_retry install -y pacote', failures=2)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["dpkg --configure -a"] * 2, self.dpkg_recoveries())

    def test_dpkg_recovery_failure_does_not_abort_the_retry(self):
        body = 'sudo() { [ "$1" = dpkg ] && return 1; apt_calls=$((${apt_calls:-0} + 1)); [ "$apt_calls" -gt 1 ]; }\n' \
               'apt_retry install -y pacote'
        result = self.run_helper(body, failures=0)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_update_does_not_run_the_dpkg_recovery(self):
        self.run_helper('apt_retry update -qq', failures=2)
        self.assertEqual([], self.dpkg_recoveries())

    def test_a_successful_first_install_does_not_touch_dpkg(self):
        self.run_helper('apt_retry install -y pacote', failures=0)
        self.assertEqual([], self.dpkg_recoveries())

    def test_attempt_timeout_sends_sigterm_first_and_sigkill_after_10_seconds(self):
        # `timeout` de mentira: registra os argumentos em vez de rodar o comando.
        body = 'timeout() { printf "%s\\n" "$*" >> "$root/timeout-args"; }\n' \
               'retry_with_backoff env true'
        result = self.run_helper(body, failures=0, extra_env={"CI_RETRY_ATTEMPT_TIMEOUT": "180"})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("-k 10 180 env true", (self.root / "timeout-args").read_text().strip())

    @unittest.skipUnless(shutil.which("timeout"), "GNU timeout ausente")
    def test_attempt_timeout_kills_a_hung_command_and_retries(self):
        # `env` força o binário externo: o `timeout` só consegue matar processo, não função de shell.
        body = 'unset -f sleep\nretry_with_backoff env sleep 5'
        result = self.run_helper(body, failures=0,
                                 extra_env={"CI_RETRY_DELAYS": "0 0", "CI_RETRY_ATTEMPT_TIMEOUT": "0.2"})
        self.assertEqual(124, result.returncode, result.stderr)
        self.assertIn("3 tentativas", result.stderr)


if __name__ == "__main__":
    unittest.main()
