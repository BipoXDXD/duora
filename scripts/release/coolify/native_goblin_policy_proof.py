"""Necessary native proof: disposable GitHub-hosted Linux runner, never production.

Run explicitly with root on that runner. Standard discovery does not silently
skip these cases or pretend that synthetic Mac fixtures prove Linux ownership.
"""

import io
import json
import os
import stat
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import receiver as r

OLD = "ghcr.io/bipoxdxd/goblinshift-executor@sha256:" + "c" * 64
OTHER = "ghcr.io/bipoxdxd/goblinshift-executor@sha256:" + "d" * 64
NEW = "ghcr.io/bipoxdxd/goblinshift-executor@sha256:" + "b" * 64


class NativePolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="goblin-policy-proof-", dir="/var/lib")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.directory = self.root / "project"
        self.directory.mkdir(mode=0o700)
        self.path = self.directory / "executor-images.json"
        self.raw = json.dumps({"allowed": [OLD, OTHER]}).encode()
        self.path.write_bytes(self.raw)
        self.path.chmod(0o600)
        self.config = self.root / "executor-ctl.json"
        self.config.write_text(json.dumps({"images_file": str(self.path)}))
        self.config.chmod(0o600)
        for target, value in [
            ("GOBLIN_IMAGES", self.path),
            ("GOBLIN_EXECUTOR_CONFIG", self.config),
        ]:
            patcher = patch.object(r, target, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_real_root_chain_appends_without_losing_previous_digests(self) -> None:
        with r.goblin_policy() as policy:
            r.append_goblin_policy(policy, NEW)
        self.assertEqual(json.loads(self.path.read_bytes()), {"allowed": [NEW, OLD, OTHER]})
        info = self.path.stat()
        self.assertEqual(
            (info.st_uid, info.st_gid, info.st_nlink, stat.S_IMODE(info.st_mode)),
            (0, 0, 1, 0o600),
        )

    def test_uid1001_ancestor_refuses_before_policy_write(self) -> None:
        os.chown(self.directory, 1001, 1001)
        with self.assertRaises(ValueError), r.goblin_policy():
            self.fail("untrusted ancestry admitted")
        self.assertEqual(self.path.read_bytes(), self.raw)

    def test_group_writable_ancestor_refuses(self) -> None:
        self.directory.chmod(0o770)
        with self.assertRaises(ValueError), r.goblin_policy():
            self.fail("writable ancestry admitted")

    def test_symlink_ancestor_refuses(self) -> None:
        moved = self.root / "original"
        self.directory.rename(moved)
        self.directory.symlink_to(moved, target_is_directory=True)
        with self.assertRaises(ValueError), r.goblin_policy():
            self.fail("symlink ancestry admitted")
        self.assertEqual((moved / self.path.name).read_bytes(), self.raw)

    def test_directory_substitution_is_not_redirected_to_new_path(self) -> None:
        with r.goblin_policy() as policy:
            moved = self.root / "pinned"
            self.directory.rename(moved)
            self.directory.mkdir(mode=0o700)
            with self.assertRaises(ValueError):
                r.append_goblin_policy(policy, NEW)
        self.assertEqual((moved / self.path.name).read_bytes(), self.raw)
        self.assertFalse(self.path.exists())

    def test_foreign_leaf_owner_refuses(self) -> None:
        os.chown(self.path, 1001, 1001)
        with self.assertRaises(ValueError), r.goblin_policy():
            self.fail("foreign-owned file admitted")

    def test_hardlinked_leaf_refuses(self) -> None:
        os.link(self.path, self.root / "hardlink")
        with self.assertRaises(ValueError), r.goblin_policy():
            self.fail("hardlinked file admitted")

    def test_symlink_leaf_refuses(self) -> None:
        target = self.root / "saved-policy.json"
        self.path.rename(target)
        self.path.symlink_to(target)
        with self.assertRaises(ValueError), r.goblin_policy():
            self.fail("symlink leaf admitted")
        self.assertEqual(target.read_bytes(), self.raw)

    def test_legacy_reader_configuration_refuses(self) -> None:
        self.config.write_text(json.dumps({"images_file": "/srv/goblinshift/executor/images.json"}))
        with self.assertRaises(ValueError), r.goblin_policy():
            self.fail("legacy reader admitted")
        self.assertEqual(self.path.read_bytes(), self.raw)

    def test_change_during_input_refuses_before_global_intent_or_import(self) -> None:
        # Real ownership/dirfds; only transport and external Docker boundaries
        # are replaced. No Docker operation is allowed to be reached.
        for changed in ["policy", "config", "directory"]:
            with self.subTest(changed=changed):
                state = self.root / ("state-" + changed)
                # Each native subcase gets independent inputs after mutation.
                if not self.path.exists():
                    self.path.write_bytes(self.raw)
                    self.path.chmod(0o600)
                self.path.write_bytes(self.raw)
                self.config.write_text(json.dumps({"images_file": str(self.path)}))
                release = {
                    "version": 2,
                    "receiver_sha256": r.receiver_digest(),
                    "project": "goblinshift",
                    "commit": "a" * 40,
                    "images": {
                        role: repo + "@sha256:" + "b" * 64
                        for role, repo in r.PROJECTS["goblinshift"].items()
                    },
                }
                imported: list[bool] = []

                def unpack(
                    stream: object,
                    destination: Path,
                    roles: object,
                    selected: str = changed,
                    document: object = release,
                ) -> None:
                    (destination / "release.json").write_text(json.dumps(document))
                    if selected == "policy":
                        self.path.write_text('{"allowed":[]}')
                    elif selected == "config":
                        self.config.write_text(
                            '{"images_file":"/srv/goblinshift/executor/images.json"}'
                        )
                    else:
                        self.directory.rename(self.root / "old-pinned")
                        self.directory.mkdir(mode=0o700)

                def import_images(data: object, stage: Path, calls: list[bool] = imported) -> None:
                    calls.append(True)
                    raise AssertionError("Docker import reached before policy revalidation")

                with (
                    patch.object(r, "STATE", state),
                    patch.object(r, "require_storage"),
                    patch.object(r, "unpack", side_effect=unpack),
                    patch.object(r, "import_images", side_effect=import_images),
                    patch.object(sys, "argv", ["receiver.py", "goblinshift"]),
                    patch.dict(os.environ, {"SSH_ORIGINAL_COMMAND": "release " + "a" * 40}),
                    self.assertRaises(ValueError),
                ):
                    r.main()
                self.assertEqual(imported, [])
                self.assertFalse((state / "pending.json").exists())
                self.assertFalse((state / "goblinshift/pending.json").exists())


def main() -> int:
    if not (
        sys.platform == "linux"
        and os.geteuid() == 0
        and os.environ.get("GITHUB_ACTIONS") == "true"
        and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
    ):
        print(
            "Refused: root on a disposable GitHub-hosted Linux runner is required.",
            file=sys.stderr,
        )
        return 3
    buffer = io.StringIO()
    result = unittest.TextTestRunner(stream=buffer, verbosity=2).run(
        unittest.defaultTestLoader.loadTestsFromTestCase(NativePolicyTests)
    )
    print(
        json.dumps(
            {
                "scope": "native_goblin_policy_filesystem_only_not_production_or_daemon_transition",
                "receiver_sha256": r.receiver_digest(),
                "tests": result.testsRun,
                "failures": len(result.failures),
                "errors": len(result.errors),
                "skipped": len(result.skipped),
                "successful": result.wasSuccessful(),
            },
            sort_keys=True,
        )
    )
    if not result.wasSuccessful():
        print(buffer.getvalue(), file=sys.stderr)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
