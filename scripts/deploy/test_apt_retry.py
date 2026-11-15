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
            # DNS de verdade fica fora dos testes: por padrão o espelho do runner resolve; quem testa a sonda redefine.
            _apt_host_resolves() {{ return 0; }}
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
        # Falha persistente: 3 tentativas, logo 2 recuperações antes das repetições.
        self.run_helper('apt_retry install -y pacote', failures=99)
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

    def mirror_switches(self) -> list[str]:
        return [line for line in (self.root / "calls").read_text().splitlines() if line.startswith("perl ")]

    def test_a_retry_first_switches_to_the_fallback_mirror(self):
        mirrors = self.root / "apt-mirrors.txt"
        mirrors.write_text("http://azure.archive.ubuntu.com/ubuntu/\n")
        result = self.run_helper('apt_retry update -qq', failures=2, extra_env={"CI_APT_MIRROR_FILES": str(mirrors)})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(self.mirror_switches()))

    def test_a_first_attempt_success_keeps_the_runner_mirror(self):
        self.run_helper('apt_retry install -y pacote', failures=0)
        self.assertEqual([], self.mirror_switches())

    def test_fallback_mirror_rewrites_only_ubuntu_com_hosts(self):
        scoped = self.root / "scoped"
        scoped.mkdir()
        mirrors = self.root / "apt-mirrors.txt"
        mirrors.write_text("http://azure.archive.ubuntu.com/ubuntu/\tpriority:1\n")
        (scoped / "ubuntu.sources").write_text(
            "URIs: http://archive.ubuntu.com/ubuntu\nURIs: http://ec2-1.archive.ubuntu.com/ubuntu\nURIs: http://security.ubuntu.com/ubuntu/\n"
            "URIs: http://ports.ubuntu.com/ubuntu-ports/\nURIs: mirror+file:/etc/apt/apt-mirrors.txt\n")
        docker = "deb [arch=amd64] https://download.docker.com/linux/ubuntu noble stable\n"
        (scoped / "docker.list").write_text(docker)
        # `sudo` de verdade só para o perl, sobre arquivos temporários; o apt-get continua simulado.
        body = ('sudo() { printf "%s\\n" "$*" >> "$root/calls"; [ "$1" = perl ] && { "$@"; return; }; '
                '[ "$1" = apt-get ] || return 0; n=$(grep -c "^apt-get " "$root/calls"); [ "$n" -gt 1 ]; }\n'
                f'apt_retry -o Dir::Etc::sourceparts={scoped} update -qq')
        result = self.run_helper(body, failures=0, extra_env={"CI_APT_MIRROR_FILES": str(mirrors)})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("https://mirrors.edge.kernel.org/ubuntu/\tpriority:1\n", mirrors.read_text())
        self.assertEqual(
            "URIs: https://mirrors.edge.kernel.org/ubuntu\nURIs: https://mirrors.edge.kernel.org/ubuntu\n"
            "URIs: https://mirrors.edge.kernel.org/ubuntu/\n"
            "URIs: https://mirrors.ocf.berkeley.edu/ubuntu-ports/\nURIs: mirror+file:/etc/apt/apt-mirrors.txt\n",
            (scoped / "ubuntu.sources").read_text())
        self.assertEqual(docker, (scoped / "docker.list").read_text())

    def test_a_retried_install_refreshes_the_lists_with_the_caller_options(self):
        result = self.run_helper('apt_retry -o Dir::Etc::sourceparts=/x -o Foo=bar install -y pacote', failures=1)
        self.assertEqual(0, result.returncode, result.stderr)
        install, refresh, retry = self.calls()
        self.assertTrue(install.endswith("install -y pacote"), install)
        self.assertTrue(refresh.endswith("-o Dir::Etc::sourceparts=/x -o Foo=bar update -qq"), refresh)
        self.assertEqual(install, retry)

    def test_a_retried_install_without_scoped_sources_still_recovers(self):
        # Caso do Playwright: install-deps chama o apt por conta própria, sem -o nem sourceparts.
        body = 'CI_RETRY_RECOVER=apt_recover_install retry_with_backoff sudo apt-get install -y pacote'
        result = self.run_helper(body, failures=1)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["dpkg --configure -a"], self.dpkg_recoveries())

    def test_without_dns_for_the_runner_mirror_the_first_attempt_already_uses_the_fallback(self):
        mirrors = self.root / "apt-mirrors.txt"
        mirrors.write_text("http://azure.archive.ubuntu.com/ubuntu/\n")
        body = ('_apt_host_resolves() { printf "probe %s\\n" "$1" >> "$root/calls"; return 2; }\n'
                'apt_retry update -qq')
        result = self.run_helper(body, failures=0, extra_env={"CI_APT_MIRROR_FILES": str(mirrors),
                                                              "CI_APT_PROBE_HOSTS": "azure.archive.ubuntu.com"})
        self.assertEqual(0, result.returncode, result.stderr)
        lines = (self.root / "calls").read_text().splitlines()
        self.assertEqual("probe azure.archive.ubuntu.com", lines[0])
        self.assertTrue(lines[1].startswith("perl "), lines)
        self.assertTrue(lines[2].startswith("apt-get "), lines)

    def test_with_dns_the_first_attempt_keeps_the_runner_mirror(self):
        mirrors = self.root / "apt-mirrors.txt"
        mirrors.write_text("http://azure.archive.ubuntu.com/ubuntu/\n")
        body = '_apt_host_resolves() { return 0; }\napt_retry update -qq'
        self.run_helper(body, failures=0, extra_env={"CI_APT_MIRROR_FILES": str(mirrors),
                                                     "CI_APT_PROBE_HOSTS": "azure.archive.ubuntu.com"})
        self.assertEqual([], self.mirror_switches())

    def test_default_probe_host_follows_the_architecture(self):
        body = ('dpkg() { echo arm64; }\n'
                '_apt_host_resolves() { printf "probe %s\\n" "$1" >> "$root/calls"; }\n'
                'apt_prefer_reachable_mirror')
        result = self.run_helper(body, failures=0)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("probe ports.ubuntu.com", (self.root / "calls").read_text())

    def test_fallback_mirror_ignores_missing_files(self):
        body = 'sudo() { [ "$1" = perl ] && { "$@"; return; }; return 0; }\napt_use_fallback_mirror'
        result = self.run_helper(body, failures=0, extra_env={"CI_APT_MIRROR_FILES": str(self.root / "nada.txt")})
        self.assertEqual(0, result.returncode, result.stderr)

    def test_attempt_timeout_sends_sigterm_first_and_sigkill_after_10_seconds(self):
        # `timeout` de mentira: registra os argumentos em vez de rodar o comando.
        body = 'timeout() { printf "%s\\n" "$*" >> "$root/timeout-args"; }\n' \
               'retry_with_backoff env true'
        result = self.run_helper(body, failures=0, extra_env={"CI_RETRY_ATTEMPT_TIMEOUT": "180"})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("-k 10 180 env true", (self.root / "timeout-args").read_text().strip())

    def test_install_recover_refresh_respects_the_attempt_timeout(self):
        # O update do recover também pode travar no espelho; sem o teto, ele seguraria o passo até o fim do job.
        body = 'timeout() { printf "%s\\n" "$*" >> "$root/timeout-args"; shift 3; "$@"; }\n' \
               'apt_retry install -y pacote'
        result = self.run_helper(body, failures=1, extra_env={"CI_RETRY_DELAYS": "0 0",
                                                              "CI_RETRY_ATTEMPT_TIMEOUT": "300"})
        self.assertEqual(0, result.returncode, result.stderr)
        refreshes = [line for line in (self.root / "timeout-args").read_text().splitlines()
                     if line.endswith(" update -qq")]
        self.assertEqual(1, len(refreshes), (self.root / "timeout-args").read_text())
        self.assertTrue(refreshes[0].startswith("-k 10 300 sudo apt-get "), refreshes[0])

    @unittest.skipUnless(shutil.which("timeout"), "GNU timeout ausente")
    def test_attempt_timeout_kills_a_hung_command_and_retries(self):
        # `env` força o binário externo: o `timeout` só consegue matar processo, não função de shell.
        body = 'unset -f sleep\nretry_with_backoff env sleep 5'
        result = self.run_helper(body, failures=0,
                                 extra_env={"CI_RETRY_DELAYS": "0 0", "CI_RETRY_ATTEMPT_TIMEOUT": "0.2"})
        self.assertEqual(124, result.returncode, result.stderr)
        self.assertIn("3 tentativas", result.stderr)


    def test_galaxy_clear_cache_removes_only_the_galaxy_cache(self):
        home = self.root / "home"
        (home / ".ansible/galaxy_cache").mkdir(parents=True)
        (home / ".ansible/galaxy_cache/api.json").write_text("{corrompido")
        (home / ".ansible/collections").mkdir()
        result = self.run_helper("galaxy_clear_cache", failures=0, extra_env={"HOME": str(home)})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse((home / ".ansible/galaxy_cache").exists())
        self.assertTrue((home / ".ansible/collections").is_dir())

    def test_galaxy_clear_cache_is_a_no_op_without_a_cache(self):
        home = self.root / "home"
        home.mkdir()
        result = self.run_helper("galaxy_clear_cache", failures=0, extra_env={"HOME": str(home)})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], sorted(home.iterdir()))

    def test_galaxy_recover_runs_between_attempts(self):
        home = self.root / "home"
        (home / ".ansible/galaxy_cache").mkdir(parents=True)
        body = textwrap.dedent('''
            tries=0
            flaky() { tries=$((tries + 1)); [ "$tries" -gt 1 ]; }
            CI_RETRY_RECOVER=galaxy_clear_cache retry_with_backoff flaky
        ''')
        result = self.run_helper(body, failures=0, extra_env={"HOME": str(home), "CI_RETRY_DELAYS": "0 0"})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse((home / ".ansible/galaxy_cache").exists())


if __name__ == "__main__":
    unittest.main()
