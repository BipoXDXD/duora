"""Require a successful main push CI run and every release gate for one commit."""

import json
import os
import re
import subprocess

REQUIRED_JOBS = {
    "backend", "frontend", "e2e", "images (amd64)", "images (arm64)",
    "ops", "secret-scan", "contract", "supply-chain",
}


def validate(run, jobs, commit, repository):
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Expected a full lowercase commit SHA")
    if (run.get("head_sha") != commit or run.get("head_branch") != "main"
            or run.get("event") != "push" or run.get("path") != ".github/workflows/ci.yml"
            or run.get("head_repository", {}).get("full_name") != repository
            or run.get("status") != "completed" or run.get("conclusion") != "success"):
        raise ValueError("CI must be a successful main push for this exact repository and commit")
    if {job.get("name") for job in jobs} != REQUIRED_JOBS or len(jobs) != len(REQUIRED_JOBS):
        raise ValueError("CI must contain exactly the nine required jobs")
    if any(job.get("status") != "completed" or job.get("conclusion") != "success" for job in jobs):
        raise ValueError("Every required job must succeed; skipped jobs do not qualify")


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", path], text=True))


def main():
    commit = os.environ["RELEASE_COMMIT"]
    repository = os.environ["GITHUB_REPOSITORY"]
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Expected a full lowercase commit SHA")
    subprocess.run(["git", "merge-base", "--is-ancestor", commit, "origin/main"], check=True)
    run_id, attempt = os.environ.get("CI_RUN", ""), os.environ.get("CI_ATTEMPT", "")
    if run_id or attempt:
        if not re.fullmatch(r"[1-9][0-9]{0,19}", run_id) or not re.fullmatch(r"[1-9][0-9]{0,19}", attempt):
            raise ValueError("CI run and attempt must be supplied together as positive decimal integers")
        expected_id, expected_attempt = int(run_id), int(attempt)
    else:
        runs = api(f"repos/{repository}/actions/workflows/ci.yml/runs?event=push&branch=main&head_sha={commit}&per_page=100")["workflow_runs"]
        if not runs:
            raise ValueError("No CI push run exists for the release commit")
        selected = max(runs, key=lambda item: item["id"])
        expected_id, expected_attempt = selected["id"], selected["run_attempt"]
    if (type(expected_id) is not int or expected_id <= 0
            or type(expected_attempt) is not int or expected_attempt <= 0):
        raise ValueError("CI run identity must be positive integers")
    run = api(f"repos/{repository}/actions/runs/{expected_id}")
    if run.get("id") != expected_id or run.get("run_attempt") != expected_attempt:
        raise ValueError("CI run identity or attempt changed")
    # Explicit attempt prevents older successful jobs from qualifying a failed rerun.
    result = api(f"repos/{repository}/actions/runs/{run['id']}/attempts/{run['run_attempt']}/jobs?per_page=100")
    jobs = result["jobs"]
    if result["total_count"] != len(jobs):
        raise ValueError("Incomplete CI job response")
    validate(run, jobs, commit, repository)
    current = api(f"repos/{repository}/actions/runs/{expected_id}")
    if current.get("id") != expected_id or current.get("run_attempt") != expected_attempt:
        raise ValueError("CI run identity or attempt changed during verification")
    validate(current, jobs, commit, repository)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"commit={commit}\nci_run={run['id']}\nci_attempt={run['run_attempt']}\n")
    print(f"Verified all nine CI gates for {commit}, run {run['id']}, attempt {run['run_attempt']}")


if __name__ == "__main__":
    main()
