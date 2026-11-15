"""Confere que os shards do backend cobrem a suíte inteira, sem perda nem repetição (#564).

Cada shard grava a saída de `dotnet test --list-tests` com o mesmo alvo e filtro com que roda; o shard "resto" grava
também a listagem da solução inteira, sem filtro. Aqui a soma das listagens dos shards (como multiconjunto, porque um
[Theory] pode repetir o nome exibido) tem de ser igual à listagem inteira. Compara listagem com listagem, e não com o
total executado: um [Theory] cujos dados não são pré-enumerados lista um caso e executa vários.

Uso: python3 scripts/ci/shard_partition.py --all ALL.txt SHARD.txt...
Sai com 1 quando falta caso, quando um caso cai em dois shards ou quando a listagem inteira vem vazia.
"""
from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
import re
import sys
from typing import Callable, Iterable, Sequence

ANSI = re.compile(r"\x1b\[[0-9;]*m")
# Na listagem, cada caso vem numa linha com exatamente dois espaços de recuo; cabeçalhos não têm recuo.
TEST_LINE = re.compile(r"^  (?P<name>\S.*?)\s*$")
SUMMARY_LINE = re.compile(r"^\S+: \d+$")
REPORT_LIMIT = 50
DESCRIPTION = "Confere que os shards do backend cobrem a suíte inteira, sem perda nem repetição (#564)."


@dataclass(frozen=True)
class Partition:
    missing: list[str]
    repeated: list[str]


def parse_listing(text: str) -> list[str]:
    names = []
    for line in ANSI.sub("", text).splitlines():
        match = TEST_LINE.match(line)
        if match and not SUMMARY_LINE.match(match["name"]):
            names.append(match["name"])
    return names


def compare(whole: Iterable[str], shards: Iterable[Iterable[str]]) -> Partition:
    expected = Counter(whole)
    covered: Counter[str] = Counter()
    for shard in shards:
        covered.update(shard)
    return Partition(missing=sorted((expected - covered).elements()), repeated=sorted((covered - expected).elements()))


def main(argv: Sequence[str] | None = None, write: Callable[[str], None] = print) -> int:
    parser = argparse.ArgumentParser(description=DESCRIPTION)
    parser.add_argument("--all", required=True, type=Path, help="listagem da solução inteira, sem filtro")
    parser.add_argument("shards", nargs="*", type=Path, help="listagem de cada shard")
    args = parser.parse_args(argv)

    missing_files = [str(path) for path in (args.all, *args.shards) if not path.is_file()]
    if missing_files:
        write(f"::error::Listagem ausente: {', '.join(missing_files)}; algum shard não gravou a sua.")
        return 1
    whole = parse_listing(args.all.read_text(encoding="utf-8"))
    if not whole:
        write(f"::error::A listagem inteira ({args.all}) não tem nenhum caso; a conferência dos shards não vale.")
        return 1
    if not args.shards:
        write("::error::Nenhuma listagem de shard foi recebida.")
        return 1

    result = compare(whole, (parse_listing(path.read_text(encoding="utf-8")) for path in args.shards))
    write("### Partição dos shards do backend (#564)")
    write(f"Casos listados na solução: {len(whole)}; listagens de shard: {len(args.shards)}.")
    if not result.missing and not result.repeated:
        write("Cada caso cai em exatamente um shard.")
        return 0
    _report(write, "Em nenhum shard", result.missing)
    _report(write, "Em mais de um shard", result.repeated)
    write("::error::Os filtros dos shards não cobrem a suíte exatamente uma vez; ajuste a matrix de backend-tests.")
    return 1


def _report(write: Callable[[str], None], title: str, names: list[str]) -> None:
    if not names:
        return
    write(f"**{title}: {len(names)}**")
    for name in names[:REPORT_LIMIT]:
        write(f"- `{name}`")
    if len(names) > REPORT_LIMIT:
        write(f"- … e mais {len(names) - REPORT_LIMIT}")


if __name__ == "__main__":
    sys.exit(main())
