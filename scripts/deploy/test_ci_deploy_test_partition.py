"""Exercita a seleção e os recibos de unittest com casos sintéticos, sem Docker ou navegador."""
from __future__ import annotations

import importlib.util
from io import StringIO
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

ROOT = Path(__file__).resolve().parents[2]
_SPEC = importlib.util.spec_from_file_location("deploy_test_partition", ROOT / "scripts/ci/deploy_test_partition.py")
assert _SPEC is not None and _SPEC.loader is not None
partition = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(partition)

OPS = "test_example.LocalTests.test_example"
POSTGRES = "test_db_privileges.ExampleTests.test_real_image"
BROWSER = "test_caddy_security_headers.CaddySecurityHeadersTests.test_browser_enforces_the_policies"


class TestPartition(unittest.TestCase):
    def test_routes_only_the_native_module_and_the_named_browser_case(self):
        self.assertEqual("ops", partition.executor_for(OPS))
        self.assertEqual("postgres", partition.executor_for(POSTGRES))
        self.assertEqual("browser", partition.executor_for(BROWSER))
        self.assertEqual("ops", partition.executor_for("test_caddy_security_headers.CaddySecurityHeadersTests.test_csp"))

    def test_runner_requires_a_successful_result_for_every_selected_case(self):
        class Synthetic(unittest.TestCase):
            def ok(self):
                pass

            def skipped(self):
                self.skipTest("fixture absent")

            @unittest.expectedFailure
            def expected_failure(self):
                self.fail("failed")

        case = Synthetic("ok")
        self.assertEqual([case.id()], partition.run_cases([case], stream=StringIO()))
        for method in ("skipped", "expected_failure"):
            with self.subTest(method=method), self.assertRaisesRegex(ValueError, "sem aprovação"):
                partition.run_cases([Synthetic(method)], stream=StringIO())
        with self.assertRaisesRegex(ValueError, "nenhum caso"):
            partition.run_cases([], stream=StringIO())

    def test_verifier_requires_exact_case_ids_on_both_native_architectures(self):
        with TemporaryDirectory() as directory:
            path = Path(directory)
            identity = {"sha": "a" * 40, "run": "170"}
            expected = {"ops": [OPS], "postgres": [POSTGRES], "browser": [BROWSER]}

            def write(name: str, tests: list[str], **extra):
                data = {**identity, "executor": name, "passed": tests, **extra}
                (path / f"{name}.json").write_text(json.dumps(data), encoding="utf-8")

            write("ops", [OPS], plan=expected)
            write("browser", [BROWSER])
            write("postgres-amd64", [POSTGRES])
            with self.assertRaisesRegex(OSError, "postgres-arm64"):
                partition.verify(path, identity)
            write("postgres-arm64", [POSTGRES])
            self.assertEqual(3, partition.verify(path, identity))

            for incorrect in ([], [POSTGRES, POSTGRES], [POSTGRES + "_other"]):
                with self.subTest(incorrect=incorrect):
                    write("postgres-arm64", incorrect)
                    with self.assertRaisesRegex(ValueError, "postgres-arm64"):
                        partition.verify(path, identity)

            write("postgres-arm64", [POSTGRES], sha="b" * 40)
            with self.assertRaisesRegex(ValueError, "outro SHA ou run"):
                partition.verify(path, identity)
            write("postgres-arm64", [POSTGRES], run="169")
            with self.assertRaisesRegex(ValueError, "outro SHA ou run"):
                partition.verify(path, identity)

    def test_verifier_requires_both_browserhost_files_without_skips(self):
        with TemporaryDirectory() as directory:
            path = Path(directory) / "browserhost.json"
            results = [{"status": "passed"}]
            test = {"projectName": "real-chromium", "expectedStatus": "passed",
                    "status": "expected", "results": results}
            expected = sorted(partition.BROWSERHOST_CASES)
            suites = [{"specs": [{"file": filename, "title": title, "tests": [test]}]}
                      for filename, title in (case.split(": ", 1) for case in expected)]
            stats = {"skipped": 0, "flaky": 0, "unexpected": 0, "expected": 2}

            def write(suite_reports, **overrides):
                path.write_text(json.dumps({"errors": [], "stats": {**stats, **overrides},
                                            "suites": suite_reports}), encoding="utf-8")

            write(suites)
            self.assertEqual(expected, partition.browserhost_passed(path))
            write(suites[:1], expected=1)
            with self.assertRaisesRegex(ValueError, "ausentes ou repetidos"):
                partition.browserhost_passed(path)
            write(suites + suites[:1], expected=3)
            with self.assertRaisesRegex(ValueError, "ausentes ou repetidos"):
                partition.browserhost_passed(path)
            write(suites, skipped=1)
            with self.assertRaisesRegex(ValueError, "skip"):
                partition.browserhost_passed(path)
            results.clear()
            write(suites)
            with self.assertRaisesRegex(ValueError, "sem execução aprovada"):
                partition.browserhost_passed(path)
