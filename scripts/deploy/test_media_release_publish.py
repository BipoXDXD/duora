"""Publication trust boundaries, without Docker, credentials or network calls."""
from __future__ import annotations

import copy
import importlib
from pathlib import Path
import sys
import unittest
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/media"))
sys.path.insert(0, str(ROOT / "scripts/release"))
from check_ci import REQUIRED_JOBS

COMMIT = "a" * 40
REPOSITORY = "BipoXDXD/duora"
IMAGE = "ghcr.io/bipoxdxd/duora-media-sanitizer@"
DIGESTS = {"amd64": "sha256:" + "b" * 64, "arm64": "sha256:" + "c" * 64}


class MediaReleasePublicationTests(unittest.TestCase):
    def setUp(self):
        self.context: dict[str, Any] = {
            "event": "workflow_dispatch", "ref": "refs/heads/main", "workflowSha": COMMIT,
            "repository": REPOSITORY, "commit": COMMIT, "run": 101, "attempt": 2,
        }
        self.ci: dict[str, Any] = {
            "id": 99, "run_attempt": 1, "head_sha": COMMIT, "head_branch": "main", "event": "push",
            "path": ".github/workflows/ci.yml", "head_repository": {"full_name": REPOSITORY},
            "status": "completed", "conclusion": "success",
        }
        self.jobs = [{"name": name, "status": "completed", "conclusion": "success"}
                     for name in sorted(REQUIRED_JOBS)]
        self.manifests: dict[str, Any] = {
            arch: {"schema": 1, "scope": "synthetic-candidate", "commit": COMMIT, "architecture": arch,
                   "image": IMAGE + digest, "ci": {"run": 99, "attempt": 1},
                   "isolation": {"run": 101, "attempt": 2}, "publication": {"run": 101, "attempt": 2}}
            for arch, digest in DIGESTS.items()
        }
        self.index: dict[str, Any] = {
            "schemaVersion": 2, "mediaType": "application/vnd.oci.image.index.v1+json",
            "manifests": [{"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": digest,
                           "size": 1024, "platform": {"os": "linux", "architecture": arch}}
                          for arch, digest in DIGESTS.items()],
        }

    def publisher(self):
        self.assertTrue((ROOT / "scripts/media/release_publish.py").is_file(),
                        "Trusted S4 publication is not implemented")
        return importlib.import_module("release_publish")

    def validate_context(self):
        return self.publisher().validate_context(self.context, COMMIT, self.ci, self.jobs)

    def validate_pair(self):
        return self.publisher().validate_manifests(self.manifests, COMMIT,
                                                  {"run": 99, "attempt": 1}, {"run": 101, "attempt": 2})

    def test_only_exact_main_workflow_and_nine_successful_jobs_qualify(self):
        self.assertEqual({"run": 99, "attempt": 1}, self.validate_context())
        self.assertEqual(DIGESTS, self.validate_pair())
        self.publisher().validate_index(self.index, DIGESTS)

    def test_pr_dispatch_from_branch_fork_or_other_commit_is_refused(self):
        for field, value in [("event", "pull_request"), ("ref", "refs/heads/untrusted"),
                             ("repository", "attacker/duora"), ("workflowSha", "d" * 40),
                             ("commit", "d" * 40), ("run", True), ("attempt", 0)]:
            original = self.context[field]
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.context[field] = value
                self.validate_context()
            self.context[field] = original

    def test_missing_duplicate_extra_skipped_pending_or_failed_gate_is_refused(self):
        original = copy.deepcopy(self.jobs)
        for variant in [original[:-1], original + [original[0]],
                        original + [{"name": "unapproved", "status": "completed", "conclusion": "success"}],
                        [{**original[0], "conclusion": "skipped"}] + original[1:],
                        [{**original[0], "conclusion": "failure"}] + original[1:],
                        [{**original[0], "status": "in_progress", "conclusion": None}] + original[1:]]:
            with self.subTest(jobs=variant), self.assertRaises(ValueError):
                self.jobs = variant
                self.validate_context()

    def test_foreign_old_attempt_or_non_main_ci_is_refused(self):
        for field, value in [("head_sha", "d" * 40), ("head_branch", "branch"), ("event", "pull_request"),
                             ("path", ".github/workflows/unapproved.yml"), ("head_repository", {"full_name": "evil/duora"}),
                             ("conclusion", "failure"), ("status", "in_progress"), ("run_attempt", True), ("id", 0)]:
            original = self.ci[field]
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.ci[field] = value
                self.validate_context()
            self.ci[field] = original

    def test_both_proofs_must_be_from_this_commit_ci_and_publication_attempt(self):
        for field, value in [("commit", "d" * 40), ("architecture", "amd64"), ("scope", "host-candidate"),
                             ("ci", {"run": 99, "attempt": 2}), ("isolation", {"run": 100, "attempt": 2}),
                             ("publication", {"run": 101, "attempt": 1}), ("schema", True)]:
            original = self.manifests["arm64"][field]
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.manifests["arm64"][field] = value
                self.validate_pair()
            self.manifests["arm64"][field] = original
        del self.manifests["arm64"]
        with self.assertRaises(ValueError):
            self.validate_pair()

    def test_foreign_mutable_or_same_native_digest_is_refused(self):
        for image in ["duora:latest", "ghcr.io/attacker/image@" + DIGESTS["arm64"],
                      IMAGE + DIGESTS["amd64"]]:
            with self.subTest(image=image), self.assertRaises(ValueError):
                self.manifests["arm64"]["image"] = image
                self.validate_pair()

    def test_release_index_cannot_replace_add_duplicate_or_omit_native_images(self):
        original = copy.deepcopy(self.index)
        for change in [lambda value: value["manifests"].pop(),
                       lambda value: value["manifests"].append(value["manifests"][0]),
                       lambda value: value["manifests"][0].update(digest="sha256:" + "f" * 64),
                       lambda value: value["manifests"][0]["platform"].update(os="windows"),
                       lambda value: value["manifests"][0]["platform"].update(architecture="arm64"),
                       lambda value: value.update(schemaVersion=True)]:
            index = copy.deepcopy(original)
            change(index)
            with self.subTest(index=index), self.assertRaises(ValueError):
                self.publisher().validate_index(index, DIGESTS)

    def test_native_manifest_and_config_must_match_scanned_image_and_architecture(self):
        native = {"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
                  "config": {"digest": "sha256:" + "e" * 64}}
        config = {"architecture": "amd64", "os": "linux"}
        self.publisher().validate_native(native, config, "amd64", "sha256:" + "e" * 64)
        for changed_native, changed_config, expected in [
                (native, {**config, "architecture": "arm64"}, "sha256:" + "e" * 64),
                (native, config, "sha256:" + "f" * 64),
                ({"manifests": []}, config, "sha256:" + "e" * 64)]:
            with self.assertRaises(ValueError):
                self.publisher().validate_native(changed_native, changed_config, "amd64", expected)
