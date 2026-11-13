"""Resiliência a falhas de infraestrutura nos passos de CI (#549, #500); estrutura lida dos YAML, sem rodar jobs."""
from __future__ import annotations

from pathlib import Path
import re
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
ACTION = ROOT / ".github/actions/postgres-component/action.yml"
MEDIA_RELEASE = ROOT / ".github/workflows/media-release.yml"
EDGE_DOCKERFILE = ROOT / "infra/images/edge/Dockerfile"
DOWNLOAD_ARTIFACT = "actions/download-artifact@"
JOB_TIMEOUT_SECONDS = 30 * 60  # timeout-minutes do job images em ci.yml


class PostgresComponentResilienceTests(unittest.TestCase):
    def setUp(self):
        self.steps = yaml.safe_load(ACTION.read_text())["runs"]["steps"]

    def test_daemon_restart_goes_through_the_tested_helper(self):
        step = next(item for item in self.steps if str(item.get("name", "")).startswith("Match the disposable"))
        script = step["run"]
        self.assertIn('source "$GITHUB_WORKSPACE/scripts/ci/docker-restart.sh"', script)
        self.assertIn("docker_restart_after_upgrade", script)
        # Um restart solto fora do helper voltaria a estourar o start-limit-hit.
        self.assertNotIn("systemctl restart", script)

    def test_artifact_download_has_exactly_one_identical_second_attempt(self):
        downloads = [item for item in self.steps if str(item.get("uses", "")).startswith(DOWNLOAD_ARTIFACT)]
        self.assertEqual(2, len(downloads))
        first, second = downloads
        self.assertTrue(first.get("continue-on-error"))
        self.assertEqual("${{ steps.%s.outcome == 'failure' }}" % first["id"], second.get("if"))
        self.assertNotIn("continue-on-error", second)
        self.assertEqual(first["uses"], second["uses"])
        self.assertEqual(first["with"], second["with"])
        self.assertEqual("error", second["with"]["digest-mismatch"])

    def test_the_wait_between_downloads_runs_only_after_a_failure(self):
        first = next(item for item in self.steps if str(item.get("uses", "")).startswith(DOWNLOAD_ARTIFACT))
        index = self.steps.index(first)
        wait = self.steps[index + 1]
        self.assertEqual("${{ steps.%s.outcome == 'failure' }}" % first["id"], wait.get("if"))
        self.assertRegex(wait["run"], r"(?m)^sleep \d+$")


class MediaReleaseAptTests(unittest.TestCase):
    def test_native_prerequisites_use_the_apt_retry_helper(self):
        jobs = yaml.safe_load(MEDIA_RELEASE.read_text())["jobs"]
        step = next(item for item in jobs["native"]["steps"]
                    if item.get("name") == "Native architecture and dedicated rootless prerequisites")
        script = step["run"]
        self.assertIn('source "$GITHUB_WORKSPACE/scripts/ci/apt-retry.sh"', script)
        self.assertIn('apt_retry "${apt_options[@]}" update -qq', script)
        self.assertRegex(script, r'apt_retry "\$\{apt_options\[@\]\}" install -y ')
        self.assertNotRegex(script, r"(?m)^\s*sudo apt-get ")


class EdgeImageResilienceTests(unittest.TestCase):
    def setUp(self):
        self.dockerfile = EDGE_DOCKERFILE.read_text()

    def instruction(self, command: str) -> str:
        """A instrução RUN que contém `command`, com as linhas de continuação e sem o que vem depois."""
        runs = re.findall(r"(?m)^RUN (?:.*\\\n)*.*$", self.dockerfile)
        return next(run for run in runs if command in run)

    def test_go_downloads_reuse_a_module_cache_mount(self):
        for command in ("go install github.com/caddyserver/xcaddy", "xcaddy build"):
            with self.subTest(command=command):
                instruction = self.instruction(command)
                self.assertIn("--mount=type=cache,id=duora-go-mod,target=/go/pkg/mod", instruction)

    def test_xcaddy_build_has_bounded_attempts_with_a_per_attempt_deadline(self):
        instruction = self.instruction("xcaddy build")
        self.assertRegex(instruction, r"for attempt in 1 2 3; do")
        deadline = re.search(r"timeout (\d+) xcaddy build", instruction)
        if deadline is None:
            self.fail("o xcaddy build precisa de um prazo por tentativa (timeout N)")
        # 3 tentativas precisam caber, com folga para o resto do build, no timeout-minutes: 30 do job images.
        self.assertLessEqual(3 * int(deadline.group(1)), JOB_TIMEOUT_SECONDS // 2)
        self.assertRegex(instruction, r"exit 1$")

    def test_xcaddy_build_does_not_wait_after_the_last_attempt(self):
        instruction = self.instruction("xcaddy build")
        self.assertIn('if [ "$attempt" -lt 3 ]; then sleep', instruction)
        self.assertNotRegex(instruction, r"(?m)^\s*sleep ")


if __name__ == "__main__":
    unittest.main()
