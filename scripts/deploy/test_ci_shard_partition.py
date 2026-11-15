"""Conferência de que os shards do backend cobrem a suíte inteira, sem perda nem repetição (#564).

As fixtures reproduzem a saída de `dotnet test --list-tests` do Microsoft.Testing.Platform: cabeçalhos sem recuo e um
caso por linha com dois espaços de recuo, às vezes com cor ANSI. A conferência compara listagens (com os filtros reais
de cada shard) e não contagens executadas, porque um [Theory] pode listar um caso e executar vários.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github/workflows/ci.yml"
_SPEC = importlib.util.spec_from_file_location("shard_partition", ROOT / "scripts/ci/shard_partition.py")
assert _SPEC is not None and _SPEC.loader is not None
partition = importlib.util.module_from_spec(_SPEC)
sys.modules["shard_partition"] = partition
_SPEC.loader.exec_module(partition)

ASSEMBLY = "/home/runner/work/duora/duora/backend/tests/Duora.UnitTests/bin/Debug/net10.0/Duora.UnitTests.dll"


def listing(*names: str) -> str:
    lines = [f"Discovering tests from {ASSEMBLY} (net10.0|x64)", "", f"Discovered {len(names)} tests in assembly - {ASSEMBLY}"]
    lines += [f"  {name}" for name in names]
    return "\n".join(lines) + "\n"


class ParseListingTests(unittest.TestCase):
    def test_reads_only_the_indented_test_lines(self):
        names = partition.parse_listing(listing("Duora.UnitTests.A.One", "Duora.UnitTests.B.Two"))
        self.assertEqual(["Duora.UnitTests.A.One", "Duora.UnitTests.B.Two"], names)

    def test_strips_ansi_colour_codes(self):
        names = partition.parse_listing("\x1b[32m  Duora.UnitTests.A.One\x1b[m\n")
        self.assertEqual(["Duora.UnitTests.A.One"], names)

    def test_keeps_repeated_display_names(self):
        # Um [Theory] com argumentos truncados ("···") lista o mesmo nome mais de uma vez.
        names = partition.parse_listing(listing("Duora.T.M(args: [···])", "Duora.T.M(args: [···])"))
        self.assertEqual(2, len(names))

    def test_empty_listing_gives_no_names(self):
        self.assertEqual([], partition.parse_listing(""))

    def test_ignores_deeper_indentation_and_summary_lines(self):
        text = "  total: 3\n    detalhe recuado\nResumo\n  Duora.UnitTests.A.One\n"
        self.assertEqual(["Duora.UnitTests.A.One"], partition.parse_listing(text))


class CompareTests(unittest.TestCase):
    def test_exact_partition_has_no_difference(self):
        result = partition.compare(["a", "b", "c"], [["a"], ["b", "c"]])
        self.assertEqual(([], []), (result.missing, result.repeated))

    def test_a_case_no_shard_lists_is_missing(self):
        result = partition.compare(["a", "b", "c"], [["a"], ["b"]])
        self.assertEqual((["c"], []), (result.missing, result.repeated))

    def test_a_case_two_shards_list_is_repeated(self):
        result = partition.compare(["a", "b"], [["a", "b"], ["b"]])
        self.assertEqual(([], ["b"]), (result.missing, result.repeated))

    def test_repeated_names_count_as_a_multiset(self):
        # O nome aparece duas vezes na suíte; um shard só com uma cópia deixa a outra de fora.
        result = partition.compare(["m", "m"], [["m"]])
        self.assertEqual((["m"], []), (result.missing, result.repeated))

    def test_missing_and_repeated_together(self):
        result = partition.compare(["a", "b"], [["a"], ["a"]])
        self.assertEqual((["b"], ["a"]), (result.missing, result.repeated))


class MainTests(unittest.TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory()
        self.dir = Path(self._dir.name)

    def tearDown(self):
        self._dir.cleanup()

    def write(self, name: str, *names: str) -> str:
        path = self.dir / name
        path.write_text(listing(*names))
        return str(path)

    def run_main(self, *args: str) -> tuple[int, str]:
        output: list[str] = []
        code = partition.main(list(args), output.append)
        return code, "\n".join(output)

    def test_passes_and_reports_counts_when_the_shards_cover_everything(self):
        whole = self.write("all.txt", "a", "b", "c")
        code, out = self.run_main("--all", whole, self.write("shard-x.txt", "a"), self.write("shard-y.txt", "b", "c"))
        self.assertEqual(0, code)
        self.assertIn("3", out)

    def test_fails_and_names_the_missing_case(self):
        whole = self.write("all.txt", "a", "b")
        code, out = self.run_main("--all", whole, self.write("shard-x.txt", "a"))
        self.assertEqual(1, code)
        self.assertIn("b", out)

    def test_fails_and_names_the_repeated_case(self):
        whole = self.write("all.txt", "a")
        code, out = self.run_main("--all", whole, self.write("shard-x.txt", "a"), self.write("shard-y.txt", "a"))
        self.assertEqual(1, code)
        self.assertIn("a", out)

    def test_an_empty_full_listing_fails_instead_of_passing_zero_against_zero(self):
        whole = self.write("all.txt")
        code, _ = self.run_main("--all", whole, self.write("shard-x.txt"))
        self.assertEqual(1, code)

    def test_a_missing_full_listing_fails_with_a_message_instead_of_a_traceback(self):
        code, out = self.run_main("--all", str(self.dir / "all.txt"), self.write("shard-x.txt", "a"))
        self.assertEqual(1, code)
        self.assertIn("all.txt", out)

    def test_no_shard_files_fails(self):
        whole = self.write("all.txt", "a")
        code, _ = self.run_main("--all", whole)
        self.assertEqual(1, code)

    def test_long_differences_are_capped_in_the_report(self):
        names = [f"Duora.T.Case{index:03d}" for index in range(partition.REPORT_LIMIT + 5)]
        whole = self.write("all.txt", *names)
        code, out = self.run_main("--all", whole, self.write("shard-x.txt"))
        self.assertEqual(1, code)
        self.assertIn(names[0], out)
        self.assertNotIn(names[-1], out)
        self.assertIn(str(len(names)), out)


class WorkflowWiringTests(unittest.TestCase):
    """O ci.yml lista cada shard com os mesmos argumentos com que roda e o agregador confere a partição."""

    def setUp(self):
        self.jobs = yaml.safe_load(CI.read_text())["jobs"]
        self.shard_steps = self.jobs["backend-tests"]["steps"]

    def step(self, name: str) -> dict:
        return next(item for item in self.shard_steps if item.get("name") == name)

    def test_each_test_run_is_listed_with_the_same_target_and_filter(self):
        run = self.step("Testes")["run"]
        self.assertIn('dotnet test ${{ matrix.target }} --no-build --list-tests -- ${{ matrix.filter }} > "$lists/shard-${{ matrix.shard }}.txt"', run)
        self.assertIn("dotnet test ${{ matrix.target }} --no-build \\", run)
        self.assertLess(run.index("--list-tests"), run.index("--report-xunit-trx"))

    def test_the_media_corpus_run_appends_its_own_listing_with_the_same_filter(self):
        run = self.step("Testes do corpus de mídia no namespace Media")["run"]
        listed = run.split("--list-tests", 1)[1].split("dotnet test", 1)[0]
        executed = run.split("--report-xunit-trx", 1)[1]
        self.assertIn('>> "$lists/shard-${{ matrix.shard }}.txt"', listed)
        self.assertIn('--filter-namespace "Duora.IntegrationTests.Media*"', listed)
        self.assertIn('--filter-namespace "Duora.IntegrationTests.Media*"', executed)

    def test_the_full_listing_comes_from_the_whole_solution_without_filter(self):
        step = self.step("Listar a suíte inteira")
        self.assertEqual("${{ matrix.shard == 'resto' }}", step["if"])
        self.assertIn('dotnet test --solution backend/Duora.slnx --no-build --list-tests > "$lists/all.txt"', step["run"])

    def test_every_shard_uploads_its_listing(self):
        upload = self.step("Preservar a listagem do shard")
        self.assertNotIn("if", upload)
        self.assertNotIn("continue-on-error", upload)
        # Nome fixo por shard, sem a tentativa: no "Re-run failed jobs" o agregador precisa também das listagens dos
        # shards que passaram na tentativa anterior (mesmo commit), e o shard que roda de novo substitui a sua.
        self.assertEqual("backend-shard-list-${{ matrix.shard }}", upload["with"]["name"])
        self.assertIs(True, upload["with"]["overwrite"])
        self.assertEqual("error", upload["with"]["if-no-files-found"])

    def test_the_aggregate_fails_when_the_partition_differs(self):
        steps = self.jobs["backend"]["steps"]
        download = next(item for item in steps if str(item.get("uses", "")).startswith("actions/download-artifact@"))
        self.assertEqual("backend-shard-list-*", download["with"]["pattern"])
        self.assertTrue(download["with"]["merge-multiple"])
        check = next(item for item in steps if "shard_partition.py" in str(item.get("run", "")))
        self.assertNotIn("continue-on-error", check)
        self.assertIn('--all "$lists/all.txt" "$lists"/shard-*.txt', check["run"])
        # A conferência roda depois de exigir que todos os shards passaram.
        names = [item.get("name") for item in steps]
        self.assertLess(names.index("Todos os shards do backend passaram"), steps.index(check))

    def test_shard_ceiling_keeps_measured_headroom(self):
        # 12 runs da main em 13/11: 3 a 8 min por shard com build; 20 min dá mais de 2x de folga.
        self.assertEqual(20, self.jobs["backend-tests"]["timeout-minutes"])


if __name__ == "__main__":
    unittest.main()
