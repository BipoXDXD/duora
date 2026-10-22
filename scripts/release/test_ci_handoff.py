"""A validated CI reference is rechecked directly, never trusted as a bypass."""
import copy
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import check_ci


class CiHandoffTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.output = Path(self.directory.name) / "outputs"
        self.commit = "a" * 40
        self.repo = "owner/repo"
        self.prefix = f"repos/{self.repo}/actions"
        self.run = {"id": 123, "run_attempt": 2, "head_sha": self.commit, "head_branch": "main",
                    "event": "push", "path": ".github/workflows/ci.yml",
                    "head_repository": {"full_name": self.repo}, "status": "completed", "conclusion": "success"}
        self.jobs = [{"name": name, "status": "completed", "conclusion": "success"}
                     for name in check_ci.REQUIRED_JOBS]
        self.environment = {"RELEASE_COMMIT": self.commit, "GITHUB_REPOSITORY": self.repo,
                            "GITHUB_OUTPUT": str(self.output), "CI_RUN": "123", "CI_ATTEMPT": "2"}
        self.reads = 0
        self.changed = None

    def api(self, path):
        if path == f"{self.prefix}/runs/123":
            self.reads += 1
            return copy.deepcopy(self.changed if self.reads > 1 and self.changed else self.run)
        if path == f"{self.prefix}/runs/123/attempts/2/jobs?per_page=100":
            return {"total_count": 9, "jobs": copy.deepcopy(self.jobs)}
        if path.startswith(f"{self.prefix}/workflows/ci.yml/runs?"):
            return {"workflow_runs": []}
        raise AssertionError(f"Unexpected API path: {path}")

    def execute(self):
        with patch.dict(os.environ, self.environment, clear=True), patch.object(check_ci, "api", self.api), \
                patch.object(check_ci.subprocess, "run"):
            check_ci.main()

    def test_known_reference_qualifies_without_index_visibility(self):
        self.execute()
        self.assertEqual(f"commit={self.commit}\nci_run=123\nci_attempt=2\n", self.output.read_text())

    def test_partial_or_invalid_reference_never_falls_back_to_index(self):
        for key, value in [("CI_RUN", ""), ("CI_ATTEMPT", ""), ("CI_RUN", "../123"),
                           ("CI_ATTEMPT", "0"), ("CI_RUN", "0123"), ("CI_ATTEMPT", "-2")]:
            original = dict(self.environment)
            self.environment[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                self.execute()
            self.assertFalse(self.output.exists())
            self.environment = original

    def test_run_id_attempt_and_existing_identity_gates_are_revalidated(self):
        for key, value in [("id", 124), ("run_attempt", 3), ("head_sha", "b" * 40),
                           ("head_branch", "feature"), ("event", "pull_request"),
                           ("path", ".github/workflows/other.yml"), ("conclusion", "failure")]:
            original = dict(self.run)
            self.run[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.execute()
            self.assertFalse(self.output.exists())
            self.run = original

    def test_incomplete_or_nonterminal_job_views_do_not_write_outputs(self):
        for jobs in (self.jobs[:-1], [{**self.jobs[0], "status": "in_progress"}] + self.jobs[1:],
                     [{**self.jobs[0], "conclusion": "skipped"}] + self.jobs[1:]):
            self.jobs = jobs
            with self.assertRaises(ValueError):
                self.execute()
            self.assertFalse(self.output.exists())

    def test_new_attempt_or_failed_run_after_job_read_does_not_qualify(self):
        for key, value in [("run_attempt", 3), ("status", "in_progress"), ("conclusion", "failure")]:
            self.reads = 0
            self.changed = {**self.run, key: value}
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.execute()
            self.assertFalse(self.output.exists())

    def test_without_reference_missing_index_still_fails_closed(self):
        self.environment.pop("CI_RUN")
        self.environment.pop("CI_ATTEMPT")
        with self.assertRaisesRegex(ValueError, "No CI push run"):
            self.execute()
        self.assertFalse(self.output.exists())
