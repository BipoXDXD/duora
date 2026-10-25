#!/usr/bin/env python3
"""Disposable ActionsPR260 recovery composition; imported modules have no native effects.

One origin seam only: exact declared incoming migrator -> verified inherited cohort build ID,
then unchanged payload._copy_stopped_sql. No daemon result, runtime, lock, SQL, WAL or API seam.
Never run this on a workstation or existing host. See README.native-recovery.md.
"""
import argparse
import fcntl
import hashlib
import http.server
import importlib.util
import json
import os
import platform
import re
import secrets
import shutil
import socket
import stat
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path
from urllib.parse import urlsplit, parse_qs
from typing import Any

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from old_image_identity import bind_old_image
from current_recovery_receipt import SCOPE, TRUE, FALSE, EXECUTOR, RECEIVER, SQL, need, validate_receipt, failure_locations

LABEL = "io.duora.current-recovery-lab"
ROOTS = tuple(map(Path, ("/etc/duora", "/opt/duora", "/var/lib/duora", "/etc/coolify-release",
                         "/var/lib/coolify-release", "/opt/coolify-release")))
ENTRIES = tuple(map(Path, ("/usr/local/sbin/duora-deploy", "/usr/local/sbin/duora-backup")))
BUNDLE = {"duora_deploy.py": EXECUTOR,
          "reconcile_payload.py": "b08625009bf74afe3e84750e383a0fc4262de17d6f6f2bb4a264cb9244c9b6b4",
          "runtime_guard.py": "f54f26b817638d2b905f22e30d5c1ffbce87f2e23d4087e1f8e8129cfd7a3f19",
          "install_executor.py": "0113832a49e021d4c3444115bbb97969f4b06859b7d43fbecabdc0ece8d55f99"}
SAFE = {"PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "HOME": "/root", "LANG": "C.UTF-8", "LC_ALL": "C.UTF-8"}
BOUND = 2 * 1024 * 1024


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()


def put(path, raw, mode=0o600):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with path.open("xb") as handle:
        os.fchmod(handle.fileno(), mode)
        handle.write(raw if isinstance(raw, bytes) else raw.encode())
        handle.flush()
        os.fsync(handle.fileno())
    descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def module(path, name) -> Any:
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec is not None and spec.loader is not None
    value = importlib.util.module_from_spec(spec)
    sys.modules[name] = value
    spec.loader.exec_module(value)
    return value


def command(argv, *, text=None, timeout=180, checked=True):
    result = subprocess.run(argv, input=text, text=True, capture_output=True,
                            check=False, timeout=timeout, env={**os.environ, "GIT_NO_LAZY_FETCH": "1"})
    need(len(result.stdout.encode()) + len(result.stderr.encode()) <= BOUND, "native output bound exceeded")
    if checked:
        need(result.returncode == 0, "native command failed: " + Path(argv[0]).name + " exit=" + str(result.returncode))
    return result


def require_actions():
    # Must precede mkdir, root-state inspection, product imports, Docker, SDK, build or network.
    need(sys.platform == "linux" and platform.machine() == "x86_64" and os.getuid() == os.geteuid() == 0,
         "disposable native Linux amd64/root required")
    need(sys.version_info[:2] == (3, 12), "Python3.12 required")
    need(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
         and os.environ.get("GITHUB_REPOSITORY") == "BipoXDXD/duora"
         and os.environ.get("GITHUB_EVENT_NAME") == "pull_request", "GitHub-hosted Duora PR runner required")
    event_path = Path(os.environ["GITHUB_EVENT_PATH"])
    need(event_path.stat().st_size <= BOUND, "event metadata bound")
    event = json.loads(event_path.read_bytes())
    need(event.get("number") == 260, "only existing PR260")
    for key in ("GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"):
        need(os.environ.get(key, "").isdigit() and int(os.environ[key]) > 0, "Actions run identity missing")
    for key in ("GITHUB_SHA", "GITHUB_WORKFLOW_SHA"):
        need(re.fullmatch(r"[a-f0-9]{40}", os.environ.get(key, "")), "Actions source identity missing")
    need(re.fullmatch(r"[a-f0-9]{40}", event["pull_request"]["head"]["sha"]), "actual PR head required")
    mem = dict(line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines())
    need(int(mem["MemTotal"].split()[0]) >= 6 * 1024 * 1024, "insufficient disposable memory")
    return event["pull_request"]["head"]["sha"]


def command_kind(argv):
    """Public producer classes; never record args, SQL, env, HTTP payload or credentials."""
    args = list(map(str, argv))
    if "psql" in args:
        return "sql_writes" if ("--file" in args or "--single-transaction" in args) else "sql_reads"
    if any(Path(arg).name in {"duora-backup", "duora_backup.py"} for arg in args):
        return "backup"
    if "compose" in args and "up" in args:
        return "compose_up"
    if "compose" in args and "run" in args and "migrator" in args:
        return "migrator"
    return "read_or_other"


def audit_to(path):
    def audit(event, arguments):
        if event == "subprocess.Popen":
            argv = arguments[1]
            need(type(argv) in (list, tuple), "shell subprocess forbidden")
            raw = canonical({"kind": command_kind(argv), "pid": os.getpid(), "at_ns": time.time_ns()})
            fd = os.open(path, os.O_APPEND | os.O_WRONLY | os.O_NOFOLLOW)
            try:
                os.write(fd, raw)
            finally:
                os.close(fd)
    sys.addaudithook(audit)


def bind_origin(executor, origin):
    """Single explicit lab provenance seam. All real transport/metadata checks below remain native."""
    original = executor._payload.extract_sql
    def extract(reference, *, revision, architecture, **kwargs):
        if reference != origin["declared_reference"]:
            return original(reference, revision=revision, architecture=architecture, **kwargs)
        need(not kwargs and revision == origin["revision"] and architecture == "amd64", "origin identity mismatch")
        metadata = executor._payload._inspect("image", origin["docker_image_id"], executor._payload.capture_binary)
        config = metadata["Config"]
        labels = config.get("Labels") or {}
        need(metadata["Id"] == origin["docker_image_id"] and metadata["Os"] == "linux" and metadata["Architecture"] == "amd64"
             and labels.get("org.opencontainers.image.revision") == revision
             and labels.get("org.opencontainers.image.source") == "https://github.com/BipoXDXD/duora"
             and labels.get("org.opencontainers.image.title") == "duora-migrator"
             and config.get("Volumes") in (None, {}), "local cohort build provenance mismatch")
        sql = executor._payload._copy_stopped_sql(metadata["Id"], metadata["Id"])
        need(sha(sql.encode()) == SQL, "cohort stopped SQL mismatch")
        return sql
    executor._payload.extract_sql = extract


def fixture_entry():
    # Called only by the own disposable root launcher. Original executor main performs actual flags/locks/root checks.
    need(sys.platform == "linux" and platform.machine() == "x86_64" and os.getuid() == os.geteuid() == 0,
         "native fixture entry refused")
    config = json.loads(Path("/opt/duora/native-entry.json").read_bytes())
    # observer.SAFE_ENV intentionally strips ambient credentials/context. Restore only saved public run identity.
    os.environ.update(config["actions_context"])
    require_actions()
    need(set(config["bundle_sha256"]) == set(BUNDLE), "entry bundle inventory")
    for name, expected in config["bundle_sha256"].items():
        need(re.fullmatch(r"[a-f0-9]{64}", expected) and (name == "duora_deploy.py" or expected == BUNDLE[name]), "entry sidecar identity")
        need(sha((Path(config["bundle"]) / name).read_bytes()) == expected, "entry bundle drift")
    sys.path.insert(0, config["bundle"])
    executor = module(Path(config["bundle"]) / "duora_deploy.py", "duora_native_executor")
    bind_origin(executor, config["origin"])
    audit_to(config["trace"])
    return executor.main(sys.argv[2:])


class LoopbackFixture:
    """A real loopback HTTP server. The unchanged receiver1ccd remains the actual API client."""
    def __init__(self, lab: "NativeLab"):
        self.lab = lab
        self.token = secrets.token_urlsafe(32)
        self.apps = {"duora": "duora-recovery-fixture", "stillon": "stillon-recovery-fixture", "goblinshift": "goblin-recovery-fixture"}
        self.rows = [{"id": 1, "key": "DUORA_OBSERVER_IMAGE", "is_literal": True, "is_preview": False, "is_shown_once": True},
                     {"id": 2, "key": "SYNTHETIC_UNRELATED", "is_literal": True, "is_preview": False, "is_shown_once": False,
                      "value": "fixture-only-preserve"}]
        self.value = None
        self.jobs = {}
        self.counts = {"patch_count": 0, "start_count": 0, "finished_count": 0, "rejected_requests": 0, "failed_jobs": 0}
        self.thread: threading.Thread | None = None
        self.worker: threading.Thread | None = None
        self.server: http.server.ThreadingHTTPServer | None = None
        self.handoff = None
        self.worker_error = None
        self.guard = threading.Lock()

    def dispatch(self, method, target, body):
        parts = urlsplit(target)
        path = parts.path.removeprefix("/api/v1/")
        need(target.startswith("/api/v1/") and len(target) < 256, "HTTP allowlist")
        app = self.apps["duora"]
        if method == "GET" and path.startswith("deployments/applications/"):
            queried = path.removeprefix("deployments/applications/")
            need(queried in self.apps.values() and parse_qs(parts.query) == {"skip": ["0"], "take": ["100"]}, "queue allowlist")
            records = [{"deployment_uuid": key, "status": value} for key, value in self.jobs.items()] if queried == app else []
            return {"deployments": records, "count": len(records)}
        need(not parts.query, "unexpected API query")
        if method == "GET" and path == "applications/" + app + "/envs":
            # shown_once value and real_value are absent, exactly as current Coolify hidden reads.
            return json.loads(json.dumps(self.rows))
        if method == "PATCH" and path == "applications/" + app + "/envs/bulk":
            expected = {"data": [{"key": "DUORA_OBSERVER_IMAGE", "value": self.lab.current.images["edge"],
                                  "is_literal": True, "is_preview": False, "is_shown_once": True}]}
            need(body == expected and self.counts["patch_count"] == 0, "strict once-only observer inverse")
            self.value = body["data"][0]["value"]
            self.counts["patch_count"] += 1
            return {"message": "Environment variables updated"}
        if method == "POST" and path == "applications/" + app + "/start":
            need(body is None and self.counts["start_count"] == 0 and self.counts["patch_count"] == 1, "strict once-only observer start")
            self.handoff = self.lock_handoff()
            identifier = "fixture-" + uuid.uuid4().hex
            self.counts["start_count"] += 1
            self.jobs[identifier] = "in_progress"
            self.worker = threading.Thread(target=self.run_observer, args=(identifier,), daemon=True)
            self.worker.start()
            return {"deployment_uuid": identifier}
        if method == "GET" and path.startswith("deployments/") and path.removeprefix("deployments/") in self.jobs:
            return {"status": self.jobs[path.removeprefix("deployments/")]}
        raise ValueError("HTTP endpoint refused")

    def lock_handoff(self):
        result = {}
        for key, path, expected in (("host_held", Path("/var/lib/coolify-release/host.lock"), True),
                                    ("project_held", Path("/var/lib/coolify-release/duora/release.lock"), True),
                                    ("executor_free", Path("/var/lib/duora/.deploy.lock"), False)):
            with path.open("r") as handle:
                try:
                    fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    held = False
                    fcntl.flock(handle, fcntl.LOCK_UN)
                except BlockingIOError:
                    held = True
            need(held is expected, "real lock handoff mismatch")
            result[key] = held if expected else not held
        return result

    def run_observer(self, identifier):
        try:
            pin = json.loads(self.lab.pinpath.read_bytes())
            need(self.value == pin["observer_environment"]["DUORA_OBSERVER_IMAGE"], "HTTP effective environment mismatch")
            need(pin == self.lab.original_pin, "observer PIN not restored")
            manifest = Path(pin["manifest"])
            need(sha(manifest.read_bytes()) == pin["manifest_sha256"], "observer manifest drift")
            observer = self.lab.observer
            rendered = observer.normalize_observer(self.lab.observer_file, self.lab.observer_file.parent,
                                                    pin["application_uuid"], self.value)
            observer.validate_observer(rendered, pin, json.loads(manifest.read_bytes()))
            # Actual child normal CLI and actual Docker observer startup. main's proxy checks are outside this fixture.
            observer.deploy_and_start(rendered, pin, manifest)
            self.lab.report["child_normal_deploy_exit"] = 0
            self.jobs[identifier] = "finished"
            self.counts["finished_count"] += 1
        except BaseException as error:
            self.worker_error = type(error).__name__
            self.jobs[identifier] = "failed"
            self.counts["failed_jobs"] += 1

    def start(self):
        owner = self
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, format: str, *args: Any) -> None:
                pass
            def handle_request(self):
                try:
                    need(self.client_address[0] == "127.0.0.1"
                         and self.headers.get("Authorization") == "Bearer " + owner.token, "HTTP authorization")
                    length = int(self.headers.get("Content-Length", "0"))
                    need(0 <= length <= 65536, "HTTP body bound")
                    body = json.loads(self.rfile.read(length)) if length else None
                    with owner.guard:
                        response = owner.dispatch(self.command, self.path, body)
                    raw = canonical(response)
                    self.send_response(200)
                except BaseException:
                    owner.counts["rejected_requests"] += 1
                    raw = b'{"message":"fixture refused"}\n'
                    self.send_response(400)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)
            do_GET = do_POST = do_PATCH = handle_request
        class Server(http.server.ThreadingHTTPServer):
            allow_reuse_address = False
            daemon_threads = True
        # An occupied fixed client port is a hard refusal, never kill/rebind an existing listener.
        self.server = Server(("127.0.0.1", 8000), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def stop(self):
        if self.worker:
            self.worker.join(timeout=30)
            need(not self.worker.is_alive(), "observer job did not finish before cleanup")
        if self.server:
            self.server.shutdown()
            self.server.server_close()
        if self.thread:
            self.thread.join(timeout=5)

    def receipt(self):
        need(len(self.jobs) == 1 and self.worker_error is None, "native observer job failed")
        return {**self.counts, "deployment_uuid": next(iter(self.jobs)), "lock_handoff": self.handoff}


class Continuity:
    """Nonce-filtered real events with consumed stopped-container-create barriers around protected operations."""
    def __init__(self, lab: "NativeLab"):
        self.lab = lab
        self.stop_event = threading.Event()
        self.condition = threading.Condition()
        self.samples: list[dict[str, Any]] = []
        self.errors: list[str] = []
        self.records: list[dict[str, Any]] = []
        self.raw = bytearray()
        self.process: subprocess.Popen[bytes] | None = None
        self.thread: threading.Thread | None = None
        self.reader: threading.Thread | None = None
        self.reader_done = threading.Event()
        self.ending = False
        self.alive = False
        self.start_marker: dict[str, Any] | None = None
        self.end_marker: dict[str, Any] | None = None
        self.start_alive = False
        self.recovery_start = self.recovery_end = 0.0
        self.recovery_start_ns = self.recovery_end_ns = 0
        self.since = 0

    @staticmethod
    def unique(items):
        result = {}
        for key, value in items:
            need(key not in result, "duplicate native event field")
            result[key] = value
        return result

    @staticmethod
    def require_owned_marker(lab: "NativeLab", identifier: str, phase: str) -> None:
        need(type(phase) is str and phase in {"start", "end"}
             and re.fullmatch(r"[a-f0-9]{64}", identifier), "unknown container marker identity")
        item = lab.inspect("container", identifier)
        config, state, host = item["Config"], item["State"], item["HostConfig"]
        need(item["Id"] == identifier and item["Name"] == "/duora-native-event-" + phase + "-" + lab.nonce
             and type(lab.marker_image_id) is str and re.fullmatch(r"sha256:[a-f0-9]{64}", lab.marker_image_id)
             and item["Image"] == lab.marker_image_id
             and config["Image"] == lab.pins["old_images"]["edge"]["reference"]
             and (config.get("Labels") or {}).get(LABEL) == lab.nonce
             and config["Labels"].get(LABEL + ".barrier") == phase
             and config["Entrypoint"] == ["/bin/true"]
             and state["Status"] == "created" and state["Running"] is False
             and state["Paused"] is False and state["Restarting"] is False and state["Pid"] == 0
             and item["RestartCount"] == 0 and item["Mounts"] == []
             and host["NetworkMode"] == "none" and host["ReadonlyRootfs"] is True
             and host.get("Binds") in (None, []) and host.get("VolumesFrom") in (None, [])
             and host.get("Devices") in (None, []), "container marker ownership/image/unstarted isolation unknown")

    def consume_events(self):
        assert self.process is not None and self.process.stdout is not None
        stream = self.process.stdout
        try:
            while True:
                line = stream.readline(65537)
                if not line:
                    need(self.ending, "native event stream ended before its boundary acknowledgement")
                    return
                need(len(line) <= 65536 and line.endswith(b"\n"), "native event record bound/framing")
                event: dict[str, Any] = json.loads(line, object_pairs_hook=self.unique,
                                   parse_constant=lambda _: need(False, "nonfinite native event"))
                need(type(event) is dict and type(event.get("timeNano")) is int and event["timeNano"] > 0
                     and type(event.get("Actor")) is dict and type(event.get("Type")) is str
                     and type(event.get("Action")) is str, "unknown native event shape")
                actor = event["Actor"]
                assert isinstance(actor, dict)  # Existing exact shape guard above; no fixture fallback.
                identifier = actor.get("ID")
                attributes = actor.get("Attributes")
                need(type(identifier) is str and re.fullmatch(r"[a-f0-9]{64}", identifier)
                     and type(attributes) is dict and attributes.get(LABEL) == self.lab.nonce,
                     "native event escaped its exact owned nonce filter")
                with self.condition:
                    need(len(self.raw) + len(line) <= BOUND, "native events cumulative bound")
                    self.raw.extend(line)
                    self.records.append({"event": event, "consumed_at_ns": time.time_ns()})
                    self.condition.notify_all()
        except BaseException:
            with self.condition:
                self.errors.append("event-stream-unknown")
                self.condition.notify_all()
        finally:
            self.reader_done.set()

    def marker(self, phase: str):
        need(phase in {"start", "end"}, "unknown event marker phase")
        # Moby container/create events carry labels; network/create events do not. Never start this marker.
        image = self.lab.pins["old_images"]["edge"]
        metadata = self.lab.inspect("image", image["reference"])
        labels = metadata["Config"].get("Labels") or {}
        need(type(metadata["Id"]) is str and re.fullmatch(r"sha256:[a-f0-9]{64}", metadata["Id"])
             and image["reference"] in metadata.get("RepoDigests", [])
             and metadata["Os"] == "linux" and metadata["Architecture"] == "amd64"
             and labels.get("org.opencontainers.image.source") == "https://github.com/BipoXDXD/duora"
             and labels.get("org.opencontainers.image.title") == "duora-edge"
             and labels.get("org.opencontainers.image.revision") == self.lab.pins["old_commit"]
             and metadata["Config"].get("Volumes") in (None, {}), "cached marker provenance/zero-volume declaration unknown")
        if self.lab.marker_image_id is None:
            self.lab.marker_image_id = metadata["Id"]  # Actual Docker object ID; no claim that this is an OCI config digest.
        need(self.lab.marker_image_id == metadata["Id"], "cached marker Docker image identity changed")
        name = "duora-native-event-" + phase + "-" + self.lab.nonce
        identifier = self.lab.docker("container", "create", "--pull", "never", "--name", name, "--label", LABEL + "=" + self.lab.nonce,
                                     "--label", LABEL + ".barrier=" + phase, "--network", "none", "--read-only",
                                     "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                                     "--entrypoint", "/bin/true", image["reference"])
        need(re.fullmatch(r"[a-f0-9]{64}", identifier), "native event marker ID unknown")
        self.require_owned_marker(self.lab, identifier, phase)
        deadline = time.monotonic() + 10
        with self.condition:
            while True:
                assert self.process is not None
                need(not self.errors and self.process.poll() is None and not self.reader_done.is_set(),
                     "native event stream unknown before marker consumption")
                found = [(index, record) for index, record in enumerate(self.records)
                         if record["event"]["Type"] == "container" and record["event"]["Action"] == "create"
                         and record["event"]["Actor"]["ID"] == identifier
                         and record["event"]["Actor"]["Attributes"].get(LABEL + ".barrier") == phase]
                if found:
                    need(len(found) == 1, "native event marker duplicated")
                    index, record = found[0]
                    return {"id": identifier, "type": "container", "action": "create", "record_index": index,
                            "time_nano": record["event"]["timeNano"], "consumed_at_ns": record["consumed_at_ns"]}
                remaining = deadline - time.monotonic()
                need(remaining > 0, "native event marker was not consumed")
                self.condition.wait(timeout=min(remaining, 0.25))

    def start(self):
        self.events_path = self.lab.root / "events.ndjson"
        # Include a real time before subscription setup. A live PID alone never admits protected operations.
        self.since = int(time.time()) - 1
        self.process = subprocess.Popen(["/usr/bin/docker", "events", "--since", str(self.since),
                                        "--filter", "label=" + LABEL + "=" + self.lab.nonce, "--format", "{{json .}}"],
                                       stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        self.reader = threading.Thread(target=self.consume_events, daemon=True)
        self.reader.start()
        def monitor():
            while not self.stop_event.is_set():
                before = time.monotonic()
                try:
                    result = command(["/usr/bin/docker", "exec", "--user", "999:999", "duora-postgres-1", "pg_isready",
                                      "-h", "127.0.0.1", "-U", "duora_app", "-d", "duora"], timeout=2, checked=False)
                    self.samples.append({"at": before, "ready": result.returncode == 0})
                except Exception:
                    self.errors.append("readiness-monitor")
                self.stop_event.wait(0.5)
        self.thread = threading.Thread(target=monitor, daemon=True)
        self.thread.start()
        self.start_marker = self.marker("start")
        self.start_alive = self.process.poll() is None and not self.reader_done.is_set()
        deadline = time.monotonic() + 5
        while len(self.samples) < 2 and time.monotonic() < deadline:
            time.sleep(0.1)
        need(len(self.samples) >= 2 and self.start_alive and not self.errors, "continuity observer did not start")
        # Only now can the caller begin arming/stopping/migrating/recovery.
        self.recovery_start = time.monotonic()
        self.recovery_start_ns = time.time_ns()
        need(self.start_marker["time_nano"] <= self.start_marker["consumed_at_ns"] <= self.recovery_start_ns,
             "start event boundary is unknown")

    def stop(self, *, complete=False):
        # Failure cleanup never manufactures an end marker or a successful coverage proof.
        if complete:
            self.recovery_end = time.monotonic()
            self.recovery_end_ns = time.time_ns()
            self.end_marker = self.marker("end")
            assert self.process is not None and self.start_marker is not None
            self.alive = self.process.poll() is None and not self.reader_done.is_set()
            need(self.alive and self.end_marker["record_index"] > self.start_marker["record_index"]
                 and self.end_marker["id"] != self.start_marker["id"]
                 and self.start_marker["time_nano"] < self.end_marker["time_nano"]
                 and self.recovery_end_ns <= self.end_marker["time_nano"] <= self.end_marker["consumed_at_ns"],
                 "end event boundary is unknown")
        self.stop_event.set()
        if self.thread:
            self.thread.join(timeout=3)
            need(not self.thread.is_alive(), "readiness observer did not stop")
        self.ending = True
        if self.process and self.process.poll() is None:
            self.process.terminate()
            self.process.wait(timeout=5)
        if self.reader:
            self.reader.join(timeout=5)
            need(not self.reader.is_alive() and self.reader_done.is_set(), "event reader did not complete")
        if self.process and self.process.stdout:
            self.process.stdout.close()

    def receipt(self, protected):
        need(not self.errors and self.samples and self.alive and self.start_alive and self.reader_done.is_set(),
             "continuity observation failed")
        assert self.start_marker is not None and self.end_marker is not None
        times = [item["at"] for item in self.samples]
        gaps = [b - a for a, b in zip(times, times[1:])]
        forbidden = 0
        with self.condition:
            records = list(self.records)
            raw = bytes(self.raw)
        start, end = self.start_marker["record_index"], self.end_marker["record_index"]
        need(0 <= start < end < len(records), "native event boundary record range")
        for record in records[start + 1:end]:
            event = record["event"]
            if event["Type"] == "container" and event["Actor"]["ID"] in protected:
                if event["Action"] in {"stop", "die", "destroy", "restart", "kill", "oom", "pause", "unpause",
                                       "health_status: unhealthy", "health_status: starting"}:
                    forbidden += 1
        put(self.events_path, raw)
        return {"samples": len(times), "all_ready": all(sample["ready"] for sample in self.samples),
                "maximum_gap_seconds": max(gaps, default=999), "events_alive_through_end": self.alive,
                "forbidden_events": forbidden, "covers_entire_recovery": times[0] <= self.recovery_start
                and times[-1] >= self.recovery_end - 3, "events_sha256": sha(raw),
                "event_barriers": {"start": self.start_marker, "end": self.end_marker, "nonce": self.lab.nonce,
                                   "event_filter": "label=" + LABEL + "=" + self.lab.nonce,
                                   "since_unix_seconds": self.since, "protected_start_ns": self.recovery_start_ns,
                                   "protected_end_ns": self.recovery_end_ns, "records_consumed": len(records),
                                   "stream_alive_at_start": self.start_alive, "stream_alive_at_end": self.alive,
                                   "end_consumed": True, "reader_complete": self.reader_done.is_set()}}

class NativeLab:
    def __init__(self, args: argparse.Namespace, head: str):
        self.args, self.head = args, head
        self.nonce = uuid.uuid4().hex
        self.root = Path("/opt/duora/fixture")
        self.pinpath = Path("/etc/duora/coolify-deploy.json")
        self.state = Path("/var/lib/duora")
        self.trace = Path("/opt/duora/commands.ndjson")
        self.compose_path = Path("/opt/duora/compose.coolify.json")
        self.host_env = Path("/etc/duora/duora.env")
        self.pins = json.loads((args.workflow_source / "scripts/deploy/compatibility/current_schema_pins.json").read_bytes())
        self.report: dict[str, Any] = {"schema": 1, "scope": SCOPE, "success": False,
                       "payload_origin": "local_cohort_build", "control_plane": "CoolifyHTTP_fixture",
                       **{key: False for key in TRUE}, **{key: False for key in FALSE}}
        self.created_roots, self.created_entries = [], []
        self.session: subprocess.Popen[bytes] | None = None
        self.continuity: Continuity | None = None
        self.api: LoopbackFixture | None = None
        self.functional: dict[str, Any] | None = None
        self.opt_fd: int | None = None
        self.opt_identity: tuple[int, int, int, int] | None = None
        self.opt_original_mode: int | None = None
        self.opt_changed = False
        self.marker_image_id: str | None = None
        self.executor: Any = None
        self.observer: Any = None
        self.current: Any = None
        self.incoming: Any = None
        self.paths: Any = None
        self.compose_ready = False
        self.bundle = {name: sha((args.workflow_source / "scripts/deploy" / name).read_bytes()) for name in BUNDLE}

    def docker(self, *args, **kwargs):
        return command(["/usr/bin/docker", *map(str, args)], **kwargs).stdout.strip()

    def inspect(self, kind, name):
        return json.loads(self.docker(kind, "inspect", name))[0]

    def git(self, root, *args):
        return command(["/usr/bin/git", "-c", "safe.directory=" + str(root), "-C", str(root), *args]).stdout.strip()

    def verify_sources(self):
        for root, commit, tree in ((self.args.cohort, self.pins["cohort_commit"], self.pins["cohort_tree"]),
                                    (self.args.old_source, self.pins["old_commit"], self.pins["old_tree"])):
            need(self.git(root, "rev-parse", "HEAD") == commit and self.git(root, "rev-parse", "HEAD^{tree}") == tree
                 and not self.git(root, "status", "--porcelain", "--untracked-files=no"), "fixture source commit/tree drift")
        need(self.git(self.args.workflow_source, "rev-parse", "HEAD") == self.head
             and not self.git(self.args.workflow_source, "status", "--porcelain", "--untracked-files=no"), "actual PR260 source drift")
        directory = self.args.workflow_source / "scripts/deploy"
        for name, expected in self.bundle.items():
            need((name == "duora_deploy.py" or expected == BUNDLE[name])
                 and sha((directory / name).read_bytes()) == expected, "actual reviewed bundle drift")
        for name, expected in self.pins["cohort_input_sha256"].items():
            need(sha((self.args.cohort / name).read_bytes()) == expected, "cohort fixture hash differs")
        need(sha((self.args.old_source / self.pins["canonical_sql_path"]).read_bytes()) == self.pins["historical_sql_sha256"], "old SQL source differs")
        self.receiver_bytes = (self.args.old_source / "scripts/release/coolify/receiver.py").read_bytes()
        need(sha(self.receiver_bytes) == RECEIVER, "qualified receiver1ccd unavailable")
        self.report["source"] = {"executor_sha256": self.bundle["duora_deploy.py"], "historical_frozen135_executor_sha256": EXECUTOR, "receiver_sha256": RECEIVER, "canonical_sql_sha256": SQL,
                                 "cohort_commit": self.pins["cohort_commit"], "cohort_tree": self.pins["cohort_tree"],
                                 "old_commit": self.pins["old_commit"], "bundle_sha256": self.bundle,
                                 "harness_sha256": sha(Path(__file__).read_bytes()),
                                 "parser_sha256": sha(Path(__file__).with_name("current_recovery_receipt.py").read_bytes()),
                                 "image_identity_helper_sha256": sha(Path(__file__).with_name("old_image_identity.py").read_bytes()),
                                 "original_cohort_caddy_sha256": self.pins["cohort_input_sha256"]["infra/caddy/Caddyfile"],
                                 "fixture_helpers_sha256": {name: sha((self.args.workflow_source / name).read_bytes()) for name in
                                     ("scripts/deploy/duora_backup.py", "scripts/backup/restore_drill.py", "scripts/backup/compose.drill.yml",
                                      "scripts/release/coolify/observer.py")}}

    def preflight_handoff(self):
        # Validate separate functional evidence, never relabel its scope as recovery.
        receipt_path = self.args.functional_receipt
        raw = receipt_path.read_bytes()
        need(len(raw) <= 65536, "functional receipt bound")
        self.functional = json.loads(raw)
        assert isinstance(self.functional, dict)
        parser = module(self.args.workflow_source / "scripts/deploy/compatibility/current_schema_receipt.py", "functional_receipt_validation")
        parser.validate_receipt(self.functional, self.pins)
        handoff = json.loads(self.args.handoff.read_bytes())
        need(set(handoff) == {"schema", "run_id", "attempt", "workflow_head", "keeper", "docker_image_id", "functional_receipt_sha256"}
             and handoff["schema"] == 1 and handoff["run_id"] == os.environ["GITHUB_RUN_ID"]
             and handoff["attempt"] == os.environ["GITHUB_RUN_ATTEMPT"] and handoff["workflow_head"] == self.head
             and handoff["functional_receipt_sha256"] == sha(raw), "same-job handoff identity")
        need(re.fullmatch(r"duora259-recovery-keeper-[a-f0-9]{32}", handoff["keeper"]), "owned keeper tag required")
        item = self.inspect("image", handoff["keeper"])
        need(item["Id"] == handoff["docker_image_id"] == self.functional["new_migrator"]["docker_image_id"], "inherited build identity differs")
        self.migrator = item["Id"]
        self.keeper = handoff["keeper"]
        self.report["new_migrator"] = {"docker_image_id": self.migrator, "source_head": self.pins["cohort_commit"], "build_count_this_driver": 0}
        self.report["functional_receipt_sha256"] = sha(raw)
        # No pull/build/tag/SDK here. The explicit same-job handoff transfers keeper cleanup after own native resources.
        self.report["old_images"] = {}
        for role, pin in self.pins["old_images"].items():
            image = self.inspect("image", pin["reference"])
            raw_manifest = (self.args.workflow_source / "scripts/deploy/compatibility/old-oci" / (role + ".json")).read_bytes()
            self.report["old_images"][role] = bind_old_image(raw_manifest, image, pin, role=role, revision=self.pins["old_commit"])

    def prepare_actions_opt_fixture(self):
        # GitHub-hosted Ubuntu intentionally ships /opt mode0777. Normalize ONLY this top inode in this disposable VM.
        require_actions()
        need(self.opt_fd is None, "Actions /opt prerequisite cannot be repeated")
        parent = Path("/").lstat()
        need(stat.S_ISDIR(parent.st_mode) and parent.st_uid == parent.st_gid == 0 and not parent.st_mode & 0o022, "unsafe prerequisite parent")
        descriptor = os.open("/opt", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            observed, visible = os.fstat(descriptor), Path("/opt").lstat()
            identity = (observed.st_dev, observed.st_ino, observed.st_uid, observed.st_gid)
            need(stat.S_ISDIR(observed.st_mode) and observed.st_uid == observed.st_gid == 0
                 and stat.S_ISDIR(visible.st_mode) and (visible.st_dev, visible.st_ino, visible.st_uid, visible.st_gid) == identity
                 and stat.S_IMODE(observed.st_mode) in {0o755, 0o777}, "unknown real hosted Actions /opt preimage")
            self.opt_fd, self.opt_identity, self.opt_original_mode = descriptor, identity, stat.S_IMODE(observed.st_mode)
            self.report["actions_opt_fixture"] = {"path": "/opt", "scope": "disposable_hosted_actions_only",
                "original_mode": self.opt_original_mode, "fixture_mode": None, "restored_mode": None, "changed": False,
                "root_owned": True, "non_recursive": True, "identity_unchanged": False,
                "identity_sha256": sha(canonical(identity)), "restored": False}
            if self.opt_original_mode == 0o777:
                os.fchmod(descriptor, 0o755)
                self.opt_changed = True
                self.report["actions_opt_fixture"]["changed"] = True
                os.fsync(descriptor)
            after, visible = os.fstat(descriptor), Path("/opt").lstat()
            need((after.st_dev, after.st_ino, after.st_uid, after.st_gid) == identity
                 and (visible.st_dev, visible.st_ino, visible.st_uid, visible.st_gid) == identity
                 and stat.S_ISDIR(visible.st_mode) and stat.S_IMODE(after.st_mode) == stat.S_IMODE(visible.st_mode) == 0o755,
                 "Actions /opt prerequisite identity/mode drift")
            self.report["actions_opt_fixture"]["fixture_mode"] = 0o755
        except BaseException:
            if self.opt_fd is None:
                os.close(descriptor)
            raise  # The main finally restores any known successful fchmod before reporting failure.

    def restore_actions_opt_fixture(self):
        if self.opt_fd is None:
            return
        descriptor = self.opt_fd
        try:
            observed, visible = os.fstat(descriptor), Path("/opt").lstat()
            need(self.opt_identity is not None and self.opt_original_mode in {0o755, 0o777}
                 and stat.S_ISDIR(visible.st_mode)
                 and (observed.st_dev, observed.st_ino, observed.st_uid, observed.st_gid) == self.opt_identity
                 and (visible.st_dev, visible.st_ino, visible.st_uid, visible.st_gid) == self.opt_identity
                 and stat.S_IMODE(observed.st_mode) == stat.S_IMODE(visible.st_mode) == 0o755,
                 "refuse restoring unknown Actions /opt identity/mode")
            if self.opt_changed:
                need(self.opt_original_mode == 0o777, "unknown Actions /opt changed preimage")
                os.fchmod(descriptor, 0o777)
                os.fsync(descriptor)
            observed, visible = os.fstat(descriptor), Path("/opt").lstat()
            need((observed.st_dev, observed.st_ino, observed.st_uid, observed.st_gid) == self.opt_identity
                 and (visible.st_dev, visible.st_ino, visible.st_uid, visible.st_gid) == self.opt_identity
                 and stat.S_ISDIR(visible.st_mode)
                 and stat.S_IMODE(observed.st_mode) == stat.S_IMODE(visible.st_mode) == self.opt_original_mode,
                 "Actions /opt preimage restoration unconfirmed")
            self.report["actions_opt_fixture"].update(restored_mode=self.opt_original_mode, identity_unchanged=True, restored=True)
        finally:
            os.close(descriptor)
            self.opt_fd = None

    def reserve_fresh_root(self):
        self.report["stage"] = "actions_opt_fixture_prerequisite"
        self.prepare_actions_opt_fixture()
        self.report["stage"] = "fresh_owned_root_bootstrap"
        self.report["fixture_ancestry"] = []
        recorded = set()
        # Refuse before first mutation; this is not an installer for an existing host.
        for path in (*ROOTS, *ENTRIES):
            need(not path.exists() and not path.is_symlink(), "existing root fixture path refused")
            for parent in path.parents:
                info = parent.lstat()
                if str(parent) not in recorded:
                    self.report["fixture_ancestry"].append({"path": str(parent), "uid": info.st_uid, "gid": info.st_gid,
                        "mode": stat.S_IMODE(info.st_mode), "directory": stat.S_ISDIR(info.st_mode)})
                    recorded.add(str(parent))
                need(stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and info.st_gid == 0
                     and not info.st_mode & 0o022, "unsafe fixture ancestry")
        need(not self.docker("ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=duora"), "existing duora containers refused")
        names = set(self.docker("ps", "--all", "--format", "{{.Names}}").split())
        need(not names.intersection({"duora-" + role + "-1" for role in ("postgres", "backup", "web", "worker", "edge", "s3", "internet", "migrator")}
                                    | {"duora-recovery-sleeping-observer"}), "foreign fixture container name")
        need(not self.docker("network", "ls", "--quiet", "--filter", "name=^duora-recovery-fixture_default$"), "foreign observer network")
        need(not self.docker("ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=duora-recovery-fixture"), "foreign observer project")
        need(not self.docker("volume", "ls", "--quiet", "--filter", "name=^duora_"), "existing duora volumes refused")
        need(not self.docker("network", "ls", "--quiet", "--filter", "name=^duora_"), "existing duora networks refused")
        # Fixed backup subnet matches the real image's pg_hba/pgBackRest configuration. Refuse collisions.
        networks = self.docker("network", "ls", "--quiet").split()
        if networks:
            import ipaddress
            for item in json.loads(self.docker("network", "inspect", *networks)):
                for config in item.get("IPAM", {}).get("Config") or []:
                    if config.get("Subnet"):
                        net = ipaddress.ip_network(config["Subnet"])
                        need(not any(net.version == candidate.version and net.overlaps(candidate) for candidate in
                                     map(ipaddress.ip_network, ("172.30.10.0/24", "172.30.20.0/24", "172.30.21.0/24"))), "fixture subnet collision")
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 8000))
        for path in ROOTS:
            path.mkdir(mode=0o700)
            self.created_roots.append(path)
            put(path / ".native-recovery-owner", self.nonce + "\n")
        self.root.mkdir(mode=0o755)
        for path in (*ROOTS[:3],):
            path.chmod(0o755)
        put(self.trace, b"")
        # Tiny public classification trace only; all real commands still execute unchanged.
        audit_to(self.trace)

    def private_fixture_directory(self, path):
        path = Path(path)
        need(any(path != root and path.is_relative_to(root) for root in self.created_roots), "directory outside owned fixture roots")
        parent = path.parent.lstat()
        need(stat.S_ISDIR(parent.st_mode) and parent.st_uid == parent.st_gid == 0 and not parent.st_mode & 0o022,
             "unsafe parent of fresh private fixture directory")
        path.mkdir(mode=0o700)  # No parents/exist_ok: each fresh ancestor gets an explicit restrictive mode.
        created = path.lstat()
        need(stat.S_ISDIR(created.st_mode) and created.st_uid == created.st_gid == 0 and stat.S_IMODE(created.st_mode) == 0o700,
             "fresh fixture directory is not private root state")

    def install_fixture_bundle(self):
        bundle = Path("/opt/duora/executor/versions/frozen135")
        for directory in (bundle.parent.parent, bundle.parent, bundle):
            self.private_fixture_directory(directory)
        for name in BUNDLE:
            put(bundle / name, (self.args.workflow_source / "scripts/deploy" / name).read_bytes(), 0o755 if name == "duora_deploy.py" else 0o600)
        sys.path.insert(0, str(bundle))
        self.executor = module(bundle / "duora_deploy.py", "native_recovery_executor_parent")
        self.observer = module(self.args.workflow_source / "scripts/release/coolify/observer.py", "native_recovery_observer")
        self.paths = self.executor.Paths(self.compose_path, self.host_env, self.state)
        # Incoming reference is a declared fixture envelope identity, never a fabricated daemon RepoDigest.
        self.declared = "ghcr.io/bipoxdxd/duora-migrator@sha256:" + sha(canonical({"fixture": self.nonce, "localConfig": self.migrator}))
        self.origin = {"declared_reference": self.declared, "docker_image_id": self.migrator, "revision": self.pins["cohort_commit"]}
        bind_origin(self.executor, self.origin)
        put(Path("/opt/duora/native-entry.json"), canonical({"bundle": str(bundle), "bundle_sha256": self.bundle, "origin": self.origin, "trace": str(self.trace),
            "actions_context": {key: os.environ[key] for key in ("GITHUB_ACTIONS", "RUNNER_ENVIRONMENT", "GITHUB_REPOSITORY",
                "GITHUB_EVENT_NAME", "GITHUB_EVENT_PATH", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_SHA", "GITHUB_WORKFLOW_SHA",
                "GITHUB_WORKFLOW_REF", "RUNNER_TEMP")}}))
        # Local launcher changes no product byte. require_actions runs again before loading the root bundle.
        script = Path("/opt/duora/current_recovery_lab.py")
        put(script, Path(__file__).read_bytes(), 0o700)
        put(script.with_name("current_recovery_receipt.py"), Path(__file__).with_name("current_recovery_receipt.py").read_bytes())
        helper_target = script.with_name("old_image_identity.py")
        put(helper_target, Path(__file__).with_name("old_image_identity.py").read_bytes())
        need(sha(helper_target.read_bytes()) == self.report["source"]["image_identity_helper_sha256"], "fixture sibling helper copy differs")
        launcher = "#!/bin/sh\nexec /usr/bin/python3 -I -B /opt/duora/current_recovery_lab.py --fixture-entry \"$@\"\n"
        put(ENTRIES[0], launcher, 0o755)
        self.created_entries.append(ENTRIES[0])
        receiver_path = Path("/opt/coolify-release/versions") / ("2.0.2-" + RECEIVER) / "receiver.py"
        for directory in (receiver_path.parent.parent, receiver_path.parent):
            self.private_fixture_directory(directory)
        put(receiver_path, self.receiver_bytes)
        Path("/opt/coolify-release/receiver.py").symlink_to(receiver_path)
        for path in (Path("/var/lib/coolify-release/host.lock"), Path("/var/lib/coolify-release/duora/release.lock"), self.state / ".deploy.lock"):
            put(path, b"")
        self.backup_path = Path("/opt/duora/duora_backup.py")
        put(self.backup_path, (self.args.workflow_source / "scripts/deploy/duora_backup.py").read_bytes())
        backup_launcher = "#!/bin/sh\nexec /usr/bin/python3 -I -B /opt/duora/duora_backup.py \"$@\" --skip-egress-check --state-dir /var/lib/duora\n"
        put(ENTRIES[1], backup_launcher, 0o755)
        self.created_entries.append(ENTRIES[1])

    def manifest(self, release_id, commit, migrations, previous=None, incoming=False):
        images = {role: pin["reference"] for role, pin in self.pins["old_images"].items()}
        if incoming:
            images["migrator"] = self.declared
        # Synthetic manifests are explicitly fixture bootstrap inputs, never production success evidence.
        return {"schemaVersion": 1, "releaseId": release_id, "commit": commit, "createdAt": "2026-10-25T00:00:00Z",
                "architecture": "amd64", "images": images, "contractVersion": "v2", "migrations": migrations,
                "schema": {"min": migrations[0], "max": migrations[-1]}, "sbom": {role: "synthetic-fixture-only" for role in images},
                "gates": {"status": "passed", "runUrl": "fixture-bootstrap-not-product-gates"}, "previousReleaseId": previous}

    def prepare_runtime(self):
        self.report["stage"] = "native_bootstrap_secrets"
        self.secrets = self.root / "secrets"
        self.certs = self.root / "certs"
        self.certs.mkdir(mode=0o755)
        self.certs.chmod(0o755)  # Only this own synthetic TLS directory: MinIO1001 must traverse it despite umask077.
        helper = module(self.args.cohort / "scripts/deploy/duora_secrets.py", "native_recovery_secrets")
        helper.init_secrets(self.secrets, change_owner=True)
        helper.init_backup(self.secrets, change_owner=True)
        # Reuse only the real drill's preparation primitives; NEVER its _prepare/run/teardown/down/PITR.
        drill = module(self.args.workflow_source / "scripts/backup/restore_drill.py", "native_recovery_backup_drill")
        s3key, s3secret = "native" + secrets.token_hex(8), helper.generate_password()
        repo = ("[global]\nrepo1-s3-endpoint=s3\nrepo1-s3-bucket=duora-drill\nrepo1-s3-region=us-east-1\n"
                "repo1-s3-key=" + s3key + "\nrepo1-s3-key-secret=" + s3secret +
                "\nrepo1-s3-uri-style=path\nrepo1-storage-port=9000\nrepo1-storage-ca-file=/etc/pgbackrest/drill-ca.crt\n")
        for name, content in (("pgbackrest_repo.conf", repo), ("pgbackrest_cipher.conf", "[global]\nrepo1-cipher-pass=" + helper.generate_password() + "\n")):
            put(self.secrets / name, content, 0o400)
            os.chown(self.secrets / name, 999, 999)
        drill.Drill._write_s3_certificate(self.certs)
        pgpass = self.secrets / "lab_app_pgpass"
        put(pgpass, "127.0.0.1:5432:duora:duora_app:" + (self.secrets / "db_app_password").read_text().strip() + "\n", 0o400)
        os.chown(pgpass, 999, 999)
        self.media = self.root / "media"
        self.private_fixture_directory(self.media)
        for area in ("quarantine", "derivatives"):
            folder = self.media / area
            self.private_fixture_directory(folder)
            put(folder / ".duora-media-area", "synthetic-native-recovery-fixture\n", 0o400)
            os.chown(folder / ".duora-media-area", 1654, 1654)
            os.chown(folder, 1654, 1654)
        put(self.root / "sentinel.sql", "SELECT 1 / (current_setting('transaction_read_only') = 'on')::integer;\nSELECT payload FROM ops.native_recovery_sentinel WHERE id=1;\nSELECT pg_sleep(0.2);\n", 0o444)
        # Fixture HTTP edge uses ACTUAL old image /srv SPA; never follows DNS/HTTPS to production.
        put(self.root / "Caddyfile", ":80 {\n root * /srv\n file_server\n}\n:8081 {\n respond /healthz 200\n}\n", 0o444)
        env = {"DUORA_IMAGE_PREFIX": "ghcr.io/bipoxdxd/duora-", "DUORA_MIN_FREE_GIB": "1", "DUORA_DISK_CHECK_PATH": "/var/lib/docker",
               "DUORA_SECRETS_DIR": str(self.secrets), "DUORA_MEDIA_DIR": str(self.media), "DUORA_MEDIA_REVIEW": "",
               "DUORA_DOMAIN": "native-recovery.invalid", "DUORA_TLS": "internal", "DUORA_HTTP_PORT": "127.0.0.1::80",
               "DUORA_HTTPS_PORT": "0", "DRILL_S3_KEY": s3key, "DRILL_S3_SECRET": s3secret, "DRILL_CERTS_DIR": str(self.certs)}
        # Rendering uses fixed harmless port values, then removes published ports entirely below.
        env["DUORA_HTTP_PORT"] = "0"
        env.update({"DUORA_" + role.upper() + "_IMAGE": pin["reference"] for role, pin in self.pins["old_images"].items()})
        put(self.host_env, "".join(key + "=" + value + "\n" for key, value in env.items()))
        self.report["stage"] = "native_bootstrap_compose_render"
        rendered = json.loads(self.docker("compose", "--project-name", "duora", "--profile", "migrate", "--profile", "restore", "--env-file", self.host_env,
                              "-f", self.args.cohort / "infra/compose/compose.prod.yml", "-f", self.args.workflow_source / "scripts/backup/compose.drill.yml",
                              "config", "--format", "json"))
        # Profiles are enabled ONLY for rendering. Runtime operations retain their explicit service/profile lists.
        required_services = {"postgres", "backup", "restore", "web", "worker", "edge", "migrator", "s3", "internet"}
        self.report["stage"] = "native_bootstrap_service_inventory"
        self.report["bootstrap_services"] = sorted(required_services.intersection(rendered["services"]))
        need(required_services.issubset(rendered["services"]), "native compose omitted a required fixture service")
        rendered["name"] = "duora"
        for name, service in rendered["services"].items():
            service.setdefault("labels", {})[LABEL] = self.nonce
            if name in ("postgres", "backup", "restore"):
                service["image"] = "${DUORA_POSTGRES_IMAGE}"
            elif name in self.pins["old_images"]:
                service["image"] = "${DUORA_" + name.upper() + "_IMAGE}"
        for kind in ("volumes", "networks"):
            for resource in rendered[kind].values():
                resource.setdefault("labels", {})[LABEL] = self.nonce
        memory = {"postgres": 1024 * 1024**2, "backup": 512 * 1024**2, "web": 768 * 1024**2,
                  "worker": 384 * 1024**2, "edge": 128 * 1024**2, "migrator": 768 * 1024**2,
                  "s3": 512 * 1024**2, "internet": 128 * 1024**2}
        for name, limit in memory.items():
            limits = rendered["services"][name].setdefault("deploy", {}).setdefault("resources", {}).setdefault("limits", {})
            limits["memory"] = str(limit)
            limits.setdefault("pids", 256)
            limits.setdefault("cpus", "1")
        pg = rendered["services"]["postgres"]
        pg["command"] = ["postgres", "-c", "shared_buffers=256MB", "-c", "max_connections=100", "-c", "idle_in_transaction_session_timeout=60s"]
        pg["volumes"] += [{"type": "bind", "source": str(self.root / "sentinel.sql"), "target": "/run/lab/sentinel.sql", "read_only": True},
                          {"type": "bind", "source": str(pgpass), "target": "/run/secrets/lab_app_pgpass", "read_only": True}]
        edge = rendered["services"]["edge"]
        edge.pop("ports", None)
        edge["volumes"].append({"type": "bind", "source": str(self.root / "Caddyfile"), "target": "/etc/caddy/Caddyfile", "read_only": True})
        # Pickup writes only to own tmpfs and startup has no pending messages; no Resend or external mail is used.
        rendered["networks"]["egress"]["internal"] = True
        put(self.compose_path, canonical(rendered))
        self.compose = ["/usr/bin/docker", "compose", "--file", str(self.compose_path), "--env-file", str(self.host_env)]
        self.compose_ready = True
        self.report["stage"] = "native_bootstrap_minio_bucket"
        self.compose_run("up", "--detach", "--wait", "s3", "internet")
        fixture_drill = drill.Drill(self.pins["old_images"]["postgres"]["reference"], self.root)
        fixture_drill._compose = self.compose
        fixture_drill._create_locked_bucket()
        self.report["stage"] = "native_bootstrap_postgres_backup"
        self.compose_run("up", "--detach", "--wait", "--no-deps", "backup", "postgres")
        self.compose_run("--profile", "migrate", "run", "--rm", "--no-deps", "migrator", timeout=300)
        self.sql_file((self.args.old_source / self.pins["canonical_sql_path"]).read_text())
        need(self.history() == self.pins["old_migrations"], "bootstrap did not produce exact35")
        self.report["stage"] = "native_bootstrap_backup_wal"
        # Actual bucket protection, full backup and fresh WAL evidence. No fake success wrapper.
        for action in ("stanza-create", "full", "verify"):
            command([str(ENTRIES[1]), action], timeout=600)
        self.compose_run("up", "--detach", "--wait", "--no-deps", "web", "worker", "edge")
        self.report["runtime_budget_bytes"] = memory

    def compose_run(self, *argv, timeout=180):
        return command(self.compose + list(argv), timeout=timeout).stdout.strip()

    def sql(self, sql):
        return self.compose_run("exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "--quiet", "--tuples-only", "--no-align",
                                "--set", "ON_ERROR_STOP=1", "--username", "postgres", "--dbname", "duora", "--command", sql)

    def sql_file(self, text):
        command(self.compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1",
                                "--single-transaction", "--username", "postgres", "--dbname", "duora", "--file", "/dev/stdin"], text=text)

    def history(self):
        return json.loads(self.sql('select coalesce(json_agg("MigrationId" order by "MigrationId"),\'[]\'::json) from ops.__ef_migrations_history;'))

    def database(self):
        item = self.inspect("container", "duora-postgres-1")
        return {"id": item["Id"], "image": item["Image"], "startedAt": item["State"]["StartedAt"], "restartCount": item["RestartCount"],
                "mountsSha256": sha(canonical(sorted(item["Mounts"], key=lambda value: value["Destination"]))),
                "postmasterStart": self.sql("select pg_postmaster_start_time();")}

    def assets(self):
        return {area: sha((self.media / area / ".duora-media-area").read_bytes()) for area in ("quarantine", "derivatives")}

    def bindings(self):
        return {name: sha(path.read_bytes()) for name, path in self.executor._recovery_preservation_paths(self.current, self.paths).items()}

    def schema_digest(self):
        raw = self.compose_run("exec", "--no-TTY", "postgres", "pg_dump", "-U", "postgres", "-d", "duora", "--schema-only", "--no-owner", "--no-acl")
        return sha("\n".join(line for line in raw.splitlines() if not line.startswith(("\\restrict ", "\\unrestrict "))).encode())

    def cli(self, flag, *extra):
        result = command([str(ENTRIES[0]), "--manifest", str(self.current_manifest), "--compose-file", str(self.compose_path), flag, *extra], timeout=1300)
        lines = result.stdout.splitlines()
        need(lines, "CLI omitted result")
        return json.loads(lines[-1])

    def enroll_fixture_control(self):
        self.report["stage"] = "native_enrollment_manifest"
        current = self.manifest("native-old-" + self.nonce[:12], self.pins["old_commit"], self.pins["old_migrations"])
        self.current = self.executor.parse_manifest(json.dumps(current), image_prefix="ghcr.io/bipoxdxd/duora-")
        self.current_manifest = Path("/opt/duora/releases") / (self.current.release_id + ".json")
        current_raw = canonical(current)
        put(self.current_manifest, current_raw)
        put(self.state / "releases" / (self.current.release_id + ".json"), current_raw)
        put(self.state / "releases" / (self.current.release_id + ".env"), "".join("DUORA_" + role.upper() + "_IMAGE=" + ref + "\n" for role, ref in self.current.images.items()) + "DUORA_RELEASE_ID=" + self.current.release_id + "\n")
        record = {"releaseId": self.current.release_id, "commit": self.current.commit, "deployedAt": "2026-10-25T00:00:00+00:00"}
        put(self.state / "current.json", canonical(record))
        put(self.state / "previous.json", canonical({**record, "releaseId": "fixture-prior-never-selected"}))
        put(self.state / "v2-contract-floor.json", canonical({"schema": 1, "minimumContract": "v2", "releaseId": self.current.release_id,
                                                             "commit": self.current.commit, "sqlSha256": self.pins["historical_sql_sha256"]}))
        # Explicit synthetic administrative bootstrap; runtime_inventory itself is real read-only collection.
        self.report["stage"] = "native_enrollment_runtime_inventory"
        inventory = self.executor.runtime_inventory(self.current, self.paths)
        put(self.state / "executor-runtime-baseline.json", canonical(inventory["profile"]))
        self.report["stage"] = "native_enrollment_runtime_recheck"
        self.executor.runtime_recheck(self.current, self.paths)
        self.report["runtime_guard_exercised"] = True
        self.report["stage"] = "native_enrollment_control_bootstrap"
        self.api = LoopbackFixture(self)
        self.api.value = self.current.images["edge"]
        for project, app in self.api.apps.items():
            put(Path("/etc/coolify-release") / (project + ".json"), canonical({"uuid": app}))
            if project != "duora":
                (Path("/var/lib/coolify-release") / project).mkdir(mode=0o700)
        put(Path("/etc/coolify-release/api-token"), self.api.token)
        put(Path("/var/lib/coolify-release/duora/current.json"), canonical({"release": {"project": "duora", "commit": self.current.commit, "duora": current},
                                                                           "deployment_uuid": "fixture-bootstrap", "finished_at": 0}))
        app = self.api.apps["duora"]
        self.original_pin = {"application_uuid": app, "manifest": str(self.current_manifest), "manifest_sha256": sha(current_raw),
                             "compose": str(self.compose_path), "compose_sha256": sha(self.compose_path.read_bytes()),
                             "caddy": str(self.root / "Caddyfile"), "caddy_sha256": sha((self.root / "Caddyfile").read_bytes()),
                             "observer_container_name": "duora-recovery-sleeping-observer",
                             "observer_healthcheck": self.observer.HEALTHCHECK,
                             "observer_labels": {"traefik.enable": "false", "coolify.version": "4.5.0", "coolify.managed": "true"},
                             "observer_networks": {"default": {"name": app + "_default", "ipam": {}},
                                                   "duora_edge": {"name": "duora_edge", "external": True, "ipam": {}}},
                             "observer_environment": {"DUORA_OBSERVER_IMAGE": self.current.images["edge"], "COOLIFY_RESOURCE_UUID": app,
                                                      "COOLIFY_CONTAINER_NAME": "duora-recovery-sleeping-observer",
                                                      "SERVICE_NAME_DEPLOYMENT_OBSERVER": "deployment-observer", "COOLIFY_URL": "", "COOLIFY_FQDN": "",
                                                      "HOST": "0.0.0.0", "SOURCE_COMMIT": self.current.commit}}
        put(self.pinpath, self.executor._recovery_pin_bytes(self.original_pin))
        # Actual observer normalization will re-read this owned JSON Compose through the real Docker CLI.
        service = {"image": "${DUORA_OBSERVER_IMAGE}", "entrypoint": self.observer.ENTRYPOINT, "user": "10001:10001",
                   "read_only": True, "cap_drop": ["ALL"], "security_opt": ["no-new-privileges:true"], "restart": "unless-stopped",
                   "mem_limit": "16m", "pids_limit": 16, "container_name": self.original_pin["observer_container_name"],
                   "labels": self.original_pin["observer_labels"], "environment": self.original_pin["observer_environment"],
                   "healthcheck": self.observer.HEALTHCHECK, "networks": ["default", "duora_edge"]}
        self.observer_file = self.root / "observer" / "docker-compose.json"
        put(self.observer_file, canonical({"name": app, "services": {"deployment-observer": service},
                                         "networks": {"default": {"name": app + "_default", "ipam": {}, "labels": {LABEL: self.nonce}},
                                                      "duora_edge": {"name": "duora_edge", "external": True}}}))
        # validate_observer's allowed network fields exclude labels; ownership of the isolated network is tracked
        # with exact generated name/ID after deployment instead; remove our custom label before normalization.
        observer_data = json.loads(self.observer_file.read_bytes())
        observer_data["networks"]["default"].pop("labels")
        self.observer_file.write_bytes(canonical(observer_data))
        self.api.start()
        self.report["stage"] = "native_enrollment_receiver_idle"
        # Observe only this fixed synthetic fixture ancestry; never repair or relax the original installer guard.
        receiver_root = Path("/opt/coolify-release")
        receiver_version = receiver_root / "versions" / ("2.0.2-" + RECEIVER)
        self.report["receiver_fixture_ancestry"] = []
        for role, path in (("root", Path("/")), ("opt", Path("/opt")), ("receiver_root", receiver_root),
                           ("versions", receiver_version.parent), ("version", receiver_version),
                           ("target", receiver_version / "receiver.py"), ("entry", receiver_root / "receiver.py")):
            info = path.lstat()
            kind = "directory" if stat.S_ISDIR(info.st_mode) else "regular" if stat.S_ISREG(info.st_mode) else "symlink" if stat.S_ISLNK(info.st_mode) else "other"
            self.report["receiver_fixture_ancestry"].append({"role": role, "uid": info.st_uid, "gid": info.st_gid,
                "mode": stat.S_IMODE(info.st_mode), "kind": kind, "links": info.st_nlink})
        receiver = self.executor._recovery_verified_receiver()
        for app_uuid in self.api.apps.values():
            receiver.require_idle(app_uuid)  # Real1ccd HTTP queries for all three synthetic resources.
        self.report["stage"] = "native_enrollment_readonly_projection"
        projection = self.cli("--enroll-current-v2-recovery-control", "--preflight-only")
        need(projection["status"] == "preflight", "read-only enrollment projection missing")
        # Explicit review of this synthetic bootstrap frame; this is not implicit product enrollment.
        self.report["stage"] = "native_enrollment_projection_binding"
        expected = self.executor.inspect_current_v2_recovery_control(self.current, self.paths)
        need(projection["baseline"] == expected and sha(canonical(expected)) == projection["sha256"], "fixture enrollment projection changed")
        self.report["stage"] = "native_enrollment_explicit_admission"
        result = self.cli("--enroll-current-v2-recovery-control", "--reviewed-control-sha256", projection["sha256"])
        need(result["status"] == "enrolled", "fixture explicit enrollment failed")
        self.report["fixture_administrative_bootstrap"] = True
        self.report["fixture_enrollment_sha256"] = projection["sha256"]

    def start_session(self):
        self.sql("create table ops.native_recovery_sentinel (id integer primary key, payload text not null); "
                 "insert into ops.native_recovery_sentinel values (1,'synthetic-" + self.nonce + "'); "
                 "grant select on ops.native_recovery_sentinel to duora_app;")
        self.monitor = "duora-native-readonly-" + self.nonce
        self.session = subprocess.Popen(["/usr/bin/docker", "exec", "--user", "999:999", "--env", "PGPASSFILE=/run/secrets/lab_app_pgpass",
                                         "--env", "PGAPPNAME=" + self.monitor, "--env", "PGOPTIONS=-c default_transaction_read_only=on",
                                         "duora-postgres-1", "pgbench", "--no-vacuum", "--client=1", "--jobs=1", "--time=1800", "--rate=2",
                                         "--protocol=simple", "--file=/run/lab/sentinel.sql", "--host=127.0.0.1", "--username=duora_app", "duora"],
                                        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            need(self.session.poll() is None, "persistent pgbench exited")
            found = self.session_identity(required=False)
            if found:
                return found
            time.sleep(0.2)
        raise ValueError("persistent readonly pgbench missing")

    def session_identity(self, required=True):
        need(self.session is not None and self.session.poll() is None, "persistent readonly process died")
        rows = json.loads(self.sql("select coalesce(json_agg(json_build_object('pid',pid,'backend_start',backend_start)),'[]'::json) "
                                   "from pg_stat_activity where datname=current_database() and usename='duora_app' and application_name='" + self.monitor + "';"))
        if not required and not rows:
            return None
        need(len(rows) == 1, "persistent backend missing/ambiguous")
        return rows[0]

    def sentinel(self):
        return sha(self.sql("select id||':'||payload from ops.native_recovery_sentinel order by id;").encode())

    def incoming_frame(self):
        incoming_raw = self.manifest("native-incoming-" + self.nonce[:12], self.pins["cohort_commit"], self.pins["new_migrations"],
                                     previous=self.current.release_id, incoming=True)
        self.incoming = self.executor.parse_manifest(json.dumps(incoming_raw), image_prefix="ghcr.io/bipoxdxd/duora-")
        path = Path("/opt/duora/releases") / (self.incoming.release_id + ".json")
        put(path, canonical(incoming_raw))
        pin = json.loads(self.pinpath.read_bytes())
        pin["manifest"], pin["manifest_sha256"] = str(path), sha(path.read_bytes())
        pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = self.incoming.images["edge"]
        # This declared fixture ingress frame is a precondition, not a simulated receiver-publication receipt.
        self.pinpath.write_bytes(self.executor._recovery_pin_bytes(pin))
        envelope = {"project": "duora", "commit": self.incoming.commit, "duora": incoming_raw}
        global_raw = canonical({"project": "duora", "commit": self.incoming.commit, "status": "importing"})
        project_raw = canonical({"release": envelope, "deployment_uuid": "fixture-original-forward-job"})
        put(Path("/var/lib/coolify-release/pending.json"), global_raw)
        put(Path("/var/lib/coolify-release/duora/pending.json"), project_raw)
        self.report["original_intents"] = {"global": sha(global_raw), "project": sha(project_raw)}

    def run_native_recovery(self):
        assert self.api is not None
        self.report["history"] = {"before": self.history()}
        self.report["session_before"] = self.start_session()
        self.report["sentinel_before_sha256"] = self.sentinel()
        self.report["database_before"] = self.database()
        self.report["runtime_before"] = self.executor.runtime_recheck(self.current, self.paths)["profile"]
        self.report["bindings_before"] = self.bindings()
        self.report["assets_before"] = self.assets()
        self.continuity = Continuity(self)
        self.continuity.start()
        self.incoming_frame()
        incoming_sql = self.executor._payload.extract_sql(self.incoming.images["migrator"], revision=self.incoming.commit, architecture="amd64")
        # Exactly one local-origin adapter, no Compose rewrite: only this own migrator job env points to the immutable build ID.
        migration_env = self.root / "incoming-local-migrator.env"
        put(migration_env, "DUORA_MIGRATOR_IMAGE=" + self.migrator + "\n")
        migration_compose = self.executor._compose_command(self.paths, self.state / "releases" / (self.current.release_id + ".env"))
        migration_compose += ["--env-file", str(migration_env)]
        with self.executor.recovery_administrative_locks(self.paths):
            store = self.executor.arm_current_v2_deploy(self.incoming, self.paths, incoming_sql, run=self.executor.run_command)
            need(store is not None, "real forward arming refused")
            self.attempt = store.load()
            result = self.executor._run_current_v2_operation(store, "worker-stop", migration_compose + ["stop", "worker"], self.executor.run_command)
            need(result.returncode == 0, "worker stop not positively acknowledged")
            result = self.executor._run_current_v2_operation(store, "migration", migration_compose + ["--profile", "migrate", "run", "--rm", "--no-deps", "migrator"], self.executor.run_command)
            need(result.returncode == 0, "migration unknown/nonzero; recovery refused")
        # Deterministic fault at an acknowledged producer boundary; no unacknowledged work may auto-recover.
        self.report["fault"] = "after_acknowledged_migrator_exit0_before_next_producer"
        self.report["history"]["after_ddl"] = self.history()
        need(self.report["history"]["after_ddl"] == self.pins["new_migrations"], "actual expansion did not reach ordered48")
        self.report["schema_after_ddl_sha256"] = self.schema_digest()
        readonly = self.cli("--recover-current-v2", "--preflight-only")
        need(readonly["status"] == "preflight" and readonly["catalogStage"] == "expanded", "actual expanded recovery preflight")
        application = self.cli("--recover-current-v2")
        need(application["status"] == "application-recovered-control-pending" and application["complete"] is False, "application/control boundary missing")
        self.report["recovery_CLI_exercised"] = True
        self.report["real_backup_WAL_verify"] = True
        self.report["control_result"] = self.cli("--complete-current-v2-recovery-control")
        self.report["complete_control_CLI_exercised"] = True
        before_trace = self.trace_counts()
        before_api = dict(self.api.counts)
        self.report["repeated_control_result"] = self.cli("--complete-current-v2-recovery-control")
        after_trace = self.trace_counts()
        self.report["repeat_effects"] = {key: after_trace[key] - before_trace[key] for key in ("sql_writes", "backup", "compose_up")}
        self.report["repeat_effects"].update(api_patch=self.api.counts["patch_count"] - before_api["patch_count"],
                                              api_start=self.api.counts["start_count"] - before_api["start_count"])
        self.report["api"] = self.api.receipt()
        for key in ("root_locks_exercised", "executor_handoff_exercised", "real_receiver_HTTP_client", "observer_deploy_and_start_exercised"):
            self.report[key] = True
        self.report["history"]["after_recovery"] = self.history()
        self.report["schema_after_recovery_sha256"] = self.schema_digest()
        self.report["session_after"] = self.session_identity()
        self.report["sentinel_after_sha256"] = self.sentinel()
        self.report["database_after"] = self.database()
        self.report["runtime_after"] = self.executor.runtime_recheck(self.current, self.paths)["profile"]
        self.report["bindings_after"] = self.bindings()
        self.report["assets_after"] = self.assets()
        images = self.executor._recovery_require_cached_images(self.current)
        self.report["applications"] = self.executor._recovery_require_running_applications(self.current, self.paths, images)
        self.report["observer"] = self.executor._recovery_require_running_observer(self.current, self.original_pin, images["edge"])
        self.report["actual_recovered_applications"] = self.report["actual_sleeping_observer"] = True
        nonce = self.attempt["nonce"]
        a = self.state / "current-v2-recovery-history" / nonce
        b = self.state / "current-v2-control-history" / nonce
        self.report["archive"] = {"global_preimage_sha256": sha((b / "global-intent.preimage.json").read_bytes()),
                                  "project_preimage_sha256": sha((b / "project-intent.preimage.json").read_bytes()),
                                  "application_outcome_sha256": sha((a / "application-outcome.json").read_bytes()),
                                  "control_outcome_sha256": sha((b / "control-outcome.json").read_bytes()),
                                  "compatibility_pointer_sha256": sha((b / "compatibility-pointer.json").read_bytes()),
                                  "fences_absent": not Path("/var/lib/coolify-release/duora-current-v2-recovery/pending.json").exists(),
                                  "active_intents_absent": all(not path.exists() for path in (self.state / "current-v2-recovery", self.state / "current-v2-control",
                                                                        Path("/var/lib/coolify-release/pending.json"), Path("/var/lib/coolify-release/duora/pending.json")))}
        need((b / "compatibility-pointer.json").read_bytes() == (self.state / "current-v2-compatibility.json").read_bytes(), "compatibility pointer drift")
        self.continuity.stop(complete=True)
        self.report["continuity"] = self.continuity.receipt({self.report["database_before"]["id"], self.report["runtime_before"]["services"]["backup"]["id"]})
        self.continuity = None
        trace_raw = self.trace.read_bytes()
        trace_path = self.args.output.with_name("recovery-command-trace.ndjson")
        put(trace_path, trace_raw, 0o644)
        self.report["command_trace_sha256"] = sha(trace_raw)
        public_records = {"application": json.loads((a / "application-outcome.json").read_bytes()),
                          "control": json.loads((b / "control-outcome.json").read_bytes()),
                          "ready": json.loads((b / "control-ready.json").read_bytes()),
                          "compatibility": json.loads((b / "compatibility-pointer.json").read_bytes())}
        records_raw = canonical(public_records)
        put(self.args.output.with_name("recovery-public-records.json"), records_raw, 0o644)
        self.report["public_records_sha256"] = sha(records_raw)

    def trace_counts(self):
        raw = self.trace.read_bytes()
        need(len(raw) <= BOUND, "classification trace bound")
        result = {key: 0 for key in ("sql_writes", "backup", "compose_up")}
        for line in raw.splitlines():
            item = json.loads(line)
            if item["kind"] in result:
                result[item["kind"]] += 1
        return result

    def cleanup(self):
        errors = []
        try:
            if self.continuity:
                self.continuity.stop()
            if self.api:
                self.api.stop()
            if self.session and self.session.poll() is None:
                self.session.terminate()
                self.session.wait(timeout=5)
        except Exception:
            errors.append("observers")
        # Never Compose down/restart/prune. Remove only the own resources after evidence has been captured.
        if self.compose_ready:
            try:
                for identifier in self.docker("ps", "--all", "--quiet", "--filter", "label=" + LABEL + "=" + self.nonce).split():
                    item = self.inspect("container", identifier)
                    labels = item["Config"]["Labels"]
                    need(labels.get(LABEL) == self.nonce, "cleanup foreign container")
                    if LABEL + ".barrier" in labels:
                        Continuity.require_owned_marker(self, identifier, labels[LABEL + ".barrier"])
                        self.docker("container", "rm", identifier)  # Only the verified never-started own marker; no force.
                    else:
                        self.docker("container", "rm", "--force", identifier)
                if hasattr(self, "original_pin"):
                    assert self.api is not None
                    name = self.original_pin["observer_container_name"]
                    result = command(["/usr/bin/docker", "container", "inspect", name], checked=False)
                    if result.returncode == 0:
                        item = json.loads(result.stdout)[0]
                        need(item["Config"]["Labels"].get("com.docker.compose.project") == self.api.apps["duora"]
                             and item["Image"] == self.report["old_images"]["edge"]["docker_image_id"], "cleanup observer foreign identity")
                        self.docker("container", "rm", "--force", item["Id"])
                for kind in ("volume", "network"):
                    for name in self.docker(kind, "ls", "--quiet", "--filter", "label=" + LABEL + "=" + self.nonce).split():
                        item = self.inspect(kind, name)
                        need((item.get("Labels") or {}).get(LABEL) == self.nonce, "cleanup foreign persistent resource")
                        self.docker(kind, "rm", name)
                if self.api:
                    network = self.api.apps["duora"] + "_default"
                    result = command(["/usr/bin/docker", "network", "inspect", network], checked=False)
                    if result.returncode == 0:
                        item = json.loads(result.stdout)[0]
                        need(item["Labels"].get("com.docker.compose.project") == self.api.apps["duora"] and not item["Containers"], "cleanup observer network foreign")
                        self.docker("network", "rm", item["Id"])
            except Exception:
                errors.append("owned-native-resources")
        if not errors and hasattr(self, "keeper"):
            try:
                item = self.inspect("image", self.keeper)
                need(item["Id"] == self.migrator, "keeper identity changed before final owned image cleanup")
                self.docker("image", "rm", self.keeper)
                self.report["keeper_cleaned_last"] = True
            except Exception:
                errors.append("keeper")
        # Audit hook remains active through return: preserve its file until no subprocesses remain.
        for entry in reversed(self.created_entries):
            try:
                need(entry.is_file() and not entry.is_symlink(), "cleanup root entry changed")
                entry.unlink()
            except Exception:
                errors.append("entry")
        if not errors:
            for root in reversed(self.created_roots):
                try:
                    need((root / ".native-recovery-owner").read_text() == self.nonce + "\n", "cleanup foreign root")
                    shutil.rmtree(root)
                except Exception:
                    errors.append("root")
        try:
            self.restore_actions_opt_fixture()
        except Exception:
            errors.append("Actions-opt-restoration")
        return not errors

    def execute(self):
        self.report["stage"] = "frozen_source_preflight"
        self.verify_sources()
        self.report["stage"] = "same_job_functional_handoff"
        self.preflight_handoff()
        self.report["stage"] = "fresh_owned_root_bootstrap"
        self.reserve_fresh_root()
        self.install_fixture_bundle()
        self.report["stage"] = "real_pg_backup_bucket_wal_bootstrap"
        self.prepare_runtime()
        self.report["stage"] = "explicit_fixture_control_enrollment"
        self.enroll_fixture_control()
        self.report["stage"] = "acknowledged_expand_recover_complete_repeat"
        self.run_native_recovery()
        self.verify_sources()
        self.report["stage"] = "native_evidence_captured"


def cleanup_transferred_keeper(handoff_path):
    """Final same-job fallback only. No root/bootstrap/runtime mutation and never prune."""
    require_actions()
    temporary = Path(os.environ["RUNNER_TEMP"]).resolve()
    need(handoff_path.resolve().is_relative_to(temporary), "keeper handoff outside RUNNER_TEMP")
    if not handoff_path.exists():
        return 0
    raw = handoff_path.read_bytes()
    need(len(raw) <= 4096, "keeper handoff bound")
    handoff = json.loads(raw)
    receipt = handoff_path.with_name("receipt.json").read_bytes()
    need(handoff["run_id"] == os.environ["GITHUB_RUN_ID"] and handoff["attempt"] == os.environ["GITHUB_RUN_ATTEMPT"]
         and sha(receipt) == handoff["functional_receipt_sha256"]
         and re.fullmatch(r"duora259-recovery-keeper-[a-f0-9]{32}", handoff["keeper"])
         and re.fullmatch(r"sha256:[a-f0-9]{64}", handoff["docker_image_id"]), "keeper cleanup ownership refused")
    result = command(["/usr/bin/docker", "image", "inspect", handoff["keeper"]], checked=False)
    if result.returncode:
        need("No such image" in result.stderr or "No such object" in result.stderr, "keeper inspection unknown")
        return 0  # The successful native driver already cleaned its transferred tag.
    item = json.loads(result.stdout)[0]
    need(item["Id"] == handoff["docker_image_id"]
         and re.fullmatch(r"[a-f0-9]{32}", (item["Config"].get("Labels") or {}).get("io.duora.current-schema-lab", "")),
         "foreign keeper identity refused")
    command(["/usr/bin/docker", "image", "rm", handoff["keeper"]])
    return 0


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--cleanup-transferred-keeper":
        return cleanup_transferred_keeper(Path(sys.argv[2]))
    if len(sys.argv) > 1 and sys.argv[1] == "--fixture-entry":
        return fixture_entry()
    parser = argparse.ArgumentParser()
    for name in ("cohort", "old-source", "workflow-source", "functional-receipt", "handoff", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    head = require_actions()
    os.umask(0o077)
    temporary = Path(os.environ["RUNNER_TEMP"]).resolve()
    for path in (args.output, args.functional_receipt, args.handoff):
        need(path.resolve().is_relative_to(temporary), "native artifacts must remain in RUNNER_TEMP")
    os.environ["GIT_NO_LAZY_FETCH"] = "1"
    lab = NativeLab(args, head)
    r = lab.report
    r["runtime"] = {"system": platform.system(), "architecture": platform.machine(), "uid": os.geteuid(),
                    "github_hosted": True, "python": "3.12"}
    r["run"] = {"id": os.environ["GITHUB_RUN_ID"], "attempt": os.environ["GITHUB_RUN_ATTEMPT"], "pull_request": 260,
                "workflow_sha": os.environ["GITHUB_WORKFLOW_SHA"], "merge_sha": os.environ["GITHUB_SHA"], "workflow_head": head}
    try:
        lab.execute()
        r["success"] = True
    except BaseException as error:
        r["success"] = False
        r["failure_type"] = type(error).__name__
        r["failure_stage"] = r.get("stage", "source_preflight")
        r["failure_locations"] = failure_locations(error)
        # Exception type/stage only: even third-party failures can embed credentials or SQL.
    finally:
        r["cleanup_successful"] = lab.cleanup()
    try:
        validate_receipt(r, lab.pins)
        code = 0
    except (ValueError, KeyError, TypeError):
        r["success"] = False
        code = 1
    raw = canonical(r)
    need(len(raw) <= 131072, "native receipt bound")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(raw)
    args.output.chmod(0o644)
    print(json.dumps({"scope": SCOPE, "success": r["success"], "payload_origin": "local_cohort_build", "incoming_ghcr_exercised": False}))
    return code


if __name__ == "__main__":
    sys.exit(main())
