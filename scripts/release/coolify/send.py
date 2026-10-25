#!/usr/bin/env python3
"""Pure release metadata builder. Transport uses the reviewed send.sh CLI."""

import hashlib
import json
import os
import re
import stat
import sys
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from receiver import PROJECTS, json_unique, receiver_digest, validate_oci, validate_release


def requested(project: Any, sha: Any) -> Any:
    if not re.fullmatch("[0-9a-f]{40}", sha):
        raise ValueError("Invalid release commit")
    if project == "duora":
        publication = json.loads(Path(os.environ["PUBLICATION_FILE"]).read_text())
        if publication["commit"] != sha or publication["architecture"] != "amd64":
            raise ValueError("Wrong publication")
        result = publication["images"]
        if set(result) != set(PROJECTS[project]):
            raise ValueError("Unexpected image roles")
        for role, reference in result.items():
            if not re.fullmatch(
                re.escape(PROJECTS[project][role]) + r"@sha256:[0-9a-f]{64}", reference
            ):
                raise ValueError("Unexpected published image")
        return result
    if project == "stillon":
        return {"app": PROJECTS[project]["app"] + ":" + sha}
    result = json.loads(Path(os.environ["SCANNED_IMAGES_FILE"]).read_text())
    if set(result) != set(PROJECTS[project]):
        raise ValueError("Missing scanned image roles")
    for role, reference in result.items():
        if not re.fullmatch(
            re.escape(PROJECTS[project][role]) + r"@sha256:[0-9a-f]{64}", reference
        ):
            raise ValueError("Scanned images must be immutable exact references")
    return result


def make_duora_manifest(publication: Any, images: Any, commit: Any) -> Any:
    if publication["commit"] != commit or publication["architecture"] != "amd64":
        raise ValueError("Publication identity mismatch")
    migrations = []
    for path in Path("backend").rglob("*.cs"):
        if "Migrations" in path.parts:
            migrations += re.findall(
                r'\[Migration\("([0-9]{14}_[A-Za-z0-9_]+)"\)\]', path.read_text()
            )
    migrations = sorted(set(migrations))
    if not migrations:
        raise ValueError("No source migration evidence")
    release_id = (
        f"{datetime.now(UTC):%Y.%m.%d}-{commit[:7]}-"
        f"{os.environ['GITHUB_RUN_ID']}-{os.environ['GITHUB_RUN_ATTEMPT']}-amd64"
    )
    return {
        "schemaVersion": 1,
        "releaseId": release_id,
        "commit": commit,
        "createdAt": datetime.now(UTC).isoformat(timespec="seconds"),
        "architecture": "amd64",
        "images": images,
        "contractVersion": "v2",
        "schema": {"min": migrations[-1], "max": migrations[-1]},
        "migrations": migrations,
        "sbom": {
            role: publication["publishRun"] + "#sbom-" + role + ".cdx.json" for role in images
        },
        "gates": {"status": "passed", "runUrl": publication["ciRun"]},
        "previousReleaseId": None,
    }


SOURCE_MANIFEST_LIMIT = 256 * 1024
INDEX_MEDIA_TYPES = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}
IMAGE_MEDIA_TYPES = {
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
}


def scanned_transport_references(references: Any, witnesses: Path) -> Any:
    """Bind each transported leaf to the exact, originally scanned manifest bytes."""
    result = {}
    for role, reference in references.items():
        path = witnesses / (role + ".json")
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
        with os.fdopen(fd, "rb") as stream:
            before = os.fstat(fd)
            if (not stat.S_ISREG(before.st_mode) or before.st_uid != os.geteuid()
                    or before.st_nlink != 1 or before.st_mode & 0o022
                    or not 0 < before.st_size <= SOURCE_MANIFEST_LIMIT):
                raise ValueError("Unprotected or oversized source manifest witness")
            raw = stream.read(SOURCE_MANIFEST_LIMIT + 1)
            after = os.fstat(fd)
            if (len(raw) > SOURCE_MANIFEST_LIMIT
                    or (before.st_ino, before.st_size, before.st_mtime_ns, before.st_mode,
                        before.st_uid, before.st_gid, before.st_nlink)
                    != (after.st_ino, after.st_size, after.st_mtime_ns, after.st_mode,
                        after.st_uid, after.st_gid, after.st_nlink)):
                raise ValueError("Source manifest witness changed during inspection")
        repository, source_digest = reference.split("@")
        if "sha256:" + hashlib.sha256(raw).hexdigest() != source_digest:
            raise ValueError("Source manifest digest differs from scanned publication")
        document = json_unique(raw.decode("utf-8"))
        if not isinstance(document, dict) or document.get("schemaVersion") != 2:
            raise ValueError("Unexpected scanned source manifest")
        digest = source_digest
        if "manifests" in document:
            if (document.get("mediaType") not in INDEX_MEDIA_TYPES
                    or "config" in document or "layers" in document
                    or not isinstance(document["manifests"], list)
                    or not 1 <= len(document["manifests"]) <= 64):
                raise ValueError("Unexpected scanned native index")
            native = []
            for descriptor in document["manifests"]:
                if (not isinstance(descriptor, dict)
                        or not isinstance(descriptor.get("platform"), dict)
                        or descriptor.get("mediaType") not in IMAGE_MEDIA_TYPES
                        or not isinstance(descriptor.get("digest"), str)
                        or not re.fullmatch(r"sha256:[0-9a-f]{64}", descriptor["digest"])
                        or type(descriptor.get("size")) is not int
                        or not 0 < descriptor["size"] <= SOURCE_MANIFEST_LIMIT):
                    raise ValueError("Invalid scanned index descriptor")
                # BuildKit provenance is non-executable; never select it as a platform.
                if descriptor["platform"] != {"os": "unknown", "architecture": "unknown"}:
                    native.append(descriptor)
            if len(native) != 1 or native[0]["platform"] != {"os": "linux", "architecture": "amd64"}:
                raise ValueError("Exactly one scanned linux/amd64 platform required")
            digest = native[0]["digest"]
        elif (document.get("mediaType") not in IMAGE_MEDIA_TYPES
                or not isinstance(document.get("config"), dict)
                or not isinstance(document.get("layers"), list)):
            raise ValueError("Unexpected scanned flat image manifest")
        result[role] = repository + "@" + digest
    return result


def main() -> None:
    project = os.environ["RELEASE_PROJECT"]
    sha = os.environ["RELEASE_COMMIT"]
    references = requested(project, sha)
    if sys.argv[1:] == ["requested"]:
        for role, reference in sorted(references.items()):
            print(role + "\t" + reference)
        return
    if len(sys.argv) != 4 or sys.argv[1] != "manifest":
        raise ValueError("Unexpected metadata operation")
    root = Path(sys.argv[3])
    pairs = [line.split("\t") for line in Path(sys.argv[2]).read_text().splitlines()]
    images = dict(pairs)
    if len(images) != len(pairs):
        raise ValueError("Duplicate image role")
    release = {
        "version": 2,
        "receiver_sha256": receiver_digest(),
        "project": project,
        "commit": sha,
        "images": images,
    }
    if project == "duora":
        publication = json.loads(Path(os.environ["PUBLICATION_FILE"]).read_text())
        release["duora"] = make_duora_manifest(publication, images, sha)
    validate_release(release, project, sha)
    if project != "stillon":
        witnesses = os.environ.get("SOURCE_MANIFESTS_DIR")
        if not witnesses:
            raise ValueError("Source manifest witnesses required before transport")
        expected = scanned_transport_references(references, Path(witnesses))
        if images != expected:
            raise ValueError("Transport digest differs from scanned publication")
    for role, reference in images.items():
        validate_oci(root / role, reference.split("@")[1])
    (root / "release.json").write_text(json.dumps(release))


if __name__ == "__main__":
    main()
