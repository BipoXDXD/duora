"""Executa a partição de unittest de deploy e confere os recibos dos quatro executores."""
from __future__ import annotations

import argparse
from collections.abc import Iterator, Sequence
import json
import os
from pathlib import Path
import sys
from typing import Any, TextIO
import unittest

TESTS = Path(__file__).resolve().parents[1] / "deploy"
BROWSER_CASE = "test_caddy_security_headers.CaddySecurityHeadersTests.test_browser_enforces_the_policies"
BROWSERHOST_CASES = {
    "language-matching.spec.ts: real PostgreSQL: no common language, either participant declines, "
    "then localized completion without microphone",
    "unlikely-routes.spec.ts: real PostgreSQL Routes: bilingual private clues, alternating choices, "
    "revision and completion without microphone",
}
EXECUTORS = {"ops": "ops", "postgres-amd64": "postgres", "postgres-arm64": "postgres", "browser": "browser"}
PATTERNS = {"ops": "test*.py", "postgres": "test_db_privileges.py", "browser": "test_caddy_security_headers.py"}


def executor_for(name: str) -> str:
    if name.startswith("test_db_privileges."):
        return "postgres"
    if name == BROWSER_CASE:
        return "browser"
    return "ops"


def iter_cases(suite: unittest.TestSuite) -> Iterator[unittest.TestCase]:
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            yield from iter_cases(item)
        else:
            yield item


class SuccessfulCases(unittest.TextTestResult):
    def __init__(self, stream, descriptions, verbosity):
        super().__init__(stream, descriptions, verbosity)
        self.passed: list[str] = []

    def addSuccess(self, test):
        super().addSuccess(test)
        self.passed.append(test.id())


def run_cases(cases: Sequence[unittest.TestCase], stream: TextIO = sys.stderr) -> list[str]:
    selected = sorted(case.id() for case in cases)
    if not selected:
        raise ValueError("nenhum caso selecionado")
    if len(selected) != len(set(selected)):
        raise ValueError("casos duplicados na seleção")
    runner = unittest.TextTestRunner(stream=stream, verbosity=2, resultclass=SuccessfulCases)
    result = runner.run(unittest.TestSuite(cases))
    assert isinstance(result, SuccessfulCases)
    passed = sorted(result.passed)
    if not result.wasSuccessful() or passed != selected:
        missing = sorted(set(selected) - set(passed))
        raise ValueError(f"seleção sem aprovação completa: {', '.join(missing[:20])}")
    return passed


def run(executor: str, receipt: Path | None, identity: dict[str, str | None]) -> None:
    group = EXECUTORS[executor]
    loader = unittest.TestLoader()
    suite = loader.discover(str(TESTS), pattern=PATTERNS[group])
    if loader.errors:
        raise ValueError("falha na descoberta: " + "\n".join(map(str, loader.errors)))
    cases = list(iter_cases(suite))
    selected = [case for case in cases if executor_for(case.id()) == group]
    passed = run_cases(selected)
    if receipt is None:
        return

    data = {**identity, "executor": executor, "passed": passed}
    if executor == "ops":
        data["plan"] = {target: sorted(case.id() for case in cases if executor_for(case.id()) == target)
                        for target in PATTERNS}
    write_receipt(receipt, data)


def write_receipt(path: Path, data: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data) + "\n", encoding="utf-8")


def read_receipt(directory: Path, executor: str, identity: dict[str, str | None]) -> dict[str, Any]:
    report = json.loads((directory / f"{executor}.json").read_text(encoding="utf-8"))
    if any(report[key] != value for key, value in identity.items()):
        raise ValueError(f"{executor}: recibo de outro SHA ou run")
    if report["executor"] != executor:
        raise ValueError(f"{executor}: recibo de outro executor")
    return report


def verify(directory: Path, identity: dict[str, str | None]) -> int:
    reports = {name: read_receipt(directory, name, identity) for name in EXECUTORS}
    plan = reports["ops"]["plan"]
    if set(plan) != set(PATTERNS) or plan["browser"] != [BROWSER_CASE]:
        raise ValueError("plano deve conter ops, PostgreSQL e a sonda Chromium")
    for group, names in plan.items():
        if not names or len(set(names)) != len(names) or any(executor_for(name) != group for name in names):
            raise ValueError(f"seleção inválida no plano de {group}")
    for executor, group in EXECUTORS.items():
        if sorted(reports[executor]["passed"]) != sorted(plan[group]):
            raise ValueError(f"{executor}: casos aprovados divergem da seleção")
    return sum(len(names) for names in plan.values())


def iter_specs(suites: list[dict[str, Any]]) -> Iterator[dict[str, Any]]:
    for suite in suites:
        yield from suite["specs"]
        yield from iter_specs(suite.get("suites", []))


def check_browserhost_cases(passed: list[str]) -> None:
    unique = set(passed)
    if len(passed) != len(unique) or not BROWSERHOST_CASES.issubset(unique):
        raise ValueError("BrowserHost: casos esperados ausentes ou repetidos")


def browserhost_passed(path: Path) -> list[str]:
    report = json.loads(path.read_text(encoding="utf-8"))
    stats = report["stats"]
    if report["errors"] or any(stats[outcome] for outcome in ("skipped", "flaky", "unexpected")):
        raise ValueError("BrowserHost: relatório contém erro, skip ou retry")
    passed = []
    for spec in iter_specs(report["suites"]):
        tests = spec["tests"]
        if not tests or any(test["projectName"] != "real-chromium" or test["expectedStatus"] != "passed"
                            or test["status"] != "expected" or len(test["results"]) != 1
                            or test["results"][0]["status"] != "passed" for test in tests):
            raise ValueError(f"BrowserHost: caso sem execução aprovada em {spec['file']}")
        passed.extend(f"{spec['file']}: {spec['title']}" for _ in tests)
    check_browserhost_cases(passed)
    if len(passed) != stats["expected"]:
        raise ValueError("BrowserHost: total aprovado diverge dos casos")
    return sorted(passed)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    runner = commands.add_parser("run")
    runner.add_argument("executor", choices=EXECUTORS)
    runner.add_argument("--receipt", type=Path)
    verifier = commands.add_parser("verify")
    verifier.add_argument("directory", type=Path)
    browserhost = commands.add_parser("record-browserhost")
    browserhost.add_argument("report", type=Path)
    browserhost.add_argument("receipt", type=Path)
    args = parser.parse_args(argv)
    identity = {"sha": os.environ.get("GITHUB_SHA"), "run": os.environ.get("GITHUB_RUN_ID")}
    try:
        if args.command == "run":
            run(args.executor, args.receipt, identity)
        elif args.command == "record-browserhost":
            passed = browserhost_passed(args.report)
            write_receipt(args.receipt, {**identity, "executor": "browserhost", "passed": passed})
        else:
            count = verify(args.directory, identity)
            browserhost_passes = read_receipt(args.directory, "browserhost", identity)["passed"]
            check_browserhost_cases(browserhost_passes)
            print(f"{count} casos de deploy aprovados nos executores previstos, sem skips.")
            print(f"{len(browserhost_passes)} casos de BrowserHost aprovados no real-chromium, sem skips.")
    except (OSError, ValueError, TypeError, KeyError) as error:
        print(f"Partição dos testes de deploy: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
