"""A component-only publication reaches the receiver without pretending the sender saw five roles."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile
from typing import Any
import unittest
from unittest.mock import patch

import postgres_component as component
import qualify_postgres_transport as transport

ROOT = Path(__file__).resolve().parents[2]
SOURCE_COMMIT = "8d7c3c756a74cd4f4d8c2f93f9f171c1ffd81362"
PUBLISH_RUN = "https://github.com/BipoXDXD/duora/actions/runs/39880434067/attempts/1"
IMAGE_ID = "sha256:" + "d" * 64


def write_blob(blobs: Path, document: dict[str, Any]) -> str:
    data = json.dumps(document).encode()
    name = hashlib.sha256(data).hexdigest()
    (blobs / name).write_bytes(data)
    return "sha256:" + name


class ComponentTransportTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        self.commands: list[list[str]] = []
        self.sent = patch.object(transport.send, "requested", side_effect=self.requested)
        self.requested_calls = self.sent.start()
        self.addCleanup(self.sent.stop)
        self.imported = patch.object(transport.receiver, "import_images", side_effect=self.import_images)
        self.import_calls = self.imported.start()
        self.addCleanup(self.imported.stop)
        self.validated = patch.object(transport.receiver, "validate_oci", side_effect=self.validate_oci)
        self.validated.start()
        self.addCleanup(self.validated.stop)
        self.run_command = patch.object(transport, "command", side_effect=self.command)
        self.run_command.start()
        self.addCleanup(self.run_command.stop)
        environment = patch.dict(os.environ, {"GITHUB_RUN_ID": "1"})
        environment.start()
        self.addCleanup(environment.stop)
        self.config_digest = ""
        self.manifest_digest = ""

    def native_graph(self, architecture: str) -> str:
        """Fix the manifest digest up front, since the lock has to name it before the transport runs."""
        self.graph = self.directory / ("graph-" + architecture)
        blobs = self.graph / "blobs/sha256"
        blobs.mkdir(parents=True)
        self.config_digest = write_blob(blobs, {"os": "linux", "architecture": architecture})
        self.manifest_digest = write_blob(blobs, {"config": {"digest": self.config_digest}})
        (self.graph / "index.json").write_text(json.dumps({"schemaVersion": 2, "manifests": [
            {"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": self.manifest_digest}]}))
        (self.graph / "oci-layout").write_text('{"imageLayoutVersion":"1.0.0"}')
        return component.IMAGE + self.manifest_digest[7:]

    def lock(self, architecture: str) -> dict[str, Any]:
        reference = self.native_graph(architecture)
        images = {"amd64": component.IMAGE + "b" * 64, "arm64": component.IMAGE + "c" * 64}
        images[architecture] = reference
        return {"version": 1, "profile": component.PROFILE,
                "fingerprint": component.fingerprint_inputs(ROOT), "source_commit": SOURCE_COMMIT,
                "publish_run": PUBLISH_RUN, "images": images}

    def command(self, arguments: list[str]) -> str:
        self.commands.append(arguments)
        if arguments[:3] == ["docker", "image", "inspect"]:
            architecture = "arm64" if "arm64" in str(self.graph) else "amd64"
            return json.dumps({"Id": IMAGE_ID, "Os": "linux", "Architecture": architecture,
                               "RepoDigests": [arguments[3]],
                               "Config": {"Labels": {"org.opencontainers.image.source":
                                                     "https://github.com/BipoXDXD/duora"}}})
        if arguments[0] == "crane":
            os.rename(self.graph, arguments[-1])
            return ""
        if arguments[:2] == ["docker", "version"]:
            return "{}"
        return "a" * 40 + "\n"

    def validate_oci(self, _layout: Path, _digest: str) -> tuple[str, int]:
        return self.config_digest, 1

    def import_images(self, release: dict[str, Any], _stage: Path) -> tuple[dict[str, str], dict[str, str]]:
        self.assertEqual(set(release["images"]), {"postgres"})
        return dict(release["images"]), {"postgres": IMAGE_ID}

    def requested(self, project: str, commit: str) -> dict[str, str]:
        self.assertEqual((project, commit), ("duora", SOURCE_COMMIT))
        return json.loads(Path(os.environ["PUBLICATION_FILE"]).read_text())["images"]

    def documents(self, lock: dict[str, Any], architecture: str, *, release: bool) -> argparse.Namespace:
        reference = lock["images"][architecture]
        if release:
            run: dict[str, Any] = {"head_sha": SOURCE_COMMIT, "path": ".github/workflows/publish-images.yml",
                                   "event": "workflow_dispatch"}
            roles = {role: "ghcr.io/bipoxdxd/duora-" + role + "@sha256:" + "e" * 64
                     for role in ("web", "worker", "migrator", "edge")}
            publication: dict[str, Any] = {
                "ciRun": "https://github.com/BipoXDXD/duora/actions/runs/2/attempts/1",
                "images": roles | {"postgres": reference}}
        else:
            run = {"head_sha": "a" * 40, "head_branch": "main", "event": "workflow_dispatch",
                   "path": component.QUALIFICATION_WORKFLOW}
            publication = {"images": {"postgres": reference}}
        run |= {"id": 39880434067, "run_attempt": 1, "status": "completed", "conclusion": "success",
                "repository": {"full_name": "BipoXDXD/duora"},
                "head_repository": {"full_name": "BipoXDXD/duora"}}
        publication |= {"commit": SOURCE_COMMIT, "architecture": architecture, "publishRun": PUBLISH_RUN}
        paths = {name: self.directory / (name + ".json") for name in ("lock", "publication", "run")}
        for name, document in [("lock", lock), ("publication", publication), ("run", run)]:
            paths[name].write_text(json.dumps(document))
        return argparse.Namespace(architecture=architecture, lock=paths["lock"],
                                  publication=paths["publication"], run_json=paths["run"],
                                  stage=self.directory / "stage", artifact_id=7)

    def test_component_publication_reaches_the_receiver_without_the_five_role_sender(self) -> None:
        lock = self.lock("amd64")
        evidence = transport.qualify(self.documents(lock, "amd64", release=False))
        self.requested_calls.assert_not_called()
        self.import_calls.assert_called_once()
        self.assertEqual(evidence["receiver_imported_reference"], lock["images"]["amd64"])
        self.assertEqual(evidence["component_publication_reference"], lock["images"]["amd64"])
        self.assertIs(evidence["receiver_import_verified"], True)

    def test_component_evidence_never_claims_a_sender_selection(self) -> None:
        evidence = transport.qualify(self.documents(self.lock("amd64"), "amd64", release=False))
        self.assertIs(evidence["receiver_import_and_sender_selection_verified"], False)
        self.assertNotIn("sender_requested_reference", evidence)
        self.assertIn("send.requested", evidence["sender_scope"])

    def test_component_reference_is_compared_with_the_selected_digest(self) -> None:
        lock = self.lock("amd64")
        arguments = self.documents(lock, "amd64", release=False)
        publication = json.loads(arguments.publication.read_text())
        publication["images"]["postgres"] = component.IMAGE + "f" * 64
        arguments.publication.write_text(json.dumps(publication))
        with patch.object(component, "validate_origin"), self.assertRaisesRegex(ValueError, "publication changed"):
            transport.qualify(arguments)

    def test_release_publication_still_goes_through_the_actual_sender(self) -> None:
        lock = self.lock("amd64")
        arguments = self.documents(lock, "amd64", release=True)
        evidence = transport.qualify(arguments)
        self.requested_calls.assert_called_once_with("duora", SOURCE_COMMIT)
        self.assertEqual(os.environ["PUBLICATION_FILE"], str(arguments.publication))
        self.assertEqual(evidence["sender_requested_reference"], lock["images"]["amd64"])
        self.assertIs(evidence["receiver_import_and_sender_selection_verified"], True)
        self.assertNotIn("sender_scope", evidence)

    def test_release_sender_disagreement_is_still_refused(self) -> None:
        lock = self.lock("amd64")
        self.requested_calls.side_effect = None
        self.requested_calls.return_value = {"postgres": component.IMAGE + "f" * 64}
        with self.assertRaisesRegex(ValueError, "sender selection changed"):
            transport.qualify(self.documents(lock, "amd64", release=True))

    def test_arm64_component_stays_native_only(self) -> None:
        evidence = transport.qualify(self.documents(self.lock("arm64"), "arm64", release=False))
        self.requested_calls.assert_not_called()
        self.import_calls.assert_not_called()
        self.assertIs(evidence["receiver_import_and_sender_selection_verified"], False)
        self.assertNotIn("receiver_import_verified", evidence)

    def test_full_release_publication_on_a_qualification_run_stops_before_docker(self) -> None:
        lock = self.lock("amd64")
        arguments = self.documents(lock, "amd64", release=False)
        publication = json.loads(arguments.publication.read_text())
        publication["images"]["web"] = "ghcr.io/bipoxdxd/duora-web@sha256:" + "e" * 64
        arguments.publication.write_text(json.dumps(publication))
        with self.assertRaises(ValueError):
            transport.qualify(arguments)
        self.assertEqual(self.commands, [])


if __name__ == "__main__":
    unittest.main()
