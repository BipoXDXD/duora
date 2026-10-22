"""Trusted Actions-only candidate publisher; never installs on application hosts."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import check_ci
from release_contract import (LIMITS, document, integer, matches, require, verify_bundle)

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "BipoXDXD/duora"
IMAGE_REPOSITORY = "ghcr.io/bipoxdxd/duora-media-sanitizer"
ARCHITECTURES = {"amd64", "arm64"}
MANIFEST_TYPES = {"application/vnd.oci.image.manifest.v1+json",
                  "application/vnd.docker.distribution.manifest.v2+json"}


def validate_context(context: dict[str, Any], current_main: str, ci: dict[str, Any],
                     jobs: list[dict[str, Any]]) -> dict[str, int]:
    require(set(context) == {"event", "ref", "workflowSha", "repository", "commit", "run", "attempt"})
    require(context["event"] == "workflow_dispatch" and context["ref"] == "refs/heads/main"
            and context["repository"] == REPOSITORY)
    require(matches(context["commit"], r"[0-9a-f]{40}")
            and context["commit"] == context["workflowSha"] == current_main)
    require(integer(context["run"]) and integer(context["attempt"])
            and integer(ci.get("id")) and integer(ci.get("run_attempt")))
    check_ci.validate(ci, jobs, context["commit"], REPOSITORY)
    return {"run": ci["id"], "attempt": ci["run_attempt"]}


def validate_manifests(manifests: dict[str, Any], commit: str, ci: dict[str, int],
                       publication: dict[str, int]) -> dict[str, str]:
    """Additional cross-bundle checks after verify_bundle has validated each full bundle."""
    require(set(manifests) == ARCHITECTURES and matches(commit, r"[0-9a-f]{40}"))
    for reference in (ci, publication):
        require(set(reference) == {"run", "attempt"} and all(integer(v) for v in reference.values()))
    result: dict[str, str] = {}
    for arch, manifest in manifests.items():
        require(type(manifest["schema"]) is int and manifest["schema"] == 1)
        require(manifest["scope"] == "synthetic-candidate" and manifest["commit"] == commit
                and manifest["architecture"] == arch and manifest["ci"] == ci
                and manifest["isolation"] == publication and manifest["publication"] == publication)
        image = manifest["image"]
        require(matches(image, r"ghcr\.io/bipoxdxd/duora-media-sanitizer@sha256:[0-9a-f]{64}"))
        result[arch] = image.split("@", 1)[1]
    require(len(set(result.values())) == 2)
    return result


def validate_native(manifest: dict[str, Any], config: dict[str, Any], architecture: str,
                    config_digest: str) -> None:
    require(architecture in ARCHITECTURES and matches(config_digest, r"sha256:[0-9a-f]{64}"))
    require(type(manifest.get("schemaVersion")) is int and manifest["schemaVersion"] == 2)
    require(manifest.get("mediaType") in MANIFEST_TYPES and "manifests" not in manifest)
    require(isinstance(manifest.get("config"), dict) and manifest["config"].get("digest") == config_digest)
    require(config.get("os") == "linux" and config.get("architecture") == architecture)


def validate_index(index: dict[str, Any], expected: dict[str, str]) -> None:
    require(set(expected) == ARCHITECTURES and len(set(expected.values())) == 2)
    require(type(index.get("schemaVersion")) is int and index["schemaVersion"] == 2)
    require(index.get("mediaType") in {"application/vnd.oci.image.index.v1+json",
                                       "application/vnd.docker.distribution.manifest.list.v2+json"})
    entries = index.get("manifests")
    require(isinstance(entries, list) and len(entries) == 2)
    actual: dict[str, str] = {}
    for entry in entries:
        require(isinstance(entry, dict) and isinstance(entry.get("platform"), dict))
        platform = entry["platform"]
        arch = platform.get("architecture")
        require(arch in ARCHITECTURES and arch not in actual and platform.get("os") == "linux")
        require(entry.get("mediaType") in MANIFEST_TYPES and integer(entry.get("size")))
        require(matches(entry.get("digest"), r"sha256:[0-9a-f]{64}"))
        actual[arch] = entry["digest"]
    require(actual == expected)


def environment_context() -> dict[str, Any]:
    require(os.environ.get("GITHUB_WORKFLOW_REF") == REPOSITORY + "/.github/workflows/media-release.yml@refs/heads/main")
    return {"event": os.environ["GITHUB_EVENT_NAME"], "ref": os.environ["GITHUB_REF"],
            "workflowSha": os.environ["GITHUB_SHA"], "repository": os.environ["GITHUB_REPOSITORY"],
            "commit": os.environ["RELEASE_COMMIT"], "run": int(os.environ["GITHUB_RUN_ID"]),
            "attempt": int(os.environ["GITHUB_RUN_ATTEMPT"])}


def exact_ci() -> dict[str, int]:
    context = environment_context()
    commit = context["commit"]
    require(matches(commit, r"[0-9a-f]{40}"))
    runs = check_ci.api(f"repos/{REPOSITORY}/actions/workflows/ci.yml/runs?event=push&branch=main&head_sha={commit}&per_page=100")
    require(isinstance(runs.get("workflow_runs"), list) and bool(runs["workflow_runs"]))
    selected = max(runs["workflow_runs"], key=lambda run: run["id"])
    run_id, attempt = selected["id"], selected["run_attempt"]
    require(integer(run_id) and integer(attempt))
    response = check_ci.api(f"repos/{REPOSITORY}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100")
    jobs = response["jobs"]
    require(isinstance(jobs, list) and type(response["total_count"]) is int and response["total_count"] == len(jobs))
    latest = check_ci.api(f"repos/{REPOSITORY}/actions/runs/{run_id}")
    require(latest["id"] == run_id and latest["run_attempt"] == attempt)
    current_main = check_ci.api(f"repos/{REPOSITORY}/commits/main")["sha"]
    return validate_context(context, current_main, latest, jobs)


def source_bytes(path: Path, limit: int) -> bytes:
    # Input paths are selected by trusted CI, but size and link restrictions still apply.
    import stat
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        info = os.fstat(fd)
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and 0 < info.st_size <= limit)
        with os.fdopen(fd, "rb", closefd=False) as stream:
            data = stream.read(limit + 1)
        require(len(data) == info.st_size <= limit)
        return data
    finally:
        os.close(fd)


def references() -> tuple[dict[str, int], dict[str, int]]:
    ci = {"run": int(os.environ["CI_RUN"]), "attempt": int(os.environ["CI_ATTEMPT"])}
    publication = {"run": int(os.environ["GITHUB_RUN_ID"]), "attempt": int(os.environ["GITHUB_RUN_ATTEMPT"])}
    require(all(integer(v) for ref in (ci, publication) for v in ref.values()))
    return ci, publication


def assemble_bundle(destination: Path, profile_path: Path, proof_path: Path, sbom_path: Path,
                    image: str, config: str, commit: str, architecture: str, launcher_uid: int,
                    ci: dict[str, int], publication: dict[str, int]) -> str:
    sources = {"profile.json": profile_path, "isolation.json": proof_path, "sbom.cdx.json": sbom_path,
               **{name: ROOT / "scripts/media" / name for name in ("launcher.py", "docker_runtime.py", "run_launcher.py")},
               "duora-media-launcher.service": ROOT / "infra/media/systemd/duora-media-launcher.service"}
    contents = {name: source_bytes(path, LIMITS[name]) for name, path in sources.items()}
    from launcher import Profile
    profile = Profile.from_dict(document(contents["profile.json"]), launcher_uid)
    manifest = {"schema": 1, "scope": "synthetic-candidate", "commit": commit, "architecture": architecture,
                "image": image, "imageConfig": config, "launcherVersion": "s2-control-v1",
                "protocol": {"control": 1, "stream": 1}, "launcherUid": launcher_uid, "profileSha256": profile.digest,
                "files": {name: hashlib.sha256(data).hexdigest() for name, data in contents.items()},
                "ci": ci, "isolation": publication, "publication": publication}
    encoded = (json.dumps(manifest, indent=2) + "\n").encode()
    pin = hashlib.sha256(encoded).hexdigest()
    destination.mkdir(mode=0o700)
    for name, data in {**contents, "manifest.json": encoded}.items():
        with (destination / name).open("xb") as output:
            output.write(data)
    verify_bundle(destination, pin, commit, architecture, "synthetic-candidate")
    return pin


def docker_json(*arguments: str) -> dict[str, Any]:
    return document(subprocess.check_output(["docker", "buildx", "imagetools", *arguments]))


def publish_indices(directory: Path, output: Path) -> None:
    ci = exact_ci()
    expected_ci, publication = references()
    require(ci == expected_ci)
    commit = os.environ["RELEASE_COMMIT"]
    manifests: dict[str, Any] = {}
    pins: dict[str, str] = {}
    for arch in sorted(ARCHITECTURES):
        metadata = document(source_bytes(directory / arch / "candidate.json", 256))
        require(set(metadata) == {"manifestSha256"})
        pin = metadata["manifestSha256"]
        manifests[arch] = verify_bundle(directory / arch / "bundle", pin, commit, arch, "synthetic-candidate")
        pins[arch] = pin
        manifest = manifests[arch]
        native = docker_json("inspect", manifest["image"], "--raw")
        config = docker_json("inspect", manifest["image"], "--format", "{{json .Image}}")
        validate_native(native, config, arch, manifest["imageConfig"])
    expected = validate_manifests(manifests, commit, ci, publication)
    sources = [manifests[arch]["image"] for arch in sorted(ARCHITECTURES)]
    validate_index(docker_json("create", "--dry-run", *sources), expected)
    # Recheck immediately before registry write; newer main never grants this candidate a ready index.
    require(exact_ci() == ci)
    tag = f"{IMAGE_REPOSITORY}:candidate-{commit}-{publication['run']}-{publication['attempt']}"
    subprocess.run(["docker", "buildx", "imagetools", "create", "--tag", tag, *sources], check=True)
    actual = docker_json("inspect", tag, "--format", "{{json .Manifest}}")
    validate_index(actual, expected)
    digest = actual.get("digest")
    require(matches(digest, r"sha256:[0-9a-f]{64}"))
    output.mkdir(mode=0o700)
    bundles: dict[str, Any] = {}
    for arch in sorted(ARCHITECTURES):
        shutil.copytree(directory / arch / "bundle", output / arch)
        verify_bundle(output / arch, pins[arch], commit, arch, "synthetic-candidate")
        bundles[arch] = {"manifestSha256": pins[arch], "image": manifests[arch]["image"],
                         "profileSha256": manifests[arch]["profileSha256"]}
    result = {"schema": 1, "scope": "synthetic-candidate", "commit": commit, "ci": ci, "publication": publication,
              "image": IMAGE_REPOSITORY + "@" + digest, "native": expected, "bundles": bundles}
    (output / "release.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


def main() -> None:
    require(os.environ.get("GITHUB_ACTIONS") == "true")
    if sys.argv[1:] == ["gate"]:
        ci = exact_ci()
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(f"commit={os.environ['RELEASE_COMMIT']}\nci_run={ci['run']}\nci_attempt={ci['attempt']}\n")
    elif sys.argv[1:] == ["bundle"]:
        ci, publication = references()
        pin = assemble_bundle(Path(os.environ["NATIVE_BUNDLE_DIR"]), Path(os.environ["TESTED_PROFILE"]),
                              Path(os.environ["ISOLATION_REPORT"]), Path(os.environ["SANITIZER_SBOM"]),
                              os.environ["SANITIZER_IMAGE"], os.environ["SANITIZER_CONFIG"],
                              os.environ["RELEASE_COMMIT"], os.environ["ARCHITECTURE"],
                              int(os.environ["LAUNCHER_UID"]), ci, publication)
        (Path(os.environ["NATIVE_BUNDLE_DIR"]).parent / "candidate.json").write_text(
            json.dumps({"manifestSha256": pin}) + "\n", encoding="utf-8")
    elif sys.argv[1:] == ["indexes"]:
        publish_indices(Path(os.environ["NATIVE_CANDIDATES"]), Path(os.environ["RELEASE_BUNDLE_DIR"]))
    else:
        raise ValueError("Expected gate, bundle or indexes.")


if __name__ == "__main__":
    main()
