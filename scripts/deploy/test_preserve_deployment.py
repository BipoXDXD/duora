import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import duora_deploy as deploy
import runtime_guard as guard
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

    def _preflight(self, release, runner):
        profile = {"services": {"postgres": {"id": "1" * 64}}}
        with patch.object(deploy, "runtime_inventory", return_value={"profile": profile}), \
                patch.object(deploy, "_private_json", return_value=profile), \
                patch.object(guard, "require_preserved"), \
                patch.object(deploy, "_read_migration_head", return_value=self.release.latest_migration):
            return deploy.runtime_preflight(release, self.paths, runner)

    def test_cold_migrator_is_pulled_by_digest_before_external_sql_authentication(self) -> None:
        runner = InputRunner()
        release = parse(dict(valid_manifest(), contractVersion="v2"))
        def cold_image(reference, **_kwargs):
            self.assertIn(["docker", "pull", "--quiet", reference], runner.commands)
            return "SELECT 1;\n"
        with patch.object(deploy._payload, "extract_sql", side_effect=cold_image):
            self.assertEqual(self._preflight(release, runner), "SELECT 1;\n")
        self.assertFalse(any("stop" in command or "create" in command for command in runner.commands))

    def test_same_schema_new_legacy_release_after_v2_is_refused_before_any_sql_or_pull(self) -> None:
        self.paths.state_dir.mkdir()
        recorded = self.paths.state_dir / deploy.RELEASES_DIR
        recorded.mkdir()
        current = dict(valid_manifest(), contractVersion="v2", releaseId="current-v2")
        (self.paths.state_dir / "current.json").write_text(json.dumps({"releaseId": current["releaseId"]}))
        (recorded / "current-v2.json").write_text(json.dumps(current))
        runner = InputRunner()
        with patch.object(deploy._payload, "capture_binary", side_effect=AssertionError("No legacy SQL access")):
            with self.assertRaises(deploy.DeployError):
                self._preflight(self.release, runner)
        self.assertEqual(runner.commands, [])

    def test_recorded_compatible_legacy_rollback_after_v2_never_materializes_legacy_sql(self) -> None:
        self.paths.state_dir.mkdir()
        recorded = self.paths.state_dir / deploy.RELEASES_DIR
        recorded.mkdir()
        current = dict(valid_manifest(), contractVersion="v2", releaseId="current-v2", previousReleaseId=self.release.release_id)
        (self.paths.state_dir / "current.json").write_text(json.dumps({"releaseId": current["releaseId"]}))
        (recorded / "current-v2.json").write_text(json.dumps(current))
        (recorded / (self.release.release_id + ".json")).write_text(json.dumps(self.release.raw))
        runner = InputRunner()
        with patch.object(deploy._payload, "capture_binary", side_effect=AssertionError("Never materialize legacy SQL")):
            self.assertIsNone(self._preflight(self.release, runner))
        self.assertEqual(runner.commands, [])

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
