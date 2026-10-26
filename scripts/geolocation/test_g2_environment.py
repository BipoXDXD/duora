import copy
import unittest
from unittest.mock import patch

import g2_environment
from g2_environment import GuardError, validate_ci, validate_environment


class G2EnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.environment = {
            "name": "g2-ipinfo-lite",
            "can_admins_bypass": False,
            "protection_rules": [
                {
                    "type": "required_reviewers",
                    "reviewers": [{"type": "User", "reviewer": {"login": "BipoXDXD"}}],
                }
            ],
            "deployment_branch_policy": {"protected_branches": False, "custom_branch_policies": True},
        }
        self.policies = {"total_count": 1, "branch_policies": [{"name": "main", "type": "branch"}]}
        self.run = {
            "id": 10,
            "run_attempt": 1,
            "path": ".github/workflows/ci.yml",
            "head_sha": "a" * 40,
            "head_branch": "main",
            "event": "push",
            "status": "completed",
            "conclusion": "success",
            "head_repository": {"full_name": "BipoXDXD/duora"},
        }
        self.jobs = [{"name": name, "status": "completed", "conclusion": "success"} for name in (
            "backend", "frontend", "e2e", "ops", "contract", "secret-scan", "supply-chain",
            "images (amd64)", "images (arm64)",
        )]

    def test_exact_environment_and_all_ci_jobs_are_accepted(self):
        validate_environment(self.environment, self.policies)
        validate_ci(self.run, {"total_count": 9, "jobs": self.jobs}, "a" * 40)

    def test_environment_requires_owner_review(self):
        self.environment["protection_rules"] = []
        with self.assertRaises(GuardError):
            validate_environment(self.environment, self.policies)

    def test_admin_bypass_is_rejected(self):
        self.environment["can_admins_bypass"] = True
        with self.assertRaises(GuardError):
            validate_environment(self.environment, self.policies)

    def test_extra_reviewers_cannot_substitute_the_owner(self):
        for extra in (
            {"type": "User", "reviewer": {"login": "another-user"}},
            {"type": "Team", "reviewer": {"slug": "another-team"}},
        ):
            invalid = copy.deepcopy(self.environment)
            invalid["protection_rules"][0]["reviewers"].append(extra)
            with self.assertRaises(GuardError):
                validate_environment(invalid, self.policies)

    def test_production_and_unprotected_environments_are_rejected(self):
        for field, value in (("name", "production"), ("deployment_branch_policy", None)):
            invalid = copy.deepcopy(self.environment)
            invalid[field] = value
            with self.assertRaises(GuardError):
                validate_environment(invalid, self.policies)

    def test_branch_patterns_and_tags_are_rejected(self):
        for policies in (
            {"total_count": 1, "branch_policies": [{"name": "*", "type": "branch"}]},
            {"total_count": 1, "branch_policies": [{"name": "main", "type": "tag"}]},
            {"total_count": 2, "branch_policies": self.policies["branch_policies"]},
        ):
            with self.assertRaises(GuardError):
                validate_environment(self.environment, policies)

    def test_wrong_sha_pr_or_foreign_repository_does_not_authorize_real_data(self):
        for field, value in (
            ("head_sha", "b" * 40), ("event", "pull_request"), ("head_branch", "feature"),
            ("head_repository", {"full_name": "example/duora"}), ("conclusion", "failure"),
        ):
            invalid = dict(self.run, **{field: value})
            with self.assertRaises(GuardError):
                validate_ci(invalid, {"total_count": 9, "jobs": self.jobs}, "a" * 40)

    def test_missing_skipped_failed_or_pending_check_is_rejected(self):
        for jobs in (
            self.jobs[:-1],
            [dict(job, conclusion="skipped") for job in self.jobs],
            [dict(job, conclusion="failure") for job in self.jobs],
            [dict(job, status="in_progress") for job in self.jobs],
        ):
            with self.assertRaises(GuardError):
                validate_ci(self.run, {"total_count": len(jobs), "jobs": jobs}, "a" * 40)

    def test_duplicate_extra_or_incomplete_job_response_is_rejected(self):
        for response in (
            {"total_count": 10, "jobs": self.jobs + [dict(self.jobs[0])]},
            {"total_count": 10, "jobs": self.jobs + [{"name": "unreviewed", "status": "completed", "conclusion": "success"}]},
            {"total_count": 10, "jobs": self.jobs},
        ):
            with self.subTest(response=response), self.assertRaises(GuardError):
                validate_ci(self.run, response, "a" * 40)

    def test_wrong_workflow_or_invalid_run_identity_is_rejected(self):
        for field, value in (("path", ".github/workflows/other.yml"), ("id", 0), ("run_attempt", False)):
            with self.subTest(field=field), self.assertRaises(GuardError):
                validate_ci(dict(self.run, **{field: value}), {"total_count": 9, "jobs": self.jobs}, "a" * 40)

    def run_guard(self, main_sha, latest_attempt=1):
        calls = []

        def api(path):
            calls.append(path)
            if path == "commits/main":
                return {"sha": main_sha}
            if path == "environments/g2-ipinfo-lite":
                return self.environment
            if path.startswith("environments/g2-ipinfo-lite/deployment-branch-policies"):
                return self.policies
            if path.startswith("actions/workflows/ci.yml/runs?"):
                return {"workflow_runs": [self.run]}
            if path == "actions/runs/10":
                return dict(self.run, run_attempt=latest_attempt)
            if path in ("actions/runs/10/jobs?per_page=100", "actions/runs/10/attempts/1/jobs?per_page=100"):
                return {"total_count": 9, "jobs": self.jobs}
            raise AssertionError("Unexpected metadata request: " + path)

        context = {"GITHUB_SHA": "a" * 40, "GITHUB_REPOSITORY": "BipoXDXD/duora",
                   "GITHUB_REF": "refs/heads/main", "GITHUB_EVENT_NAME": "workflow_dispatch"}
        with patch.dict(g2_environment.os.environ, context, clear=True), patch.object(g2_environment, "api", side_effect=api):
            return g2_environment.main(), calls

    def test_old_successful_sha_cannot_authorize_after_main_advances(self):
        result, _ = self.run_guard("b" * 40)
        self.assertEqual(result, 1)

    def test_attempt_change_during_verification_is_rejected(self):
        result, _ = self.run_guard("a" * 40, latest_attempt=2)
        self.assertEqual(result, 1)

    def test_exact_attempt_and_current_main_are_read_explicitly(self):
        result, calls = self.run_guard("a" * 40)
        self.assertEqual(result, 0)
        self.assertIn("actions/runs/10/attempts/1/jobs?per_page=100", calls)
        self.assertIn("actions/runs/10", calls)
        self.assertEqual(calls.count("commits/main"), 2)


if __name__ == "__main__":
    unittest.main()
