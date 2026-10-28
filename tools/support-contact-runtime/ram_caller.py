#!/usr/bin/env python3
"""Operator69-only RAM caller. Default/unqualified invocation does no capture.

No build, network probe, SDK, remote session, privilege elevation or environment
capture is performed at import. Only the main entrypoint operates on Linux.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import selectors
import signal
import subprocess
import sys
import time
import uuid

_spec = importlib.util.spec_from_file_location("support_contact_capture", Path(__file__).with_name("capture.py"))
capture_module = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(capture_module)
Refusal = capture_module.Refusal
SUPPLIER_SECONDS, TOTAL_SECONDS, CLEANUP_SECONDS = 5, 25, 5
MAX_OUTPUT, RUNNER_SECONDS = 16384, 15
ARTIFACT_HEAD = "4da134f32f3cc0a2c699f776365f647549858f1a"
FALSE_FLAGS = ("cached_runtime_options_observed", "runtime_effective_proven", "application_readiness_observed", "smtp_observed", "outbox_observed", "m5_observed")
CLEAN_ENV = {"PATH": "/usr/bin:/bin", "DOCKER_HOST": "unix:///var/run/docker.sock", "DOCKER_CONFIG": "/nonexistent"}
ENTRYPOINT = ["/opt/python/bin/python3.13", "-I", "-B", "/opt/probe/run_preserved_probe.py", "--artifact-dir", "/opt/probe/artifact", "--dotnet", "/usr/share/dotnet/dotnet"]
# Set only inside the dedicated supplier child, after setsid succeeds. Nested
# metadata CLIs remain in this owned group, so timeout cannot orphan them.
SUPPLIER_SESSION = False
REASONS = {"UNKNOWN_RUNTIME_PROFILE", "UNKNOWN_PRIVATE_CAPTURE", "ROLE_INVALID", "ROLE_ALREADY_ATTEMPTED", "C934_CONTEXT_CHANGED", "SUPPLIER_TIMEOUT", "CAPTURE_INVALID", "CALLER_TIMEOUT", "OUTPUT_INVALID", "PROBE_UNKNOWN", "CALLER_FAILED", "SANDBOX_UNQUALIFIED", "CLEANUP_UNCONFIRMED"}

class Session:
    def __init__(self): self.attempted = set()

def sandbox_plan(role, admitted, identity):
    capture_module.check_admission(admitted)
    capture_module.validate_identity(role, identity, admitted)
    pid = identity["pid"]
    mounts = [("/proc/" + str(pid) + "/root/app", "/app"),
              ("/proc/" + str(pid) + "/root/run/secrets", "/run/secrets")]
    argv = ["container", "create", "--pull=never", "--interactive", "--user", "1654:1654",
        "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
        "--log-driver", "none", "--memory", "536870912", "--memory-swap", "536870912", "--cpus", "0.5",
        "--pids-limit", "64", "--ulimit", "core=0:0", "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16777216",
        "--workdir", "/app", "--entrypoint", ENTRYPOINT[0]]
    for source, target in mounts:
        argv += ["--mount", "type=bind,source=" + source + ",destination=" + target + ",readonly,bind-propagation=rprivate"]
    argv += [admitted["profile"]["image_digest"]] + ENTRYPOINT[1:]
    return {"create_argv": argv, "entrypoint": list(ENTRYPOINT), "mounts": mounts,
        "profile": dict(admitted["profile"]), "identity": dict(identity)}

def sanitize_reply(raw):
    try:
        if type(raw) is not bytes or len(raw) > MAX_OUTPUT: raise ValueError()
        value = json.loads(raw, object_pairs_hook=capture_module.unique_object)
        keys = {"artifact_head", "cached_runtime_options_observed", "runtime_effective_proven", "application_readiness_observed", "status", "configuration_matches_approved", "ioptions_matches_approved", "source", "infrastructure_module_id"}
        if type(value) is not dict or set(value) != keys or value["artifact_head"] != ARTIFACT_HEAD: raise ValueError()
        for key in FALSE_FLAGS[:3]:
            if value[key] is not False: raise ValueError()
        for key in ("configuration_matches_approved", "ioptions_matches_approved"):
            if type(value[key]) is not bool: raise ValueError()
        matches = value["configuration_matches_approved"] and value["ioptions_matches_approved"]
        if value["status"] != ("replay_verified" if matches else "replay_mismatch"): raise ValueError()
        if value["source"] not in {"json", "environment", "command_line", "key_per_file"}: raise ValueError()
        module = str(uuid.UUID(value["infrastructure_module_id"]))
        return {"status": "REPLAY_VERIFIED" if matches else "REPLAY_MISMATCH", "configuration_matches_approved": value["configuration_matches_approved"],
            "ioptions_matches_approved": value["ioptions_matches_approved"], "source": value["source"], "infrastructure_module_id": module}
    except (ValueError, TypeError, UnicodeError, AttributeError, KeyError):
        raise Refusal("OUTPUT_INVALID") from None

def run_role(role, admitted, authority, executor, session=None, clock=time.monotonic):
    session = session if session is not None else Session()
    result = {key: False for key in FALSE_FLAGS}
    result.update(status="UNKNOWN", cleanup_confirmed=False)
    started = False
    local_cleanup_confirmed = True
    try:
        capture_module.check_admission(admitted)
        if role not in capture_module.ROLES: raise Refusal("ROLE_INVALID")
        if role in session.attempted: raise Refusal("ROLE_ALREADY_ATTEMPTED")
        session.attempted.add(role)
        deadline = clock() + TOTAL_SECONDS
        identity = authority.snapshot(role, deadline)
        capture_module.validate_identity(role, identity, admitted)
        plan = sandbox_plan(role, admitted, identity)
        def supplier():
            if clock() >= deadline: raise Refusal("CALLER_TIMEOUT")
            limit = min(deadline, clock() + SUPPLIER_SECONDS)
            if authority.snapshot(role, limit) != identity: raise Refusal("C934_CONTEXT_CHANGED")
            payload = capture_module.capture(role, admitted, authority, limit)
            if authority.snapshot(role, limit) != identity: raise Refusal("C934_CONTEXT_CHANGED")
            return payload
        started = True
        raw = executor.run(plan, supplier, deadline)
        if clock() >= deadline: raise Refusal("CALLER_TIMEOUT")
        after = authority.snapshot(role, deadline)
        if identity != after: raise Refusal("C934_CONTEXT_CHANGED")
        result.update(sanitize_reply(raw))
    except Refusal as error:
        if error.reason in {"SUPPLIER_CLEANUP_UNCONFIRMED", "CALLER_CLEANUP_UNCONFIRMED"}: local_cleanup_confirmed = False
        result.update(status="UNKNOWN", reason=error.reason if error.reason in REASONS else "CALLER_FAILED")
    except (Exception, KeyboardInterrupt):
        result.update(status="UNKNOWN", reason="CALLER_FAILED")
    finally:
        if started:
            try: result["cleanup_confirmed"] = executor.cleanup_owned(clock() + CLEANUP_SECONDS) is True
            except (Exception, KeyboardInterrupt): result["cleanup_confirmed"] = False
            if result["cleanup_confirmed"] is not True or not local_cleanup_confirmed:
                # A sanitized replay is not accepted while owned resources remain.
                result = {key: False for key in FALSE_FLAGS}
                result.update(status="UNKNOWN", reason="CLEANUP_UNCONFIRMED", cleanup_confirmed=False)
    return result

def bounded_command(argv, deadline, payload=None, allow_stderr=False):
    """One bounded local Docker CLI invocation, with a clean environment.

    Docker/.NET diagnostics are discarded and never become exception messages.
    No shell, inherited authentication, Docker context, stdin or extra FDs.
    """
    process = None
    selector = selectors.DefaultSelector()
    output, count, sent, diagnostic = bytearray(), 0, 0, False
    try:
        if time.monotonic() >= deadline: raise Refusal("CALLER_TIMEOUT")
        process = subprocess.Popen(["/usr/bin/docker"] + argv,
            stdin=subprocess.PIPE if payload is not None else subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=CLEAN_ENV,
            close_fds=True, start_new_session=not SUPPLIER_SESSION)
        # Lazy supplier starts only after the sandbox process has started and
        # its actual mounted preimage has been checked, before stdin is written.
        if callable(payload): payload = payload()
        streams = [(process.stdout, selectors.EVENT_READ), (process.stderr, selectors.EVENT_READ)]
        if payload is not None: streams.append((process.stdin, selectors.EVENT_WRITE))
        for stream, event in streams:
            os.set_blocking(stream.fileno(), False); selector.register(stream, event)
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0: raise Refusal("CALLER_TIMEOUT")
            for key, _ in selector.select(min(remaining, 0.1)):
                stream = key.fileobj
                if stream is process.stdin:
                    if sent < len(payload): sent += os.write(stream.fileno(), payload[sent:sent + 4096])
                    if sent == len(payload): selector.unregister(stream); stream.close()
                else:
                    part = os.read(stream.fileno(), 4096)
                    if not part: selector.unregister(stream); stream.close(); continue
                    count += len(part)
                    if count > MAX_OUTPUT: raise Refusal("OUTPUT_INVALID")
                    if stream is process.stdout: output.extend(part)
                    else: diagnostic = True
        remaining = deadline - time.monotonic()
        if remaining <= 0: raise Refusal("CALLER_TIMEOUT")
        code = process.wait(timeout=remaining)
        if diagnostic and not allow_stderr: raise Refusal("CALLER_FAILED")
        return code, bytes(output)
    except (OSError, subprocess.TimeoutExpired): raise Refusal("CALLER_FAILED") from None
    finally:
        selector.close()
        if process is not None:
            try:
                if SUPPLIER_SESSION: os.kill(process.pid, signal.SIGKILL)
                else: os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError: pass
            try: process.wait(timeout=0.5)
            except subprocess.TimeoutExpired: raise Refusal("CALLER_CLEANUP_UNCONFIRMED") from None
            for stream in (process.stdin, process.stdout, process.stderr):
                if stream is not None: stream.close()

def validate_mounted_identity(expected, observed):
    if type(expected) is not list or len(expected) != 2 or expected != observed:
        raise Refusal("C934_CONTEXT_CHANGED")


def read_supplier_pipe(read_fd, deadline):
    """Bound bytes AND EOF. A stalled open writer is a timeout even below cap."""
    selector = selectors.DefaultSelector()
    data = bytearray()
    try:
        os.set_blocking(read_fd, False); selector.register(read_fd, selectors.EVENT_READ)
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0: raise Refusal("SUPPLIER_TIMEOUT")
            if not selector.select(min(remaining, 0.1)): continue
            chunk = os.read(read_fd, 4096)
            if not chunk: return bytes(data)
            data.extend(chunk)
            if len(data) > capture_module.MAX_CAPTURE: raise Refusal("CAPTURE_INVALID")
    finally: selector.close()


def bounded_supplier(supplier, deadline):
    """A dedicated owned session bounds supplier and every nested metadata CLI.

    Always kill/verify its process group, even after leader exit and pipe EOF.
    Existing RAM protection is reapplied before synthetic/real supplier work.
    """
    global SUPPLIER_SESSION
    read_fd, write_fd = os.pipe2(os.O_CLOEXEC)
    try: child = os.fork()
    except BaseException:
        os.close(read_fd); os.close(write_fd)
        raise
    if child == 0:
        os.close(read_fd)
        try:
            os.setsid()
            SUPPLIER_SESSION = True
            capture_module.protect_memory()
            value = supplier()
            if len(value) > capture_module.MAX_CAPTURE: os._exit(2)
            offset = 0
            while offset < len(value): offset += os.write(write_fd, value[offset:offset + 4096])
            os.close(write_fd); os._exit(0)
        except BaseException:
            os.close(write_fd); os._exit(2)
    os.close(write_fd)
    reaped = False
    try:
        data = read_supplier_pipe(read_fd, deadline)
        # EOF is not leader termination; leader termination is not group cleanup.
        while True:
            done, status = os.waitpid(child, os.WNOHANG)
            if done:
                reaped = True
                if status != 0: raise Refusal("UNKNOWN_PRIVATE_CAPTURE")
                return bytes(data)
            if time.monotonic() >= deadline: raise Refusal("SUPPLIER_TIMEOUT")
            time.sleep(0.01)
    finally:
        os.close(read_fd)
        try: os.killpg(child, signal.SIGKILL)
        except ProcessLookupError:
            # If the deadline won the race before setsid, the child has not run
            # supplier work yet. Kill that exact owned child, never operator PG.
            if not reaped:
                try: os.kill(child, signal.SIGKILL)
                except ProcessLookupError: pass
        limit = time.monotonic() + 0.5
        group_gone = False
        while time.monotonic() < limit:
            if not reaped:
                done, _ = os.waitpid(child, os.WNOHANG)
                reaped = bool(done)
            try: os.killpg(child, 0)
            except ProcessLookupError: group_gone = True
            except PermissionError: group_gone = False
            if reaped and group_gone: break
            time.sleep(0.01)
        if not (reaped and group_gone): raise Refusal("SUPPLIER_CLEANUP_UNCONFIRMED")

class DockerExecutor:
    """Only owns its fresh random-name sandbox. Never targets running app roles."""
    def __init__(self): self.name = "duora-support-contact-" + uuid.uuid4().hex; self.owned = False
    def public(self, argv, deadline):
        code, raw = bounded_command(argv, deadline)
        if code != 0: raise Refusal("CALLER_FAILED")
        return raw
    def verify_sandbox(self, plan, deadline):
        fmt = '{"image":{{json .Image}},"host":{{json .HostConfig}},"config":{{json .Config}},"mounts":{{json .Mounts}}}'
        try:
            value = json.loads(self.public(["container", "inspect", "--format", fmt, self.name], deadline), object_pairs_hook=capture_module.unique_object)
            host, config = value["host"], value["config"]
            if value["image"] != plan["profile"]["config_digest"] or config["User"] != "1654:1654": raise ValueError()
            if config["Entrypoint"] != ENTRYPOINT[:1] or config["Cmd"] != ENTRYPOINT[1:] or config["WorkingDir"] != "/app": raise ValueError()
            if host["NetworkMode"] != "none" or host["ReadonlyRootfs"] is not True or host["CapDrop"] != ["ALL"] or "no-new-privileges=true" not in host["SecurityOpt"] or host["LogConfig"]["Type"] != "none": raise ValueError()
            if host.get("CapAdd") not in (None, []): raise ValueError()
            if host["Privileged"] is not False or host["PidMode"] not in {"", "private"} or host["IpcMode"] not in {"private", "none"} or host["Binds"] not in (None, []): raise ValueError()
            if host["Memory"] != 536870912 or host["MemorySwap"] != 536870912 or host["PidsLimit"] != 64 or host["NanoCpus"] != 500000000: raise ValueError()
            expected = set(plan["mounts"])
            actual = set()
            for mount in value["mounts"]:
                if mount["Type"] != "bind" or mount["RW"] is not False or mount.get("Propagation") != "rprivate": raise ValueError()
                actual.add((mount["Source"], mount["Destination"]))
            if actual != expected or len(value["mounts"]) != len(expected): raise ValueError()
            # Exact qualified public image environment; no host values enter.
            if config["Env"] != plan["profile"]["public_environment"]: raise ValueError()
            if host["Tmpfs"] != {"/tmp": "rw,noexec,nosuid,nodev,size=16777216"}: raise ValueError()
            if not any(u["Name"] == "core" and u["Soft"] == 0 and u["Hard"] == 0 for u in host["Ulimits"]): raise ValueError()
        except (ValueError, KeyError, TypeError): raise Refusal("SANDBOX_UNQUALIFIED") from None
    def run(self, plan, supplier, deadline):
        argv = list(plan["create_argv"])
        argv[2:2] = ["--name", self.name]
        # Set ownership before request: a lost response can still have created it.
        self.owned = True
        raw = self.public(argv, deadline)
        if not re.fullmatch(b"[0-9a-f]{64}\n?", raw): raise Refusal("SANDBOX_UNQUALIFIED")
        self.verify_sandbox(plan, deadline)
        payload = lambda: self.capture_mounted(plan, supplier, deadline)
        code, reply = bounded_command(["container", "start", "--attach", "--interactive", self.name], deadline, payload)
        if code not in {0, 3}: raise Refusal("PROBE_UNKNOWN")
        return reply
    def capture_mounted(self, plan, supplier, deadline):
        limit = min(deadline, time.monotonic() + SUPPLIER_SECONDS)
        fmt = '{"pid":{{json .State.Pid}},"running":{{json .State.Running}}}'
        fd = None
        try:
            while time.monotonic() < limit:
                value = json.loads(self.public(["container", "inspect", "--format", fmt, self.name], limit), object_pairs_hook=capture_module.unique_object)
                if value["running"] is True and type(value["pid"]) is int and value["pid"] > 0: break
                time.sleep(0.05)
            else: raise Refusal("CALLER_TIMEOUT")
            pid = value["pid"]
            fd = os.open("/proc/" + str(pid), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            def stamp():
                root = os.open("root", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC, dir_fd=fd)
                result = []
                try:
                    for parts in [("app",), ("run", "secrets")]:
                        current = os.dup(root)
                        try:
                            for component in parts:
                                following = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=current)
                                os.close(current); current = following
                            st = os.fstat(current); result.append((st.st_dev, st.st_ino))
                        finally: os.close(current)
                finally: os.close(root)
                return result
            validate_mounted_identity(plan["identity"].get("mount_stamp"), stamp())
            payload = bounded_supplier(supplier, limit)
            validate_mounted_identity(plan["identity"].get("mount_stamp"), stamp())
            return payload
        except (ValueError, OSError, KeyError, TypeError): raise Refusal("C934_CONTEXT_CHANGED") from None
        finally:
            if fd is not None: os.close(fd)

    def cleanup_owned(self, deadline):
        if not self.owned: return False
        self.public(["container", "rm", "--force", self.name], deadline)
        raw = self.public(["container", "ls", "--all", "--quiet", "--filter", "name=^/" + self.name + "$"], deadline)
        if raw.strip(): return False
        self.owned = False
        return True

class Parser(argparse.ArgumentParser):
    def error(self, _message): raise Refusal("UNKNOWN_PRIVATE_CAPTURE")

def main(argv=None, output=None):
    output = output if output is not None else sys.stdout
    results = []
    authority = None
    try:
        parser = Parser(description=__doc__)
        parser.add_argument("--admission", type=Path)
        parser.add_argument("--admission-sha256")
        args = parser.parse_args(argv)
        if args.admission is None or args.admission_sha256 is None: raise Refusal("UNKNOWN_RUNTIME_PROFILE")
        if args.admission.is_symlink(): raise Refusal("UNKNOWN_PRIVATE_CAPTURE")
        with args.admission.open("rb") as stream: raw = stream.read(capture_module.MAX_CAPTURE + 1)
        admitted = capture_module.load_admission(raw, args.admission_sha256)
        capture_module.protect_memory()
        # Only public image metadata may precede private read. LinuxAuthority
        # enforces exact existing nonroot reader identity; no fallback.
        metadata = DockerExecutor()
        authority = capture_module.LinuxAuthority(admitted, metadata)
        session = Session()
        for role in ("web", "worker"):
            result = run_role(role, admitted, authority, DockerExecutor(), session)
            results.append(dict(result, role=role))
            if result["status"] == "UNKNOWN": break
    except Refusal as error:
        result = {key: False for key in FALSE_FLAGS}
        result.update(status="UNKNOWN", reason=error.reason if error.reason in REASONS else "CALLER_FAILED", cleanup_confirmed=False)
        results.append(result)
    except (Exception, KeyboardInterrupt):
        results.append(dict({key: False for key in FALSE_FLAGS}, status="UNKNOWN", reason="CALLER_FAILED", cleanup_confirmed=False))
    finally:
        if authority is not None: authority.close()
    output.write(json.dumps({"results": results}, separators=(",", ":")) + "\n")
    return 0 if results and all(r["status"] == "REPLAY_VERIFIED" for r in results) else 2

if __name__ == "__main__": raise SystemExit(main())
