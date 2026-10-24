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
        private_json = deploy._private_json
        with patch.object(deploy, "runtime_inventory", return_value={"profile": profile}), \
                patch.object(deploy, "_private_json", side_effect=lambda path: profile if path.name == guard.BASELINE_NAME else private_json(path)), \
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

    def test_v1_a_v2_b_rollback_a_then_new_v1_c_cannot_reset_contract_floor(self) -> None:
        self.paths.state_dir.mkdir()
        recorded = self.paths.state_dir / deploy.RELEASES_DIR
        recorded.mkdir()
        first = dict(valid_manifest(), releaseId="legacy-a")
        second = dict(first, releaseId="external-b", contractVersion="v2", previousReleaseId="legacy-a")
        for value in [first, second]:
            (recorded / (value["releaseId"] + ".json")).write_text(json.dumps(value))
        # Application rollback A succeeded; the qualified B history must still constrain later new releases.
        (self.paths.state_dir / "current.json").write_text(json.dumps({"releaseId": "legacy-a"}))
        target = parse(dict(first, releaseId="new-legacy-c", previousReleaseId="legacy-a"))
        runner = InputRunner()
        with patch.object(deploy._payload, "capture_binary", side_effect=AssertionError("Never reach legacy SQL after v2")):
            with self.assertRaises(deploy.DeployError): self._preflight(target, runner)
        self.assertEqual(runner.commands, [])

    def test_failed_first_v2_migrator_keeps_durable_floor_before_worker_stop_and_before_recording_release(self) -> None:
        release = parse(dict(valid_manifest(), releaseId="first-v2", contractVersion="v2"))
        runner = InputRunner(fail_on="run --rm --no-deps migrator")
        with patch.object(deploy, "runtime_recheck"), patch.object(deploy, "runtime_preflight", return_value="SELECT 1;\n"):
            with self.assertRaises(deploy.DeployError):
                deploy.deploy(release, self.paths, runner, sleep=lambda _: None)
        floor = self.paths.state_dir / "v2-contract-floor.json"
        self.assertTrue(floor.is_file(), "A partial v2 attempt must not permit weaker later legacy SQL")
        self.assertEqual(json.loads(floor.read_text())["minimumContract"], "v2")
        self.assertFalse((self.paths.state_dir / deploy.RELEASES_DIR / "first-v2.json").exists())
        with patch.object(deploy._payload, "capture_binary", side_effect=AssertionError("No legacy access after failed v2")):
            with self.assertRaises(deploy.DeployError): self._preflight(self.release, InputRunner())

    def test_failed_first_v2_can_restore_qualified_current_v1_app_only_without_lowering_floor(self) -> None:
        self.paths.state_dir.mkdir()
        recorded = self.paths.state_dir / deploy.RELEASES_DIR
        recorded.mkdir()
        (self.paths.state_dir / "current.json").write_text(json.dumps({"releaseId": self.release.release_id}))
        (recorded / (self.release.release_id + ".json")).write_text(json.dumps(self.release.raw))
        floor = self.paths.state_dir / "v2-contract-floor.json"
        floor.write_text(json.dumps({"schema": 1, "minimumContract": "v2", "releaseId": "first-v2-failed",
            "commit": "b" * 40, "sqlSha256": "c" * 64}))
        floor.chmod(0o600)
        original = floor.read_bytes()
        with patch.object(deploy._payload, "capture_binary", side_effect=AssertionError("Never weaker SQL")):
            self.assertIsNone(self._preflight(self.release, InputRunner()))
        runner = InputRunner(pre_head=self.release.latest_migration)
        with patch.object(deploy, "runtime_recheck"), patch.object(deploy, "runtime_preflight", return_value=None):
            deploy.deploy(self.release, self.paths, runner, sleep=lambda _: None)
        self.assertFalse(any("migrator" in command and "run" in command for command in runner.commands))
        self.assertFalse(any(text is not None for text in runner.inputs))
        self.assertEqual(floor.read_bytes(), original)
        with self.assertRaises(deploy.DeployError):
            deploy._is_rollback(self.release, "20990101000000_UnknownPartialSchema", self.paths.state_dir)

    def test_runtime_or_payload_refusal_precedes_pull_worker_migration_and_staging(self) -> None:
        runner = InputRunner()
        with patch.object(deploy, "runtime_recheck"), patch.object(deploy, "runtime_preflight", side_effect=deploy.DeployError("unqualified runtime/payload")):
            with self.assertRaisesRegex(deploy.DeployError, "unqualified"):
                deploy.deploy(self.release, self.paths, runner)
        self.assertEqual(runner.commands, [])
        self.assertFalse((self.paths.state_dir / deploy.STAGING_DIR).exists())

    def test_preservation_never_up_starts_or_recreates_database_backup_or_dependencies(self) -> None:
        runner = InputRunner()
        sql = "SELECT 'verified canonical payload';\n"
        with patch.object(deploy, "runtime_recheck"), patch.object(deploy, "runtime_preflight", return_value=sql):
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
        with patch.object(deploy, "runtime_recheck"), patch.object(deploy, "runtime_preflight", return_value="older SQL that must never be applied"):
            deploy.deploy(self.release, self.paths, runner, sleep=lambda _: None)
        self.assertFalse(any("migrator" in c and "run" in c for c in runner.commands))
        self.assertFalse(any(text is not None for text in runner.inputs))

class RuntimeDriftBoundaryTests(unittest.TestCase):
    """Regression for the auditor's backup-ID change during a slow application pull."""

    def test_drift_at_pull_worker_stop_or_final_readiness_never_promotes_the_release(self) -> None:
        import copy
        from test_runtime_guard import reviewed_profile

        for boundary in ["pull", "stop", "readiness"]:
            with self.subTest(boundary=boundary), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                state = root / "state"
                (state / "releases").mkdir(parents=True)
                original = dict(valid_manifest(), releaseId="qualified-current", contractVersion="v1")
                (state / "current.json").write_text(json.dumps({"releaseId": "qualified-current"}))
                (state / "releases/qualified-current.json").write_text(json.dumps(original))
                profile = reviewed_profile()
                baseline = state / guard.BASELINE_NAME
                baseline.write_text(json.dumps(profile))
                baseline.chmod(0o600)
                observed = copy.deepcopy(profile)
                release = parse(dict(original, releaseId="candidate-v2", contractVersion="v2",
                                     previousReleaseId="qualified-current"))
                paths = deploy.Paths(root / "compose.yml", root / "host.env", state)

                class DriftRunner(InputRunner):
                    def __call__(self, command, **kwargs):
                        if ((boundary == "pull" and "pull" in command and "web" in command)
                                or (boundary == "stop" and "stop" in command)
                                or (boundary == "readiness" and "/health/ready" in " ".join(command))):
                            observed["services"]["backup"]["id"] = "9" * 64
                        return super().__call__(command, **kwargs)

                runner = DriftRunner(pre_head=release.latest_migration)
                with patch.object(deploy, "runtime_inventory", side_effect=lambda *_: {"profile": copy.deepcopy(observed)}), \
                        patch.object(deploy, "_read_migration_head", return_value=release.latest_migration), \
                        patch.object(deploy._payload, "extract_sql", return_value="SELECT 1;\n"):
                    with self.assertRaises(deploy.DeployError):
                        deploy.deploy(release, paths, runner, sleep=lambda _: None)
                self.assertEqual(deploy._current_release_id(state), "qualified-current")
                self.assertFalse((state / "releases/candidate-v2.json").exists())
                self.assertEqual(json.loads(baseline.read_text()), profile, "Never auto-enroll drift")
                if boundary == "pull":
                    self.assertFalse(runner.ran("stop worker"))
                    self.assertFalse((state / "v2-contract-floor.json").exists())
                if boundary in {"pull", "stop"}:
                    self.assertFalse(runner.ran("run --rm --no-deps migrator"))

    def test_application_only_rollback_rechecks_runtime_before_changing_the_application(self) -> None:
        import copy
        from test_runtime_guard import reviewed_profile

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            state = root / "state"
            (state / "releases").mkdir(parents=True)
            target = dict(valid_manifest(), releaseId="previous-v2", contractVersion="v2")
            current = dict(target, releaseId="current-v2", previousReleaseId="previous-v2")
            (state / "current.json").write_text(json.dumps({"releaseId": "current-v2"}))
            for raw in [target, current]:
                (state / "releases" / (raw["releaseId"] + ".json")).write_text(json.dumps(raw))
            profile = reviewed_profile()
            baseline = state / guard.BASELINE_NAME
            baseline.write_text(json.dumps(profile))
            baseline.chmod(0o600)
            observed = copy.deepcopy(profile)
            release = parse(target)

            class RollbackRunner(InputRunner):
                def __call__(self, command, **kwargs):
                    if "pull" in command:
                        observed["services"]["backup"]["id"] = "9" * 64
                    return super().__call__(command, **kwargs)

            runner = RollbackRunner(pre_head=release.latest_migration)
            with patch.object(deploy, "runtime_inventory", side_effect=lambda *_: {"profile": copy.deepcopy(observed)}), \
                    patch.object(deploy, "_read_migration_head", return_value=release.latest_migration):
                with self.assertRaises(deploy.DeployError):
                    deploy.deploy(release, deploy.Paths(root / "compose.yml", root / "host.env", state),
                                  runner, sleep=lambda _: None)
            self.assertFalse(runner.ran("up --detach"))
            self.assertEqual(deploy._current_release_id(state), "current-v2")
