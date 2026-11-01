#!/usr/bin/env python3
"""Sensitivity checks for PR A only. Mutate temporary copies, never application sources."""
from pathlib import Path
import os
import shutil
import subprocess
import sys
import tempfile
import uuid

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
RELATIVE = HERE.relative_to(REPO)
CASES = [
    ("writable-control-parent", "fixture.py", "folder(root/'control',0,17010,0o750)",
     "folder(root/'control',0,17010,0o770)", "test_boundary.BoundaryTests.test_authority_cannot_replace_lock_name"),
    ("legacy-peer-admitted", "Authority.cs", "peer.Uid == 17020 && peer.Gid == 17020",
     "((peer.Uid == 17020 && peer.Gid == 17020) || (peer.Uid == 1654 && peer.Gid == 1654))",
     "test_boundary.BoundaryTests.test_legacy_cannot_use_a_valid_client_grant_or_spoof_uid"),
    ("early-unlock", "Authority.cs", '            barrier.Wait("before-put", (int)variant);',
     '            gate.Dispose();\n            barrier.Wait("before-put", (int)variant);',
     "test_journal.JournalTests.test_paused_put_holds_lock_through_eof_and_data_fds_are_cloexec"),
]


def main():
    for name, filename, old, new, test in CASES:
        image = "duora-boundary-mutant:" + uuid.uuid4().hex
        with tempfile.TemporaryDirectory(prefix="duora-boundary-mutation-") as scratch:
            root = Path(scratch)
            for relative in ("Directory.Build.props", "Directory.Packages.props", ".editorconfig", "global.json",
                             "backend/src/Duora.Core/Media/IObjectStore.cs", "backend/src/Duora.Core/Media/ObjectKey.cs",
                             "backend/src/Duora.Core/Media/PhotoVariants.cs", "backend/src/Duora.Infrastructure/Media/ExactLengthStream.cs",
                             "backend/src/Duora.Infrastructure/Media/FileSystemObjectStore.cs",
                             "scripts/testing/private-generation-closure/Barrier.cs"):
                destination = root / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(REPO / relative, destination)
            shutil.copytree(HERE, root / RELATIVE, ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"))
            target = root / RELATIVE / filename
            source = target.read_text()
            assert source.count(old) == 1, name
            target.write_text(source.replace(old, new))
            try:
                build = subprocess.run(["docker", "build", "-f", str(RELATIVE / "Dockerfile"), "-t", image, "."],
                                       cwd=root, capture_output=True, text=True, timeout=240)
                assert build.returncode == 0, build.stdout + build.stderr
                result = subprocess.run([sys.executable, "-m", "unittest", "-v", test], cwd=root / RELATIVE,
                                        env=dict(os.environ, DUORA_BOUNDARY_IMAGE=image),
                                        capture_output=True, text=True, timeout=180)
                output = result.stdout + result.stderr
                assert result.returncode == 1 and "AssertionError" in output and "ERROR" not in output, output
                print("DETECTED", name, test, flush=True)
            finally:
                subprocess.run(["docker", "image", "rm", image], capture_output=True, timeout=30)
    print("PR A mutations detected; cutover/restore and power-loss sensitivity remain unqualified.")


if __name__ == "__main__":
    main()
