"""Issue #403: a transient registry hiccup on the historical pull must not fail the lab; anything else still does."""
import contextlib
import io
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import continuity_lab as lab

REFERENCE = "ghcr.io/bipoxdxd/duora-web@sha256:" + "d2235" + "0" * 59
TIMEOUT = ('Error response from daemon: Get "https://ghcr.io/v2/bipoxdxd/duora-web/manifests/sha256:d2235": '
           "net/http: timeout awaiting response headers")
FAKE_DOCKER = """#!/bin/sh
count=$(cat "$FAKE_STATE/count" 2>/dev/null || echo 0)
count=$((count + 1))
echo "$count" > "$FAKE_STATE/count"
echo "$*" >> "$FAKE_STATE/calls"
if [ "$count" -le "$FAKE_FAILURES" ]; then
    printf '%s\\n' "$FAKE_MESSAGE" >&2
    exit 1
fi
echo "$4"
"""


class FakeDocker:
    """A real `docker` executable on PATH that fails its first N calls with a fixed daemon message."""

    def __init__(self, test: unittest.TestCase, *, failures: int, message: str = TIMEOUT) -> None:
        directory = tempfile.TemporaryDirectory()
        test.addCleanup(directory.cleanup)
        self.state = Path(directory.name)
        executable = self.state / "docker"
        executable.write_text(FAKE_DOCKER)
        executable.chmod(0o700)
        environment = {"PATH": str(self.state) + os.pathsep + os.environ["PATH"], "FAKE_STATE": str(self.state),
                       "FAKE_FAILURES": str(failures), "FAKE_MESSAGE": message}
        patcher = mock.patch.dict(os.environ, environment)
        patcher.start()
        test.addCleanup(patcher.stop)

    def calls(self) -> list[str]:
        path = self.state / "calls"
        return path.read_text().splitlines() if path.exists() else []


class PullRetryTests(unittest.TestCase):
    def pull(self) -> tuple[list[float], str]:
        sleeps: list[float] = []
        log = io.StringIO()
        with contextlib.redirect_stderr(log):
            lab.pull_by_digest(REFERENCE, sleep=sleeps.append)
        return sleeps, log.getvalue()

    def test_transient_timeouts_below_the_ceiling_let_the_lab_continue(self) -> None:
        docker = FakeDocker(self, failures=lab.PULL_MAX_ATTEMPTS - 1)
        sleeps, log = self.pull()
        self.assertEqual(docker.calls(), ["pull --quiet " + REFERENCE] * lab.PULL_MAX_ATTEMPTS)
        self.assertEqual(sleeps, list(lab.PULL_BACKOFF_SECONDS))
        for attempt in range(1, lab.PULL_MAX_ATTEMPTS + 1):
            self.assertIn(f"attempt {attempt}/{lab.PULL_MAX_ATTEMPTS}", log)
        self.assertEqual(log.count("transient failure (timeout)"), lab.PULL_MAX_ATTEMPTS - 1)

    def test_timeouts_beyond_the_ceiling_fail_with_the_original_message(self) -> None:
        docker = FakeDocker(self, failures=lab.PULL_MAX_ATTEMPTS)
        with self.assertRaises(RuntimeError) as raised:
            self.pull()
        self.assertEqual(str(raised.exception),
                         "Synthetic lab command failed: docker pull --quiet " + REFERENCE + ": " + TIMEOUT + "\n")
        self.assertEqual(len(docker.calls()), lab.PULL_MAX_ATTEMPTS)

    def test_every_transient_class_is_retried(self) -> None:
        messages = {
            "timeout": TIMEOUT,
            "deadline": "Error response from daemon: Get \"https://ghcr.io/v2/\": context deadline exceeded",
            "refused": "Error response from daemon: dial tcp 140.82.112.33:443: connect: connection refused",
            "reset": "Error response from daemon: read tcp 10.1.0.4:443: read: connection reset by peer",
            "502": "Error response from daemon: received unexpected HTTP status: 502 Bad Gateway",
            "503": "Error response from daemon: received unexpected HTTP status: 503 Service Unavailable",
            "500": "Error response from daemon: received unexpected HTTP status: 500 Internal Server Error",
        }
        for name, message in messages.items():
            with self.subTest(failure=name):
                docker = FakeDocker(self, failures=1, message=message)
                self.pull()
                self.assertEqual(len(docker.calls()), 2)

    def test_permanent_failures_fail_on_the_first_attempt(self) -> None:
        messages = {
            "missing digest": "Error response from daemon: manifest unknown: manifest unknown",
            "not found": "Error response from daemon: pull access denied for duora-web, repository does not exist",
            "denied": "Error response from daemon: denied: permission_denied: read_package",
            "unauthorized": "Error response from daemon: unauthorized: authentication required",
            "denied with a timeout word": "Error response from daemon: denied: requested access to the resource is denied (timeout)",
            "unknown": "Error response from daemon: invalid reference format",
        }
        for name, message in messages.items():
            with self.subTest(failure=name):
                docker = FakeDocker(self, failures=1, message=message)
                with self.assertRaises(RuntimeError) as raised:
                    self.pull()
                self.assertEqual(str(raised.exception),
                                 "Synthetic lab command failed: docker pull --quiet " + REFERENCE + ": " + message + "\n")
                self.assertEqual(len(docker.calls()), 1)

    def test_only_a_digest_reference_is_pulled(self) -> None:
        docker = FakeDocker(self, failures=0)
        for reference in ("ghcr.io/bipoxdxd/duora-web:latest", "ghcr.io/bipoxdxd/duora-web@sha256:abc",
                          "ghcr.io/bipoxdxd/duora-web@sha256:" + "A" * 64, ""):
            with self.subTest(reference=reference), self.assertRaises(RuntimeError):
                lab.pull_by_digest(reference, sleep=lambda _seconds: None)
        self.assertEqual(docker.calls(), [])

    def test_ceiling_and_backoff_stay_inside_the_root_lab_timeout(self) -> None:
        self.assertEqual(lab.PULL_MAX_ATTEMPTS, 3)
        self.assertEqual(lab.PULL_BACKOFF_SECONDS, (5, 15))
        self.assertEqual(len(lab.PULL_BACKOFF_SECONDS), lab.PULL_MAX_ATTEMPTS - 1)


if __name__ == "__main__":
    unittest.main()
