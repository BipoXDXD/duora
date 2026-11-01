#!/usr/bin/env python3
"""Build and run only the disposable Linux experiment; record reproducible evidence."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
IMAGE = "duora-generation-boundary:local"


def output(args):
    return subprocess.check_output(args, cwd=REPO, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report-dir", type=Path, required=True)
    args = parser.parse_args()
    reports = args.report_dir.resolve()
    reports.mkdir(parents=True, exist_ok=True)
    # Includes uncommitted experimental source. No enrollment/nonces exist on the host.
    paths = sorted(p for p in HERE.iterdir() if p.is_file())
    digest = hashlib.sha256()
    for path in paths:
        digest.update(path.name.encode() + b'\0' + path.read_bytes())
    metadata = dict(head=output(["git", "rev-parse", "HEAD"]),
                    dirty=bool(output(["git", "status", "--porcelain"])),
                    boundarySourceSha256=digest.hexdigest(),
                    dockerArchitecture=output(["docker", "info", "--format", "{{.Architecture}}"]),
                    limits=["No terminal SQL capability", "No power-loss or host-crash qualification",
                            "No legacy cutover/epoch admission or operational restore", "No S3/B2 qualification"])
    with (reports / "build.log").open("w") as log:
        subprocess.run(["docker", "build", "-f", str(HERE / "Dockerfile"), "-t", IMAGE, "."],
                       cwd=REPO, stdout=log, stderr=subprocess.STDOUT, check=True)
    metadata["image"] = json.loads(output(["docker", "image", "inspect", IMAGE,
                                          "--format", "{{json .}} "]))["Id"]
    metadata["sdk"] = output(["docker", "run", "--rm", "--network", "none", "--read-only",
                              "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                              "--entrypoint", "dotnet", IMAGE, "--version"])
    env = dict(os.environ, DUORA_BOUNDARY_IMAGE=IMAGE)
    with (reports / "tests.log").open("w") as log:
        result = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s", str(HERE),
                                 "-p", "test_*.py", "-v"], cwd=REPO, env=env,
                                stdout=log, stderr=subprocess.STDOUT)
    metadata["testExitCode"] = result.returncode
    (reports / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
    print("PASS" if result.returncode == 0 else "FAIL", reports)
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
