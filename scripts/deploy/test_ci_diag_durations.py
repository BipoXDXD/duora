"""Testes do resumo de tempo por classe lido do .diag do Microsoft.Testing.Platform (scripts/ci/diag_durations.py).

O .diag em Trace grava cada mudança de estado de teste como uma linha `TestNodeUpdateMessage { ... }` logo depois
da linha com carimbo de hora do barramento de mensagens. As fixtures abaixo reproduzem esse formato, encurtado.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
_SPEC = importlib.util.spec_from_file_location("diag_durations", ROOT / "scripts/ci/diag_durations.py")
assert _SPEC is not None and _SPEC.loader is not None
diag = importlib.util.module_from_spec(_SPEC)
sys.modules["diag_durations"] = diag
_SPEC.loader.exec_module(diag)


def stamp(time: str) -> str:
    return f"2026-11-12T{time}+00:00 Microsoft.Testing.Platform.Messages.AsynchronousMessageBus TRACE The producer"


def update(uid: str, namespace: str, type_name: str, method: str, state: str) -> str:
    return (
        "TestNodeUpdateMessage { DisplayName = TestNode update, Properties = [], ParentTestNodeUid = <null>, "
        f"TestNode = TestNode {{ Uid = TestNodeUid {{ Value = {uid} }}, DisplayName = {namespace}.{type_name}.{method}, "
        f"Properties = [ TestMethodIdentifierProperty {{ AssemblyFullName = Duora.IntegrationTests, "
        f"Namespace = {namespace}, TypeName = {type_name}, MethodName = {method}, MethodArity = 0 }}, "
        f"{state}TestNodeStateProperty {{ Explanation =  }} ] }} }}"
    )


IDENTITY = "Duora.IntegrationTests.Identity"
DATABASE = "Duora.IntegrationTests.Database"


class ParseTests(unittest.TestCase):
    def test_a_passed_test_has_start_end_and_its_class(self):
        runs = diag.parse_lines([
            stamp("00:31:20.0000000"),
            update("a", IDENTITY, "LoginTests", "Works", "InProgress"),
            stamp("00:31:22.5000000"),
            update("a", IDENTITY, "LoginTests", "Works", "Passed"),
        ])

        self.assertEqual(1, len(runs))
        run = runs[0]
        self.assertEqual("Identity.LoginTests", run.class_name)
        self.assertEqual("Works", run.method)
        self.assertEqual("Passed", run.outcome)
        self.assertEqual(2.5, run.seconds)

    def test_a_test_without_terminal_state_stays_open_until_the_last_stamp(self):
        runs = diag.parse_lines([
            stamp("00:31:20.0000000"),
            update("a", DATABASE, "PostgresMemoryProfileTests", "Fits", "InProgress"),
            stamp("00:41:20.0000000"),
            "linha sem mensagem de teste",
        ])

        self.assertEqual(1, len(runs))
        self.assertIsNone(runs[0].outcome)
        self.assertEqual(600.0, runs[0].seconds)

    def test_failed_states_are_kept_as_outcomes(self):
        lines: list[str] = []
        for uid, state in (("f", "Failed"), ("e", "Error"), ("t", "Timeout"), ("c", "Cancelled"), ("s", "Skipped")):
            lines += [stamp("00:31:20.0000000"), update(uid, IDENTITY, "X", uid, "InProgress"),
                      stamp("00:31:21.0000000"), update(uid, IDENTITY, "X", uid, state)]

        outcomes = sorted(run.outcome or "" for run in diag.parse_lines(lines))

        self.assertEqual(["Cancelled", "Error", "Failed", "Skipped", "Timeout"], outcomes)

    def test_an_update_before_any_stamp_is_ignored(self):
        self.assertEqual([], diag.parse_lines([update("a", IDENTITY, "X", "Y", "InProgress")]))

    def test_no_test_messages_gives_no_runs(self):
        self.assertEqual([], diag.parse_lines([]))


class SummaryTests(unittest.TestCase):
    def runs(self) -> list:
        return diag.parse_lines([
            stamp("00:00:00.0000000"),
            update("a1", IDENTITY, "RegistrationTests", "A", "InProgress"),
            update("a2", IDENTITY, "RegistrationTests", "B", "InProgress"),
            update("b1", DATABASE, "QuickTests", "C", "InProgress"),
            stamp("00:00:01.0000000"),
            update("b1", DATABASE, "QuickTests", "C", "Passed"),
            stamp("00:00:30.0000000"),
            update("a1", IDENTITY, "RegistrationTests", "A", "Passed"),
            stamp("00:01:00.0000000"),
            update("a2", IDENTITY, "RegistrationTests", "B", "Failed"),
            stamp("00:05:00.0000000"),
            update("o1", DATABASE, "StuckTests", "Hangs", "InProgress"),
            stamp("00:20:00.0000000"),
            "fim",
        ])

    def test_classes_are_ranked_by_wall_time_from_first_start_to_last_end(self):
        classes = diag.summarize_classes(self.runs())

        self.assertEqual(["Database.StuckTests", "Identity.RegistrationTests", "Database.QuickTests"],
                         [c.name for c in classes])
        self.assertEqual(900.0, classes[0].wall_seconds)
        self.assertEqual(60.0, classes[1].wall_seconds)
        self.assertEqual(2, classes[1].tests)
        self.assertEqual(1, classes[1].failed)
        self.assertEqual(1, classes[0].open)

    def test_markdown_lists_open_tests_first_with_start_offset(self):
        text = diag.render_markdown("Duora.IntegrationTests", self.runs(), top=2)

        open_section, classes_section = text.split("| Classe", 1)
        self.assertIn("Database.StuckTests.Hangs", open_section)
        self.assertIn("+5,0 min", open_section)
        self.assertIn("15,0 min", open_section)
        self.assertIn("Database.StuckTests", classes_section)
        self.assertIn("Identity.RegistrationTests", classes_section)
        self.assertNotIn("Database.QuickTests", classes_section)

    def test_markdown_without_open_tests_says_so(self):
        runs = [run for run in self.runs() if run.outcome is not None]

        self.assertIn("Nenhum teste em andamento", diag.render_markdown("m", runs, top=5))

    def test_markdown_of_an_empty_diag_is_explicit(self):
        self.assertIn("Nenhuma mensagem de teste", diag.render_markdown("m", [], top=5))


if __name__ == "__main__":
    unittest.main()
