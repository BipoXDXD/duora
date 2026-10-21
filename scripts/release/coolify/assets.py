"""Guard original media hashes before publishing. New media may be added."""

import hashlib
import json
from pathlib import Path


def main() -> None:
    manifest = json.loads(Path(__file__).with_name("preserved-assets.json").read_text())
    for name, digest in manifest["assets"].items():
        path = Path(name)
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError("Preserved asset removed or modified: " + name)
    print(f"Original media preserved: {len(manifest['assets'])} files")


if __name__ == "__main__":
    main()
