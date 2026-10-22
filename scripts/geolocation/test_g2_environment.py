import copy
import unittest

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
        validate_ci(self.run, {"jobs": self.jobs}, "a" * 40)

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
                validate_ci(invalid, {"jobs": self.jobs}, "a" * 40)

    def test_missing_skipped_failed_or_pending_check_is_rejected(self):
        for jobs in (
            self.jobs[:-1],
            [dict(job, conclusion="skipped") for job in self.jobs],
            [dict(job, conclusion="failure") for job in self.jobs],
            [dict(job, status="in_progress") for job in self.jobs],
        ):
            with self.assertRaises(GuardError):
                validate_ci(self.run, {"jobs": jobs}, "a" * 40)


if __name__ == "__main__":
    unittest.main()
