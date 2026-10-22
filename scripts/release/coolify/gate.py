"""Accept only successful CI of the exact main commit from this repository."""

import http.client
import json
import os
import re
import time
from typing import Any

REPOSITORIES = {
    "duora": "BipoXDXD/duora",
    "stillon": "BipoXDXD/stillon",
    "goblinshift": "BipoXDXD/GoblinShift",
}
REQUIRED = {
    "duora": {
        "backend",
        "frontend",
        "e2e",
        "images (amd64)",
        "images (arm64)",
        "ops",
        "secret-scan",
        "contract",
        "supply-chain",
    },
    "stillon": {
        "lint",
        "test (postgres:17-alpine)",
        "test (postgres:18-alpine)",
        "secrets",
        "web",
        "image",
        "e2e",
        "vps",
        "workflows",
        "publish",
    },
    "goblinshift": {
        "Verificações",
        "Segredos (gitleaks)",
        "Interface web",
        "Interface no navegador (Playwright)",
        "Contrato (Schemathesis)",
        "Executor sob gVisor",
        "SLOs e alertas (OpenObserve)",
        "Imagem",
        "Imagem do executor",
        "Imagem do Nginx com a SPA",
    },
}
LEGACY_SKIPS = {"Deploy na VPS", "Jornadas na VPS (pós-deploy)"}


class PendingJobView(ValueError):
    """A valid, terminal run whose job snapshot has not converged yet."""


def validate(run: Any, jobs: Any, project: Any, commit: Any) -> Any:
    if not isinstance(commit, str) or not re.fullmatch("[0-9a-f]{40}", commit):
        raise ValueError("Full lowercase SHA required")
    if (
        run.get("head_sha") != commit
        or run.get("head_branch") != "main"
        or run.get("event") != "push"
        or run.get("path") != ".github/workflows/ci.yml"
        or run.get("head_repository", {}).get("full_name") != REPOSITORIES[project]
        or run.get("status") != "completed"
        or run.get("conclusion") != "success"
    ):
        raise ValueError("Only successful CI main push from this repository qualifies")
    names = [job.get("name") for job in jobs]
    allowed = REQUIRED[project] | (LEGACY_SKIPS if project == "goblinshift" else set())
    if len(names) != len(set(names)) or set(names) != allowed:
        raise ValueError("Missing, extra or duplicated CI gate")
    pending = False
    for job in jobs:
        if job.get("status") != "completed":
            if job.get("status") not in {"queued", "in_progress"} or job.get("conclusion") is not None:
                raise ValueError("Invalid pending CI gate")
            pending = True
            continue
        if job["name"] in REQUIRED[project] and job.get("conclusion") != "success":
            raise ValueError("Required gate did not succeed")
        if job["name"] not in REQUIRED[project] and job.get("conclusion") != "skipped":
            raise ValueError("Legacy deploy must remain disabled")
    if pending:
        raise PendingJobView("CI gate still running")
    return commit


def api(path: Any) -> Any:
    connection = http.client.HTTPSConnection("api.github.com", timeout=30)
    try:
        connection.request(
            "GET",
            "/" + path,
            headers={
                "Authorization": "Bearer " + os.environ["GH_TOKEN"],
                "Accept": "application/vnd.github+json",
                "User-Agent": "coolify-release-gate",
            },
        )
        response = connection.getresponse()
        body = response.read()
        if response.status != 200:
            raise ValueError("GitHub gate API request failed")
        return json.loads(body)
    finally:
        connection.close()


def current_main(repository: str, commit: str) -> bool:
    if api(f"repos/{repository}/commits/main")["sha"] == commit:
        return True
    if os.environ.get("SKIP_SUPERSEDED") != "true":
        raise ValueError("Superseded main commit; refusing stale deployment")
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        output.write("eligible=false\n")
    print("Release superseded by newer main; no images or deployment requested")
    return False


def completed_job_view(run: Any, project: str, commit: str) -> Any:
    """Wait only for a coherent job view, never for a failed gate to become green."""
    run_id, attempt = run.get("id"), run.get("run_attempt")
    if type(run_id) is not int or run_id <= 0 or type(attempt) is not int or attempt <= 0:
        raise ValueError("Invalid CI run identity")
    repository = REPOSITORIES[project]
    # Authenticated GitHub job responses advertise a 60-second cache lifetime.
    # Bound consistency recovery to two such lifetimes; every gate stays mandatory.
    deadline = time.monotonic() + 120
    while True:
        result = api(f"repos/{repository}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100")
        if (not isinstance(result, dict) or not isinstance(result.get("jobs"), list)
                or type(result.get("total_count")) is not int
                or result["total_count"] != len(result["jobs"])):
            raise ValueError("Incomplete CI gate response")
        try:
            validate(run, result["jobs"], project, commit)
            return run
        except PendingJobView:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise ValueError("CI job view did not settle before release timeout") from None
            time.sleep(min(5, remaining))
            refreshed = api(f"repos/{repository}/actions/runs/{run_id}")
            if refreshed.get("id") != run_id or refreshed.get("run_attempt") != attempt:
                raise ValueError("CI run or attempt changed during gate verification")
            run = refreshed


def main() -> None:
    project = os.environ["RELEASE_PROJECT"]
    repository = REPOSITORIES[project]
    if os.environ["GITHUB_REPOSITORY"] != repository:
        raise ValueError("Repository mismatch")
    commit = os.environ.get("RELEASE_COMMIT", "")
    if not re.fullmatch("[0-9a-f]{40}", commit):
        raise ValueError("Full commit required")
    if not current_main(repository, commit):
        return
    run_id = os.environ.get("CI_RUN_ID", "")
    deadline = time.monotonic() + (1800 if os.environ.get("WAIT_FOR_CI") == "true" else 0)
    while True:
        if run_id:
            if not run_id.isdigit():
                raise ValueError("Invalid CI run id")
            run = api(f"repos/{repository}/actions/runs/{run_id}")
        else:
            runs = api(
                f"repos/{repository}/actions/workflows/ci.yml/runs?event=push&branch=main&head_sha={commit}&per_page=100"
            )["workflow_runs"]
            run = max(runs, key=lambda item: item["id"]) if runs else None
        if run is not None and run.get("status") == "completed":
            break
        if time.monotonic() >= deadline:
            raise ValueError("CI did not finish before release timeout")
        time.sleep(10)
    run = completed_job_view(run, project, commit)
    if not current_main(repository, commit):
        return
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        output.write(f"eligible=true\ncommit={commit}\nci_run={run['id']}\nci_attempt={run['run_attempt']}\n")
    print(f"Exact main commit {commit}: every required CI gate succeeded")


if __name__ == "__main__":
    main()
