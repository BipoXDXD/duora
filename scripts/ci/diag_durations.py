"""Resumo de tempo por classe de teste lido do .diag do Microsoft.Testing.Platform (#507).

O job backend grava o .diag em Trace. Cada mudança de estado de teste é uma linha `TestNodeUpdateMessage { ... }`
logo depois da linha com carimbo de hora do barramento de mensagens. Daqui sai, em Markdown para o resumo do job:
os testes ainda em andamento (sem estado final) e as classes mais longas, do primeiro início ao último fim.

Uso: python3 scripts/ci/diag_durations.py [--top N] ARQUIVO.diag...
Só lê; nunca reprova o job. Nada do .diag além de nomes de teste e tempos chega à saída.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
import re
import sys
from typing import Iterable

STAMP = re.compile(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d+)\d*([+-]\d\d:\d\d) ")
UPDATE = re.compile(
    r"^TestNodeUpdateMessage \{.*?TestNodeUid \{ Value = (?P<uid>[^ ]+) \}"
    r".*?Namespace = (?P<namespace>[^,]+), TypeName = (?P<type>[^,]+), MethodName = (?P<method>[^,]+),"
    r".*\b(?P<state>[A-Za-z]+)TestNodeStateProperty \{"
)
IN_PROGRESS = "InProgress"
FAILED_OUTCOMES = frozenset({"Failed", "Error", "Timeout", "Cancelled"})
DEFAULT_TOP = 15


@dataclass
class TestRun:
    uid: str
    class_name: str
    method: str
    start: datetime
    end: datetime
    outcome: str | None

    @property
    def seconds(self) -> float:
        return (self.end - self.start).total_seconds()


@dataclass(frozen=True)
class ClassSummary:
    name: str
    wall_seconds: float
    tests: int
    failed: int
    open: int


def parse_lines(lines: Iterable[str]) -> list[TestRun]:
    """Uma entrada por teste; teste sem estado final fica aberto até o último carimbo do arquivo."""
    runs: dict[str, TestRun] = {}
    now: datetime | None = None
    for line in lines:
        stamp = STAMP.match(line)
        if stamp:
            now = _parse_stamp(stamp)
            continue
        update = UPDATE.match(line)
        if update is None or now is None:
            continue
        state = update["state"]
        uid = update["uid"]
        if state == IN_PROGRESS:
            runs.setdefault(uid, TestRun(uid, _class_name(update["namespace"], update["type"]),
                                         update["method"], now, now, None))
        elif uid in runs:
            runs[uid].end = now
            runs[uid].outcome = state
    if now is not None:
        for run in runs.values():
            if run.outcome is None:
                run.end = now
    return list(runs.values())


def summarize_classes(runs: list[TestRun]) -> list[ClassSummary]:
    """Classes da mais longa para a mais curta; o tempo é de parede, porque os testes da classe correm juntos."""
    by_class: dict[str, list[TestRun]] = {}
    for run in runs:
        by_class.setdefault(run.class_name, []).append(run)
    summaries = [
        ClassSummary(
            name=name,
            wall_seconds=(max(r.end for r in group) - min(r.start for r in group)).total_seconds(),
            tests=len(group),
            failed=sum(1 for r in group if r.outcome in FAILED_OUTCOMES),
            open=sum(1 for r in group if r.outcome is None),
        )
        for name, group in by_class.items()
    ]
    return sorted(summaries, key=lambda s: (-s.wall_seconds, s.name))


def render_markdown(module: str, runs: list[TestRun], *, top: int) -> str:
    lines = [f"### {module}", ""]
    if not runs:
        lines.append("Nenhuma mensagem de teste no .diag.")
        return "\n".join(lines) + "\n"

    origin = min(run.start for run in runs)
    open_runs = sorted((run for run in runs if run.outcome is None), key=lambda run: run.start)
    if open_runs:
        lines += ["**Testes em andamento no fim do .diag:**", "", "| Início | Aberto há | Teste |", "|---|---|---|"]
        lines += [f"| +{_minutes((run.start - origin).total_seconds())} | {_minutes(run.seconds)} "
                  f"| `{run.class_name}.{run.method}` |" for run in open_runs]
    else:
        lines.append("Nenhum teste em andamento no fim do .diag.")
    lines += ["", "| Classe | Parede | Testes | Falhas | Abertos |", "|---|---|---|---|---|"]
    lines += [f"| `{c.name}` | {_minutes(c.wall_seconds)} | {c.tests} | {c.failed} | {c.open} |"
              for c in summarize_classes(runs)[:top]]
    return "\n".join(lines) + "\n"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Resumo de tempo por classe lido do .diag do MTP.")
    parser.add_argument("--top", type=int, default=DEFAULT_TOP)
    parser.add_argument("files", nargs="+", type=Path)
    args = parser.parse_args(argv)
    for path in args.files:
        with path.open(encoding="utf-8", errors="replace") as lines:
            runs = parse_lines(lines)
        sys.stdout.write(render_markdown(path.name.split("_", 1)[0], runs, top=args.top) + "\n")
    return 0


def _parse_stamp(match: re.Match[str]) -> datetime:
    # O .diag traz 7 casas de fração; o fromisoformat aceita até 6.
    whole, fraction = match[1].split(".")
    return datetime.fromisoformat(f"{whole}.{fraction[:6]}{match[2]}")


def _class_name(namespace: str, type_name: str) -> str:
    """Sem o nome do assembly: `Duora.IntegrationTests.Identity` + `X` vira `Identity.X`."""
    parts = namespace.split(".", 2)
    return f"{parts[2]}.{type_name}" if len(parts) == 3 else f"{namespace}.{type_name}"


def _minutes(seconds: float) -> str:
    return f"{seconds / 60:.1f} min".replace(".", ",")


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
