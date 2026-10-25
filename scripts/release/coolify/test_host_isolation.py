"""Real processes/flock/files, synthetic Docker/Coolify boundaries; no production access."""

import json
import multiprocessing
import os
import shutil
import sys
import tempfile
import time
import traceback
import unittest
from contextlib import contextmanager, nullcontext, redirect_stderr, redirect_stdout
from multiprocessing.process import BaseProcess
from pathlib import Path
from typing import Any
from unittest.mock import patch

import receiver as r

SHA = "a" * 40
CONFIG_ID = "sha256:" + "c" * 64
PREVIOUS_EXECUTOR = "ghcr.io/bipoxdxd/goblinshift-executor@sha256:" + "c" * 64


@contextmanager
def synthetic_goblin_policy(path: Path, reader_config: Path | None = None) -> Any:
    """Only the kernel trust boundary is synthetic; append/rename/fsync stay real.

    These host-lock tests run without root. Native ownership/ancestry acceptance
    belongs to the separate Linux runner proof, never to this fixture.
    """
    directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    previous = path.read_bytes()
    config_raw = reader_config.read_bytes() if reader_config is not None else None
    metadata = r.require_admin_metadata

    def root_metadata(info: Any, **kwargs: Any) -> None:
        values = list(info)
        values[4:6] = [0, 0]
        metadata(os.stat_result(values), **kwargs)

    def verify() -> None:
        pinned = os.fstat(directory)
        current = path.parent.stat()
        if (current.st_dev, current.st_ino) != (pinned.st_dev, pinned.st_ino):
            raise ValueError("Synthetic pinned directory replaced")
        if reader_config is not None and reader_config.read_bytes() != config_raw:
            raise ValueError("Synthetic reader configuration changed")
        if path.read_bytes() != previous:
            raise ValueError("Synthetic policy changed")

    try:
        with patch.object(r, "require_admin_metadata", side_effect=root_metadata):
            yield {"directory": directory, "raw": previous, "verify": verify}
    finally:
        os.close(directory)


def worker(root: Path, project: str, mode: str) -> None:
    state = root / "state"
    config = root / "config"
    config.mkdir(exist_ok=True)
    (config / "storage.json").write_text(
        json.dumps({"version": 1, "image_roots": [str(root)]})
    )
    whitelist = root / "executor-images.json"
    if mode.endswith("change-during-input"):
        (root / "policy").mkdir()
        whitelist = root / "policy/executor-images.json"
    if not whitelist.exists():
        whitelist.write_text(json.dumps({"allowed": [PREVIOUS_EXECUTOR]}))
        whitelist.chmod(0o600)
    reader_config = config / "executor-ctl.json"
    reader_config.write_text(json.dumps({"images_file": str(whitelist)}))
    (config / (project + ".json")).write_text(
        json.dumps(
            {
                "uuid": project,
                "containers": [project],
                "image_roles": {project: "web" if project == "duora" else "app"},
                "readiness": [],
            }
        )
    )

    def unpack(stream: Any, destination: Path, roles: Any) -> None:
        (root / (project + ".unpacked")).touch()
        (destination / "release.json").write_text("{}")
        if mode == "policy-change-during-input":
            whitelist.write_text(json.dumps({"allowed": []}))
        if mode == "config-change-during-input":
            reader_config.write_text(
                json.dumps({"images_file": "/srv/goblinshift/executor/images.json"})
            )
        if mode == "directory-change-during-input":
            whitelist.parent.rename(root / "pinned-policy")
            whitelist.parent.mkdir()

    release: dict[str, Any] = {
        "project": project,
        "commit": SHA,
        "images": {
            role: repo + "@sha256:" + "b" * 64
            for role, repo in r.PROJECTS[project].items()
        },
    }

    def imported(data: Any, stage: Path) -> Any:
        (root / (project + ".importing")).touch()
        if mode == "hold":
            while not (root / "continue").exists():
                time.sleep(0.01)
        return (
            {role: ref for role, ref in release["images"].items()},
            {role: CONFIG_ID for role in release["images"]},
        )

    def api(path: str, method: str = "GET", payload: Any = None) -> Any:
        if path.startswith("deployments/applications/"):
            return {"count": 0, "deployments": []}
        if path.endswith("/start"):
            (root / (project + ".starting")).touch()
            if mode == "hold-start":
                while not (root / "continue-start").exists():
                    time.sleep(0.01)
            if mode == "lost-response":
                raise TimeoutError("accepted but response lost")
            return {"deployment_uuid": project + "-deployment"}
        if path.startswith("deployments/"):
            return {"status": "failed" if mode == "failed-deployment" else "finished"}
        if method == "PATCH" and path == "applications/" + project + "/envs/bulk":
            return {}
        raise AssertionError(path)

    def command(argv: list[str]) -> bytes:
        if argv != ["/usr/bin/docker", "inspect", project]:
            raise AssertionError(argv)
        return json.dumps(
            [
                {
                    "Image": CONFIG_ID,
                    "State": {"Status": "running", "Health": {"Status": "healthy"}},
                    "Config": {"Labels": {"coolify.applicationUuid": project}},
                }
            ]
        ).encode()

    with (
        patch.object(r, "STATE", state),
        patch.object(r, "BASE", config),
        patch.object(r, "GOBLIN_IMAGES", whitelist),
        (
            patch.object(
                r,
                "goblin_policy",
                side_effect=lambda: synthetic_goblin_policy(whitelist, reader_config),
            )
            if mode != "untrusted-policy"
            else nullcontext()
        ),
        patch.object(os, "geteuid", return_value=0),
        patch.object(r, "unpack", side_effect=unpack),
        patch.object(r, "validate_release", return_value=release),
        patch.object(r, "import_images", side_effect=imported),
        patch.object(r, "api", side_effect=api),
        patch.object(r, "command", side_effect=command),
        patch.object(r, "configure_duora"),
        patch.object(
            shutil,
            "disk_usage",
            return_value=type(
                "Disk", (), {"free": 1 if mode == "low-storage" else 100 * 1024**3}
            )(),
        ),
        patch.object(
            r,
            "docker_request",
            return_value=json.dumps({"DockerRootDir": str(root)}).encode(),
        ),
        patch.object(sys, "argv", ["receiver.py", project]),
        patch.dict(os.environ, {"SSH_ORIGINAL_COMMAND": "release " + SHA}),
    ):
        r.main()


def isolated_worker(root: Path, project: str, mode: str) -> None:
    # Spawned interpreter: no inherited locks/patches, diagnostics remain in the test workspace.
    with (
        (root / (project + ".log")).open("a") as log,
        redirect_stdout(log),
        redirect_stderr(log),
    ):
        try:
            worker(root, project, mode)
        except Exception:
            traceback.print_exc()
            raise SystemExit(1) from None


class HostIsolationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.processes: list[BaseProcess] = []
        self.addCleanup(self.stop_workers)
        # Synthetic databases/volumes/rollback images must survive every scenario.
        for name in ["postgres/data", "uploads/asset", "previous/image"]:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"preserve")

    def stop_workers(self) -> None:
        for process in self.processes:
            if process.is_alive():
                process.kill()
            process.join(timeout=5)
            process.close()

    def start(self, project: str, mode: str = "success") -> BaseProcess:
        process = multiprocessing.get_context("spawn").Process(
            target=isolated_worker, args=(self.root, project, mode)
        )
        process.start()
        self.processes.append(process)
        return process

    def finish(self, process: BaseProcess) -> int | None:
        process.join(timeout=5)
        self.assertFalse(process.is_alive(), "worker timed out")
        return process.exitcode

    def wait_for_import(self, project: str) -> None:
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if (self.root / (project + ".importing")).exists():
                return
            time.sleep(0.01)
        self.fail("worker did not reach import")

    def assert_preserved(self) -> None:
        allowed = self.root / "executor-images.json"
        if allowed.exists():
            self.assertIn(PREVIOUS_EXECUTOR, json.loads(allowed.read_text())["allowed"])
        for name in ["postgres/data", "uploads/asset", "previous/image"]:
            self.assertEqual((self.root / name).read_bytes(), b"preserve")

    def assert_refused_before_input(self, project: str) -> None:
        process = self.start(project)
        self.assertNotEqual(self.finish(process), 0)
        self.assertFalse((self.root / (project + ".unpacked")).exists())
        self.assertFalse((self.root / "state" / project / "current.json").exists())

    def test_two_projects_cannot_import_or_deploy_simultaneously(self) -> None:
        first = self.start("duora", "hold")
        self.wait_for_import("duora")
        self.assert_refused_before_input("goblinshift")
        (self.root / "continue").touch()
        self.assertEqual(self.finish(first), 0, (self.root / "duora.log").read_text())
        second = self.start("goblinshift")
        self.assertEqual(
            self.finish(second), 0, (self.root / "goblinshift.log").read_text()
        )
        self.assert_preserved()

    def test_killed_import_keeps_host_fenced_until_admin_reconciliation(self) -> None:
        first = self.start("duora", "hold")
        self.wait_for_import("duora")
        self.assert_refused_before_input("goblinshift")
        first.kill()
        self.finish(first)
        self.assert_refused_before_input("goblinshift")
        pending = self.root / "state" / "pending.json"
        self.assertEqual(json.loads(pending.read_text())["project"], "duora")
        # Simulate the runbook after the external import is known stopped. No auto-retry.
        with r.release_locks(self.root / "state" / "host.lock"):
            pending.replace(self.root / "reconciled-import.json")
        second = self.start("goblinshift")
        self.assertEqual(
            self.finish(second), 0, (self.root / "goblinshift.log").read_text()
        )
        self.assert_preserved()

    def test_lost_start_response_blocks_other_project_after_process_exit(self) -> None:
        first = self.start("duora", "lost-response")
        self.assertNotEqual(self.finish(first), 0)
        pending = self.root / "state" / "duora" / "pending.json"
        self.assertIsNone(json.loads(pending.read_text())["deployment_uuid"])
        self.assert_refused_before_input("goblinshift")
        with r.release_locks(self.root / "state" / "host.lock"):
            pending.replace(self.root / "reconciled-project.json")
            (self.root / "state" / "pending.json").replace(
                self.root / "reconciled-host.json"
            )
        second = self.start("goblinshift")
        self.assertEqual(
            self.finish(second), 0, (self.root / "goblinshift.log").read_text()
        )
        self.assert_preserved()

    def test_killed_deployment_does_not_release_other_project(self) -> None:
        first = self.start("goblinshift", "hold-start")
        deadline = time.monotonic() + 5
        while not (self.root / "goblinshift.starting").exists():
            if time.monotonic() >= deadline:
                self.fail("worker did not start deployment")
            time.sleep(0.01)
        self.assert_refused_before_input("duora")
        first.terminate()
        self.finish(first)
        self.assert_refused_before_input("duora")
        # Even removing the host receipt alone cannot bypass an unresolved project intent.
        (self.root / "state" / "pending.json").replace(self.root / "host-evidence.json")
        self.assert_refused_before_input("duora")
        self.assert_preserved()

    def test_failed_deployment_preserves_predecessor_and_fences_host(self) -> None:
        state = self.root / "state" / "duora"
        state.mkdir(parents=True)
        current = state / "current.json"
        current.write_text('{"release":"predecessor"}')
        first = self.start("duora", "failed-deployment")
        self.assertNotEqual(self.finish(first), 0)
        self.assertEqual(json.loads(current.read_text()), {"release": "predecessor"})
        self.assertEqual(
            json.loads((state / "pending.json").read_text())["deployment_uuid"],
            "duora-deployment",
        )
        self.assert_refused_before_input("goblinshift")
        self.assert_preserved()

    def test_low_storage_refuses_before_receiving_or_importing(self) -> None:
        process = self.start("duora", "low-storage")
        self.assertNotEqual(self.finish(process), 0)
        self.assertFalse((self.root / "duora.unpacked").exists())
        self.assertFalse((self.root / "duora.importing").exists())
        self.assertFalse((self.root / "state" / "pending.json").exists())
        self.assert_preserved()

    def test_old_project_pending_is_respected_without_new_host_receipt(self) -> None:
        state = self.root / "state" / "duora"
        state.mkdir(parents=True)
        (state / "pending.json").write_text('{"deployment_uuid":"unknown"}')
        self.assert_refused_before_input("goblinshift")
        self.assert_preserved()


if __name__ == "__main__":
    unittest.main()
