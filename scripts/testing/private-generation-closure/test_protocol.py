"""Process experiments; synthetic data only. Run against the built Probe.dll."""
import concurrent.futures
import fcntl
import os
from pathlib import Path
import select
import shutil
import subprocess
import tempfile
import unittest
import uuid

DLL = os.environ.get("DUORA_CLOSURE_PROBE", str(Path(__file__).parent / "bin/Debug/net10.0/Probe.dll"))
DOMAIN = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
PAYLOAD = bytes([1, 2, 3]) * 32768


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="duora-closure-", dir=os.environ.get("DUORA_CLOSURE_ROOT"))
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.generation = uuid.uuid4().hex
        self.area = os.environ.get("DUORA_CLOSURE_AREA", "candidate")
        self.run_probe("reserve")

    def spawn(self, command, point="none", kind=0, generation=None, domain=DOMAIN):
        child = subprocess.Popen(["dotnet", DLL, command, str(self.root), self.area,
                                  generation or self.generation, domain, point, str(kind)],
                                 stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                 stderr=subprocess.PIPE, text=True, bufsize=1)
        self.addCleanup(self.stop, child)
        return child

    @staticmethod
    def stop(child):
        if child.poll() is None:
            child.kill()
        child.communicate(timeout=15)

    def finish(self, child, expected=0, release=False):
        out, err = child.communicate(input="go\n" if release else None, timeout=20)
        self.assertEqual(expected, child.returncode, (out, err))
        return out.strip()

    def run_probe(self, command, expected=0, **kwargs):
        return self.finish(self.spawn(command, **kwargs), expected)

    def ready(self, child):
        self.assertTrue(select.select([child.stdout], [], [], 20)[0], "barrier not reached")
        self.assertEqual("READY\n", child.stdout.readline())

    @property
    def control(self):
        return self.root / "control" / (self.area + "-" + self.generation)

    def objects(self, generation=None):
        prefix = ("review-candidates/" if self.area == "candidate" else "derivatives/review-")
        return [self.root / "data" / (prefix + (generation or self.generation) + "-" + kind)
                for kind in ("thumb", "card", "full")]

    def assert_empty(self):
        self.assertTrue(all(not p.exists() for p in self.objects()))
        self.assertFalse(list((self.root / "data").rglob("*.partial")))

    def test_late_write_is_rejected_after_restart(self):
        self.run_probe("write")
        self.assertEqual("CLOSED", self.run_probe("close"))
        self.assertEqual("SWEPT", self.run_probe("sweep"))
        self.run_probe("write", expected=2)
        self.assert_empty()
        self.assertEqual("CLOSED", self.run_probe("close"))
        self.assertEqual("SWEPT", self.run_probe("sweep"))

    def test_close_response_blocks_late_write_without_sweep(self):
        self.assertEqual("CLOSED", self.run_probe("close"))
        self.run_probe("write", expected=2)
        self.assert_empty()

    def test_lock_and_data_descriptors_are_close_on_exec(self):
        child = self.spawn("write", "eof")
        self.ready(child)
        seen = set()
        for descriptor in Path(f"/proc/{child.pid}/fd").iterdir():
            try:
                target = os.readlink(descriptor)
            except FileNotFoundError:
                continue
            if target == str(self.control / "lock") or target.endswith(".partial"):
                info = Path(f"/proc/{child.pid}/fdinfo/{descriptor.name}").read_text()
                flags = next(line.split()[1] for line in info.splitlines() if line.startswith("flags:"))
                self.assertTrue(int(flags, 8) & os.O_CLOEXEC, "descriptor can escape across exec")
                seen.add("lock" if target.endswith("/lock") else "data")
        self.assertEqual({"lock", "data"}, seen)
        self.finish(child, release=True)

    def test_admission_paused_before_lock_cannot_write_after_close(self):
        child = self.spawn("write", "before-lock")
        self.ready(child)
        self.run_probe("close")
        self.run_probe("sweep")
        self.finish(child, 2, release=True)
        self.assert_empty()

    def test_paused_writer_blocks_close_and_sweep_for_each_variant(self):
        for kind in range(3):
            with self.subTest(kind=kind):
                child = self.spawn("write", "eof", kind)
                self.ready(child)
                self.assertTrue(list((self.root / "data").rglob("*.partial")))
                for _ in range(3):
                    self.assertEqual("BUSY", self.run_probe("close"))
                    self.assertEqual("BUSY", self.run_probe("sweep"))
                self.assertFalse((self.control / "closed").exists())
                self.finish(child, release=True)
                self.assertTrue(all(p.read_bytes() == PAYLOAD for p in self.objects()))
                # A new, already-reserved generation is needed for the next variant.
                self.run_probe("close")
                self.run_probe("sweep")
                self.assert_empty()
                self.generation = uuid.uuid4().hex
                self.run_probe("reserve")

    def test_sigkill_releases_lock_and_recovers_without_writer_ack(self):
        for point in ("before-put", "eof", "after-put"):
            for kind in range(3):
                with self.subTest(point=point, kind=kind):
                    child = self.spawn("write", point, kind)
                    self.ready(child)
                    child.kill()
                    child.communicate(timeout=15)
                    self.assertEqual(-9, child.returncode)
                    self.assertEqual("CLOSED", self.run_probe("close"))
                    self.assertEqual("SWEPT", self.run_probe("sweep"))
                    self.assert_empty()
                    self.run_probe("write", expected=2)
                    self.generation = uuid.uuid4().hex
                    self.run_probe("reserve")

    def test_closer_crash_and_lost_response_are_idempotent(self):
        for point in ("marker-flushed", "marker-renamed", "directory-synced"):
            with self.subTest(point=point):
                self.run_probe("write")
                child = self.spawn("close", point)
                self.ready(child)
                child.kill()
                child.communicate(timeout=15)
                self.assertEqual(-9, child.returncode)
                self.assertEqual("CLOSED", self.run_probe("close"))
                self.run_probe("sweep")
                self.run_probe("write", expected=2)
                self.assert_empty()
                self.generation = uuid.uuid4().hex
                self.run_probe("reserve")

    def test_sweep_crash_leaves_closure_and_retry_removes_remaining_variants(self):
        for kind in range(3):
            with self.subTest(kind=kind):
                self.run_probe("write")
                self.run_probe("close")
                child = self.spawn("sweep", "after-delete", kind)
                self.ready(child)
                child.kill()
                child.communicate(timeout=15)
                self.run_probe("write", expected=2)
                self.assertEqual("SWEPT", self.run_probe("sweep"))
                self.assert_empty()
                self.generation = uuid.uuid4().hex
                self.run_probe("reserve")

    def test_two_writers_and_collectors_share_one_authority(self):
        first = self.spawn("write", "eof")
        self.ready(first)
        self.assertEqual("BUSY", self.run_probe("write"))
        self.assertEqual("BUSY", self.run_probe("close"))
        self.finish(first, release=True)
        with concurrent.futures.ThreadPoolExecutor(2) as pool:
            results = list(pool.map(lambda _: self.run_probe("close"), range(2)))
        self.assertTrue(all(r in ("CLOSED", "BUSY") for r in results))
        self.assertIn("CLOSED", results)
        self.run_probe("sweep")
        self.assert_empty()

    def test_sweep_requires_prior_closure(self):
        self.run_probe("write")
        self.run_probe("sweep", expected=2)
        self.assertTrue(all(p.exists() for p in self.objects()))

    def test_missing_corrupt_or_wrong_domain_controls_fail_closed(self):
        self.run_probe("write", domain="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", expected=2)
        self.run_probe("close", domain="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", expected=2)
        reservation = self.control / "reservation"
        original = reservation.read_bytes()
        for value in (b"", b"corrupt"):
            reservation.write_bytes(value)
            self.run_probe("write", expected=2)
            self.run_probe("close", expected=2)
        reservation.unlink()
        self.run_probe("write", expected=2)
        self.run_probe("close", expected=2)
        reservation.write_bytes(original)
        (self.control / "closed").write_text("corrupt")
        self.run_probe("write", expected=2)
        self.run_probe("close", expected=2)
        self.run_probe("sweep", expected=2)
        self.assert_empty()

    def test_reservation_cannot_reopen_closed_generation(self):
        self.run_probe("close")
        self.run_probe("reserve", expected=2)
        self.run_probe("write", expected=2)
        self.assert_empty()

    def test_missing_lock_is_not_recreated(self):
        (self.control / "lock").unlink()
        self.run_probe("write", expected=2)
        self.run_probe("close", expected=2)
        self.assertFalse((self.control / "lock").exists())

    def test_sweep_preserves_other_generation_legacy_asset_and_unknown_partial(self):
        other = uuid.uuid4().hex
        self.run_probe("reserve", generation=other)
        self.run_probe("write", generation=other)
        self.run_probe("write")
        area = self.objects()[0].parent
        asset = area / "cccccccccccccccccccccccccccccccc-full"
        unknown = Path(str(self.objects()[0]) + ".unknown.partial")
        asset.write_bytes(b"confirmed")
        unknown.write_bytes(b"not-attributable")
        attributable = Path(str(self.objects()[0]) + "." + "d" * 32 + ".partial")
        attributable.write_bytes(b"orphan")
        self.run_probe("close")
        self.run_probe("sweep")
        self.assertFalse(attributable.exists())
        self.assertTrue(all(not p.exists() for p in self.objects()))
        self.assertTrue(all(p.read_bytes() == PAYLOAD for p in self.objects(other)))
        self.assertEqual(b"confirmed", asset.read_bytes())
        self.assertEqual(b"not-attributable", unknown.read_bytes())

    def test_controls_outside_media_restore_keep_generation_closed(self):
        self.run_probe("write")
        snapshot = self.root / "snapshot"
        shutil.copytree(self.root / "data", snapshot)
        self.run_probe("close")
        self.run_probe("sweep")
        shutil.copytree(snapshot, self.root / "data", dirs_exist_ok=True)
        self.run_probe("write", expected=2)
        self.run_probe("sweep")
        self.assert_empty()

    def test_sync_failure_returns_no_success_and_retry_keeps_closure(self):
        self.run_probe("write")
        self.run_probe("close", point="fail-close-sync", expected=2)
        self.run_probe("write", expected=2)
        self.run_probe("close")
        self.run_probe("sweep", point="fail-removal-sync", expected=2)
        self.run_probe("write", expected=2)
        self.assertEqual("SWEPT", self.run_probe("sweep"))
        self.assert_empty()

    def test_delete_permission_failure_does_not_report_sweep_success(self):
        self.run_probe("write")
        self.run_probe("close")
        area = self.objects()[0].parent
        area.chmod(0o500)
        try:
            self.run_probe("sweep", expected=2)
            self.assertTrue(all(p.exists() for p in self.objects()))
        finally:
            area.chmod(0o700)
        self.run_probe("sweep")
        self.assert_empty()

    def test_inherited_lock_description_prevents_false_drain(self):
        # A parent dying/closing is insufficient if a descendant inherited its OFD.
        with (self.control / "lock").open("r+") as descriptor:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
            child = subprocess.Popen(["python3", "-c", "import sys; print('READY', flush=True); sys.stdin.readline()"],
                                     pass_fds=(descriptor.fileno(),), stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            self.addCleanup(self.stop, child)
            self.ready(child)
        self.assertEqual("BUSY", self.run_probe("close"))
        self.finish(child, release=True)
        self.assertEqual("CLOSED", self.run_probe("close"))

    def test_lock_symlink_is_denied(self):
        lock = self.control / "lock"
        lock.rename(self.control / "old-lock")
        lock.symlink_to("old-lock")
        self.run_probe("write", expected=2)
        self.run_probe("close", expected=2)

    def test_counterexample_lock_replacement_creates_two_authorities(self):
        child = self.spawn("write", "before-put")
        self.ready(child)
        lock = self.control / "lock"
        lock.unlink()
        lock.touch()
        self.run_probe("close")
        self.run_probe("sweep")
        self.finish(child, release=True)
        self.assertTrue(all(p.read_bytes() == PAYLOAD for p in self.objects()))

    def test_counterexample_missing_closed_marker_is_indistinguishable_from_open(self):
        self.run_probe("close")
        self.run_probe("sweep")
        (self.control / "closed").unlink()
        self.run_probe("write")
        self.assertTrue(all(p.read_bytes() == PAYLOAD for p in self.objects()))

    def test_counterexample_restoring_open_controls_reopens_generation(self):
        # Expected counterexample, NOT a passing restore guarantee.
        snapshot = self.root / "snapshot"
        shutil.copytree(self.root / "control", snapshot)
        self.run_probe("close")
        self.run_probe("sweep")
        shutil.rmtree(self.root / "control")
        shutil.copytree(snapshot, self.root / "control")
        self.run_probe("write")
        self.assertTrue(all(p.read_bytes() == PAYLOAD for p in self.objects()))

    def test_counterexample_legacy_writer_ignores_closure(self):
        # Same UID / writable mount: advisory flock does not revoke this capability.
        child = self.spawn("legacy-write", "before-put")
        self.ready(child)
        self.run_probe("close")
        self.run_probe("sweep")
        self.finish(child, release=True)
        self.assertTrue(all(p.read_bytes() == PAYLOAD for p in self.objects()))


if __name__ == "__main__":
    unittest.main(verbosity=2)
