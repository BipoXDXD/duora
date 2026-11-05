"""A release must retain the reviewed database image or refuse before publishing."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from typing import Any
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/release"))
import postgres_component as component


class PostgresComponentTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.runtime = self.root / "infra/images/postgres"
        shutil.copytree(ROOT / "infra/images/postgres", self.runtime)
        shutil.copyfile(ROOT / ".dockerignore", self.root / ".dockerignore")

    def lock(self) -> dict[str, Any]:
        return {
            "version": 1,
            "profile": "postgres-embedded-reconcile-v1",
            "fingerprint": component.fingerprint_inputs(self.root),
            "source_commit": "8d7c3c756a74cd4f4d8c2f93f9f171c1ffd81362",
            "publish_run": "https://github.com/BipoXDXD/duora/actions/runs/39880434067/attempts/1",
            "images": {
                "amd64": "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "b" * 64,
                "arm64": "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "c" * 64,
            },
        }

    def test_same_runtime_uses_exact_reviewed_native_digest(self) -> None:
        lock = self.lock()
        self.assertEqual(component.select_image(self.root, lock, "amd64"),
                         "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "b" * 64)
        self.assertEqual(component.select_image(self.root, lock, "arm64"),
                         "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "c" * 64)

    def test_application_and_documentation_changes_do_not_change_selection(self) -> None:
        lock = self.lock()
        (self.root / "README.md").write_text("A new application release")
        (self.root / "backend").mkdir()
        (self.root / "backend/App.cs").write_text("application changed")
        self.assertEqual(component.select_image(self.root, lock, "amd64"), lock["images"]["amd64"])

    def test_each_runtime_file_change_requires_a_reviewed_component_update(self) -> None:
        for source in sorted(self.runtime.rglob("*")):
            if not source.is_file():
                continue
            lock = self.lock()
            original = source.read_bytes()
            with self.subTest(path=source.relative_to(self.root)):
                try:
                    source.write_bytes(original + b"\n# changed runtime input\n")
                    with self.assertRaises(ValueError):
                        component.select_image(self.root, lock, "amd64")
                finally:
                    source.write_bytes(original)

    def test_added_or_removed_runtime_input_refuses_reuse(self) -> None:
        lock = self.lock()
        extra = self.runtime / "new-input.conf"
        extra.write_text("new input")
        with self.assertRaises(ValueError):
            component.select_image(self.root, lock, "amd64")
        extra.unlink()
        (self.runtime / "pgbackrest.conf").unlink()
        with self.assertRaises(ValueError):
            component.select_image(self.root, lock, "amd64")

    def test_symlink_file_or_directory_is_not_a_runtime_input(self) -> None:
        lock = self.lock()
        outside = self.root / "outside"
        outside.mkdir()
        (outside / "config").write_text("outside")
        for target in [outside / "config", outside]:
            link = self.runtime / "unexpected-link"
            link.symlink_to(target, target_is_directory=target.is_dir())
            try:
                with self.assertRaises(ValueError):
                    component.select_image(self.root, lock, "amd64")
            finally:
                link.unlink()

    def test_lock_outside_runtime_directory_does_not_hash_itself(self) -> None:
        lock = self.lock()
        path = self.root / "infra/images/postgres-component-lock.json"
        path.write_text(json.dumps(lock))
        self.assertEqual(component.select_image(self.root, component.read_lock(path), "amd64"),
                         lock["images"]["amd64"])

    def test_unsupported_architecture_is_refused(self) -> None:
        lock = self.lock()
        for architecture in ["", "x86_64", "linux/amd64", "s390x"]:
            with self.subTest(architecture=architecture), self.assertRaises(ValueError):
                component.select_image(self.root, lock, architecture)

    def test_both_exact_architectures_are_mandatory(self) -> None:
        for transform in [lambda images: images.pop("arm64"),
                          lambda images: images.update(extra="ignored")]:
            lock = self.lock()
            transform(lock["images"])
            with self.assertRaises(ValueError):
                component.select_image(self.root, lock, "amd64")

    def test_mutable_foreign_or_malformed_digest_is_refused(self) -> None:
        for reference in ["ghcr.io/bipoxdxd/duora-postgres:latest",
                          "ghcr.io/attacker/postgres@sha256:" + "b" * 64,
                          "ghcr.io/bipoxdxd/duora-postgres:tag@sha256:" + "b" * 64,
                          "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "B" * 64,
                          "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "b" * 63]:
            lock = self.lock()
            lock["images"]["amd64"] = reference
            with self.subTest(reference=reference), self.assertRaises(ValueError):
                component.select_image(self.root, lock, "amd64")

    def test_metadata_cannot_change_version_profile_or_trusted_origin(self) -> None:
        for field, value in [("version", True), ("version", 2), ("profile", "unreviewed"),
                             ("source_commit", "main"), ("source_commit", "B" * 40),
                             ("publish_run", "https://github.com/attacker/repo/actions/runs/1/attempts/1"),
                             ("publish_run", "https://github.com/BipoXDXD/duora/actions/runs/0/attempts/1"),
                             ("fingerprint", "b" * 64)]:
            lock = self.lock()
            lock[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                component.select_image(self.root, lock, "amd64")

    def test_unknown_and_missing_lock_fields_are_refused(self) -> None:
        valid = self.lock()
        for field in valid:
            lock = copy.deepcopy(valid)
            del lock[field]
            with self.subTest(missing=field), self.assertRaises(ValueError):
                component.select_image(self.root, lock, "amd64")
        lock = valid | {"fallback": "build"}
        with self.assertRaises(ValueError):
            component.select_image(self.root, lock, "amd64")

    def test_duplicate_json_keys_are_refused_including_nested_images(self) -> None:
        path = self.root / "lock.json"
        for document in ['{"version":1,"version":1}',
                         '{"images":{"amd64":"one","amd64":"two"}}']:
            path.write_text(document)
            with self.assertRaises(ValueError):
                component.read_lock(path)

    def test_executable_mode_change_invalidates_the_component(self) -> None:
        lock = self.lock()
        path = self.runtime / "pg_hba.duora.conf"
        path.chmod(path.stat().st_mode | 0o100)
        with self.assertRaises(ValueError):
            component.select_image(self.root, lock, "amd64")

    def test_copy_outside_the_closed_input_tree_cannot_be_reviewed_as_complete(self) -> None:
        path = self.runtime / "Dockerfile"
        with path.open("a") as handle:
            handle.write("\nCOPY backend/uninventoried.sql /usr/local/share/duora/extra.sql\n")
        lock = self.lock()
        with self.assertRaises(ValueError):
            component.select_image(self.root, lock, "amd64")

    def test_mutable_base_and_unreviewed_build_arguments_refuse_selection(self) -> None:
        path = self.runtime / "Dockerfile"
        original = path.read_text()
        changed = [original.replace("postgres:18.6-trixie@sha256:" +
                                    "74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336",
                                    "postgres:latest"),
                   original + "\nARG UNREVIEWED_CONTENT\n"]
        for recipe in changed:
            path.write_text(recipe)
            lock = self.lock()
            with self.subTest(recipe=recipe[-100:]), self.assertRaises(ValueError):
                component.select_image(self.root, lock, "amd64")

    def origin(self) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
        lock = self.lock()
        run: dict[str, Any] = {
            "id": 39880434067, "run_attempt": 1,
            "head_sha": lock["source_commit"], "path": ".github/workflows/coolify.yml",
            "event": "workflow_run", "status": "completed", "conclusion": "success",
            "repository": {"full_name": "BipoXDXD/duora"},
            "head_repository": {"full_name": "BipoXDXD/duora"},
        }
        publication: dict[str, Any] = {
            "commit": lock["source_commit"], "architecture": "amd64",
            "publishRun": lock["publish_run"],
            "ciRun": "https://github.com/BipoXDXD/duora/actions/runs/39879009709/attempts/1",
            "images": {role: "ghcr.io/bipoxdxd/duora-" + role + "@sha256:" + "b" * 64
                       for role in ["web", "worker", "migrator", "edge", "postgres"]},
        }
        return lock, run, publication

    def test_authenticated_run_and_native_artifact_must_match_the_lock(self) -> None:
        lock, run, publication = self.origin()
        component.validate_origin(lock, run, publication, "amd64")

    def test_foreign_failed_forked_or_wrong_attempt_origin_is_refused(self) -> None:
        lock, run, publication = self.origin()
        for field, value in [("id", 1), ("run_attempt", 2), ("head_sha", "a" * 40),
                             ("path", ".github/workflows/other.yml"), ("event", "pull_request"),
                             ("status", "in_progress"), ("conclusion", "failure"),
                             ("repository", {"full_name": "attacker/duora"}),
                             ("head_repository", {"full_name": "attacker/duora"})]:
            changed = copy.deepcopy(run)
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                component.validate_origin(lock, changed, publication, "amd64")

    def test_artifact_cannot_substitute_commit_architecture_run_ci_or_digest(self) -> None:
        lock, run, publication = self.origin()
        for field, value in [("commit", "a" * 40), ("architecture", "arm64"),
                             ("publishRun", lock["publish_run"].replace("attempts/1", "attempts/2")),
                             ("ciRun", "https://github.com/attacker/duora/actions/runs/1/attempts/1"),
                             ("images", publication["images"] | {"postgres":
                              "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "d" * 64})]:
            changed = copy.deepcopy(publication)
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                component.validate_origin(lock, run, changed, "amd64")

    def local_image(self, lock: dict[str, Any]) -> dict[str, Any]:
        return {"Id": "sha256:" + "d" * 64, "Os": "linux", "Architecture": "amd64",
                "RepoDigests": [lock["images"]["amd64"]],
                "Config": {"Labels": {"org.opencontainers.image.source":
                                       "https://github.com/BipoXDXD/duora"}}}

    def test_pulled_native_image_retains_reviewed_repository_digest(self) -> None:
        lock = self.lock()
        component.validate_local_image(lock, self.local_image(lock), "amd64")

    def test_wrong_pulled_platform_source_or_manifest_is_refused(self) -> None:
        lock = self.lock()
        image = self.local_image(lock)
        for field, value in [("Architecture", "arm64"), ("Os", "windows"),
                             ("RepoDigests", ["ghcr.io/bipoxdxd/duora-postgres@sha256:" + "e" * 64]),
                             ("Config", {"Labels": {"org.opencontainers.image.source":
                                                    "https://github.com/attacker/duora"}})]:
            changed = copy.deepcopy(image)
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                component.validate_local_image(lock, changed, "amd64")

    def cli(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run([sys.executable, str(ROOT / "scripts/release/postgres_component.py"),
                               *arguments], capture_output=True, text=True, check=False, timeout=10)

    def test_cli_selects_exact_component_and_scoped_proof_artifact(self) -> None:
        path = self.root / "lock.json"
        lock = self.lock()
        path.write_text(json.dumps(lock))
        result = self.cli("select", "--root", str(self.root), "--lock", str(path),
                          "--architecture", "amd64")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(result.stdout.strip(), "selection must return a verifiable component plan")
        selected = json.loads(result.stdout)
        self.assertEqual(selected, {"image": lock["images"]["amd64"], "run_id": 39880434067,
                                    "attempt": 1, "artifact": "release-amd64-1",
                                    "source_commit": lock["source_commit"]})

    def test_cli_refuses_drift_without_returning_a_publication_plan(self) -> None:
        path = self.root / "lock.json"
        path.write_text(json.dumps(self.lock()))
        (self.runtime / "pgbackrest.conf").write_text("changed runtime")
        result = self.cli("select", "--root", str(self.root), "--lock", str(path),
                          "--architecture", "amd64")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")

    def test_cli_cannot_verify_a_failed_publication_origin(self) -> None:
        lock, run, publication = self.origin()
        run["conclusion"] = "failure"
        for name, document in [("lock", lock), ("run", run), ("publication", publication)]:
            (self.root / (name + ".json")).write_text(json.dumps(document))
        result = self.cli("verify-origin", "--lock", str(self.root / "lock.json"),
                          "--run-json", str(self.root / "run.json"),
                          "--publication-json", str(self.root / "publication.json"),
                          "--architecture", "amd64")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")

    def test_build_context_policy_change_requires_component_review(self) -> None:
        lock = self.lock()
        (self.root / ".dockerignore").write_text("*\n")
        with self.assertRaises(ValueError):
            component.select_image(self.root, lock, "amd64")

    def artifacts(self) -> dict[str, Any]:
        return {"total_count": 1, "artifacts": [{
            "id": 12470357181, "name": "release-amd64-1", "expired": False,
            "workflow_run": {"id": 39880434067,
                             "head_sha": "8d7c3c756a74cd4f4d8c2f93f9f171c1ffd81362"},
        }]}

    def test_proof_artifact_from_exact_run_is_selected(self) -> None:
        self.assertEqual(component.select_proof_artifact(self.lock(), self.artifacts(), "amd64"),
                         12470357181)

    def test_missing_expired_duplicate_or_incomplete_proof_is_refused(self) -> None:
        for transform in [lambda response: response.update(total_count=2),
                          lambda response: response.update(total_count=0, artifacts=[]),
                          lambda response: response["artifacts"][0].update(expired=True),
                          lambda response: response["artifacts"][0].update(name="release-amd64-2"),
                          lambda response: response.update(total_count=2,
                                                           artifacts=response["artifacts"] * 2)]:
            inventory = self.artifacts()
            transform(inventory)
            with self.subTest(inventory=inventory), self.assertRaises(ValueError):
                component.select_proof_artifact(self.lock(), inventory, "amd64")

    def test_proof_artifact_metadata_cannot_substitute_source_or_run(self) -> None:
        for field, value in [("id", 1), ("head_sha", "a" * 40)]:
            inventory = self.artifacts()
            inventory["artifacts"][0]["workflow_run"][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                component.select_proof_artifact(self.lock(), inventory, "amd64")


    def qualification(self) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
        lock = self.lock()
        run: dict[str, Any] = {
            "id": 39880434067, "run_attempt": 1, "head_sha": "a" * 40, "head_branch": "main",
            "path": ".github/workflows/qualify-postgres.yml",
            "event": "workflow_dispatch", "status": "completed", "conclusion": "success",
            "repository": {"full_name": "BipoXDXD/duora"},
            "head_repository": {"full_name": "BipoXDXD/duora"},
        }
        publication: dict[str, Any] = {
            "commit": lock["source_commit"], "architecture": "amd64",
            "publishRun": lock["publish_run"], "images": {"postgres": lock["images"]["amd64"]},
        }
        return lock, run, publication

    def test_main_qualification_binds_the_lock_through_the_publication_commit(self) -> None:
        lock, run, publication = self.qualification()
        self.assertNotEqual(run["head_sha"], lock["source_commit"])
        component.validate_origin(lock, run, publication, "amd64")

    def test_qualification_renewal_of_the_same_commit_is_accepted(self) -> None:
        lock, run, publication = self.qualification()
        run["head_sha"] = lock["source_commit"]
        component.validate_origin(lock, run, publication, "amd64")

    def test_qualification_outside_a_successful_main_dispatch_is_refused(self) -> None:
        lock, run, publication = self.qualification()
        for field, value in [("head_branch", "ci/candidate"), ("head_branch", None),
                             ("event", "workflow_run"), ("event", "push"),
                             ("head_sha", "main"), ("head_sha", None),
                             ("id", 1), ("run_attempt", 2), ("status", "in_progress"),
                             ("conclusion", "failure"),
                             ("repository", {"full_name": "attacker/duora"}),
                             ("head_repository", {"full_name": "attacker/duora"})]:
            changed = copy.deepcopy(run)
            changed[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                component.validate_origin(lock, changed, publication, "amd64")

    def test_qualification_publication_holds_only_the_exact_postgres_digest(self) -> None:
        lock, run, publication = self.qualification()
        other = "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "d" * 64
        web = {"web": "ghcr.io/bipoxdxd/duora-web@sha256:" + "d" * 64}
        for field, value in [("commit", "a" * 40), ("architecture", "arm64"),
                             ("publishRun", lock["publish_run"].replace("attempts/1", "attempts/2")),
                             ("images", {"postgres": other}), ("images", {}),
                             ("images", publication["images"] | web), ("images", ["postgres"]),
                             ("ciRun", "https://github.com/BipoXDXD/duora/actions/runs/1/attempts/1")]:
            changed = copy.deepcopy(publication)
            changed[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                component.validate_origin(lock, run, changed, "amd64")

    def test_release_contract_still_requires_commit_identity_and_all_five_roles(self) -> None:
        lock, run, _ = self.origin()
        _, _, component_publication = self.qualification()
        with self.assertRaises(ValueError):
            component.validate_origin(lock, run, component_publication, "amd64")
        moved = dict(run, head_sha="a" * 40, head_branch="main",
                     path=".github/workflows/publish-images.yml", event="workflow_dispatch")
        with self.assertRaises(ValueError):
            component.validate_origin(lock, moved, self.origin()[2], "amd64")

    def test_qualification_run_cannot_carry_a_full_release_publication(self) -> None:
        lock, run, _ = self.qualification()
        with self.assertRaises(ValueError):
            component.validate_origin(lock, run, self.origin()[2], "amd64")

    def qualification_artifacts(self) -> dict[str, Any]:
        inventory = self.artifacts()
        inventory["artifacts"][0]["workflow_run"]["head_sha"] = "a" * 40
        return inventory

    def test_proof_from_main_qualification_is_selected_with_authenticated_run(self) -> None:
        lock, run, _ = self.qualification()
        self.assertEqual(component.select_proof_artifact(lock, self.qualification_artifacts(), "amd64", run),
                         12470357181)

    def test_proof_from_main_qualification_needs_the_authenticated_run(self) -> None:
        with self.assertRaises(ValueError):
            component.select_proof_artifact(self.lock(), self.qualification_artifacts(), "amd64")

    def test_proof_head_must_match_the_run_that_authenticated_it(self) -> None:
        lock, run, _ = self.qualification()
        for head in ["b" * 40, None]:
            inventory = self.qualification_artifacts()
            inventory["artifacts"][0]["workflow_run"]["head_sha"] = head
            with self.subTest(head=head), self.assertRaises(ValueError):
                component.select_proof_artifact(lock, inventory, "amd64", run)

    def test_run_metadata_cannot_relax_the_proof_of_a_foreign_or_unsuccessful_run(self) -> None:
        lock, run, _ = self.qualification()
        for field, value in [("id", 1), ("run_attempt", 2), ("head_branch", "ci/candidate"),
                             ("event", "push"), ("conclusion", "failure"), ("head_sha", None)]:
            changed = copy.deepcopy(run)
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                component.select_proof_artifact(lock, self.qualification_artifacts(), "amd64", changed)

    def test_release_run_metadata_keeps_the_source_commit_comparison(self) -> None:
        lock, run, _ = self.origin()
        self.assertEqual(component.select_proof_artifact(lock, self.artifacts(), "amd64", run), 12470357181)
        with self.assertRaises(ValueError):
            component.select_proof_artifact(lock, self.qualification_artifacts(), "amd64",
                                            dict(run, head_sha="a" * 40))

    def test_recipe_reports_the_fingerprint_of_valid_candidate_inputs(self) -> None:
        self.assertEqual(component.inspect_recipe(self.root), component.fingerprint_inputs(self.root))

    def test_recipe_refuses_inputs_outside_the_reviewed_contract(self) -> None:
        with (self.runtime / "Dockerfile").open("a") as handle:
            handle.write("\nARG UNREVIEWED_CONTENT\n")
        with self.assertRaises(ValueError):
            component.inspect_recipe(self.root)

    def test_written_publication_satisfies_the_origin_contract(self) -> None:
        lock, run, _ = self.qualification()
        publication = component.qualification_publication(
            lock["source_commit"], "amd64", lock["publish_run"], lock["images"]["amd64"])
        component.validate_origin(lock, run, publication, "amd64")

    def test_malformed_publication_is_never_written(self) -> None:
        lock, _, _ = self.qualification()
        valid = (lock["source_commit"], "amd64", lock["publish_run"], lock["images"]["amd64"])
        for position, value in [(0, "main"), (0, "B" * 40), (1, "x86_64"),
                                (2, "https://github.com/attacker/repo/actions/runs/1/attempts/1"),
                                (3, "ghcr.io/bipoxdxd/duora-postgres:latest"),
                                (3, "ghcr.io/bipoxdxd/duora-web@sha256:" + "b" * 64)]:
            arguments = list(valid)
            arguments[position] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                component.qualification_publication(*arguments)

    def test_cli_verifies_the_qualification_contract_from_authenticated_documents(self) -> None:
        lock, run, publication = self.qualification()
        for name, document in [("lock", lock), ("run", run), ("publication", publication),
                               ("inventory", self.qualification_artifacts())]:
            (self.root / (name + ".json")).write_text(json.dumps(document))
        origin = self.cli("verify-origin", "--lock", str(self.root / "lock.json"),
                          "--run-json", str(self.root / "run.json"),
                          "--publication-json", str(self.root / "publication.json"),
                          "--architecture", "amd64")
        self.assertEqual((origin.returncode, origin.stdout), (0, "verified\n"), origin.stderr)
        proof = ["select-proof", "--lock", str(self.root / "lock.json"), "--architecture", "amd64",
                 "--inventory-json", str(self.root / "inventory.json")]
        self.assertNotEqual(self.cli(*proof).returncode, 0)
        selected = self.cli(*proof, "--run-json", str(self.root / "run.json"))
        self.assertEqual((selected.returncode, selected.stdout), (0, "12470357181\n"), selected.stderr)

    def test_cli_reports_recipe_without_a_lock(self) -> None:
        result = self.cli("recipe", "--root", str(self.root))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout),
                         {"fingerprint": component.fingerprint_inputs(self.root)})

    def test_cli_writes_publication_without_a_lock(self) -> None:
        lock, run, _ = self.qualification()
        result = self.cli("publication", "--architecture", "amd64", "--commit", lock["source_commit"],
                          "--publish-run", lock["publish_run"], "--image", lock["images"]["amd64"])
        self.assertEqual(result.returncode, 0, result.stderr)
        component.validate_origin(lock, run, json.loads(result.stdout), "amd64")

    def test_cli_refuses_incomplete_arguments_for_each_operation(self) -> None:
        for arguments in [("select",), ("select", "--lock", "unused.json"),
                          ("select", "--architecture", "amd64"),
                          ("publication", "--architecture", "amd64"),
                          ("publication", "--commit", "a" * 40)]:
            result = self.cli(*arguments)
            with self.subTest(arguments=arguments):
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")


    def test_only_a_commit_inside_the_compared_history_is_accepted(self) -> None:
        for status in ["ahead", "identical"]:
            component.require_ancestor(status)
        for status in ["behind", "diverged", "", "AHEAD", "ahead\n", None]:
            with self.subTest(status=status), self.assertRaises(ValueError):
                component.require_ancestor(status)

    def test_qualification_head_comes_only_from_the_exact_qualification_run(self) -> None:
        lock, run, _ = self.qualification()
        self.assertEqual(component.qualification_head(lock, run), "a" * 40)
        with self.assertRaises(ValueError):
            component.qualification_head(lock, dict(run, head_branch="ci/candidate"))

    def test_release_runs_have_no_qualification_head_to_compare(self) -> None:
        lock, run, _ = self.origin()
        self.assertIsNone(component.qualification_head(lock, run))

    def test_dockerfile_parser_directives_are_refused(self) -> None:
        path = self.runtime / "Dockerfile"
        original = path.read_text()
        for directive in ["# syntax=docker/dockerfile:1", "#syntax=attacker/frontend", "# SYNTAX = x",
                          "# escape=`", "#\tEscape = `", "# check=skip=all", "#   ChEcK=error=true"]:
            path.write_text(directive + "\n" + original)
            with self.subTest(directive=directive), self.assertRaisesRegex(ValueError, "parser directive"):
                component.validate_recipe(self.root)

    def test_ordinary_comments_mentioning_the_same_words_are_accepted(self) -> None:
        path = self.runtime / "Dockerfile"
        path.write_text("# syntax highlighting note\n# check the escape rules\n" + path.read_text())
        component.validate_recipe(self.root)

    def test_cli_exposes_the_history_decisions(self) -> None:
        lock, run, _ = self.qualification()
        (self.root / "lock.json").write_text(json.dumps(lock))
        (self.root / "run.json").write_text(json.dumps(run))
        head = self.cli("qualification-head", "--lock", str(self.root / "lock.json"),
                        "--run-json", str(self.root / "run.json"))
        self.assertEqual((head.returncode, head.stdout), (0, "a" * 40 + "\n"), head.stderr)
        self.assertEqual(self.cli("verify-ancestor", "--compare-status", "ahead").returncode, 0)
        refused = self.cli("verify-ancestor", "--compare-status", "diverged")
        self.assertNotEqual(refused.returncode, 0)
        self.assertEqual(refused.stdout, "")

    def test_cli_prints_no_head_for_a_release_run(self) -> None:
        lock, run, _ = self.origin()
        (self.root / "lock.json").write_text(json.dumps(lock))
        (self.root / "run.json").write_text(json.dumps(run))
        head = self.cli("qualification-head", "--lock", str(self.root / "lock.json"),
                        "--run-json", str(self.root / "run.json"))
        self.assertEqual((head.returncode, head.stdout), (0, ""), head.stderr)

    def test_cli_refuses_history_operations_without_their_inputs(self) -> None:
        for arguments in [("verify-ancestor",), ("qualification-head",),
                          ("qualification-head", "--lock", "unused.json")]:
            result = self.cli(*arguments)
            with self.subTest(arguments=arguments):
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")


if __name__ == "__main__":
    unittest.main()
