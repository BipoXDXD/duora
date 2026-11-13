"""Disposable root proof of the PostgreSQL digest swap with real Docker; never a production entry point.

Runs the installed bundle's real command against real PostgreSQL, pgBackRest and MinIO, on a host that owns the fixed
paths (a hosted runner or a docker:dind container). web, worker, edge and migrator are minimal stand-ins here: the
application path with real images is continuity_lab.py, which reuses SwapLab.

    postgres_swap_bench.py --mode cycle|matrix|unhealthy|pitr|cleanup

It owns the fixed production paths while it runs, so it refuses any host that is not explicitly disposable: a hosted
runner of this repository, or a host carrying a marker with its own hostname. Nothing is created or removed on refusal.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any, Callable
from unittest.mock import patch

import duora_deploy as executor
import duora_secrets
import install_executor as installer
import postgres_swap as swap
import reconcile_payload as payload
import runtime_guard as guard
import smoke_local

REPO = Path(__file__).resolve().parents[2]
INGRESS = Path("/var/lib/coolify-release")
RELEASES = Path("/opt/duora/releases")
PIN = Path("/etc/duora/coolify-deploy.json")
BACKUP_COMMAND = Path("/usr/local/sbin/duora-backup")
DISPOSABLE_MARKER = Path("/etc/duora-swap-disposable-host")
CLAIM = Path("/run/duora-swap-bench-claim.json")
CLAIM_FIELDS = {"root", "registry", "ancestors"}
PID_CEILING = 2 ** 31  # pid_t is a signed 32-bit integer: os.kill overflows at this value.
FIXTURE = REPO / "scripts/deploy/fixtures/postgres-swap-target.json"
LOCK = REPO / "infra/images/postgres-component-lock.json"
RECEIVER = REPO / "scripts/release/coolify/receiver.py"
DRIVER = REPO / "scripts/deploy/postgres_swap_driver.py"
STANDIN_BASE = "busybox:1.37@sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec"
MIGRATION = "20261105120000_RegistrationReconciliationManifest"
BUCKET = "duora-swap"
ROWS = 25
RECOVERY_MARGIN_SECONDS = 2
MINIO_IMAGE = "bitnamilegacy/minio:2025.7.23-debian-12-r5@sha256:6dabb4a2088c9a79908de3bc05f4586c23ad2182c8908e7e3acbf61c1467fb20"
COMMAND_SECONDS = 900
# Every forward state crossed by SIGKILL, each resolved both ways; then the two states with a single exit.
KILL_STATES = ("journaled", "verify-backup", "quiesce", "swap-runtime", "verify-runtime", "reopen", "commit")
CLOSE_KILL_STATES = ("awaiting-reseal", "closing", "aborted-awaiting-reseal")
ABORT_ONLY_STATE = "aborting"
# The exact refusal each step expects: any other error would be a proof that proves nothing.
REFUSED_BY_FENCE = "PostgreSQL swap aberto"
REFUSED_BY_DIGEST = "Cannot qualify the unchanged database/backup runtime"
REFUSED_DIGEST_CAUSE = "Runtime differs from the desired immutable component"  # The executor's message covers any failure.
REFUSED_BY_COMPOSE = "Processo compose do projeto ainda vivo"
REFUSED_BY_RESEAL = "rode a ressela do instalador"
REFUSED_RESUME_OF_ABORT = "Abort interrompido só continua com --abort"


class ProofFailure(RuntimeError):
    """The proof did not establish what it claims."""


def log(message: str) -> None:
    print("[swap-proof] " + message, flush=True)


def docker(*arguments: str, timeout: int = COMMAND_SECONDS) -> str:
    result = subprocess.run(["docker", *arguments], text=True, capture_output=True, check=False, timeout=timeout)
    if result.returncode:
        raise ProofFailure("docker " + " ".join(arguments[:4]) + ": " + result.stderr[-1500:])
    return result.stdout.strip()


def target_for(architecture: str) -> str:
    return json.loads(FIXTURE.read_text())["images"][architecture]


def alternative_digest(running: str, architecture: str) -> str:
    """Always a real swap: the qualified target, or back to its origin once the lock has moved there."""
    fixture = json.loads(FIXTURE.read_text())
    for candidate in (fixture["images"][architecture], fixture["origin"][architecture]):
        if candidate != running:
            return candidate
    raise ProofFailure("Fixture has no digest different from " + running + "; update it instead of skipping the proof")


def source_for(architecture: str) -> str:
    return json.loads(LOCK.read_text())["images"][architecture]


def ensure_root_directory(path: Path, mode: int = 0o755) -> None:
    """Every missing level, not only the last: mkdir(parents=True) leaves the others to the umask and the parent's ACL."""
    for level in (*reversed(path.parents), path):
        if not level.exists():
            os.mkdir(level)
            os.chown(level, 0, 0)
            os.chmod(level, mode)


def write_root(path: Path, raw: bytes, *, mode: int = 0o600) -> None:
    ensure_root_directory(path.parent)
    path.write_bytes(raw)
    path.chmod(mode)


def write_backup_command(paths: executor.Paths) -> None:
    # Docker Desktop and hosted runners have no reviewed DOCKER-USER chain; same exception as restore_drill.py.
    wrapper = ("#!/bin/sh\nexec " + sys.executable + " " + str(REPO / "scripts/deploy/duora_backup.py")
               + ' "$@" --state-dir ' + str(paths.state_dir) + " --skip-egress-check\n")
    write_root(BACKUP_COMMAND, wrapper.encode(), mode=0o755)


def prepare_fixed_paths(current: executor.Release, paths: executor.Paths) -> None:
    """The fixed production locations, on a machine that exists only for this proof."""
    for path in (PIN, RELEASES):
        if path.exists():
            raise ProofFailure("Refusing to adopt existing host state: " + str(path))
    manifest = swap._swap_manifest_bytes(current)
    write_root(RELEASES / (current.release_id + ".json"), manifest, mode=0o644)
    pin = {"application_uuid": "swap-proof", "compose": str(paths.compose_file),
           "compose_sha256": hashlib.sha256(paths.compose_file.read_bytes()).hexdigest(),
           "manifest": str(RELEASES / (current.release_id + ".json")),
           "manifest_sha256": hashlib.sha256(manifest).hexdigest(),
           "observer_environment": {"DUORA_OBSERVER_IMAGE": current.images["edge"]}}
    write_root(PIN, executor._recovery_pin_bytes(pin))
    # Reaudit: what the proof created, and whatever appeared after the claim, must also pass the executor's reader.
    protect_ancestors(unprotected_ancestors(protected_reads(paths, current)))


def prepare_ingress() -> None:
    ensure_root_directory(INGRESS)
    ensure_root_directory(INGRESS / "duora", 0o700)
    for lock in (INGRESS / "host.lock", INGRESS / "duora/release.lock"):
        lock.touch(mode=0o600)
        lock.chmod(0o600)


def host_is_disposable(environment: dict | None = None) -> bool:
    values = os.environ if environment is None else environment
    if (values.get("GITHUB_ACTIONS") == "true" and values.get("RUNNER_ENVIRONMENT") == "github-hosted"
            and values.get("GITHUB_REPOSITORY") == "BipoXDXD/duora"):
        return True
    try:
        return DISPOSABLE_MARKER.read_text().strip() == socket.gethostname()
    except OSError:
        return False


def require_host_free() -> None:
    """Runs before anything exists to clean: a refusal here leaves the host exactly as it was."""
    raw = claim_bytes()
    if raw is not None:
        claim = parse_claim(raw)
        if claim is not None and "pid" in claim and process_alive(claim["pid"]):
            raise ProofFailure(running_proof(claim["pid"]))
        # It holds the original modes of the ancestors: a new claim would record the protected ones as original.
        raise ProofFailure("A previous proof left its claim at " + str(CLAIM) + ": run --mode cleanup first")
    for path in (PIN.parent, RELEASES.parent, INGRESS, BACKUP_COMMAND):
        if path.exists() or path.is_symlink():
            raise ProofFailure("Refusing to adopt existing host state: " + str(path))
    if docker("ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=duora"):
        raise ProofFailure("Refusing to adopt an existing stack")
    if "duora_pgdata" in docker("volume", "ls", "--format", "{{.Name}}", "--filter", "name=^duora_pgdata$").split():
        raise ProofFailure("Refusing to adopt an existing data volume")


def unprotected_ancestors(targets: list[Path]) -> list[dict]:
    """Existing directories the executor's protected reader would refuse, with what it takes to put them back.

    It is the reader's own rule. Hosted runner images run chmod -R 777 /opt, which no production host does.
    """
    found: dict[str, dict] = {}
    for target in targets:
        for item in reversed((target, *target.parents)):
            if item == item.parent:
                continue
            try:
                info = os.lstat(item)
            except FileNotFoundError:
                continue
            if stat.S_ISLNK(info.st_mode):
                raise ProofFailure("Symlink in the ancestry of a protected read: " + str(item))
            if info.st_uid != 0 or info.st_gid != 0 or info.st_mode & 0o022:
                found[str(item)] = {"path": str(item), "mode": stat.S_IMODE(info.st_mode), "uid": info.st_uid, "gid": info.st_gid}
    return list(found.values())


def protect_ancestors(entries: list[dict]) -> None:
    for entry in entries:
        log("protecting %s during the proof (was %o, %d:%d)" % (entry["path"], entry["mode"], entry["uid"], entry["gid"]))
        os.chown(entry["path"], 0, 0)
        os.chmod(entry["path"], entry["mode"] & ~0o022)


def restore_ancestors(entries: list[dict]) -> None:
    for entry in reversed(entries):
        os.chown(entry["path"], entry["uid"], entry["gid"])
        os.chmod(entry["path"], entry["mode"])


def ancestry_report(path: Path) -> str:
    """What the executor's message leaves out: mode, owner and extended attributes of each ancestor."""
    rows = []
    for item in reversed(path.parents):
        if item == item.parent:
            continue
        try:
            info = os.lstat(item)
        except OSError as error:
            rows.append(f"{item} missing ({type(error).__name__})")
            continue
        list_attributes = getattr(os, "listxattr", None)  # Linux only; a default ACL would show up here.
        try:
            attributes = list_attributes(item, follow_symlinks=False) if list_attributes else []
        except OSError:
            attributes = []
        rows.append(f"{item} {stat.filemode(info.st_mode)} {info.st_uid}:{info.st_gid}" + (" " + ",".join(attributes) if attributes else ""))
    return "; ".join(rows)


def protected_reads(paths: executor.Paths, release: executor.Release) -> list[Path]:
    """Everything the swap entry reads through the executor's protected reader, from the locks to the plan."""
    state, name = paths.state_dir, release.release_id
    return [PIN, INGRESS / "host.lock", INGRESS / "duora/release.lock", state / ".deploy.lock", state / "current.json",
            state / "executor-install-receipt.json", state / guard.BASELINE_NAME, state / "releases" / (name + ".json"),
            state / "releases" / (name + ".env"), RELEASES / (name + ".json"), paths.compose_file, paths.host_env]


def process_alive(pid: int) -> bool:
    """A reused PID or an unreaped zombie reads as alive: the error refuses a cleanup, never allows one."""
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        pass  # It exists and belongs to another user.
    return True


def running_proof(pid: int) -> str:
    return ("Proof PID %d is still running with its claim at %s: wait for it or stop it. A reused PID clears when that "
            "process ends; deleting the claim by hand loses the record of the ancestors' original modes" % (pid, CLAIM))


def literal_cut(literal: bytes, then: bytes) -> bytes:
    """A pattern for any prefix of the literal, reaching then only once the literal is whole."""
    pattern = then
    for byte in reversed(literal):
        pattern = b"(?:" + re.escape(bytes([byte])) + pattern + b")?"
    return pattern


# Every prefix of what json.dump writes, in its key order and default separators.
CUT_CLAIM = re.compile(literal_cut(b'{"root": "', rb'[^"]*(?:"' + literal_cut(b', "registry": "', rb'[^"]*(?:"' + literal_cut(
    b', "ancestors": [', rb'[\[\]{}",: \w/.-]*') + rb')?') + rb')?'))


def claim_bytes() -> bytes | None:
    """None when there is no claim, including one its proof released right now."""
    try:
        return CLAIM.read_bytes()
    except FileNotFoundError:
        return None


def parse_claim(raw: bytes) -> dict | None:
    """None is a claim cut by a kill, which only the former writer left: it dies before claiming anything."""
    try:
        claim = json.loads(raw)
    except json.JSONDecodeError as error:
        if CUT_CLAIM.fullmatch(raw):
            return None
        raise ProofFailure("Unknown claim at " + str(CLAIM)) from error
    except ValueError as error:  # Not UTF-8, or an integer past the interpreter's digit limit.
        raise ProofFailure("Unknown claim at " + str(CLAIM)) from error
    if not isinstance(claim, dict) or not CLAIM_FIELDS <= set(claim) <= CLAIM_FIELDS | {"pid"}:
        raise ProofFailure("Unknown claim at " + str(CLAIM))
    if "pid" in claim and (type(claim["pid"]) is not int or not 0 < claim["pid"] < PID_CEILING):
        raise ProofFailure("Unknown claim at " + str(CLAIM))
    return claim


def publish_claim(claim: dict) -> None:
    """Complete before it is visible, so a kill cannot leave a partial claim; link, unlike rename, refuses an existing one."""
    descriptor, temporary = tempfile.mkstemp(dir=CLAIM.parent, prefix=CLAIM.name + ".")
    try:
        with os.fdopen(descriptor, "w") as file:
            json.dump(claim, file)
        os.link(temporary, CLAIM)
    finally:
        os.unlink(temporary)


def _remove_fixed_paths() -> None:
    for path in (PIN.parent, RELEASES.parent, INGRESS):
        shutil.rmtree(path, ignore_errors=True)
    BACKUP_COMMAND.unlink(missing_ok=True)


class FixedHost:
    """Ownership of the fixed paths: only a run that found them absent may remove them."""

    def __init__(self) -> None:
        self.claimed = False
        self.ancestors: list[dict] = []
        self.umask = 0o022

    def claim(self, *, root: Path, registry: str) -> None:
        require_host_free()
        self.ancestors = unprotected_ancestors([PIN.parent, RELEASES, INGRESS, root])
        publish_claim({"root": str(root), "registry": registry, "ancestors": self.ancestors, "pid": os.getpid()})
        self.claimed = True
        self.umask = os.umask(0o022)
        protect_ancestors(self.ancestors)

    def release(self) -> None:
        if not self.claimed:
            return
        _remove_fixed_paths()
        restore_ancestors(self.ancestors)
        CLAIM.unlink(missing_ok=True)
        os.umask(self.umask)
        self.claimed = False


def cleanup_claimed() -> bool:
    """What a killed run left behind. Without a claim on a disposable host there is nothing of ours to remove."""
    raw = claim_bytes() if host_is_disposable() else None
    if raw is None:
        return False
    claim = parse_claim(raw)
    if claim is None:
        CLAIM.unlink()
        log("truncated claim removed: its run died before claiming anything, and its work directory is not named here")
        return True
    if "pid" not in claim:
        raise ProofFailure("Claim at " + str(CLAIM) + " has no owner PID: run --mode cleanup from the version that wrote it")
    if process_alive(claim["pid"]):
        raise ProofFailure(running_proof(claim["pid"]))
    label = "label=com.docker.compose.project=duora"
    containers = docker("ps", "--all", "--quiet", "--filter", label).split()
    if containers:
        docker("rm", "--force", "--volumes", *containers)
    remove_holds()
    subprocess.run(["docker", "rm", "--force", claim["registry"]], capture_output=True, check=False, timeout=30)
    volumes = docker("volume", "ls", "--quiet", "--filter", label).split()
    if volumes:
        docker("volume", "rm", *volumes)
    networks = docker("network", "ls", "--quiet", "--filter", label).split()
    if networks:
        docker("network", "rm", *networks)
    _remove_fixed_paths()
    restore_ancestors(claim["ancestors"])
    shutil.rmtree(claim["root"], ignore_errors=True)
    CLAIM.unlink()
    return True


def error_chain(error: BaseException) -> list[str]:
    messages = []
    current: BaseException | None = error
    while current is not None:
        messages.append(str(current))
        current = current.__cause__
    return messages


def driver_environment(environment: dict | None = None) -> dict:
    """The lab enters through sudo, but the proof is of a direct root operator, which the executor rightly demands."""
    values = os.environ if environment is None else environment
    return {key: value for key, value in values.items() if not key.startswith("SUDO_")}


def outage_seconds(samples: list[tuple[float, bool]]) -> float:
    """Time from each failed sample to the next one; an outage cannot be shorter than what was seen failing."""
    return sum(right[0] - left[0] for left, right in zip(samples, samples[1:]) if not left[1])


class OutageProbe:
    """What a client sees while a swap runs: PostgreSQL readiness and application readiness through the edge."""
    PROBES = {"database": ["docker", "exec", "duora-postgres-1", "pg_isready", "-q", "-h", "127.0.0.1", "-U", "duora_app",
                           "-d", "postgres", "-t", "1"],
              "application": ["docker", "exec", "duora-edge-1", "wget", "--quiet", "--spider", "--timeout=1",
                              "http://duora-web:8080/health/ready"]}
    INTERVAL_SECONDS = 0.25
    PROBE_DEADLINE_SECONDS = 5

    def __init__(self) -> None:
        self.stop = threading.Event()
        self.samples: dict[str, list[tuple[float, bool]]] = {name: [] for name in self.PROBES}
        self.threads = [threading.Thread(target=self.sample, args=(name,), daemon=True) for name in self.PROBES]

    def probe(self, name: str) -> bool:
        try:
            return subprocess.run(self.PROBES[name], capture_output=True, check=False,
                                  timeout=self.PROBE_DEADLINE_SECONDS).returncode == 0
        except subprocess.TimeoutExpired:
            return False

    def sample(self, name: str) -> None:
        while not self.stop.is_set():
            ready = self.probe(name)
            self.samples[name].append((time.monotonic(), ready))
            self.stop.wait(self.INTERVAL_SECONDS)

    def __enter__(self) -> "OutageProbe":
        for thread in self.threads:
            thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self.stop.set()
        for thread in self.threads:
            thread.join(timeout=self.PROBE_DEADLINE_SECONDS + 1)

    def report(self) -> dict:
        return {name: {"outageSeconds": round(outage_seconds(samples), 1), "samples": len(samples)}
                for name, samples in self.samples.items()}


def repository_config(key: str, secret: str) -> str:
    return ("[global]\nrepo1-s3-endpoint=s3\nrepo1-s3-bucket=" + BUCKET + "\nrepo1-s3-region=us-east-1\n"
            "repo1-s3-key=" + key + "\nrepo1-s3-key-secret=" + secret + "\nrepo1-s3-uri-style=path\n"
            "repo1-storage-port=9000\nrepo1-storage-ca-file=/etc/pgbackrest/drill-ca.crt\n")


def write_s3_certificate(directory: Path) -> Path:
    directory.mkdir(mode=0o755)
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=s3",
                    "-addext", "subjectAltName=DNS:s3,DNS:localhost", "-keyout", str(directory / "private.key"),
                    "-out", str(directory / "public.crt")], check=True, capture_output=True)
    for name in ("private.key", "public.crt"):
        (directory / name).chmod(0o644)
    return directory


def create_locked_bucket(compose: list[str]) -> None:
    script = 'MC_HOST_proof="https://$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD@localhost:9000" exec mc "$@"'
    mc = compose + ["exec", "--no-TTY", "s3", "sh", "-c", script, "mc"]
    deadline = time.monotonic() + 120
    while subprocess.run(mc + ["ls", "proof"], capture_output=True, check=False, timeout=60).returncode != 0:
        if time.monotonic() > deadline:
            raise ProofFailure("MinIO double did not answer")
        time.sleep(2)
    for arguments in (["mb", "--with-lock", "proof/" + BUCKET],
                      ["retention", "set", "--default", "COMPLIANCE", "21d", "proof/" + BUCKET]):
        subprocess.run(mc + arguments, check=True, capture_output=True, timeout=60)


def initialize_backup() -> None:
    for action in ("full", "verify"):
        backup(action)


def backup(action: str) -> None:
    result = subprocess.run([str(BACKUP_COMMAND), action], text=True, capture_output=True, check=False, timeout=COMMAND_SECONDS)
    if result.returncode:
        raise ProofFailure("duora-backup " + action + ": " + (result.stdout + result.stderr)[-2000:])


def remove_holds() -> None:
    holds = subprocess.run(["docker", "ps", "--all", "--quiet", "--filter", "name=duora-postgres-swap-hold"],
                           text=True, capture_output=True, check=False, timeout=30).stdout.split()
    if holds:
        subprocess.run(["docker", "rm", "--force", *holds], capture_output=True, check=False, timeout=60)


class SwapLab:
    """The operator's side of the protocol: real command, real reseal, real following deployment."""

    def __init__(self, *, paths: executor.Paths, bundle: Path, manifest_sha256: str, commit: str,
                 install_paths: installer.InstallPaths, architecture: str,
                 deploy: Callable[[executor.Release], None], current: executor.Release, target: str) -> None:
        self.paths, self.bundle, self.manifest_sha256, self.commit = paths, bundle, manifest_sha256, commit
        self.install_paths, self.architecture, self.deploy, self.current = install_paths, architecture, deploy, current
        self.counter = 0
        self.outages: list[dict] = []
        self.target = target
        self.receiver = installer._module(RECEIVER, "duora_swap_lab_receiver")
        setattr(self.receiver, "STATE", INGRESS)

    def compose(self, release: executor.Release) -> list[str]:
        return executor._compose_command(self.paths, self.paths.state_dir / "releases" / (release.release_id + ".env"))

    def sql(self, statement: str, release: executor.Release | None = None) -> str:
        return executor._query(self.compose(release or self.current), executor.run_command, statement, "SQL do laboratório")

    def driver(self, release: executor.Release, target: str, *flags: str, kill_at: str | None = None) -> list[str]:
        manifest = RELEASES / (release.release_id + ".json")
        command = [sys.executable, str(DRIVER), "--entry", str(self.install_paths.entry), "--receiver", str(RECEIVER),
                   "--ingress", str(INGRESS), "--architecture", self.architecture, "--state", str(self.paths.state_dir)]
        if kill_at:
            command += ["--kill-at-phase", kill_at]
        return command + ["--manifest", str(manifest), "--state-dir", str(self.paths.state_dir),
                          "--host-env", str(self.paths.host_env), "--swap-postgres-component",
                          "--target-postgres", target, *flags]

    def swap(self, release: executor.Release, target: str, *flags: str, refused: str | None = None) -> dict:
        log("swap " + " ".join(flags) + " -> " + target[-12:])
        result = subprocess.run(self.driver(release, target, *flags), text=True, capture_output=True, check=False,
                                timeout=COMMAND_SECONDS, env=driver_environment())
        if refused is not None:
            if result.returncode == 0 or refused not in result.stderr:
                raise ProofFailure("Expected refusal '" + refused + "': " + (result.stdout + result.stderr)[-1500:])
            return {}
        if result.returncode:
            raise ProofFailure("Swap command failed: " + (result.stdout + result.stderr)[-3000:])
        return json.loads(result.stdout.strip().splitlines()[-1])

    def killed(self, release: executor.Release, target: str, phase: str, *flags: str) -> None:
        log("SIGKILL at " + phase + " " + " ".join(flags))
        result = subprocess.run(self.driver(release, target, *flags, kill_at=phase), text=True, capture_output=True,
                                check=False, timeout=COMMAND_SECONDS, env=driver_environment())
        if result.returncode != -signal.SIGKILL:
            raise ProofFailure("Parent was not killed at " + phase + ": " + (result.stdout + result.stderr)[-2000:])

    def phase(self) -> str:
        return json.loads((self.paths.state_dir / "postgres-swap/phase.json").read_text())["phase"]

    def compose_alive(self) -> bool:
        return any(swap._swap_is_project_compose(process, self.paths) for process in swap.list_processes())

    def backup_alive(self) -> bool:
        return any("duora_backup.py" in item for process in swap.list_processes() for item in process.argv)

    def wait_for_orphans(self) -> None:
        deadline = time.monotonic() + COMMAND_SECONDS
        while self.compose_alive() or self.backup_alive():
            if time.monotonic() > deadline:
                raise ProofFailure("Orphan compose did not finish")
            time.sleep(0.2)

    def reseal(self, release: executor.Release) -> None:
        """The real installer in common mode: inspection, then --apply with the reviewed inventory."""
        def idle() -> None:
            installer.require_ingress_idle(self.receiver, self.install_paths, expected_commit=self.commit,
                                           manifest_sha256=self.manifest_sha256)
        inspect = lambda: executor.runtime_inventory(release, self.paths)
        options: dict[str, Any] = dict(expected_commit=self.commit, manifest_sha256=self.manifest_sha256, paths=self.install_paths,
                       expected_executor_sha256=installer._sha(self.install_paths.entry), inspect_runtime=inspect,
                       require_idle=idle, reviewed_inventory_sha256=inspect()["inventorySha256"])
        before = json.loads((self.paths.state_dir / guard.BASELINE_NAME).read_text())
        installer.install_bundle(self.bundle, **options)
        installer.install_bundle(self.bundle, **options, apply=True)
        after = json.loads((self.paths.state_dir / guard.BASELINE_NAME).read_text())
        stable = lambda value: ({k: v for k, v in value["context"].items() if k != "configHashes"}, value["volumes"])
        if stable(before) != stable(after):
            raise ProofFailure("Reseal changed host, Docker, Compose or volume identity")

    def require_idle(self) -> None:
        """No outcome counts while anything still blocks the host."""
        self.receiver.require_host_idle()
        for leftover in (swap._swap_fence_path(INGRESS), self.paths.state_dir / "postgres-swap/attempt.json",
                         INGRESS / "pending.json", INGRESS / "duora/pending.json"):
            if leftover.exists():
                raise ProofFailure("Left behind: " + str(leftover))

    def next_release(self, current: executor.Release, postgres: str | None = None) -> executor.Release:
        self.counter += 1
        raw = json.loads(json.dumps(current.raw))
        raw.update(releaseId="proof-next-" + str(self.counter), previousReleaseId=current.release_id)
        raw["images"]["postgres"] = postgres or current.images["postgres"]
        return executor.parse_manifest(json.dumps(raw), image_prefix="")

    def recorded(self, release_id: str) -> executor.Release:
        return executor.parse_manifest((self.paths.state_dir / "releases" / (release_id + ".json")).read_text(), image_prefix="")

    def receive(self, release: executor.Release) -> None:
        """What the receiver does before the executor runs: publish the manifest and move the pin."""
        manifest = swap._swap_manifest_bytes(release)
        path = RELEASES / (release.release_id + ".json")
        write_root(path, manifest, mode=0o644)
        write_root(PIN, swap._swap_pin_bytes(PIN.read_bytes(), str(path), hashlib.sha256(manifest).hexdigest()))

    def following_deploy(self, current: executor.Release) -> executor.Release:
        """The acceptance test of every outcome: a real next deployment goes through and nothing stays fenced."""
        self.require_idle()
        following = self.next_release(current)
        self.receive(following)
        self.deploy(following)
        if executor._current_release_id(self.paths.state_dir) != following.release_id:
            raise ProofFailure("Following deployment did not become current")
        self.require_idle()
        return following

    def refused_deploy(self, release: executor.Release, reason: str, *, cause: str | None = None) -> None:
        worker = docker("ps", "--quiet", "--filter", "label=com.docker.compose.project=duora",
                        "--filter", "label=com.docker.compose.service=worker")
        try:
            self.deploy(release)
        except executor.DeployError as error:
            causes = error_chain(error)
            if reason not in causes[0] or (cause is not None and not any(cause in item for item in causes)):
                raise ProofFailure("Refused for another reason: " + " <- ".join(causes)) from error
        else:
            raise ProofFailure("Deployment that must be refused was accepted")
        if docker("ps", "--quiet", "--filter", "label=com.docker.compose.project=duora",
                  "--filter", "label=com.docker.compose.service=worker") != worker:
            raise ProofFailure("Refused deployment touched the worker")

    def require_protected_reads(self, release: executor.Release) -> None:
        """The executor says only that an ancestry is unsafe; a proof that fails has to say where."""
        refused = []
        for path in protected_reads(self.paths, release):
            try:
                executor._recovery_root_read(path)
            except executor.DeployError as error:
                refused.append(str(path) + ": " + str(error) + " [" + ancestry_report(path) + "]")
        if refused:
            # Close to the end of the output: the lab keeps only the last 6000 characters of what it captures.
            print("[swap-proof] protected reads refused: " + " | ".join(refused), file=sys.stderr, flush=True)
            raise ProofFailure("Protected read refused for " + " | ".join(refused))

    def sealed_swap(self, release: executor.Release, target: str) -> executor.Release:
        """Plan, apply, reseal and close through the real command."""
        self.require_protected_reads(release)
        plan = self.swap(release, target, "--preflight-only")
        facts = plan["plan"]["facts"]
        log("plan " + json.dumps({key: facts[key] for key in ("postgres", "pgbackrest", "compatibility", "collationVersion")}))
        with OutageProbe() as probe:
            started = time.monotonic()
            result = self.swap(release, target, "--reviewed-plan-sha256", plan["planSha256"])
        self.outages.append({"target": target[-12:], "applySeconds": round(time.monotonic() - started, 1), **probe.report()})
        log("outage " + json.dumps(self.outages[-1]))
        if result["status"] != "awaiting-reseal":
            raise ProofFailure("Unexpected apply status " + json.dumps(result))
        swapped = self.recorded(result["releaseId"])
        self.refused_deploy(self.next_release(swapped), REFUSED_BY_FENCE)  # The fence stays until the journal closes.
        self.swap(release, target, "--resume", refused=REFUSED_BY_RESEAL)
        self.reseal(swapped)
        if self.swap(release, target, "--resume")["status"] != "closed":
            raise ProofFailure("Resume did not close")
        self.require_idle()
        return swapped

    def cycle(self) -> dict:
        """Sentinel, swap, real reseal, forward, rollback and reverse: section 7's happy path."""
        source = self.current
        target = self.target
        self.sql("create table if not exists ops.swap_sentinel(id int primary key, value text); "
                 "insert into ops.swap_sentinel values(1,'before-swap') on conflict do nothing")
        photo = json.loads(self.sql(swap.SWAP_PHOTO_SQL))
        swapped = self.sealed_swap(source, target)
        self.current = swapped
        sentinel = self.sql("select value from ops.swap_sentinel where id=1")
        if sentinel != "before-swap" or json.loads(self.sql(swap.SWAP_PHOTO_SQL)) != photo:
            raise ProofFailure("Swap lost the sentinel or changed the database photo")
        self.refused_deploy(self.next_release(swapped, source.images["postgres"]), REFUSED_BY_DIGEST,
                            cause=REFUSED_DIGEST_CAUSE)  # Old lock after the swap.
        forward = self.next_release(swapped)
        self.deploy(forward)
        self.deploy(swapped)  # R''->R' is an ordinary recorded rollback.
        if executor._current_release_id(self.paths.state_dir) != swapped.release_id:
            raise ProofFailure("Rollback to the swap release did not become current")
        self.refused_deploy(source, REFUSED_BY_DIGEST, cause=REFUSED_DIGEST_CAUSE)  # R'->R stays refused on purpose.
        reversed_release = self.sealed_swap(swapped, source.images["postgres"])
        self.current = reversed_release
        if self.sql("select value from ops.swap_sentinel where id=1") != "before-swap":
            raise ProofFailure("Reverse swap lost the sentinel")
        self.current = self.following_deploy(reversed_release)
        return {"source": source.images["postgres"], "target": target, "swapRelease": swapped.release_id,
                "forwardRelease": forward.release_id, "reverseRelease": reversed_release.release_id, "photoStable": True,
                "outages": self.outages}

    def other_digest(self, release: executor.Release, first: str, second: str) -> str:
        return second if release.images["postgres"] == first else first

    def resolve_killed(self, release: executor.Release, target: str, how: str) -> executor.Release:
        """Refused while the orphan lives; then the explicit command; then a real following deployment."""
        # The real orphan may finish at any moment; this second live client makes the refusal deterministic.
        client = subprocess.Popen(self.compose(release) + ["logs", "--follow", "--tail", "0", "backup"],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            self.swap(release, target, "--" + how, refused=REFUSED_BY_COMPOSE)
        finally:
            client.terminate()
            client.wait(timeout=30)
        self.wait_for_orphans()
        result = self.swap(release, target, "--" + how)
        closed = release
        if how == "resume":
            closed = self.recorded(result["releaseId"])
            self.reseal(closed)
            self.swap(release, target, "--resume")
        elif result["status"] == "aborted-awaiting-reseal":
            self.reseal(release)
            self.swap(release, target, "--resume")
        return self.following_deploy(closed)

    def matrix(self, first: str, second: str) -> dict:
        self.require_protected_reads(self.current)
        outcomes = []
        for phase in KILL_STATES:
            for how in ("resume", "abort"):
                release, started = self.current, time.monotonic()
                target = self.other_digest(release, first, second)
                plan = self.swap(release, target, "--preflight-only")["planSha256"]
                self.killed(release, target, phase, "--reviewed-plan-sha256", plan)
                if self.phase() != phase:
                    raise ProofFailure("Killed at " + self.phase() + " instead of " + phase)
                orphan = self.compose_alive()
                self.current = self.resolve_killed(release, target, how)
                outcomes.append({"phase": phase, "how": how, "orphanCompose": orphan,
                                 "seconds": round(time.monotonic() - started, 1)})
                log("outcome " + json.dumps(outcomes[-1]))
        release = self.current
        target = self.other_digest(release, first, second)
        plan = self.swap(release, target, "--preflight-only")["planSha256"]
        result = self.swap(release, target, "--reviewed-plan-sha256", plan)
        swapped = self.recorded(result["releaseId"])
        self.reseal(swapped)
        self.killed_in_close(release, target, "awaiting-reseal")
        self.current = self.following_deploy(swapped)
        outcomes.append({"phase": "awaiting-reseal", "how": "resume"})
        release = self.current
        target = self.other_digest(release, first, second)
        plan = self.swap(release, target, "--preflight-only")["planSha256"]
        swapped = self.recorded(self.swap(release, target, "--reviewed-plan-sha256", plan)["releaseId"])
        self.reseal(swapped)
        self.killed_in_close(release, target, "closing")
        self.current = self.following_deploy(swapped)
        outcomes.append({"phase": "closing", "how": "resume"})
        release = self.current
        target = self.other_digest(release, first, second)
        plan = self.swap(release, target, "--preflight-only")["planSha256"]
        self.killed(release, target, "verify-runtime", "--reviewed-plan-sha256", plan)
        self.wait_for_orphans()
        if self.swap(release, target, "--abort")["status"] != "aborted-awaiting-reseal":
            raise ProofFailure("Abort after the database was replaced must wait for a reseal")
        self.reseal(release)
        self.killed_in_close(release, target, "aborted-awaiting-reseal")
        self.current = self.following_deploy(release)
        outcomes.append({"phase": "aborted-awaiting-reseal", "how": "resume"})
        release = self.current
        target = self.other_digest(release, first, second)
        plan = self.swap(release, target, "--preflight-only")["planSha256"]
        self.killed(release, target, "verify-runtime", "--reviewed-plan-sha256", plan)
        self.wait_for_orphans()
        self.killed(release, target, ABORT_ONLY_STATE, "--abort")
        self.swap(release, target, "--resume", refused=REFUSED_RESUME_OF_ABORT)
        self.current = self.resolve_killed(release, target, "abort")
        outcomes.append({"phase": ABORT_ONLY_STATE, "how": "abort"})
        return {"outcomes": outcomes}

    def killed_in_close(self, release: executor.Release, target: str, phase: str) -> None:
        self.killed(release, target, phase, "--resume")
        if self.phase() != phase:
            raise ProofFailure("Close killed at " + self.phase() + " instead of " + phase)
        self.wait_for_orphans()
        if self.swap(release, target, "--resume")["status"] != "closed":
            raise ProofFailure("Resume did not close after a kill at " + phase)

    def unhealthy(self, bad: str) -> dict:
        self.require_protected_reads(self.current)
        release = self.current
        plan = self.swap(release, bad, "--preflight-only")["planSha256"]
        with OutageProbe() as probe:
            started = time.monotonic()
            self.swap(release, bad, "--reviewed-plan-sha256", plan, refused="subida de backup")
            if self.phase() != "swap-runtime":
                raise ProofFailure("Unhealthy target stopped at " + self.phase())
            status = self.swap(release, bad, "--abort")["status"]
        outage = {"applyAndAbortSeconds": round(time.monotonic() - started, 1), **probe.report()}
        if status != "aborted-awaiting-reseal" or self.sql("select 1") != "1":
            raise ProofFailure("Abort did not bring the old digest back: " + status)
        self.refused_deploy(self.next_release(release), REFUSED_BY_FENCE)  # Fence kept until the reseal.
        self.reseal(release)
        self.swap(release, bad, "--resume")
        self.current = self.following_deploy(release)
        return {"target": bad, "abortedTo": release.images["postgres"], "outage": outage}


class Bench:
    """A complete disposable stack in Compose project duora: real data tier, stand-in application tier."""

    def __init__(self, *, local_postgres: bool) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="duora-swap-proof-", dir="/root"))
        self.architecture = docker("version", "--format", "{{.Server.Arch}}")
        self.registry_name = "duora-swap-registry-" + self.root.name[-8:]
        self.registry = "localhost:5051/duora/"
        self.local_postgres = local_postgres
        self.compose_command: list[str] | None = None
        self.host = FixedHost()
        self.commit = hashlib.sha1(self.root.name.encode()).hexdigest()

    def push(self, image: str, name: str) -> str:
        tagged = self.registry + name
        docker("tag", image, tagged)
        docker("push", "--quiet", tagged)
        references = json.loads(docker("image", "inspect", "--format", "{{json .RepoDigests}}", tagged))
        return next(item for item in references if item.startswith(tagged.split(":", 2)[0] + ":" + tagged.split(":")[1]) and "@sha256:" in item)

    def build(self, name: str, dockerfile: str) -> str:
        directory = self.root / ("build-" + name.replace("/", "-").replace(":", "-"))
        directory.mkdir()
        (directory / "Dockerfile").write_text(dockerfile)
        docker("build", "--quiet", "--tag", self.registry + name, str(directory))
        docker("push", "--quiet", self.registry + name)
        references = json.loads(docker("image", "inspect", "--format", "{{json .RepoDigests}}", self.registry + name))
        return next(item for item in references if "@sha256:" in item and item.startswith(self.registry))

    def secrets(self, key: str, secret: str) -> Path:
        directory = self.root / "secrets"
        duora_secrets.init_secrets(directory, change_owner=True)
        duora_secrets.init_backup(directory, change_owner=True)
        repository = repository_config(key, secret)
        files = [("pgbackrest_repo.conf", repository, 999), ("pgbackrest_cipher.conf", "[global]\nrepo1-cipher-pass=synthetic\n", 999),
                 ("selfie-web-aws-access-key-id", "SYNTHETICWEB\n", 1654), ("selfie-web-aws-secret-access-key", "synthetic\n", 1654),
                 ("selfie-worker-aws-access-key-id", "SYNTHETICWORKER\n", 1654), ("selfie-worker-aws-secret-access-key", "synthetic\n", 1654)]
        for name, text, owner in files:
            file = directory / name
            file.write_text(text)
            file.chmod(0o400)
            os.chown(file, owner, owner)
        return directory

    def media(self) -> Path:
        media = self.root / "media"
        for area in ("quarantine", "derivatives"):
            directory = media / area
            directory.mkdir(parents=True, mode=0o700)
            marker = directory / ".duora-media-area"
            marker.write_text("synthetic\n")
            marker.chmod(0o400)
            os.chown(marker, 1654, 1654)
            os.chown(directory, 1654, 1654)
        return media

    def standin_overlay(self) -> Path:
        """Keeps the production data tier byte for byte; only the application tier is replaced by the stand-in."""
        service = ('    user: "0:0"\n    read_only: false\n    entrypoint: ["/bin/sh", "-c"]\n'
                   '    command: ["mkdir -p /www/health && echo ok > /www/health/ready && exec httpd -f -p 8080 -h /www"]\n'
                   '    healthcheck: {test: ["CMD", "true"], interval: 2s, timeout: 2s, retries: 3, start_period: 1s}\n')
        worker = service
        overlay = "services:\n  web:\n" + service + "  worker:\n" + worker + "  edge:\n" + service.replace(
            "exec httpd -f -p 8080 -h /www", "exec sleep 86400") + ('  migrator:\n    user: "0:0"\n    entrypoint: ["/bin/true"]\n    command: []\n')
        path = self.root / "standin.yml"
        path.write_text(overlay)
        return path

    def start(self) -> None:
        self.host.claim(root=self.root, registry=self.registry_name)
        docker("run", "--detach", "--rm", "--name", self.registry_name, "--publish", "127.0.0.1:5051:5000", smoke_local.REGISTRY_IMAGE)
        log("pull data tier")
        source = source_for(self.architecture)
        target = alternative_digest(source, self.architecture)
        docker("pull", "--quiet", source)
        docker("pull", "--quiet", target)
        if self.local_postgres:
            self.source, self.target = self.push(source, "duora-postgres:source"), self.push(target, "duora-postgres:target")
        else:
            self.source, self.target = source, target
        docker("pull", "--quiet", STANDIN_BASE)
        images = {"postgres": self.source}
        for role in ("web", "worker", "edge", "migrator"):
            images[role] = self.build("duora-" + role + ":proof", "FROM " + STANDIN_BASE + '\nLABEL duora.role="' + role + '"\n')
        s3_key, s3_secret = "swap" + os.urandom(6).hex(), duora_secrets.generate_password()
        secrets_dir, media, certs = self.secrets(s3_key, s3_secret), self.media(), write_s3_certificate(self.root / "certs")
        host_env = self.root / "duora.env"
        host_env.write_text("\n".join(["DUORA_IMAGE_PREFIX=ghcr.io/bipoxdxd/duora-", "DUORA_MIN_FREE_GIB=0", "DUORA_DISK_CHECK_PATH=/",
            f"DUORA_SECRETS_DIR={secrets_dir}", f"DUORA_MEDIA_DIR={media}", "DUORA_MEDIA_REVIEW=", "DUORA_DOMAIN=localhost",
            "DUORA_TLS=internal", "DUORA_HTTP_PORT=0", "DUORA_HTTPS_PORT=0", "DUORA_REGION_CUTOVER=2026-10-22T22:34:50Z",
            f"DRILL_S3_KEY={s3_key}", f"DRILL_S3_SECRET={s3_secret}", f"DRILL_CERTS_DIR={certs}"]) + "\n")
        host_env.chmod(0o600)
        placeholder = self.root / "merge.env"
        # Only the release variables stay interpolated, as in production; Compose resolves everything else in the merge.
        names = [f"DUORA_{role.upper()}_IMAGE" for role in images] + ["DUORA_RELEASE_ID"]
        marks = {name: "proof-placeholder-" + name.lower().replace("_", "-") for name in names}
        placeholder.write_text("".join(f"{name}={mark}\n" for name, mark in marks.items()))
        files = [REPO / "infra/compose/compose.prod.yml", REPO / "scripts/backup/compose.drill.yml", self.standin_overlay()]
        merged = subprocess.run(["docker", "compose", *[item for file in files for item in ("--file", str(file))],
                                 "--env-file", str(host_env), "--env-file", str(placeholder), "--profile", "migrate", "--profile", "restore", "config"],
                                text=True, capture_output=True, check=True, timeout=120).stdout
        for name, mark in marks.items():
            merged = merged.replace(mark, "${" + name + ":?}")
        compose_file = self.root / "compose.yml"
        compose_file.write_text(merged)
        compose_file.chmod(0o600)
        state = self.root / "state"
        state.mkdir(mode=0o700)
        (state / ".deploy.lock").touch(mode=0o600)
        self.paths = executor.Paths(compose_file, host_env, state)
        manifest = smoke_local.build_manifest(self.commit, self.architecture, images)
        manifest.update(contractVersion="v2", releaseId="proof-initial", migrations=[MIGRATION], schema={"min": MIGRATION, "max": MIGRATION})
        self.release = executor.parse_manifest(json.dumps(manifest), image_prefix="")
        executor._stage_release_files(self.release, state)
        executor._record_release(self.release, state)
        executor._mark_current(self.release, state)
        for name in (state / "current.json", state / "releases/proof-initial.json", state / "releases/proof-initial.env"):
            name.chmod(0o600)
        compose = executor._compose_command(self.paths, state / "releases/proof-initial.env")
        self.compose_command = compose
        self.run_compose("up", "--detach", "--wait", "s3")
        create_locked_bucket(compose)
        self.run_compose("up", "--detach", "--wait", "--no-deps", "backup", "postgres")
        schema = ('create schema if not exists ops; create table ops.__ef_migrations_history("MigrationId" varchar(150) primary key, '
                  '"ProductVersion" varchar(32) not null); insert into ops.__ef_migrations_history values(\'' + MIGRATION + "','10.0.0');")
        self.run_compose("exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "duora", "-c", schema)
        self.run_compose("up", "--detach", "--wait", "--no-deps", "web", "worker", "edge")
        prepare_ingress()
        prepare_fixed_paths(self.release, self.paths)
        write_backup_command(self.paths)
        backup("stanza-create")
        initialize_backup()
        self.install()

    def run_compose(self, *arguments: str) -> str:
        assert self.compose_command is not None
        result = subprocess.run(self.compose_command + list(arguments), text=True, capture_output=True, check=False, timeout=COMMAND_SECONDS)
        if result.returncode:
            raise ProofFailure("compose " + " ".join(arguments[:5]) + ": " + result.stderr[-2000:])
        return result.stdout

    def install(self) -> None:
        self.install_paths = installer.InstallPaths(self.root / "duora-deploy", self.root / "versions", self.root / "preimages", self.paths.state_dir, INGRESS)
        self.install_paths.entry.write_text("# synthetic previous executor; never invoked\n")
        self.install_paths.entry.chmod(0o755)
        bundle = self.root / "bundle"
        self.manifest_sha256 = installer.build_bundle(REPO / "scripts/deploy", bundle,
            commit=self.commit, run_url="https://github.com/BipoXDXD/duora/actions/runs/1")
        self.bundle = bundle
        self.receiver = installer._module(RECEIVER, "duora_swap_bench_receiver")
        setattr(self.receiver, "STATE", INGRESS)
        inspect = lambda: executor.runtime_inventory(self.release, self.paths)
        idle = lambda: installer.require_ingress_idle(self.receiver, self.install_paths, expected_commit=self.commit, manifest_sha256=self.manifest_sha256)
        installer.install_bundle(bundle, expected_commit=self.commit, manifest_sha256=self.manifest_sha256,
            reviewed_inventory_sha256=inspect()["inventorySha256"], expected_executor_sha256=installer._sha(self.install_paths.entry),
            paths=self.install_paths, inspect_runtime=inspect, require_idle=idle, apply=True)
        self.active = installer._module(self.install_paths.entry, "duora_swap_bench_active")

    def lab(self) -> SwapLab:
        def deploy(release: executor.Release) -> None:
            # Canonical application SQL needs the real schema; the stand-in tier proves the data-tier protocol.
            with patch.object(payload, "extract_sql", return_value="select 1;\n"):
                self.active.deploy(self.active.parse_manifest(json.dumps(release.raw), image_prefix=""), self.paths, self.active.run_command)
        return SwapLab(paths=self.paths, bundle=self.bundle, manifest_sha256=self.manifest_sha256, commit=self.commit,
                       install_paths=self.install_paths, architecture=self.architecture, deploy=deploy, current=self.release,
                       target=self.target)

    def stop(self) -> None:
        stopped = True
        if self.compose_command is not None:
            try:
                stopped = subprocess.run(self.compose_command + ["--profile", "restore", "--profile", "migrate", "down", "--volumes", "--remove-orphans"],
                                         capture_output=True, check=False, timeout=180).returncode == 0
            except subprocess.TimeoutExpired:
                stopped = False
        if self.host.claimed:
            remove_holds()
        subprocess.run(["docker", "rm", "--force", self.registry_name], capture_output=True, check=False, timeout=30)
        if stopped:
            self.host.release()
        else:
            log("compose down failed: the claim stays so --mode cleanup can still find the stack")
        shutil.rmtree(self.root, ignore_errors=True)


def pitr(bench: Bench, lab: SwapLab) -> dict:
    """Backup on the old digest, real swap, writes, PITR across the swap with the new digest, verify-repo without gaps."""
    source = lab.current
    table = "ops.swap_drill"
    lab.sql("create table " + table + "(phase text not null, at timestamptz not null default clock_timestamp())")
    insert = lambda phase: "insert into " + table + "(phase) select '" + phase + "' from generate_series(1," + str(ROWS) + ")"
    lab.sql(insert("before-swap"))
    backup("full")
    lab.sql(insert("after-backup"))
    swapped = lab.sealed_swap(source, bench.target)
    lab.current = swapped
    lab.sql(insert("after-swap"))
    time.sleep(RECOVERY_MARGIN_SECONDS)
    target_time = lab.sql("select to_char(clock_timestamp() at time zone 'UTC', 'YYYY-MM-DD HH24:MI:SS.US') || '+00'")
    time.sleep(RECOVERY_MARGIN_SECONDS)
    lab.sql(insert("after-target"))
    backup("verify")
    compose = lab.compose(swapped)
    run = lambda *arguments: subprocess.run(compose + list(arguments), check=True, capture_output=True, timeout=COMMAND_SECONDS)
    run("stop", "worker", "web", "edge")
    run("stop", "postgres")
    run("rm", "--stop", "--force", "postgres", "backup")
    docker("volume", "rm", "duora_pgdata")
    run("--profile", "restore", "run", "--rm", "--no-deps", "restore", "--repo=1", "--type=time", "--target=" + target_time,
        "--target-action=promote", "restore")
    run("up", "--detach", "--wait", "--no-deps", "backup")
    run("up", "--detach", "--wait", "--no-deps", "postgres")
    deadline = time.monotonic() + 300
    while lab.sql("select pg_is_in_recovery()", swapped) != "f":
        if time.monotonic() > deadline:
            raise ProofFailure("Restored database did not promote")
        time.sleep(1)
    counts = dict(line.split("|") for line in lab.sql("select phase,count(*) from " + table + " group by 1", swapped).splitlines())
    expected = {phase: str(ROWS) for phase in ("before-swap", "after-backup", "after-swap")}
    if counts != expected:
        raise ProofFailure("PITR across the swap returned " + json.dumps(counts))
    timeline = int(lab.sql("select timeline_id from pg_control_checkpoint()", swapped))
    if timeline < 2:
        raise ProofFailure("PITR did not open a new timeline")
    backup("full")
    for action in ("verify", "verify-repo"):
        backup(action)
    return {"source": bench.source, "target": bench.target, "timeline": timeline, "rows": counts, "outages": lab.outages}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("cycle", "matrix", "unhealthy", "pitr", "cleanup"), required=True)
    arguments = parser.parse_args()
    if os.geteuid() != 0:
        raise SystemExit("Disposable root host required")
    if arguments.mode == "cleanup":
        try:
            log("claim cleaned" if cleanup_claimed() else "nothing claimed to clean")
        except ProofFailure as error:
            log("REFUSED: " + str(error))
            return 1
        return 0
    if not host_is_disposable():
        raise ProofFailure("This host is not disposable: the proof takes over the fixed production paths")
    require_host_free()
    bench = Bench(local_postgres=arguments.mode in ("matrix", "unhealthy"))
    def terminated(*_: object) -> None:
        raise SystemExit(128 + signal.SIGTERM)  # Reaches the finally below, so a stopped proof still cleans up.
    signal.signal(signal.SIGTERM, terminated)
    started = time.monotonic()
    try:
        bench.start()
        lab = bench.lab()
        log("stack up in %.0f s" % (time.monotonic() - started))
        if arguments.mode == "cycle":
            report = lab.cycle()
        elif arguments.mode == "pitr":
            report = pitr(bench, lab)
        elif arguments.mode == "unhealthy":
            bad = bench.build("duora-postgres:unhealthy", "FROM " + bench.target + '\nENTRYPOINT ["/bin/false"]\n')
            report = lab.unhealthy(bad)
        else:
            report = lab.matrix(bench.source, bench.target)
        report.update(mode=arguments.mode, architecture=bench.architecture, seconds=round(time.monotonic() - started, 1))
        print("SWAP_RESULT=" + json.dumps(report, separators=(",", ":")), flush=True)
        return 0
    finally:
        bench.stop()


if __name__ == "__main__":
    sys.exit(main())
