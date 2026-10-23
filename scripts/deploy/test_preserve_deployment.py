import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import duora_deploy as deploy
from test_duora_deploy import FakeRunner, parse, valid_manifest


class InputRunner(FakeRunner):
    def __init__(self, **kwargs) -> None:
        super().__init__(**kwargs)
        self.inputs: list[str | None] = []

    def __call__(self, command: list[str], *, capture: bool = False, input_text: str | None = None):
        self.inputs.append(input_text)
        return super().__call__(command, capture=capture)


class PreservationDeploymentTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.paths = deploy.Paths(self.root / "compose.yml", self.root / "host.env", self.root / "state")
        self.release = parse(valid_manifest())

    def test_runtime_or_payload_refusal_precedes_pull_worker_migration_and_staging(self) -> None:
        runner = InputRunner()
        with patch.object(deploy, "runtime_preflight", side_effect=deploy.DeployError("unqualified runtime/payload")):
            with self.assertRaisesRegex(deploy.DeployError, "unqualified"):
                deploy.deploy(self.release, self.paths, runner)
        self.assertEqual(runner.commands, [])
        self.assertFalse((self.paths.state_dir / deploy.STAGING_DIR).exists())

    def test_preservation_never_up_starts_or_recreates_database_backup_or_dependencies(self) -> None:
        runner = InputRunner()
        sql = "SELECT 'verified canonical payload';\n"
        with patch.object(deploy, "runtime_preflight", return_value=sql):
            deploy.deploy(self.release, self.paths, runner, sleep=lambda _: None)
        for command in runner.commands:
            self.assertFalse(any(op in command for op in ["up", "start", "restart", "create"]) and
                             any(role in command for role in ["postgres", "backup"]))
            if "migrator" in command and "run" in command:
                self.assertIn("--no-deps", command)
        applied = [(c, text) for c, text in zip(runner.commands, runner.inputs) if text is not None]
        self.assertEqual(len(applied), 1)
        self.assertEqual(applied[0][1], sql)
        self.assertIn("--single-transaction", applied[0][0])
        self.assertIn("/dev/stdin", applied[0][0])

    def test_same_schema_known_previous_application_rollback_never_reapplies_older_sql(self) -> None:
        self.paths.state_dir.mkdir()
        recorded = self.paths.state_dir / deploy.RELEASES_DIR
        recorded.mkdir()
        target = valid_manifest()
        current = dict(target, releaseId="2026.10.16-1", previousReleaseId=target["releaseId"])
        (self.paths.state_dir / "current.json").write_text(json.dumps({"releaseId": current["releaseId"]}))
        (recorded / (target["releaseId"] + ".json")).write_text(json.dumps(target))
        (recorded / (current["releaseId"] + ".json")).write_text(json.dumps(current))
        runner = InputRunner(pre_head=self.release.latest_migration)
        with patch.object(deploy, "runtime_preflight", return_value="older SQL that must never be applied"):
            deploy.deploy(self.release, self.paths, runner, sleep=lambda _: None)
        self.assertFalse(any("migrator" in c and "run" in c for c in runner.commands))
        self.assertFalse(any(text is not None for text in runner.inputs))
