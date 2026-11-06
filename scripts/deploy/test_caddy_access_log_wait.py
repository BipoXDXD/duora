"""Observation of asynchronous access logs and edge requests, without starting Docker or a web server."""

from __future__ import annotations

import json
from types import SimpleNamespace
from typing import Any
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


class CaddyEdgeRequestTests(unittest.TestCase):
    TLS_ALERT = "tlsv1 alert internal error\nwget: error getting response: Connection reset by peer\n"

    def case(self) -> access_log.CaddyAccessLogTests:
        case = access_log.CaddyAccessLogTests("test_uppercase_uuid_and_query_string_are_both_redacted")
        case.container = "synthetic-no-container"
        return case

    def test_fixture_starts_the_probed_upstream_before_waiting_for_http_and_tls(self) -> None:
        fixture = access_log.CaddyAccessLogTests
        calls: list[Any] = []
        with patch.object(access_log, "_run", side_effect=calls.append), \
                patch.object(access_log, "wait_for_edge", side_effect=calls.append), \
                patch.object(access_log, "remove_container"):
            fixture.setUpClass()
            fixture.doClassCleanups()
        started, upstream, ready = calls
        self.assertEqual(["docker", "run"], started[:2])
        self.assertEqual("duora-upstream", upstream[-1])
        self.assertEqual(fixture.wget, ready)

    def test_a_refused_tls_handshake_fails_at_the_request_with_the_wget_error(self) -> None:
        refused = SimpleNamespace(returncode=1, stdout="", stderr=self.TLS_ALERT)
        with patch.object(access_log.subprocess, "run", return_value=refused) as capture:
            with self.assertRaisesRegex(AssertionError, "tlsv1 alert internal error"):
                self.case().request("/api/v1/me/blocks/synthetic")
        self.assertEqual(1, capture.call_count)

    def test_an_answer_other_than_the_expected_status_fails_at_the_request(self) -> None:
        bad_gateway = SimpleNamespace(returncode=1, stdout="", stderr="  HTTP/1.1 502 Bad Gateway\n")
        with patch.object(access_log.subprocess, "run", return_value=bad_gateway):
            with self.assertRaisesRegex(AssertionError, "502 Bad Gateway"):
                self.case().request("/api/v1/me/blocks/synthetic")

    def test_a_post_refused_with_the_expected_status_is_accepted_despite_the_wget_exit_code(self) -> None:
        too_large = SimpleNamespace(returncode=1, stdout="", stderr=(
            "  HTTP/1.1 413 Request Entity Too Large\n"
            "wget: server returned error: HTTP/1.1 413 Request Entity Too Large\n"))
        with patch.object(access_log.subprocess, "run", return_value=too_large):
            self.case().post("/webhooks/synthetic", 16, expected_status=413)

    def test_a_post_that_never_got_an_answer_fails_with_the_wget_error(self) -> None:
        refused = SimpleNamespace(returncode=1, stdout="", stderr=self.TLS_ALERT)
        with patch.object(access_log.subprocess, "run", return_value=refused):
            with self.assertRaisesRegex(AssertionError, "tlsv1 alert internal error"):
                self.case().post("/webhooks/synthetic", 16, expected_status=413)


if __name__ == "__main__":
    unittest.main()
