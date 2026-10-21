"""Synthetic OCI identities, expansion limits and distinct Docker storage filesystems."""

import gzip
import hashlib
import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path
from typing import Any
from unittest.mock import patch

import receiver as r


def layout(
    root: Path,
    layer: bytes = b"",
    media: str = "application/vnd.oci.image.layer.v1.tar",
) -> tuple[str, str]:
    blobs = root / "blobs/sha256"
    blobs.mkdir(parents=True)

    def blob(data: bytes) -> dict[str, Any]:
        digest = "sha256:" + hashlib.sha256(data).hexdigest()
        (blobs / digest[7:]).write_bytes(data)
        return {"digest": digest, "size": len(data)}

    config = blob(b'{"os":"linux","architecture":"amd64"}')
    descriptor = blob(
        json.dumps(
            {
                "schemaVersion": 2,
                "config": config,
                "layers": [{**blob(layer), "mediaType": media}],
            }
        ).encode()
    )
    (root / "oci-layout").write_text('{"imageLayoutVersion":"1.0.0"}')
    (root / "index.json").write_text(json.dumps({"schemaVersion": 2, "manifests": [descriptor]}))
    return descriptor["digest"], config["digest"]


class ImageStorageTests(unittest.TestCase):
    def test_import_inspects_config_id_and_keeps_manifest_reference(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            stage = Path(temp)
            manifest, config_id = layout(stage / "app")
            operations = []

            def docker(argv: list[str]) -> bytes:
                operations.append(argv)
                if argv[:3] == ["/usr/bin/docker", "image", "inspect"]:
                    if (
                        argv[3] != config_id
                        and argv[3]
                        != "ghcr.io/bipoxdxd/stillon:release-" + "a" * 40 + "@" + manifest
                    ):
                        raise ValueError("manifest is not a Docker config ID")
                    return json.dumps(
                        [{"Id": config_id, "Architecture": "amd64", "Os": "linux"}]
                    ).encode()
                return b""

            release = {
                "project": "stillon",
                "commit": "a" * 40,
                "images": {"app": "ghcr.io/bipoxdxd/stillon@" + manifest},
            }
            with patch.object(r, "command", side_effect=docker):
                refs, ids = r.import_images(release, stage)
            self.assertEqual(ids, {"app": config_id})
            self.assertEqual(
                refs["app"],
                "ghcr.io/bipoxdxd/stillon:release-" + "a" * 40 + "@" + manifest,
            )
            self.assertEqual(operations[-2][2], config_id)

    def test_unresolvable_manifest_reference_refuses_before_coolify(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            stage = Path(temp)
            manifest, config_id = layout(stage / "app")

            def docker(argv: list[str]) -> bytes:
                if argv[:3] == ["/usr/bin/docker", "image", "inspect"]:
                    if "@" in argv[3]:
                        raise ValueError("immutable reference unavailable locally")
                    return json.dumps(
                        [{"Id": config_id, "Architecture": "amd64", "Os": "linux"}]
                    ).encode()
                return b""

            release = {
                "project": "stillon",
                "commit": "a" * 40,
                "images": {"app": "ghcr.io/bipoxdxd/stillon@" + manifest},
            }
            with (
                patch.object(r, "command", side_effect=docker),
                self.assertRaisesRegex(ValueError, "immutable reference"),
            ):
                r.import_images(release, stage)

    def test_gzip_expansion_is_bounded_before_docker_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest, _ = layout(
                root,
                gzip.compress(b"x" * 4096),
                "application/vnd.oci.image.layer.v1.tar+gzip",
            )
            with (
                patch.object(r, "EXPANDED_LIMIT", 1024, create=True),
                self.assertRaisesRegex(ValueError, "[Ee]xpand"),
            ):
                r.validate_oci(root, manifest)

    def test_all_roles_share_expansion_budget_before_any_import(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            stage = Path(temp)
            images = {}
            for role, repo in r.PROJECTS["goblinshift"].items():
                manifest, _ = layout(stage / role, b"x" * 6)
                images[role] = repo + "@" + manifest
            release = {"project": "goblinshift", "commit": "a" * 40, "images": images}

            def forbidden(argv: list[str]) -> bytes:
                self.fail("Docker mutation before aggregate admission")

            with (
                patch.object(r, "EXPANDED_LIMIT", 10),
                patch.object(r, "command", side_effect=forbidden),
                self.assertRaisesRegex(ValueError, "Expanded release"),
            ):
                r.import_images(release, stage)

    def test_docker_contract_refuses_prune_volume_removal_and_service_stop(self) -> None:
        for argv in [
            ["/usr/bin/docker", "system", "prune", "-af"],
            ["/usr/bin/docker", "volume", "rm", "database"],
            ["/usr/bin/docker", "stop", "other-project"],
            ["/usr/bin/docker", "compose", "down", "-v"],
        ]:
            with (
                self.subTest(argv=argv),
                self.assertRaisesRegex(ValueError, "outside release contract"),
            ):
                r.command(argv)

    def test_unknown_compression_is_refused(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest, _ = layout(root, b"opaque", "application/vnd.oci.image.layer.v1.tar+zstd")
            with self.assertRaises(ValueError):
                r.validate_oci(root, manifest)

    def test_low_docker_filesystem_refuses_before_input_or_import(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp)
            docker_root = state / "docker"
            docker_root.mkdir()

            (state / "storage.json").write_text(
                json.dumps({"version": 1, "image_roots": [str(docker_root)]})
            )
            original_stat = Path.stat

            def stat(path: Path, *args: Any, **kwargs: Any) -> Any:
                result = original_stat(path, *args, **kwargs)
                if path == docker_root:
                    values = list(result)
                    values[2] += 1  # Simulate a separate Docker filesystem.
                    return os.stat_result(values)
                return result

            def disk(path: Any) -> Any:
                return type(
                    "Disk",
                    (),
                    {"free": 1 if Path(path) == docker_root else 100 * 1024**3},
                )()

            with (
                patch.object(r, "BASE", state),
                patch.object(Path, "stat", stat),
                patch.object(
                    r,
                    "docker_request",
                    return_value=json.dumps({"DockerRootDir": str(docker_root)}).encode(),
                ),
                patch.object(shutil, "disk_usage", side_effect=disk),
                self.assertRaises(ValueError),
            ):
                r.require_storage(state)

    def test_same_filesystem_accounts_for_staging_and_expansion_together(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "storage.json").write_text(json.dumps({"version": 1, "image_roots": [temp]}))
            with (
                patch.object(r, "BASE", root),
                patch.object(
                    r,
                    "docker_request",
                    return_value=json.dumps({"DockerRootDir": temp}).encode(),
                ),
                patch.object(
                    shutil,
                    "disk_usage",
                    return_value=type("Disk", (), {"free": 24 * 1024**3})(),
                ),
                self.assertRaises(ValueError),
            ):
                r.require_storage(root)

    def test_receiver_identity_is_bound_to_request(self) -> None:
        data = {
            "version": 2,
            "project": "stillon",
            "commit": "a" * 40,
            "receiver_sha256": "0" * 64,
            "images": {"app": "ghcr.io/bipoxdxd/stillon@sha256:" + "b" * 64},
        }
        with self.assertRaisesRegex(ValueError, "[Rr]eceiver"):
            r.validate_release(data, "stillon", "a" * 40)


if __name__ == "__main__":
    unittest.main()
