"""Host requalification (--rehost, --reconverge-runtime) with real temporary files and locks; Docker is a fake."""
import copy
import hashlib
import json
import subprocess
import unittest
from pathlib import Path
from unittest.mock import patch

import duora_deploy as deploy
import install_executor as installer
import runtime_guard as guard
from test_duora_deploy import PREFIX, valid_manifest
from test_install_executor import InstallationFixture
from test_runtime_guard import reviewed_profile

NEW_HOST = "b" * 64
TOOL_COMMIT = "c" * 40
APPLIED_AT = 1_763_100_000


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


class RehostTests(InstallationFixture):
    def setUp(self) -> None:
        super().setUp()
        installed = self.invoke(apply=True)
        self.executor_sha = installed["executorSha256"]
        self.baseline_path = self.paths.state / guard.BASELINE_NAME
        self.receipt_path = self.paths.state / installer.RECEIPT_NAME
        self.old_baseline = self.baseline_path.read_bytes()
        self.old_receipt = self.receipt_path.read_bytes()
        profile = reviewed_profile()
        profile["context"].update(hostIdSha256=NEW_HOST, dockerVersion="29.9.3", composeVersion="5.7.1")
        self.new_inventory = self._inventory(profile)
        self.fence = self.paths.ingress / installer.REHOST_FENCE_DIRECTORY / "pending.json"

    @staticmethod
    def _inventory(profile: dict) -> dict:
        inventory = {"profile": profile, "audit": {"reviewed": "synthetic new host"}}
        inventory["inventorySha256"] = guard.digest(inventory)
        return inventory

    def rehost(self, *, apply: bool = False, **overrides) -> dict:
        arguments = {"paths": self.paths, "expected_executor_sha256": self.executor_sha,
                     "previous_baseline_sha256": _sha(self.old_baseline),
                     "previous_receipt_sha256": _sha(self.old_receipt),
                     "reviewed_inventory_sha256": self.new_inventory["inventorySha256"],
                     "inspect_runtime": lambda: self.new_inventory, "require_idle": lambda: None,
                     "tool_commit": TOOL_COMMIT, "now": lambda: APPLIED_AT}
        arguments.update(overrides)
        return installer.rehost_runtime(apply=apply, **arguments)

    def assertSealUnchanged(self) -> None:
        self.assertEqual(self.baseline_path.read_bytes(), self.old_baseline)
        self.assertEqual(self.receipt_path.read_bytes(), self.old_receipt)
        self.assertFalse(self.fence.exists())
        self.assertEqual(_sha(self.entry.read_bytes()), self.executor_sha)

    def test_preflight_prints_the_plan_and_inventory_without_writing(self) -> None:
        result = self.rehost()
        self.assertEqual(result["status"], "rehost-preflight")
        self.assertEqual(result["inventorySha256"], self.new_inventory["inventorySha256"])
        self.assertEqual(result["previousBaselineSha256"], _sha(self.old_baseline))
        self.assertEqual(result["previousReceiptSha256"], _sha(self.old_receipt))
        self.assertEqual(result["previousHostIdSha256"], "e" * 64)
        self.assertEqual(result["hostIdSha256"], NEW_HOST)
        self.assertEqual(result["inventory"], self.new_inventory)
        self.assertSealUnchanged()
        self.assertFalse((self.paths.state / installer.REHOST_HISTORY_DIRECTORY).exists())

    def test_apply_seals_the_new_host_and_archives_the_previous_seal_by_rename(self) -> None:
        result = self.rehost(apply=True)
        self.assertEqual(result["status"], "rehosted")
        self.assertEqual(json.loads(self.baseline_path.read_text()), self.new_inventory["profile"])
        receipt = json.loads(self.receipt_path.read_text())
        old = json.loads(self.old_receipt)
        self.assertEqual(receipt["status"], "installed")
        for key in ("sourceCommit", "manifestSha256", "executorSha256", "files", "qualificationRun", "preimage"):
            self.assertEqual(receipt[key], old[key])
        self.assertEqual(receipt["baselineSha256"], _sha(self.baseline_path.read_bytes()))
        self.assertEqual(receipt["inventorySha256"], self.new_inventory["inventorySha256"])
        self.assertEqual(receipt["appliedAt"], APPLIED_AT)
        archive = Path(receipt["rehost"]["archive"])
        self.assertEqual(receipt["rehost"], {
            "archive": str(archive), "toolCommit": TOOL_COMMIT, "previousBaselineSha256": _sha(self.old_baseline),
            "previousReceiptSha256": _sha(self.old_receipt), "previousHostIdSha256": "e" * 64,
            "hostIdSha256": NEW_HOST})
        self.assertEqual(archive.parent, self.paths.state / installer.REHOST_HISTORY_DIRECTORY)
        self.assertEqual((archive / guard.BASELINE_NAME).read_bytes(), self.old_baseline)
        self.assertEqual((archive / installer.RECEIPT_NAME).read_bytes(), self.old_receipt)
        self.assertFalse(self.fence.exists())
        self.assertEqual(_sha(self.entry.read_bytes()), self.executor_sha)

    def test_divergent_previous_baseline_is_refused(self) -> None:
        self.baseline_path.write_bytes(self.old_baseline.replace(b"\n", b" \n"))
        self.old_baseline = self.baseline_path.read_bytes()
        with self.assertRaises(installer.InstallError): self.rehost()
        with self.assertRaises(installer.InstallError): self.rehost(apply=True)
        self.assertSealUnchanged()

    def test_previous_seal_differing_from_reviewed_evidence_is_refused(self) -> None:
        for key in ("previous_baseline_sha256", "previous_receipt_sha256"):
            with self.subTest(key=key):
                with self.assertRaisesRegex(installer.InstallError, "rehost refused"):
                    self.rehost(apply=True, **{key: "0" * 64})
                self.assertSealUnchanged()

    def test_receipt_that_is_not_installed_or_whose_version_changed_is_refused(self) -> None:
        receipt = json.loads(self.old_receipt)
        receipt["status"] = "preflight"
        self.receipt_path.write_bytes(installer._json(receipt))
        self.old_receipt = self.receipt_path.read_bytes()
        with self.assertRaises(installer.InstallError): self.rehost(apply=True)
        self.assertSealUnchanged()

    def test_same_host_is_refused_so_rehost_cannot_reseal_in_place(self) -> None:
        same = self._inventory(json.loads(self.old_baseline))
        with self.assertRaises(installer.InstallError) as refused:
            self.rehost(apply=True, inspect_runtime=lambda: same, reviewed_inventory_sha256=same["inventorySha256"])
        self.assertIn("Same host", str(refused.exception.__cause__))
        self.assertSealUnchanged()

    def test_open_swap_or_installation_is_refused(self) -> None:
        for path in (self.paths.state / "postgres-swap/attempt.json",
                     self.paths.ingress / installer.SWAP_FENCE_DIRECTORY / "pending.json",
                     self.paths.ingress / installer.FENCE_DIRECTORY / "pending.json",
                     self.paths.state / "executor-install-pending.json"):
            with self.subTest(path=path.name):
                path.parent.mkdir(mode=0o700, exist_ok=True)
                path.write_text("{}")
                try:
                    with self.assertRaises(installer.InstallError): self.rehost()
                    with self.assertRaises(installer.InstallError): self.rehost(apply=True)
                finally:
                    path.unlink()
                self.assertSealUnchanged()

    def test_different_reviewed_inventory_is_refused(self) -> None:
        with self.assertRaises(installer.InstallError):
            self.rehost(apply=True, reviewed_inventory_sha256="0" * 64)
        self.assertSealUnchanged()

    def test_apply_requires_every_reviewed_value(self) -> None:
        for key in ("previous_baseline_sha256", "previous_receipt_sha256", "reviewed_inventory_sha256"):
            with self.subTest(key=key):
                with self.assertRaises(installer.InstallError): self.rehost(apply=True, **{key: None})
                self.assertSealUnchanged()

    def test_executor_other_than_the_installed_one_is_refused(self) -> None:
        with self.assertRaises(installer.InstallError):
            self.rehost(apply=True, expected_executor_sha256="0" * 64)
        self.assertSealUnchanged()

    def test_runtime_rejected_by_collect_runtime_is_never_sealed(self) -> None:
        def raw_image_id() -> dict:
            raise deploy.DeployError("Config.Image is a raw image id, not the release digest reference")
        with self.assertRaises(installer.InstallError): self.rehost(inspect_runtime=raw_image_id)
        with self.assertRaises(installer.InstallError): self.rehost(apply=True, inspect_runtime=raw_image_id)
        self.assertSealUnchanged()

    def test_runtime_changing_while_staging_never_fences_or_seals(self) -> None:
        snapshots = iter([self.new_inventory, self._inventory(dict(self.new_inventory["profile"], schema=2))])
        with self.assertRaises(installer.InstallError):
            self.rehost(apply=True, inspect_runtime=lambda: next(snapshots))
        self.assertSealUnchanged()

    def test_busy_host_is_refused(self) -> None:
        def busy() -> None: raise installer.InstallError("synthetic active queue")
        with self.assertRaises(installer.InstallError): self.rehost(apply=True, require_idle=busy)
        self.assertSealUnchanged()

    def test_interrupted_rehost_fences_releases_and_resumes_only_with_the_same_arguments(self) -> None:
        real_write = installer._write

        def crash_on_live_receipt(path, raw, **kwargs):
            if path == self.receipt_path: raise OSError("synthetic crash")
            return real_write(path, raw, **kwargs)
        with patch.object(installer, "_write", crash_on_live_receipt):
            with self.assertRaises(installer.InstallError): self.rehost(apply=True)
        self.assertTrue(self.fence.exists())
        self.assertFalse(self.receipt_path.exists())
        receiver = self._swap_receiver()
        with self.assertRaises(ValueError): receiver.require_no_pending(self.fence.parent)
        with self.assertRaises(installer.InstallError): self.invoke(apply=True)
        self.assertEqual(self.rehost()["status"], "rehost-interrupted")
        with self.assertRaises(installer.InstallError):
            self.rehost(apply=True, reviewed_inventory_sha256="0" * 64)
        result = self.rehost(apply=True)
        self.assertEqual(result["status"], "rehosted")
        archive = self.paths.state / installer.REHOST_HISTORY_DIRECTORY / result["archive"]
        self.assertEqual((archive / guard.BASELINE_NAME).read_bytes(), self.old_baseline)
        self.assertEqual((archive / installer.RECEIPT_NAME).read_bytes(), self.old_receipt)
        self.assertEqual(json.loads(self.baseline_path.read_text()), self.new_inventory["profile"])
        self.assertFalse(self.fence.exists())

    def test_installer_refuses_another_host_until_rehost_and_reseals_on_the_same_host_after(self) -> None:
        self.inventory, self.old_hash = self.new_inventory, self.executor_sha
        with self.assertRaises(installer.InstallError) as refused:
            self.invoke(apply=True)
        self.assertIn("--rehost", str(refused.exception.__cause__))
        self.assertSealUnchanged()
        self.rehost(apply=True)
        self.assertEqual(self.invoke(apply=True)["status"], "installed")


class ReconvergeTests(InstallationFixture):
    def setUp(self) -> None:
        super().setUp()
        manifest = valid_manifest()
        manifest.update(releaseId="2026.11.13-1", architecture="amd64")
        incoming = copy.deepcopy(manifest)
        incoming.update(releaseId="2026.11.14-1", previousReleaseId="2026.11.13-1")
        incoming["images"]["edge"] = PREFIX + "edge@sha256:" + "b" * 64
        self.release = deploy.parse_manifest(json.dumps(manifest), image_prefix=PREFIX)
        self.incoming = deploy.parse_manifest(json.dumps(incoming), image_prefix=PREFIX)
        self.reference = self.release.images["postgres"]
        self.image_id = "sha256:" + "9" * 64
        releases = Path("/opt/duora/releases")
        self.compose_file = Path("/opt/duora/compose.coolify.json")
        self.runtime_env = Path("/var/lib/duora/releases/2026.11.13-1.env")
        self.pin_path = Path("/etc/duora/coolify-deploy.json")
        current_raw = json.dumps(manifest, indent=2).encode()
        incoming_raw = json.dumps(incoming, indent=2).encode()
        self.restored_pin = deploy._recovery_pin_bytes({
            "application_uuid": "u" * 24, "compose": str(self.compose_file), "caddy": "/opt/duora/Caddyfile",
            "manifest": str(releases / "2026.11.13-1.json"), "manifest_sha256": _sha(current_raw),
            "observer_environment": {"DUORA_OBSERVER_IMAGE": self.release.images["edge"]}})
        failed_pin = json.loads(self.restored_pin)
        failed_pin.update(manifest=str(releases / "2026.11.14-1.json"), manifest_sha256=_sha(incoming_raw))
        failed_pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = self.incoming.images["edge"]
        self.files = {releases / "2026.11.13-1.json": current_raw, releases / "2026.11.14-1.json": incoming_raw,
                      self.compose_file: b'{"name": "duora"}\n', self.pin_path: deploy._recovery_pin_bytes(failed_pin),
                      self.runtime_env: deploy._release_env_text(self.release).encode()}
        self.failed_pin = self.files[self.pin_path]
        self.containers = {"postgres": {"id": "1" * 64, "config": self.image_id, "image": self.image_id},
                           "backup": {"id": "2" * 64, "config": self.image_id, "image": self.image_id}}
        self.recreated = {"postgres": "3" * 64, "backup": "4" * 64}
        self.commands: list[list[str]] = []
        self.verified = []
        self.compose = ["docker", "compose", "--file", str(self.compose_file)]
        self.host = installer.ReconvergeHost(core=deploy, compose=self.compose, compose_file=self.compose_file,
            runtime_env=self.runtime_env, capture=self._capture, run=self._run, read_protected=self.files.__getitem__,
            verify_runtime=lambda: self.verified.append(True) or {}, pin_path=self.pin_path, sleep=lambda _: None)
        for name, value in (("_recovery_apply_pin", self._apply_pin), ("_wait_ready", lambda *a: None),
                            ("_require_services_running", lambda *a: None)):
            patcher = patch.object(deploy, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def _container(self, role: str) -> dict:
        item = self.containers[role]
        return {"Id": item["id"], "Image": item["image"], "State": {"Status": "running"},
                "Config": {"Image": item["config"], "Labels": {"com.docker.compose.project": "duora",
                                                                "com.docker.compose.service": role}},
                "Mounts": [{"Type": "volume", "Name": "duora_pgdata", "Destination": "/var/lib/postgresql",
                            "RW": role == "postgres"}]}

    def _capture(self, command: list[str]) -> bytes:
        if command[:3] == ["docker", "image", "inspect"] and command[-1] == self.reference:
            return json.dumps({"Id": self.image_id}).encode()
        if command == self.compose + ["config", "--format", "json"]:
            return json.dumps({"services": {role: {"image": self.reference} for role in ("postgres", "backup")}}).encode()
        if command[:-1] == self.compose + ["ps", "--all", "--quiet"]:
            return (self.containers[command[-1]]["id"] + "\n").encode()
        if command[:3] == ["docker", "container", "inspect"]:
            role = next(role for role, item in self.containers.items() if item["id"] == command[-1])
            return json.dumps(self._container(role)).encode()
        if command[:3] == ["docker", "volume", "inspect"]:
            return json.dumps({"Name": "duora_pgdata", "CreatedAt": "2026-11-14T06:05:00Z"}).encode()
        raise AssertionError("unexpected capture " + " ".join(command))

    def _run(self, command: list[str]):
        self.commands.append(command[len(self.compose):])
        if "--force-recreate" in command:
            role = command[-1]
            self.containers[role].update(id=self.recreated[role], config=self.reference)
        return subprocess.CompletedProcess(command, 0, "", "")

    def _apply_pin(self, path, *, expected_current_sha256, restored_raw, nonce):
        self.assertRegex(nonce, r"^[a-f0-9]{32}$")
        self.assertEqual(_sha(self.files[path]), expected_current_sha256)
        self.files[path] = restored_raw
        return {"status": "restored", "sha256": _sha(restored_raw)}

    def reconverge(self, *, apply: bool = False, reviewed: str | None = None, idle=None) -> dict:
        return installer.reconverge_runtime(self.release, self.host, paths=self.paths, apply=apply,
            reviewed_plan_sha256=reviewed, require_idle=idle or (lambda: None))

    def assertUntouched(self) -> None:
        self.assertEqual(self.commands, [])
        self.assertEqual(self.files[self.pin_path], self.failed_pin)
        self.assertEqual(self.verified, [])

    def test_raw_id_of_the_same_image_is_re_referenced_by_digest_and_the_pin_restored(self) -> None:
        plan = self.reconverge()
        self.assertEqual(plan["status"], "reconverge-preflight")
        self.assertEqual(plan["recreate"], ["postgres", "backup"])
        self.assertEqual(plan["pin"]["stage"], "restore")
        self.assertEqual(plan["pin"]["incomingReleaseId"], "2026.11.14-1")
        self.assertEqual(plan["pin"]["afterSha256"], _sha(self.restored_pin))
        self.assertUntouched()
        result = self.reconverge(apply=True, reviewed=plan["planSha256"])
        self.assertEqual(result["status"], "reconverged")
        self.assertEqual(self.files[self.pin_path], self.restored_pin)
        self.assertEqual(self.commands, [
            ["stop", "worker"], ["stop", "web"], ["stop", "postgres"], ["stop", "backup"],
            ["up", "--detach", "--wait", "--no-deps", "--force-recreate", "backup"],
            ["up", "--detach", "--wait", "--no-deps", "--force-recreate", "postgres"],
            ["up", "--detach", "--wait", "--no-deps", "web", "worker"]])
        self.assertEqual({role: item["configImage"] for role, item in result["containers"].items()},
                         {"postgres": self.reference, "backup": self.reference})
        self.assertEqual(self.verified, [True])

    def test_converged_runtime_and_current_pin_change_nothing(self) -> None:
        for item in self.containers.values(): item["config"] = self.reference
        self.files[self.pin_path] = self.failed_pin = self.restored_pin
        plan = self.reconverge()
        self.assertEqual((plan["recreate"], plan["pin"]["stage"]), ([], "current"))
        self.assertEqual(self.reconverge(apply=True, reviewed=plan["planSha256"])["status"], "reconverged")
        self.assertEqual(self.commands, [])
        self.assertEqual(self.files[self.pin_path], self.restored_pin)

    def test_container_running_another_image_is_refused_whatever_its_reference(self) -> None:
        for config in ("sha256:" + "8" * 64, None):
            with self.subTest(config=config):
                self.containers["postgres"]["image"] = "sha256:" + "8" * 64
                self.containers["postgres"]["config"] = config or self.reference
                with self.assertRaises(installer.InstallError): self.reconverge()
                self.assertUntouched()

    def test_foreign_mutable_reference_is_refused(self) -> None:
        self.containers["backup"]["config"] = PREFIX + "postgres:latest"
        with self.assertRaises(installer.InstallError): self.reconverge()
        self.assertUntouched()

    def test_writable_backup_mount_is_refused(self) -> None:
        real = self._container
        self._container = lambda role: dict(real(role), Mounts=[dict(real(role)["Mounts"][0], RW=True)])
        with self.assertRaises(installer.InstallError): self.reconverge()
        self.assertUntouched()

    def test_different_reviewed_plan_is_refused_before_any_effect(self) -> None:
        self.reconverge()
        for reviewed in (None, "0" * 64):
            with self.subTest(reviewed=reviewed):
                with self.assertRaises(installer.InstallError): self.reconverge(apply=True, reviewed=reviewed)
                self.assertUntouched()

    def test_pin_not_exactly_the_receiver_rewrite_is_refused(self) -> None:
        pin = json.loads(self.failed_pin)
        pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = PREFIX + "edge@sha256:" + "c" * 64
        self.files[self.pin_path] = self.failed_pin = deploy._recovery_pin_bytes(pin)
        with self.assertRaises(installer.InstallError): self.reconverge()
        self.assertUntouched()

    def test_pin_of_a_release_that_does_not_follow_the_current_is_refused(self) -> None:
        incoming = json.loads(self.files[Path("/opt/duora/releases/2026.11.14-1.json")])
        incoming["previousReleaseId"] = "2026.11.12-1"
        raw = json.dumps(incoming, indent=2).encode()
        self.files[Path("/opt/duora/releases/2026.11.14-1.json")] = raw
        pin = json.loads(self.failed_pin)
        pin["manifest_sha256"] = _sha(raw)
        self.files[self.pin_path] = self.failed_pin = deploy._recovery_pin_bytes(pin)
        with self.assertRaises(installer.InstallError): self.reconverge()
        self.assertUntouched()

    def test_non_canonical_pin_bytes_are_refused(self) -> None:
        self.files[self.pin_path] = self.failed_pin = installer._json(json.loads(self.failed_pin))
        with self.assertRaises(installer.InstallError): self.reconverge()
        self.assertUntouched()

    def test_runtime_environment_other_than_the_current_release_is_refused(self) -> None:
        self.files[self.runtime_env] += b"DUORA_EXTRA=1\n"
        with self.assertRaises(installer.InstallError): self.reconverge()
        self.assertUntouched()

    def test_open_swap_or_rehost_is_refused(self) -> None:
        for path in (self.paths.state / "postgres-swap/attempt.json",
                     self.paths.ingress / installer.REHOST_FENCE_DIRECTORY / "pending.json"):
            with self.subTest(path=path.parent.name):
                path.parent.mkdir(mode=0o700, exist_ok=True)
                path.write_text("{}")
                try:
                    with self.assertRaises(installer.InstallError): self.reconverge()
                finally:
                    path.unlink()
                self.assertUntouched()

    def test_runtime_that_does_not_converge_is_reported_as_refused(self) -> None:
        plan = self.reconverge()

        def recreate_without_effect(command):
            self.commands.append(command[len(self.compose):])
            return subprocess.CompletedProcess(command, 0, "", "")
        self.host = self.host.__class__(**{**self.host.__dict__, "run": recreate_without_effect})
        with self.assertRaises(installer.InstallError): self.reconverge(apply=True, reviewed=plan["planSha256"])
        self.assertEqual(self.verified, [])

    def test_failed_compose_step_stops_before_starting_the_database(self) -> None:
        plan = self.reconverge()

        def fail_backup(command):
            self.commands.append(command[len(self.compose):])
            return subprocess.CompletedProcess(command, 1 if "--force-recreate" in command else 0, "", "boom")
        self.host = self.host.__class__(**{**self.host.__dict__, "run": fail_backup})
        with self.assertRaises(installer.InstallError): self.reconverge(apply=True, reviewed=plan["planSha256"])
        self.assertEqual(self.commands[-1], ["up", "--detach", "--wait", "--no-deps", "--force-recreate", "backup"])
        self.assertEqual(self.verified, [])


if __name__ == "__main__":
    unittest.main()
