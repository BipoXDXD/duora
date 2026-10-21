import io
import json
import tarfile
import tempfile
import unittest
from pathlib import Path
from typing import Any
from unittest.mock import patch

import receiver as r
import send

SHA = "a" * 40
DIGEST = "sha256:" + "b" * 64


class ReviewRegressionTests(unittest.TestCase):
    def test_goblin_transport_uses_exact_scanned_digests_not_tags(self) -> None:
        refs = {role: repo + "@" + DIGEST for role, repo in r.PROJECTS["goblinshift"].items()}
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "scanned.json"
            path.write_text(json.dumps(refs))
            with patch.dict("os.environ", {"SCANNED_IMAGES_FILE": str(path)}):
                self.assertEqual(send.requested("goblinshift", SHA), refs)

    def test_pax_metadata_is_counted_in_archive_limit(self) -> None:
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w", format=tarfile.PAX_FORMAT) as archive:
            member = tarfile.TarInfo("release.json")
            member.size = 2
            member.pax_headers = {"comment": "x" * (1024 * 1024)}
            archive.addfile(member, io.BytesIO(b"{}"))
        stream.seek(0)
        with tempfile.TemporaryDirectory() as temp, self.assertRaises(ValueError):
            r.unpack(stream, Path(temp), {"app"}, limit=100)

    def test_duora_predecessor_is_taken_from_trusted_current_state(self) -> None:
        manifest = {"releaseId": "new", "previousReleaseId": None}
        self.assertEqual(r.link_previous(manifest, "old")["previousReleaseId"], "old")
        self.assertIsNone(r.link_previous(manifest, None)["previousReleaseId"])
        with self.assertRaises(ValueError):
            r.link_previous(manifest, "new")

    def test_unresolved_pending_release_blocks_before_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp)
            r.require_no_pending(state)
            (state / "pending.json").write_text(json.dumps({"deployment_uuid": "still-running"}))
            with self.assertRaises(ValueError):
                r.require_no_pending(state)

    def test_stillon_shared_backup_lock_blocks_release(self) -> None:
        import fcntl

        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / ".entrada.trava"
            with path.open("a") as handle:
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                with (
                    self.assertRaises(BlockingIOError),
                    r.release_locks(Path(temp) / "state.lock", path),
                ):
                    pass
            with r.release_locks(Path(temp) / "state.lock", path):
                pass

    def test_atomic_version_write_replaces_only_when_complete(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "versao"
            path.write_text("old\n")
            r.atomic_text(path, SHA + "\n")
            self.assertEqual(path.read_text(), SHA + "\n")
            self.assertFalse(path.with_suffix(".next").exists())

    def test_pending_intent_survives_lost_start_response_and_blocks_next_release(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp)
            release = {
                "project": "stillon",
                "commit": SHA,
                "images": {"app": "ghcr.io/bipoxdxd/stillon@" + DIGEST},
            }
            calls = []

            def fake_api(path: Any, method: Any = "GET", payload: Any = None) -> Any:
                if path.startswith("deployments/applications/"):
                    self.assertFalse((state / "pending.json").exists())
                    return {"count": 0, "deployments": []}
                self.assertTrue((state / "pending.json").exists())
                calls.append(path)
                if path.endswith("/start"):
                    raise TimeoutError("accepted but response lost")
                return {}

            with patch.object(r, "api", side_effect=fake_api):
                with self.assertRaises(TimeoutError):
                    r.deploy(release, {"app": "image"}, {"uuid": "resource"}, state)
                count = len(calls)
                with self.assertRaises(ValueError):
                    r.deploy(release, {"app": "other"}, {"uuid": "resource"}, state)
                self.assertEqual(len(calls), count)
            self.assertIsNone(json.loads((state / "pending.json").read_text())["deployment_uuid"])

    def test_active_or_unknown_coolify_task_blocks_publication(self) -> None:
        for status in ["queued", "in_progress", "unknown"]:
            with (
                self.subTest(status=status),
                patch.object(
                    r, "api", return_value={"count": 1, "deployments": [{"status": status}]}
                ),
                self.assertRaises(ValueError),
            ):
                r.require_idle("resource")
        with patch.object(
            r, "api", return_value={"count": 1, "deployments": [{"status": "finished"}]}
        ):
            r.require_idle("resource")

    def test_incomplete_stream_and_entry_flood_are_refused(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(ValueError):
                r.unpack(io.BytesIO(b"truncated"), Path(temp), {"app"})
            stream = io.BytesIO()
            with tarfile.open(fileobj=stream, mode="w") as archive:
                for _ in range(3):
                    member = tarfile.TarInfo("app/")
                    member.type = tarfile.DIRTYPE
                    archive.addfile(member)
            stream.seek(0)
            with self.assertRaises(ValueError):
                r.unpack(stream, Path(temp), {"app"}, max_members=2)

    def test_json_allocation_and_slow_stream_are_bounded(self) -> None:
        import time

        class SlowInput:
            def read(self, size: Any) -> bytes:
                time.sleep(0.5)
                return b""

        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "index.json"
            path.write_bytes(b" " * 1025)
            with self.assertRaises(ValueError):
                r.metadata(path, maximum=1024)
            with self.assertRaises(ValueError):
                r.unpack(SlowInput(), Path(temp), {"app"}, timeout=0.02)


if __name__ == "__main__":
    unittest.main()
