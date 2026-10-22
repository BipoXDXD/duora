"""Read-only S4 candidate bundle verification; never installs or starts a service."""
from __future__ import annotations

import hashlib
import json
import math
import os
import re
import stat
from pathlib import Path
from typing import Any

from launcher import Profile

LIMITS = {
    "manifest.json": 16384, "profile.json": 4096, "isolation.json": 65536,
    "sbom.cdx.json": 8 * 1024 * 1024, "launcher.py": 256 * 1024,
    "docker_runtime.py": 256 * 1024, "run_launcher.py": 256 * 1024,
    "duora-media-launcher.service": 256 * 1024,
}
PAYLOADS = set(LIMITS) - {"manifest.json"}
MANIFEST_FIELDS = {"schema", "scope", "commit", "architecture", "image", "imageConfig",
                   "launcherVersion", "protocol", "launcherUid", "profileSha256", "files",
                   "ci", "isolation", "publication"}
CASE_NAMES = {
    "writable-root-negative-control-rejects-dac-false-positive",
    "effective-cgroups-network-dns-storage-secrets-readonly-tmpfs-seccomp",
    "unauthorized-kernel-peer-rejected-before-header", "stdout-withholds-all-bytes",
    "stderr-withholds-all-bytes", "crash-withholds-all-bytes", "hard-deadline-withholds-bytes",
    "descendants-terminated-at-deadline", "kernel-oom-is-not-success", "one-slot-and-explicit-cancel",
    "sigkill-cannot-release-a-result", "launcher-sigkill-orphan-cleanup-and-durable-dedupe",
    "actual-unmodified-dotnet-sanitizer-produces-three-variants",
    "supplied-restricted-systemd-template-starts-and-runs-sanitizer",
}
BOOLEAN_CONTROLS = {"nonRoot", "noCapabilities", "noPrivileges", "seccomp", "noSecrets", "noStorage",
                    "noDockerSocket", "rootReadOnly", "rootMountReadOnly", "tmpfsBounded", "noExec",
                    "noNetwork", "noDns"}
RESOURCE_CONTROLS = {"memory": "536870912", "swap": "0", "cpu": "100000 100000", "pids": "64"}


def require(condition: bool) -> None:
    if not condition:
        raise ValueError("Invalid media release bundle.")


def matches(value: object, pattern: str) -> bool:
    return isinstance(value, str) and re.fullmatch(pattern, value) is not None


def integer(value: object, maximum: int = (1 << 63) - 1) -> bool:
    return type(value) is int and 0 < value <= maximum


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def invalid_constant(_: str) -> Any:
    raise ValueError("Invalid media release JSON constant.")


def document(data: bytes) -> dict[str, Any]:
    value = json.loads(data.decode("utf-8"), object_pairs_hook=unique_object,
                       parse_constant=invalid_constant)
    require(isinstance(value, dict))
    return value


def read_file(directory: int, name: str) -> bytes:
    descriptor = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC,
                         dir_fd=directory)
    try:
        before = os.fstat(descriptor)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                and 0 < before.st_size <= LIMITS[name])
        pieces: list[bytes] = []
        remaining = LIMITS[name] + 1
        while remaining:
            piece = os.read(descriptor, min(65536, remaining))
            if not piece:
                break
            pieces.append(piece)
            remaining -= len(piece)
        after = os.fstat(descriptor)
        result = b"".join(pieces)
        require(len(result) == before.st_size <= LIMITS[name] and after.st_nlink == 1
                and before.st_size == after.st_size and before.st_mtime_ns == after.st_mtime_ns
                and before.st_ctime_ns == after.st_ctime_ns)
        return result
    finally:
        os.close(descriptor)


def validate_manifest(manifest: dict[str, Any], commit: str, architecture: str, scope: str) -> None:
    require(set(manifest) == MANIFEST_FIELDS)
    require(type(manifest["schema"]) is int and manifest["schema"] == 1)
    require(scope in {"synthetic-candidate", "host-candidate"} and manifest["scope"] == scope)
    require(matches(commit, r"[0-9a-f]{40}") and manifest["commit"] == commit)
    require(architecture in {"amd64", "arm64"} and manifest["architecture"] == architecture)
    require(matches(manifest["image"], r"ghcr\.io/bipoxdxd/duora-media-sanitizer@sha256:[0-9a-f]{64}"))
    require(matches(manifest["imageConfig"], r"sha256:[0-9a-f]{64}"))
    require(manifest["launcherVersion"] == "s2-control-v1")
    protocol = manifest["protocol"]
    require(isinstance(protocol, dict) and set(protocol) == {"control", "stream"})
    require(all(type(value) is int and value == 1 for value in protocol.values()))
    require(integer(manifest["launcherUid"], (1 << 31) - 1))
    require(matches(manifest["profileSha256"], r"[0-9a-f]{64}"))
    files = manifest["files"]
    require(isinstance(files, dict) and set(files) == PAYLOADS)
    require(all(matches(value, r"[0-9a-f]{64}") for value in files.values()))
    for name in ("ci", "isolation", "publication"):
        reference = manifest[name]
        require(isinstance(reference, dict) and set(reference) == {"run", "attempt"})
        require(all(integer(value) for value in reference.values()))


def validate_proof(proof: dict[str, Any], manifest: dict[str, Any]) -> None:
    require(set(proof) == {"architecture", "candidateOnly", "complete", "imageReference", "profileSha256",
                           "cases", "images", "runtime", "effectiveControls", "kernelOomObserved",
                           "writableRootNegativeControl", "sanitizerResponseBytes", "sanitizerResponseHash"})
    require(proof["architecture"] == {"amd64": "x86_64", "arm64": "aarch64"}[manifest["architecture"]])
    require(proof["candidateOnly"] is True and proof["complete"] is True)
    require(proof["imageReference"] == manifest["image"] and proof["profileSha256"] == manifest["profileSha256"])
    cases = proof["cases"]
    require(isinstance(cases, list) and len(cases) == len(CASE_NAMES))
    names: set[str] = set()
    for case in cases:
        require(isinstance(case, dict) and set(case) == {"name", "passed", "milliseconds"})
        name = case["name"]
        require(isinstance(name, str) and name in CASE_NAMES and name not in names and case["passed"] is True)
        duration = case["milliseconds"]
        require(type(duration) in (int, float) and math.isfinite(duration) and duration >= 0)
        names.add(name)
    images = proof["images"]
    require(isinstance(images, dict) and set(images) == {"sanitizer", "isolation-probe"})
    require(images["sanitizer"] == manifest["imageConfig"])
    require(matches(images["isolation-probe"], r"sha256:[0-9a-f]{64}"))
    runtime = proof["runtime"]
    require(isinstance(runtime, dict) and set(runtime) == {"ServerVersion", "CgroupDriver", "CgroupVersion"})
    require(isinstance(runtime["ServerVersion"], str) and bool(runtime["ServerVersion"])
            and runtime["CgroupDriver"] == "systemd" and runtime["CgroupVersion"] == "2")
    controls = proof["effectiveControls"]
    require(isinstance(controls, dict) and set(controls) == BOOLEAN_CONTROLS | set(RESOURCE_CONTROLS))
    require(all(controls[name] is True for name in BOOLEAN_CONTROLS))
    require(all(controls[name] == value for name, value in RESOURCE_CONTROLS.items()))
    require(proof["kernelOomObserved"] is True and proof["writableRootNegativeControl"] is True)
    require(integer(proof["sanitizerResponseBytes"], 24 * 1024 * 1024 + 4096))
    require(matches(proof["sanitizerResponseHash"], r"[0-9a-f]{64}"))


def verify_bundle(directory: Path, expected_manifest_sha256: str, expected_commit: str,
                  expected_architecture: str, expected_scope: str) -> dict[str, Any]:
    """Verify a bounded snapshot against a separately trusted pin; grant no activation authority."""
    try:
        require(matches(expected_manifest_sha256, r"[0-9a-f]{64}"))
        descriptor = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            names: set[str] = set()
            with os.scandir(descriptor) as entries:
                for entry in entries:
                    require(entry.name in LIMITS and entry.name not in names)
                    names.add(entry.name)
            require(names == set(LIMITS))
            manifest_bytes = read_file(descriptor, "manifest.json")
            require(hashlib.sha256(manifest_bytes).hexdigest() == expected_manifest_sha256)
            manifest = document(manifest_bytes)
            validate_manifest(manifest, expected_commit, expected_architecture, expected_scope)
            contents: dict[str, bytes] = {}
            for name in PAYLOADS:
                contents[name] = read_file(descriptor, name)
                require(hashlib.sha256(contents[name]).hexdigest() == manifest["files"][name])
            profile = document(contents["profile.json"])
            require(integer(profile.get("worker_uid"), (1 << 31) - 1)
                    and integer(profile.get("control_gid"), (1 << 31) - 1))
            parsed = Profile.from_dict(profile, manifest["launcherUid"])
            require(parsed.image == manifest["image"] and parsed.digest == manifest["profileSha256"])
            validate_proof(document(contents["isolation.json"]), manifest)
            bom = document(contents["sbom.cdx.json"])
            require(bom.get("bomFormat") == "CycloneDX" and bom.get("specVersion") in {"1.5", "1.6"})
            require(integer(bom.get("version")) and isinstance(bom.get("components"), list))
            return manifest
        finally:
            os.close(descriptor)
    except (OSError, UnicodeError, TypeError, KeyError, OverflowError, RecursionError, ValueError) as error:
        raise ValueError("Invalid media release bundle.") from error
