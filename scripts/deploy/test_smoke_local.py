"""Testes da limpeza do smoke local (sem Docker: o subprocess é simulado)."""

import contextlib
import io
import subprocess
import unittest
from pathlib import Path
from unittest.mock import patch

import smoke_local


class ReturnMediaDirTests(unittest.TestCase):
    media_dir = Path("/tmp/duora-smoke-x/media")

    def call(self, outcome: subprocess.CompletedProcess[str] | Exception) -> tuple[list, str]:
        stderr = io.StringIO()
        with patch.object(smoke_local.subprocess, "run") as run, contextlib.redirect_stderr(stderr):
            if isinstance(outcome, Exception):
                run.side_effect = outcome
            else:
                run.return_value = outcome
            smoke_local.return_media_dir_to_user(self.media_dir, "example/postgres@sha256:" + "a" * 64)
        return run.call_args_list, stderr.getvalue()

    def test_chown_runs_without_check_so_the_rest_of_the_cleanup_proceeds(self) -> None:
        calls, stderr = self.call(subprocess.CompletedProcess([], 0, "", ""))

        self.assertEqual(1, len(calls))
        self.assertIs(False, calls[0].kwargs["check"])
        self.assertIn("chown", calls[0].args[0])
        self.assertEqual("", stderr)

    def test_failed_chown_is_reported_and_does_not_raise(self) -> None:
        _, stderr = self.call(subprocess.CompletedProcess([], 125, "", "daemon fora"))

        self.assertIn(str(self.media_dir), stderr)
        self.assertIn("125", stderr)
        self.assertIn("daemon fora", stderr)

    def test_docker_that_cannot_run_is_reported_and_does_not_raise(self) -> None:
        _, stderr = self.call(FileNotFoundError("docker"))

        self.assertIn(str(self.media_dir), stderr)
        self.assertIn("docker", stderr)


if __name__ == "__main__":
    unittest.main()
