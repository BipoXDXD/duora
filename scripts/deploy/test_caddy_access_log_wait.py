"""Observation of asynchronous access logs, without starting Docker or a web server."""

from __future__ import annotations

import json
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import test_caddy_access_log as access_log


class CaddyAccessLogObservationTests(unittest.TestCase):
    def test_an_empty_first_snapshot_does_not_hide_a_delayed_access(self) -> None:
        expected = "/api/v1/me/sessions/{id}?redacted"
        case = access_log.CaddyAccessLogTests("test_uppercase_uuid_and_query_string_are_both_redacted")
        case.container = "synthetic-no-container"
        empty = SimpleNamespace(stdout="", stderr="")
        ready = SimpleNamespace(stdout=json.dumps({"status": 200, "request": {"uri": expected}}), stderr="")
        with patch.object(access_log.subprocess, "run", side_effect=[empty, ready]) as capture, \
                patch.object(access_log.time, "sleep"), \
                patch.object(access_log.time, "monotonic", side_effect=[0.0, 0.1, 0.2]):
            self.assertIn(expected, case.logged_uris(expected))
            self.assertEqual(2, capture.call_count)

    def test_waiting_for_a_redacted_access_preserves_other_entries_so_leaks_are_still_visible(self) -> None:
        expected = "/api/v1/me/sessions/{id}?redacted"
        leaked = "/api/v1/me/sessions/RAW-CANARY?token=RAW-CANARY"
        raw = json.dumps({"status": 200, "request": {"uri": leaked}})
        ready = raw + "\n" + json.dumps({"status": 200, "request": {"uri": expected}})
        case = access_log.CaddyAccessLogTests("test_uppercase_uuid_and_query_string_are_both_redacted")
        case.container = "synthetic-no-container"
        with patch.object(access_log.subprocess, "run", side_effect=[
                SimpleNamespace(stdout=raw, stderr=""), SimpleNamespace(stdout=ready, stderr="")]), \
                patch.object(access_log.time, "sleep"), \
                patch.object(access_log.time, "monotonic", side_effect=[0.0, 0.1, 0.2]):
            observed = case.logged_uris(expected)
            self.assertIn(expected, observed)
            self.assertIn(leaked, observed)

    def test_an_unrelated_entry_cannot_replace_the_missing_access_or_wait_forever(self) -> None:
        expected = "/api/v1/me/sessions/{id}?redacted"
        unrelated = json.dumps({"status": 200, "request": {"uri": "/healthz"}})
        case = access_log.CaddyAccessLogTests("test_uppercase_uuid_and_query_string_are_both_redacted")
        case.container = "synthetic-no-container"
        with patch.object(access_log.subprocess, "run", return_value=SimpleNamespace(stdout=unrelated, stderr="")), \
                patch.object(access_log.time, "sleep"), \
                patch.object(access_log.time, "monotonic", side_effect=[0.0, 0.1, 6.0]):
            with self.assertRaisesRegex(AssertionError, "access log"):
                case.logged_uris(expected)


if __name__ == "__main__":
    unittest.main()
