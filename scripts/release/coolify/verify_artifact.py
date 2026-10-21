#!/usr/bin/env python3
"""Read-only verification of the reviewed shared receiver and optional peer installation."""

import argparse
import hashlib
import json
from pathlib import Path


def verify(root: Path) -> dict[str, str | int]:
    artifact = json.loads((root / "receiver-release.json").read_text())
    if set(artifact) != {"version", "protocol", "sha256"} or artifact["protocol"] != 2:
        raise ValueError("Unexpected receiver artifact metadata")
    actual = hashlib.sha256((root / "receiver.py").read_bytes()).hexdigest()
    if actual != artifact["sha256"]:
        raise ValueError("Receiver bytes differ from the reviewed artifact")
    return {"version": str(artifact["version"]), "protocol": 2, "sha256": actual}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--peer", type=Path, help="Other repository or installed version directory")
    args = parser.parse_args()
    artifact = verify(Path(__file__).resolve().parent)
    if args.peer is not None and verify(args.peer) != artifact:
        raise ValueError("Receiver versions differ; coordinate both repositories before installing")
    print(json.dumps(artifact, sort_keys=True))


if __name__ == "__main__":
    main()
