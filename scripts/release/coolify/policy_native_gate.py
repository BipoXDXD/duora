"""PR260-only disposable root gate. No Docker, deployment, production secret or API use."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET

RECEIVER_SHA = "e9af03f08162ecf2bda094d38511b44b810043863d3104f60ea1046ae687f3b3"
PEERS = {"goblin": "6c5eb8956b146c5644a9638ba47574c33508b05f", "stillon": "7a8085e50901d22d5cb40acc2a1b71dbd76a244a"}
FILES = ("GOBLIN_POLICY_MIGRATION.md", "ISOLATION.md", "native_goblin_policy_proof.py", "receiver-release.json",
         "receiver.py", "test_goblin_policy.py", "test_host_isolation.py")


def need(value, message):
    if not value: raise ValueError(message)


def qualify_results(receiver, junit):
    need(type(receiver) is dict and receiver.get("receiver_sha256") == RECEIVER_SHA
         and receiver.get("successful") is True, "receiver source/result does not qualify")
    for key, expected in (("tests", 10), ("failures", 0), ("errors", 0), ("skipped", 0)):
        need(type(receiver.get(key)) is int and receiver[key] == expected, "receiver native case count/result differs")
    suites = junit.findall("testsuite")
    need(len(suites) == 1, "exactly one native reader suite required")
    suite = suites[0]
    need(all(suite.get(key) == str(value) for key, value in (("tests", 13), ("failures", 0), ("errors", 0), ("skipped", 0))),
         "reader native results contain failures/skips or missing cases")
    cases = suite.findall("testcase")
    need(len(cases) == 13 and all(case.get("name") and len(case) == 0 for case in cases), "reader native cases incomplete")
    need(len({case.get("name") for case in cases}) == 13, "duplicate native reader cases")
    return {"receiver_tests": 10, "reader_tests": 13, "skipped": 0}


def run(argv, *, cwd=None, env=None):
    result = subprocess.run(argv, cwd=cwd, env=env, capture_output=True, text=True, timeout=180)
    need(len(result.stdout.encode()) + len(result.stderr.encode()) <= 2 * 1024 * 1024, "gate output budget exceeded")
    if result.returncode:
        # These are strictly synthetic tests. No production credentials/metadata reach their diagnostics.
        sys.stderr.write((result.stdout + result.stderr)[-12000:])
        raise ValueError("native command failed; no qualified artifact")
    return result.stdout.strip()


def prepare_public_output(output):
    need(output.is_absolute() and output.name == "qualification.json" and ".." not in output.parts,
         "exact absolute public metadata destination required")
    # Only this NEW directory contains public test counts/hashes. Never chmod/reuse existing or private fixtures.
    output.parent.mkdir(mode=0o755)
    output.parent.chmod(0o755)


def publish_public_metadata(output, result, junit):
    need(junit.parent == output.parent and junit.name == "reader-native.xml", "only the own public JUnit metadata may be exposed")
    parent = output.parent.lstat()
    need(stat.S_ISDIR(parent.st_mode) and parent.st_uid == os.geteuid() and parent.st_gid == os.getegid()
         and stat.S_IMODE(parent.st_mode) == 0o755, "new public output directory ownership/mode differs")
    info = junit.lstat()
    need(stat.S_ISREG(info.st_mode) and info.st_uid == os.geteuid() and info.st_gid == os.getegid()
         and info.st_nlink == 1 and info.st_size <= 2 * 1024 * 1024, "public XML must be the owned bounded regular metadata file")
    fd = os.open(junit, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        actual = os.fstat(fd)
        need((actual.st_dev, actual.st_ino) == (info.st_dev, info.st_ino), "public XML changed before publication")
        os.fchmod(fd, 0o644)
        os.fsync(fd)
    finally: os.close(fd)
    raw = (json.dumps(result, sort_keys=True, allow_nan=False) + "\n").encode()
    need(len(raw) <= 64 * 1024, "public JSON metadata too large")
    fd = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "wb") as out:
        out.write(raw)
        out.flush()
        os.fchmod(out.fileno(), 0o644)
        os.fsync(out.fileno())
    directory = os.open(output.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try: os.fsync(directory)
    finally: os.close(directory)


def main():
    parser = argparse.ArgumentParser()
    for name in ("duora", "goblin", "stillon", "output"): parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    need(sys.platform == "linux" and os.geteuid() == 0 and sys.version_info[:2] == (3, 12), "Linux/root/Python3.12 required")
    need(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
         and os.environ.get("GITHUB_REPOSITORY") == "BipoXDXD/duora" and os.environ.get("GITHUB_EVENT_NAME") == "pull_request",
         "disposable hosted Duora PR only")
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_bytes())
    need(event.get("number") == 260, "only existing PR260 is eligible")
    heads = {"duora": event["pull_request"]["head"]["sha"], **PEERS}
    directories = {name: getattr(args, name).resolve(strict=True) for name in heads}
    for name, directory in directories.items():
        base = ["/usr/bin/git", "-c", "safe.directory=" + str(directory), "-C", str(directory)]
        need(run(base + ["rev-parse", "HEAD"]) == heads[name], "pinned source head differs: " + name)
        need(not run(base + ["status", "--porcelain", "--untracked-files=no"]), "tracked checkout changed: " + name)
    shared = {"duora": directories["duora"] / "scripts/release/coolify",
              "goblin": directories["goblin"] / "deploy/coolify/release",
              "stillon": directories["stillon"] / "deploy/coolify/release"}
    hashes = {}
    for name in FILES:
        raw = (shared["duora"] / name).read_bytes()
        need(all((directory / name).read_bytes() == raw for directory in shared.values()), "shared byte divergence: " + name)
        hashes[name] = hashlib.sha256(raw).hexdigest()
    need(hashes["receiver.py"] == RECEIVER_SHA, "receiver bytes differ from qualified format candidate")
    need(json.loads((shared["duora"] / "receiver-release.json").read_bytes()) == {"version": "2.1.1", "protocol": 2, "sha256": RECEIVER_SHA},
         "receiver metadata differs")
    prepare_public_output(args.output)
    environment = {**os.environ, "PYTHONDONTWRITEBYTECODE": "1", "PYTEST_DISABLE_PLUGIN_AUTOLOAD": "1"}
    receipt = json.loads(run([sys.executable, "-B", str(shared["duora"] / "native_goblin_policy_proof.py")],
        cwd=shared["duora"], env=environment))
    junit = args.output.parent / "reader-native.xml"
    environment["PYTHONPATH"] = str(directories["goblin"] / "deploy")
    run([sys.executable, "-B", "-m", "pytest", "-q", "-c", "/dev/null", "--noconftest", "-p", "no:cacheprovider",
         "--junitxml=" + str(junit), str(directories["goblin"] / "tests/executor_ctl/test_policy_native.py")],
        cwd=directories["goblin"], env=environment)
    results = qualify_results(receipt, ET.fromstring(junit.read_bytes()))
    result = {"schema": 1, "scope": "shared-policy-native-filesystem-only", "heads": heads,
        "sharedFileSha256": hashes, "results": results, "uid": os.geteuid(), "gid": os.getegid(),
        "system": platform.system(), "python": platform.python_version(), "productionChanged": False,
        "daemonTransitionTested": False, "workflowRunId": os.environ["GITHUB_RUN_ID"], "workflowRunAttempt": os.environ["GITHUB_RUN_ATTEMPT"]}
    publish_public_metadata(args.output, result, junit)
    print(json.dumps({"scope": result["scope"], "results": results, "productionChanged": False}, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
