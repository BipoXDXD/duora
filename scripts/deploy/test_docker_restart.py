"""Testes do helper scripts/ci/docker-restart.sh (restart do daemon Docker depois do upgrade via apt, #549).

O helper é carregado com `source` num bash real. `sudo`, `systemctl`, `docker` e `sleep` são funções de shell
falsas: o `systemctl` falso imita o limite de partidas do systemd (StartLimitBurst) e responde `start-limit-hit`
quando o contador estoura, como no run 43260629565. Nada é reiniciado e nenhum tempo real é gasto: o `sleep`
falso só avança `SECONDS`.
"""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "scripts/ci/docker-restart.sh"
START_LIMIT_BURST = 3


class DockerRestartTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="duora-docker-restart-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def run_helper(self, *, starts_before: int = START_LIMIT_BURST, broken_restarts: int = 0,
                   containerd_active: bool = True, daemon_ready_after: int = 0):
        """Roda `docker_restart_after_upgrade` contra um systemd falso.

        starts_before: partidas já contadas pelo systemd (o postinst do docker-ce reinicia o serviço).
        broken_restarts: quantos restarts falham por outro motivo, mesmo com o contador zerado.
        containerd_active: se o containerd chega a ficar ativo.
        daemon_ready_after: quantas sondagens do daemon falham depois de cada restart bem-sucedido.
        """
        (self.root / "calls").write_text("")
        harness = textwrap.dedent(f'''
            starts={starts_before}
            broken={broken_restarts}
            probes=0
            sudo() {{ "$@"; }}
            systemctl() {{
              printf 'systemctl %s\\n' "$*" >> "$root/calls"
              case "$1" in
                reset-failed) starts=0 ;;
                restart)
                  if [ "$starts" -ge {START_LIMIT_BURST} ]; then
                    echo "docker.service: Failed with result 'start-limit-hit'." >&2
                    return 1
                  fi
                  starts=$((starts + 1))
                  if [ "$broken" -gt 0 ]; then broken=$((broken - 1)); return 1; fi
                  probes=0
                  ;;
                is-active) {"return 0" if containerd_active else "return 3"} ;;
                status) ;;
              esac
            }}
            journalctl() {{ printf 'journalctl\\n' >> "$root/calls"; }}
            test() {{
              # Só o socket do containerd é falso; o resto vai para o builtin.
              if [ "$*" = "-S /run/containerd/containerd.sock" ]; then return 0; fi
              builtin test "$@"
            }}
            docker() {{
              probes=$((probes + 1))
              printf 'docker %s\\n' "$*" >> "$root/calls"
              [ "$probes" -gt {daemon_ready_after} ]
            }}
            sleep() {{ printf 'sleep %s\\n' "$1" >> "$root/calls"; SECONDS=$((SECONDS + $1)); }}
            source "$helper"
            docker_restart_after_upgrade
        ''')
        env = {**os.environ, "root": str(self.root), "helper": str(HELPER)}
        return subprocess.run(["bash", "--noprofile", "--norc", "-euo", "pipefail", "-c", harness],
                              env=env, capture_output=True, text=True, timeout=30)

    def calls(self, prefix: str) -> list[str]:
        return [line for line in (self.root / "calls").read_text().splitlines() if line.startswith(prefix)]

    def total_sleep(self) -> int:
        return sum(int(line.split()[1]) for line in self.calls("sleep "))

    def test_clears_the_start_limit_left_by_apt_before_the_first_restart(self):
        result = self.run_helper(starts_before=START_LIMIT_BURST)
        self.assertEqual(0, result.returncode, result.stderr)
        systemctl = self.calls("systemctl ")
        self.assertTrue(systemctl[0].startswith("systemctl reset-failed docker.service"), systemctl)
        self.assertEqual(1, len(self.calls("systemctl restart")))
        self.assertNotIn("::warning", result.stdout)

    def test_waits_until_the_daemon_answers_after_the_restart(self):
        result = self.run_helper(daemon_ready_after=3)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(4, len(self.calls("docker ")))
        self.assertEqual(1, len(self.calls("systemctl restart")))

    def test_retries_once_with_diagnostics_when_the_first_restart_fails(self):
        result = self.run_helper(broken_restarts=1)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(self.calls("systemctl restart")))
        self.assertEqual(2, len(self.calls("systemctl reset-failed")))
        self.assertIn("::warning title=Restart do Docker falhou", result.stdout)
        self.assertTrue(self.calls("journalctl"))

    def test_fails_with_diagnostics_when_the_second_restart_also_fails(self):
        result = self.run_helper(broken_restarts=2)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(2, len(self.calls("systemctl restart")))
        self.assertEqual(2, len(self.calls("journalctl")))

    def test_daemon_that_never_answers_fails_within_the_bounded_wait(self):
        result = self.run_helper(daemon_ready_after=10_000)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(2, len(self.calls("systemctl restart")))
        # Duas esperas de no máximo 60 s pelo daemon e uma de no máximo 60 s pelo containerd.
        self.assertLessEqual(self.total_sleep(), 180)
        self.assertIn("daemon Docker não respondeu em 60 s", result.stdout)

    def test_containerd_that_never_becomes_active_stops_before_the_second_restart(self):
        result = self.run_helper(broken_restarts=1, containerd_active=False)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(1, len(self.calls("systemctl restart")))
        self.assertLessEqual(self.total_sleep(), 60)
        self.assertIn("containerd não ficou ativo em 60 s", result.stdout)


if __name__ == "__main__":
    unittest.main()
