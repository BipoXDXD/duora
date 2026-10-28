"""Gate de latência de leitura no edge do sombra (c2_server_latency)."""
from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from typing import Any

import c2_harness as harness
import c2_server_latency as server

ACCESS = "http.log.access.log0"


def edge(t: float, uri: str, seconds: float, method: str = "GET", status: int = 200,
         logger: str = ACCESS) -> str:
    return json.dumps({"logger": logger, "ts": t, "request": {"method": method, "uri": uri},
                       "duration": seconds, "status": status})


def phase(name: str, start: float, end: float, clients: int = 10) -> list[dict[str, Any]]:
    return [{"event": "phase_start", "phase": name, "t": start, "clients": clients},
            {"event": "phase_end", "phase": name, "t": end}]


class ParseEdgeReadsTests(unittest.TestCase):
    def test_only_api_gets_outside_auth_are_reads(self) -> None:
        lines = [edge(1.0, "/api/v1/me", 0.012), edge(2.0, "/api/v1/auth/csrf", 0.5),
                 edge(3.0, "/api/v1/events/x/participation", 0.3, method="POST"), edge(4.0, "/", 0.9),
                 "not json", json.dumps({"msg": "sem request"})]
        self.assertEqual([server.EdgeRead(1.0, 12.0)], server.parse_edge_reads(lines))

    def test_only_successful_responses_count_like_the_client_gate(self) -> None:
        lines = [edge(1.0, "/api/v1/me", 0.010, status=status) for status in (200, 304, 401, 429, 500, 503)]
        self.assertEqual([server.EdgeRead(1.0, 10.0), server.EdgeRead(1.0, 10.0)], server.parse_edge_reads(lines))

    def test_error_logger_entry_is_not_counted_twice(self) -> None:
        lines = [edge(1.0, "/api/v1/me", 0.010), edge(1.0, "/api/v1/me", 0.010, logger="http.log.error")]
        self.assertEqual(1, len(server.parse_edge_reads(lines)))

    def test_access_line_without_numeric_ts_fails_loudly(self) -> None:
        line = json.dumps({"logger": ACCESS, "ts": "x", "request": {"method": "GET", "uri": "/api/v1/me"},
                           "duration": 0.01, "status": 200})
        with self.assertRaisesRegex(ValueError, "ts/duration"):
            server.parse_edge_reads([line])


class ParseRunTests(unittest.TestCase):
    def test_finished_run_is_complete(self) -> None:
        run = server.parse_run(phase("warmup", 0.0, 10.0) + phase("peak", 10.0, 20.0, clients=100))
        self.assertTrue(run.complete)
        self.assertEqual([server.PhaseWindow("warmup", 0.0, 10.0, 10), server.PhaseWindow("peak", 10.0, 20.0, 100)],
                         run.windows)

    def test_aborted_run_is_incomplete(self) -> None:
        events = phase("warmup", 0.0, 10.0) + [{"event": "phase_start", "phase": "ramp", "t": 10.0, "clients": 10},
                                               {"event": "abort", "phase": "ramp", "t": 12.0}]
        self.assertFalse(server.parse_run(events).complete)

    def test_interrupted_run_is_incomplete(self) -> None:
        events = phase("warmup", 0.0, 10.0) + [{"event": "interrupted", "t": 11.0}]
        self.assertFalse(server.parse_run(events).complete)

    def test_phase_left_open_makes_the_run_incomplete(self) -> None:
        events = phase("warmup", 0.0, 10.0) + [{"event": "phase_start", "phase": "ramp", "t": 10.0, "clients": 10}]
        self.assertFalse(server.parse_run(events).complete)


class EvaluateTests(unittest.TestCase):
    def test_server_p95_at_the_ceiling_passes_and_one_millisecond_above_fails(self) -> None:
        run = server.parse_run(phase("warmup", 0.0, 10.0) + phase("peak", 10.0, 20.0))
        result = server.evaluate([server.EdgeRead(1.0, 250.0), server.EdgeRead(11.0, 251.0)], run, 250.0)
        self.assertEqual(True, result["phases"]["warmup"]["read_p95_ok"])
        self.assertEqual(False, result["phases"]["peak"]["read_p95_ok"])
        self.assertFalse(result["all_ok"])

    def test_read_at_the_phase_end_belongs_to_the_next_phase(self) -> None:
        run = server.parse_run(phase("a", 0.0, 10.0) + phase("b", 10.0, 20.0))
        result = server.evaluate([server.EdgeRead(10.0, 999.0)], run, 250.0)
        self.assertEqual(0, result["phases"]["a"]["reads"])
        self.assertEqual(1, result["phases"]["b"]["reads"])

    def test_loaded_phase_without_reads_fails_instead_of_being_skipped(self) -> None:
        run = server.parse_run(phase("warmup", 0.0, 10.0) + phase("peak", 10.0, 20.0))
        result = server.evaluate([server.EdgeRead(11.0, 10.0)], run, 250.0)
        self.assertEqual(False, result["phases"]["warmup"]["read_p95_ok"])
        self.assertFalse(result["all_ok"])

    def test_phase_without_clients_has_no_verdict(self) -> None:
        run = server.parse_run(phase("baseline", 0.0, 10.0, clients=0) + phase("peak", 10.0, 20.0))
        result = server.evaluate([server.EdgeRead(11.0, 10.0)], run, 250.0)
        self.assertIsNone(result["phases"]["baseline"]["read_p95_ok"])
        self.assertTrue(result["all_ok"])

    def test_no_loaded_phase_at_all_is_not_a_pass(self) -> None:
        run = server.parse_run(phase("baseline", 0.0, 10.0, clients=0))
        self.assertFalse(server.evaluate([], run, 250.0)["all_ok"])

    def test_aborted_run_never_passes_even_with_good_phases(self) -> None:
        events = phase("warmup", 0.0, 10.0) + [{"event": "abort", "phase": "warmup", "t": 10.5}]
        result = server.evaluate([server.EdgeRead(1.0, 10.0)], server.parse_run(events), 250.0)
        self.assertEqual(True, result["phases"]["warmup"]["read_p95_ok"])
        self.assertFalse(result["all_ok"])


class MainTests(unittest.TestCase):
    def run_main(self, events: list[dict[str, Any]], lines: list[str]) -> int:
        with tempfile.TemporaryDirectory() as tmp:
            events_path, edge_path = Path(tmp, "events.jsonl"), Path(tmp, "edge.log")
            events_path.write_text("".join(json.dumps(e) + "\n" for e in events), encoding="utf-8")
            edge_path.write_text("".join(line + "\n" for line in lines), encoding="utf-8")
            return server.main(["--events", str(events_path), "--edge-log", str(edge_path)])

    def test_uses_the_harness_read_ceiling_for_the_exit_code(self) -> None:
        ceiling_s = harness.Limits().read_p95_ms / 1000
        events = phase("peak", 0.0, 10.0)
        self.assertEqual(harness.EXIT_OK, self.run_main(events, [edge(1.0, "/api/v1/me", ceiling_s)]))
        self.assertEqual(harness.EXIT_ABORTED, self.run_main(events, [edge(1.0, "/api/v1/me", ceiling_s + 0.002)]))


if __name__ == "__main__":
    unittest.main()
