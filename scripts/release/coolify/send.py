#!/usr/bin/env python3
"""Pure release metadata builder. Transport uses the reviewed send.sh CLI."""

import json
import os
import re
import sys
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from receiver import PROJECTS, receiver_digest, validate_oci, validate_release


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
        "contractVersion": "v1",
        "schema": {"min": migrations[-1], "max": migrations[-1]},
        "migrations": migrations,
        "sbom": {
            role: publication["publishRun"] + "#sbom-" + role + ".cdx.json" for role in images
        },
        "gates": {"status": "passed", "runUrl": publication["ciRun"]},
        "previousReleaseId": None,
    }


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
    if project != "stillon" and images != references:
        raise ValueError("Transport digest differs from scanned publication")
    for role, reference in images.items():
        validate_oci(root / role, reference.split("@")[1])
    (root / "release.json").write_text(json.dumps(release))


if __name__ == "__main__":
    main()
