"""Read-only G2 gate. Never receives the IPinfo secret or prints API responses."""

import json
import os
import subprocess

REPOSITORY = "BipoXDXD/duora"
CHECKS = frozenset(("backend", "frontend", "e2e", "ops", "contract", "secret-scan", "supply-chain",
                    "images (amd64)", "images (arm64)"))


class GuardError(Exception):
    """A fixed, sanitized failure; API payloads and secrets stay out of logs."""


def validate_environment(environment, policies):
    required = [rule for rule in environment.get("protection_rules", []) if rule.get("type") == "required_reviewers"]
    reviewers = [reviewer for rule in required for reviewer in rule.get("reviewers", [])]
    if (environment.get("name") != "g2-ipinfo-lite" or environment.get("can_admins_bypass") is not False
            or len(required) != 1 or len(reviewers) != 1
            or reviewers[0].get("type") != "User" or reviewers[0].get("reviewer", {}).get("login") != "BipoXDXD"
            or environment.get("deployment_branch_policy") != {"protected_branches": False, "custom_branch_policies": True}
            or policies.get("total_count") != 1
            or [(p.get("name"), p.get("type")) for p in policies.get("branch_policies", [])] != [("main", "branch")]):
        raise GuardError("G2 environment protection is missing or incompatible.")


def validate_ci(run, jobs, sha):
    if (not isinstance(run, dict) or type(run.get("id")) is not int or run["id"] <= 0
            or type(run.get("run_attempt")) is not int or run["run_attempt"] <= 0
            or run.get("path") != ".github/workflows/ci.yml"
            or run.get("head_sha") != sha or run.get("head_branch") != "main" or run.get("event") != "push"
            or run.get("status") != "completed" or run.get("conclusion") != "success"
            or run.get("head_repository", {}).get("full_name") != REPOSITORY):
        raise GuardError("Exact main CI has not succeeded.")
    if (not isinstance(jobs, dict) or not isinstance(jobs.get("jobs"), list)
            or type(jobs.get("total_count")) is not int
            or jobs["total_count"] != len(jobs["jobs"]) or len(jobs["jobs"]) != len(CHECKS)
            or any(not isinstance(job, dict) for job in jobs["jobs"])
            or {job.get("name") for job in jobs["jobs"]} != CHECKS
            or any(job.get("status") != "completed" or job.get("conclusion") != "success" for job in jobs["jobs"])):
        raise GuardError("Exactly the nine complete, unique CI checks must succeed.")


def api(path):
    result = subprocess.run(["gh", "api", f"repos/{REPOSITORY}/{path}"], capture_output=True, text=True, check=False)
    if result.returncode:
        raise GuardError("G2 read-only access or configuration is unavailable.")
    return json.loads(result.stdout)


def main():
    try:
        sha = os.environ.get("GITHUB_SHA", "")
        if (os.environ.get("GITHUB_REPOSITORY") != REPOSITORY or os.environ.get("GITHUB_REF") != "refs/heads/main"
                or os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
                or len(sha) != 40 or any(c not in "0123456789abcdef" for c in sha)):
            raise GuardError("Real G2 requires a manual main run in the source repository.")
        if api("commits/main").get("sha") != sha:
            raise GuardError("G2 source is no longer current main.")
        environment = api("environments/g2-ipinfo-lite")
        policies = api("environments/g2-ipinfo-lite/deployment-branch-policies?per_page=100")
        validate_environment(environment, policies)
        runs = api(f"actions/workflows/ci.yml/runs?head_sha={sha}&event=push&per_page=10")["workflow_runs"]
        if not runs:
            raise GuardError("Exact main CI is missing.")
        latest = max(runs, key=lambda run: run["id"])
        run_id, attempt = latest.get("id"), latest.get("run_attempt")
        if type(run_id) is not int or run_id <= 0 or type(attempt) is not int or attempt <= 0:
            raise GuardError("CI run identity is invalid.")
        jobs = api(f"actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100")
        validate_ci(latest, jobs, sha)
        current = api(f"actions/runs/{run_id}")
        if current.get("id") != run_id or current.get("run_attempt") != attempt:
            raise GuardError("CI attempt changed during verification.")
        validate_ci(current, jobs, sha)
        if api("commits/main").get("sha") != sha:
            raise GuardError("Main changed during verification.")
        print("G2 environment and exact main CI verified. Per-run approval and profile still required.")
        return 0
    except (GuardError, ValueError, KeyError, OSError):
        print("G2 blocked: check protected environment, read access and exact main CI; no provider access attempted.")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
