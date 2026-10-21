import hashlib
import io
import json
import tarfile
import tempfile
import unittest
from pathlib import Path
from typing import Any

import receiver as r

SHA = "a" * 40
DIGEST = "sha256:" + "b" * 64


class ValidationTests(unittest.TestCase):
    def release(self, project: Any = "stillon") -> Any:
        return {
            "version": 1,
            "project": project,
            "commit": SHA,
            "images": {"app": "ghcr.io/bipoxdxd/stillon@" + DIGEST},
        }

    def test_accepts_exact_project_release(self) -> None:
        self.assertEqual(r.validate_release(self.release(), "stillon", SHA)["commit"], SHA)

    def test_rejects_cross_project_mutable_refs_extra_fields(self) -> None:
        for key, value in [("project", "duora"), ("commit", "c" * 40), ("extra", "shell")]:
            data = self.release()
            data[key] = value
            with self.assertRaises(ValueError):
                r.validate_release(data, "stillon", SHA)
        for image in [
            "stillon:latest",
            "ghcr.io/attacker/stillon@" + DIGEST,
            "ghcr.io/bipoxdxd/stillon:main@" + DIGEST,
        ]:
            data = self.release()
            data["images"]["app"] = image
            with self.assertRaises(ValueError):
                r.validate_release(data, "stillon", SHA)

    def test_duplicate_json_keys_are_refused(self) -> None:
        with self.assertRaises(ValueError):
            r.json_unique('{"a":1,"a":2}')

    def archive(self, entries: Any) -> Any:
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w") as tar:
            for name, body, kind in entries:
                entry = tarfile.TarInfo(name)
                entry.type = kind
                entry.size = len(body)
                tar.addfile(entry, io.BytesIO(body))
        stream.seek(0)
        return stream

    def test_archive_rejects_traversal_links_duplicates_and_oversize(self) -> None:
        for entries in [
            [("../escape", b"x", tarfile.REGTYPE)],
            [("app/blobs/sha256/" + "a" * 64, b"x", tarfile.SYMTYPE)],
            [("release.json", b"{}", tarfile.REGTYPE)] * 2,
            [("release.json", b"x" * 101, tarfile.REGTYPE)],
        ]:
            with self.subTest(entries=entries), tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(ValueError):
                    r.unpack(self.archive(entries), Path(temp), {"app"}, limit=100)
                self.assertFalse((Path(temp).parent / "escape").exists())

    def test_archive_extracts_only_allowed_regular_files(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            data = b"{}"
            stream = self.archive([("release.json", data, tarfile.REGTYPE)])
            r.unpack(stream, Path(temp), {"app"})
            self.assertEqual((Path(temp) / "release.json").read_bytes(), data)

    def test_oci_binds_digest_and_platform_before_import(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "blobs/sha256").mkdir(parents=True)
            config = json.dumps({"os": "linux", "architecture": "amd64"}).encode()
            digest = "sha256:" + hashlib.sha256(config).hexdigest()
            (root / "blobs/sha256" / digest[7:]).write_bytes(config)
            document = json.dumps(
                {
                    "schemaVersion": 2,
                    "config": {"digest": digest, "size": len(config)},
                    "layers": [],
                }
            ).encode()
            image = "sha256:" + hashlib.sha256(document).hexdigest()
            (root / "blobs/sha256" / image[7:]).write_bytes(document)
            (root / "oci-layout").write_text('{"imageLayoutVersion":"1.0.0"}')
            index: dict[str, Any] = {
                "schemaVersion": 2,
                "manifests": [
                    {
                        "digest": image,
                        "size": len(document),
                        "mediaType": "application/vnd.oci.image.manifest.v1+json",
                    }
                ],
            }
            (root / "index.json").write_text(json.dumps(index))
            r.validate_oci(root, image)
            index["manifests"][0]["annotations"] = {"org.opencontainers.image.ref.name": "caddy:2"}
            (root / "index.json").write_text(json.dumps(index))
            with self.assertRaises(ValueError):
                r.validate_oci(root, image)
            del index["manifests"][0]["annotations"]
            (root / "index.json").write_text(json.dumps(index))
            (root / "blobs/sha256" / digest[7:]).write_bytes(b"corruption")
            with self.assertRaises(ValueError):
                r.validate_oci(root, image)

    def test_request_is_exact_not_a_shell(self) -> None:
        self.assertEqual(r.parse_request("release " + SHA), SHA)
        for value in [
            "release " + SHA + "\n",
            "release " + SHA + ";id",
            "backup",
            "release --help",
            "release " + "A" * 40,
        ]:
            with self.assertRaises(ValueError):
                r.parse_request(value)

    def test_failed_coolify_does_not_mark_release_success(self) -> None:
        for state in ["failed", "cancelled-by-user", "error"]:
            with self.assertRaises(ValueError):
                r.deployment_finished({"status": state})
        self.assertFalse(r.deployment_finished({"status": "in_progress"}))
        self.assertTrue(r.deployment_finished({"status": "finished"}))


class RuntimeTests(unittest.TestCase):
    def record(self, image: Any = DIGEST) -> Any:
        return {
            "Image": image,
            "State": {"Status": "running", "Health": {"Status": "healthy"}},
            "Config": {"Labels": {"coolify.applicationUuid": "resource"}},
        }

    def test_old_healthy_image_is_not_a_successful_new_release(self) -> None:
        r.validate_runtime(self.record(), "resource", DIGEST)
        with self.assertRaises(ValueError):
            r.validate_runtime(self.record("sha256:" + "c" * 64), "resource", DIGEST)

    def test_wrong_owner_and_unhealthy_container_are_refused(self) -> None:
        with self.assertRaises(ValueError):
            r.validate_runtime(self.record(), "other", DIGEST)
        data = self.record()
        data["State"]["Health"]["Status"] = "unhealthy"
        with self.assertRaises(ValueError):
            r.validate_runtime(data, "resource", DIGEST)


if __name__ == "__main__":
    unittest.main()
