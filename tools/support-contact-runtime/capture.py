"""Fixed C934 protected reader. Importing this module performs no host operation.

Only operator69 may construct LinuxAuthority on the authorized Linux host. Public
admission must be pinned independently; receipt digests are attestations, not facts
inferred from Docker. Private values have no public representation or fingerprint.
"""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import resource
import sys
import time

MAX_CAPTURE = 262144
PINS_SHA256 = "9c779cebc3dc3af2a0b0c0c945fef86460210bd19bcd6187f68e0a6e536bf128"
RUNNER_SHA256 = "87e09073a60a5c2ab12804c25160fa8d67c8cb53a297a052b0bd4d3733ab8211"
DLL_SHA256 = "11419c7d93a94666d535b5b5cfa2205d2ac0d1f8d0c840297f3eaa2489b044c3"
SOURCE = "c9341ab39a5e3133067eb579021efdd8c19b0033"
ROLES = {
    "web": {"container": "aa597c09dd34de16402f71e0f8578fa7abd159016285d9e1a35f84bdc7b5eb6b",
        "started": "2026-10-26T02:38:13.054108541Z",
        "reference": "ghcr.io/bipoxdxd/duora-web@sha256:3e400f5c99ef7fd59415483226819b3ef8dc80d975fc34257faae81ae289224c",
        "native": "sha256:2dfea2ca945197c09bdd4f6d924b2a9c44e82c317061df1d59dcd967dee2138d",
        "config": "sha256:96e57335081a56e54b51d8e4d1dd3d9457aa96a37a3503f512eb78ba70edea5d",
        "source": SOURCE, "assembly": "/app/Duora.Web.dll"},
    "worker": {"container": "655f8ae20bff9ea9a183d7b23f608a1bca0b41162ecbc1e55d88d3d075c73e86",
        "started": "2026-10-26T02:38:13.660354919Z",
        "reference": "ghcr.io/bipoxdxd/duora-worker@sha256:fe0547db66a5454a1c5398e9d85a6e23bf936e211dba3cd0fccee5b14fb97468",
        "native": "sha256:73a8b6ada4f2ce322ab68fdce465c40fbab0e6ddc7c96a48eac8e07dcb1be995",
        "config": "sha256:14ba70702caae34334cd2074046d5a2e6550b8a796e5c6f2463c5227aca7a730",
        "source": SOURCE, "assembly": "/app/Duora.Worker.dll"},
}
PROFILE_KEYS = {"image_digest", "config_digest", "qualification_receipt_sha256",
    "python_closure_sha256", "public_environment", "independent_review_sha256", "capture_source_sha256", "caller_source_sha256"}
WITNESS_KEYS = {"startup_stability_receipt_sha256", "mounted_preimage_receipt_sha256"}

class Refusal(Exception):
    """Only fixed public reasons; never chain raw exceptions into output."""
    def __init__(self, reason):
        self.reason = reason
        super().__init__(reason)

def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result: raise ValueError()
        result[key] = value
    return result

def digest(value, prefix=False):
    return isinstance(value, str) and re.fullmatch(("sha256:" if prefix else "") + "[0-9a-f]{64}", value) is not None

def check_admission(value):
    if value is None: raise Refusal("UNKNOWN_RUNTIME_PROFILE")
    try:
        if type(value) is not dict or set(value) != {"version", "pins_sha256", "reader_uid", "exclusive_operator", "handoff_sha256", "profile", "roles"}: raise ValueError()
        if type(value["version"]) is not int or value["version"] != 1: raise ValueError()
        if value["pins_sha256"] != PINS_SHA256 or type(value["exclusive_operator"]) is not int or value["exclusive_operator"] != 69: raise ValueError()
        if type(value["reader_uid"]) is not int or value["reader_uid"] <= 0 or not digest(value["handoff_sha256"]): raise ValueError()
        profile = value["profile"]
        if type(profile) is not dict or set(profile) != PROFILE_KEYS: raise ValueError()
        for key in PROFILE_KEYS - {"public_environment"}:
            if not digest(profile[key], key in {"image_digest", "config_digest"}): raise ValueError()
        public_env = profile["public_environment"]
        if type(public_env) is not list or not public_env or not all(type(item) is str and "=" in item and "\0" not in item for item in public_env): raise ValueError()
        names = [item.split("=", 1)[0] for item in public_env]
        fixed_controls = {"DOTNET_NOLOGO": "true", "DOTNET_CLI_TELEMETRY_OPTOUT": "true", "DOTNET_EnableDiagnostics": "0", "COMPlus_DbgEnableMiniDump": "0"}
        allowed = {"PATH", "APP_UID", "ASPNET_VERSION", "DOTNET_VERSION", "DOTNET_RUNNING_IN_CONTAINER", "ASPNETCORE_HTTP_PORTS", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"} | set(fixed_controls)
        if len(set(names)) != len(names) or not set(names).issubset(allowed) or "PATH=/usr/bin:/bin" not in public_env: raise ValueError()
        if any(name in fixed_controls and item.split("=", 1)[1] != fixed_controls[name] for name, item in zip(names, public_env)): raise ValueError()
        for name, key in [("capture.py", "capture_source_sha256"), ("ram_caller.py", "caller_source_sha256")]:
            if hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() != profile[key]: raise ValueError()
        if type(value["roles"]) is not dict or set(value["roles"]) != set(ROLES): raise ValueError()
        for role in ROLES:
            witness = value["roles"][role]
            if type(witness) is not dict or set(witness) != WITNESS_KEYS or not all(digest(v) for v in witness.values()): raise ValueError()
    except (ValueError, TypeError, KeyError, OSError):
        raise Refusal("UNKNOWN_PRIVATE_CAPTURE") from None
    return value

def load_admission(raw, expected_sha256):
    try:
        if type(raw) is not bytes or len(raw) > MAX_CAPTURE or not digest(expected_sha256) or hashlib.sha256(raw).hexdigest() != expected_sha256: raise ValueError()
        return check_admission(json.loads(raw, object_pairs_hook=unique_object))
    except (ValueError, TypeError, UnicodeError):
        raise Refusal("UNKNOWN_PRIVATE_CAPTURE") from None

def check_deadline(authority, deadline):
    if authority.clock() >= deadline: raise Refusal("SUPPLIER_TIMEOUT")

def validate_identity(role, value, admitted):
    if role not in ROLES: raise Refusal("ROLE_INVALID")
    try:
        if any(value[key] != expected for key, expected in ROLES[role].items()): raise ValueError()
        if type(value["pid"]) is not int or value["pid"] <= 0 or not isinstance(value["kernel_start"], str) or not value["kernel_start"].isdigit(): raise ValueError()
        if not value["boot_id"] or value["reader_uid"] != admitted["reader_uid"]: raise ValueError()
        if any(value[key] is not True for key in ("readonly_app", "readonly_config", "fixed_mounts")): raise ValueError()
    except (ValueError, KeyError, TypeError):
        raise Refusal("C934_CONTEXT_CHANGED") from None

def parse_start_stat(raw, pid):
    try:
        text = raw.decode("ascii")
        if not text.startswith(str(pid) + " ("): raise ValueError()
        fields = text[text.rindex(")") + 2:].split()
        value = fields[19]
        if not value.isdigit(): raise ValueError()
        return value
    except (UnicodeError, ValueError, IndexError):
        raise Refusal("C934_CONTEXT_CHANGED") from None

def capture(role, admitted, authority, deadline):
    """Return private bytes to the caller only, never write them to an output stream."""
    check_admission(admitted)
    if role not in ROLES: raise Refusal("ROLE_INVALID")
    check_deadline(authority, deadline)
    before = authority.snapshot(role, deadline)
    validate_identity(role, before, admitted)
    check_deadline(authority, deadline)
    try:
        environ, cmdline, cwd = authority.read_process(role, before, deadline)
        if len(environ) + len(cmdline) > MAX_CAPTURE or cwd != "/app": raise ValueError()
        if not environ.endswith(b"\0") or not cmdline.endswith(b"\0"): raise ValueError()
        environment = {}
        for entry in environ[:-1].split(b"\0"):
            key, value = entry.decode("utf-8").split("=", 1)
            if not key or key in environment: raise ValueError()
            if value and key.upper() in {"DOTNET_STARTUP_HOOKS", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"}: raise ValueError()
            environment[key] = value
        args = [arg.decode("utf-8") for arg in cmdline[:-1].split(b"\0")]
        if len(args) < 2 or args[0] not in {"dotnet", "/usr/share/dotnet/dotnet"} or args[1] not in {ROLES[role]["assembly"], ROLES[role]["assembly"].split("/")[-1]}: raise ValueError()
        payload = json.dumps({"Role": role, "ApplicationAssembly": ROLES[role]["assembly"], "ContentRoot": cwd,
            "Environment": environment, "Arguments": args[2:]}, ensure_ascii=True, separators=(",", ":")).encode()
        if len(payload) > MAX_CAPTURE: raise ValueError()
    except (ValueError, TypeError, UnicodeError, OSError):
        raise Refusal("CAPTURE_INVALID") from None
    check_deadline(authority, deadline)
    after = authority.snapshot(role, deadline)
    validate_identity(role, after, admitted)
    if before != after: raise Refusal("C934_CONTEXT_CHANGED")
    check_deadline(authority, deadline)
    return payload

def protect_memory():
    """No elevation or limit increase. Refuse before private reads if RAM cannot lock."""
    if sys.platform != "linux": raise Refusal("UNKNOWN_PRIVATE_CAPTURE")
    # No swap on the operator host: Docker CLI/daemon buffers are outside the
    # sandbox cgroup and an exec does not inherit mlock locks.
    with open("/proc/swaps", "r", encoding="ascii") as swaps:
        if len(swaps.read(4096).splitlines()) != 1: raise Refusal("UNKNOWN_PRIVATE_CAPTURE")
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    libc = ctypes.CDLL(None, use_errno=True)
    if libc.mlockall(1 | 2) != 0: raise Refusal("UNKNOWN_PRIVATE_CAPTURE")
    if libc.prctl(4, 0, 0, 0, 0) != 0: raise Refusal("UNKNOWN_PRIVATE_CAPTURE")

class LinuxAuthority:
    """Fixed metadata/proc route; no PID/path/command input accepted from callers.

    docker.public(argv, deadline) is a bounded, diagnostic-discarding command
    transport provided by the operator caller. Snapshot opens /proc once and
    retains that process directory FD until close; never reopens a recycled PID.
    """
    clock = staticmethod(time.monotonic)
    def __init__(self, admitted, docker):
        check_admission(admitted)
        if sys.platform != "linux" or os.geteuid() != admitted["reader_uid"]: raise Refusal("UNKNOWN_PRIVATE_CAPTURE")
        self.admitted, self.docker, self.handles = admitted, docker, {}
    def _read(self, fd, name, limit):
        handle = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
        try:
            chunks = bytearray()
            while len(chunks) <= limit:
                part = os.read(handle, min(4096, limit + 1 - len(chunks)))
                if not part: break
                chunks.extend(part)
            if len(chunks) > limit: raise Refusal("CAPTURE_INVALID")
            return bytes(chunks)
        finally: os.close(handle)
    def snapshot(self, role, deadline):
        if role not in ROLES: raise Refusal("ROLE_INVALID")
        check_deadline(self, deadline)
        pin = ROLES[role]
        fmt = '{"container":{{json .Id}},"config":{{json .Image}},"reference":{{json .Config.Image}},"started":{{json .State.StartedAt}},"pid":{{json .State.Pid}},"running":{{json .State.Running}},"readonly":{{json .HostConfig.ReadonlyRootfs}},"mounts":{{json .Mounts}}}'
        image_fmt = '{"config":{{json .Id}},"architecture":{{json .Architecture}},"source":{{json (index .Config.Labels "org.opencontainers.image.revision")}},"digests":{{json .RepoDigests}}}'
        try:
            current = json.loads(self.docker.public(["container", "inspect", "--format", fmt, pin["container"]], deadline), object_pairs_hook=unique_object)
            image = json.loads(self.docker.public(["image", "inspect", "--format", image_fmt, pin["reference"].split("@", 1)[0] + "@" + pin["native"]], deadline), object_pairs_hook=unique_object)
            if current["running"] is not True or current["readonly"] is not True or image["architecture"] != "amd64" or image["config"] != pin["config"]: raise ValueError()
            if not any(d.endswith("@" + pin["native"]) for d in image["digests"]): raise ValueError()
            pid = current["pid"]
            if type(pid) is not int or pid <= 0: raise ValueError()
            if role not in self.handles:
                proc = os.open("/proc/" + str(pid), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
                self.handles[role] = (pid, proc)
            held_pid, fd = self.handles[role]
            if held_pid != pid: raise ValueError()
            start = parse_start_stat(self._read(fd, "stat", 8192), pid)
            # Root is the one intentional proc magic link, retained as an FD. All
            # descendants use O_NOFOLLOW, preventing app/config symlink escapes.
            root = os.open("root", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC, dir_fd=fd)
            try:
                stamp = []
                for parts in [("app",), ("run", "secrets")]:
                    directory = os.dup(root)
                    try:
                        for component in parts:
                            next_fd = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory)
                            os.close(directory); directory = next_fd
                        st = os.fstat(directory); stamp.append((st.st_dev, st.st_ino))
                    finally: os.close(directory)
            finally: os.close(root)
            mounts = current["mounts"]
            fixed = True
            for mount in mounts:
                dest = mount["Destination"]
                if dest == "/" or any(dest == path or dest.startswith(path + "/") or path.startswith(dest + "/") for path in ("/app", "/run/secrets")):
                    if mount["RW"] is not False: fixed = False
            with open("/proc/sys/kernel/random/boot_id", "r", encoding="ascii") as stream: boot = stream.read(80).strip()
            result = dict(pin)
            result.update({key: current[key] for key in ("container", "config", "reference", "started", "pid")})
            result.update(source=image["source"], kernel_start=start, boot_id=boot,
                reader_uid=os.geteuid(), readonly_app=fixed, readonly_config=fixed, fixed_mounts=fixed,
                mount_stamp=stamp, mount_metadata=mounts)
            validate_identity(role, result, self.admitted)
            return result
        except Refusal: raise
        except (OSError, ValueError, KeyError, TypeError, AttributeError):
            raise Refusal("C934_CONTEXT_CHANGED") from None
    def read_process(self, role, identity, deadline):
        check_deadline(self, deadline)
        try:
            pid, fd = self.handles[role]
            if pid != identity["pid"] or parse_start_stat(self._read(fd, "stat", 8192), pid) != identity["kernel_start"]: raise Refusal("C934_CONTEXT_CHANGED")
            env = self._read(fd, "environ", MAX_CAPTURE)
            args = self._read(fd, "cmdline", MAX_CAPTURE)
            cwd = os.readlink("cwd", dir_fd=fd)
            if parse_start_stat(self._read(fd, "stat", 8192), pid) != identity["kernel_start"]: raise Refusal("C934_CONTEXT_CHANGED")
            return env, args, cwd
        except OSError: raise Refusal("UNKNOWN_PRIVATE_CAPTURE") from None
    def close(self):
        for _, fd in self.handles.values(): os.close(fd)
        self.handles.clear()
