"""Hosted-runner qualification of the selected component through the existing OCI ingress.

No SSH, deployment, containers, databases or production credentials. AMD64 exercises
the actual receiver import path; ARM64 proves the native OCI graph and local image.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any

import postgres_component as component

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/release/coolify"))
import receiver
import send


def command(arguments: list[str]) -> str:
    return subprocess.run(arguments, text=True, capture_output=True, check=True,
                          timeout=5 * 60).stdout


def qualify(args: argparse.Namespace) -> dict[str, Any]:
    lock = component.read_lock(args.lock)
    reference = component.select_image(ROOT, lock, args.architecture)
    publication = component.read_lock(args.publication)
    component.validate_origin(lock, component.read_lock(args.run_json), publication, args.architecture)
    before = json.loads(command(["docker", "image", "inspect", reference, "--format", "{{json .}}"]))
    component.validate_local_image(lock, before, args.architecture)
    stage = args.stage
    stage.mkdir(parents=True, exist_ok=False)
    layout = stage / "postgres"
    command(["crane", "pull", "--platform", "linux/" + args.architecture,
             "--format", "oci", reference, str(layout)])
    # Exactly the existing send.sh normalization: never rewrite a manifest blob.
    index_path = layout / "index.json"
    index = receiver.metadata(index_path)
    descriptors = index.get("manifests")
    expected_digest = reference.split("@")[1]
    if not isinstance(descriptors, list) or len(descriptors) != 1:
        raise ValueError("Selected component did not export as one native OCI manifest")
    descriptor = descriptors[0]
    descriptor.pop("artifactType", None)
    if descriptor.get("digest") != expected_digest:
        raise ValueError("OCI transport changed the reviewed component digest")
    index_path.write_text(json.dumps(index, separators=(",", ":")) + "\n")
    blobs = layout / "blobs/sha256"
    for blob in blobs.iterdir():
        if blob.is_symlink() or not blob.is_file() or not re.fullmatch(r"[0-9a-f]{64}", blob.name):
            raise ValueError("Unexpected OCI blob")
        digest = hashlib.sha256()
        with blob.open("rb") as source:
            for chunk in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(chunk)
        if digest.hexdigest() != blob.name:
            raise ValueError("OCI blob digest differs")
    manifest = receiver.metadata(blobs / expected_digest[7:])
    config_digest = manifest["config"]["digest"]
    if not isinstance(config_digest, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", config_digest):
        raise ValueError("OCI image configuration does not have a valid immutable descriptor")
    configuration = receiver.metadata(blobs / config_digest[7:])
    if configuration.get("os") != "linux" or configuration.get("architecture") != args.architecture:
        raise ValueError("OCI graph does not retain the selected native platform")
    evidence: dict[str, Any] = {
        "architecture": args.architecture, "component_reference": reference,
        "fingerprint": lock["fingerprint"], "source_commit": lock["source_commit"],
        "source_publication": lock["publish_run"], "oci_manifest_digest": expected_digest,
        "oci_config_digest": config_digest, "native_image_id": before["Id"],
        "postgres_rebuilt_or_republished": False,
        "source_artifact_id": args.artifact_id,
        "candidate_checkout": command(["git", "rev-parse", "HEAD"]).strip(),
        "qualification_run": "https://github.com/BipoXDXD/duora/actions/runs/" + os.environ["GITHUB_RUN_ID"],
        "qualifier_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    }
    if args.architecture == "amd64":
        # The production ingress is deliberately AMD64-only. Do not broaden it for this proof.
        config_id, _ = receiver.validate_oci(layout, expected_digest)
        if config_id != config_digest:
            raise ValueError("Receiver disagrees with the transported image configuration")
        release = {"project": "duora", "commit": lock["source_commit"],
                   "images": {"postgres": reference}}
        imported, identities = receiver.import_images(release, stage)
        imported_reference = imported["postgres"]
        after = json.loads(command(["docker", "image", "inspect", imported_reference,
                                    "--format", "{{json .}}"]))
        if (identities["postgres"] != before["Id"] or after["Id"] != before["Id"]
                or imported_reference.split("@")[1] != expected_digest):
            raise ValueError("Actual receiver import changed the selected component")
        os.environ["PUBLICATION_FILE"] = str(args.publication)
        requested = send.requested("duora", lock["source_commit"])
        if requested["postgres"] != reference:
            raise ValueError("Actual sender selection changed the reviewed component")
        evidence.update({"receiver_imported_reference": imported_reference,
                         "receiver_imported_image_id": after["Id"],
                         "sender_requested_reference": requested["postgres"],
                         "receiver_import_and_sender_selection_verified": True})
    else:
        evidence["receiver_import_and_sender_selection_verified"] = False
        evidence["receiver_scope"] = "Production receiver accepts AMD64 only; ARM64 native OCI verified"
    return evidence


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--architecture", required=True, choices=component.ARCHITECTURES)
    parser.add_argument("--lock", type=Path, required=True)
    parser.add_argument("--publication", type=Path, required=True)
    parser.add_argument("--run-json", type=Path, required=True)
    parser.add_argument("--stage", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--artifact-id", type=int, required=True)
    args = parser.parse_args()
    if args.artifact_id <= 0:
        parser.error("A verified source artifact ID is required")
    result = qualify(args)
    args.evidence.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
