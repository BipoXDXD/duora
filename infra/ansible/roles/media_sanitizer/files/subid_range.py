#!/usr/bin/python3
"""Decide whether the sanitizer account already owns its subordinate ID range.

Prints "present" or "absent" for the requested range; exits 2 without changing anything when
the account has another range or when the range overlaps an entry of another account (a shared
range would map one account's container root onto files of the other).
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path

# Rootless Docker needs a full 16-bit range: container UID 1654 and the image's root map inside it.
MINIMUM_COUNT = 65536


class SubidConflict(ValueError):
    """The requested range cannot be granted without sharing IDs or replacing a reviewed range."""


@dataclass(frozen=True)
class SubidRange:
    owner: str
    start: int
    count: int

    @property
    def end(self) -> int:
        return self.start + self.count

    def overlaps(self, other: SubidRange) -> bool:
        return self.start < other.end and other.start < self.end


def parse(text: str) -> list[SubidRange]:
    entries = []
    for number, line in enumerate(text.splitlines(), start=1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        fields = line.split(":")
        if len(fields) != 3 or not fields[0] or not fields[1].isdigit() or not fields[2].isdigit():
            raise SubidConflict(f"malformed subordinate ID line {number}")
        entries.append(SubidRange(fields[0], int(fields[1]), int(fields[2])))
    return entries


def plan(entries: list[SubidRange], wanted: SubidRange) -> str:
    if wanted.count < MINIMUM_COUNT or wanted.start <= 0:
        raise SubidConflict("requested range is smaller than a 16-bit user namespace")
    own = [entry for entry in entries if entry.owner == wanted.owner]
    if own:
        if own == [wanted]:
            return "present"
        raise SubidConflict(f"{wanted.owner} already has a different subordinate range")
    for entry in entries:
        if entry.overlaps(wanted):
            raise SubidConflict(f"requested range overlaps the range of {entry.owner}")
    return "absent"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--user", required=True)
    parser.add_argument("--start", type=int, required=True)
    parser.add_argument("--count", type=int, required=True)
    parser.add_argument("files", nargs="+", type=Path)
    args = parser.parse_args(argv)
    wanted = SubidRange(args.user, args.start, args.count)
    try:
        decisions = {plan(parse(path.read_text()), wanted) for path in args.files}
    except SubidConflict as error:
        print(error, file=sys.stderr)
        return 2
    if len(decisions) != 1:
        print("subuid and subgid disagree for the account", file=sys.stderr)
        return 2
    print(decisions.pop())
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
