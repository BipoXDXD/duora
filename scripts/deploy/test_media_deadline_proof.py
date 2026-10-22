"""Fail-closed native proof oracle: blocked is never accepted without durable recovery."""
from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "media"))
from test_sanitizer_isolation import Proof

JOB = "a" * 32


class DeadlineProofTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.state = Path(self.directory.name)
        self.record("blocked")
        self.proof = SimpleNamespace(state=self.state, images={"isolation-probe": "sha256:" + "b" * 64},
                                     exchange=Mock(side_effect=[(8, b""), (8, b""), (7, b""), (2, b"")]),
                                     stop=Mock(), start=Mock(), assert_empty=Mock())

    def record(self, state: str):
        (self.state / "journal.json").write_text(json.dumps([
            {"id": JOB, "state": state, "deadline": 12345, "digest": "c" * 64}]))

    def verify(self, code: int = 8, payload: bytes = b""):
        method = getattr(Proof, "finish_deadline_proof", None)
        assert method is not None, "Blocked deadline proof lacks mandatory recovery checks"
        return method(self.proof, JOB, code, payload)

    def test_confirmed_failure_still_requires_no_remaining_container(self):
        self.record("failed")
        self.verify(4)
        self.proof.assert_empty.assert_called_once()
        self.proof.start.assert_not_called()

    def test_blocked_requires_admission_refusal_restart_cleanup_and_durable_dedupe(self):
        self.verify()
        self.proof.stop.assert_called_once()
        self.proof.start.assert_called_once_with(self.proof.images["isolation-probe"])
        self.assertEqual(4, self.proof.exchange.call_count)
        self.assertEqual(2, self.proof.assert_empty.call_count)

    def test_success_unknown_or_any_payload_is_never_accepted(self):
        for code, payload in [(0, b""), (5, b""), (6, b""), (4, b"partial"), (8, b"partial")]:
            with self.subTest(code=code), self.assertRaises(AssertionError):
                self.verify(code, payload)

    def test_blocked_cannot_qualify_if_status_or_new_admission_is_not_blocked(self):
        for responses in [[(7, b"")], [(8, b""), (4, b"")], [(8, b""), (8, b"partial")]]:
            self.proof.exchange = Mock(side_effect=responses)
            with self.subTest(responses=responses), self.assertRaises(AssertionError):
                self.verify()
        self.proof.start.assert_not_called()

    def test_missing_durable_admission_cannot_qualify(self):
        (self.state / "journal.json").write_text("[]")
        with self.assertRaises(AssertionError):
            self.verify()

    def test_remaining_container_after_restart_cannot_qualify(self):
        self.proof.assert_empty.side_effect = AssertionError("Synthetic orphan remains")
        with self.assertRaises(AssertionError):
            self.verify()

    def test_lost_fencing_or_replayed_success_after_restart_cannot_qualify(self):
        for status, replay in [(5, 2), (7, 0), (7, 5), (7, 6), (7, 8)]:
            self.proof.exchange = Mock(side_effect=[(8, b""), (8, b""), (status, b""), (replay, b"")])
            with self.subTest(status=status, replay=replay), self.assertRaises(AssertionError):
                self.verify()
