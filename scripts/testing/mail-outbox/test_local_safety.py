"""Offline regression: refusing local execution must precede external processes."""
import os
from pathlib import Path
import runpy
import shutil
import tempfile
import unittest
from unittest import mock


class RunnerSafetyTests(unittest.TestCase):
    def test_refuses_local_and_self_hosted_execution_before_external_processes(self):
        for environment, platform in (({}, "linux"),
                                      ({"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "self-hosted"}, "linux"),
                                      ({"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}, "darwin")):
            with self.subTest(environment=environment, platform=platform), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                script = root / "validation" / "cross_version.py"
                script.parent.mkdir()
                shutil.copyfile(Path(__file__).with_name("cross_version.py"), script)
                with mock.patch.dict(os.environ, environment, clear=True), mock.patch("sys.platform", platform), \
                     mock.patch("subprocess.run", side_effect=AssertionError("external process forbidden")) as run:
                    with self.assertRaisesRegex(SystemExit, "ephemeral GitHub-hosted Linux runner"):
                        runpy.run_path(str(script), run_name="__main__")
                    run.assert_not_called()
                self.assertFalse((root / "artifacts").exists())


if __name__ == "__main__":
    unittest.main()
