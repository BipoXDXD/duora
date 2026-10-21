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
    def test_import_binds_runtime_id_for_classic_and_containerd_stores(self) -> None:
        for backend in ["classic", "containerd"]:
            with self.subTest(backend=backend), tempfile.TemporaryDirectory() as temp:
                stage = Path(temp)
                manifest, config_id = layout(stage / "app")
                runtime_id = manifest if backend == "containerd" else config_id
                operations: list[list[str]] = []

                def docker(
                    argv: list[str],
                    operations: Any = operations,
                    manifest: str = manifest,
                    runtime_id: str = runtime_id,
                    config_id: str = config_id,
                    backend: str = backend,
                ) -> bytes:
                    operations.append(argv)
                    if argv[:3] == ["/usr/bin/docker", "image", "inspect"]:
                        if argv[3] == config_id and backend == "containerd":
                            raise r.DockerOperationError(404)
                        if argv[3] == manifest and backend == "classic":
                            raise ValueError("classic store requires config ID lookup")
                        if argv[3] not in {
                            config_id,
                            manifest,
                            "ghcr.io/bipoxdxd/stillon:release-" + "a" * 40 + "@" + manifest,
                        }:
                            raise ValueError("inspect must bind the transported OCI graph")
                        return json.dumps(
                            [
                                {
                                    "Id": runtime_id,
                                    "Architecture": "amd64",
                                    "Os": "linux",
                                    "Descriptor": {"digest": manifest},
                                }
                            ]
                        ).encode()
                    return b""

                release = {
                    "project": "stillon",
                    "commit": "a" * 40,
                    "images": {"app": "ghcr.io/bipoxdxd/stillon@" + manifest},
                }
                with patch.object(r, "command", side_effect=docker):
                    refs, ids = r.import_images(release, stage)
                self.assertEqual(ids, {"app": runtime_id})
                self.assertEqual(
                    refs["app"], "ghcr.io/bipoxdxd/stillon:release-" + "a" * 40 + "@" + manifest
                )
                self.assertEqual(operations[-2][2], runtime_id)

    def test_docker_inspect_server_error_does_not_fall_back(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            stage = Path(temp)
            manifest, config_id = layout(stage / "app")

            def docker(argv: list[str]) -> bytes:
                if argv[1:3] == ["image", "inspect"]:
                    if argv[3] == config_id:
                        raise r.DockerOperationError(500)
                    return json.dumps(
                        [{"Id": manifest, "Architecture": "amd64", "Os": "linux"}]
                    ).encode()
                return b""

            release = {
                "project": "stillon",
                "commit": "a" * 40,
                "images": {"app": "ghcr.io/bipoxdxd/stillon@" + manifest},
            }
            with patch.object(r, "command", side_effect=docker), self.assertRaises(ValueError):
                r.import_images(release, stage)

    def test_import_refuses_unrelated_runtime_id_or_manifest_descriptor(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            stage = Path(temp)
            manifest, config_id = layout(stage / "app")
            for record in [
                {"Id": "sha256:" + "0" * 64, "Architecture": "amd64", "Os": "linux"},
                {
                    "Id": config_id,
                    "Architecture": "amd64",
                    "Os": "linux",
                    "Descriptor": {"digest": "sha256:" + "0" * 64},
                },
            ]:

                def docker(argv: list[str], record: Any = record) -> bytes:
                    return (
                        json.dumps([record]).encode() if argv[1:3] == ["image", "inspect"] else b""
                    )

                release = {
                    "project": "stillon",
                    "commit": "a" * 40,
                    "images": {"app": "ghcr.io/bipoxdxd/stillon@" + manifest},
                }
                with (
                    patch.object(r, "command", side_effect=docker),
                    self.assertRaisesRegex(ValueError, "identity"),
                ):
                    r.import_images(release, stage)

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

    def test_finished_coolify_waits_for_runtime_health_starting(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp)

            def api(path: str, method: str = "GET", payload: Any = None) -> Any:
                if path.startswith("deployments/applications/"):
                    return {"deployments": [], "count": 0}
                if path.endswith("/start"):
                    return {"deployment_uuid": "deployment"}
                return {"status": "finished"}

            def record(health: str) -> bytes:
                return json.dumps(
                    [
                        {
                            "Image": "sha256:" + "b" * 64,
                            "State": {"Status": "running", "Health": {"Status": health}},
                            "Config": {"Labels": {"coolify.applicationUuid": "app"}},
                        }
                    ]
                ).encode()

            release = {
                "project": "duora",
                "commit": "a" * 40,
                "images": {"edge": "repo@sha256:" + "b" * 64},
            }
            config = {
                "uuid": "app",
                "containers": ["runtime"],
                "image_roles": {"runtime": "edge"},
                "readiness": [],
            }
            with (
                patch.object(r, "api", side_effect=api),
                patch.object(r, "configure_duora"),
                patch.object(r, "command", side_effect=[record("starting"), record("healthy")]),
                patch("time.sleep", return_value=None),
            ):
                result = r.deploy(release, {}, config, state, {"edge": "sha256:" + "b" * 64})
            self.assertEqual(result, "deployment")
            self.assertTrue((state / "current.json").exists())
            self.assertFalse((state / "pending.json").exists())

    def test_unready_or_wrong_runtime_keeps_pending_receipt(self) -> None:
        for scenario in ["wrong-image", "wrong-owner", "unhealthy", "deadline"]:
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as temp:
                state = Path(temp)

                def api(path: str, method: str = "GET", payload: Any = None) -> Any:
                    if path.startswith("deployments/applications/"):
                        return {"deployments": [], "count": 0}
                    if path.endswith("/start"):
                        return {"deployment_uuid": "deployment"}
                    return {"status": "finished"}

                record = {
                    "Image": "sha256:" + ("c" if scenario == "wrong-image" else "b") * 64,
                    "State": {
                        "Status": "running",
                        "Health": {
                            "Status": "unhealthy" if scenario == "unhealthy" else "starting"
                        },
                    },
                    "Config": {
                        "Labels": {
                            "coolify.applicationUuid": "other"
                            if scenario == "wrong-owner"
                            else "app"
                        }
                    },
                }
                release = {
                    "project": "stillon",
                    "commit": "a" * 40,
                    "images": {"app": "repo@sha256:" + "b" * 64},
                }
                config = {
                    "uuid": "app",
                    "containers": ["runtime"],
                    "image_roles": {"runtime": "app"},
                    "readiness": [],
                }
                with (
                    patch.object(r, "api", side_effect=api),
                    patch.object(r, "command", return_value=json.dumps([record]).encode()),
                    patch(
                        "time.monotonic",
                        side_effect=[0, 0, 0, 121] if scenario == "deadline" else None,
                        return_value=0,
                    ),
                    patch(
                        "time.sleep",
                        side_effect=AssertionError(
                            "Identity and terminal health failures must not wait"
                        ),
                    ),
                    self.assertRaises(ValueError),
                ):
                    r.deploy(
                        release,
                        {"app": "immutable-image"},
                        config,
                        state,
                        {"app": "sha256:" + "b" * 64},
                    )
                self.assertTrue((state / "pending.json").exists())
                self.assertFalse((state / "current.json").exists())

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
