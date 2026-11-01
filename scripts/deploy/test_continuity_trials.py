"""Independent artifacts and bounded synthetic load for complete native qualifications."""
import json
from pathlib import Path
import tempfile
import time
import unittest

import continuity_lab as lab


class ContinuityTrialTests(unittest.TestCase):
    def test_three_trials_keep_the_first_canonical_report_and_all_original_bundles(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            bundle.mkdir()
            paths = []
            for trial in (1, 2, 3):
                (bundle / "executor.py").write_text("synthetic-trial-" + str(trial))
                report = {"architecture": "arm64", "syntheticTrial": trial}
                paths.append(lab.save_qualification(report, bundle, root, trial=trial))
            base = root / "duora-executor-lab/arm64"
            self.assertEqual(paths, [base, base / "trials/2", base / "trials/3"])
            for trial, output in enumerate(paths, 1):
                self.assertEqual(json.loads((output / "qualification.json").read_text())["syntheticTrial"], trial)
                self.assertEqual((output / "bundle/executor.py").read_text(), "synthetic-trial-" + str(trial))
            with self.assertRaises(FileExistsError):
                lab.save_qualification({"architecture": "arm64"}, bundle, root, trial=2)

    def test_trial_identifiers_and_architecture_cannot_escape_the_qualification_tree(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for architecture, trial in (("../../foreign", 1), ("arm64", 0), ("amd64", 4), ("arm64", True)):
                with self.subTest(architecture=architecture, trial=trial), self.assertRaises(ValueError):
                    lab.save_qualification({"architecture": architecture}, root, root, trial=trial)

    def test_controlled_cpu_worker_reports_measured_work_and_is_reaped(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            load = lab.TrialCpuLoad(Path(directory) / "load")
            try:
                time.sleep(0.15)
                metrics = load.finish()
                self.assertGreater(metrics["cpuSeconds"], 0)
                self.assertGreater(metrics["wallSeconds"], 0)
                self.assertGreater(metrics["batches"], 0)
                self.assertGreaterEqual(metrics["nice"], 10)
                self.assertEqual(metrics["reason"], "requested")
                self.assertIsNotNone(load.process.poll())
            finally:
                load.abort()

    def test_a_worker_that_died_before_trial_completion_cannot_qualify_load_coverage(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            load = lab.TrialCpuLoad(Path(directory) / "load")
            try:
                load.process.terminate()
                load.process.wait(timeout=3)
                with self.assertRaisesRegex(RuntimeError, "load.*cover"):
                    load.finish()
            finally:
                load.abort()
