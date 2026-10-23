"""Synthetic daemon boundary tests; real docker cp proof remains a native CI gate."""
import copy
import json
import unittest

import reconcile_payload as payload
from test_reconcile_payload import archive

REVISION = "a" * 40
REFERENCE = "ghcr.io/bipoxdxd/duora-migrator@sha256:" + "b" * 64
IMAGE_ID = "sha256:" + "c" * 64
CONTAINER_ID = "d" * 64
SQL = b"SELECT 1;\n"


class Daemon:
    def __init__(self) -> None:
        self.commands: list[list[str]] = []
        self.image = {"Id": IMAGE_ID, "RepoDigests": [REFERENCE], "Os": "linux", "Architecture": "amd64",
                      "Config": {"Volumes": None, "Labels": {
                          "org.opencontainers.image.revision": REVISION,
                          "org.opencontainers.image.source": "https://github.com/BipoXDXD/duora",
                          "org.opencontainers.image.title": "duora-migrator"}}}
        self.container: dict = {}
        self.create_failure = False
        self.foreign_container = False
        self.running_container = False
        self.cp_failure = False
        self.rm_failure = False
        self.corrupt_checksum = False
        self.create_output: bytes | None = None

    def __call__(self, command: list[str]) -> bytes:
        self.commands.append(command)
        if command[:3] == ["docker", "image", "inspect"]:
            return json.dumps(self.image).encode()
        if command[:2] == ["docker", "create"]:
            labels = {}
            for index, part in enumerate(command):
                if part == "--label":
                    key, value = command[index + 1].split("=", 1)
                    labels[key] = value
            self.container = {"Id": CONTAINER_ID, "Image": IMAGE_ID,
                              "Name": "/" + command[command.index("--name") + 1],
                              "State": {"Running": self.running_container, "Status": "running" if self.running_container else "created"},
                              "Mounts": [], "Config": {"Labels": labels}, "HostConfig": {"NetworkMode": "none"}}
            if self.foreign_container:
                self.container["Config"]["Labels"] = {}
            if self.create_failure:
                raise payload.PayloadError("ambiguous create timeout")
            return self.create_output if self.create_output is not None else (CONTAINER_ID + "\n").encode()
        if command[:3] == ["docker", "container", "inspect"]:
            return json.dumps(self.container).encode()
        if command[:2] == ["docker", "cp"]:
            if self.cp_failure:
                raise payload.PayloadError("interrupted copy")
            if payload.CHECKSUM_NAME in command[-2]:
                import hashlib
                checksum = hashlib.sha256(SQL).hexdigest().encode() + b"  reconcile-privileges.sql\n"
                return archive(payload.CHECKSUM_NAME, b"invalid" if self.corrupt_checksum else checksum)
            return archive(payload.SQL_NAME, SQL)
        if command[:2] == ["docker", "rm"]:
            if self.rm_failure:
                raise payload.PayloadError("cleanup failed")
            return (CONTAINER_ID + "\n").encode()
        raise AssertionError(f"Unexpected daemon operation: {command}")

    def extract(self) -> str:
        return payload.extract_sql(REFERENCE, revision=REVISION, architecture="amd64", capture=self)


class ExtractionTests(unittest.TestCase):
    def test_valid_payload_is_copied_from_an_owned_stopped_isolated_container(self) -> None:
        daemon = Daemon()
        self.assertEqual(daemon.extract(), SQL.decode())
        create = next(c for c in daemon.commands if c[:2] == ["docker", "create"])
        self.assertEqual(create[create.index("--network") + 1], "none")
        self.assertEqual(create[-1], REFERENCE)
        self.assertIn("--name", create)
        self.assertIn("--label", create)
        copies = [c for c in daemon.commands if c[:2] == ["docker", "cp"]]
        self.assertEqual(len(copies), 2)
        self.assertTrue(all(c[-1] == "-" for c in copies))
        self.assertTrue(any(c[:2] == ["docker", "rm"] for c in daemon.commands))
        forbidden = {"start", "run", "exec", "restart", "prune", "--force", "-f", "--volume", "-v", "--mount", "--env", "-e"}
        self.assertFalse(any(forbidden.intersection(c) for c in daemon.commands))

    def test_image_volumes_are_refused_before_create(self) -> None:
        for volumes in [{"/data": {}}, [], "invalid"]:
            daemon = Daemon()
            daemon.image["Config"]["Volumes"] = volumes
            with self.subTest(volumes=volumes), self.assertRaises(payload.PayloadError):
                daemon.extract()
            self.assertFalse(any(c[:2] == ["docker", "create"] for c in daemon.commands))

    def test_image_identity_revision_platform_and_role_are_bound_before_create(self) -> None:
        baseline = Daemon().image
        variants = []
        for key, value in [("RepoDigests", []), ("Architecture", "arm64"), ("Os", "windows"), ("Id", "invalid")]:
            image = copy.deepcopy(baseline)
            image[key] = value
            variants.append(image)
        for key in ["revision", "source", "title"]:
            image = copy.deepcopy(baseline)
            image["Config"]["Labels"]["org.opencontainers.image." + key] = "foreign"
            variants.append(image)
        for image in variants:
            daemon = Daemon()
            daemon.image = image
            with self.subTest(image=image), self.assertRaises(payload.PayloadError):
                daemon.extract()
            self.assertFalse(any(c[:2] == ["docker", "create"] for c in daemon.commands))

    def test_mutable_or_foreign_reference_cannot_reach_the_daemon(self) -> None:
        for reference in ["ghcr.io/bipoxdxd/duora-migrator:latest", REFERENCE.replace("duora-migrator", "duora-web"),
                          REFERENCE.replace("bipoxdxd", "foreign")]:
            daemon = Daemon()
            with self.subTest(reference=reference), self.assertRaises(payload.PayloadError):
                payload.extract_sql(reference, revision=REVISION, architecture="amd64", capture=daemon)
            self.assertEqual(daemon.commands, [])

    def test_copy_failure_and_bad_checksum_still_remove_only_owned_container(self) -> None:
        for mode in ["cp_failure", "corrupt_checksum"]:
            daemon = Daemon()
            setattr(daemon, mode, True)
            with self.subTest(mode=mode), self.assertRaises(payload.PayloadError):
                daemon.extract()
            removal = [c for c in daemon.commands if c[:2] == ["docker", "rm"]]
            self.assertEqual(len(removal), 1)
            self.assertIn(CONTAINER_ID, removal[0])
            self.assertNotIn("--force", removal[0])

    def test_ambiguous_create_failure_inspects_and_cleans_the_owned_nonce(self) -> None:
        daemon = Daemon()
        daemon.create_failure = True
        with self.assertRaises(payload.PayloadError):
            daemon.extract()
        self.assertTrue(any(c[:3] == ["docker", "container", "inspect"] for c in daemon.commands))
        self.assertTrue(any(c[:2] == ["docker", "rm"] for c in daemon.commands))
        self.assertFalse(any(c[:2] == ["docker", "cp"] for c in daemon.commands))

    def test_foreign_or_running_container_is_never_copied_or_removed(self) -> None:
        for mode in ["foreign_container", "running_container"]:
            daemon = Daemon()
            setattr(daemon, mode, True)
            with self.subTest(mode=mode), self.assertRaises(payload.PayloadError):
                daemon.extract()
            self.assertFalse(any(c[:2] in [["docker", "cp"], ["docker", "rm"]] for c in daemon.commands))

    def test_cleanup_failure_does_not_return_successful_sql(self) -> None:
        daemon = Daemon()
        daemon.rm_failure = True
        with self.assertRaises(payload.PayloadError):
            daemon.extract()

    def test_truncated_create_identity_still_cleans_the_owned_container(self) -> None:
        for output in [b"truncated\n", b"", b"\xff"]:
            daemon = Daemon()
            daemon.create_output = output
            with self.subTest(output=output):
                with self.assertRaises(payload.PayloadError):
                    daemon.extract()
                self.assertFalse(any(c[:2] == ["docker", "cp"] for c in daemon.commands))
                removal = [c for c in daemon.commands if c[:2] == ["docker", "rm"]]
                self.assertEqual(len(removal), 1)
                self.assertEqual(removal[0][-1], CONTAINER_ID)

    def test_truncated_create_identity_does_not_remove_a_foreign_container(self) -> None:
        daemon = Daemon()
        daemon.create_output = b"truncated\n"
        daemon.foreign_container = True
        with self.assertRaises(payload.PayloadError):
            daemon.extract()
        self.assertFalse(any(c[:2] in [["docker", "cp"], ["docker", "rm"]] for c in daemon.commands))


if __name__ == "__main__":
    unittest.main()
