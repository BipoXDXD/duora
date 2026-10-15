#!/usr/bin/env python3
"""Confere as licenças de SBOMs CycloneDX contra a allowlist de license-policy.json (spec 2.3 e 17.1).

Uso: check_licenses.py <license-policy.json> <sbom.cdx.json> [<sbom.cdx.json> ...]

Sai com 1 se algum componente tiver licença fora da allowlist ou nenhuma licença declarada, salvo exceção
escrita na política. Imprime o inventário de licenças em Markdown (o CI o copia para o resumo do job).
Só usa a biblioteca padrão, para rodar no Python do runner sem instalar nada.
"""

import fnmatch
import json
import re
import sys
from collections import Counter
from dataclasses import dataclass, field

MISSING_LICENSE_WAIVED = "(sem licença, exceção)"


class PolicyError(ValueError):
    """Política de licenças inválida."""


@dataclass(frozen=True)
class LicenseException:
    package: str
    license: str
    reason: str


@dataclass(frozen=True)
class MissingLicenseException:
    pattern: str
    reason: str


@dataclass(frozen=True)
class Policy:
    allowed: frozenset
    exceptions: tuple
    missing_license_allowed: tuple

    @classmethod
    def from_dict(cls, data):
        allowed = frozenset(data.get("allowed", []))
        if not allowed:
            raise PolicyError("a allowlist 'allowed' está vazia")
        exceptions = tuple(
            LicenseException(e["package"], e["license"], _require_reason(e, e["package"]))
            for e in data.get("exceptions", [])
        )
        missing = tuple(
            MissingLicenseException(m["pattern"], _require_reason(m, m["pattern"]))
            for m in data.get("missingLicenseAllowed", [])
        )
        return cls(allowed, exceptions, missing)


def _require_reason(entry, label):
    reason = entry.get("reason", "").strip()
    if not reason:
        raise PolicyError(f"exceção sem justificativa: {label}")
    return reason


@dataclass
class CheckResult:
    violations: list = field(default_factory=list)
    unused_exceptions: list = field(default_factory=list)
    inventory: dict = field(default_factory=dict)


def check_components(components, policy):
    result = CheckResult()
    inventory = Counter()
    used = set()
    for comp in components:
        if comp.get("type") != "library":
            continue
        name = _package_name(comp)
        label = f"{name}@{comp.get('version', '?')}"
        licenses = [_license_text(entry) for entry in comp.get("licenses") or []]
        if not licenses:
            waiver = _find_missing_waiver(name, policy)
            if waiver is None:
                result.violations.append(f"{label}: sem licença declarada")
            else:
                used.add(waiver)
                inventory[MISSING_LICENSE_WAIVED] += 1
            continue
        for text in licenses:
            inventory[text] += 1
            if is_allowed(text, policy.allowed):
                continue
            exception = _find_exception(name, text, policy)
            if exception is None:
                result.violations.append(f"{label}: licença fora da allowlist: {text}")
            else:
                used.add(exception)
    result.inventory = dict(inventory)
    result.unused_exceptions = [
        f"exceção sem uso: {e.package} ({e.license})" for e in policy.exceptions if e not in used
    ] + [f"exceção sem uso: {m.pattern} (sem licença)" for m in policy.missing_license_allowed if m not in used]
    return result


def _package_name(comp):
    group = comp.get("group")
    return f"{group}/{comp['name']}" if group else comp["name"]


def _license_text(entry):
    if "expression" in entry:
        return entry["expression"]
    lic = entry.get("license", {})
    return lic.get("id") or lic.get("name") or ""


def _find_exception(name, text, policy):
    return next(
        (e for e in policy.exceptions if fnmatch.fnmatchcase(name, e.package) and e.license == text), None
    )


def _find_missing_waiver(name, policy):
    return next((m for m in policy.missing_license_allowed if fnmatch.fnmatchcase(name, m.pattern)), None)


# --- Expressões SPDX: OR < AND < WITH, parênteses. Um "id WITH exceção" só passa se estiver na allowlist inteiro.

_TOKEN = re.compile(r"\s*(\(|\)|[^\s()]+)")


def is_allowed(expression, allowed):
    tokens = _TOKEN.findall(expression)
    if not tokens:
        return False
    parser = _ExpressionParser(tokens, allowed)
    try:
        verdict = parser.parse_or()
    except _MalformedExpression:
        return False
    return verdict and parser.at_end()


class _MalformedExpression(Exception):
    pass


class _ExpressionParser:
    def __init__(self, tokens, allowed):
        self._tokens = tokens
        self._pos = 0
        self._allowed = allowed

    def at_end(self):
        return self._pos == len(self._tokens)

    def parse_or(self):
        verdict = self.parse_and()
        while self._peek_operator("OR"):
            self._pos += 1
            right = self.parse_and()
            verdict = verdict or right
        return verdict

    def parse_and(self):
        verdict = self.parse_term()
        while self._peek_operator("AND"):
            self._pos += 1
            right = self.parse_term()
            verdict = verdict and right
        return verdict

    def parse_term(self):
        token = self._next()
        if token == "(":
            verdict = self.parse_or()
            if self._next() != ")":
                raise _MalformedExpression()
            return verdict
        if token == ")" or token.upper() in ("OR", "AND", "WITH"):
            raise _MalformedExpression()
        if self._peek_operator("WITH"):
            self._pos += 1
            return f"{token} WITH {self._next()}" in self._allowed
        return token in self._allowed

    def _peek_operator(self, operator):
        return self._pos < len(self._tokens) and self._tokens[self._pos].upper() == operator

    def _next(self):
        if self._pos >= len(self._tokens):
            raise _MalformedExpression()
        token = self._tokens[self._pos]
        self._pos += 1
        return token


def main(argv):
    if len(argv) < 3:
        print(__doc__, file=sys.stderr)
        return 2
    with open(argv[1], encoding="utf-8") as f:
        policy = Policy.from_dict(json.load(f))
    components = []
    for path in argv[2:]:
        with open(path, encoding="utf-8") as f:
            components.extend(json.load(f).get("components", []))
    result = check_components(components, policy)
    _print_report(result, argv[2:])
    return 1 if result.violations else 0


def _print_report(result, sbom_paths):
    print("### Inventário de licenças\n")
    print(f"SBOMs: {', '.join(sbom_paths)}\n")
    print("| Licença | Componentes |\n|---|---:|")
    for text, count in sorted(result.inventory.items(), key=lambda kv: (-kv[1], kv[0])):
        print(f"| `{text}` | {count} |")
    for note in result.unused_exceptions:
        print(f"\n> Aviso: {note}. Remova-a de license-policy.json se a dependência saiu.")
    if result.violations:
        print("\n### Licenças reprovadas\n")
        for violation in sorted(set(result.violations)):
            print(f"- {violation}")


if __name__ == "__main__":
    sys.exit(main(sys.argv))
