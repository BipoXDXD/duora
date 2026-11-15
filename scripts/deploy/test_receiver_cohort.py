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

CANDIDATE = "6a8352bb8996c15c7c8115ab92ae35328afdffc76a513bde7929d7ea51cfc835"
FIXTURE = Path(__file__).parent / "fixtures/receiver-2.1.2-6a83.py.txt"


class ReceiverCohortTests(unittest.TestCase):
    @contextmanager
    def installed_receiver(self, *, version: str = "2.1.2", content: bytes | None = None):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()  # sem o symlink /var -> /private/var do macOS
            entry = root / "receiver.py"
            target = root / "versions" / (version + "-" + CANDIDATE) / "receiver.py"
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

    def test_exact_candidate_target_is_bound_before_administrative_operations(self) -> None:
        self.assertEqual(hashlib.sha256(FIXTURE.read_bytes()).hexdigest(), CANDIDATE)
        with self.installed_receiver() as (entry, target):
            binding = installer.receiver_binding()
            self.assertEqual(binding["sha256"], CANDIDATE)
            self.assertEqual(binding["readlink"], str(target))
            self.assertEqual(binding["resolvedTarget"], str(target))
            self.assertEqual(entry.resolve(), target)

    def test_candidate_bytes_under_the_legacy_version_path_are_refused(self) -> None:
        with self.installed_receiver(version="2.1.1"):
            with self.assertRaises(installer.InstallError):
                installer.receiver_binding()

    def test_changed_receiver_bytes_at_the_qualified_target_are_refused(self) -> None:
        with self.installed_receiver(content=FIXTURE.read_bytes() + b"\n"):
            with self.assertRaises(installer.InstallError):
                installer.receiver_binding()

    def test_administrative_recovery_loads_the_same_qualified_installed_candidate(self) -> None:
        with self.installed_receiver():
            receiver = deploy._recovery_verified_receiver()
            self.assertEqual(receiver.receiver_digest(), CANDIDATE)
            self.assertEqual(receiver.PROJECTS["duora"]["web"], "ghcr.io/bipoxdxd/duora-web")
