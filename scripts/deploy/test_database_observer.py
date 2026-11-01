"""Exercise the observer's real pipe/deadline handling with faulty child processes."""
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

import continuity_lab as lab


CHILD = '''import os, sys, time
mode = os.environ["OBSERVER_TEST_MODE"]
if mode == "startup-eof": sys.exit(1)
if mode == "startup-silent": time.sleep(10)
print("READY", flush=True)
for request in sys.stdin:
    if mode == "eof": sys.exit(1)
    if mode == "partial":
        sys.stdout.write("0"); sys.stdout.flush(); time.sleep(10)
    if mode == "silent": time.sleep(10)
    print(mode, flush=True)
'''


class DatabaseObserverTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory(prefix="duora-observer-protocol-")
        self.addCleanup(directory.cleanup)
        docker = Path(directory.name) / "docker"
        docker.write_text("#!" + sys.executable + "\n" + CHILD)
        docker.chmod(0o755)
        env = patch.dict(os.environ, {"PATH": directory.name + os.pathsep + os.environ["PATH"]})
        env.start()
        self.addCleanup(env.stop)

    def test_each_nonready_exit_status_remains_a_failed_readiness_sample(self) -> None:
        for status in ("1", "2", "3"):
            with self.subTest(status=status), patch.dict(os.environ, {"OBSERVER_TEST_MODE": status}):
                with lab.DatabaseObserver("synthetic") as observer:
                    samples = [observer.sample(), observer.sample()]
                self.assertTrue(all(not sample["database_ready"] for sample in samples))
                with self.assertRaises(RuntimeError):
                    lab.validate_observations(samples, start=samples[0]["at"], end=time.time(),
                                              errors=[], events_alive=True)

    def test_repeated_live_responses_cover_the_window(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "0"}), lab.DatabaseObserver("synthetic") as observer:
            start = time.time()
            samples = [observer.sample(), observer.sample()]
            lab.validate_observations(samples, start=start, end=time.time(), errors=[], events_alive=True)
            self.assertTrue(all(sample["database_ready"] for sample in samples))
            self.assertTrue(all(sample["probeSeconds"] >= 0 for sample in samples))

    def test_partial_reply_cannot_bypass_the_probe_deadline(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "partial"}), lab.DatabaseObserver("synthetic") as observer:
            with self.assertRaisesRegex(lab.DatabaseObservationError, "probe:deadline"):
                observer.sample()

    def test_silent_observer_cannot_qualify_continuity(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "silent"}), lab.DatabaseObserver("synthetic") as observer:
            with self.assertRaisesRegex(lab.DatabaseObservationError, "probe:deadline"):
                observer.sample()

    def test_dead_or_malformed_observer_is_refused_without_disclosing_output(self) -> None:
        for mode in ("eof", "private-untrusted-output", "0\n0", "0" * 100):
            with self.subTest(mode=mode), patch.dict(os.environ, {"OBSERVER_TEST_MODE": mode}):
                with lab.DatabaseObserver("synthetic") as observer:
                    with self.assertRaises(lab.DatabaseObservationError) as failure:
                        observer.sample()
                self.assertNotIn("private-untrusted-output", str(failure.exception))

    def test_dead_startup_does_not_open_an_observation_window(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "startup-eof"}):
            with self.assertRaisesRegex(lab.DatabaseObservationError, "startup:eof"):
                lab.DatabaseObserver("synthetic")

    def test_silent_startup_is_bounded_and_refused(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "startup-silent"}):
            with self.assertRaisesRegex(lab.DatabaseObservationError, "startup:deadline"):
                lab.DatabaseObserver("synthetic")
