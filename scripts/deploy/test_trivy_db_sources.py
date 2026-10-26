"""Keep approved DB sources inside every pinned Trivy container; no scan or network."""
from __future__ import annotations

import copy
from pathlib import Path
import re
import shlex
from typing import Any
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = {"ci.yml": 4, "publish-images.yml": 3}
IMAGE = "ghcr.io/aquasecurity/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa"
REPOSITORIES = ("ghcr.io/aquasecurity/trivy-db:2", "mirror.gcr.io/aquasec/trivy-db:2")
INVOCATION = re.compile(r'\bdocker\s+run\b(?P<options>.*?)"\$TRIVY_IMAGE"\s+(?:image|fs)\b', re.S)


def checked_invocations(workflow: dict[str, Any], expected_count: int) -> int:
    """Inspect declared container inputs, not the host's unforwarded environment."""
    seen = 0
    for job in workflow["jobs"].values():
        for step in job.get("steps", []):
            script = step.get("run", "")
            if not isinstance(script, str):
                continue
            script = script.replace("\\\n", " ")
            matches = list(INVOCATION.finditer(script))
            if '"$TRIVY_IMAGE"' in script and not matches:
                raise AssertionError("unrecognized Trivy container launch")
            for match in matches:
                environment = {**workflow.get("env", {}), **job.get("env", {}), **step.get("env", {})}
                if environment.get("TRIVY_IMAGE") != IMAGE:
                    raise AssertionError("pinned scanner changed")
                repositories = tuple(str(environment.get("TRIVY_DB_REPOSITORY", "")).split(","))
                if repositories != REPOSITORIES:
                    raise AssertionError("official DB source priority/schema changed")
                options = shlex.split(match.group("options"))
                if not any(left in ("-e", "--env") and right == "TRIVY_DB_REPOSITORY"
                           for left, right in zip(options, options[1:])):
                    raise AssertionError("DB setting is not passed into the container")
                if re.search(r"--(?:skip-db-update|skip-update|offline-scan)\b", script):
                    raise AssertionError("DB/scan bypass is forbidden")
                seen += 1
    if seen != expected_count:
        raise AssertionError("scanner launch coverage changed; review every invocation")
    return seen


class TrivyDatabaseSourcesTests(unittest.TestCase):
    def workflows(self):
        for name, count in WORKFLOWS.items():
            yield name, count, yaml.safe_load((ROOT / ".github/workflows" / name).read_text())

    def test_all_seven_launch_sites_receive_primary_official_source_and_mirror(self):
        total = 0
        for name, count, workflow in self.workflows():
            with self.subTest(workflow=name):
                total += checked_invocations(workflow, count)
        self.assertEqual(7, total)

    def test_regression_unconfigured_default_policy_is_rejected(self):
        for name, count, workflow in self.workflows():
            old = copy.deepcopy(workflow)
            old["env"].pop("TRIVY_DB_REPOSITORY", None)
            for job in old["jobs"].values():
                for step in job.get("steps", []):
                    if isinstance(step.get("run"), str):
                        step["run"] = step["run"].replace("-e TRIVY_DB_REPOSITORY ", "")
            with self.subTest(workflow=name), self.assertRaisesRegex(AssertionError, "source priority"):
                checked_invocations(old, count)

    def test_host_environment_alone_cannot_fix_a_container_scan(self):
        for name, count, workflow in self.workflows():
            broken = copy.deepcopy(workflow)
            for job in broken["jobs"].values():
                for step in job.get("steps", []):
                    if isinstance(step.get("run"), str):
                        step["run"] = step["run"].replace("-e TRIVY_DB_REPOSITORY ", "")
            with self.subTest(workflow=name), self.assertRaisesRegex(AssertionError, "not passed"):
                checked_invocations(broken, count)

    def test_mirror_first_unapproved_repository_and_step_override_are_refused(self):
        for name, count, workflow in self.workflows():
            for value in (",".join(reversed(REPOSITORIES)),
                          "ghcr.io/unreviewed/trivy-db:2",
                          "ghcr.io/aquasecurity/trivy-db:latest"):
                broken = copy.deepcopy(workflow)
                broken["env"]["TRIVY_DB_REPOSITORY"] = value
                with self.subTest(workflow=name, source=value), self.assertRaisesRegex(AssertionError, "source priority"):
                    checked_invocations(broken, count)
            broken = copy.deepcopy(workflow)
            step = next(step for job in broken["jobs"].values() for step in job.get("steps", [])
                        if '"$TRIVY_IMAGE"' in step.get("run", ""))
            step.setdefault("env", {})["TRIVY_DB_REPOSITORY"] = ",".join(reversed(REPOSITORIES))
            with self.subTest(workflow=name, scope="step override"), self.assertRaisesRegex(AssertionError, "source priority"):
                checked_invocations(broken, count)

    def test_vulnerability_gate_still_fails_closed_without_skipping_database(self):
        for name, count, workflow in self.workflows():
            steps = [step for job in workflow["jobs"].values() for step in job.get("steps", [])
                     if step.get("name") == "Trivy nas imagens (HIGH e CRITICAL corrigíveis bloqueiam)"]
            with self.subTest(workflow=name):
                self.assertEqual(1, len(steps))
                step = steps[0]
                self.assertFalse(step.get("continue-on-error", False))
                for required in ("--severity HIGH,CRITICAL", "--ignore-unfixed",
                                 "--ignorefile /trivyignore", "--exit-code 1"):
                    self.assertIn(required, step["run"])
                broken = copy.deepcopy(workflow)
                for job in broken["jobs"].values():
                    for candidate in job.get("steps", []):
                        if candidate.get("name") == step["name"]:
                            candidate["run"] += "\n# disallowed mutation\n--skip-db-update\n"
                with self.assertRaisesRegex(AssertionError, "bypass"):
                    checked_invocations(broken, count)
