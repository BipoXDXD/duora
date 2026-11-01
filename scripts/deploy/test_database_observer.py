"""Exercise the observer's real pipe/deadline handling with faulty child processes."""
import os
from pathlib import Path
import selectors
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
    if mode == "delayed":
        time.sleep(0.1)
        with open(os.environ["OBSERVER_REPLY_CLOCK"], "w") as clock: clock.write(str(time.time()))
    print("0" if mode in ("delayed", "dead-after-reply", "clean-death", "nonzero-shutdown", "bad-shutdown") else mode, flush=True)
    if mode == "dead-after-reply": sys.exit(7)
    if mode == "clean-death": sys.exit(0)
if mode == "bad-shutdown": time.sleep(10)
if mode == "nonzero-shutdown": sys.exit(7)
'''


class DatabaseObserverTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory(prefix="duora-observer-protocol-")
        self.addCleanup(directory.cleanup)
        self.reply_clock = Path(directory.name) / "reply-clock"
        docker = Path(directory.name) / "docker"
        docker.write_text("#!" + sys.executable + "\n" + CHILD)
        docker.chmod(0o755)
        env = patch.dict(os.environ, {"PATH": directory.name + os.pathsep + os.environ["PATH"]})
        env.start()
        self.addCleanup(env.stop)

    def test_readable_reply_cannot_be_accepted_after_descheduling_past_the_deadline(self) -> None:
        real_read = os.read
        def descheduled_read(fd: int, size: int) -> bytes:
            data = real_read(fd, size)
            time.sleep(2.05)  # Real clock/pipe; lose CPU after readiness and read, before accepting the frame.
            return data
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "0"}), lab.DatabaseObserver("synthetic") as observer:
            with patch.object(lab.os, "read", descheduled_read):
                with self.assertRaisesRegex(lab.DatabaseObservationError, "probe:deadline"):
                    observer.sample()

    def test_late_startup_greeting_is_refused_even_if_the_pipe_was_readable(self) -> None:
        class DescheduledSelector(selectors.DefaultSelector):
            def select(self, timeout=None):
                ready = super().select(timeout)
                if ready: time.sleep(5.05)
                return ready
        observer = None
        try:
            with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "0"}), patch.object(
                    lab.selectors, "DefaultSelector", DescheduledSelector):
                with self.assertRaisesRegex(lab.DatabaseObservationError, "startup:deadline"):
                    observer = lab.DatabaseObserver("synthetic")
        finally:
            if observer is not None: observer.close()

    def test_sample_timestamp_is_after_the_new_reply_not_before_the_request(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "delayed", "OBSERVER_REPLY_CLOCK": str(self.reply_clock)}):
            with lab.DatabaseObserver("synthetic") as observer:
                sample = observer.sample()
            self.assertGreaterEqual(sample["at"], float(self.reply_clock.read_text()))

    def test_death_after_the_last_reply_cannot_close_as_a_successful_proof(self) -> None:
        for mode in ("dead-after-reply", "clean-death", "nonzero-shutdown"):
            with self.subTest(mode=mode), patch.dict(os.environ, {"OBSERVER_TEST_MODE": mode}):
                observer = lab.DatabaseObserver("synthetic")
                try:
                    self.assertTrue(observer.sample()["database_ready"])
                    if mode != "nonzero-shutdown": observer._process.wait(timeout=3)
                    with self.assertRaisesRegex(lab.DatabaseObservationError, "shutdown:"):
                        observer.close()
                finally:
                    observer.close()

    def test_shutdown_that_requires_killing_the_peer_is_not_a_successful_proof(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "bad-shutdown"}):
            observer = lab.DatabaseObserver("synthetic")
            try:
                self.assertTrue(observer.sample()["database_ready"])
                with self.assertRaisesRegex(lab.DatabaseObservationError, "shutdown:"):
                    observer.close()
                self.assertIsNotNone(observer._process.poll())
            finally:
                observer.close()

    def test_failed_window_keeps_its_original_error_even_if_cleanup_must_kill_the_peer(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "bad-shutdown"}):
            observer = lab.DatabaseObserver("synthetic")
            with self.assertRaisesRegex(RuntimeError, "synthetic window failure"):
                with observer:
                    observer.sample()
                    raise RuntimeError("synthetic window failure")
            self.assertIsNotNone(observer._process.poll())

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
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "partial"}):
            with self.assertRaisesRegex(lab.DatabaseObservationError, "probe:deadline"):
                with lab.DatabaseObserver("synthetic") as observer:
                    observer.sample()

    def test_silent_observer_cannot_qualify_continuity(self) -> None:
        with patch.dict(os.environ, {"OBSERVER_TEST_MODE": "silent"}):
            with self.assertRaisesRegex(lab.DatabaseObservationError, "probe:deadline"):
                with lab.DatabaseObserver("synthetic") as observer:
                    observer.sample()

    def test_dead_or_malformed_observer_is_refused_without_disclosing_output(self) -> None:
        for mode in ("eof", "private-untrusted-output", "0\n0", "0" * 100):
            with self.subTest(mode=mode), patch.dict(os.environ, {"OBSERVER_TEST_MODE": mode}):
                with self.assertRaisesRegex(lab.DatabaseObservationError, "probe:") as failure:
                    with lab.DatabaseObserver("synthetic") as observer:
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
