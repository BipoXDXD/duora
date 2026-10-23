"""A release must retain the reviewed database image or refuse before publishing."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import shutil
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


if __name__ == "__main__":
    unittest.main()
