#!/usr/bin/env python3
"""Run actual storage host/pipeline/SQL acceptance in a disposable native Linux runner.

The local Docker socket is exposed only to this authorized Testcontainers runner.
No operational mounts, host PID namespace, credentials or existing volumes are mounted.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import uuid


def command(args, **kwargs):
    return subprocess.run(args, check=True, text=True, **kwargs)


def capture(args):
    return command(args, capture_output=True).stdout.strip()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-dir", type=Path, required=True)
    parser.add_argument("--scenario", choices=("full", "alias"), default="full")
    args = parser.parse_args()
    report = args.report_dir.resolve()
    report.mkdir(parents=True, exist_ok=False)
    root = Path(__file__).resolve().parents[3]
    arch = capture(["docker", "info", "--format", "{{.Architecture}}"])
    if arch not in ("aarch64", "arm64"):
        raise SystemExit("This digest is native arm64 only; no emulated amd64 qualification.")
    before = capture(["docker", "volume", "ls", "--format", "{{.Name}}"]).splitlines()
    run_id = uuid.uuid4().hex
    tag = f"duora-storage-authority-checks:{run_id}"
    cidfile = report / "runner.id"
    metadata = {
        "base": capture(["git", "-C", str(root), "rev-parse", "HEAD"]),
        "dirty": bool(capture(["git", "-C", str(root), "status", "--porcelain"])),
        "architecture": arch,
        "scenario": args.scenario,
        "volumes_before": before,
        "production_changed": False,
        "limits": "synthetic processes/logical rollback; not host power or provider qualification",
    }
    files = [
        p for folder in ("backend/src", "backend/tests", "scripts/storage-authority", "scripts/testing/storage-authority")
        for p in (root / folder).rglob("*")
        if p.is_file() and not {"bin", "obj", "__pycache__"}.intersection(p.parts)
    ]
    metadata["source_sha256"] = hashlib.sha256(
        b"".join(str(p.relative_to(root)).encode() + b"\0" + p.read_bytes() for p in sorted(files))
    ).hexdigest()
    try:
        with (report / "build.log").open("w") as log:
            command(["docker", "build", "--progress=plain", "-t", tag, "-f",
                     str(Path(__file__).with_name("Dockerfile")), str(root)],
                    stdout=log, stderr=subprocess.STDOUT, timeout=1200)
        metadata["image_id"] = capture(["docker", "image", "inspect", "--format", "{{.Id}}", tag])
        with (report / "runtime.log").open("w") as log:
            command([
                "docker", "run", "--rm", "--cidfile", str(cidfile),
                "--label", f"duora.synthetic-authority={run_id}",
                "--memory", "4g", "--memory-swap", "4g", "--cpus", "2", "--pids-limit", "256",
                "--ulimit", "core=0",
                "--security-opt", "no-new-privileges:true",
                "--mount", "type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock",
                "-e", "TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal", tag,
                *(["--alias-check"] if args.scenario == "alias" else []),
            ], stdout=log, stderr=subprocess.STDOUT, timeout=600)
        metadata["exit_code"] = 0
    except subprocess.CalledProcessError as error:
        metadata["exit_code"] = error.returncode
        raise
    finally:
        if cidfile.exists():
            cid = cidfile.read_text().strip()
            found = subprocess.run(["docker", "inspect", "--format",
                                    '{{ index .Config.Labels "duora.synthetic-authority" }}', cid],
                                   capture_output=True, text=True, check=False)
            if found.returncode == 0 and found.stdout.strip() == run_id:
                command(["docker", "rm", "-f", cid], capture_output=True)
        after = capture(["docker", "volume", "ls", "--format", "{{.Name}}"]).splitlines()
        metadata["volumes_after"] = after
        metadata["original_volumes_preserved"] = set(before).issubset(after)
        (report / "result.json").write_text(json.dumps(metadata, indent=2) + "\n")
        if not metadata["original_volumes_preserved"]:
            raise RuntimeError("An original volume disappeared; inspect before claiming completion.")
    print(f"PASS: actual Linux authority acceptance; evidence in {report}")


if __name__ == "__main__":
    main()
