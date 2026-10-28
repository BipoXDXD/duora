"""Gate de latência de leitura no edge do sombra (c2_server_latency)."""
from __future__ import annotations

import json
import unittest

import c2_server_latency as server


def edge(t: float, uri: str, seconds: float, method: str = "GET") -> str:
    return json.dumps({"ts": t, "request": {"method": method, "uri": uri}, "duration": seconds, "status": 200})


def phase(name: str, start: float, end: float) -> list[str]:
    return [json.dumps({"event": "phase_start", "phase": name, "t": start}),
            json.dumps({"event": "phase_end", "phase": name, "t": end})]


class ParseEdgeReadsTests(unittest.TestCase):
    def test_only_api_gets_outside_auth_are_reads(self) -> None:
        lines = [edge(1.0, "/api/v1/me", 0.012), edge(2.0, "/api/v1/auth/csrf", 0.5),
                 edge(3.0, "/api/v1/events/x/participation", 0.3, method="POST"), edge(4.0, "/", 0.9),
                 "not json", json.dumps({"msg": "sem request"})]
        self.assertEqual([server.EdgeRead(1.0, 12.0)], server.parse_edge_reads(lines))


class EvaluateTests(unittest.TestCase):
    def test_server_p95_at_the_ceiling_passes_and_one_millisecond_above_fails(self) -> None:
        windows = server.phase_windows(phase("warmup", 0.0, 10.0) + phase("peak", 10.0, 20.0))
        reads = [server.EdgeRead(1.0, 250.0), server.EdgeRead(11.0, 251.0)]
        result = server.evaluate(reads, windows, 250.0)
        self.assertEqual(True, result["phases"]["warmup"]["read_p95_ok"])
        self.assertEqual(False, result["phases"]["peak"]["read_p95_ok"])
        self.assertFalse(result["all_ok"])

    def test_read_at_the_phase_end_belongs_to_the_next_phase(self) -> None:
        windows = server.phase_windows(phase("a", 0.0, 10.0) + phase("b", 10.0, 20.0))
        result = server.evaluate([server.EdgeRead(10.0, 999.0)], windows, 250.0)
        self.assertEqual(0, result["phases"]["a"]["reads"])
        self.assertEqual(1, result["phases"]["b"]["reads"])

    def test_phase_without_reads_has_no_verdict_and_no_reads_at_all_is_not_a_pass(self) -> None:
        windows = server.phase_windows(phase("baseline", 0.0, 10.0))
        result = server.evaluate([], windows, 250.0)
        self.assertIsNone(result["phases"]["baseline"]["read_p95_ok"])
        self.assertFalse(result["all_ok"])

    def test_aborted_phase_without_end_is_left_out(self) -> None:
        lines = phase("warmup", 0.0, 10.0) + [json.dumps({"event": "phase_start", "phase": "ramp", "t": 10.0})]
        self.assertEqual([server.PhaseWindow("warmup", 0.0, 10.0)], server.phase_windows(lines))


if __name__ == "__main__":
    unittest.main()
