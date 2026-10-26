"""Package existing standalone instrumentation outputs; never build or execute them."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import stat
from typing import Any

PROJECTS = ("Duora.MediaCorpusTests", "Duora.MediaBaselineHost")
OUTPUTS = tuple(Path("backend/tests") / project / "bin/Debug/net10.0" for project in PROJECTS)
ARCHITECTURES = {"x86_64": "amd64", "aarch64": "arm64"}


def package(root: Path, output: Path, *, commit: str, tree: str, run_id: str,
            run_attempt: str, architecture: str) -> dict[str, Any]:
    """Copy a fixed selection and describe every byte, without a decoder qualification claim."""
    for value in (commit, tree):
        if re.fullmatch(r"[0-9a-f]{40}", value) is None:
            raise ValueError("source must identify an exact commit and tree")
    for value in (run_id, run_attempt):
        if re.fullmatch(r"[1-9][0-9]*", value) is None:
            raise ValueError("CI run and attempt must be positive integers")
    if architecture not in ARCHITECTURES:
        raise ValueError("unsupported runner architecture")
    root = root.resolve(strict=True)
    if output.is_symlink() or output.exists():
        raise FileExistsError(output)
    output = output.resolve()
    if output.is_relative_to(root):
        raise ValueError("output must be outside checkout")

    selected = [Path("global.json")]
    for project, relative in zip(PROJECTS, OUTPUTS):
        current = root
        for part in relative.parts:
            current /= part
            if current.is_symlink():
                raise ValueError("symbolic link in build output path")
        if not current.is_dir():
            raise ValueError("required output directory is missing")
        for suffix in (".dll", ".deps.json", ".runtimeconfig.json"):
            if not (current / (project + suffix)).is_file():
                raise ValueError("required output is missing")
        for path in sorted(current.rglob("*")):
            if path.is_symlink():
                raise ValueError("symbolic link in build output")
            mode = path.stat().st_mode
            if stat.S_ISDIR(mode):
                continue
            if not stat.S_ISREG(mode):
                raise ValueError("build payload must be a regular file")
            if path.name.casefold() == "duora.integrationtests.dll":
                raise ValueError("integration assembly is not standalone instrumentation")
            selected.append(path.relative_to(root))
    for relative in selected:
        path = root / relative
        if path.is_symlink() or not path.is_file():
            raise ValueError("selected payload must be a regular file, not a symbolic link")

    manifest: dict[str, Any] = {
        "schema": 1, "kind": "standalone-instrumentation", "repository": "BipoXDXD/duora",
        "source": {"commit": commit, "tree": tree},
        "ci": {"id": int(run_id), "attempt": int(run_attempt), "job": "backend",
               "architecture": ARCHITECTURES[architecture]},
        "configuration": "Debug", "targetFramework": "net10.0", "hostQualified": False,
        "files": [],
    }
    output.mkdir(parents=True, exist_ok=False)
    try:
        for relative in sorted(selected):
            source = root / relative
            before = source.stat()
            destination = output / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            digest = hashlib.sha256()
            size = 0
            with source.open("rb") as incoming, destination.open("xb") as outgoing:
                while chunk := incoming.read(1024 * 1024):
                    outgoing.write(chunk)
                    digest.update(chunk)
                    size += len(chunk)
            after = source.stat()
            if (before.st_ino, before.st_size, before.st_mtime_ns) != (
                    after.st_ino, after.st_size, after.st_mtime_ns) or size != before.st_size:
                raise ValueError("build output changed while packaging")
            destination.chmod(stat.S_IMODE(before.st_mode))
            manifest["files"].append({"path": relative.as_posix(), "bytes": size,
                                      "sha256": digest.hexdigest()})
        (output / "instrumentation.json").write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    except Exception:
        shutil.rmtree(output)
        raise
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    for name in ("commit", "tree", "run-id", "run-attempt", "architecture"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    manifest = package(args.root, args.output, commit=args.commit, tree=args.tree,
                       run_id=args.run_id, run_attempt=args.run_attempt,
                       architecture=args.architecture)
    print(json.dumps({"files": len(manifest["files"]), "source": manifest["source"]}))


if __name__ == "__main__":
    main()
