"""Select a reviewed PostgreSQL runtime without rebuilding it during application releases."""
import hashlib
import json
from pathlib import Path
import re
import stat
from typing import Any

REPOSITORY = "BipoXDXD/duora"
ARCHITECTURES = ("amd64", "arm64")
PROFILE = "postgres-embedded-reconcile-v1"
IMAGE = "ghcr.io/bipoxdxd/duora-postgres@sha256:"
LOCK_FIELDS = {"version", "profile", "fingerprint", "source_commit", "publish_run", "images"}


def fingerprint_inputs(root: Path) -> str:
    directory = root
    for part in ("infra", "images", "postgres"):
        directory = directory / part
        if directory.is_symlink() or not directory.is_dir():
            raise ValueError("PostgreSQL inputs must be real repository directories")
    inventory = []
    total = 0
    for path in sorted(directory.rglob("*")):
        mode = path.lstat().st_mode
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode) or path.stat().st_size > 1024 * 1024:
            raise ValueError("Unsupported PostgreSQL runtime input")
        with path.open("rb") as handle:
            data = handle.read(1024 * 1024 + 1)
        if len(data) > 1024 * 1024:
            raise ValueError("PostgreSQL runtime input exceeds its size budget")
        total += len(data)
        if total > 4 * 1024 * 1024:
            raise ValueError("PostgreSQL runtime inputs exceed the component budget")
        inventory.append({"path": path.relative_to(root).as_posix(),
                          "sha256": hashlib.sha256(data).hexdigest(),
                          "executable": bool(mode & 0o111)})
    if not any(item["path"] == "infra/images/postgres/Dockerfile" for item in inventory):
        raise ValueError("PostgreSQL Dockerfile is missing")
    recipe = {"policy": 1, "profile": PROFILE, "platforms": list(ARCHITECTURES),
              "source_label": "https://github.com/" + REPOSITORY, "files": inventory}
    return "sha256:" + hashlib.sha256(
        json.dumps(recipe, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def read_lock(path: Path) -> dict[str, Any]:
    def unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate component lock key")
            result[key] = value
        return result

    with path.open("rb") as handle:
        data = handle.read(64 * 1024 + 1)
    if len(data) > 64 * 1024:
        raise ValueError("Component lock exceeds its size budget")
    result = json.loads(data, object_pairs_hook=unique)
    if not isinstance(result, dict):
        raise ValueError("Component lock must be an object")
    return result


def validate_lock(lock: dict[str, Any]) -> None:
    if (set(lock) != LOCK_FIELDS or type(lock.get("version")) is not int
            or lock["version"] != 1 or lock["profile"] != PROFILE):
        raise ValueError("Unknown PostgreSQL component contract")
    if (not isinstance(lock["source_commit"], str)
            or not re.fullmatch(r"[0-9a-f]{40}", lock["source_commit"])
            or not isinstance(lock["publish_run"], str)
            or not re.fullmatch(re.escape("https://github.com/" + REPOSITORY)
                                + r"/actions/runs/[1-9][0-9]*/attempts/[1-9][0-9]*", lock["publish_run"])
            or not isinstance(lock["fingerprint"], str)
            or not re.fullmatch(r"sha256:[0-9a-f]{64}", lock["fingerprint"])):
        raise ValueError("Component provenance or fingerprint is malformed")
    images = lock["images"]
    if not isinstance(images, dict) or set(images) != set(ARCHITECTURES):
        raise ValueError("Both native component digests are mandatory")
    for reference in images.values():
        if not isinstance(reference, str) or not re.fullmatch(re.escape(IMAGE) + r"[0-9a-f]{64}", reference):
            raise ValueError("PostgreSQL component must use an immutable allowed digest")


def select_image(root: Path, lock: dict[str, Any], architecture: str) -> str:
    validate_lock(lock)
    if architecture not in ARCHITECTURES:
        raise ValueError("Unsupported native PostgreSQL architecture")
    if fingerprint_inputs(root) != lock["fingerprint"]:
        raise ValueError("Runtime inputs changed: review and qualify a PostgreSQL component update")
    return lock["images"][architecture]


def validate_origin(lock: dict[str, Any], run: dict[str, Any], publication: dict[str, Any],
                    architecture: str) -> None:
    raise NotImplementedError("Authenticated native component publication provenance")


def validate_local_image(lock: dict[str, Any], image: dict[str, Any], architecture: str) -> None:
    raise NotImplementedError("Pulled PostgreSQL component identity")
