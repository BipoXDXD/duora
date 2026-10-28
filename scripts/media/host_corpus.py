#!/usr/bin/python3
"""Run the 18-case synthetic corpus on the qualified host, inside the dedicated rootless runtime.

Root on the host orchestrates; every container runs in the media_sanitizer account's rootless daemon,
never in the system daemon. The corpus client gets only the verified kit (read-only), bounded scratch
space and no network, Docker API, secret or product storage. Besides the corpus, the run reads the
effective cgroup limits of the exact launcher sandbox flags, proves the absence of network and secrets,
and exercises contained failures (OOM, SIGKILL, external deadline, daemon restart). Only synthetic data
is used and only resources created by this run are stopped or removed.

Evidence (one new directory per run, never overwritten): evidence.json, corpus.log, probe.txt, SHA256SUMS.
Manual: docs/operations/media-sanitizer-host.md. P174-3/4 stay candidates; a pass here is not an
operational acceptance, capacity approval or activation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import pwd
import grp
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from docker_runtime import validate_runtime_security
from launcher import Profile

# Same pinned SDK image as the "Media isolation" workflow (guarded by a test).
SDK_IMAGE = ("mcr.microsoft.com/dotnet/sdk:10.0.402-noble"
             "@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa")
ARCHITECTURES = {"x86_64": "amd64", "aarch64": "arm64"}
CORPUS_ASSEMBLY = "backend/tests/Duora.MediaCorpusTests/bin/Debug/net10.0/Duora.MediaCorpusTests.dll"
REQUIRED_KIT_FILES = frozenset({
    "global.json", CORPUS_ASSEMBLY,
    "backend/tests/Duora.MediaCorpusTests/bin/Debug/net10.0/Duora.MediaCorpusTests.runtimeconfig.json",
    "backend/tests/Duora.MediaBaselineHost/bin/Debug/net10.0/Duora.MediaBaselineHost.dll",
})
# Corpus client limits, identical to the hosted-runner step "Complete standalone corpus".
CLIENT_MEMORY_BYTES = 1073741824
CLIENT_CPU_MAX = "50000 100000"
CLIENT_PIDS = 256
CLIENT_TMPFS_BYTES = 536870912
CORPUS_DEADLINE_SECONDS = 1200
SAMPLES_PER_CASE = 2
# P174-4 candidate sandbox, as imposed by Profile.create_args (launcher.py).
SANDBOX_EXPECTED = {
    "memory.max": "536870912", "memory.swap.max": "0", "cpu.max": "100000 100000", "pids.max": "64",
    "uid": "1654:1654", "Seccomp": "2", "NoNewPrivs": "1", "CapEff": "0000000000000000",
    "interfaces": "lo,", "dns": "failed", "tcp": "failed", "rootfs": "readonly", "probe": "done",
}
FORBIDDEN_PATHS = tuple(PurePosixPath(path) for path in (
    "/run/secrets", "/var/run/docker.sock", "/run/docker.sock", "/run/user", "/var/lib/docker",
    "/var/lib/duora-media", "/etc/duora", "/var/lib/duora"))
SECRET_ENV_HINTS = ("PASSWORD", "SECRET", "TOKEN", "KEY", "CONNECTIONSTRING", "AWS_", "B2_", "DOCKER_", "SSH_")
ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")
SUMMARY = re.compile(r"Total: (\d+), Errors: (\d+), Failed: (\d+), Skipped: (\d+), Not Run: (\d+)")
BASELINE_MARKER = "sanitizer-baseline "
# The 18 trusted synthetic cases (S0 corpus); a test keeps this equal to the hosted-runner harness list.
CORPUS_CASES = frozenset({
    "jpeg-20mp-texture", "png-20mp-texture", "webp-20mp-texture", "png-dense-noise", "jpeg-metadata-icc",
    "png-metadata-icc", "webp-metadata-icc", "jpeg-gps-rotation", "png-html-polyglot", "jpeg-zip-polyglot",
    "png-animation", "webp-animation", "png-declared-jpeg", "jpeg-bomb", "png-bomb", "webp-bomb", "png-truncated",
    "jpeg-byte-limit"})

# Probe executed with the launcher's sandbox flags; prints only KEY=VALUE lines, never file contents.
PROBE_SCRIPT = r"""
echo "memory.max=$(cat /sys/fs/cgroup/memory.max)"
echo "memory.swap.max=$(cat /sys/fs/cgroup/memory.swap.max)"
echo "cpu.max=$(cat /sys/fs/cgroup/cpu.max)"
echo "pids.max=$(cat /sys/fs/cgroup/pids.max)"
echo "uid=$(id -u):$(id -g)"
grep -E '^(Seccomp|NoNewPrivs|CapEff):' /proc/self/status | sed 's/:[[:space:]]*/=/'
echo "interfaces=$(tail -n +3 /proc/net/dev | cut -d: -f1 | tr -d ' ' | sort | tr '\n' ',')"
if getent hosts example.com >/dev/null 2>&1; then echo dns=resolved; else echo dns=failed; fi
if timeout 3 bash -c 'exec 3<>/dev/tcp/1.1.1.1/443' 2>/dev/null; then echo tcp=connected; else echo tcp=failed; fi
if touch /duora-probe-write 2>/dev/null; then echo rootfs=writable; else echo rootfs=readonly; fi
for p in /run/secrets /var/run/docker.sock /run/docker.sock /var/lib/duora-media /etc/duora; do
  if [ -e "$p" ]; then echo "present=$p"; fi
done
echo "env=$(env | cut -d= -f1 | sort | tr '\n' ',')"
echo probe=done
"""
# Holds ~768 MiB in one shell variable: above the 512 MiB sandbox, so the kernel must kill it.
OOM_SCRIPT = "x=$(head -c 805306368 /dev/zero | tr '\\000' a); echo survived"
SLEEP_SCRIPT = "sleep 300"


class HostCheckError(RuntimeError):
    """A precondition of the host run is missing; nothing was started."""


# ---------------------------------------------------------------- kit

def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def safe_member(name: str) -> PurePosixPath:
    path = PurePosixPath(name)
    if not name or "\\" in name or "\x00" in name or path.is_absolute() or ".." in path.parts:
        raise HostCheckError("kit member with unsafe path")
    return path


def verify_kit(archive: Path, expected_sha256: str, architecture: str) -> dict[str, Any]:
    """Check the ZIP against its expected hash and its own closed manifest before any extraction."""
    if re.fullmatch(r"[0-9a-f]{64}", expected_sha256) is None:
        raise HostCheckError("kit SHA-256 must be 64 lowercase hex digits")
    if sha256_file(archive) != expected_sha256:
        raise HostCheckError("kit archive does not match the expected SHA-256")
    with zipfile.ZipFile(archive) as bundle:
        sizes: dict[str, int] = {}
        for info in bundle.infolist():
            safe_member(info.filename)
            if stat.S_ISLNK(info.external_attr >> 16):
                raise HostCheckError("kit contains a symbolic link")
            if info.is_dir():
                continue
            if info.filename in sizes:
                raise HostCheckError("kit contains a duplicated member")
            sizes[info.filename] = info.file_size
        try:
            manifest = json.loads(bundle.read("instrumentation.json"))
        except (KeyError, json.JSONDecodeError) as error:
            raise HostCheckError("kit manifest is missing or invalid") from error
    if (manifest.get("schema") != 1 or manifest.get("kind") != "standalone-instrumentation"
            or manifest.get("repository") != "BipoXDXD/duora"
            or manifest.get("ci", {}).get("architecture") != architecture):
        raise HostCheckError("kit manifest is not standalone instrumentation for this architecture")
    listed = {row["path"]: row for row in manifest.get("files", [])}
    payload = set(sizes) - {"instrumentation.json"}
    if payload != set(listed) or len(listed) != len(manifest["files"]):
        raise HostCheckError("kit members differ from its manifest")
    if any(sizes[path] != row["bytes"] for path, row in listed.items()):
        raise HostCheckError("kit member size differs from its manifest")
    if not REQUIRED_KIT_FILES <= payload or any(Path(path).name.casefold() == "duora.integrationtests.dll"
                                                for path in payload):
        raise HostCheckError("kit lacks the standalone corpus or carries the integration assembly")
    return manifest


def extract_kit(archive: Path, manifest: dict[str, Any], destination: Path) -> None:
    """Extract only the manifest files, re-hashing every byte; world-readable for the rootless account."""
    destination.mkdir(mode=0o755)
    with zipfile.ZipFile(archive) as bundle:
        for row in manifest["files"]:
            relative = safe_member(row["path"])
            target = destination.joinpath(*relative.parts)
            for parent in reversed(target.parents):
                if parent.is_relative_to(destination) and not parent.exists():
                    parent.mkdir(mode=0o755)
            digest = hashlib.sha256()
            with bundle.open(row["path"]) as source, target.open("xb") as output:
                while chunk := source.read(1024 * 1024):
                    digest.update(chunk)
                    output.write(chunk)
            if digest.hexdigest() != row["sha256"]:
                raise HostCheckError("kit member hash differs from its manifest")
            target.chmod(0o644)


# ---------------------------------------------------------------- parsing and assessment

def parse_corpus_log(text: str) -> dict[str, Any]:
    cases: dict[str, int] = {}
    peak = 0
    summary = None
    for raw in text.splitlines():
        line = ANSI.sub("", raw)
        marker = line.find(BASELINE_MARKER)
        if marker >= 0:
            sample = json.loads(line[marker + len(BASELINE_MARKER):].strip())
            cases[sample["Case"]] = cases.get(sample["Case"], 0) + 1
            peak = max(peak, int(sample["Sample"].get("PeakWorkingSetBytes") or 0))
        found = SUMMARY.search(line)
        if found:
            summary = dict(zip(("total", "errors", "failed", "skipped", "notRun"), map(int, found.groups())))
    return {"cases": cases, "summary": summary, "peakWorkingSetBytes": peak}


def assess_corpus(parsed: dict[str, Any]) -> list[str]:
    failures = []
    cases = parsed["cases"]
    if set(cases) != CORPUS_CASES:
        failures.append(f"corpus cases differ from the 18 trusted cases ({len(cases)} seen)")
    for name, count in sorted(cases.items()):
        if count != SAMPLES_PER_CASE:
            failures.append(f"case {name} has {count} samples")
    summary = parsed["summary"]
    if summary is None:
        failures.append("xUnit summary missing")
    elif summary["total"] < 1 or any(summary[key] for key in ("errors", "failed", "skipped", "notRun")):
        failures.append("xUnit reported errors, failures, skipped or not-run tests")
    return failures


def parse_probe(text: str) -> dict[str, list[str]]:
    values: dict[str, list[str]] = {}
    for line in text.splitlines():
        key, separator, value = line.partition("=")
        if separator:
            values.setdefault(key.strip(), []).append(value.strip())
    return values


def secret_like(names: list[str]) -> list[str]:
    return sorted(name for name in names if any(hint in name.upper() for hint in SECRET_ENV_HINTS))


def assess_probe(values: dict[str, list[str]]) -> list[str]:
    failures = []
    for key, expected in SANDBOX_EXPECTED.items():
        if values.get(key) != [expected]:
            failures.append(f"sandbox {key} is {values.get(key)}, expected {expected}")
    for path in values.get("present", []):
        failures.append(f"sandbox sees {path}")
    environment = [name for name in values.get("env", [""])[0].split(",") if name]
    for name in secret_like(environment):
        failures.append(f"sandbox environment has {name}")
    return failures


def parse_mountinfo(text: str) -> list[dict[str, str]]:
    rows = []
    for line in text.splitlines():
        fields, separator, tail = line.partition(" - ")
        parts = fields.split()
        rest = tail.split()
        if not separator or len(parts) < 6 or len(rest) < 2:
            raise HostCheckError("unexpected mountinfo line")
        rows.append({"root": parts[3], "mountPoint": parts[4], "options": parts[5],
                     "fsType": rest[0], "source": rest[1]})
    return rows


def assess_mounts(rows: list[dict[str, str]], readonly: set[str]) -> list[str]:
    failures = []
    for row in rows:
        for field in ("mountPoint", "root"):
            path = PurePosixPath(row[field].replace("\\040", " "))
            if path.name == "docker.sock" or any(path == bad or path.is_relative_to(bad) for bad in FORBIDDEN_PATHS):
                failures.append(f"client mount exposes {row[field]}")
    options = {row["mountPoint"]: row["options"].split(",") for row in rows}
    for point in sorted(readonly):
        if "ro" not in options.get(point, []):
            failures.append(f"client mount {point} is not read-only")
    return failures


def interfaces(netdev: str) -> list[str]:
    return sorted(line.split(":", 1)[0].strip() for line in netdev.splitlines()[2:] if ":" in line)


def cgroup_path(proc_cgroup: str) -> str:
    for line in proc_cgroup.splitlines():
        if line.startswith("0::"):
            return line[3:]
    raise HostCheckError("process is not in a cgroup v2 hierarchy")


def sandbox_args(profile: Profile, job_id: str, script: str) -> list[str]:
    """The launcher's own create flags, with only the entrypoint replaced by a probe shell."""
    args = profile.create_args(job_id)
    index = args.index("--entrypoint")
    if args[index + 1:] != ["dotnet", profile.image, "Duora.MediaSanitizer.dll"]:
        raise HostCheckError("launcher create arguments changed; review the probe derivation")
    return args[:index] + ["--entrypoint", "/bin/sh", profile.image, "-c", script]


def client_args(name: str, kit: Path, image: str) -> list[str]:
    return ["create", "--name", name, "--pull", "never", "--network", "none", "--read-only",
            "--user", "1654:1654", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--memory", str(CLIENT_MEMORY_BYTES), "--memory-swap", str(CLIENT_MEMORY_BYTES),
            "--cpus", "0.5", "--pids-limit", str(CLIENT_PIDS), "--log-driver", "none",
            "--tmpfs", f"/tmp:rw,noexec,nosuid,nodev,size={CLIENT_TMPFS_BYTES},mode=1777",
            "--mount", f"type=bind,src={kit},dst=/workspace,readonly", "--workdir", "/workspace",
            "--env", "DOTNET_NOLOGO=true", "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=true",
            "--entrypoint", "/usr/share/dotnet/dotnet", image,
            "/workspace/" + CORPUS_ASSEMBLY, "-showLiveOutput", "-reporter", "verbose"]


def assess_client_cgroup(chain: list[dict[str, str]]) -> list[str]:
    """chain[0] is the container scope; the rest are its ancestors up to the account slice."""
    failures = []
    expected = {"memory.max": str(CLIENT_MEMORY_BYTES), "memory.swap.max": "0",
                "cpu.max": CLIENT_CPU_MAX, "pids.max": str(CLIENT_PIDS)}
    for key, value in expected.items():
        if chain[0].get(key) != value:
            failures.append(f"client {key} is {chain[0].get(key)}, expected {value}")
    slices = [row for row in chain if re.fullmatch(r".*/user-\d+\.slice", row["path"])]
    if len(slices) != 1:
        failures.append("client cgroup is not below the account slice")
    elif slices[0].get("memory.max") in (None, "max") or slices[0].get("memory.swap.max") != "0":
        failures.append("account slice has no memory ceiling or allows swap")
    return failures


# ---------------------------------------------------------------- host side effects

class HostRun:
    def __init__(self, user: str, evidence: Path, staging: Path):
        account = pwd.getpwnam(user)
        self.user = user
        self.uid = account.pw_uid
        self.evidence = evidence
        self.staging = staging
        self.socket = Path(f"/run/user/{self.uid}/docker.sock")
        self.cli = staging / "cli"
        self.created: list[str] = []
        self.checks: dict[str, Any] = {}
        self.failures: list[str] = []

    def docker(self, *args: str, timeout: float = 60, check: bool = True) -> subprocess.CompletedProcess[str]:
        command = ["runuser", "-u", self.user, "--", "/usr/bin/docker", "--config", str(self.cli),
                   "--host", "unix://" + str(self.socket), *args]
        return subprocess.run(command, capture_output=True, text=True, timeout=timeout, check=check,
                              env={"PATH": "/usr/bin:/bin", "HOME": "/nonexistent", "LC_ALL": "C"})

    def user_systemctl(self, *args: str, timeout: float = 120) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["systemctl", f"--machine={self.user}@.host", "--user", *args],
                              capture_output=True, text=True, timeout=timeout, check=False)

    def record(self, name: str, failures: list[str], **details: Any) -> None:
        self.checks[name] = {"passed": not failures, "failures": failures, **details}
        self.failures.extend(f"{name}: {failure}" for failure in failures)

    def preflight(self) -> None:
        info = self.socket.lstat() if self.socket.exists() else None
        if info is None or not stat.S_ISSOCK(info.st_mode) or info.st_uid != self.uid or info.st_mode & 0o077:
            raise HostCheckError("rootless socket missing or not private; start the dedicated daemon first")
        self.cli.mkdir(mode=0o700)
        os.chown(self.cli, self.uid, -1)
        runtime = json.loads(self.docker("info", "--format", "{{json .}}").stdout)
        try:
            validate_runtime_security(runtime)
        except ValueError as error:
            raise HostCheckError("dedicated daemon is not rootless/systemd/cgroup v2/builtin seccomp") from error
        if runtime.get("Driver") != "overlay2":
            raise HostCheckError("dedicated daemon must use the overlay2 backend")
        if self.docker("ps", "--all", "--quiet").stdout.strip():
            raise HostCheckError("dedicated daemon already holds containers; it must be exclusive to this run")
        self.checks["runtime"] = {"serverVersion": runtime.get("ServerVersion"), "driver": runtime.get("Driver"),
                                  "cgroupDriver": runtime.get("CgroupDriver"),
                                  "securityOptions": runtime.get("SecurityOptions")}
        self.checks["accountSlice"] = self.read_limits(Path(f"/sys/fs/cgroup/user.slice/user-{self.uid}.slice"))
        self.checks["systemContainersBefore"] = self.system_containers()

    def system_containers(self) -> list[str]:
        # Only identifiers of the system daemon's running containers: proves they were left untouched.
        result = subprocess.run(["/usr/bin/docker", "ps", "--quiet", "--no-trunc"], capture_output=True,
                                text=True, timeout=30, check=True)
        return sorted(result.stdout.split())

    @staticmethod
    def read_limits(directory: Path) -> dict[str, str]:
        values = {"path": str(directory)}
        for name in ("memory.max", "memory.swap.max", "cpu.max", "pids.max"):
            file = directory / name
            if file.exists():
                values[name] = file.read_text().strip()
        return values

    def ensure_sdk_image(self) -> str:
        if self.docker("image", "inspect", SDK_IMAGE, check=False).returncode != 0:
            self.docker("pull", "--quiet", SDK_IMAGE, timeout=1200)
        image_id = self.docker("image", "inspect", "--format", "{{.Id}}", SDK_IMAGE).stdout.strip()
        if re.fullmatch(r"sha256:[0-9a-f]{64}", image_id) is None:
            raise HostCheckError("SDK image is not addressed by its configuration digest")
        self.checks["sdkImage"] = {"reference": SDK_IMAGE, "configId": image_id}
        return image_id

    def sandbox_profile(self, image_id: str) -> Profile:
        gid = grp.getgrnam("duora-media-client").gr_gid
        return Profile.from_dict({"schema": 1, "image": image_id, "runtime_socket": str(self.socket),
                                  "control_socket": "/run/duora-media/control.sock",
                                  "journal_directory": "/var/lib/duora-media-launcher/journal",
                                  "worker_uid": 1654, "control_gid": gid}, self.uid)

    def create_sandbox(self, profile: Profile, script: str) -> str:
        job_id = os.urandom(16).hex()
        args = sandbox_args(profile, job_id, script)
        name = args[args.index("--name") + 1]
        self.created.append(name)
        self.docker(*args)
        return name

    def state(self, name: str) -> dict[str, Any]:
        return json.loads(self.docker("inspect", "--format", "{{json .State}}", name).stdout)

    def remove(self, name: str) -> float:
        began = time.monotonic()
        self.docker("rm", "--force", name, check=False, timeout=30)
        if self.docker("ps", "--all", "--quiet", "--filter", f"name=^/{name}$").stdout.strip():
            raise HostCheckError("container still present after removal")
        return round(time.monotonic() - began, 3)

    def probe_sandbox(self, profile: Profile) -> None:
        name = self.create_sandbox(profile, PROBE_SCRIPT)
        host = json.loads(self.docker("inspect", "--format", "{{json .HostConfig}}", name).stdout)
        output = self.docker("start", "--attach", name, timeout=60, check=False).stdout
        (self.evidence / "probe.txt").write_text(output)
        state = self.state(name)
        self.remove(name)
        failures = assess_probe(parse_probe(output))
        if state["ExitCode"] != 0:
            failures.append(f"probe exited {state['ExitCode']}")
        if (host["NetworkMode"] != "none" or not host["ReadonlyRootfs"] or host["Privileged"]
                or host.get("Binds") or host["CapDrop"] != ["ALL"] or host.get("CapAdd")):
            failures.append("sandbox host configuration differs from the launcher profile")
        self.record("sandboxLimitsAndIsolation", failures, values=parse_probe(output),
                    hostConfig={key: host.get(key) for key in ("NetworkMode", "ReadonlyRootfs", "Privileged",
                                                               "Memory", "MemorySwap", "NanoCpus", "PidsLimit",
                                                               "CapDrop", "SecurityOpt", "IpcMode")})

    def run_corpus(self, kit: Path, image_id: str) -> None:
        name = "duora-corpus-" + os.urandom(8).hex()
        self.created.append(name)
        self.docker(*client_args(name, kit, image_id))
        log_path = self.evidence / "corpus.log"
        observed: dict[str, Any] = {}
        began = time.monotonic()
        with log_path.open("w") as log:
            command = ["runuser", "-u", self.user, "--", "/usr/bin/docker", "--config", str(self.cli),
                       "--host", "unix://" + str(self.socket), "start", "--attach", name]
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT,
                                       env={"PATH": "/usr/bin:/bin", "HOME": "/nonexistent", "LC_ALL": "C"})
            sampler = threading.Thread(target=self.observe_client, args=(name, observed, process), daemon=True)
            sampler.start()
            try:
                process.wait(timeout=CORPUS_DEADLINE_SECONDS)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=30)
                observed.setdefault("errors", []).append("corpus exceeded the external deadline")
            sampler.join(timeout=10)
        elapsed = round(time.monotonic() - began, 3)
        state = self.state(name)
        self.remove(name)
        parsed = parse_corpus_log(log_path.read_text(errors="replace"))
        failures = assess_corpus(parsed) + observed.get("errors", [])
        if state["ExitCode"] != 0 or state["OOMKilled"]:
            failures.append(f"corpus client exited {state['ExitCode']} (OOMKilled={state['OOMKilled']})")
        if "cgroupChain" in observed:
            failures += assess_client_cgroup(observed["cgroupChain"])
            failures += assess_mounts(observed["mounts"], {"/", "/workspace"})
            if observed["interfaces"] != ["lo"]:
                failures.append(f"client network interfaces {observed['interfaces']}")
            failures += [f"client environment has {key}" for key in secret_like(observed["environment"])]
        else:
            failures.append("client process was never observed from the host")
        self.record("corpus", failures, elapsedSeconds=elapsed, cases=parsed["cases"], summary=parsed["summary"],
                    peakWorkingSetBytes=parsed["peakWorkingSetBytes"],
                    observed={key: value for key, value in observed.items() if key != "mounts"},
                    mountPoints=[(row["mountPoint"], row["options"]) for row in observed.get("mounts", [])])

    def observe_client(self, name: str, observed: dict[str, Any], process: subprocess.Popen[bytes]) -> None:
        """Read the client's real cgroup, mounts, network and environment names from the host side."""
        try:
            pid = 0
            deadline = time.monotonic() + 60
            while not pid and time.monotonic() < deadline and process.poll() is None:
                pid = int(self.docker("inspect", "--format", "{{.State.Pid}}", name, check=False).stdout.strip() or 0)
                time.sleep(0.5)
            if not pid:
                return
            proc = Path(f"/proc/{pid}")
            relative = cgroup_path((proc / "cgroup").read_text())
            scope = Path("/sys/fs/cgroup" + relative)
            chain = [self.read_limits(directory) for directory in [scope, *scope.parents]
                     if directory.is_relative_to(Path(f"/sys/fs/cgroup/user.slice/user-{self.uid}.slice"))]
            observed["cgroupChain"] = chain
            observed["mounts"] = parse_mountinfo((proc / "mountinfo").read_text())
            observed["interfaces"] = interfaces((proc / "net/dev").read_text())
            observed["environment"] = sorted(item.split(b"=", 1)[0].decode() for item in
                                             (proc / "environ").read_bytes().split(b"\0") if item)
            peak_memory = peak_pids = oom_kills = 0
            while process.poll() is None and scope.exists():
                try:
                    peak_memory = max(peak_memory, int((scope / "memory.current").read_text()))
                    peak_pids = max(peak_pids, int((scope / "pids.current").read_text()))
                    events = dict(line.split() for line in (scope / "memory.events").read_text().splitlines())
                    oom_kills = int(events.get("oom_kill", 0))
                except (OSError, ValueError):
                    break
                time.sleep(1)
            observed.update(peakMemoryCurrentBytes=peak_memory, peakPids=peak_pids, oomKills=oom_kills)
        except (OSError, ValueError, HostCheckError, subprocess.SubprocessError) as error:
            observed.setdefault("errors", []).append(f"host observation failed: {type(error).__name__}")

    def exercise_failures(self, profile: Profile) -> None:
        name = self.create_sandbox(profile, OOM_SCRIPT)
        result = self.docker("start", "--attach", name, timeout=120, check=False)
        state = self.state(name)
        cleanup = self.remove(name)
        failures = []
        if state["ExitCode"] != 137 or "survived" in result.stdout:
            failures.append(f"allocation above the sandbox limit was not killed (exit {state['ExitCode']})")
        self.record("oomKill", failures, exitCode=state["ExitCode"], oomKilled=state["OOMKilled"],
                    cleanupSeconds=cleanup)

        name = self.create_sandbox(profile, SLEEP_SCRIPT)
        self.docker("start", name)
        running = self.wait_for(lambda: self.state(name)["Running"], 15)
        self.docker("kill", "--signal", "KILL", name)
        stopped = self.wait_for(lambda: not self.state(name)["Running"], 15)
        state = self.state(name)
        cleanup = self.remove(name)
        failures = [] if running and stopped and state["ExitCode"] == 137 and not state["OOMKilled"] else [
            f"SIGKILL not observed as exit 137 (running={running}, stopped={stopped}, exit={state['ExitCode']})"]
        self.record("sigkill", failures, exitCode=state["ExitCode"], cleanupSeconds=cleanup)

        name = self.create_sandbox(profile, SLEEP_SCRIPT)
        began = time.monotonic()
        try:
            self.docker("start", "--attach", name, timeout=5, check=False)
            failures = ["sleeping sandbox returned before the external deadline"]
        except subprocess.TimeoutExpired:
            failures = []
        cleanup = self.remove(name)
        total = round(time.monotonic() - began, 3)
        if cleanup > 10:
            failures.append(f"deadline cleanup took {cleanup}s")
        self.record("externalDeadline", failures, deadlineSeconds=5, cleanupSeconds=cleanup, totalSeconds=total)

        restart = self.user_systemctl("restart", "docker.service")
        healthy = self.wait_for(lambda: self.docker("info", "--format", "{{json .}}", check=False).returncode == 0, 90)
        failures = [] if restart.returncode == 0 and healthy else ["dedicated daemon did not come back after restart"]
        if healthy:
            try:
                validate_runtime_security(json.loads(self.docker("info", "--format", "{{json .}}").stdout))
            except ValueError:
                failures.append("restarted daemon lost rootless/seccomp/systemd guarantees")
            if self.docker("ps", "--all", "--quiet").stdout.strip():
                failures.append("containers left behind after the daemon restart")
        self.record("daemonRestart", failures)

    @staticmethod
    def wait_for(condition: Any, seconds: float) -> bool:
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                if condition():
                    return True
            except (subprocess.SubprocessError, json.JSONDecodeError, KeyError):
                pass
            time.sleep(0.5)
        return False

    def dispose(self) -> None:
        for name in self.created:
            self.docker("rm", "--force", name, check=False, timeout=30)
        leftovers = self.docker("ps", "--all", "--quiet", check=False).stdout.split()
        self.record("cleanup", [f"{len(leftovers)} containers left in the dedicated daemon"] if leftovers else [])
        after = self.system_containers()
        before = self.checks.get("systemContainersBefore", after)
        self.record("systemDaemonPreserved", [] if after == before else ["system daemon containers changed"],
                    before=len(before), after=len(after))


def write_evidence(evidence: Path, report: dict[str, Any]) -> None:
    (evidence / "evidence.json").write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
    sums = [f"{sha256_file(path)}  {path.name}" for path in sorted(evidence.iterdir()) if path.name != "SHA256SUMS"]
    (evidence / "SHA256SUMS").write_text("\n".join(sums) + "\n")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--kit-zip", type=Path, required=True, help="media-host-instrumentation-<arch> ZIP")
    parser.add_argument("--kit-sha256", required=True, help="SHA-256 of that ZIP, from the GitHub artifact")
    parser.add_argument("--evidence-dir", type=Path, required=True, help="new directory for this run")
    parser.add_argument("--user", default="duora-media-launcher")
    parser.add_argument("--skip-failures", action="store_true", help="omit the contained failure exercises")
    args = parser.parse_args(argv)
    if os.geteuid() != 0 or sys.platform != "linux":
        parser.error("run as root on the Linux host")
    architecture = ARCHITECTURES.get(platform.machine())
    if architecture is None:
        parser.error("unsupported architecture")
    evidence = args.evidence_dir.resolve()
    evidence.mkdir(mode=0o700, parents=False, exist_ok=False)
    report: dict[str, Any] = {"schema": 1, "kind": "media-host-corpus", "candidateOnly": True,
                              "operationalAcceptance": False, "architecture": architecture,
                              "startedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                              "bootId": Path("/proc/sys/kernel/random/boot_id").read_text().strip()}
    staging = Path(tempfile.mkdtemp(prefix="duora-media-corpus-", dir="/var/tmp"))
    staging.chmod(0o755)
    run = None
    try:
        manifest = verify_kit(args.kit_zip, args.kit_sha256, architecture)
        report["kit"] = {"sha256": args.kit_sha256, "files": len(manifest["files"]),
                         "source": manifest.get("source"), "ci": manifest.get("ci")}
        run = HostRun(args.user, evidence, staging)
        run.preflight()
        kit = staging / "kit"
        extract_kit(args.kit_zip, manifest, kit)
        image_id = run.ensure_sdk_image()
        profile = run.sandbox_profile(image_id)
        run.probe_sandbox(profile)
        run.run_corpus(kit, image_id)
        if not args.skip_failures:
            run.exercise_failures(profile)
    except (HostCheckError, subprocess.SubprocessError, OSError, KeyError, json.JSONDecodeError) as error:
        report["aborted"] = f"{type(error).__name__}: {error}"
    finally:
        if run is not None:
            try:
                run.dispose()
            except (subprocess.SubprocessError, OSError) as error:
                report["disposeError"] = type(error).__name__
            report["checks"] = run.checks
            report["failures"] = run.failures
        # Temporary copy of the verified kit; a failed removal must not prevent writing the evidence.
        shutil.rmtree(staging, ignore_errors=True)
        report["finishedAtUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        report["passed"] = bool(run and not run.failures and "aborted" not in report and "disposeError" not in report)
        write_evidence(evidence, report)
    print(json.dumps({"passed": report["passed"], "evidence": str(evidence),
                      "failures": report.get("failures", []), "aborted": report.get("aborted")}, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
