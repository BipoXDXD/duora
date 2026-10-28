"""Latência de leitura medida no edge do sombra, por fase da repetição C2 (issue #86).

O p95 do cliente inclui o RTT entre o Mac e a VPS pelo túnel SSH (~190 ms Brasil→Boston), que cresce com pouca
carga e decide o gate sozinho. Este gate lê a duração que o próprio Caddy do sombra registra (do primeiro byte da
requisição ao último da resposta) e aplica o mesmo teto de leitura do harness.

Leitura no servidor = GET em /api/v1/ fora de /api/v1/auth/. Inclui o GET /me da reconciliação: no servidor ele é
uma leitura igual às outras.
"""
from __future__ import annotations

import argparse
import json
import sys
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import c2_harness as harness

READ_PREFIX = "/api/v1/"
EXCLUDED_PREFIX = "/api/v1/auth/"
MS_PER_SECOND = 1000.0
P95 = 95


@dataclass(frozen=True)
class EdgeRead:
    t: float
    ms: float


@dataclass(frozen=True)
class PhaseWindow:
    phase: str
    start: float
    end: float


def parse_edge_reads(lines: Iterable[str]) -> list[EdgeRead]:
    """Linhas do log de acesso JSON do Caddy; o que não é leitura de API ou não é JSON fica de fora."""
    reads = []
    for line in lines:
        try:
            entry = json.loads(line)
        except ValueError:
            continue
        request = entry.get("request") if isinstance(entry, dict) else None
        if not isinstance(request, dict) or "duration" not in entry:
            continue
        uri = str(request.get("uri", ""))
        if request.get("method") != "GET" or not uri.startswith(READ_PREFIX) or uri.startswith(EXCLUDED_PREFIX):
            continue
        reads.append(EdgeRead(float(entry["ts"]), float(entry["duration"]) * MS_PER_SECOND))
    return reads


def phase_windows(events: Iterable[str]) -> list[PhaseWindow]:
    """Fases com início e fim do events.jsonl; fase sem fim (abortada) fica de fora."""
    windows: list[PhaseWindow] = []
    open_phase: tuple[str, float] | None = None
    for line in events:
        event = json.loads(line)
        if event["event"] == "phase_start":
            open_phase = (event["phase"], float(event["t"]))
        elif event["event"] == "phase_end" and open_phase is not None:
            windows.append(PhaseWindow(open_phase[0], open_phase[1], float(event["t"])))
            open_phase = None
    return windows


def evaluate(reads: Sequence[EdgeRead], windows: Sequence[PhaseWindow], ceiling_ms: float) -> dict[str, Any]:
    phases = {}
    for window in windows:
        durations = [read.ms for read in reads if window.start <= read.t < window.end]
        p95 = harness.percentile(durations, P95)
        phases[window.phase] = {"reads": len(durations), "p95_ms": p95,
                                "read_p95_ok": None if p95 is None else p95 <= ceiling_ms}
    verdicts = [phase["read_p95_ok"] for phase in phases.values() if phase["read_p95_ok"] is not None]
    return {"ceiling_ms": ceiling_ms, "phases": phases, "all_ok": bool(verdicts) and all(verdicts)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="gate de latência de leitura medido no edge do sombra")
    parser.add_argument("--events", type=Path, required=True, help="events.jsonl da evidência")
    parser.add_argument("--edge-log", type=Path, required=True, help="log de acesso JSON do edge do sombra")
    args = parser.parse_args(argv)
    with args.events.open(encoding="utf-8") as events, args.edge_log.open(encoding="utf-8") as edge:
        result = evaluate(parse_edge_reads(edge), phase_windows(events), harness.Limits().read_p95_ms)
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return harness.EXIT_OK if result["all_ok"] else harness.EXIT_ABORTED


if __name__ == "__main__":
    sys.exit(main())
