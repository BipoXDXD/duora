"""Policy guard ordering: synthetic external services, real temporary filesystem."""

import contextlib
import io
import json
import stat
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import receiver as r
from test_host_isolation import synthetic_goblin_policy, worker

OLD = "ghcr.io/bipoxdxd/goblinshift-executor@sha256:" + "c" * 64
NEW = "ghcr.io/bipoxdxd/goblinshift-executor@sha256:" + "b" * 64


class GoblinPolicyPreflightTests(unittest.TestCase):
    def test_policy_change_during_input_refuses_before_intent_or_import(self) -> None:
        # The policy was valid when pinned. A change while receiving input must
        # be refused before the import barrier, not merely before Coolify start.
        for changed in ["policy", "config", "directory"]:
            with (
                self.subTest(changed=changed),
                tempfile.TemporaryDirectory() as temporary,
            ):
                root = Path(temporary)
                with (
                    contextlib.redirect_stdout(io.StringIO()),
                    self.assertRaises(ValueError),
                ):
                    worker(root, "goblinshift", changed + "-change-during-input")
                self.assertTrue((root / "goblinshift.unpacked").exists())
                self.assertFalse((root / "goblinshift.importing").exists())
                self.assertFalse((root / "goblinshift.starting").exists())
                self.assertFalse((root / "state/pending.json").exists())
                self.assertFalse((root / "state/goblinshift/pending.json").exists())

    def test_untrusted_policy_refuses_before_import_or_start(self) -> None:
        # Removing the administrative-policy preflight would let the old writer
        # trust this deploy-owned fixture and reach Docker/Coolify.
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with (
                contextlib.redirect_stdout(io.StringIO()),
                self.assertRaises(ValueError),
            ):
                worker(root, "goblinshift", "untrusted-policy")
            self.assertFalse((root / "goblinshift.importing").exists())
            self.assertFalse((root / "goblinshift.starting").exists())
            self.assertFalse((root / "state/goblinshift/pending.json").exists())


class GoblinPolicyMetadataTests(unittest.TestCase):
    def test_untrusted_owners_types_modes_and_links_refuse(self) -> None:
        trusted = {
            "st_mode": stat.S_IFREG | 0o600,
            "st_uid": 0,
            "st_gid": 0,
            "st_nlink": 1,
        }
        for changed in [
            {"st_uid": 1001},
            {"st_gid": 1001},
            {"st_mode": stat.S_IFREG | 0o620},
            {"st_mode": stat.S_IFREG | 0o644},
            {"st_mode": stat.S_IFLNK | 0o600},
            {"st_mode": stat.S_IFIFO | 0o600},
            {"st_nlink": 2},
        ]:
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                r.require_admin_metadata(
                    SimpleNamespace(**(trusted | changed)), private=True
                )
        r.require_admin_metadata(SimpleNamespace(**trusted), private=True)
        r.require_admin_metadata(
            SimpleNamespace(**(trusted | {"st_mode": stat.S_IFDIR | 0o700})),
            directory=True,
        )

    def test_duplicate_fields_empty_mutable_or_extra_policy_refuse(self) -> None:
        for raw in [
            b'{"allowed":[],"allowed":[]}',
            b'{"allowed":[]}',
            b'{"allowed":["main"]}',
            b'{"allowed":[3]}',
            json.dumps({"allowed": [OLD], "extra": True}).encode(),
            b"[]",
        ]:
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                r.allowed_policy(raw)


class GoblinPolicyAtomicTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "executor-images.json"
        self.raw = json.dumps({"allowed": [OLD]}).encode()
        self.path.write_bytes(self.raw)
        self.path.chmod(0o600)
        self.patch = patch.object(r, "GOBLIN_IMAGES", self.path)
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def test_append_preserves_old_digest_and_ignores_predictable_next_symlink(
        self,
    ) -> None:
        sentinel = self.path.parent / "sentinel"
        sentinel.write_bytes(b"preserve")
        self.path.with_suffix(".next").symlink_to(sentinel)
        with synthetic_goblin_policy(self.path) as policy:
            r.append_goblin_policy(policy, NEW)
        self.assertEqual(json.loads(self.path.read_bytes()), {"allowed": [NEW, OLD]})
        self.assertEqual(sentinel.read_bytes(), b"preserve")
        self.assertEqual(stat.S_IMODE(self.path.stat().st_mode), 0o600)
        self.assertEqual(list(self.path.parent.glob(".executor-images-*.tmp")), [])

    def test_write_failure_before_rename_preserves_original_and_cleans_own_temp(
        self,
    ) -> None:
        with (
            synthetic_goblin_policy(self.path) as policy,
            patch.object(
                r.os, "fsync", side_effect=OSError("synthetic durability failure")
            ),
            self.assertRaises(OSError),
        ):
            r.append_goblin_policy(policy, NEW)
        self.assertEqual(self.path.read_bytes(), self.raw)
        self.assertEqual(list(self.path.parent.glob(".executor-images-*.tmp")), [])

    def test_concurrent_change_is_not_overwritten(self) -> None:
        with synthetic_goblin_policy(self.path) as policy:
            self.path.write_bytes(b'{"allowed":[]}')
            with self.assertRaises(ValueError):
                r.append_goblin_policy(policy, NEW)
        self.assertEqual(self.path.read_bytes(), b'{"allowed":[]}')

    def test_exclusive_temp_collision_does_not_follow_or_delete_foreign_entry(
        self,
    ) -> None:
        target = self.path.parent / "foreign"
        target.write_bytes(b"preserve")
        collision = self.path.parent / (".executor-images-" + "0" * 32 + ".tmp")
        collision.symlink_to(target)
        with (
            synthetic_goblin_policy(self.path) as policy,
            patch.object(r.secrets, "token_hex", return_value="0" * 32),
            self.assertRaises(FileExistsError),
        ):
            r.append_goblin_policy(policy, NEW)
        self.assertTrue(collision.is_symlink())
        self.assertEqual(target.read_bytes(), b"preserve")
        self.assertEqual(self.path.read_bytes(), self.raw)

    def test_other_project_files_unchanged_and_wrong_repository_refused(self) -> None:
        other = self.path.parent / "duora-current.json"
        other.write_bytes(b"preserve")
        with (
            synthetic_goblin_policy(self.path) as policy,
            self.assertRaises(ValueError),
        ):
            r.append_goblin_policy(
                policy, "ghcr.io/attacker/executor@sha256:" + "b" * 64
            )
        self.assertEqual(self.path.read_bytes(), self.raw)
        self.assertEqual(other.read_bytes(), b"preserve")


if __name__ == "__main__":
    unittest.main()
