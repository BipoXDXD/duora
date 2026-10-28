"""Latência de leitura medida no edge do sombra, por fase da repetição C2 (issue #86).

O p95 do cliente inclui o RTT entre o Mac e a VPS pelo túnel SSH (~190 ms Brasil→Boston), que cresce com pouca
carga e decide o gate sozinho. Este gate lê a duração que o próprio Caddy do sombra registra (do primeiro byte da
requisição ao último da resposta) e aplica o mesmo teto de leitura do harness.

Leitura no servidor = GET em /api/v1/ fora de /api/v1/auth/. Inclui o GET /me da reconciliação: no servidor ele é
uma leitura igual às outras.

Coleta do log: `docker logs duora-c2-edge-1 2>&1 > edge.log` (sem prefixo de serviço). As janelas usam o relógio do
Mac e o `ts` do Caddy usa o da VPS (fim da resposta); o gate assume NTP nos dois lados.
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
import c2_load

READ_PREFIX = "/api/v1/"
EXCLUDED_PREFIX = "/api/v1/auth/"
ACCESS_LOGGER = "http.log.access"
STOP_EVENTS = ("abort", "interrupted")
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
    clients: int


@dataclass(frozen=True)
class Run:
    windows: list[PhaseWindow]
    complete: bool


def parse_edge_reads(lines: Iterable[str]) -> list[EdgeRead]:
    """Linhas do log de acesso JSON do Caddy; fica de fora o que não é JSON, não é do logger de acesso, não é
    leitura de API ou não teve sucesso (o harness também só mede latência das respostas ok)."""
    reads = []
    for line in lines:
        try:
            entry = json.loads(line)
        except ValueError:
            continue
        if not isinstance(entry, dict) or not str(entry.get("logger", "")).startswith(ACCESS_LOGGER):
            continue
        request = entry.get("request")
        if not isinstance(request, dict) or not _is_read(request) or not _is_ok(entry.get("status")):
            continue
        try:
            reads.append(EdgeRead(float(entry["ts"]), float(entry["duration"]) * MS_PER_SECOND))
        except (KeyError, TypeError, ValueError) as error:
            raise ValueError(f"linha de acesso sem ts/duration numéricos: {line.strip()[:200]}") from error
    return reads


def _is_read(request: dict[str, Any]) -> bool:
    uri = str(request.get("uri", ""))
    return request.get("method") == "GET" and uri.startswith(READ_PREFIX) and not uri.startswith(EXCLUDED_PREFIX)


def _is_ok(status: Any) -> bool:
    return isinstance(status, int) and c2_load.classify(status) == "ok"


def parse_run(events: Sequence[dict[str, Any]]) -> Run:
    """Janelas como o harness as fecha; a execução só é completa sem abort/interrupted e com toda fase encerrada."""
    clients = {e["phase"]: int(e.get("clients", 0)) for e in events if e["event"] == "phase_start"}
    windows = [PhaseWindow(name, start, end, clients[name]) for name, start, end in harness.phase_windows(events)]
    stopped = any(e["event"] in STOP_EVENTS for e in events)
    ended = sum(1 for e in events if e["event"] == "phase_end")
    return Run(windows, complete=not stopped and ended == len(clients))


def evaluate(reads: Sequence[EdgeRead], run: Run, ceiling_ms: float) -> dict[str, Any]:
    """Fase com carga e sem leitura no log reprova: o gate nunca aprova o que não mediu."""
    phases = {}
    for window in run.windows:
        durations = [read.ms for read in reads if window.start <= read.t < window.end]
        p95 = harness.percentile(durations, P95)
        verdict = None if window.clients == 0 else p95 is not None and p95 <= ceiling_ms
        phases[window.phase] = {"clients": window.clients, "reads": len(durations), "p95_ms": p95,
                                "read_p95_ok": verdict}
    verdicts = [phase["read_p95_ok"] for phase in phases.values() if phase["read_p95_ok"] is not None]
    all_ok = run.complete and bool(verdicts) and all(verdicts)
    return {"ceiling_ms": ceiling_ms, "run_complete": run.complete, "phases": phases, "all_ok": all_ok}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="gate de latência de leitura medido no edge do sombra")
    parser.add_argument("--events", type=Path, required=True, help="events.jsonl da evidência")
    parser.add_argument("--edge-log", type=Path, required=True, help="log de acesso JSON do edge do sombra")
    args = parser.parse_args(argv)
    with args.edge_log.open(encoding="utf-8") as edge:
        reads = parse_edge_reads(edge)
    result = evaluate(reads, parse_run(harness.read_jsonl(args.events)), harness.Limits().read_p95_ms)
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return harness.EXIT_OK if result["all_ok"] else harness.EXIT_ABORTED


if __name__ == "__main__":
    sys.exit(main())
