import unittest

import gate

SHA = "a" * 40


class GateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.ci_run = {
            "head_sha": SHA,
            "head_branch": "main",
            "event": "push",
            "path": ".github/workflows/ci.yml",
            "head_repository": {"full_name": "BipoXDXD/stillon"},
            "status": "completed",
            "conclusion": "success",
        }
        self.jobs = [
            {"name": name, "status": "completed", "conclusion": "success"}
            for name in gate.REQUIRED["stillon"]
        ]

    def test_all_exact_jobs_success(self) -> None:
        self.assertEqual(gate.validate(self.ci_run, self.jobs, "stillon", SHA), SHA)

    def test_wrong_run_ref_repository_or_state_refused(self) -> None:
        for field, value in [
            ("head_sha", "b" * 40),
            ("head_branch", "feature"),
            ("event", "pull_request"),
            ("path", ".github/workflows/fake.yml"),
            ("conclusion", "failure"),
            ("head_repository", {"full_name": "fork/stillon"}),
            ("status", "in_progress"),
        ]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                gate.validate({**self.ci_run, field: value}, self.jobs, "stillon", SHA)

    def test_missing_duplicate_skipped_failed_or_running_gate_refused(self) -> None:
        for jobs in [
            self.jobs[:-1],
            [*self.jobs, self.jobs[0]],
            [{**self.jobs[0], "conclusion": "skipped"}, *self.jobs[1:]],
            [{**self.jobs[0], "conclusion": "failure"}, *self.jobs[1:]],
            [{**self.jobs[0], "status": "in_progress"}, *self.jobs[1:]],
        ]:
            with self.assertRaises(ValueError):
                gate.validate(self.ci_run, jobs, "stillon", SHA)

    def test_sha_not_interpreted_as_arguments_or_newline(self) -> None:
        for sha in ["a" * 7, "--help", "a" * 40 + "\n"]:
            with self.assertRaises(ValueError):
                gate.validate(self.ci_run, self.jobs, "stillon", sha)


if __name__ == "__main__":
    unittest.main()
