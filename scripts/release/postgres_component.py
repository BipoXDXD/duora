"""Select a reviewed PostgreSQL runtime without rebuilding it during application releases."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shlex
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
    ignore = root / ".dockerignore"
    if ignore.is_symlink() or not ignore.is_file():
        raise ValueError("A regular build-context policy is required")
    with ignore.open("rb") as handle:
        ignore_data = handle.read(1024 * 1024 + 1)
    if len(ignore_data) > 1024 * 1024:
        raise ValueError("Build-context policy exceeds the component budget")
    inventory.append({"path": ".dockerignore", "sha256": hashlib.sha256(ignore_data).hexdigest(),
                      "executable": bool(ignore.stat().st_mode & 0o111)})
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
    validate_recipe(root)
    if fingerprint_inputs(root) != lock["fingerprint"]:
        raise ValueError("Runtime inputs changed: review and qualify a PostgreSQL component update")
    return lock["images"][architecture]


def validate_origin(lock: dict[str, Any], run: dict[str, Any], publication: dict[str, Any],
                    architecture: str) -> None:
    validate_lock(lock)
    run_id, attempt = run_identity(lock)
    if architecture not in ARCHITECTURES:
        raise ValueError("Unsupported native PostgreSQL architecture")
    if (type(run.get("id")) is not int or run["id"] != run_id
            or type(run.get("run_attempt")) is not int or run["run_attempt"] != attempt
            or run.get("head_sha") != lock["source_commit"]
            or run.get("path") not in {".github/workflows/coolify.yml", ".github/workflows/publish-images.yml"}
            or run.get("event") not in {"workflow_run", "workflow_dispatch"}
            or run.get("status") != "completed" or run.get("conclusion") != "success"
            or not isinstance(run.get("repository"), dict)
            or run["repository"].get("full_name") != REPOSITORY
            or not isinstance(run.get("head_repository"), dict)
            or run["head_repository"].get("full_name") != REPOSITORY):
        raise ValueError("Component proof is not the exact successful trusted workflow attempt")
    images = publication.get("images")
    ci_run = publication.get("ciRun")
    if (publication.get("commit") != lock["source_commit"]
            or publication.get("architecture") != architecture
            or publication.get("publishRun") != lock["publish_run"]
            or not isinstance(ci_run, str)
            or not re.fullmatch(re.escape("https://github.com/" + REPOSITORY)
                                + r"/actions/runs/[1-9][0-9]*/attempts/[1-9][0-9]*", ci_run)
            or not isinstance(images, dict)
            or set(images) != {"web", "worker", "migrator", "edge", "postgres"}):
        raise ValueError("Native component publication identity differs from the reviewed lock")
    for role, reference in images.items():
        if (not isinstance(reference, str)
                or not re.fullmatch(re.escape("ghcr.io/bipoxdxd/duora-" + role)
                                    + r"@sha256:[0-9a-f]{64}", reference)):
            raise ValueError("Native component publication contains an unapproved image")
    if images["postgres"] != lock["images"][architecture]:
        raise ValueError("Native PostgreSQL digest differs from the reviewed lock")


def validate_local_image(lock: dict[str, Any], image: dict[str, Any], architecture: str) -> None:
    validate_lock(lock)
    if architecture not in ARCHITECTURES:
        raise ValueError("Unsupported native PostgreSQL architecture")
    reference = lock["images"][architecture]
    config = image.get("Config")
    digests = image.get("RepoDigests")
    image_id = image.get("Id")
    descriptor = image.get("Descriptor")
    if (image.get("Os") != "linux" or image.get("Architecture") != architecture
            or not isinstance(image_id, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", image_id)
            or not isinstance(digests, list) or reference not in digests
            or not isinstance(config, dict) or not isinstance(config.get("Labels"), dict)
            or config["Labels"].get("org.opencontainers.image.source") != "https://github.com/" + REPOSITORY
            or (descriptor is not None and (not isinstance(descriptor, dict)
                                           or descriptor.get("digest") != reference.split("@")[1]))):
        raise ValueError("Pulled PostgreSQL platform, source or immutable identity differs")


def run_identity(lock: dict[str, Any]) -> tuple[int, int]:
    validate_lock(lock)
    parts = lock["publish_run"].split("/")
    return int(parts[-3]), int(parts[-1])


def validate_recipe(root: Path) -> None:
    """Reject inputs the closed runtime inventory cannot describe; never build as fallback."""
    instructions = []
    pending = ""
    for raw in (root / "infra/images/postgres/Dockerfile").read_text().splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        continuation = line.endswith("\\")
        pending += (line[:-1] if continuation else line) + " "
        if not continuation:
            instructions.append(pending.strip())
            pending = ""
    if pending:
        raise ValueError("Incomplete PostgreSQL Dockerfile instruction")
    bases = []
    for instruction in instructions:
        parts = instruction.split(maxsplit=1)
        command = parts[0].upper()
        value = parts[1] if len(parts) == 2 else ""
        if command not in {"FROM", "ARG", "RUN", "LABEL", "COPY", "ENV", "USER"}:
            raise ValueError("Unsupported PostgreSQL recipe instruction")
        if command == "FROM":
            bases.append(value)
        elif command == "ARG" and not re.fullmatch(r"PGBACKREST_VERSION=[A-Za-z0-9.+:~_-]+", value):
            raise ValueError("PostgreSQL build argument is outside the reviewed recipe")
        elif command == "ADD" or (command == "RUN" and value.startswith("--")):
            raise ValueError("PostgreSQL recipe reads inputs outside the closed inventory")
        elif command == "COPY":
            tokens = shlex.split(value)
            while tokens and tokens[0].startswith("--"):
                if not re.fullmatch(r"--chmod=[0-7]{3,4}", tokens.pop(0)):
                    raise ValueError("Unsupported PostgreSQL COPY option")
            if len(tokens) < 2:
                raise ValueError("Unsupported PostgreSQL COPY")
            for source in tokens[:-1]:
                path = Path(source)
                if (not source.startswith("infra/images/postgres/") or path.is_absolute()
                        or ".." in path.parts or not re.fullmatch(r"[A-Za-z0-9._/-]+", source)
                        or (root / path).is_symlink() or not (root / path).is_file()):
                    raise ValueError("PostgreSQL COPY escapes the closed runtime inventory")
    if len(bases) != 1 or not re.fullmatch(r"postgres:18\.[0-9]+-trixie@sha256:[0-9a-f]{64}", bases[0]):
        raise ValueError("PostgreSQL base must be the reviewed immutable single-stage platform image")


def select_proof_artifact(lock: dict[str, Any], inventory: dict[str, Any], architecture: str) -> int:
    validate_lock(lock)
    if architecture not in ARCHITECTURES:
        raise ValueError("Unsupported native PostgreSQL architecture")
    artifacts = inventory.get("artifacts")
    if (not isinstance(artifacts, list) or type(inventory.get("total_count")) is not int
            or inventory["total_count"] != len(artifacts) or len(artifacts) > 100
            or any(not isinstance(item, dict) for item in artifacts)):
        raise ValueError("Incomplete authenticated PostgreSQL proof inventory")
    run_id, attempt = run_identity(lock)
    expected_name = f"release-{architecture}-{attempt}"
    candidates = [item for item in artifacts if item.get("name") == expected_name]
    if len(candidates) != 1 or candidates[0].get("expired") is not False:
        raise ValueError("Retained PostgreSQL proof missing or expired; qualify renewal before publication")
    artifact = candidates[0]
    source = artifact.get("workflow_run")
    artifact_id = artifact.get("id")
    if (type(artifact_id) is not int or artifact_id <= 0 or not isinstance(source, dict)
            or type(source.get("id")) is not int or source["id"] != run_id
            or source.get("head_sha") != lock["source_commit"]):
        raise ValueError("PostgreSQL proof artifact is not from the reviewed source run")
    return artifact_id


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=["select", "verify-origin", "verify-image", "select-proof"])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--lock", type=Path, required=True)
    parser.add_argument("--architecture", required=True, choices=ARCHITECTURES)
    parser.add_argument("--run-json", type=Path)
    parser.add_argument("--publication-json", type=Path)
    parser.add_argument("--image-json", type=Path)
    parser.add_argument("--inventory-json", type=Path)
    args = parser.parse_args()
    try:
        lock = read_lock(args.lock)
        if args.operation == "select":
            image = select_image(args.root, lock, args.architecture)
            run_id, attempt = run_identity(lock)
            print(json.dumps({"image": image, "run_id": run_id, "attempt": attempt,
                              "artifact": f"release-{args.architecture}-{attempt}",
                              "source_commit": lock["source_commit"]}))
        elif args.operation == "verify-origin":
            if args.run_json is None or args.publication_json is None:
                raise ValueError("Native publication and authenticated run metadata are required")
            validate_origin(lock, read_lock(args.run_json), read_lock(args.publication_json), args.architecture)
            print("verified")
        elif args.operation == "verify-image":
            if args.image_json is None:
                raise ValueError("Pulled component metadata is required")
            validate_local_image(lock, read_lock(args.image_json), args.architecture)
            print("verified")
        else:
            if args.inventory_json is None:
                raise ValueError("Authenticated component artifact inventory is required")
            print(select_proof_artifact(lock, read_lock(args.inventory_json), args.architecture))
    except (ValueError, OSError) as error:
        parser.exit(1, f"PostgreSQL component refused: {error}\n")


if __name__ == "__main__":
    main()
