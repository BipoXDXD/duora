import unittest
from check_ci import REQUIRED_JOBS, validate


class ReleaseGateTests(unittest.TestCase):
    def setUp(self):
        self.commit = "a" * 40
        self.repo = "owner/repo"
        self.run = dict(head_sha=self.commit, head_branch="main", event="push",
                        path=".github/workflows/ci.yml", head_repository={"full_name": self.repo},
                        status="completed", conclusion="success")
        self.jobs = [dict(name=name, status="completed", conclusion="success") for name in REQUIRED_JOBS]

    def test_success(self):
        validate(self.run, self.jobs, self.commit, self.repo)

    def test_invalid_run_identity_or_state(self):
        for field, value in [("head_sha", "b" * 40), ("head_branch", "feature"),
                             ("event", "pull_request"), ("path", ".github/workflows/other.yml"),
                             ("head_repository", {"full_name": "fork/repo"}),
                             ("status", "in_progress"), ("conclusion", "failure")]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate({**self.run, field: value}, self.jobs, self.commit, self.repo)

    def test_missing_duplicate_or_extra_job(self):
        for jobs in [self.jobs[:-1], self.jobs + [self.jobs[0]], self.jobs[:-1] + [self.jobs[0]]]:
            with self.assertRaises(ValueError):
                validate(self.run, jobs, self.commit, self.repo)

    def test_skipped_failed_pending_jobs(self):
        for state in ["skipped", "failure", "cancelled", None]:
            with self.subTest(state=state), self.assertRaises(ValueError):
                validate(self.run, [{**self.jobs[0], "conclusion": state}] + self.jobs[1:], self.commit, self.repo)

    def test_running_job(self):
        with self.assertRaises(ValueError):
            validate(self.run, [{**self.jobs[0], "status": "in_progress"}] + self.jobs[1:], self.commit, self.repo)

    def test_reject_short_sha_and_injection(self):
        for commit in ["a" * 7, "A" * 40, "--help", "a" * 40 + "\n"]:
            with self.subTest(commit=commit), self.assertRaises(ValueError):
                validate(self.run, self.jobs, commit, self.repo)
