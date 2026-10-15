#!/usr/bin/env python3
"""Garante que toda entrada do .trivyignore tem justificativa e validade (docs/security/supply-chain.md).

Uso: check_trivyignore.py <.trivyignore>

Cada ID ignorado precisa de um comentário não vazio na linha imediatamente acima (um comentário por entrada)
e de `exp:AAAA-MM-DD`. Depois da validade o Trivy volta a reportar o achado, o que força nova triagem.
"""

import datetime
import sys


def find_problems(text):
    problems = []
    previous_is_justification = False
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if line.startswith("#"):
            previous_is_justification = bool(line.lstrip("#").strip())
            continue
        if not line:
            previous_is_justification = False
            continue
        finding_id, *options = line.split()
        if not previous_is_justification:
            problems.append(f"linha {number}: {finding_id} sem comentário de justificativa na linha de cima")
        if not any(_is_valid_expiry(option) for option in options):
            problems.append(f"linha {number}: {finding_id} sem validade exp:AAAA-MM-DD")
        previous_is_justification = False
    return problems


def _is_valid_expiry(option):
    if not option.startswith("exp:"):
        return False
    try:
        datetime.date.fromisoformat(option.removeprefix("exp:"))
    except ValueError:
        return False
    return True


def main(argv):
    if len(argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    with open(argv[1], encoding="utf-8") as f:
        problems = find_problems(f.read())
    for problem in problems:
        print(f"{argv[1]}: {problem}", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
