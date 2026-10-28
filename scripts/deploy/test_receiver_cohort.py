"""Receiver identity at the real installer and administrative recovery boundaries.

Only root metadata is modeled: receiver bytes, symlinks, module loading and digest
checks are real. No Docker, network, installation or product data is used.
"""
import hashlib
import os
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path
from unittest.mock import patch

import duora_deploy as deploy
import install_executor as installer

E9 = "e9af03f08162ecf2bda094d38511b44b810043863d3104f60ea1046ae687f3b3"
FIXTURE = Path(__file__).parent / "fixtures/receiver-2.1.1-e9af.py.txt"


class ReceiverCohortTests(unittest.TestCase):
    @contextmanager
    def installed_receiver(self, *, version: str = "2.1.1", content: bytes | None = None):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "receiver.py"
            target = root / "versions" / (version + "-" + E9) / "receiver.py"
            target.parent.mkdir(parents=True)
            target.write_bytes(FIXTURE.read_bytes() if content is None else content)
            target.chmod(0o755)
            entry.symlink_to(target)
            actual_lstat = Path.lstat

            def root_metadata(path: Path):
                values = list(actual_lstat(path))
                values[4], values[5] = 0, 0
                if path != root and root not in path.parents:
                    values[0] = int(values[0]) & ~0o022
                return os.stat_result(values)

            with patch.object(installer, "RECEIVER_PATH", entry), \
                    patch.object(Path, "lstat", root_metadata), \
                    patch.object(os, "geteuid", return_value=0):
                yield entry, target

    def test_exact_e9_target_is_bound_before_administrative_operations(self) -> None:
        self.assertEqual(hashlib.sha256(FIXTURE.read_bytes()).hexdigest(), E9)
        with self.installed_receiver() as (entry, target):
            binding = installer.receiver_binding()
            self.assertEqual(binding["sha256"], E9)
            self.assertEqual(binding["readlink"], str(target))
            self.assertEqual(binding["resolvedTarget"], str(target))
            self.assertEqual(entry.resolve(), target)

    def test_e9_bytes_under_the_legacy_version_path_are_refused(self) -> None:
        with self.installed_receiver(version="2.0.2"):
            with self.assertRaises(installer.InstallError):
                installer.receiver_binding()

    def test_changed_receiver_bytes_at_the_qualified_target_are_refused(self) -> None:
        with self.installed_receiver(content=FIXTURE.read_bytes() + b"\n"):
            with self.assertRaises(installer.InstallError):
                installer.receiver_binding()

    def test_administrative_recovery_loads_the_same_qualified_installed_e9(self) -> None:
        with self.installed_receiver():
            receiver = deploy._recovery_verified_receiver()
            self.assertEqual(receiver.receiver_digest(), E9)
            self.assertEqual(receiver.PROJECTS["duora"]["web"], "ghcr.io/bipoxdxd/duora-web")
