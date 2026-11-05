"""Pure parts of the real-Docker swap proof; the proof itself runs in postgres_swap_bench.py and continuity_lab.py."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
from typing import Any
import unittest
from unittest.mock import patch

import continuity_lab as lab
import postgres_swap as swap
import postgres_swap_bench as bench
import postgres_swap_driver as driver
import runtime_guard as guard

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("restore_drill_under_test", REPO / "scripts/backup/restore_drill.py")
assert spec is not None and spec.loader is not None
drill = importlib.util.module_from_spec(spec)
sys.modules["restore_drill_under_test"] = drill
spec.loader.exec_module(drill)


class FixtureTests(unittest.TestCase):
    def test_target_is_the_qualified_digest_of_each_architecture(self) -> None:
        fixture = json.loads(bench.FIXTURE.read_text())
        self.assertEqual(fixture["runUrl"], "https://github.com/BipoXDXD/duora/actions/runs/42073873377")
        self.assertEqual(fixture["images"], {
            "amd64": "ghcr.io/bipoxdxd/duora-postgres@sha256:b5103bfd8ac7832915eed8170ffd0bcb94d9bca834cbd659114fa25606f0434e",
            "arm64": "ghcr.io/bipoxdxd/duora-postgres@sha256:fa94a32e8c70257f67505bad12a9da878a4df7f33cd52ba9e6319a4c91025d4f"})

    def test_v1_phase_keeps_the_legacy_digest(self) -> None:
        for architecture, reference in guard.LEGACY_DIGESTS.items():
            self.assertNotEqual(bench.target_for(architecture), reference)

    def test_source_comes_from_the_lock(self) -> None:
        lock = json.loads(bench.LOCK.read_text())
        self.assertEqual(bench.source_for("arm64"), lock["images"]["arm64"])


class OutageTests(unittest.TestCase):
    def test_outage_sums_the_unavailable_intervals_between_samples(self) -> None:
        samples = [(0.0, True), (1.0, False), (2.0, False), (3.0, True), (4.0, False), (5.0, True)]
        self.assertEqual(bench.outage_seconds(samples), 3.0)

    def test_no_failure_is_no_outage_and_a_final_failure_counts_until_the_end(self) -> None:
        self.assertEqual(bench.outage_seconds([(0.0, True), (1.0, True)]), 0.0)
        self.assertEqual(bench.outage_seconds([(0.0, True), (1.0, False), (2.5, False)]), 1.5)
        self.assertEqual(bench.outage_seconds([]), 0.0)


class MatrixTests(unittest.TestCase):
    def test_every_forward_phase_is_crossed_by_sigkill(self) -> None:
        self.assertEqual(bench.KILL_STATES, swap.SWAP_FORWARD_PHASES)
        self.assertIn(bench.FORWARD_ONLY_STATE, swap.SWAP_OTHER_PHASES)
        self.assertIn(bench.ABORT_ONLY_STATE, swap.SWAP_OTHER_PHASES)


class ComposeDoubleTests(unittest.TestCase):
    def test_s3_double_keeps_the_production_file_and_mounts_the_ca_only_in_backup(self) -> None:
        raw = (REPO / "infra/compose/compose.prod.yml").read_text()
        changed = lab._with_s3_double(raw, key="k", secret="s", certificates=Path("/certs"))
        self.assertEqual(changed.count("\n  s3:\n"), 1)
        self.assertEqual(changed.count("/certs/public.crt:/etc/pgbackrest/drill-ca.crt:ro"), 1)
        backup = changed[changed.index("\n  backup:\n"):changed.index("\n  restore:\n")]
        self.assertIn("drill-ca.crt", backup)
        self.assertEqual(changed.count("\nservices:\n"), 1)
        for line in raw.splitlines():
            self.assertIn(line, changed)

    def test_repository_config_is_the_double_never_a_real_bucket(self) -> None:
        config = bench.repository_config("key", "secret")
        self.assertIn("repo1-s3-endpoint=s3\n", config)
        self.assertIn("repo1-storage-ca-file=/etc/pgbackrest/drill-ca.crt", config)


class DrillStageTests(unittest.TestCase):
    def test_root_runs_directly_and_sudo_is_noninteractive(self) -> None:
        self.assertEqual(drill.swap_command(euid=0, can_sudo=False, required=True)[1:], [str(drill.SWAP_PROOF), "--mode", "pitr"])
        self.assertEqual(drill.swap_command(euid=1000, can_sudo=True, required=True)[:2], ["sudo", "--non-interactive"])

    def test_actions_cannot_skip_and_a_workstation_says_it_skipped(self) -> None:
        with self.assertRaises(drill.DrillFailure):
            drill.swap_command(euid=1000, can_sudo=False, required=True)
        self.assertIsNone(drill.swap_command(euid=1000, can_sudo=False, required=False))

    def report(self):
        return drill.DrillReport(image="image")

    def stage(self, result) -> tuple[bool, Any]:
        report = self.report()
        with patch.object(drill.subprocess, "run", return_value=result), patch.object(drill, "_can_sudo", return_value=True), \
                patch.object(drill, "print", create=True):
            return drill._swap_stage(report), report

    def test_failure_or_missing_result_fails_the_drill(self) -> None:
        from subprocess import CompletedProcess
        self.assertFalse(self.stage(CompletedProcess([], 1, "SWAP_RESULT={}\n", "boom"))[0])
        self.assertFalse(self.stage(CompletedProcess([], 0, "no result\n", ""))[0])

    def test_success_is_recorded_in_the_report(self) -> None:
        from subprocess import CompletedProcess
        passed, report = self.stage(CompletedProcess([], 0, 'noise\nSWAP_RESULT={"timeline":2}\n', ""))
        self.assertTrue(passed)
        self.assertEqual(report.swap, {"timeline": 2})


class DriverTests(unittest.TestCase):
    def test_phase_reader_tolerates_absent_journal(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            self.assertIsNone(driver.current_phase(Path(directory)))
            journal = Path(directory) / "postgres-swap"
            journal.mkdir()
            (journal / "phase.json").write_text('{"phase":"quiesce"}')
            self.assertEqual(driver.current_phase(Path(directory)), "quiesce")

    def test_clock_is_inside_the_real_window(self) -> None:
        swap._require_swap_window(driver.WINDOW)


if __name__ == "__main__":
    unittest.main()
