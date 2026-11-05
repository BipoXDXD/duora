"""Component qualification runs reviewed main code; the candidate contributes only inert build inputs."""
from __future__ import annotations

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from typing import Any
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/qualify-postgres.yml"
ACTION = ROOT / ".github/actions/postgres-component/action.yml"
CANDIDATE = ".postgres-candidate"
PINNED_ACTION = re.compile(r"^[\w./-]+@[0-9a-f]{40}$")


class QualifyPostgresWorkflowTests(unittest.TestCase):
    workflow: dict[Any, Any]
    job: dict[str, Any]
    steps: list[dict[str, Any]]

    @classmethod
    def setUpClass(cls) -> None:
        cls.workflow = yaml.safe_load(WORKFLOW.read_text())
        cls.job = cls.workflow["jobs"]["component"]
        cls.steps = cls.job["steps"]

    def step(self, needle: str) -> dict[str, Any]:
        found = [step for step in self.steps if needle in step.get("run", "")]
        self.assertEqual(len(found), 1, needle)
        return found[0]

    def index(self, needle: str) -> int:
        return self.steps.index(self.step(needle))

    def scripts(self) -> list[str]:
        return [step["run"] for job in self.workflow["jobs"].values() for step in job["steps"] if "run" in step]

    def test_only_manual_dispatch_with_the_two_reviewed_inputs_starts_it(self) -> None:
        triggers = self.workflow[True]
        self.assertEqual(set(triggers), {"workflow_dispatch"})
        inputs = triggers["workflow_dispatch"]["inputs"]
        self.assertEqual(set(inputs), {"candidate_commit", "mode"})
        self.assertTrue(inputs["candidate_commit"]["required"])
        self.assertEqual(inputs["mode"]["type"], "choice")
        self.assertEqual(inputs["mode"]["options"], ["qualify", "renew"])

    def test_dispatch_outside_main_or_without_a_full_sha_fails_before_any_build(self) -> None:
        guard = "\n".join(step.get("run", "") for step in self.workflow["jobs"]["verify"]["steps"])
        self.assertIn('test "$GITHUB_REF" = refs/heads/main', guard)
        self.assertIn('[[ "$CANDIDATE_COMMIT" =~ ^[0-9a-f]{40}$ ]]', guard)
        self.assertEqual(self.job["needs"], "verify")
        self.assertNotIn("if", self.job, "a skipped job would still leave the run successful")

    def test_only_the_publishing_job_can_write_packages(self) -> None:
        self.assertEqual(self.workflow["permissions"], {"contents": "read"})
        self.assertNotIn("permissions", self.workflow["jobs"]["verify"])
        self.assertEqual(self.job["permissions"], {"contents": "read", "packages": "write"})

    def test_native_matrix_matches_the_release_runners(self) -> None:
        publish = yaml.safe_load((ROOT / ".github/workflows/publish-images.yml").read_text())
        self.assertEqual(self.job["strategy"]["matrix"]["include"],
                         publish["jobs"]["images"]["strategy"]["matrix"]["include"])
        self.assertIs(self.job["strategy"]["fail-fast"], False)

    def test_every_action_is_pinned_by_full_commit_sha(self) -> None:
        uses = [step["uses"] for job in self.workflow["jobs"].values() for step in job["steps"] if "uses" in step]
        self.assertTrue(uses)
        for action in uses:
            with self.subTest(action=action):
                self.assertRegex(action, PINNED_ACTION)

    def test_candidate_checkout_is_sparse_exact_and_keeps_no_credential(self) -> None:
        candidate = [step for step in self.steps if step.get("with", {}).get("path") == CANDIDATE]
        self.assertEqual(len(candidate), 1)
        options = candidate[0]["with"]
        self.assertEqual(options["ref"], "${{ inputs.candidate_commit }}")
        self.assertIs(options["persist-credentials"], False)
        self.assertIs(options["sparse-checkout-cone-mode"], False)
        self.assertEqual(options["sparse-checkout"].split(), ["/.dockerignore", "/infra/images/postgres/"])
        self.assertEqual(options["fetch-depth"], 1)

    def test_main_checkout_comes_first_and_keeps_no_credential(self) -> None:
        checkout = self.steps[0]
        self.assertIn("actions/checkout", checkout["uses"])
        self.assertEqual(checkout["with"], {"persist-credentials": False})

    def test_candidate_files_run_only_through_the_build_and_its_image(self) -> None:
        for script in self.scripts():
            self.assertNotRegex(script, rf"(?:^|\s)(?:\./|bash |sh |python3? ){re.escape(CANDIDATE)}")
            self.assertNotIn("source " + CANDIDATE, script)
        for step in self.steps:
            self.assertFalse(step.get("uses", "").startswith("./" + CANDIDATE))
            if step.get("working-directory") == CANDIDATE:
                self.assertIn("docker buildx build", step["run"])

    def test_dispatch_inputs_never_reach_a_shell_as_text(self) -> None:
        for script in self.scripts():
            self.assertNotIn("${{", script)

    def test_build_avoids_the_index_the_transport_refuses(self) -> None:
        build = self.step("docker buildx build")
        self.assertEqual(build["if"], "${{ inputs.mode == 'qualify' }}")
        self.assertEqual(build["working-directory"], CANDIDATE)
        for flag in ("--provenance=false", "--sbom=false", "--load", "--tag duora-postgres:ci"):
            self.assertIn(flag, build["run"])

    def test_token_is_not_visible_to_the_build(self) -> None:
        self.assertNotIn("env", self.step("docker buildx build"))
        self.assertNotIn("GH_TOKEN", self.job.get("env", {}))
        self.assertNotIn("GH_TOKEN", self.workflow.get("env", {}))

    def test_recipe_is_validated_before_the_build_and_renewal_pull(self) -> None:
        recipe = self.index("postgres_component.py recipe --root " + CANDIDATE)
        self.assertLess(recipe, self.index("docker buildx build"))
        self.assertLess(recipe, self.index("docker pull"))

    def test_qualify_pushes_one_manifest_and_never_a_mutable_or_index_reference(self) -> None:
        push = self.step("docker push")
        self.assertEqual(push["if"], "${{ inputs.mode == 'qualify' }}")
        self.assertIn('qualify-$CANDIDATE_COMMIT-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT-$ARCH', push["run"])
        self.assertIn('has("manifests") | not', push["run"])
        self.assertLess(self.index("docker buildx build"), self.steps.index(push))
        self.assertNotIn("latest", push["run"])

    def test_renewal_only_pulls_the_locked_digest_of_the_locked_commit(self) -> None:
        pull = self.step("docker pull")
        self.assertEqual(pull["if"], "${{ inputs.mode == 'renew' }}")
        script = pull["run"]
        self.assertIn("postgres_component.py select --root " + CANDIDATE, script)
        self.assertIn('test "$(jq -er .source_commit', script)
        self.assertIn("postgres_component.py verify-image", script)
        self.assertNotIn("docker push", script)
        self.assertNotIn("buildx", script)

    def test_registry_login_ends_with_the_push(self) -> None:
        script = self.step("docker push")["run"]
        self.assertIn('trap \'docker logout ghcr.io', script)
        self.assertIn('--password-stdin', script)

    def test_renewal_pulls_the_public_digest_without_a_credential(self) -> None:
        pull = self.step("docker pull")
        self.assertNotIn("env", pull)
        self.assertNotIn("docker login", pull["run"])
        self.assertNotIn("GH_TOKEN", pull["run"])

    def test_every_gate_runs_in_both_modes_on_the_selected_image(self) -> None:
        for needle in ("pgbackrest version", "restore_drill.py --image duora-postgres:ci",
                       "--format cyclonedx", "postgres_component.py publication"):
            with self.subTest(gate=needle):
                self.assertNotIn("if", self.step(needle))

    def test_gates_run_after_the_image_exists_and_before_the_publication(self) -> None:
        order = [self.index(needle) for needle in (
            "docker push", "pgbackrest version", "restore_drill.py --image")]
        order += [self.steps.index(step) for step in self.gates()]
        order += [self.index(needle) for needle in ("--format cyclonedx", "postgres_component.py publication")]
        self.assertEqual(order, sorted(order))
        upload = next(step for step in self.steps if "upload-artifact" in step.get("uses", ""))
        self.assertLess(order[-1], self.steps.index(upload))

    def test_privilege_selection_is_only_the_class_without_application_images(self) -> None:
        step = self.step("pgbackrest version")
        self.assertEqual(step["env"]["DUORA_POSTGRES_IMAGE"], "duora-postgres:ci")
        selected = re.findall(r"test_db_privileges\.(\w+)", step["run"])
        self.assertEqual(selected, ["DatabaseObserverImageTests"])
        self.assertNotIn("discover", step["run"])
        tests = (ROOT / "scripts/deploy/test_db_privileges.py").read_text()
        body = tests.split("class DatabaseObserverImageTests", 1)[1].split("\n@unittest", 1)[0]
        self.assertNotIn("apply_candidate_sql", body)
        self.assertNotIn("duora-migrator:ci", body)

    def gates(self) -> list[dict[str, Any]]:
        return [step for step in self.steps if "--exit-code 1" in step.get("run", "")]

    def test_each_mode_has_exactly_one_blocking_scanner_gate(self) -> None:
        gates = self.gates()
        self.assertEqual([gate["if"] for gate in gates],
                         ["${{ inputs.mode == 'qualify' }}", "${{ inputs.mode == 'renew' }}"])
        for gate in gates:
            for required in ("--severity HIGH,CRITICAL", "--ignore-unfixed", "duora-postgres:ci"):
                self.assertIn(required, gate["run"])
            self.assertNotIn("continue-on-error", gate)

    def test_qualification_scan_cannot_see_any_exception_file(self) -> None:
        qualify = self.gates()[0]["run"]
        self.assertNotIn("trivyignore", qualify)
        self.assertNotIn("--ignorefile", qualify)

    def test_renewal_reads_exceptions_only_from_the_reviewed_main_checkout(self) -> None:
        renew = self.gates()[1]["run"]
        self.assertIn('-v "$GITHUB_WORKSPACE/.trivyignore:/trivyignore:ro"', renew)
        self.assertIn("--ignorefile /trivyignore", renew)
        self.assertEqual(len(re.findall(r"\.trivyignore:", renew)), 1)
        mentions = [script for script in self.scripts() if "trivyignore" in script]
        self.assertEqual(mentions, [self.gates()[1]["run"]])
        for script in self.scripts():
            self.assertNotRegex(script, re.escape(CANDIDATE) + r"\S*trivyignore")

    def test_publication_binds_the_candidate_commit_and_this_exact_attempt(self) -> None:
        step = self.step("postgres_component.py publication")
        self.assertIn('--commit "$CANDIDATE_COMMIT"', step["run"])
        self.assertIn("runs/$GITHUB_RUN_ID/attempts/$GITHUB_RUN_ATTEMPT", step["run"])
        self.assertEqual(step["env"]["COMPONENT_IMAGE"],
                         "${{ steps.candidate.outputs.image || steps.locked.outputs.image }}")

    def test_retained_artifact_has_the_name_the_selector_requires(self) -> None:
        upload = next(step for step in self.steps if "upload-artifact" in step.get("uses", ""))
        self.assertEqual(upload["with"]["name"], "release-${{ matrix.arch }}-${{ github.run_attempt }}")
        self.assertEqual(upload["with"]["retention-days"], 90)
        self.assertEqual(upload["with"]["if-no-files-found"], "error")
        self.assertEqual(upload["with"]["path"], "${{ runner.temp }}/release/")

    def test_no_secret_reaches_the_workflow(self) -> None:
        self.assertNotIn("secrets.", WORKFLOW.read_text())


FAKE_GH = """#!/bin/bash
printf '%s\\n' "$*" >> "$GH_LOG"
case "$2" in
  *compare/*...refs/heads/main) status=$MAIN_STATUS ;;
  *compare/*) status=$CHECKOUT_STATUS ;;
  *artifacts*) exec cat "$GH_ARTIFACTS" ;;
  *) exec cat "$GH_RUN" ;;
esac
[ "$status" != api-failure ] || exit 1
printf '%s\\n' "$status"
"""


class ComponentActionHistoryTests(unittest.TestCase):
    """Run the real step script; only gh is fake, so the old path proves it never reaches compare."""

    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.repo = self.root / "repo"
        (self.repo / "scripts/release").mkdir(parents=True)
        (self.repo / "infra/images").mkdir(parents=True)
        shutil.copy(ROOT / "scripts/release/postgres_component.py", self.repo / "scripts/release")
        subprocess.run(["git", "init", "--quiet", str(self.repo)], check=True, timeout=10)
        subprocess.run(["git", "-C", str(self.repo), "-c", "user.name=t", "-c", "user.email=t@example.com",
                        "commit", "--quiet", "--allow-empty", "--message", "fixture"], check=True, timeout=10)
        self.checkout_head = subprocess.check_output(["git", "-C", str(self.repo), "rev-parse", "HEAD"],
                                                     text=True, timeout=10).strip()
        self.source = "c" * 40
        (self.repo / "infra/images/postgres-component-lock.json").write_text(json.dumps({
            "version": 1, "profile": "postgres-embedded-reconcile-v1", "fingerprint": "sha256:" + "f" * 64,
            "source_commit": self.source,
            "publish_run": "https://github.com/BipoXDXD/duora/actions/runs/123/attempts/1",
            "images": {"amd64": "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "b" * 64,
                       "arm64": "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "c" * 64}}))
        tools = self.root / "bin"
        tools.mkdir()
        (tools / "gh").write_text(FAKE_GH)
        (tools / "gh").chmod(0o755)
        self.temp = self.root / "runner"
        (self.temp / "postgres-component").mkdir(parents=True)
        self.output = self.root / "output"
        self.output.write_text("")
        self.log = self.root / "gh.log"
        self.log.write_text("")
        self.tools = tools
        self.main_head = "a" * 40

    def run_step(self, *, path: str, main_status: str = "ahead", checkout_status: str = "ahead"
                 ) -> subprocess.CompletedProcess[str]:
        qualification = path.endswith("qualify-postgres.yml")
        head = self.main_head if qualification else self.source
        (self.root / "run.json").write_text(json.dumps({
            "id": 123, "run_attempt": 1, "head_sha": head, "head_branch": "main", "path": path,
            "event": "workflow_dispatch", "status": "completed", "conclusion": "success",
            "repository": {"full_name": "BipoXDXD/duora"},
            "head_repository": {"full_name": "BipoXDXD/duora"}}))
        (self.root / "artifacts.json").write_text(json.dumps({"total_count": 1, "artifacts": [{
            "id": 77, "name": "release-amd64-1", "expired": False,
            "workflow_run": {"id": 123, "head_sha": head}}]}))
        action = yaml.safe_load(ACTION.read_text())
        step = next(item for item in action["runs"]["steps"] if item.get("id") == "metadata")
        self.assertIn("SOURCE_COMMIT", step["env"])
        environment = {"PATH": str(self.tools) + os.pathsep + os.environ["PATH"], "HOME": str(self.root),
                       "RUNNER_TEMP": str(self.temp), "GITHUB_OUTPUT": str(self.output),
                       "COMPONENT_ARCH": "amd64", "SOURCE_RUN": "123", "SOURCE_ATTEMPT": "1",
                       "SOURCE_COMMIT": self.source, "GH_LOG": str(self.log),
                       "GH_RUN": str(self.root / "run.json"), "GH_ARTIFACTS": str(self.root / "artifacts.json"),
                       "MAIN_STATUS": main_status, "CHECKOUT_STATUS": checkout_status}
        return subprocess.run(["bash", "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", step["run"]],
                              cwd=self.repo, env=environment, capture_output=True, text=True, check=False,
                              timeout=30)

    def compares(self) -> list[str]:
        return [line for line in self.log.read_text().splitlines() if "/compare/" in line]

    def test_release_paths_never_reach_the_history_comparison(self) -> None:
        for path in (".github/workflows/coolify.yml", ".github/workflows/publish-images.yml"):
            result = self.run_step(path=path, main_status="diverged", checkout_status="diverged")
            with self.subTest(path=path):
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.compares(), [])
                self.assertIn("artifact_id=77", self.output.read_text())

    def test_qualification_compares_its_head_with_the_main_branch_and_the_source_with_the_checkout(self) -> None:
        result = self.run_step(path=".github/workflows/qualify-postgres.yml")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.compares(), [
            f"api repos/BipoXDXD/duora/compare/{self.main_head}...refs/heads/main --jq .status",
            f"api repos/BipoXDXD/duora/compare/{self.source}...{self.checkout_head} --jq .status"])
        self.assertIn("artifact_id=77", self.output.read_text())

    def test_identical_history_is_accepted_on_both_comparisons(self) -> None:
        result = self.run_step(path=".github/workflows/qualify-postgres.yml", main_status="identical",
                               checkout_status="identical")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_a_run_outside_the_main_branch_history_yields_no_artifact(self) -> None:
        for status in ("behind", "diverged", "", "api-failure"):
            result = self.run_step(path=".github/workflows/qualify-postgres.yml", main_status=status)
            with self.subTest(status=status):
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn("artifact_id", self.output.read_text())

    def test_a_source_outside_the_checkout_history_yields_no_artifact(self) -> None:
        for status in ("behind", "diverged", "", "api-failure"):
            result = self.run_step(path=".github/workflows/qualify-postgres.yml", checkout_status=status)
            with self.subTest(status=status):
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn("artifact_id", self.output.read_text())


if __name__ == "__main__":
    unittest.main()
