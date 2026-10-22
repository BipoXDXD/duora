import unittest
import os
import tempfile
from pathlib import Path
from unittest.mock import patch

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


class SupersededReleaseTests(unittest.TestCase):
    def test_superseded_release_is_successful_noop_before_any_ci_query(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'output'
            env = {'RELEASE_PROJECT': 'duora', 'GITHUB_REPOSITORY': 'BipoXDXD/duora',
                   'RELEASE_COMMIT': SHA, 'GITHUB_OUTPUT': str(output),
                   'SKIP_SUPERSEDED': 'true'}
            with patch.dict(os.environ, env, clear=True), patch.object(
                    gate, 'api', return_value={'sha': 'b' * 40}) as api:
                gate.main()
            self.assertIn('eligible=false', output.read_text())
            self.assertEqual(api.call_count, 1)

    def test_strict_callers_still_refuse_stale_commits(self):
        with patch.dict(os.environ, {'RELEASE_PROJECT': 'duora',
                'GITHUB_REPOSITORY': 'BipoXDXD/duora', 'RELEASE_COMMIT': SHA}, clear=True), \
                patch.object(gate, 'api', return_value={'sha': 'b' * 40}):
            with self.assertRaises(ValueError):
                gate.main()


class ConsistentJobViewTests(unittest.TestCase):
    def setUp(self):
        self.ci_run = {
            'id': 101, 'run_attempt': 1, 'head_sha': SHA, 'head_branch': 'main',
            'event': 'push', 'path': '.github/workflows/ci.yml',
            'head_repository': {'full_name': 'BipoXDXD/duora'},
            'status': 'completed', 'conclusion': 'success',
        }
        self.jobs = [{'name': name, 'status': 'completed', 'conclusion': 'success'}
                     for name in sorted(gate.REQUIRED['duora'])]
        self.pending = [{**self.jobs[0], 'status': 'in_progress', 'conclusion': None}, *self.jobs[1:]]

    def execute(self, snapshots, *, later_run=None, clock=None):
        calls = []
        views = iter(snapshots)
        run_reads = 0

        def api(path):
            nonlocal run_reads
            calls.append(path)
            if path.endswith('/commits/main'):
                return {'sha': SHA}
            if path.endswith('/actions/runs/101'):
                run_reads += 1
                return later_run if later_run is not None and run_reads > 1 else self.ci_run
            if path.endswith('/actions/runs/101/attempts/1/jobs?per_page=100'):
                jobs = next(views)
                return {'total_count': len(jobs), 'jobs': jobs}
            raise AssertionError('Unexpected API path: ' + path)

        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        output = Path(temporary.name) / 'output'
        env = {'RELEASE_PROJECT': 'duora', 'GITHUB_REPOSITORY': 'BipoXDXD/duora',
               'RELEASE_COMMIT': SHA, 'CI_RUN_ID': '101', 'GITHUB_OUTPUT': str(output)}
        with patch.dict(os.environ, env, clear=True), patch.object(gate, 'api', side_effect=api), \
                patch.object(gate.time, 'sleep') as sleep, \
                patch.object(gate.time, 'monotonic', side_effect=clock or (lambda: 0)):
            try:
                gate.main()
                error = None
            except ValueError as caught:
                error = caught
        return output, calls, sleep.call_count, error

    def test_completed_run_rechecks_a_transient_pending_job_view(self):
        output, calls, waits, error = self.execute([self.pending, self.jobs])
        self.assertIsNone(error, 'A transient jobs view must be rechecked without accepting pending gates')
        self.assertIn('eligible=true', output.read_text())
        self.assertIn('ci_run=101', output.read_text())
        self.assertEqual(2, sum('/attempts/1/jobs?' in path for path in calls))
        self.assertGreaterEqual(waits, 1)

    def test_pending_jobs_never_write_eligibility_after_the_bounded_wait(self):
        output, _, _, error = self.execute([self.pending] * 4, clock=iter([0, 0, 10000, 20000]).__next__)
        self.assertIsInstance(error, ValueError)
        self.assertFalse(output.exists())

    def test_completed_failure_skip_or_unknown_status_never_gets_a_retry(self):
        for change in [{'status': 'completed', 'conclusion': 'failure'},
                       {'status': 'completed', 'conclusion': 'skipped'},
                       {'status': 'not-a-state', 'conclusion': None}]:
            jobs = [{**self.jobs[0], **change}, *self.jobs[1:]]
            with self.subTest(change=change):
                output, calls, waits, error = self.execute([jobs, self.jobs])
                self.assertIsInstance(error, ValueError)
                self.assertFalse(output.exists())
                self.assertEqual(1, sum('/jobs?' in path for path in calls))
                self.assertEqual(0, waits)

    def test_failed_job_after_a_pending_job_still_fails_without_waiting(self):
        jobs = [self.pending[0], {**self.jobs[1], 'conclusion': 'failure'}, *self.jobs[2:]]
        output, _, waits, error = self.execute([jobs, self.jobs])
        self.assertIsInstance(error, ValueError)
        self.assertFalse(output.exists())
        self.assertEqual(0, waits)

    def test_attempt_or_identity_change_cannot_qualify_the_original_job_view(self):
        for change in [{'run_attempt': 2}, {'head_sha': 'b' * 40}, {'id': 102},
                       {'status': 'in_progress', 'conclusion': None}]:
            with self.subTest(change=change):
                output, _, _, error = self.execute([self.pending, self.jobs], later_run={**self.ci_run, **change})
                self.assertIsInstance(error, ValueError)
                self.assertFalse(output.exists())

    def test_missing_duplicate_or_extra_jobs_are_not_treated_as_transient(self):
        for jobs in [self.jobs[:-1], [*self.jobs, self.jobs[0]],
                     [*self.jobs, {'name': 'unexpected', 'status': 'in_progress', 'conclusion': None}]]:
            with self.subTest(jobs=jobs):
                output, _, waits, error = self.execute([jobs, self.jobs])
                self.assertIsInstance(error, ValueError)
                self.assertFalse(output.exists())
                self.assertEqual(0, waits)


if __name__ == "__main__":
    unittest.main()
